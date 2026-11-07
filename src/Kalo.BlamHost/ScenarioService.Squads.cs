using System;
using System.Collections.Generic;
using System.Diagnostics;
using Bungie.Tags;
using Kalo.Protocol;

namespace Kalo.BlamHost
{
    /// <summary>
    /// AI squads of campaign missions: the squad list for the map view's object list (squad groups → squads → spawn
    /// points), and one squad's cells and spawn points with what spawns (characters, weapons) for the squad editor.
    /// </summary>
    internal sealed partial class ScenarioService
    {
        private static readonly string[] Teams = { "default", "player", "human", "covenant", "brute", "mule", "spare", "covenant_player" };

        private static void AddSquads(TagFile tag, ScenarioPlacements result)
        {
            result.Squads = new List<SquadSummary>();
            result.SquadGroups = new List<SquadGroupInfo>();
            var groups = tag.SelectField("Block:squad groups") as TagFieldBlock;
            for (int i = 0; groups != null && i < groups.Elements.Count; i++)
            {
                string g = "Block:squad groups[" + i + "]/";
                result.SquadGroups.Add(new SquadGroupInfo { Index = i, Name = Text(tag, g + "String:name"), Parent = Int(tag.SelectField(g + "ShortBlockIndex:parent")) });
            }
            var squads = tag.SelectField("Block:squads") as TagFieldBlock;
            for (int i = 0; squads != null && i < squads.Elements.Count; i++)
            {
                string s = "Block:squads[" + i + "]/";
                result.Squads.Add(new SquadSummary { Index = i, Name = Text(tag, s + "String:name"), Group = Int(tag.SelectField(s + "ShortBlockIndex:parent")) });
            }
        }

        public SquadDetail Squad(string path, int index)
        {
            var sw = Stopwatch.StartNew();
            TagFile tag = _tags.GetOrOpen(path);
            var squads = tag.SelectField("Block:squads") as TagFieldBlock;
            if (squads == null || index < 0 || index >= squads.Elements.Count) throw new ArgumentOutOfRangeException("index", "No squad " + index);
            string s = "Block:squads[" + index + "]/";
            var d = new SquadDetail
            {
                Index = index,
                Name = Text(tag, s + "String:name"),
                Template = Text(tag, s + "StringId:template"),
                Groups = new List<string>(),
                Cells = new List<SquadCell>(),
                SpawnPoints = new List<SquadPoint>(),
                Characters = PaletteList(tag, "Block:character palette", "Reference:reference"),
                Weapons = PaletteList(tag, "Block:weapon palette", "Reference:name"),
                Vehicles = PaletteList(tag, "Block:vehicle palette", "Reference:name"),
            };
            int team = SafeValue(() => Convert.ToInt32(((TagFieldEnum)tag.SelectField(s + "ShortEnum:team")).Value), -1);
            // Where it starts: its initial zone and objective (by name), for the squad's plain summary.
            foreach (TagField f in squads.Elements[index].Fields)
            {
                string fn = f.FieldName ?? "";
                if (fn.StartsWith("initial objective", StringComparison.OrdinalIgnoreCase))
                {
                    int o = Num(tag, s + f.FieldPath.Substring(f.FieldPath.LastIndexOf('/') + 1));
                    if (o < 0) o = Int(f);
                    if (o >= 0) d.Objective = FirstText(tag, "Block:ai objectives[" + o + "]/", "StringId:name", "String:name");
                }
                else if (fn.StartsWith("initial zone", StringComparison.OrdinalIgnoreCase))
                {
                    int z = Int(f);
                    if (z >= 0) d.Zone = FirstText(tag, "Block:zones[" + z + "]/", "String:name", "StringId:name");
                }
            }
            d.Team = team >= 0 && team < Teams.Length ? Teams[team] : "";
            var seen = new HashSet<int>();
            for (int g = Int(tag.SelectField(s + "ShortBlockIndex:parent")); g >= 0 && seen.Add(g); g = Int(tag.SelectField("Block:squad groups[" + g + "]/ShortBlockIndex:parent")))
                d.Groups.Add(Text(tag, "Block:squad groups[" + g + "]/String:name"));

            // Designer cells are the squad's own; a squad made from a squad template keeps the template's in "templated".
            int designer = Count(tag, s + "Struct:designer[0]/Block:cells");
            int templated = Count(tag, s + "Struct:templated[0]/Block:cells");
            d.CellsKind = designer == 0 && templated > 0 ? "templated" : "designer";
            string cells = s + "Struct:" + d.CellsKind + "[0]/Block:cells";
            for (int c = 0, n = Count(tag, cells); c < n; c++)
            {
                string e = cells + "[" + c + "]/";
                d.Cells.Add(new SquadCell
                {
                    Index = c,
                    Name = Text(tag, e + "StringId:name"),
                    Count = Int(tag.SelectField(e + "ShortInteger:normal diff count")),
                    CountPath = e + "ShortInteger:normal diff count",
                    Characters = Choices(tag, e + "Block:character type", "character type"),
                    Weapons = Choices(tag, e + "Block:initial weapon", "weapon type"),
                });
                // Its vehicle (a vehicle palette index): the field's exact type name varies, find it by name.
                var cellBlock = tag.SelectField(cells) as TagFieldBlock;
                if (cellBlock != null)
                    foreach (TagField f in cellBlock.Elements[c].Fields)
                        if (string.Equals(f.FieldName, "vehicle type", StringComparison.OrdinalIgnoreCase))
                        {
                            d.Cells[d.Cells.Count - 1].VehiclePath = e + f.FieldPath.Substring(f.FieldPath.LastIndexOf('/') + 1);
                            d.Cells[d.Cells.Count - 1].Vehicle = Int(f);
                        }
            }
            for (int j = 0, n = Count(tag, s + "Block:spawn points"); j < n; j++)
            {
                string e = s + "Block:spawn points[" + j + "]/";
                d.SpawnPoints.Add(new SquadPoint
                {
                    Index = j,
                    Name = Text(tag, e + "StringId:name"),
                    Cell = Num(tag, e + "ShortBlockIndexCustomSearch:cell"),
                    Character = Int(tag.SelectField(e + "ShortBlockIndex:character type")),
                    CharacterPath = e + "ShortBlockIndex:character type",
                    Weapon = Int(tag.SelectField(e + "ShortBlockIndex:initial weapon")),
                    WeaponPath = e + "ShortBlockIndex:initial weapon",
                });
            }
            d.Ms = sw.ElapsedMilliseconds;
            return d;
        }

        public CategoriesResult Categories(CategoriesParams p)
        {
            TagFile tag = _tags.GetOrOpen(p.Path);
            var result = new CategoriesResult { Categories = new List<PlacementCategory>() };
            foreach (string name in p.Names ?? new List<string>())
            {
                var c = CampaignCategory(tag, name);
                if (c != null) result.Categories.Add(c);
            }
            var squads = new ScenarioPlacements();
            AddSquads(tag, squads);
            result.Squads = squads.Squads;
            return result;
        }

        public SquadOpResult SquadOp(SquadOpParams p)
        {
            TagFile tag = _tags.GetOrOpen(p.Path);
            var squads = tag.SelectField("Block:squads") as TagFieldBlock;
            if (p.Op == SquadOps.Create) return CreateSquad(tag, squads, p);
            if (squads == null || p.Index < 0 || p.Index >= squads.Elements.Count) throw new ArgumentOutOfRangeException("index", "No squad " + p.Index);
            switch (p.Op)
            {
                case SquadOps.Detach:
                {
                    int cells = Detach(tag, p.Index);
                    return new SquadOpResult { Index = p.Index, Name = Text(tag, "Block:squads[" + p.Index + "]/String:name"), Cells = cells };
                }
                case SquadOps.New:
                {
                    if (squads.Elements.Count >= squads.MaximumElementCount) throw new InvalidOperationException("No room for more squads in this map.");
                    byte[] data = squads.SerializeElement(p.Index);
                    squads.AddElement();
                    int i = squads.Elements.Count - 1;
                    squads.DeserializeElement(i, data);
                    string s = "Block:squads[" + i + "]/";
                    var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int k = 0; k < squads.Elements.Count; k++) if (k != i) taken.Add(Text(tag, "Block:squads[" + k + "]/String:name"));
                    string b = string.IsNullOrWhiteSpace(p.Name) ? Text(tag, "Block:squads[" + p.Index + "]/String:name") + "_new" : p.Name.Trim().Replace(' ', '_');
                    string name = b;
                    for (int n = 2; taken.Contains(name); n++) name = b + "_" + n;
                    SetText(tag.SelectField(s + "String:name"), name);
                    if (Count(tag, s + "Struct:designer[0]/Block:cells") == 0) Detach(tag, i);
                    // One spawn point, the first one's copy, at the position.
                    var points = tag.SelectField(s + "Block:spawn points") as TagFieldBlock;
                    if (points == null || points.Elements.Count == 0) throw new InvalidOperationException("The squad has no spawn point to copy.");
                    while (points.Elements.Count > 1) points.RemoveElement(points.Elements.Count - 1);
                    TrySet(() => SetText(tag.SelectField(s + "Block:spawn points[0]/StringId:name"), name + "_1"));
                    int flat = FlatIndex(tag, CampaignCategories.AiSpawnPoints, s + "Block:spawn points", 0);
                    if (p.Position != null && p.Position.Length >= 3)
                        CampaignSet(tag, new SetPlacementParams
                        {
                            Category = CampaignCategories.AiSpawnPoints, Index = flat,
                            Position = new[] { p.Position[0], p.Position[1], p.Position[2] },
                            Rotation = p.Position.Length > 3 ? new[] { p.Position[3], 0f } : null,
                        });
                    var after = new ScenarioPlacements();
                    AddSquads(tag, after);
                    return new SquadOpResult { Index = i, Name = name, SpawnPoint = flat, Squads = after.Squads, Category = CampaignCategory(tag, CampaignCategories.AiSpawnPoints) };
                }
                default:
                    throw new ArgumentException("Unknown squad op " + p.Op);
            }
        }

        private SquadOpResult CreateSquad(TagFile tag, TagFieldBlock squads, SquadOpParams p)
        {
            if (squads == null) throw new InvalidOperationException("This map has no squads block (not a campaign mission?).");
            if (squads.Elements.Count >= squads.MaximumElementCount) throw new InvalidOperationException("No room for more squads in this map.");
            if (Count(tag, "Block:character palette") == 0) throw new InvalidOperationException("This map has no characters in its character palette.");
            squads.AddElement();
            int i = squads.Elements.Count - 1;
            string s = "Block:squads[" + i + "]/";
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int k = 0; k < i; k++) taken.Add(Text(tag, "Block:squads[" + k + "]/String:name"));
            string b = string.IsNullOrWhiteSpace(p.Name) ? "squad" : p.Name.Trim().Replace(' ', '_');
            string name = b;
            for (int n = 2; taken.Contains(name); n++) name = b + "_" + n;
            SetText(tag.SelectField(s + "String:name"), name);
            TrySet(() => ((TagFieldEnum)tag.SelectField(s + "ShortEnum:team")).Value = 3); // covenant
            foreach (string f in new[] { "ShortBlockIndex:parent", "ShortBlockIndex:initial zone", "ShortBlockIndex:initial objective", "ShortBlockIndex:editor folder" })
                TrySet(() => ((TagFieldBlockIndex)tag.SelectField(s + f)).Value = -1);
            // One designer cell: one AI of the character, with the weapon.
            var cells = (TagFieldBlock)tag.SelectField(s + "Struct:designer[0]/Block:cells");
            cells.AddElement();
            string c = s + "Struct:designer[0]/Block:cells[0]/";
            TrySet(() => SetText(tag.SelectField(c + "StringId:name"), "cell"));
            ((TagFieldElementInteger)tag.SelectField(c + "ShortInteger:normal diff count")).Data = 1;
            ((TagFieldBlock)tag.SelectField(c + "Block:character type")).AddElement();
            int character = p.Character;
            if (character < 0)
            {
                // An Elite if the mission has one (a clear enemy to start with), else its first character.
                var palette = PaletteList(tag, "Block:character palette", "Reference:reference");
                character = Math.Max(0, palette.FindIndex(x => (x ?? "").EndsWith(@"elite\ai\elite.character", StringComparison.OrdinalIgnoreCase)));
            }
            ((TagFieldBlockIndex)tag.SelectField(c + "Block:character type[0]/ShortBlockIndex:character type")).Value = character;
            TrySet(() => ((TagFieldElementInteger)tag.SelectField(c + "Block:character type[0]/ShortInteger:chance")).Data = 1);
            if (p.Weapon >= 0)
            {
                ((TagFieldBlock)tag.SelectField(c + "Block:initial weapon")).AddElement();
                ((TagFieldBlockIndex)tag.SelectField(c + "Block:initial weapon[0]/ShortBlockIndex:weapon type")).Value = p.Weapon;
                TrySet(() => ((TagFieldElementInteger)tag.SelectField(c + "Block:initial weapon[0]/ShortInteger:chance")).Data = 1);
            }
            // One spawn point using that cell, no overrides.
            var points = (TagFieldBlock)tag.SelectField(s + "Block:spawn points");
            points.AddElement();
            string e = s + "Block:spawn points[0]/";
            TrySet(() => SetText(tag.SelectField(e + "StringId:name"), name + "_1"));
            TrySet(() => ((TagFieldBlockIndex)tag.SelectField(e + "ShortBlockIndex:character type")).Value = -1);
            TrySet(() => ((TagFieldBlockIndex)tag.SelectField(e + "ShortBlockIndex:initial weapon")).Value = -1);
            foreach (string f in new[] { "ShortBlockIndex:initial secondary weapon", "ShortBlockIndex:initial equipment", "ShortBlockIndex:vehicle type", "ShortBlockIndex:reference frame", "ShortBlockIndex:point set" })
                TrySet(() => ((TagFieldBlockIndex)tag.SelectField(e + f)).Value = -1);
            // No placement script (0 would be the mission's first script).
            SetIndexByName(tag, e, "placement script index", -1);
            // The spawn point uses the squad's cell 0 (-1 left the AI without the cell's character set-up and vehicle).
            SetIndexByName(tag, e, "cell", 0);
            int flat = FlatIndex(tag, CampaignCategories.AiSpawnPoints, s + "Block:spawn points", 0);
            if (p.Position != null && p.Position.Length >= 3)
                CampaignSet(tag, new SetPlacementParams
                {
                    Category = CampaignCategories.AiSpawnPoints, Index = flat,
                    Position = new[] { p.Position[0], p.Position[1], p.Position[2] },
                    Rotation = p.Position.Length > 3 ? new[] { p.Position[3], 0f } : null,
                });
            // Its level piece: the smallest bsp whose world bounds hold the point.
            if (p.Position != null && p.Position.Length >= 3)
            {
                var bounds = BspBounds(p.Path).Bounds;
                int best = -1; double bestVolume = double.MaxValue;
                for (int k = 0; k < bounds.Count; k++)
                {
                    var bb = bounds[k];
                    if (bb == null || p.Position[0] < bb[0] || p.Position[0] > bb[1] || p.Position[1] < bb[2] || p.Position[1] > bb[3] || p.Position[2] < bb[4] || p.Position[2] > bb[5]) continue;
                    double v = (double)(bb[1] - bb[0]) * (bb[3] - bb[2]) * (bb[5] - bb[4]);
                    if (v < bestVolume) { bestVolume = v; best = k; }
                }
                if (best >= 0) TrySet(() => ((TagFieldBlockIndex)tag.SelectField(e + "ShortBlockIndex:structure bsp")).Value = best);
            }
            var after = new ScenarioPlacements();
            AddSquads(tag, after);
            return new SquadOpResult { Index = i, Name = name, SpawnPoint = flat, Squads = after.Squads, Category = CampaignCategory(tag, CampaignCategories.AiSpawnPoints) };
        }

        /// <summary>
        /// The squad's templated cells become its own (designer) cells and its template is cleared: the game then spawns
        /// what these cells say. Returns how many cells were copied.
        /// </summary>
        private static int Detach(TagFile tag, int index)
        {
            string s = "Block:squads[" + index + "]/";
            var designer = tag.SelectField(s + "Struct:designer[0]/Block:cells") as TagFieldBlock;
            var templated = tag.SelectField(s + "Struct:templated[0]/Block:cells") as TagFieldBlock;
            if (designer == null || templated == null) throw new InvalidOperationException("This squad has no cells to copy.");
            if (designer.Elements.Count > 0 && string.IsNullOrEmpty(Text(tag, s + "StringId:template"))) throw new InvalidOperationException("This squad already has its own cells.");
            int copied = 0;
            if (designer.Elements.Count == 0)
            {
                for (int c = 0; c < templated.Elements.Count; c++)
                {
                    byte[] data = templated.SerializeElement(c);
                    designer.AddElement();
                    designer.DeserializeElement(designer.Elements.Count - 1, data);
                    copied++;
                }
                while (templated.Elements.Count > 0) templated.RemoveElement(templated.Elements.Count - 1);
            }
            SetText(tag.SelectField(s + "StringId:template"), "");
            // The resolved template (a hash, e.g. -1577240502 for sq_camp_skirmisher_nr_1) stays set otherwise, and the
            // game then places nothing: a squad without a template has -1 here.
            var squads = (TagFieldBlock)tag.SelectField("Block:squads");
            foreach (TagField f in squads.Elements[index].Fields)
                if (string.Equals(f.FieldName, "squad template index", StringComparison.OrdinalIgnoreCase) && f is TagFieldElementInteger)
                    ((TagFieldElementInteger)f).Data = -1;
            return copied;
        }

        /// <summary>An integer field of any kind (block index, custom search index...), -1 if unreadable.</summary>
        private static int Num(TagFile tag, string path)
        {
            var f = tag.SelectField(path);
            if (f == null) return -1;
            int v = Int(f);
            if (v != -1) return v;
            int parsed;
            return int.TryParse(Safe(() => TagService.ValueOf(f)), out parsed) ? parsed : -1;
        }

        /// <summary>The names mission scripts refer to, by kind.</summary>
        public ScenarioNamesResult Names(string path)
        {
            var sw = Stopwatch.StartNew();
            TagFile tag = _tags.GetOrOpen(path);
            var names = new Dictionary<string, List<string>>();
            void From(string kind, string block, params string[] fields)
            {
                var list = new List<string>();
                for (int i = 0, n = Count(tag, block); i < n; i++)
                {
                    string v = "";
                    foreach (var f in fields) { v = Text(tag, block + "[" + i + "]/" + f); if (v.Length > 0) break; }
                    if (v.Length > 0) list.Add(v);
                }
                if (list.Count > 0) names[kind] = list;
            }
            From("object", "Block:object names", "StringId:name", "String:name");
            From("squad", "Block:squads", "String:name");
            From("squad group", "Block:squad groups", "String:name");
            From("trigger volume", "Block:trigger volumes", "StringId:name", "String:name");
            From("cutscene flag", "Block:cutscene flags", "StringId:name", "String:name");
            From("camera point", "Block:cutscene camera points", "String:name", "StringId:name");
            From("zone set", "Block:zone sets", "StringId:name");
            From("objective", "Block:ai objectives", "StringId:name", "String:name");
            From("device group", "Block:device groups", "String:name");
            From("point set", "Block:scripting data[0]/Block:point sets", "String:name");
            // Points (set/point) and objective tasks (objective/task).
            var points = new List<string>();
            for (int i = 0, n = Count(tag, "Block:scripting data[0]/Block:point sets"); i < n; i++)
            {
                string set = "Block:scripting data[0]/Block:point sets[" + i + "]/";
                string setName = Text(tag, set + "String:name");
                for (int j = 0, m = Count(tag, set + "Block:points"); j < m; j++)
                {
                    string pn = Text(tag, set + "Block:points[" + j + "]/String:name");
                    if (pn.Length > 0) points.Add(setName + "/" + pn);
                }
            }
            if (points.Count > 0) names["point"] = points;
            var tasks = new List<string>();
            for (int i = 0, n = Count(tag, "Block:ai objectives"); i < n; i++)
            {
                string o = "Block:ai objectives[" + i + "]/";
                string on = Text(tag, o + "StringId:name");
                for (int j = 0, m = Count(tag, o + "Block:tasks"); j < m; j++)
                {
                    string tn = Text(tag, o + "Block:tasks[" + j + "]/StringId:name");
                    if (tn.Length == 0) tn = Text(tag, o + "Block:tasks[" + j + "]/String:name");
                    if (tn.Length > 0) tasks.Add(on + "/" + tn);
                }
            }
            if (tasks.Count > 0) names["task"] = tasks;
            return new ScenarioNamesResult { Names = names, Scripts = MissionScripts(tag), Ms = sw.ElapsedMilliseconds };
        }

        private static string FirstText(TagFile tag, string element, params string[] fields)
        {
            foreach (var f in fields) { string v = Text(tag, element + f); if (v.Length > 0) return v; }
            return "";
        }

        private static int Count(TagFile tag, string block)
        {
            var b = tag.SelectField(block) as TagFieldBlock;
            return b == null ? 0 : b.Elements.Count;
        }

        private static List<string> PaletteList(TagFile tag, string block, string field)
        {
            var list = new List<string>();
            for (int i = 0, n = Count(tag, block); i < n; i++) list.Add(Text(tag, block + "[" + i + "]/" + field));
            return list;
        }

        private static List<SquadChoice> Choices(TagFile tag, string block, string indexField)
        {
            var list = new List<SquadChoice>();
            for (int i = 0, n = Count(tag, block); i < n; i++)
            {
                string e = block + "[" + i + "]/";
                list.Add(new SquadChoice
                {
                    Palette = Int(tag.SelectField(e + "ShortBlockIndex:" + indexField)),
                    Path = e + "ShortBlockIndex:" + indexField,
                    Chance = Int(tag.SelectField(e + "ShortInteger:chance")),
                    ChancePath = e + "ShortInteger:chance",
                });
            }
            return list;
        }
    }
}
