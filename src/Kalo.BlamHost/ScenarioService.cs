using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
            result.Ms = sw.ElapsedMilliseconds;
            return result;
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
            return Read(tag, k, p.Index, ObjectNames(tag));
        }

        public PlacementOpResult Op(PlacementOpParams p)
        {
            TagFile tag = _tags.GetOrOpen(p.Path);
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
                Label = Safe(() => TagService.ValueOf(tag.SelectField(e + "Struct:multiplayer data[0]/String:megalo label")))
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
