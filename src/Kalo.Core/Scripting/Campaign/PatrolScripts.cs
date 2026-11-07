using System.Text.RegularExpressions;

namespace Kalo.Core.Scripting.Campaign;

/// <summary>
/// Campaign missions: a squad that walks a path of script points (a point set) until it sees an enemy. Written into the
/// mission script (Python-style Kalo Script) as Bungie does it: a command script looping cs_go_to over the points
/// (repeat ... until false = sleep_until (begin ... false)), aborted on alert, and a startup script that gives it to the
/// squad once the squad is in the game (placed by ai_place, a volume action...).
/// </summary>
public static class PatrolScripts
{
    public static string Mark(string squad) => $"# Squad {squad}: patrols until it sees an enemy. Written by Kalo (map inspector › Patrol).";

    private static string Id(string squad)
    {
        string s = Regex.Replace(squad.ToLowerInvariant(), "[^a-z0-9_]", "_");
        return s.Length > 18 ? s[..18] : s;
    }

    public static string CommandScript(string squad) => "kalo_patrol_" + Id(squad);

    /// <summary>The code: <paramref name="points"/> are the point names of <paramref name="pointSet"/>, in walking order.</summary>
    public static string Code(string squad, string pointSet, IReadOnlyList<string> points, bool walk)
    {
        string cs = CommandScript(squad);
        var l = new List<string>
        {
            Mark(squad),
            $"command_script {cs}:",
            "    cs_abort_on_alert(true)",
        };
        if (walk) l.Add("    cs_walk(true)");
        l.Add("    repeat:");
        foreach (string p in points) l.Add($"        cs_go_to({pointSet}/{p})");
        l.Add("        until false");
        l.Add($"startup {cs}_go:");
        l.Add($"    sleep_until(ai_living_count({squad}) > 0, 1)");
        l.Add($"    cs_run_command_script({squad}, {cs})");
        return string.Join("\n", l) + "\n";
    }

    /// <summary>The mission script with the patrol added at its end, replacing the one Kalo wrote earlier for this squad.</summary>
    public static string AddTo(string script, string squad, string pointSet, IReadOnlyList<string> points, bool walk) =>
        WrittenBlocks.Put(script, Mark(squad), Code(squad, pointSet, points, walk));

    public static string RemoveFor(string script, string squad) => WrittenBlocks.Remove(script, Mark(squad));
}
