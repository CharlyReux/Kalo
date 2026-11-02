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
            };
            int team = SafeValue(() => Convert.ToInt32(((TagFieldEnum)tag.SelectField(s + "ShortEnum:team")).Value), -1);
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
