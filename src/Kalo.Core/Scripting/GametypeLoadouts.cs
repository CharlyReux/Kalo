using System.Text;
using System.Text.RegularExpressions;

namespace Kalo.Core.Scripting;

/// <summary>One loadout players can pick at spawn: Megalo's loadout block.</summary>
public sealed record LoadoutDef(string Id, string Name, string Primary, string Secondary, string Equipment, string Grenades);

/// <summary>Which loadouts Spartans and Elites pick from, and the loadouts the gametype defines itself.</summary>
public sealed class LoadoutsState
{
    /// <summary>Palette for Spartans / Elites (tier 1, the one every mode but Invasion uses); null = not set by the script.</summary>
    public string? Spartan { get; set; }
    public string? Elite { get; set; }
    /// <summary>Loadouts written by Kalo's Loadouts form (kalo_loadout_N), in the palette kalo_loadouts.</summary>
    public List<LoadoutDef> Custom { get; } = new();
    /// <summary>The script includes Bungie's loadout list (empty_palette, slayer_loadouts...).</summary>
    public bool HasInclude { get; set; }
    /// <summary>The script's own include line that brings Bungie's list (directly or through other includes), or null.</summary>
    public string? IncludeVia { get; set; }
}

/// <summary>
/// The Loadouts part of the gametype Settings panel. Reads and writes:
/// <code>
/// loadout kalo_loadout_1          -- the form's own loadouts (near the top: Megalo reads them before the settings)
///   name = loadout_name_ranger
///   primary_weapon = dmr
///   backpack_weapon = magnum
///   equipment = sprint_equipment
///   grenades = 2 frag
/// end
/// loadout_palette kalo_loadouts
///   item = kalo_loadout_1
/// end
/// setting loadout_palette = spartan_tier1 kalo_loadouts
/// </code>
/// Everything else in the script stays as written. Bungie's palettes come from includes/multiplayer_loadouts.txt,
/// included when one of them is picked. That list fills all 32 loadout slots, so it can't be mixed with the form's own.
/// Checked with HREK's compiler (kalo-cli loadouts-apply, then kscript-compile).
/// </summary>
public static class GametypeLoadouts
{
    public const string MyPalette = "kalo_loadouts";
    public const string NoPalette = "kalo_no_loadouts";
    public const string Include = "includes/multiplayer_loadouts.txt";
    private static readonly HashSet<string> Invasion = new() { "unsc_bronze", "unsc_silver", "unsc_gold", "covy_bronze", "covy_silver", "covy_gold" };
    private static readonly string[] Tiers = { "spartan_tier1", "spartan_tier2", "spartan_tier3", "elite_tier1", "elite_tier2", "elite_tier3" };

    private static readonly Regex PaletteSetting = new(@"^\s*(locked\s+|hidden\s+)*setting\s+loadout_palette\s*=\s*(\w+)\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex MyBlock = new(@"^(loadout\s+kalo_loadout_\d+|loadout_palette\s+kalo_(no_)?loadouts)\b", RegexOptions.Compiled);
    private static readonly Regex IncludeLine = new(@"^\s*include\s+""includes/multiplayer_loadouts[^""]*\.txt""", RegexOptions.Compiled);
    private static readonly Regex KaloComment = new(@"^\s*(--|#).*Settings › Loadouts", RegexOptions.Compiled);

    /// <summary>Palettes offered for Spartans: (value, label). null value = leave it to the game (no line).</summary>
    public static readonly (string? Value, string Label)[] SpartanPalettes =
    {
        (null, "Game default (not set)"),
        ("empty_palette", "None: spawn with the Player traits' weapons"),
        (MyPalette, "My loadouts (below)"),
        ("slayer_loadouts", "Slayer loadouts (Bungie)"),
        ("slayer_pro_loadouts", "Slayer Pro loadouts"),
        ("swat_loadouts", "SWAT loadouts"),
        ("objective_loadouts", "Objective loadouts"),
        ("unsc_bronze", "Invasion bronze"), ("unsc_silver", "Invasion silver"), ("unsc_gold", "Invasion gold"),
    };

    public static readonly (string? Value, string Label)[] ElitePalettes =
    {
        (null, "Game default (not set)"),
        ("empty_palette", "None: spawn with the Player traits' weapons"),
        (MyPalette, "My loadouts (below)"),
        ("covy_slayer_loadouts", "Elite Slayer loadouts (Bungie)"),
        ("slayer_loadouts", "Same as Spartans' Slayer loadouts"),
        ("covy_bronze", "Invasion bronze"), ("covy_silver", "Invasion silver"), ("covy_gold", "Invasion gold"),
    };

    /// <summary>Names a loadout can show in the menu (the game's own strings).</summary>
    public static readonly string[] Names =
    {
        "loadout_name_scout", "loadout_name_guard", "loadout_name_air_assault", "loadout_name_marksman", "loadout_name_recon_marksman",
        "loadout_name_expert_marksman", "loadout_name_operator", "loadout_name_grenadier", "loadout_name_medic", "loadout_name_spec_ops",
        "loadout_name_warrior", "loadout_name_assassin", "loadout_name_deceiver", "loadout_name_zealot", "loadout_name_dark_assassin",
        "loadout_name_infiltrator", "loadout_name_ranger", "loadout_name_security", "loadout_name_juggernaut", "loadout_name_power",
        "loadout_name_sentry", "loadout_name_stalker", "loadout_name_ninja", "loadout_name_demolitions", "loadout_name_berserker",
        "loadout_name_champion_assassin", "loadout_name_royal_zealot", "loadout_name_warden", "loadout_name_zombie",
    };

    public static readonly string[] Weapons =
    {
        "assault_rifle", "dmr", "magnum", "shotgun", "sniper_rifle", "rocket_launcher", "spartan_laser", "grenade_launcher",
        "target_locator", "machine_gun_turret", "energy_sword", "gravity_hammer", "golf_club", "plasma_pistol", "plasma_repeater",
        "plasma_rifle", "needler", "needle_rifle", "spiker", "focus_rifle", "concussion_rifle", "fuel_rod_gun", "plasma_launcher",
        "plasma_turret", "random", "none",
    };

    public static readonly string[] Equipment =
    {
        "sprint_equipment", "jet_pack_equipment", "evade_equipment", "armor_lock_equipment", "hologram_equipment",
        "active_camo_equipment", "drop_shield_equipment", "none",
    };

    public static readonly string[] Grenades = { "2 frag", "2 plasma", "1 frag", "1 plasma", "0 frag" };

    /// <summary>A palette from Bungie's list (includes/multiplayer_loadouts.txt) is chosen.</summary>
    public static bool BungieChosen(LoadoutsState s) =>
        new[] { s.Spartan, s.Elite }.Any(p => p != null && p != MyPalette && p != NoPalette && p != "empty_palette");

    /// <summary>Why these choices can't be written, or null. A gametype holds at most 32 loadouts and Bungie's list has 32.</summary>
    public static string? Problem(LoadoutsState s)
    {
        bool mine = s.Spartan == MyPalette || s.Elite == MyPalette;
        if (mine && s.Custom.Count == 0) return "Add at least one loadout below, or pick another choice.";
        if (mine && (BungieChosen(s) || s.HasInclude))
            return s.HasInclude && !BungieChosen(s)
                ? "This gametype includes Bungie's loadout list (through include \"" + (s.IncludeVia ?? Include) + "\"), which fills all 32 loadout slots. Remove that include line from the script to use your own loadouts, or pick Bungie's loadouts."
                : "Bungie's loadouts fill all 32 loadout slots of a gametype: use My loadouts for both Spartans and Elites, or Bungie's for both.";
        if (s.Custom.Count > 8) return "At most 8 loadouts of your own.";
        return null;
    }

    public static LoadoutDef NewLoadout(int n) =>
        new($"kalo_loadout_{n}", Names[Math.Min(n - 1, Names.Length - 1)], "assault_rifle", "magnum", "sprint_equipment", "2 frag");

    /// <param name="megaloFolder">datamultiplayermegalo: included files are read to see whether they bring Bungie's list.</param>
    public static LoadoutsState Parse(string text, string? megaloFolder = null)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var s = new LoadoutsState();
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i];
            if (IncludeLine.IsMatch(l)) { s.HasInclude = true; s.IncludeVia ??= Include; }
            else if (megaloFolder != null && !s.HasInclude && Regex.Match(l, @"^\s*include\s+""([^""]+)""") is { Success: true } inc && BringsList(megaloFolder, inc.Groups[1].Value, 0))
            { s.HasInclude = true; s.IncludeVia = inc.Groups[1].Value; }
            var m = PaletteSetting.Match(l);
            if (m.Success)
            {
                if (m.Groups[2].Value == "spartan_tier1") s.Spartan = m.Groups[3].Value;
                else if (m.Groups[2].Value == "elite_tier1") s.Elite = m.Groups[3].Value;
                continue;
            }
            var lm = Regex.Match(l, @"^loadout\s+(kalo_loadout_\d+)\b");
            if (!lm.Success) continue;
            var rows = new Dictionary<string, string>();
            int end = BlockEnd(lines, i);
            for (int k = i + 1; k <= end; k++)
            {
                var r = Regex.Match(lines[k], @"^\s+(\w+)\s*=\s*(.*?)\s*((--|#).*)?$");
                if (r.Success) rows[r.Groups[1].Value] = r.Groups[2].Value;
            }
            i = end;
            string Get(string k, string d) => rows.TryGetValue(k, out var v) && v.Length > 0 ? v : d;
            s.Custom.Add(new LoadoutDef(lm.Groups[1].Value, Get("name", Names[0]), Get("primary_weapon", "none"), Get("backpack_weapon", "none"),
                Get("equipment", "none"), Get("grenades", "0 frag")));
        }
        // "None" written without Bungie's list reads back as None.
        if (s.Spartan == NoPalette) s.Spartan = "empty_palette";
        if (s.Elite == NoPalette) s.Elite = "empty_palette";
        return s;
    }

    /// <summary>An included file brings Bungie's loadout list, itself or through its own includes.</summary>
    private static bool BringsList(string folder, string include, int depth)
    {
        if (depth > 4 || include.Contains("..")) return false;
        if (Regex.IsMatch(include, @"^includes/multiplayer_loadouts[^/]*\.txt$", RegexOptions.IgnoreCase)) return true;
        string file = Path.Combine(folder, include.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(file)) return false;
        string text;
        try { text = File.ReadAllText(file); } catch { return false; }
        foreach (Match m in Regex.Matches(text, @"^\s*include\s+""([^""]+)""", RegexOptions.Multiline))
            if (BringsList(folder, m.Groups[1].Value, depth + 1)) return true;
        return Regex.IsMatch(text, @"^\s*loadout_palette\s+empty_palette\b", RegexOptions.Multiline);
    }

    /// <summary>
    /// Last line of the top-level block starting at <paramref name="start"/>: its indented rows, and the classic style's
    /// closing end (the Python style has none).
    /// </summary>
    private static int BlockEnd(IReadOnlyList<string> lines, int start)
    {
        int i = start + 1;
        for (; i < lines.Count; i++)
        {
            string l = lines[i];
            if (Regex.IsMatch(l, @"^end\b")) return i;
            if (l.Trim().Length == 0 || !char.IsWhiteSpace(l[0])) break;
        }
        return i - 1;
    }

    /// <summary>The script with the loadout lines replaced by <paramref name="s"/>; everything else unchanged.</summary>
    public static string Apply(string text, LoadoutsState s)
    {
        bool classicStyle = KaloScriptStyle.IsClassic(text);
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

        // Take out what the form wrote before: its comments, the palette settings and its own blocks.
        int settingsAt = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (PaletteSetting.IsMatch(lines[i]))
            {
                if (settingsAt < 0) settingsAt = i;
                lines.RemoveAt(i--);
                continue;
            }
            if (KaloComment.IsMatch(lines[i]))
            {
                bool defsComment = lines[i].Contains("of this gametype");
                if (!defsComment && settingsAt < 0) settingsAt = i;
                lines.RemoveAt(i--);
                continue;
            }
            if (!MyBlock.IsMatch(lines[i])) continue;
            int end = BlockEnd(lines, i);
            lines.RemoveRange(i, end - i + 1);
            // The blank line Kalo put after its definitions goes with them.
            if (i < lines.Count && lines[i].Trim().Length == 0 && (i == 0 || lines[i - 1].Trim().Length == 0)) lines.RemoveAt(i);
            i--;
        }

        if (s.Spartan == null && s.Elite == null) return string.Join(nl, lines);

        bool usesMine = s.Spartan == MyPalette || s.Elite == MyPalette;
        bool bungie = BungieChosen(s);
        // "None" without Bungie's list: an empty palette of our own (Bungie's list fills every loadout slot).
        string Resolve(string p) => p == "empty_palette" && !bungie && !s.HasInclude ? NoPalette : p;
        string Style(string classic) => classicStyle ? classic : KaloScriptStyle.FromClassic(classic) ?? classic;

        var settings = new StringBuilder("-- Loadouts players pick at spawn (Settings › Loadouts)\n");
        foreach (string tier in Tiers)
        {
            string? palette = tier.StartsWith("spartan") ? s.Spartan : s.Elite;
            if (palette == null) continue;
            // Tiers 2 and 3 are Invasion's later phases: only Invasion palettes set them.
            if (!tier.EndsWith("1") && !Invasion.Contains(palette)) continue;
            settings.Append($"setting loadout_palette = {tier} {Resolve(palette)}\n");
        }
        int at = settingsAt >= 0 ? Math.Min(settingsAt, lines.Count) : AfterSettings(lines);
        var settingLines = Style(settings.ToString().TrimEnd('\n')).Split('\n').ToList();
        if (settingsAt < 0 && at > 0 && lines[at - 1].Trim().Length > 0) settingLines.Insert(0, "");
        lines.InsertRange(at, settingLines);

        if (bungie && !s.HasInclude) lines.Insert(IncludeAt(lines), $"include \"{Include}\"");

        // Definitions near the top: Megalo reads palettes before the settings that name them.
        var defs = new StringBuilder();
        if (usesMine)
        {
            foreach (var d in s.Custom)
                defs.Append($"loadout {d.Id}\n  name = {d.Name}\n  primary_weapon = {d.Primary}\n  backpack_weapon = {d.Secondary}\n" +
                            $"  equipment = {d.Equipment}\n  grenades = {d.Grenades}\nend\n");
            defs.Append($"loadout_palette {MyPalette}\n");
            foreach (var d in s.Custom) defs.Append($"  item = {d.Id}\n");
            defs.Append("end\n");
        }
        if (new[] { s.Spartan, s.Elite }.Any(p => p != null && Resolve(p) == NoPalette)) defs.Append($"loadout_palette {NoPalette}\nend\n");
        if (defs.Length > 0)
        {
            var defLines = Style("-- Loadouts of this gametype (Settings › Loadouts)\n" + defs.ToString().TrimEnd('\n')).Split('\n').ToList();
            int top = IncludeAt(lines);
            if (top >= lines.Count || lines[top].Trim().Length > 0) defLines.Add("");
            lines.InsertRange(top, defLines);
        }
        return string.Join(nl, lines);
    }

    /// <summary>After the last top-level setting / option / traits / objects / constant declaration, or under the header.</summary>
    private static int AfterSettings(List<string> lines)
    {
        int last = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string l = lines[i];
            if (Regex.IsMatch(l, @"^(locked\s+|hidden\s+)*(setting|option|traits|objects|constant)\b"))
            {
                last = Regex.IsMatch(l, @"^(setting\s+\w+\s*:?\s*$|traits\b)") ? BlockEnd(lines, i) : i;
                i = last;
            }
            else if (Regex.IsMatch(l, @"^(for each|on |do\b|function |def )")) break;
        }
        return last < 0 ? IncludeAt(lines) : last + 1;
    }

    /// <summary>After the header comments (title...) and the includes.</summary>
    private static int IncludeAt(List<string> lines)
    {
        int last = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].TrimStart();
            if (t.Length == 0) continue;
            bool header = t.StartsWith("include ") || (t.StartsWith("--") || t.StartsWith("#")) &&
                          (t.Contains("title:") || t.Contains("Made with Kalo") || t.Contains("converted from Megalo"));
            if (header) last = i;
            else if (t.StartsWith("--") || t.StartsWith("#")) continue;
            else break;
        }
        return last + 1;
    }
}
