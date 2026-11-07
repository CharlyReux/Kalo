using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Bungie.Tags;
using Kalo.Protocol;

namespace Kalo.BlamHost
{
    /// <summary>
    /// Campaign missions in the map view: trigger volumes, squad spawn points, cutscene flags and cameras and script
    /// points are shown and moved like objects (pseudo categories, see CampaignCategories); zone sets, mission scripts and
    /// cutscenes are listed. Mission scripts (.scenario_hs_source_file) can be read and written as text.
    /// </summary>
    internal sealed partial class ScenarioService
    {
        private static readonly string[] ScenarioTypes = { "solo", "multiplayer", "main menu", "multiplayer shared", "single player shared", "sounds shared" };

        /// <summary>solo, multiplayer, main menu... ("" if unknown).</summary>
        public static string ScenarioType(TagFile tag)
        {
            var e = tag.SelectField("CharEnum:type") as TagFieldEnum;
            if (e == null) return "";
            int v = SafeValue(() => Convert.ToInt32(e.Value), -1);
            return v >= 0 && v < ScenarioTypes.Length ? ScenarioTypes[v] : "";
        }

        private readonly Dictionary<string, KeyValuePair<DateTime, float[]>> _bspBounds = new Dictionary<string, KeyValuePair<DateTime, float[]>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>World bounds of each structure bsp of a scenario ("world bounds x/y/z"), cached per bsp file version.</summary>
        public BspBoundsResult BspBounds(string path)
        {
            var sw = Stopwatch.StartNew();
            var result = new BspBoundsResult { Bounds = BoundsOf(_tags.GetOrOpen(path)) };
            result.Ms = sw.ElapsedMilliseconds;
            return result;
        }

        private List<float[]> BoundsOf(TagFile tag)
        {
            var list = new List<float[]>();
            var bsps = tag.SelectField("Block:structure bsps") as TagFieldBlock;
            for (int i = 0; bsps != null && i < bsps.Elements.Count; i++)
            {
                string bsp = TagService.ValueOf(tag.SelectField("Block:structure bsps[" + i + "]/Reference:structure bsp"));
                float[] b = null;
                try
                {
                    string file = string.IsNullOrEmpty(bsp) ? null : _tags.FullPath(bsp);
                    if (file != null && File.Exists(file))
                    {
                        DateTime stamp = File.GetLastWriteTimeUtc(file);
                        KeyValuePair<DateTime, float[]> cached;
                        if (_bspBounds.TryGetValue(bsp, out cached) && cached.Key == stamp) b = cached.Value;
                        else
                        {
                            using (var t = new TagFile(TagService.TagPathOf(bsp)))
                            {
                                TagField x = Find(t.Fields, "world bounds x", 1), y = Find(t.Fields, "world bounds y", 1), z = Find(t.Fields, "world bounds z", 1);
                                if (x != null && y != null && z != null)
                                {
                                    var fx = Pad(Floats(x), 2); var fy = Pad(Floats(y), 2); var fz = Pad(Floats(z), 2);
                                    b = new[] { fx[0], fx[1], fy[0], fy[1], fz[0], fz[1] };
                                }
                                else Server.Log(bsp + ": no world bounds fields");
                            }
                            _bspBounds[bsp] = new KeyValuePair<DateTime, float[]>(stamp, b);
                        }
                    }
                }
                catch (Exception ex) { Server.Log(bsp + ": bounds unavailable: " + ex.Message); }
                list.Add(b);
            }
            return list;
        }

        /// <summary>
        /// The structure bsp of the nearest other script point within 40 units (the mission's own points know their bsp; bsp
        /// bounds overlap too much to tell), or -1.
        /// </summary>
        private static int NearestPointBsp(TagFile tag, string self, float[] pos)
        {
            int best = -1;
            double bestD = 40 * 40;
            // The AI zones' firing positions first: thousands of them on the walkable ground, each knowing its bsp.
            var zones = tag.SelectField("Block:zones") as TagFieldBlock;
            for (int z = 0; zones != null && z < zones.Elements.Count; z++)
            {
                var fps = tag.SelectField("Block:zones[" + z + "]/Block:firing positions") as TagFieldBlock;
                for (int k = 0; fps != null && k < fps.Elements.Count; k++)
                {
                    string e = "Block:zones[" + z + "]/Block:firing positions[" + k + "]/";
                    var posField = tag.SelectField(e + "RealPoint3d:position (local)");
                    if (posField == null) break;
                    var q = Pad(Floats(posField), 3);
                    double d = (q[0] - pos[0]) * (q[0] - pos[0]) + (q[1] - pos[1]) * (q[1] - pos[1]) + (q[2] - pos[2]) * (q[2] - pos[2]);
                    if (d >= bestD) continue;
                    int bsp = -1;
                    foreach (string type in new[] { "ShortBlockIndex", "ShortBlockIndexCustomSearch", "ShortInteger" })
                        if (tag.SelectField(e + type + ":structure bsp") is TagField f) { bsp = Int(f); break; }
                    if (bsp >= 0) { bestD = d; best = bsp; }
                }
            }
            if (best >= 0 && bestD < 15 * 15) return best;
            foreach (var (s, j) in ScriptPointList(tag))
            {
                string e = "Block:scripting data[0]/Block:point sets[" + s + "]/Block:points[" + j + "]/";
                if (e == self) continue;
                int bsp = -1;
                foreach (string type in new[] { "ShortBlockIndex", "ShortBlockIndexCustomSearch", "ShortInteger" })
                    if (tag.SelectField(e + type + ":structure bsp") is TagField f) { bsp = Int(f); break; }
                if (bsp < 0) continue;
                var q = Pad(Floats(tag.SelectField(e + "RealPoint3d:position")), 3);
                double d = (q[0] - pos[0]) * (q[0] - pos[0]) + (q[1] - pos[1]) * (q[1] - pos[1]) + (q[2] - pos[2]) * (q[2] - pos[2]);
                if (d < bestD) { bestD = d; best = bsp; }
            }
            // Squad spawn points know theirs too.
            var squads = tag.SelectField("Block:squads") as TagFieldBlock;
            for (int s = 0; squads != null && s < squads.Elements.Count; s++)
            {
                var pts = tag.SelectField("Block:squads[" + s + "]/Block:spawn points") as TagFieldBlock;
                for (int j = 0; pts != null && j < pts.Elements.Count; j++)
                {
                    string e = "Block:squads[" + s + "]/Block:spawn points[" + j + "]/";
                    if (e == self) continue;
                    int bsp = -1;
                    foreach (string type in new[] { "ShortBlockIndex", "ShortBlockIndexCustomSearch", "ShortInteger" })
                        if (tag.SelectField(e + type + ":structure bsp") is TagField f) { bsp = Int(f); break; }
                    if (bsp < 0) continue;
                    var q = Pad(Floats(tag.SelectField(e + "RealPoint3d:position")), 3);
                    double d = (q[0] - pos[0]) * (q[0] - pos[0]) + (q[1] - pos[1]) * (q[1] - pos[1]) + (q[2] - pos[2]) * (q[2] - pos[2]);
                    if (d < bestD) { bestD = d; best = bsp; }
                }
            }
            return best;
        }

        /// <summary>Sets an integer or block index field of an element by its name (its type name varies between fields).</summary>
        private static void SetIndexByName(TagFile tag, string element, string name, int value)
        {
            foreach (string type in new[] { "ShortBlockIndex", "ShortBlockIndexCustomSearch", "CharBlockIndex", "LongBlockIndex", "ShortInteger", "CharInteger", "LongInteger" })
            {
                var f = tag.SelectField(element + type + ":" + name);
                if (f is TagFieldBlockIndex b) { b.Value = value; return; }
                if (f is TagFieldElementInteger i) { i.Data = value; return; }
            }
            Server.Log("no field " + name + " in " + element);
        }

        /// <summary>The structure bsp a point is in: the smallest whose world bounds hold it (shared bsps are big), or -1.</summary>
        private int BspAt(TagFile tag, float[] pos)
        {
            var all = BoundsOf(tag);
            int best = -1;
            double bestVolume = double.MaxValue;
            for (int i = 0; i < all.Count; i++)
            {
                var b = all[i];
                if (b == null || pos[0] < b[0] || pos[0] > b[1] || pos[1] < b[2] || pos[1] > b[3] || pos[2] < b[4] || pos[2] > b[5]) continue;
                double v = (double)(b[1] - b[0]) * (b[3] - b[2]) * (b[5] - b[4]);
                if (v < bestVolume) { bestVolume = v; best = i; }
            }
            return best;
        }

        /// <summary>The type of a scenario (open in the host or read from disk).</summary>
        public string TypeOf(string path)
        {
            if (_tags.IsOpen(path)) return ScenarioType(_tags.GetOrOpen(path));
            using (var t = new TagFile(TagService.TagPathOf(path))) return ScenarioType(t);
        }

        private void AddCampaign(TagFile tag, ScenarioPlacements result)
        {
            foreach (string c in CampaignCategories.All)
            {
                var cat = CampaignCategory(tag, c);
                if (cat != null) result.Categories.Add(cat);
            }
            result.ZoneSets = ZoneSets(tag, result.Bsps.Count);
            result.Scripts = MissionScripts(tag);
            AddSquads(tag, result);
            result.Cinematics = new List<string>();
            var cin = tag.SelectField("Block:cinematics") as TagFieldBlock;
            for (int i = 0; cin != null && i < cin.Elements.Count; i++)
            {
                string c = TagService.ValueOf(tag.SelectField("Block:cinematics[" + i + "]/Reference:name"));
                if (!string.IsNullOrEmpty(c)) result.Cinematics.Add(c);
            }
        }

        // ---- reading -------------------------------------------------------------------------------

        private PlacementCategory CampaignCategory(TagFile tag, string category)
        {
            var items = new List<Placement>();
            string marker;
            switch (category)
            {
                case CampaignCategories.TriggerVolumes:
                {
                    var b = tag.SelectField("Block:trigger volumes") as TagFieldBlock;
                    if (b == null) return null;
                    for (int i = 0; i < b.Elements.Count; i++) items.Add(ReadVolume(tag, i));
                    marker = null;
                    break;
                }
                case CampaignCategories.AiSpawnPoints:
                {
                    var squads = tag.SelectField("Block:squads") as TagFieldBlock;
                    if (squads == null) return null;
                    foreach (var (s, j) in AiPoints(tag)) items.Add(ReadAiPoint(tag, items.Count, s, j));
                    marker = @"objects\multi\spawning\respawn_point.scenery";
                    break;
                }
                case CampaignCategories.CutsceneFlags:
                {
                    var b = tag.SelectField("Block:cutscene flags") as TagFieldBlock;
                    if (b == null) return null;
                    for (int i = 0; i < b.Elements.Count; i++) items.Add(ReadFlag(tag, i));
                    marker = @"objects\multi\spawning\respawn_point.scenery";
                    break;
                }
                case CampaignCategories.CutsceneCameras:
                {
                    var b = tag.SelectField("Block:cutscene camera points") as TagFieldBlock;
                    if (b == null) return null;
                    for (int i = 0; i < b.Elements.Count; i++) items.Add(ReadCamera(tag, i));
                    marker = null;
                    break;
                }
                case CampaignCategories.ScriptPoints:
                {
                    if (tag.SelectField("Block:scripting data") == null) return null;
                    foreach (var (s, j) in ScriptPointList(tag)) items.Add(ReadScriptPoint(tag, items.Count, s, j));
                    marker = @"objects\multi\spawning\respawn_point.scenery";
                    break;
                }
                default:
                    return null;
            }
            var entry = marker == null ? new PaletteEntry { Tag = PaletteName(category), BoundingRadius = 0.6f } : Info(marker);
            return new PlacementCategory
            {
                Name = category,
                Palette = new List<PaletteEntry> { new PaletteEntry { Tag = PaletteName(category), RenderModel = entry.RenderModel, BoundingRadius = Math.Max(0.4f, entry.BoundingRadius) } },
                Items = items,
            };
        }

        /// <summary>The name shown for these markers (in the object list and "Add").</summary>
        private static string PaletteName(string category)
        {
            switch (category)
            {
                case CampaignCategories.TriggerVolumes: return "trigger volume";
                case CampaignCategories.AiSpawnPoints: return "ai spawn point";
                case CampaignCategories.CutsceneFlags: return "cutscene flag";
                case CampaignCategories.CutsceneCameras: return "cutscene camera";
                default: return "script point";
            }
        }

        private static string Text(TagFile tag, string path) => Safe(() => TagService.ValueOf(tag.SelectField(path))) ?? "";

        // Trigger volumes: a box (corner position, forward and up vectors, extents along forward / left / up) or a
        // sector (floor polygon with runtime planes and bounds). Shown with their middle as the position.
        private static Placement ReadVolume(TagFile tag, int i)
        {
            string e = "Block:trigger volumes[" + i + "]/";
            var p = new Placement
            {
                Index = i,
                Type = 0,
                Name = Text(tag, e + "StringId:name"),
                Scale = 1,
                UniqueId = -1,
                Folder = Int(tag.SelectField(e + "ShortBlockIndex:editor folder")),
                Shape = 3,
                Block = "trigger volumes",
                BlockIndex = i,
            };
            bool sector = SafeValue(() => Convert.ToInt32(((TagFieldEnum)tag.SelectField(e + "ShortEnum:type")).Value), 0) == 1;
            p.IsSector = sector;
            if (sector)
            {
                var (min, max) = SectorBounds(tag, e);
                p.Position = new[] { (min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2 };
                p.Rotation = new[] { 0f, 0f, 0f };
                float h = (max[2] - min[2]) / 2;
                p.ShapeSize = new[] { max[0] - min[0], max[1] - min[1], h, h };
                p.Label = "sector";
                return p;
            }
            var box = Box(tag, e);
            p.Position = V(box.Center);
            p.Rotation = new[] { box.Yaw, box.Pitch, 0f };
            p.ShapeSize = new[] { box.Extents[0], box.Extents[1], box.Extents[2] / 2, box.Extents[2] / 2 };
            p.Label = "box";
            return p;
        }

        private struct VolumeBox
        {
            public float[] Corner, Forward, Up, Left, Extents, Center;
            public float Yaw, Pitch;
        }

        private static VolumeBox Box(TagFile tag, string e)
        {
            var b = new VolumeBox
            {
                Corner = Pad(Floats(tag.SelectField(e + "RealPoint3d:position"))),
                Forward = Normalize(Pad(Floats(tag.SelectField(e + "RealVector3d:forward"))), new[] { 1f, 0f, 0f }),
                Up = Normalize(Pad(Floats(tag.SelectField(e + "RealVector3d:up"))), new[] { 0f, 0f, 1f }),
                Extents = Pad(Floats(tag.SelectField(e + "RealPoint3d:extents"))),
            };
            b.Left = Cross(b.Up, b.Forward);
            b.Center = Add(b.Corner, Add(Mul(b.Forward, b.Extents[0] / 2), Add(Mul(b.Left, b.Extents[1] / 2), Mul(b.Up, b.Extents[2] / 2))));
            b.Yaw = (float)(Math.Atan2(b.Forward[1], b.Forward[0]) * 180 / Math.PI);
            b.Pitch = (float)(Math.Asin(Math.Max(-1, Math.Min(1, b.Forward[2]))) * 180 / Math.PI);
            return b;
        }

        /// <summary>A sector's bounds: the runtime bounds when the game has computed them, else its points.</summary>
        private static (float[] Min, float[] Max) SectorBounds(TagFile tag, string e)
        {
            float x0 = Float(tag.SelectField(e + "Real:runtime sector bounds x0")), x1 = Float(tag.SelectField(e + "Real:runtime sector bounds x1"));
            float y0 = Float(tag.SelectField(e + "Real:runtime sector bounds y0")), y1 = Float(tag.SelectField(e + "Real:runtime sector bounds y1"));
            float z0 = Float(tag.SelectField(e + "Real:runtime sector bounds z0")), z1 = Float(tag.SelectField(e + "Real:runtime sector bounds z1"));
            if (x1 > x0 && y1 > y0) return (new[] { x0, y0, z0 }, new[] { x1, y1, Math.Max(z1, z0 + 0.1f) });
            var pts = tag.SelectField(e + "Block:sector points") as TagFieldBlock;
            var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (int j = 0; pts != null && j < pts.Elements.Count; j++)
            {
                var v = Pad(Floats(tag.SelectField(e + "Block:sector points[" + j + "]/RealPoint3d:position")));
                for (int a = 0; a < 3; a++) { min[a] = Math.Min(min[a], v[a]); max[a] = Math.Max(max[a], v[a]); }
            }
            if (min[0] > max[0]) return (new float[3], new[] { 1f, 1f, 1f });
            float sink = Float(tag.SelectField(e + "Real:z sink"));
            min[2] -= Math.Max(0, sink);
            max[2] = Math.Max(max[2], min[2] + 1f);
            return (min, max);
        }

        /// <summary>Squad spawn points, as (squad, point) pairs in order.</summary>
        private static List<(int Squad, int Point)> AiPoints(TagFile tag)
        {
            var list = new List<(int, int)>();
            var squads = tag.SelectField("Block:squads") as TagFieldBlock;
            for (int s = 0; squads != null && s < squads.Elements.Count; s++)
            {
                var pts = tag.SelectField("Block:squads[" + s + "]/Block:spawn points") as TagFieldBlock;
                for (int j = 0; pts != null && j < pts.Elements.Count; j++) list.Add((s, j));
            }
            return list;
        }

        private static Placement ReadAiPoint(TagFile tag, int index, int s, int j)
        {
            string sq = "Block:squads[" + s + "]/";
            string e = sq + "Block:spawn points[" + j + "]/";
            var facing = Pad(Floats(tag.SelectField(e + "RealEulerAngles2d:facing (yaw, pitch)")), 2);
            return new Placement
            {
                Index = index,
                Type = 0,
                Name = Text(tag, e + "StringId:name"),
                Position = Pad(Floats(tag.SelectField(e + "RealPoint3d:position"))),
                Rotation = new[] { facing[0], facing[1], Float(tag.SelectField(e + "Real:roll")) },
                Scale = 1,
                UniqueId = -1,
                Folder = Int(tag.SelectField(sq + "ShortBlockIndex:editor folder")),
                Label = "squad " + Text(tag, sq + "String:name"),
                Block = "squads[" + s + "]/Block:spawn points",
                BlockIndex = j,
            };
        }

        private static Placement ReadFlag(TagFile tag, int i)
        {
            string e = "Block:cutscene flags[" + i + "]/";
            var facing = Pad(Floats(tag.SelectField(e + "RealEulerAngles2d:facing")), 2);
            return new Placement
            {
                Index = i,
                Type = 0,
                Name = Text(tag, e + "StringId:name"),
                Position = Pad(Floats(tag.SelectField(e + "RealPoint3d:position"))),
                Rotation = new[] { facing[0], facing[1], 0f },
                Scale = 1,
                UniqueId = -1,
                Folder = Int(tag.SelectField(e + "ShortBlockIndex:editor folder")),
                Block = "cutscene flags",
                BlockIndex = i,
            };
        }

        private static Placement ReadCamera(TagFile tag, int i)
        {
            string e = "Block:cutscene camera points[" + i + "]/";
            var o = Pad(Floats(tag.SelectField(e + "RealEulerAngles3d:orientation")));
            return new Placement
            {
                Index = i,
                Type = 0,
                Name = Text(tag, e + "String:name"),
                Position = Pad(Floats(tag.SelectField(e + "RealPoint3d:position"))),
                Rotation = new[] { o[0], o[1], o[2] },
                Scale = 1,
                UniqueId = -1,
                Folder = -1,
                Block = "cutscene camera points",
                BlockIndex = i,
            };
        }

        /// <summary>Script points (scripting data › point sets › points), as (set, point) pairs in order.</summary>
        private static List<(int Set, int Point)> ScriptPointList(TagFile tag)
        {
            var list = new List<(int, int)>();
            var sets = tag.SelectField("Block:scripting data[0]/Block:point sets") as TagFieldBlock;
            for (int s = 0; sets != null && s < sets.Elements.Count; s++)
            {
                var pts = tag.SelectField("Block:scripting data[0]/Block:point sets[" + s + "]/Block:points") as TagFieldBlock;
                for (int j = 0; pts != null && j < pts.Elements.Count; j++) list.Add((s, j));
            }
            return list;
        }

        private static Placement ReadScriptPoint(TagFile tag, int index, int s, int j)
        {
            string set = "Block:scripting data[0]/Block:point sets[" + s + "]/";
            string e = set + "Block:points[" + j + "]/";
            var facing = Pad(Floats(tag.SelectField(e + "RealEulerAngles2d:facing direction")), 2);
            return new Placement
            {
                Index = index,
                Type = 0,
                Name = Text(tag, e + "String:name"),
                Position = Pad(Floats(tag.SelectField(e + "RealPoint3d:position"))),
                Rotation = new[] { facing[0], facing[1], 0f },
                Scale = 1,
                UniqueId = -1,
                Folder = Int(tag.SelectField(set + "ShortBlockIndex:editor folder")),
                Label = "point set " + Text(tag, set + "String:name"),
                Block = "scripting data[0]/Block:point sets[" + s + "]/Block:points",
                BlockIndex = j,
            };
        }

        // ---- editing ---------------------------------------------------------------------------------

        /// <summary>The element path ("Block:...[i]/") of a pseudo placement's flat index.</summary>
        private static string ElementOf(TagFile tag, string category, int index)
        {
            switch (category)
            {
                case CampaignCategories.TriggerVolumes: return "Block:trigger volumes[" + index + "]/";
                case CampaignCategories.CutsceneFlags: return "Block:cutscene flags[" + index + "]/";
                case CampaignCategories.CutsceneCameras: return "Block:cutscene camera points[" + index + "]/";
                case CampaignCategories.AiSpawnPoints:
                {
                    var all = AiPoints(tag);
                    if (index < 0 || index >= all.Count) throw new ArgumentOutOfRangeException("index", "No ai spawn point " + index);
                    return "Block:squads[" + all[index].Squad + "]/Block:spawn points[" + all[index].Point + "]/";
                }
                case CampaignCategories.ScriptPoints:
                {
                    var all = ScriptPointList(tag);
                    if (index < 0 || index >= all.Count) throw new ArgumentOutOfRangeException("index", "No script point " + index);
                    return "Block:scripting data[0]/Block:point sets[" + all[index].Set + "]/Block:points[" + all[index].Point + "]/";
                }
                default: throw new ArgumentException("Unknown category " + category);
            }
        }

        private Placement ReadCampaign(TagFile tag, string category, int index)
        {
            switch (category)
            {
                case CampaignCategories.TriggerVolumes: return ReadVolume(tag, index);
                case CampaignCategories.CutsceneFlags: return ReadFlag(tag, index);
                case CampaignCategories.CutsceneCameras: return ReadCamera(tag, index);
                case CampaignCategories.AiSpawnPoints: { var a = AiPoints(tag)[index]; return ReadAiPoint(tag, index, a.Squad, a.Point); }
                default: { var a = ScriptPointList(tag)[index]; return ReadScriptPoint(tag, index, a.Set, a.Point); }
            }
        }

        private Placement CampaignSet(TagFile tag, SetPlacementParams p)
        {
            string e = ElementOf(tag, p.Category, p.Index);
            switch (p.Category)
            {
                case CampaignCategories.TriggerVolumes:
                    SetVolume(tag, e, p);
                    break;
                case CampaignCategories.AiSpawnPoints:
                    if (p.Position != null) SetFloats(tag.SelectField(e + "RealPoint3d:position"), p.Position);
                    if (p.Rotation != null && p.Rotation.Length >= 2)
                    {
                        SetFloats(tag.SelectField(e + "RealEulerAngles2d:facing (yaw, pitch)"), new[] { p.Rotation[0], p.Rotation[1] });
                        if (p.Rotation.Length > 2) TrySet(() => ((TagFieldElementSingle)tag.SelectField(e + "Real:roll")).Data = p.Rotation[2]);
                    }
                    break;
                case CampaignCategories.CutsceneFlags:
                    if (p.Position != null) SetFloats(tag.SelectField(e + "RealPoint3d:position"), p.Position);
                    if (p.Rotation != null && p.Rotation.Length >= 2) SetFloats(tag.SelectField(e + "RealEulerAngles2d:facing"), new[] { p.Rotation[0], p.Rotation[1] });
                    break;
                case CampaignCategories.CutsceneCameras:
                    if (p.Position != null) SetFloats(tag.SelectField(e + "RealPoint3d:position"), p.Position);
                    if (p.Rotation != null && p.Rotation.Length >= 3) SetFloats(tag.SelectField(e + "RealEulerAngles3d:orientation"), p.Rotation.Take(3).ToArray());
                    break;
                case CampaignCategories.ScriptPoints:
                    if (p.Position != null) SetFloats(tag.SelectField(e + "RealPoint3d:position"), p.Position);
                    if (p.Position != null)
                    {
                        // The AI find a point through its bsp (as Bungie's points: structure bsp set, bsp and sector -1 for the game to work out).
                        int bsp = NearestPointBsp(tag, e, p.Position);
                        if (bsp < 0) bsp = BspAt(tag, p.Position);
                        SetIndexByName(tag, e, "structure bsp", bsp);
                        SetIndexByName(tag, e, "bsp index", -1);
                        SetIndexByName(tag, e, "sector index", -1);
                    }
                    if (p.Rotation != null && p.Rotation.Length >= 2) SetFloats(tag.SelectField(e + "RealEulerAngles2d:facing direction"), new[] { p.Rotation[0], p.Rotation[1] });
                    break;
            }
            return ReadCampaign(tag, p.Category, p.Index);
        }

        /// <summary>
        /// Moves (to a new middle), turns (boxes only) or resizes (boxes only) a trigger volume. A sector moves as a whole:
        /// its points, its runtime triangles (planes and corners) and its runtime bounds.
        /// </summary>
        private static void SetVolume(TagFile tag, string e, SetPlacementParams p)
        {
            bool sector = SafeValue(() => Convert.ToInt32(((TagFieldEnum)tag.SelectField(e + "ShortEnum:type")).Value), 0) == 1;
            if (sector)
            {
                if (p.Position == null) return;
                var (min, max) = SectorBounds(tag, e);
                var d = new[] { p.Position[0] - (min[0] + max[0]) / 2, p.Position[1] - (min[1] + max[1]) / 2, p.Position[2] - (min[2] + max[2]) / 2 };
                if (Math.Abs(d[0]) + Math.Abs(d[1]) + Math.Abs(d[2]) < 1e-5f) return;
                var pts = tag.SelectField(e + "Block:sector points") as TagFieldBlock;
                for (int j = 0; pts != null && j < pts.Elements.Count; j++)
                {
                    var f = tag.SelectField(e + "Block:sector points[" + j + "]/RealPoint3d:position");
                    SetFloats(f, Add(Pad(Floats(f)), d));
                }
                var tris = tag.SelectField(e + "Block:runtime triangles") as TagFieldBlock;
                for (int t = 0; tris != null && t < tris.Elements.Count; t++)
                {
                    string te = e + "Block:runtime triangles[" + t + "]/";
                    for (int k = 0; k < 5; k++)
                    {
                        var f = tag.SelectField(te + "RealPlane3d:plane " + k);
                        if (f == null) continue;
                        var pl = Pad(Floats(f), 4);
                        // n·x = d, moved by d: the new distance is d + n·delta.
                        pl[3] += pl[0] * d[0] + pl[1] * d[1] + pl[2] * d[2];
                        SetFloats(f, pl);
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        var f = tag.SelectField(te + "RealPoint2d:vertex " + k);
                        if (f == null) continue;
                        var v = Pad(Floats(f), 2);
                        SetFloats(f, new[] { v[0] + d[0], v[1] + d[1] });
                    }
                }
                string[] axes = { "x", "y", "z" };
                for (int a = 0; a < 3; a++)
                    foreach (string end in new[] { "0", "1" })
                    {
                        var f = tag.SelectField(e + "Real:runtime sector bounds " + axes[a] + end) as TagFieldElementSingle;
                        if (f != null) f.Data += d[a];
                    }
                return;
            }
            var box = Box(tag, e);
            float[] center = p.Position != null ? Pad(p.Position) : box.Center;
            float[] forward = box.Forward, up = box.Up;
            if (p.Rotation != null && p.Rotation.Length >= 1)
            {
                double yaw = p.Rotation[0] * Math.PI / 180, pitch = (p.Rotation.Length > 1 ? p.Rotation[1] : 0) * Math.PI / 180;
                forward = new[] { (float)(Math.Cos(yaw) * Math.Cos(pitch)), (float)(Math.Sin(yaw) * Math.Cos(pitch)), (float)Math.Sin(pitch) };
                up = new[] { (float)(-Math.Cos(yaw) * Math.Sin(pitch)), (float)(-Math.Sin(yaw) * Math.Sin(pitch)), (float)Math.Cos(pitch) };
            }
            float[] ext = box.Extents;
            if (p.Size != null && p.Size.Length >= 3) ext = new[] { Math.Max(0.05f, p.Size[0]), Math.Max(0.05f, p.Size[1]), Math.Max(0.05f, p.Size[2]) };
            float[] left = Cross(up, forward);
            float[] corner = Add(center, Mul(Add(Mul(forward, ext[0]), Add(Mul(left, ext[1]), Mul(up, ext[2]))), -0.5f));
            SetFloats(tag.SelectField(e + "RealPoint3d:position"), corner);
            SetFloats(tag.SelectField(e + "RealVector3d:forward"), forward);
            SetFloats(tag.SelectField(e + "RealVector3d:up"), up);
            SetFloats(tag.SelectField(e + "RealPoint3d:extents"), ext);
        }

        private PlacementOpResult CampaignOp(TagFile tag, PlacementOpParams p)
        {
            string category = p.Category;
            int select = -1;
            switch (p.Op)
            {
                case PlacementOps.Add:
                case PlacementOps.Duplicate:
                    select = CampaignAdd(tag, category, p.Op == PlacementOps.Duplicate ? p.Index : -1, p.Position);
                    break;
                case PlacementOps.Remove:
                {
                    string e = ElementOf(tag, category, p.Index).TrimEnd('/');
                    int open = e.LastIndexOf('[');
                    var block = tag.SelectField(e.Substring(0, open)) as TagFieldBlock;
                    int i = int.Parse(e.Substring(open + 1, e.Length - open - 2), CultureInfo.InvariantCulture);
                    block.RemoveElement(i);
                    break;
                }
                case PlacementOps.Rename:
                {
                    string e = ElementOf(tag, category, p.Index);
                    string name = (p.Tag ?? "").Trim();
                    if (name.Length == 0) throw new ArgumentException("Give it a name: scripts refer to it by name.");
                    switch (category)
                    {
                        case CampaignCategories.CutsceneCameras: SetText(tag.SelectField(e + "String:name"), name); break;
                        case CampaignCategories.ScriptPoints:
                            SetText(tag.SelectField(e + "String:name"), name);
                            TrySet(() => SetText(tag.SelectField(e + "StringId:name_id"), name));
                            break;
                        default: SetText(tag.SelectField(e + "StringId:name"), name); break;
                    }
                    select = p.Index;
                    break;
                }
                case PlacementOps.SetFolder:
                {
                    string e = ElementOf(tag, category, p.Index);
                    var f = tag.SelectField(e + "ShortBlockIndex:editor folder") as TagFieldBlockIndex;
                    if (f == null) throw new InvalidOperationException("These are kept in their squad's or point set's folder.");
                    f.Value = p.Folder;
                    select = p.Index;
                    break;
                }
                default:
                    throw new ArgumentException(PaletteName(category) + "s cannot " + p.Op + ".");
            }
            return new PlacementOpResult { Category = CampaignCategory(tag, category), SelectIndex = select };
        }

        /// <summary>
        /// A new element: a copy of <paramref name="copyOf"/> (its squad or point set for spawn and script points), or for
        /// volumes, flags and cameras a fresh one. New names are made unique (scripts refer to these by name).
        /// Returns the new flat index.
        /// </summary>
        private int CampaignAdd(TagFile tag, string category, int copyOf, float[] position)
        {
            string blockPath;
            int source = -1;
            switch (category)
            {
                case CampaignCategories.AiSpawnPoints:
                case CampaignCategories.ScriptPoints:
                {
                    bool ai = category == CampaignCategories.AiSpawnPoints;
                    if (!ai && copyOf < 0)
                    {
                        // Add (not Duplicate): a new point set (ps_path, ps_path_2...) with this first point.
                        var sets = tag.SelectField("Block:scripting data[0]/Block:point sets") as TagFieldBlock;
                        if (sets == null)
                        {
                            var data0 = tag.SelectField("Block:scripting data") as TagFieldBlock;
                            if (data0 == null) throw new InvalidOperationException("This map has no scripting data block.");
                            if (data0.Elements.Count == 0) data0.AddElement();
                            sets = tag.SelectField("Block:scripting data[0]/Block:point sets") as TagFieldBlock;
                        }
                        if (sets == null) throw new InvalidOperationException("This map has no point sets block.");
                        var setNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        for (int s = 0; s < sets.Elements.Count; s++) setNames.Add(Text(tag, "Block:scripting data[0]/Block:point sets[" + s + "]/String:name") ?? "");
                        string setName = "ps_path";
                        for (int n = 2; setNames.Contains(setName); n++) setName = "ps_path_" + n;
                        sets.AddElement();
                        int si = sets.Elements.Count - 1;
                        string setPath = "Block:scripting data[0]/Block:point sets[" + si + "]/";
                        SetText(tag.SelectField(setPath + "String:name"), setName);
                        TrySet(() => ((TagFieldBlockIndex)tag.SelectField(setPath + "ShortBlockIndex:bsp index")).Value = -1);
                        TrySet(() => ((TagFieldBlockIndex)tag.SelectField(setPath + "ShortBlockIndex:editor folder")).Value = -1);
                        blockPath = setPath + "Block:points";
                        break;
                    }
                    List<(int, int)> all = new List<(int, int)>();
                    if (ai) foreach (var x in AiPoints(tag)) all.Add((x.Squad, x.Point));
                    else foreach (var x in ScriptPointList(tag)) all.Add((x.Set, x.Point));
                    if (all.Count == 0) throw new InvalidOperationException(ai ? "This map has no squad to add a spawn point to." : "This map has no point set to add a point to.");
                    var at = copyOf >= 0 && copyOf < all.Count ? all[copyOf] : all[all.Count - 1];
                    blockPath = ai ? "Block:squads[" + at.Item1 + "]/Block:spawn points" : "Block:scripting data[0]/Block:point sets[" + at.Item1 + "]/Block:points";
                    source = at.Item2;
                    break;
                }
                case CampaignCategories.TriggerVolumes: blockPath = "Block:trigger volumes"; break;
                case CampaignCategories.CutsceneFlags: blockPath = "Block:cutscene flags"; break;
                case CampaignCategories.CutsceneCameras: blockPath = "Block:cutscene camera points"; break;
                default: throw new ArgumentException("Unknown category " + category);
            }
            var block = tag.SelectField(blockPath) as TagFieldBlock;
            if (block == null) throw new InvalidOperationException("No " + blockPath);
            if (block.Elements.Count >= block.MaximumElementCount) throw new InvalidOperationException("No room for more " + PaletteName(category) + "s.");
            if (source < 0 && copyOf >= 0) source = copyOf;
            byte[] data = source >= 0 && source < block.Elements.Count ? block.SerializeElement(source) : null;
            block.AddElement();
            int i = block.Elements.Count - 1;
            if (data != null) block.DeserializeElement(i, data);
            string e = blockPath + "[" + i + "]/";
            string baseName = data != null ? CampaignName(tag, category, e) : (category == CampaignCategories.TriggerVolumes ? "volume" : category == CampaignCategories.CutsceneFlags ? "flag" : "camera");
            string unique = UniqueName(tag, category, baseName);
            switch (category)
            {
                case CampaignCategories.CutsceneCameras: SetText(tag.SelectField(e + "String:name"), unique); break;
                case CampaignCategories.ScriptPoints:
                {
                    // Points are named within their set, Bungie's way: p0, p1, p2... (scripts say ps_path/p1).
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int k = 0; k < block.Elements.Count; k++) if (k != i) names.Add(Text(tag, blockPath + "[" + k + "]/String:name") ?? "");
                    string pointName = "p" + i;
                    for (int k = i + 1; names.Contains(pointName); k++) pointName = "p" + k;
                    SetText(tag.SelectField(e + "String:name"), pointName);
                    TrySet(() => SetText(tag.SelectField(e + "StringId:name_id"), pointName));
                    break;
                }
                default: TrySet(() => SetText(tag.SelectField(e + "StringId:name"), unique)); break;
            }
            if (category == CampaignCategories.TriggerVolumes && data == null)
            {
                // A new box, 4 x 4 x 3, standing on the point.
                TrySet(() => ((TagFieldEnum)tag.SelectField(e + "ShortEnum:type")).Value = 0);
                SetFloats(tag.SelectField(e + "RealVector3d:forward"), new[] { 1f, 0f, 0f });
                SetFloats(tag.SelectField(e + "RealVector3d:up"), new[] { 0f, 0f, 1f });
                SetFloats(tag.SelectField(e + "RealPoint3d:extents"), new[] { 4f, 4f, 3f });
                TrySet(() => ((TagFieldBlockIndex)tag.SelectField(e + "ShortBlockIndex:object name")).Value = -1);
                TrySet(() => ((TagFieldBlockIndex)tag.SelectField(e + "ShortBlockIndex:kill trigger volume")).Value = -1);
                TrySet(() => ((TagFieldBlockIndex)tag.SelectField(e + "ShortBlockIndex:editor folder")).Value = -1);
                if (position != null) SetFloats(tag.SelectField(e + "RealPoint3d:position"), new[] { position[0] - 2, position[1] - 2, position[2] });
                position = null;
            }
            if (category == CampaignCategories.CutsceneFlags && data == null)
                TrySet(() => ((TagFieldBlockIndex)tag.SelectField(e + "ShortBlockIndex:editor folder")).Value = -1);
            int flat = FlatIndex(tag, category, blockPath, i);
            if (position != null) CampaignSet(tag, new SetPlacementParams { Category = category, Index = flat, Position = position });
            return flat;
        }

        private static string CampaignName(TagFile tag, string category, string e)
        {
            switch (category)
            {
                case CampaignCategories.CutsceneCameras:
                case CampaignCategories.ScriptPoints: return Text(tag, e + "String:name");
                default: return Text(tag, e + "StringId:name");
            }
        }

        /// <summary>name, name_2, name_3...: the first that no other element of this kind has.</summary>
        private string UniqueName(TagFile tag, string category, string baseName)
        {
            string b = string.IsNullOrWhiteSpace(baseName) ? PaletteName(category).Replace(' ', '_') : baseName.Trim();
            int us = b.LastIndexOf('_');
            int n;
            if (us > 0 && int.TryParse(b.Substring(us + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) b = b.Substring(0, us);
            var taken = new HashSet<string>(CampaignCategory(tag, category).Items.Select(x => x.Name ?? ""), StringComparer.OrdinalIgnoreCase);
            for (int k = 2; ; k++)
            {
                string candidate = b + "_" + k;
                if (candidate.Length > 31) candidate = candidate.Substring(candidate.Length - 31);
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        private static int FlatIndex(TagFile tag, string category, string blockPath, int i)
        {
            if (category == CampaignCategories.AiSpawnPoints)
            {
                var all = AiPoints(tag);
                return all.FindIndex(x => blockPath == "Block:squads[" + x.Squad + "]/Block:spawn points" && x.Point == i);
            }
            if (category == CampaignCategories.ScriptPoints)
            {
                var all = ScriptPointList(tag);
                return all.FindIndex(x => blockPath == "Block:scripting data[0]/Block:point sets[" + x.Set + "]/Block:points" && x.Point == i);
            }
            return i;
        }

        // ---- zone sets -------------------------------------------------------------------------------

        private static List<ZoneSetInfo> ZoneSets(TagFile tag, int bspCount)
        {
            var list = new List<ZoneSetInfo>();
            var block = tag.SelectField("Block:zone sets") as TagFieldBlock;
            for (int i = 0; block != null && i < block.Elements.Count; i++)
            {
                string e = "Block:zone sets[" + i + "]/";
                string name = Text(tag, e + "StringId:name");
                if (name.Length == 0) name = Text(tag, e + "LongString:name string");
                list.Add(new ZoneSetInfo { Index = i, Name = name, Bsps = BlockFlagBits(tag.SelectField(e + "BlockFlags:bsp zone flags"), bspCount) });
            }
            return list;
        }

        private static bool _flagsLogged;

        /// <summary>
        /// The set bits of a "block flags" field (one bit per element of another block, e.g. which bsps a zone set loads).
        /// ManagedBlam's type for these is read by reflection (a bit test method, or an integer value); null if unknown.
        /// </summary>
        private static List<int> BlockFlagBits(TagField f, int count)
        {
            if (f == null) return null;
            Type t = f.GetType();
            try
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    var ps = m.GetParameters();
                    if (m.ReturnType != typeof(bool) || ps.Length != 1 || ps[0].ParameterType != typeof(int)) continue;
                    string n = m.Name.ToLowerInvariant();
                    if (!(n.Contains("bit") || n.Contains("set") || n.Contains("test") || n.Contains("flag"))) continue;
                    var bits = new List<int>();
                    for (int i = 0; i < count; i++) if ((bool)m.Invoke(f, new object[] { i })) bits.Add(i);
                    return bits;
                }
                foreach (string pn in new[] { "RawValue", "Value", "Data", "Flags", "Bits" })
                {
                    PropertyInfo pi = t.GetProperty(pn, BindingFlags.Public | BindingFlags.Instance);
                    if (pi == null) continue;
                    object v = pi.GetValue(f);
                    ulong raw;
                    if (v is System.Collections.BitArray ba)
                    {
                        var bits = new List<int>();
                        for (int i = 0; i < Math.Min(count, ba.Length); i++) if (ba[i]) bits.Add(i);
                        return bits;
                    }
                    if (v is IEnumerable<bool> bools)
                        return bools.Select((b, i) => (b, i)).Where(x => x.b && x.i < count).Select(x => x.i).ToList();
                    if (v is Array arr && arr.Length > 0 && !(v is string))
                    {
                        var bits = new List<int>();
                        int width = System.Runtime.InteropServices.Marshal.SizeOf(arr.GetValue(0).GetType()) * 8;
                        for (int w = 0; w < arr.Length; w++)
                        {
                            ulong word = Convert.ToUInt64(arr.GetValue(w));
                            for (int b = 0; b < width; b++) if ((word >> b & 1) != 0 && w * width + b < count) bits.Add(w * width + b);
                        }
                        return bits;
                    }
                    if (v != null && ulong.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out raw))
                    {
                        var bits = new List<int>();
                        for (int i = 0; i < Math.Min(64, count); i++) if ((raw >> i & 1) != 0) bits.Add(i);
                        return bits;
                    }
                }
            }
            catch (Exception ex)
            {
                Server.Log("block flags: " + ex.Message);
            }
            if (!_flagsLogged)
            {
                _flagsLogged = true;
                Server.Log("block flags: unknown field type " + t.FullName + ": " + string.Join(", ",
                    t.GetMembers(BindingFlags.Public | BindingFlags.Instance).Select(m => m.MemberType + " " + m.Name).Distinct()));
            }
            return null;
        }

        /// <summary>Called before Kalo saves a scenario (see DetachEditedResources). Returns how many resource tags were detached.</summary>
        public int BeforeSave(string path)
        {
            int n = DetachEditedResources(_tags.GetOrOpen(path), path);
            if (n > 0) Server.Log(path + ": " + n + " split resource tag(s) detached; the game now reads the scenario's own blocks.");
            return n;
        }

        // ---- mission scripts ---------------------------------------------------------------------------

        private static List<string> MissionScripts(TagFile tag)
        {
            var list = new List<string>();
            var res = tag.SelectField("Block:scenario resources") as TagFieldBlock;
            for (int r = 0; res != null && r < res.Elements.Count; r++)
            {
                var src = tag.SelectField("Block:scenario resources[" + r + "]/Block:script source") as TagFieldBlock;
                for (int i = 0; src != null && i < src.Elements.Count; i++)
                {
                    string s = TagService.ValueOf(tag.SelectField("Block:scenario resources[" + r + "]/Block:script source[" + i + "]/Reference:reference"));
                    if (!string.IsNullOrEmpty(s) && !list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
                }
            }
            return list;
        }

        /// <summary>
        /// A new campaign map gets its own copies of the template mission's scripts (those kept in the template's folder,
        /// e.g. levels\solo\m10\scripts\m10.scenario_hs_source_file → levels\mods\mod\map\scripts\...), so they can be edited
        /// for this map only. Shared scripts (globals\...) stay shared. Returns the new script tags.
        /// </summary>
        private List<string> CopyMissionScripts(TagFile tag, string template, string newPath)
        {
            var copied = new List<string>();
            string templateDir = (Path.GetDirectoryName(template.Replace('/', '\\')) ?? "") + "\\";
            string newDir = (Path.GetDirectoryName(newPath.Replace('/', '\\')) ?? "") + "\\";
            var res = tag.SelectField("Block:scenario resources") as TagFieldBlock;
            for (int r = 0; res != null && r < res.Elements.Count; r++)
            {
                var src = tag.SelectField("Block:scenario resources[" + r + "]/Block:script source") as TagFieldBlock;
                for (int i = 0; src != null && i < src.Elements.Count; i++)
                {
                    var field = tag.SelectField("Block:scenario resources[" + r + "]/Block:script source[" + i + "]/Reference:reference") as TagFieldReference;
                    string s = field == null ? null : TagService.ValueOf(field);
                    if (string.IsNullOrEmpty(s) || !s.StartsWith(templateDir, StringComparison.OrdinalIgnoreCase)) continue;
                    string target = newDir + s.Substring(templateDir.Length);
                    if (!File.Exists(_tags.FullPath(target)))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_tags.FullPath(target)));
                        using (var t = new TagFile(TagService.TagPathOf(s))) t.SaveAs(TagService.TagPathOf(target));
                    }
                    field.Path = TagService.TagPathOf(target);
                    copied.Add(target);
                }
            }
            return copied;
        }

        public MissionScriptResult ReadMissionScript(MissionScriptParams p)
        {
            var sw = Stopwatch.StartNew();
            TagFile tag = _tags.GetOrOpen(p.Path);
            var data = tag.SelectField("Data:source") as TagFieldData;
            if (data == null) throw new InvalidOperationException(p.Path + " is not a mission script (no source).");
            byte[] bytes = data.GetData() ?? new byte[0];
            int n = bytes.Length;
            while (n > 0 && bytes[n - 1] == 0) n--;
            return new MissionScriptResult
            {
                Path = p.Path,
                Name = Text(tag, "String:name"),
                Text = Encoding.UTF8.GetString(bytes, 0, n),
                Ms = sw.ElapsedMilliseconds,
            };
        }

        /// <summary>
        /// An empty campaign mission: the level, its zone sets, player starts, objects and markers stay; the AI (squads,
        /// groups, patrols, objectives, cues), the cutscenes and the mission's own scripts go, replaced by one starter
        /// script. Trigger volumes, cutscene flags and point sets stay: without scripts nothing uses them, and removing
        /// volumes would break the scenario's precomputed volume data (mopp code, zone set switches, kill zones).
        /// </summary>
        private void EmptyMission(TagFile tag, string template, string newPath, CreateMapResult result)
        {
            foreach (string block in new[] { "squad patrols", "actual cues", "full cues", "quick cues", "mission scenes", "ai objectives", "squads", "squad groups", "cinematics" })
            {
                var b = tag.SelectField("Block:" + block) as TagFieldBlock;
                if (b == null) continue;
                result.Removed += b.Elements.Count;
                b.RemoveAllElements();
            }
            // Scenery that spawns AI (turrets...) pointed at squads.
            for (int i = 0, n = Count(tag, "Block:scenery"); i < n; i++)
                TrySet(() => ((TagFieldBlockIndex)tag.SelectField("Block:scenery[" + i + "]/Struct:scenery data[0]/ShortBlockIndex:ai spawning squad")).Value = -1);

            // Scripts: the mission's own files go; one starter file named after the map takes their place.
            string name = Path.GetFileNameWithoutExtension(newPath);
            string newDir = (Path.GetDirectoryName(newPath.Replace('/', '\\')) ?? "") + "\\";
            string starter = newDir + "scripts\\" + name + ".scenario_hs_source_file";
            string first = null;
            var res = tag.SelectField("Block:scenario resources") as TagFieldBlock;
            for (int r = 0; res != null && r < res.Elements.Count; r++)
            {
                var src = tag.SelectField("Block:scenario resources[" + r + "]/Block:script source") as TagFieldBlock;
                for (int i = src == null ? -1 : src.Elements.Count - 1; i >= 0; i--)
                {
                    var field = tag.SelectField("Block:scenario resources[" + r + "]/Block:script source[" + i + "]/Reference:reference") as TagFieldReference;
                    string s = field == null ? null : TagService.ValueOf(field);
                    if (string.IsNullOrEmpty(s) || !s.StartsWith(newDir, StringComparison.OrdinalIgnoreCase)) continue;
                    // The mission's main script (m10 for m10) is the one to start from.
                    if (first == null || string.Equals(Path.GetFileNameWithoutExtension(s), Path.GetFileNameWithoutExtension(template), StringComparison.OrdinalIgnoreCase)) first = s;
                    src.RemoveElement(i);
                }
            }
            if (first == null) throw new InvalidOperationException("The mission has no script of its own to start from.");
            // The starter is a copy of one of the mission's script files (so it is a valid script source tag), with new text.
            string firstFull = _tags.FullPath(first), starterFull = _tags.FullPath(starter);
            Directory.CreateDirectory(Path.GetDirectoryName(starterFull));
            if (!string.Equals(firstFull, starterFull, StringComparison.OrdinalIgnoreCase)) File.Copy(firstFull, starterFull, true);
            foreach (string old in result.Scripts ?? new List<string>())
                if (!string.Equals(old, starter, StringComparison.OrdinalIgnoreCase)) TryDelete(_tags.FullPath(old));
            var src0 = tag.SelectField("Block:scenario resources[0]/Block:script source") as TagFieldBlock;
            if (src0 == null) throw new InvalidOperationException("The mission has no script source block.");
            src0.AddElement();
            ((TagFieldReference)tag.SelectField("Block:scenario resources[0]/Block:script source[" + (src0.Elements.Count - 1) + "]/Reference:reference")).Path = TagService.TagPathOf(starter);
            using (var s = new TagFile(TagService.TagPathOf(starter)))
            {
                var data = s.SelectField("Data:source") as TagFieldData;
                byte[] text = Encoding.UTF8.GetBytes(StarterScript(name).Replace("\n", "\r\n"));
                MethodInfo set = data == null ? null : data.GetType().GetMethod("SetData", new[] { typeof(byte[]) });
                if (set == null) throw new InvalidOperationException("ManagedBlam cannot write the starter script.");
                set.Invoke(data, new object[] { text.Concat(new byte[] { 0 }).ToArray() });
                TrySet(() => SetText(s.SelectField("String:name"), name));
                s.Save();
            }
            result.Scripts = new List<string> { starter };
        }

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>The script an empty mission starts with (HaloScript; Kalo shows it in Kalo Script).</summary>
        private static string StarterScript(string name) =>
            "; " + name + ": your mission's script. Kalo shows it in Kalo Script (Mission scripts in the Campaign bar).\n" +
            "; A startup script runs once when the mission starts.\n" +
            "\n" +
            "(script startup mission_start\n" +
            "    (print \"mission started\")\n" +
            ")\n";

        public MissionScriptResult WriteMissionScript(MissionScriptParams p)
        {
            var sw = Stopwatch.StartNew();
            TagFile tag = _tags.GetOrOpen(p.Path);
            var data = tag.SelectField("Data:source") as TagFieldData;
            if (data == null) throw new InvalidOperationException(p.Path + " is not a mission script (no source).");
            byte[] old = data.GetData() ?? new byte[0];
            byte[] text = Encoding.UTF8.GetBytes((p.Text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"));
            // Keep the original's trailing zero, if it had one.
            byte[] bytes = old.Length > 0 && old[old.Length - 1] == 0 ? text.Concat(new byte[] { 0 }).ToArray() : text;
            MethodInfo set = data.GetType().GetMethod("SetData", new[] { typeof(byte[]) });
            if (set == null) throw new InvalidOperationException("ManagedBlam cannot write data fields (no SetData).");
            set.Invoke(data, new object[] { bytes });
            tag.Save();
            return new MissionScriptResult { Path = p.Path, Name = Text(tag, "String:name"), Text = p.Text, Ms = sw.ElapsedMilliseconds };
        }

        // ---- small vector helpers ------------------------------------------------------------------------

        private static float[] Pad(float[] v, int n = 3)
        {
            var r = new float[n];
            for (int i = 0; i < n && v != null && i < v.Length; i++) r[i] = v[i];
            return r;
        }
        private static float[] V(float[] v) => new[] { v[0], v[1], v[2] };
        private static float[] Add(float[] a, float[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        private static float[] Mul(float[] a, float k) => new[] { a[0] * k, a[1] * k, a[2] * k };
        private static float[] Cross(float[] a, float[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        private static float[] Normalize(float[] v, float[] fallback)
        {
            double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return l < 1e-6 ? fallback : new[] { (float)(v[0] / l), (float)(v[1] / l), (float)(v[2] / l) };
        }
    }
}
