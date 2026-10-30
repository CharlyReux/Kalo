using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Bungie.Tags;
using Kalo.Protocol;

namespace Kalo.BlamHost
{
    /// <summary>
    /// Object placements of a scenario, for Kalo's map view: read them all in one call, move one, add, duplicate
    /// or delete. Works on the same open TagFile as the tag editor; nothing is written until the tag is saved.
    /// </summary>
    internal sealed class ScenarioService
    {
        /// <summary>Placement block, its palette block, and the object type name used in object ids.</summary>
        private static readonly string[][] Kinds =
        {
            new[] { "scenery", "scenery palette", "scenery" },
            new[] { "crates", "crate palette", "crate" },
            new[] { "weapons", "weapon palette", "weapon" },
            new[] { "vehicles", "vehicle palette", "vehicle" },
            new[] { "equipment", "equipment palette", "equipment" },
            new[] { "machines", "machine palette", "machine" },
            new[] { "controls", "control palette", "control" },
            new[] { "terminals", "terminal palette", "terminal" },
            new[] { "bipeds", "biped palette", "biped" },
            new[] { "giants", "giant palette", "giant" },
            new[] { "effect scenery", "effect scenery palette", "effect_scenery" },
            new[] { "sound scenery", "sound scenery palette", "sound_scenery" },
        };

        private readonly TagService _tags;
        private readonly Dictionary<string, PaletteEntry> _objectInfo = new Dictionary<string, PaletteEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Random _random = new Random();

        public ScenarioService(TagService tags)
        {
            _tags = tags;
        }

        public ScenarioPlacements Get(string path)
        {
            var sw = Stopwatch.StartNew();
            TagFile tag = _tags.GetOrOpen(path);
            var names = ObjectNames(tag);
            var result = new ScenarioPlacements
            {
                Path = path,
                Bsps = new List<string>(),
                Categories = new List<PlacementCategory>(),
                SpawnPoints = new List<Placement>()
            };
            var bsps = tag.SelectField("Block:structure bsps") as TagFieldBlock;
            if (bsps != null)
                for (int i = 0; i < bsps.Elements.Count; i++)
                    result.Bsps.Add(TagService.ValueOf(tag.SelectField("Block:structure bsps[" + i + "]/Reference:structure bsp")));
            foreach (string[] k in Kinds)
            {
                PlacementCategory c = Category(tag, k, names);
                if (c != null) result.Categories.Add(c);
            }
            var starts = tag.SelectField("Block:player starting locations") as TagFieldBlock;
            if (starts != null)
                for (int i = 0; i < starts.Elements.Count; i++)
                {
                    string b = "Block:player starting locations[" + i + "]/";
                    result.SpawnPoints.Add(new Placement
                    {
                        Index = i,
                        Type = -1,
                        Position = Floats(tag.SelectField(b + "RealPoint3d:position")),
                        Rotation = new[] { Float(tag.SelectField(b + "Angle:facing")), Float(tag.SelectField(b + "Angle:pitch")), 0f },
                        Scale = 1
                    });
                }
            result.Folders = Folders(tag);
            result.Ms = sw.ElapsedMilliseconds;
            return result;
        }

        private static List<EditorFolder> Folders(TagFile tag)
        {
            var list = new List<EditorFolder>();
            var block = tag.SelectField("Block:editor folders") as TagFieldBlock;
            for (int i = 0; block != null && i < block.Elements.Count; i++)
                list.Add(new EditorFolder
                {
                    Index = i,
                    Parent = Int(tag.SelectField("Block:editor folders[" + i + "]/LongBlockIndex:parent folder")),
                    Name = TagService.ValueOf(tag.SelectField("Block:editor folders[" + i + "]/LongString:name")) ?? ""
                });
            return list;
        }

        public Placement Set(SetPlacementParams p)
        {
            TagFile tag = _tags.GetOrOpen(p.Path);
            string[] k = Kind(p.Category);
            string b = "Block:" + k[0] + "[" + p.Index + "]/Struct:object data[0]/";
            var block = tag.SelectField("Block:" + k[0]) as TagFieldBlock;
            if (block == null || p.Index < 0 || p.Index >= block.Elements.Count) throw new ArgumentOutOfRangeException("index", "No placement " + p.Index + " in " + p.Category);
            if (p.Position != null) SetFloats(tag.SelectField(b + "RealPoint3d:position"), p.Position);
            if (p.Rotation != null) SetFloats(tag.SelectField(b + "RealEulerAngles3d:rotation"), p.Rotation);
            if (p.Scale.HasValue)
            {
                var scale = tag.SelectField(b + "Real:scale") as TagFieldElementSingle;
                if (scale != null) scale.Data = Math.Max(0.01f, p.Scale.Value);
            }
            return Read(tag, k, p.Index, ObjectNames(tag));
        }

        public PlacementOpResult Op(PlacementOpParams p)
        {
            TagFile tag = _tags.GetOrOpen(p.Path);
            switch (p.Op)
            {
                case PlacementOps.AddFolder: return AddFolder(tag, p);
                case PlacementOps.RenameFolder: return RenameFolder(tag, p);
                case PlacementOps.DeleteFolder: return DeleteFolder(tag, p);
            }
            string[] k = Kind(p.Category);
            var block = tag.SelectField("Block:" + k[0]) as TagFieldBlock;
            if (block == null) throw new InvalidOperationException("This scenario has no " + p.Category + " block.");
            int count = block.Elements.Count;
            int select = -1;
            switch (p.Op)
            {
                case PlacementOps.Add:
                {
                    if (count >= block.MaximumElementCount) throw new InvalidOperationException("No room for more " + p.Category + ".");
                    var palette = tag.SelectField("Block:" + k[1]) as TagFieldBlock;
                    if (palette == null || p.Type < 0 || p.Type >= palette.Elements.Count) throw new ArgumentOutOfRangeException("type", "No palette entry " + p.Type);
                    block.AddElement();
                    select = count;
                    string e = "Block:" + k[0] + "[" + select + "]/";
                    ((TagFieldBlockIndex)tag.SelectField(e + "ShortBlockIndex:type")).Value = p.Type;
                    TrySet(() => ((TagFieldBlockIndex)tag.SelectField(e + "ShortBlockIndex:name")).Value = -1);
                    if (p.Position != null) SetFloats(tag.SelectField(e + "Struct:object data[0]/RealPoint3d:position"), p.Position);
                    NewObjectId(tag, e, k[2]);
                    break;
                }
                case PlacementOps.Duplicate:
                {
                    if (count >= block.MaximumElementCount) throw new InvalidOperationException("No room for more " + p.Category + ".");
                    if (p.Index < 0 || p.Index >= count) throw new ArgumentOutOfRangeException("index");
                    block.DuplicateElement(p.Index);
                    select = p.Index + 1;
                    string e = "Block:" + k[0] + "[" + select + "]/";
                    TrySet(() => ((TagFieldBlockIndex)tag.SelectField(e + "ShortBlockIndex:name")).Value = -1); // names must stay unique
                    NewObjectId(tag, e, k[2]);
                    if (p.Position != null) SetFloats(tag.SelectField(e + "Struct:object data[0]/RealPoint3d:position"), p.Position);
                    break;
                }
                case PlacementOps.Remove:
                    if (p.Index < 0 || p.Index >= count) throw new ArgumentOutOfRangeException("index");
                    block.RemoveElement(p.Index);
                    select = -1;
                    break;
                case PlacementOps.SetType:
                {
                    if (p.Index < 0 || p.Index >= count) throw new ArgumentOutOfRangeException("index");
                    var palette = tag.SelectField("Block:" + k[1]) as TagFieldBlock;
                    if (palette == null || p.Type < 0 || p.Type >= palette.Elements.Count) throw new ArgumentOutOfRangeException("type", "No palette entry " + p.Type);
                    ((TagFieldBlockIndex)tag.SelectField("Block:" + k[0] + "[" + p.Index + "]/ShortBlockIndex:type")).Value = p.Type;
                    select = p.Index;
                    break;
                }
                case PlacementOps.SetFolder:
                {
                    if (p.Index < 0 || p.Index >= count) throw new ArgumentOutOfRangeException("index");
                    var folder = tag.SelectField("Block:" + k[0] + "[" + p.Index + "]/Struct:object data[0]/ShortBlockIndex:editor folder") as TagFieldBlockIndex;
                    if (folder == null) throw new InvalidOperationException("These objects have no editor folder field.");
                    folder.Value = p.Folder;
                    select = p.Index;
                    break;
                }
                case PlacementOps.AddPalette:
                {
                    var palette = tag.SelectField("Block:" + k[1]) as TagFieldBlock;
                    if (palette == null) throw new InvalidOperationException("No " + k[1] + " block.");
                    string wanted = (p.Tag ?? "").Replace('/', '\\').Trim();
                    for (int i = 0; i < palette.Elements.Count; i++)
                        if (string.Equals(TagService.ValueOf(tag.SelectField("Block:" + k[1] + "[" + i + "]/Reference:name")), wanted, StringComparison.OrdinalIgnoreCase)) { select = i; break; }
                    if (select < 0)
                    {
                        var reference = (TagFieldReference)null;
                        palette.AddElement();
                        select = palette.Elements.Count - 1;
                        reference = (TagFieldReference)tag.SelectField("Block:" + k[1] + "[" + select + "]/Reference:name");
                        TagPath tp = TagService.TagPathOf(wanted);
                        if (!reference.Definition.TagPathIsAllowable(tp))
                        {
                            palette.RemoveElement(select);
                            throw new ArgumentException(wanted + " cannot go in the " + k[1] + ".");
                        }
                        reference.Path = tp;
                    }
                    break;
                }
                default:
                    throw new ArgumentException("Unknown placement operation: " + p.Op);
            }
            return new PlacementOpResult { Category = Category(tag, k, ObjectNames(tag)), SelectIndex = select };
        }

        /// <summary>
        /// A new map from a Reach level: the template scenario saved under a new path (it keeps using the
        /// template's level geometry), with a new map id, and optionally every object removed except respawn points.
        /// </summary>
        public CreateMapResult CreateMap(CreateMapParams p)
        {
            var sw = Stopwatch.StartNew();
            if (_tags.IsOpen(p.Template)) throw new InvalidOperationException("Close the template map first: it is open in a tab.");
            if (System.IO.File.Exists(_tags.FullPath(p.NewPath))) throw new InvalidOperationException("A map already exists at " + p.NewPath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_tags.FullPath(p.NewPath)));
            using (var template = new TagFile(TagService.TagPathOf(p.Template)))
                template.SaveAs(TagService.TagPathOf(p.NewPath));

            TagFile tag = _tags.GetOrOpen(p.NewPath);
            var result = new CreateMapResult { Path = p.NewPath };
            TrySet(() => ((TagFieldElementInteger)tag.SelectField("LongInteger:map id")).Data = p.MapId);
            if (p.Empty)
            {
                foreach (string[] k in Kinds)
                {
                    var block = tag.SelectField("Block:" + k[0]) as TagFieldBlock;
                    if (block == null) continue;
                    var palette = new List<string>();
                    var pal = tag.SelectField("Block:" + k[1]) as TagFieldBlock;
                    if (pal != null)
                        for (int i = 0; i < pal.Elements.Count; i++)
                            palette.Add(TagService.ValueOf(tag.SelectField("Block:" + k[1] + "[" + i + "]/Reference:name")) ?? "");
                    for (int i = block.Elements.Count - 1; i >= 0; i--)
                    {
                        int type = Int(tag.SelectField("Block:" + k[0] + "[" + i + "]/ShortBlockIndex:type"));
                        string objectTag = type >= 0 && type < palette.Count ? palette[type] : "";
                        bool keep = k[0] == "scenery" && objectTag.IndexOf("respawn_point", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (keep) { result.Kept++; continue; }
                        block.RemoveElement(i);
                        result.Removed++;
                    }
                }
            }
            if (p.Barebone) MakeBarebone(tag, result);
            tag.Save();
            result.Ms = sw.ElapsedMilliseconds;
            return result;
        }

        /// <summary>Turns a test level into a multiplayer map: settings MP maps have, a platform, spawn points on it.</summary>
        private void MakeBarebone(TagFile tag, CreateMapResult result)
        {
            TrySet(() => ((TagFieldEnum)tag.SelectField("CharEnum:type")).SetValue("multiplayer"));
            SetReference(tag, "Reference:required resources", @"levels\multi\multiplayer.scenario_required_resource");
            SetReference(tag, "Reference:game performance throttles", @"globals\default.game_performance_throttle");
            SetReference(tag, "Reference:performance throttles", @"globals\defaults\default_mp_small.performance_throttles");
            foreach (string[] k in Kinds)
            {
                var block = tag.SelectField("Block:" + k[0]) as TagFieldBlock;
                if (block != null) block.RemoveAllElements();
            }
            var crates = Kind("crates");
            int platform = PaletteIndex(tag, crates, BareboneMap.Platform);
            Op(tag, crates, platform, new[] { 0f, 0f, 0f });
            var scenery = Kind("scenery");
            int respawn = PaletteIndex(tag, scenery, BareboneMap.RespawnPoint);
            var spots = new[] { new[] { -1.5f, -1.5f, 0.6f }, new[] { 1.5f, 1.5f, 0.6f }, new[] { -1.5f, 1.5f, 0.6f }, new[] { 1.5f, -1.5f, 0.6f } };
            foreach (var spot in spots) Op(tag, scenery, respawn, spot);
            var starts = tag.SelectField("Block:player starting locations") as TagFieldBlock;
            if (starts != null)
            {
                starts.RemoveAllElements();
                starts.AddElement();
                SetFloats(tag.SelectField("Block:player starting locations[0]/RealPoint3d:position"), new[] { 0f, 0f, 0.6f });
            }
            result.Kept = spots.Length;
        }

        private static void SetReference(TagFile tag, string field, string path)
        {
            TrySet(() => ((TagFieldReference)tag.SelectField(field)).Path = TagService.TagPathOf(path));
        }

        private int PaletteIndex(TagFile tag, string[] k, string objectTag)
        {
            var palette = tag.SelectField("Block:" + k[1]) as TagFieldBlock;
            for (int i = 0; palette != null && i < palette.Elements.Count; i++)
                if (string.Equals(TagService.ValueOf(tag.SelectField("Block:" + k[1] + "[" + i + "]/Reference:name")), objectTag, StringComparison.OrdinalIgnoreCase)) return i;
            palette.AddElement();
            int index = palette.Elements.Count - 1;
            ((TagFieldReference)tag.SelectField("Block:" + k[1] + "[" + index + "]/Reference:name")).Path = TagService.TagPathOf(objectTag);
            return index;
        }

        /// <summary>Adds a placement of palette entry type at a position (as the add operation does).</summary>
        private void Op(TagFile tag, string[] k, int type, float[] position)
        {
            var block = (TagFieldBlock)tag.SelectField("Block:" + k[0]);
            block.AddElement();
            string e = "Block:" + k[0] + "[" + (block.Elements.Count - 1) + "]/";
            ((TagFieldBlockIndex)tag.SelectField(e + "ShortBlockIndex:type")).Value = type;
            TrySet(() => ((TagFieldBlockIndex)tag.SelectField(e + "ShortBlockIndex:name")).Value = -1);
            SetFloats(tag.SelectField(e + "Struct:object data[0]/RealPoint3d:position"), position);
            NewObjectId(tag, e, k[2]);
        }

        // ---- editor folders ----------------------------------------------------------------------

        private PlacementOpResult AddFolder(TagFile tag, PlacementOpParams p)
        {
            var block = tag.SelectField("Block:editor folders") as TagFieldBlock;
            if (block == null) throw new InvalidOperationException("This scenario has no editor folders block.");
            block.AddElement();
            int i = block.Elements.Count - 1;
            ((TagFieldBlockIndex)tag.SelectField("Block:editor folders[" + i + "]/LongBlockIndex:parent folder")).Value = p.Folder;
            SetText(tag.SelectField("Block:editor folders[" + i + "]/LongString:name"), string.IsNullOrWhiteSpace(p.Tag) ? "folder" : p.Tag.Trim());
            return new PlacementOpResult { SelectIndex = i, Folders = Folders(tag) };
        }

        private PlacementOpResult RenameFolder(TagFile tag, PlacementOpParams p)
        {
            SetText(tag.SelectField("Block:editor folders[" + p.Folder + "]/LongString:name"), (p.Tag ?? "").Trim());
            return new PlacementOpResult { SelectIndex = p.Folder, Folders = Folders(tag) };
        }

        /// <summary>Removes a folder; what was in it moves to its parent, and every later folder index shifts down by one.</summary>
        private PlacementOpResult DeleteFolder(TagFile tag, PlacementOpParams p)
        {
            var block = tag.SelectField("Block:editor folders") as TagFieldBlock;
            int f = p.Folder;
            if (block == null || f < 0 || f >= block.Elements.Count) throw new ArgumentOutOfRangeException("folder");
            int parent = Int(tag.SelectField("Block:editor folders[" + f + "]/LongBlockIndex:parent folder"));
            Func<int, int> remap = v => v == f ? (parent > f ? parent - 1 : parent) : v > f ? v - 1 : v;
            foreach (string[] k in Kinds)
            {
                var objects = tag.SelectField("Block:" + k[0]) as TagFieldBlock;
                for (int i = 0; objects != null && i < objects.Elements.Count; i++)
                {
                    var field = tag.SelectField("Block:" + k[0] + "[" + i + "]/Struct:object data[0]/ShortBlockIndex:editor folder") as TagFieldBlockIndex;
                    if (field != null && field.Value >= 0) field.Value = remap(field.Value);
                }
            }
            for (int i = 0; i < block.Elements.Count; i++)
            {
                if (i == f) continue;
                var field = tag.SelectField("Block:editor folders[" + i + "]/LongBlockIndex:parent folder") as TagFieldBlockIndex;
                if (field != null && field.Value >= 0) field.Value = remap(field.Value);
            }
            block.RemoveElement(f);
            var names = ObjectNames(tag);
            var categories = new List<PlacementCategory>();
            foreach (string[] k in Kinds)
            {
                var c = Category(tag, k, names);
                if (c != null) categories.Add(c);
            }
            return new PlacementOpResult { SelectIndex = -1, Folders = Folders(tag), Categories = categories };
        }

        private static void SetText(TagField f, string text)
        {
            var e = f as TagFieldElement;
            if (e == null || !e.SetStringData(text)) throw new ArgumentException("Could not set " + (f == null ? "field" : f.FieldName));
        }

        // ---- compared with the map it came from ------------------------------------------------------

        /// <summary>
        /// Each placement as same / changed / new compared with a baseline scenario (vanilla, or the level a new
        /// map was copied from), matching objects by their unique id. Everything except the index is compared.
        /// </summary>
        public PlacementStatusResult Status(PlacementStatusParams p)
        {
            var sw = Stopwatch.StartNew();
            TagFile tag = _tags.GetOrOpen(p.Path);
            var result = new PlacementStatusResult { States = new Dictionary<string, List<int>>() };
            string tempRel = "_kalo_vanilla\\status\\" + Path.GetFileName(p.Path);
            string temp = _tags.FullPath(tempRel);
            bool haveBaseline = !string.IsNullOrEmpty(p.BaselineFile) && File.Exists(p.BaselineFile);
            TagFile baseline = null;
            try
            {
                if (haveBaseline)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(temp));
                    File.Copy(p.BaselineFile, temp, true);
                    baseline = new TagFile(TagService.TagPathOf(tempRel));
                }
                foreach (string[] k in Kinds)
                {
                    var block = tag.SelectField("Block:" + k[0]) as TagFieldBlock;
                    if (block == null) continue;
                    var before = baseline == null ? new Dictionary<int, string>() : Signatures(baseline, k);
                    var after = Signatures(tag, k, keepOrder: true);
                    var states = new List<int>();
                    var seen = new HashSet<int>();
                    for (int i = 0; i < block.Elements.Count; i++)
                    {
                        int id = UniqueId(tag, k[0], i);
                        string sig;
                        string now = after.TryGetValue(-1 - i, out sig) ? sig : "";
                        string old;
                        if (!before.TryGetValue(id, out old)) states.Add(PlacementStates.New);
                        else { seen.Add(id); states.Add(old == now ? PlacementStates.Same : PlacementStates.Changed); }
                    }
                    result.Removed += before.Keys.Count(id => !seen.Contains(id));
                    result.States[k[0]] = states;
                }
            }
            finally
            {
                try { if (baseline != null) baseline.Dispose(); } catch (Exception) { }
                try { if (haveBaseline) File.Delete(temp); } catch (Exception) { }
            }
            result.Ms = sw.ElapsedMilliseconds;
            return result;
        }

        private static int UniqueId(TagFile tag, string block, int i) =>
            Int(tag.SelectField("Block:" + block + "[" + i + "]/Struct:object data[0]/Struct:object id[0]/LongInteger:unique id"));

        /// <summary>
        /// Placement signatures: palette tag + every value of the element (paths without the index).
        /// keyed by unique id, or by -1-index when keepOrder (for the current map, where ids may repeat).
        /// </summary>
        private static Dictionary<int, string> Signatures(TagFile tag, string[] k, bool keepOrder = false)
        {
            var result = new Dictionary<int, string>();
            var block = tag.SelectField("Block:" + k[0]) as TagFieldBlock;
            if (block == null) return result;
            var palette = new List<string>();
            var pal = tag.SelectField("Block:" + k[1]) as TagFieldBlock;
            for (int i = 0; pal != null && i < pal.Elements.Count; i++)
                palette.Add(TagService.ValueOf(tag.SelectField("Block:" + k[1] + "[" + i + "]/Reference:name")) ?? "");
            for (int i = 0; i < block.Elements.Count; i++)
            {
                var sb = new System.Text.StringBuilder();
                int type = Int(tag.SelectField("Block:" + k[0] + "[" + i + "]/ShortBlockIndex:type"));
                sb.Append(type >= 0 && type < palette.Count ? palette[type] : "?").Append('|');
                AppendValues(block.Elements[i].Fields, sb, 0);
                int key = keepOrder ? -1 - i : UniqueId(tag, k[0], i);
                result[key] = sb.ToString();
            }
            return result;
        }

        private static void AppendValues(TagField[] fields, System.Text.StringBuilder sb, int depth)
        {
            if (fields == null || depth > 6) return;
            foreach (TagField f in fields)
            {
                if (f == null || f.FieldName == "type") continue; // the palette tag is compared instead of its index
                var s = f as TagFieldStruct;
                if (s != null) { foreach (TagElement e in s.Elements) AppendValues(e.Fields, sb, depth + 1); continue; }
                var b = f as TagFieldBlock;
                if (b != null) { sb.Append('[').Append(b.Elements.Count).Append(']'); foreach (TagElement e in b.Elements) AppendValues(e.Fields, sb, depth + 1); continue; }
                string v = Safe(() => TagService.ValueOf(f));
                if (v != null) sb.Append(v).Append(';');
            }
        }

        // ---- reading ---------------------------------------------------------------------------

        private PlacementCategory Category(TagFile tag, string[] k, List<string> names)
        {
            var block = tag.SelectField("Block:" + k[0]) as TagFieldBlock;
            if (block == null) return null;
            var c = new PlacementCategory { Name = k[0], Palette = new List<PaletteEntry>(), Items = new List<Placement>() };
            var palette = tag.SelectField("Block:" + k[1]) as TagFieldBlock;
            if (palette != null)
                for (int i = 0; i < palette.Elements.Count; i++)
                    c.Palette.Add(Info(TagService.ValueOf(tag.SelectField("Block:" + k[1] + "[" + i + "]/Reference:name"))));
            for (int i = 0; i < block.Elements.Count; i++) c.Items.Add(Read(tag, k, i, names));
            return c;
        }

        private static Placement Read(TagFile tag, string[] k, int i, List<string> names)
        {
            string e = "Block:" + k[0] + "[" + i + "]/";
            string o = e + "Struct:object data[0]/";
            int name = Int(tag.SelectField(e + "ShortBlockIndex:name"));
            return new Placement
            {
                Index = i,
                Type = Int(tag.SelectField(e + "ShortBlockIndex:type")),
                Name = name >= 0 && name < names.Count ? names[name] : null,
                Position = Floats(tag.SelectField(o + "RealPoint3d:position")),
                Rotation = Floats(tag.SelectField(o + "RealEulerAngles3d:rotation")),
                Scale = Float(tag.SelectField(o + "Real:scale")),
                Label = Safe(() => TagService.ValueOf(tag.SelectField(e + "Struct:multiplayer data[0]/String:megalo label"))),
                UniqueId = Int(tag.SelectField(o + "Struct:object id[0]/LongInteger:unique id")),
                Folder = Int(tag.SelectField(o + "ShortBlockIndex:editor folder"))
            };
        }

        private static List<string> ObjectNames(TagFile tag)
        {
            var list = new List<string>();
            var block = tag.SelectField("Block:object names") as TagFieldBlock;
            if (block == null) return list;
            for (int i = 0; i < block.Elements.Count; i++)
            {
                TagField first = block.Elements[i].Fields.FirstOrDefault();
                list.Add(first == null ? null : TagService.ValueOf(first));
            }
            return list;
        }

        public ObjectInfoResult ObjectInfo(TagListParams p) =>
            new ObjectInfoResult { Objects = (p.Tags ?? new List<string>()).Select(Info).ToList() };

        /// <summary>See MakeVariantParams. Existing copies are replaced.</summary>
        public MakeVariantResult MakeVariant(MakeVariantParams p)
        {
            string ext = Path.GetExtension(p.Object);
            if (!string.Equals(ext, Path.GetExtension(p.NewObject), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The copy must be a " + ext);
            string dir = Path.GetDirectoryName(p.NewObject);
            string name = Path.GetFileNameWithoutExtension(p.NewObject);
            string modelPath, renderPath;
            using (var obj = new TagFile(TagService.TagPathOf(p.Object)))
            {
                var model = Find(obj.Fields, "model", 3) as TagFieldReference;
                if (model == null || model.Path == null) throw new InvalidOperationException(p.Object + " has no model.");
                modelPath = model.Path.RelativePathWithExtension;
            }
            using (var m = new TagFile(TagService.TagPathOf(modelPath)))
            {
                var render = Find(m.Fields, "render model", 2) as TagFieldReference;
                if (render == null || render.Path == null) throw new InvalidOperationException(modelPath + " has no render model.");
                renderPath = render.Path.RelativePathWithExtension;
            }
            Directory.CreateDirectory(_tags.FullPath(dir));
            string newRender = Path.Combine(dir, name + ".render_model");
            string newModel = Path.Combine(dir, name + ".model");
            int count = 0;
            _tags.CloseIfOpen(newRender); _tags.CloseIfOpen(newModel); _tags.CloseIfOpen(p.NewObject);
            using (var r = new TagFile(TagService.TagPathOf(renderPath)))
            {
                var materials = r.SelectField("Block:materials") as TagFieldBlock;
                for (int i = 0; materials != null && i < materials.Elements.Count; i++)
                {
                    var rm = r.SelectField("Block:materials[" + i + "]/Reference:render method") as TagFieldReference;
                    if (rm == null) continue;
                    rm.Path = TagService.TagPathOf(p.Shader);
                    count++;
                }
                r.SaveAs(TagService.TagPathOf(newRender));
            }
            using (var m = new TagFile(TagService.TagPathOf(modelPath)))
            {
                ((TagFieldReference)Find(m.Fields, "render model", 2)).Path = TagService.TagPathOf(newRender);
                m.SaveAs(TagService.TagPathOf(newModel));
            }
            using (var o = new TagFile(TagService.TagPathOf(p.Object)))
            {
                ((TagFieldReference)Find(o.Fields, "model", 3)).Path = TagService.TagPathOf(newModel);
                o.SaveAs(TagService.TagPathOf(p.NewObject));
            }
            _objectInfo.Remove(p.NewObject);
            return new MakeVariantResult { Object = p.NewObject, RenderModel = newRender, Materials = count };
        }

        /// <summary>Render model and bounds of an object tag (cached; object tags rarely change while mapping).</summary>
        private PaletteEntry Info(string objectTag)
        {
            if (string.IsNullOrEmpty(objectTag)) return new PaletteEntry { Tag = objectTag, BoundingRadius = 0.5f };
            PaletteEntry info;
            if (_objectInfo.TryGetValue(objectTag, out info)) return info;
            info = new PaletteEntry { Tag = objectTag, BoundingRadius = 0.5f };
            try
            {
                using (var obj = new TagFile(TagService.TagPathOf(objectTag)))
                {
                    TagField radius = Find(obj.Fields, "bounding radius", 3);
                    if (radius != null) info.BoundingRadius = Math.Max(0.05f, Float(radius));
                    TagField offset = Find(obj.Fields, "bounding offset", 3);
                    if (offset != null) info.BoundingOffset = Floats(offset);
                    var model = Find(obj.Fields, "model", 3) as TagFieldReference;
                    if (model != null && model.Path != null)
                        using (var m = new TagFile(model.Path))
                        {
                            var render = Find(m.Fields, "render model", 2) as TagFieldReference;
                            if (render != null && render.Path != null) info.RenderModel = render.Path.RelativePathWithExtension;
                        }
                }
            }
            catch (Exception ex)
            {
                Server.Log("Could not read " + objectTag + ": " + ex.Message);
            }
            _objectInfo[objectTag] = info;
            return info;
        }

        /// <summary>First field with this name at the top level or inside structs (not blocks).</summary>
        private static TagField Find(TagField[] fields, string name, int depth)
        {
            if (fields == null || depth < 0) return null;
            foreach (TagField f in fields)
                if (f != null && string.Equals(f.FieldName, name, StringComparison.OrdinalIgnoreCase) && !(f is TagFieldStruct)) return f;
            foreach (TagField f in fields)
            {
                var s = f as TagFieldStruct;
                if (s == null || s.Elements.Count == 0) continue;
                TagField found = Find(s.Elements[0].Fields, name, depth - 1);
                if (found != null) return found;
            }
            return null;
        }

        // ---- writing ---------------------------------------------------------------------------

        private void NewObjectId(TagFile tag, string element, string typeName)
        {
            string id = element + "Struct:object data[0]/Struct:object id[0]/";
            TrySet(() => ((TagFieldElementInteger)tag.SelectField(id + "LongInteger:unique id")).Data = _random.Next(1, int.MaxValue));
            TrySet(() => ((TagFieldEnum)tag.SelectField(id + "CharEnum:type")).SetValue(typeName));
            TrySet(() => ((TagFieldEnum)tag.SelectField(id + "CharEnum:source")).SetValue("editor"));
        }

        private static string[] Kind(string category)
        {
            string[] k = Kinds.FirstOrDefault(x => string.Equals(x[0], category, StringComparison.OrdinalIgnoreCase));
            if (k == null) throw new ArgumentException("Unknown placement category: " + category);
            return k;
        }

        private static void SetFloats(TagField f, float[] values)
        {
            var a = f as TagFieldElementArray;
            if (a == null) throw new InvalidOperationException("Not a vector field: " + (f == null ? "missing" : f.FieldPath));
            string[] parts = values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)).ToArray();
            if (!a.SetStringData(parts)) throw new ArgumentException("Value rejected for " + f.FieldName);
        }

        private static float[] Floats(TagField f)
        {
            var single = f as TagFieldElementArraySingle;
            if (single != null) return single.Data.ToArray();
            string v = TagService.ValueOf(f);
            if (string.IsNullOrEmpty(v)) return new float[3];
            return v.Split(',').Select(x => float.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
        }

        private static float Float(TagField f)
        {
            var s = f as TagFieldElementSingle;
            if (s != null) return s.Data;
            float v;
            return float.TryParse(TagService.ValueOf(f), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0f;
        }

        private static int Int(TagField f)
        {
            var bi = f as TagFieldBlockIndex;
            if (bi != null) return bi.Value;
            var i = f as TagFieldElementInteger;
            return i != null ? (int)i.Data : -1;
        }

        private static void TrySet(Action set)
        {
            try { set(); } catch (Exception ex) { Server.Log("placement field not set: " + ex.Message); }
        }

        private static string Safe(Func<string> get)
        {
            try { return get(); } catch (Exception) { return null; }
        }
    }
}
