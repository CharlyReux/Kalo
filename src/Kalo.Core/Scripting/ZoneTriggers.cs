using System.Text;
using System.Text.RegularExpressions;

namespace Kalo.Core.Scripting;

/// <summary>What happens when a player enters a zone (Map › inspector › Zone › When a player enters).</summary>
public enum ZoneAction { Message, Kill, GiveWeapon, Points, Traits, Teleport }

/// <summary>
/// Writes the gametype code for "when a player enters this zone, do X": a trigger over the objects with the zone's Megalo
/// label that checks each player with object_in_area and acts once per entry (a player flag, reset on leaving).
/// Compiled with HREK's compiler for every action (kalo-cli zone-trigger).
/// </summary>
public static class ZoneTriggers
{
    public static readonly (ZoneAction Action, string Label, string ParamLabel, string Default)[] Choices =
    {
        (ZoneAction.Message, "Show a message", "Message", "Welcome to the zone!"),
        (ZoneAction.Kill, "Kill the player (kill zone)", "", ""),
        (ZoneAction.GiveWeapon, "Give a weapon", "Weapon", "rocket_launcher"),
        (ZoneAction.Points, "Give points", "Points", "1"),
        (ZoneAction.Traits, "Speed boost for 10 seconds", "Speed %", "200"),
        (ZoneAction.Teleport, "Teleport to another object", "Label of the destination", "teleport_exit"),
    };

    /// <summary>A Megalo label from an object name: lower case, letters, digits and _.</summary>
    public static string LabelFor(string name)
    {
        string s = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9_]+", "_").Trim('_');
        return s.Length == 0 ? "zone" : char.IsDigit(s[0]) ? "zone_" + s : s;
    }

    /// <summary>The code, in the classic style (KaloScriptStyle.FromClassic turns it into the Python style).</summary>
    public static string Classic(string label, ZoneAction action, string param)
    {
        string id = LabelFor(label);
        string flag = $"kalo_{Short(action)}_{id}";
        if (flag.Length > 30) flag = flag[..30];
        var sb = new StringBuilder();
        string what = Choices.First(c => c.Action == action).Label.ToLowerInvariant();
        sb.Append($"-- Zone \"{label}\": when a player enters, {what}. Written by Kalo (map inspector › Zone).\n");
        sb.Append($"declare player.number {flag} = 0\n");
        if (action == ZoneAction.Traits)
            sb.Append($"traits kalo_boost_{id} \"Boost\" \"\"\n  speed = {Number(param, 200)}\nend\n" +
                      $"declare player.timer kalo_boost_{id}_time = 10\n" +
                      $"declare player.number kalo_boosting_{id} = 0\n");
        if (action == ZoneAction.Teleport)
            sb.Append($"objects kalo_exit_{id} with label \"{LabelFor(param)}\"\n");
        sb.Append($"for each object with label \"{label}\" do\n");
        sb.Append("  for each player do\n");
        sb.Append("    temp object body = current_player\n");
        sb.Append($"    if body != none and object_in_area(body, current_object) then\n");
        sb.Append($"      if current_player.{flag} == 0 then\n");
        sb.Append($"        current_player.{flag} = 1\n");
        foreach (string l in ActionLines(action, param, id)) sb.Append("        " + l + "\n");
        sb.Append("      end\n");
        sb.Append("    else\n");
        sb.Append($"      current_player.{flag} = 0\n");
        sb.Append("    end\n");
        sb.Append("  end\n");
        sb.Append("end\n");
        if (action == ZoneAction.Traits)
        {
            // The boost lasts while its timer runs.
            sb.Append("for each player do\n");
            sb.Append($"  if current_player.kalo_boosting_{id} == 1 then\n");
            sb.Append($"    apply_player_traits(current_player, kalo_boost_{id})\n");
            sb.Append($"    if timer_expired(current_player.kalo_boost_{id}_time) then\n");
            sb.Append($"      current_player.kalo_boosting_{id} = 0\n");
            sb.Append("    end\n");
            sb.Append("  end\n");
            sb.Append("end\n");
        }
        return sb.ToString();
    }

    private static IEnumerable<string> ActionLines(ZoneAction action, string param, string id)
    {
        switch (action)
        {
            case ZoneAction.Message:
                yield return $"hud_post_message(player current_player, none, \"{param.Replace("\"", "'")}\")";
                break;
            case ZoneAction.Kill:
                yield return "object_destroy(body)";
                break;
            case ZoneAction.GiveWeapon:
                yield return $"biped_give_weapon(body, \"{LabelFor(param)}\", force)";
                break;
            case ZoneAction.Points:
                yield return $"current_player.score += {Number(param, 1)}";
                break;
            case ZoneAction.Traits:
                yield return $"current_player.kalo_boosting_{id} = 1";
                yield return $"timer_reset(current_player.kalo_boost_{id}_time)";
                yield return $"timer_set_rate(current_player.kalo_boost_{id}_time, 1)";
                break;
            case ZoneAction.Teleport:
                // Megalo has no teleport: attach the body to the destination, then detach it there.
                yield return "temp object exit = none";
                yield return $"get_random_object(kalo_exit_{id}, none, exit)";
                yield return "if exit != none then";
                yield return "  object_attach(body, exit, 0, 0, 0)";
                yield return "  object_detach(body)";
                yield return "end";
                break;
        }
    }

    private static string Short(ZoneAction a) => a switch
    {
        ZoneAction.Message => "msg", ZoneAction.Kill => "kill", ZoneAction.GiveWeapon => "gun", ZoneAction.Points => "pts", ZoneAction.Traits => "boost", _ => "tp",
    };

    private static string Number(string s, int fallback) =>
        int.TryParse(s.Trim(), out int n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : fallback.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The script with the zone code added at its end (in its own style), replacing code Kalo wrote earlier for the same zone.</summary>
    public static string AddTo(string script, string label, ZoneAction action, string param)
    {
        string classic = Classic(label, action, param);
        bool classicStyle = KaloScriptStyle.IsClassic(script);
        string code = classicStyle ? classic : KaloScriptStyle.FromClassic(classic) ?? classic;
        string nl = script.Contains("\r\n") ? "\r\n" : "\n";
        string text = RemoveFor(script.Replace("\r\n", "\n"), label, action).TrimEnd('\n');
        return (text + "\n\n" + code.TrimEnd('\n') + "\n").Replace("\n", nl);
    }

    /// <summary>
    /// Removes the block Kalo wrote for this zone and action (another action on the same zone stays): from its comment
    /// line to the first blank line.
    /// </summary>
    public static string RemoveFor(string script, string label, ZoneAction action)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n').ToList();
        string mark = $"Zone \"{label}\": when a player enters, {Choices.First(c => c.Action == action).Label.ToLowerInvariant()}.";
        int start = lines.FindIndex(l => l.TrimStart().StartsWith("--") || l.TrimStart().StartsWith("#") ? l.Contains(mark) : false);
        if (start < 0) return script;
        int end = start + 1;
        // Kalo's block is contiguous (no blank lines inside): it ends at the first blank line or the end of the file.
        while (end < lines.Count && lines[end].Trim().Length > 0) end++;
        lines.RemoveRange(start, end - start);
        if (start < lines.Count && start > 0 && lines[start].Trim().Length == 0 && lines[start - 1].Trim().Length == 0) lines.RemoveAt(start);
        return string.Join("\n", lines);
    }
}
