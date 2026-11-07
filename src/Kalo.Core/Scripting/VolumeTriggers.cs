using System.Text.RegularExpressions;

namespace Kalo.Core.Scripting;

/// <summary>What happens when the player enters a trigger volume of a campaign mission (map inspector › When the player enters).</summary>
public enum VolumeAction { SpawnSquad, Checkpoint, TeleportToFlag, Kill, Win }

/// <summary>
/// Campaign missions: the mission script code for "when the player enters this trigger volume, do X", in the mission
/// script's Kalo Script (Python style, saved as HaloScript). Once-only actions are a startup script that waits for the
/// player (sleep_until volume_test_players); repeating ones (kill, teleport) a continuous script. The multiplayer
/// counterpart (gametype code for object zones) is ZoneTriggers.
/// </summary>
public static class VolumeTriggers
{
    /// <summary>(action, label, what the box under it holds: "" none, "squad", "flag").</summary>
    public static readonly (VolumeAction Action, string Label, string Param)[] Choices =
    {
        (VolumeAction.SpawnSquad, "Spawn a squad (once)", "squad"),
        (VolumeAction.Checkpoint, "Checkpoint: save the game (once)", ""),
        (VolumeAction.TeleportToFlag, "Teleport the player to a flag", "flag"),
        (VolumeAction.Kill, "Kill the player (kill zone)", ""),
        (VolumeAction.Win, "Win the mission", ""),
    };

    public static string Label(VolumeAction a) => Choices.First(c => c.Action == a).Label;

    private static string Short(VolumeAction a) => a switch
    {
        VolumeAction.SpawnSquad => "ai", VolumeAction.Checkpoint => "save", VolumeAction.TeleportToFlag => "tp",
        VolumeAction.Kill => "kill", _ => "win",
    };

    /// <summary>HaloScript script names: letters, digits, _; at most 31 characters.</summary>
    private static string ScriptName(string volume, VolumeAction a)
    {
        string v = Regex.Replace(volume.ToLowerInvariant(), "[^a-z0-9_]", "_");
        string name = $"kalo_{Short(a)}_{v}";
        return name.Length <= 31 ? name : name[..31];
    }

    /// <summary>The block's first line, also how Kalo finds it again to replace it.</summary>
    public static string Mark(string volume, VolumeAction a) => $"# Volume {volume}: when the player enters, {Label(a).ToLowerInvariant()}. Written by Kalo (map inspector).";

    /// <summary>The mission script code (Python-style Kalo Script).</summary>
    public static string Code(string volume, VolumeAction action, string param)
    {
        string name = ScriptName(volume, action);
        bool repeats = action is VolumeAction.Kill or VolumeAction.TeleportToFlag;
        var lines = new List<string> { Mark(volume, action), $"{(repeats ? "continuous" : "startup")} {name}:" };
        lines.Add($"    sleep_until(volume_test_players({volume}), 1)");
        switch (action)
        {
            case VolumeAction.SpawnSquad: lines.Add($"    ai_place({param})"); break;
            case VolumeAction.Checkpoint: lines.Add("    game_save()"); break;
            case VolumeAction.TeleportToFlag: lines.Add($"    object_teleport(player_get(0), {param})"); break;
            case VolumeAction.Kill: lines.Add("    unit_kill(player_get(0))"); break;
            case VolumeAction.Win: lines.Add("    game_won()"); break;
        }
        // A repeating script waits for the player to leave (or die) before it can act again.
        if (action == VolumeAction.Kill) lines.Add($"    sleep_until(not volume_test_players({volume}), 1)");
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>The mission script with the block added at its end, replacing the one Kalo wrote earlier for this volume and action.</summary>
    public static string AddTo(string script, string volume, VolumeAction action, string param)
    {
        string nl = script.Contains("\r\n") ? "\r\n" : "\n";
        string text = RemoveFor(script.Replace("\r\n", "\n"), volume, action).TrimEnd('\n');
        return (text + "\n\n" + Code(volume, action, param)).Replace("\n", nl);
    }

    /// <summary>Removes Kalo's block for this volume and action: its mark line and the indented script under it.</summary>
    public static string RemoveFor(string script, string volume, VolumeAction action)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n').ToList();
        int start = lines.FindIndex(l => l.Trim() == Mark(volume, action));
        if (start < 0) return script;
        int end = start + 2; // the mark, the script line, then its indented body
        while (end < lines.Count && lines[end].Length > 0 && char.IsWhiteSpace(lines[end][0])) end++;
        lines.RemoveRange(start, Math.Min(end, lines.Count) - start);
        if (start < lines.Count && start > 0 && lines[start].Trim().Length == 0 && lines[start - 1].Trim().Length == 0) lines.RemoveAt(start);
        return string.Join("\n", lines);
    }
}
