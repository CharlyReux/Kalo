using System.Text;
using System.Text.RegularExpressions;

namespace Kalo.Core.Scripting;

public enum DeclKind { Setting, SettingBlock, Option, Traits, Objects, Constant, Other }

/// <summary>One top-level declaration of a Kalo Script, as the settings panel sees it.</summary>
public sealed class DeclItem
{
    public DeclKind Kind { get; set; }
    public string Name { get; set; } = "";
    /// <summary>"hidden " / "locked " before the declaration.</summary>
    public string Prefix { get; set; } = "";
    /// <summary>1-based lines in the script, inclusive.</summary>
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Setting / constant value, or an option's default.</summary>
    public string Value { get; set; } = "";
    public bool IsRanged { get; set; }
    public string Min { get; set; } = "";
    public string Max { get; set; } = "";
    /// <summary>Option values: (value, label tokens).</summary>
    public List<(string Value, string Label)> Values { get; } = new();
    /// <summary>Traits / setting block / objects rows: key = value.</summary>
    public List<(string Key, string Value)> Rows { get; } = new();
}

/// <summary>
/// Reads and edits the declarations of a Kalo Script (settings, options, traits, map objects, constants) for the
/// Gametype settings panel. Edits replace only the declaration's own lines; everything else stays as written.
/// </summary>
public static class GametypeSettings
{
    private static readonly HashSet<string> Starters = new() { "setting", "option", "traits", "objects", "constant", "hidden", "locked" };

    public static List<DeclItem> Parse(string text)
    {
        // Python style: read through the classic form (same line numbers).
        if (!KaloScriptStyle.IsClassic(text)) text = KaloScriptStyle.ToClassic(text).Classic;
        var errors = new List<ScriptError>();
        var toks = Lexer.Run(text.Replace("\r\n", "\n"), errors);
        var items = new List<DeclItem>();
        int i = 0, depth = 0;
        while (i < toks.Count && toks[i].Kind != T.End)
        {
            var t = toks[i];
            if (depth == 0 && t.Kind == T.Word && Starters.Contains(t.Text) && !(i + 1 < toks.Count && toks[i + 1].Is("(")))
            {
                var item = ParseItem(toks, ref i);
                if (item != null) { items.Add(item); continue; }
            }
            if (depth == 0 && t.Kind == T.Word && Transpiler.DeclarationWords.Contains(t.Text) && !Starters.Contains(t.Text))
            {
                // Other declarations (engine, teams, widgets...): skip their lines.
                int end = OtherEnd(toks, i);
                items.Add(new DeclItem { Kind = DeclKind.Other, Name = t.Text, StartLine = t.Line, EndLine = end });
                while (i < toks.Count && toks[i].Kind != T.End && toks[i].Line <= end) i++;
                continue;
            }
            // Triggers: if / do / inline open, end closes.
            if (t.Is("if") || t.Is("do") || t.Is("function") || (t.Is("inline") && (i + 1 >= toks.Count || toks[i + 1].Line > t.Line))) depth++;
            else if (t.Is("end")) depth = Math.Max(0, depth - 1);
            i++;
        }
        return items;
    }

    private static List<Tok> LineOf(List<Tok> toks, int from)
    {
        int line = toks[from].Line;
        var list = new List<Tok>();
        for (int k = from; k < toks.Count && toks[k].Kind != T.End && toks[k].Line == line; k++) list.Add(toks[k]);
        return list;
    }

    private static string Text(IEnumerable<Tok> toks)
    {
        var parts = new List<string>();
        Tok? prev = null;
        foreach (var t in toks)
        {
            string s = t.Kind == T.String ? "\"" + t.Text + "\"" : t.Text;
            // "-" + number, "a" "." "b": glue
            if (prev != null && ((prev.Kind == T.Op && prev.Text == "-" && t.Kind == T.Number) || prev.Is(".") || t.Is(".")))
                parts[^1] += s;
            else parts.Add(s);
            prev = t;
        }
        return string.Join(" ", parts);
    }

    private static DeclItem? ParseItem(List<Tok> toks, ref int i)
    {
        int start = i;
        var item = new DeclItem { StartLine = toks[i].Line };
        while (toks[i].Is("hidden") || toks[i].Is("locked")) { item.Prefix += toks[i].Text + " "; i++; }
        var kw = toks[i];
        var line = LineOf(toks, i);
        int eq = line.FindIndex(x => x.Is("="));
        string Name() => line.Count > 1 ? line[1].Text : "";
        switch (kw.Text)
        {
            case "setting" when eq == 2:
                item.Kind = DeclKind.Setting;
                item.Name = Name();
                item.Value = Text(line.Skip(3));
                break;
            case "setting" when eq < 0 && line.Count == 2:
                item.Kind = DeclKind.SettingBlock;
                item.Name = Name();
                if (!Rows(toks, ref i, line, item)) { i = start + 1; return null; }
                item.EndLine = toks[i - 1].Line;
                return item;
            case "constant" when eq == 2:
                item.Kind = DeclKind.Constant;
                item.Name = Name();
                item.Value = Text(line.Skip(3));
                break;
            case "objects":
                item.Kind = DeclKind.Objects;
                item.Name = Name();
                if (line.Count > 2 && line[2].Is("with"))
                {
                    var rest = line.Skip(3).ToList();
                    for (int k = 0; k + 1 < rest.Count; k += 2) item.Rows.Add((rest[k].Text, Text(new[] { rest[k + 1] })));
                    break;
                }
                if (!Rows(toks, ref i, line, item)) { i = start + 1; return null; }
                item.EndLine = toks[i - 1].Line;
                return item;
            case "traits" when line.Count >= 4:
                item.Kind = DeclKind.Traits;
                item.Name = Name();
                item.Title = Text(new[] { line[2] });
                item.Description = Text(new[] { line[3] });
                if (!Rows(toks, ref i, line, item)) { i = start + 1; return null; }
                item.EndLine = toks[i - 1].Line;
                return item;
            case "option" when eq >= 4:
            {
                item.Kind = DeclKind.Option;
                item.Name = Name();
                item.Title = Text(new[] { line[2] });
                item.Description = Text(new[] { line[3] });
                var after = line.Skip(eq + 1).ToList();
                int r = after.FindIndex(x => x.Is("range"));
                if (r >= 0)
                {
                    item.IsRanged = true;
                    item.Value = Text(after.Take(r));
                    var mm = SplitValues(after.Skip(r + 1).ToList());
                    if (mm.Count != 2) { i = start + 1; return null; }
                    item.Min = mm[0];
                    item.Max = mm[1];
                    break;
                }
                item.Value = Text(after);
                i += line.Count;
                while (i < toks.Count && toks[i].Is("value"))
                {
                    var v = LineOf(toks, i);
                    // The Python style read as classic puts the closing end on the last row ("value 2 two end").
                    bool closes = v.Count > 2 && v[^1].Is("end");
                    var vals = SplitValues(v.Skip(1).Take(v.Count - (closes ? 2 : 1)).ToList());
                    item.Values.Add((vals.FirstOrDefault() ?? "", string.Join(" ", vals.Skip(1))));
                    i += v.Count;
                    if (closes) { item.EndLine = v[0].Line; return item; }
                }
                if (i >= toks.Count || !toks[i].Is("end")) { i = start + 1; return null; }
                item.EndLine = toks[i].Line;
                i++;
                return item;
            }
            default:
                // option NAME = VALUE, or anything else the panel doesn't edit.
                item.Kind = DeclKind.Other;
                item.Name = kw.Text + " " + Name();
                if (eq < 0 && !(kw.Is("option") || kw.Is("setting")))
                {
                    i = start + 1;
                    return null;
                }
                break;
        }
        item.EndLine = line[^1].Line;
        i += line.Count;
        return item;
    }

    /// <summary>Values of a line, with "-" joined to the number after it.</summary>
    private static List<string> SplitValues(List<Tok> toks)
    {
        var list = new List<string>();
        for (int k = 0; k < toks.Count; k++)
        {
            if (toks[k].Kind == T.Op && toks[k].Text == "-" && k + 1 < toks.Count && toks[k + 1].Kind == T.Number) { list.Add("-" + toks[k + 1].Text); k++; continue; }
            list.Add(toks[k].Kind == T.String ? "\"" + toks[k].Text + "\"" : toks[k].Text);
        }
        return list;
    }

    /// <summary>"key = value" lines after a header line, up to end (consumed).</summary>
    private static bool Rows(List<Tok> toks, ref int i, List<Tok> header, DeclItem item)
    {
        i += header.Count;
        while (i < toks.Count && toks[i].Kind != T.End && !toks[i].Is("end"))
        {
            var row = LineOf(toks, i);
            // The Python style read as classic puts the closing end on the last row ("speed = 150 end").
            bool closes = row.Count > 3 && row[^1].Is("end");
            if (row.Count < 3 || !row[1].Is("=") || row[0].Kind != T.Word) return false;
            item.Rows.Add((row[0].Text, Text(row.Skip(2).Take(row.Count - (closes ? 3 : 2)))));
            i += row.Count;
            if (closes) return true;
        }
        if (i >= toks.Count || !toks[i].Is("end")) return false;
        i++;
        return true;
    }

    /// <summary>Last line of another declaration (engine, teams, widget...).</summary>
    private static int OtherEnd(List<Tok> toks, int i)
    {
        var first = LineOf(toks, i);
        if (first.Any(x => x.Is("=")) || first[0].Text is "widget" or "stat" or "base" or "localized_include" or "constant") return first[^1].Line;
        int depth = 1, k = i + first.Count;
        while (k < toks.Count && toks[k].Kind != T.End)
        {
            var l = LineOf(toks, k);
            if (l[0].Is("end")) depth--;
            else if (!l.Any(x => x.Is("="))) depth++;
            if (depth == 0) return l[0].Line;
            k += l.Count;
        }
        return first[^1].Line;
    }

    // ---- writing ------------------------------------------------------------------------------------------

    /// <summary>The Kalo Script lines of a declaration.</summary>
    /// <summary>A declaration written in the style of the script it goes into.</summary>
    public static string RenderFor(string script, DeclItem d)
    {
        string classic = Render(d);
        if (KaloScriptStyle.IsClassic(script)) return classic;
        return KaloScriptStyle.FromClassic(classic) ?? classic;
    }

    public static string Render(DeclItem d)
    {
        var sb = new StringBuilder();
        switch (d.Kind)
        {
            case DeclKind.Setting:
                sb.Append($"{d.Prefix}setting {d.Name} = {d.Value}");
                break;
            case DeclKind.Constant:
                sb.Append($"constant {d.Name} = {d.Value}");
                break;
            case DeclKind.SettingBlock:
                sb.Append($"{d.Prefix}setting {d.Name}\n");
                foreach (var (k, v) in d.Rows) sb.Append($"  {k} = {v}\n");
                sb.Append("end");
                break;
            case DeclKind.Traits:
                sb.Append($"{d.Prefix}traits {d.Name} {Quote(d.Title)} {Quote(d.Description)}\n");
                foreach (var (k, v) in d.Rows) sb.Append($"  {k} = {v}\n");
                sb.Append("end");
                break;
            case DeclKind.Objects:
                if (d.Rows.Count is >= 1 and <= 3)
                    sb.Append($"objects {d.Name} with {string.Join(" ", d.Rows.Select(r => r.Key + " " + r.Value))}");
                else
                {
                    sb.Append($"objects {d.Name}\n");
                    foreach (var (k, v) in d.Rows) sb.Append($"  {k} = {v}\n");
                    sb.Append("end");
                }
                break;
            case DeclKind.Option:
                sb.Append($"{d.Prefix}option {d.Name} {Quote(d.Title)} {Quote(d.Description)} = {d.Value}");
                if (d.IsRanged) sb.Append($" range {d.Min} {d.Max}");
                else
                {
                    sb.Append('\n');
                    foreach (var (v, l) in d.Values) sb.Append($"  value {v}{(l.Length > 0 ? " " + l : "")}\n");
                    sb.Append("end");
                }
                break;
            default:
                throw new InvalidOperationException("This declaration is edited in the code.");
        }
        return sb.ToString();
    }

    /// <summary>Text as a Megalo title: a string id stays as it is, anything else goes in quotes.</summary>
    public static string Quote(string title)
    {
        if (title.StartsWith('"') && title.EndsWith('"') && title.Length >= 2) return title;
        if (Regex.IsMatch(title, @"^[A-Za-z_][A-Za-z0-9_]*$") && title.Contains('_')) return title;
        return "\"" + title.Replace("\"", "'") + "\"";
    }

    /// <summary>A title or description for display: quotes removed.</summary>
    public static string Unquote(string token) => token.Length >= 2 && token[0] == '"' && token[^1] == '"' ? token[1..^1] : token;

    /// <summary>Replaces a declaration's lines (or removes them with null).</summary>
    public static string Replace(string text, DeclItem old, string? kalo)
    {
        var lines = Lines(text);
        int a = Math.Clamp(old.StartLine - 1, 0, lines.Count), b = Math.Clamp(old.EndLine, a, lines.Count);
        lines.RemoveRange(a, b - a);
        if (kalo != null) lines.InsertRange(a, kalo.Split('\n'));
        return string.Join(Newline(text), lines);
    }

    /// <summary>Adds a declaration after the last one of the same kind, else before the first trigger, else at the end.</summary>
    public static string Insert(string text, List<DeclItem> items, DeclKind kind, string kalo)
    {
        var lines = Lines(text);
        bool Same(DeclKind k) => k == kind || (kind is DeclKind.Setting or DeclKind.SettingBlock && k is DeclKind.Setting or DeclKind.SettingBlock);
        var last = items.LastOrDefault(i => Same(i.Kind)) ?? items.LastOrDefault(i => i.Kind != DeclKind.Other) ?? items.LastOrDefault();
        int at;
        if (last != null) at = Math.Min(last.EndLine, lines.Count);
        else
        {
            // Before the first trigger (a line starting with for / on / do), after includes and comments at the top.
            at = lines.FindIndex(l => Regex.IsMatch(l.TrimStart(), @"^(for\s|on\s|do\b|declare\b|every\s+tick\b|def\s|(global|player|team|object)\.\w+\s*:)"));
            if (at < 0) at = lines.Count;
        }
        var add = kalo.Split('\n').ToList();
        if (last == null) add.Add("");
        lines.InsertRange(at, add);
        return string.Join(Newline(text), lines);
    }

    private static List<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n').ToList();
    private static string Newline(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    // ---- what the panel offers ---------------------------------------------------------------------------

    public sealed record Known(string Name, string Label, string Help, string[] Values);

    /// <summary>Base settings (Megalo overrides) shown in the panel, with the values Bungie's gametypes use.</summary>
    public static readonly Known[] Settings =
    {
        new("score_to_win_round", "Score to win", "Points to win a round (0 = no limit).", new[] { "0", "10", "15", "25", "50", "100", "150", "300", "500" }),
        new("round_time_limit", "Round time limit", "Minutes per round (0 = no limit).", new[] { "0", "3", "5", "10", "12", "15", "20" }),
        new("round_count", "Number of rounds", "Rounds in a game.", new[] { "1", "2", "3", "4", "5" }),
        new("early_victory_win_count", "Early victory", "Rounds a team must win to end the game early (0 = off).", new[] { "0", "2", "3" }),
        new("teams_enabled", "Teams", "1 for team games, 0 for free for all.", new[] { "0", "1" }),
        new("fire_teams_enabled", "Fireteams", "Spawn with fireteam mates (Invasion style).", new[] { "0", "1" }),
        new("lives_per_round", "Lives per round", "0 = unlimited.", new[] { "0", "1", "3", "5" }),
        new("respawn_time", "Respawn time", "Seconds before respawning.", new[] { "3", "5", "10" }),
        new("suicide_respawn_penalty", "Suicide penalty", "Extra seconds before respawning after a suicide.", new[] { "0", "3", "5" }),
        new("friendly_fire_enabled", "Friendly fire", "1 = teammates can hurt each other.", new[] { "0", "1" }),
        new("betrayal_booting_enabled", "Betrayal booting", "Kick players who betray too often.", new[] { "0", "1" }),
        new("weapon_set", "Weapons on map", "Which weapons the map's weapon spawns use.", new[] { "none", "no_weapons", "slayer_pro", "covenant", "juggernaut" }),
        new("vehicle_set", "Vehicles on map", "Which vehicles the map spawns.", new[] { "none", "no_vehicles", "mongoose_only" }),
        new("grenades_on_map", "Grenades on map", "1 = grenades lying on the map spawn.", new[] { "0", "1" }),
        new("indestructible_vehicles", "Indestructible vehicles", "1 = vehicles can't be destroyed.", new[] { "0", "1" }),
        new("powerups_on_map", "Powerups on map", "1 = powerups spawn.", new[] { "0", "1" }),
        new("grace_period_time_limit", "Grace period", "Seconds at the start when players can still join.", new[] { "0", "5" }),
        new("sudden_death_time_limit", "Sudden death", "Seconds of overtime while an objective is contested.", new[] { "0", "30" }),
        new("loadout_selection_time", "Loadout choice time", "Seconds to pick a loadout at spawn.", new[] { "0", "7", "10" }),
        new("perfection_enabled", "Perfection medal", "Award the Perfection medal.", new[] { "false", "true" }),
        new("team_scoring_mode", "Team scoring", "How a team's score is counted.", new[] { "sum", "minimum", "maximum" }),
    };

    /// <summary>Player trait keys, with the values Bungie's gametypes use.</summary>
    public static readonly Known[] TraitKeys =
    {
        new("speed", "Movement speed", "Percent (100 = normal).", new[] { "25", "50", "75", "90", "100", "110", "125", "150", "200", "300" }),
        new("jump_modifier", "Jump height", "Percent (100 = normal, 0 = no jump).", new[] { "0", "50", "75", "100", "120", "150" }),
        new("gravity", "Gravity", "Percent (100 = normal).", new[] { "50", "100", "150", "200", "300" }),
        new("damage_resistance", "Damage resistance", "Percent, or invulnerable.", new[] { "10", "50", "90", "100", "110", "150", "200", "300", "invulnerable" }),
        new("damage_modifier", "Damage dealt", "Percent of damage the player deals.", new[] { "0", "25", "50", "90", "100", "110", "125", "150", "200", "300" }),
        new("melee_damage_modifier", "Melee damage", "Percent.", new[] { "0", "50", "100", "200", "300" }),
        new("shield_multiplier", "Shields", "Percent of normal shields (0 = none).", new[] { "0", "50", "100", "150", "200", "300", "400" }),
        new("shield_recharge", "Shield recharge", "Percent speed (0 = no recharge).", new[] { "0", "25", "50", "90", "100", "110", "200" }),
        new("body_recharge", "Health recharge", "Percent speed.", new[] { "0", "50", "100", "110", "200" }),
        new("initial_primary_weapon", "Primary weapon", "Weapon at spawn.", new[] { "assault_rifle", "dmr", "magnum", "shotgun", "sniper_rifle", "rocket_launcher", "energy_sword", "gravity_hammer", "plasma_pistol", "plasma_repeater", "golf_club", "random", "none" }),
        new("initial_secondary_weapon", "Secondary weapon", "Second weapon at spawn.", new[] { "magnum", "assault_rifle", "plasma_pistol", "energy_sword", "random", "none" }),
        new("initial_grenades", "Grenades", "Grenades at spawn.", new[] { "none", "1", "2", "1 frag", "2 frag", "2 plasma" }),
        new("initial_equipment", "Armor ability", "Armor ability at spawn.", new[] { "sprint_equipment", "jet_pack_equipment", "evade_equipment", "armor_lock_equipment", "hologram_equipment", "active_camo_equipment", "none" }),
        new("infinite_ammo", "Infinite ammo", "1 / true = no reloading needed for reserve.", new[] { "0", "1" }),
        new("bottomless_clip", "Bottomless clip", "Never reload.", new[] { "false", "true" }),
        new("weapon_pickup", "Can pick up weapons", "0 / false = can't.", new[] { "0", "1" }),
        new("equipment_usage", "Armor ability use", "off = can't use it.", new[] { "on", "off" }),
        new("infinite_equipment", "Infinite armor ability", "", new[] { "false", "true" }),
        new("vehicle_usage", "Vehicle use", "", new[] { "none", "passenger", "not_passenger", "full" }),
        new("active_camo", "Active camo", "", new[] { "off", "poor", "good", "invisible" }),
        new("waypoint", "Waypoint", "Who sees the marker over the player.", new[] { "off", "allies", "all", "unchanged" }),
        new("gamertag_visibility", "Name visible", "", new[] { "off", "allies", "all" }),
        new("tracker_mode", "Motion tracker", "", new[] { "off", "allies", "normal", "enhanced" }),
        new("tracker_range", "Tracker range", "Meters.", new[] { "10", "15", "25", "50", "75" }),
        new("color", "Player color", "", new[] { "off", "red", "blue", "green", "orange", "purple", "gold", "brown", "pink", "white", "black", "zombie" }),
        new("assassination_immunity", "Assassination immunity", "", new[] { "false", "true" }),
        new("headshot_immunity", "Headshot immunity", "", new[] { "false", "true" }),
        new("drop_equipment", "Drops armor ability", "", new[] { "false", "true" }),
    };
}
