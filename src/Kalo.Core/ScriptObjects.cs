using System.Text.RegularExpressions;

namespace Kalo.Core;

/// <summary>
/// The names gametype scripts use for objects they can create (create_object("name", ...)).
/// Two files hold them, in the same order: the tag multiplayer\globals.multiplayer_object_type_list (the game's list,
/// edited through the Blam Host) and data\multiplayer\megalo\object_lists\objects.txt (what HREK's script compiler
/// reads; one name per line). Entries past HREK's own count were added by the mod.
/// </summary>
public static class ScriptObjects
{
    public const string ListTag = @"multiplayer\globals.multiplayer_object_type_list";
    public const string NamesFile = @"data\multiplayer\megalo\object_lists\objects.txt";

    /// <summary>Object tag kinds that can go in the list, with the map palette category they use.</summary>
    public static readonly IReadOnlyDictionary<string, string> PaletteCategory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".crate"] = "crates", [".scenery"] = "scenery", [".weapon"] = "weapons", [".vehicle"] = "vehicles",
        [".equipment"] = "equipment", [".biped"] = "bipeds", [".device_machine"] = "machines", [".device_control"] = "controls",
        [".device_terminal"] = "terminals", [".giant"] = "giants", [".effect_scenery"] = "effect scenery", [".sound_scenery"] = "sound scenery",
    };

    public static bool IsObjectTag(string tagPath) => PaletteCategory.ContainsKey(Path.GetExtension(tagPath));

    public static List<string> ReadNames(KaloPaths paths) => ReadNames(Path.Combine(paths.Workspace, NamesFile));

    private static List<string> ReadNames(string file) =>
        File.Exists(file) ? File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : new List<string>();

    /// <summary>How many entries HREK itself has: the ones before this index belong to the game, the rest to the mod.</summary>
    public static int VanillaCount(KaloPaths paths) => ReadNames(Path.Combine(paths.Hrek, NamesFile)).Count;

    public static void WriteNames(KaloPaths paths, IEnumerable<string> names) =>
        File.WriteAllText(Path.Combine(paths.Workspace, NamesFile), string.Concat(names.Select(n => n + "\r\n")));

    private static readonly Regex NameRule = new("^[a-z][a-z0-9_]{0,31}$", RegexOptions.Compiled);
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "none", "all", "any", "current_object", "current_player", "global" };

    /// <summary>Null when the name can be used, otherwise why not.</summary>
    public static string? CheckName(string name, IEnumerable<string> existing)
    {
        if (!NameRule.IsMatch(name)) return "Use lowercase letters, digits and _, starting with a letter (32 characters at most).";
        if (Reserved.Contains(name)) return name + " is a reserved word in scripts.";
        if (existing.Contains(name, StringComparer.OrdinalIgnoreCase)) return name + " is already used by another object.";
        return null;
    }

    /// <summary>A name from a tag file name: lowercase, letters/digits/_ only.</summary>
    public static string SuggestName(string tagPath)
    {
        string s = Regex.Replace(Path.GetFileNameWithoutExtension(tagPath).ToLowerInvariant(), "[^a-z0-9_]", "_").Trim('_');
        if (s.Length == 0 || !char.IsLetter(s[0])) s = "obj_" + s;
        return s.Length > 32 ? s[..32] : s;
    }

    /// <summary>Object type names a compiled gametype creates (create_object "name" in the Megalo text).</summary>
    public static HashSet<string> CreatedIn(string megalo)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(megalo, @"\bcreate_object\s+""?([A-Za-z0-9_]+)""?")) set.Add(m.Groups[1].Value);
        return set;
    }
}
