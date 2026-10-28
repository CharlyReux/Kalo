using System.Text.RegularExpressions;

namespace Kalo.App.ViewModels;

/// <summary>
/// Friendly names for engine fields. The engine's own labels are mostly raw lowercase names
/// ("rounds loaded maximum"); this catalog gives the common ones a name a beginner recognises,
/// and capitalises everything else. Keys are raw field names.
/// </summary>
public static class FieldLabels
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rounds loaded maximum"] = "Magazine size",
        ["rounds total initial"] = "Starting ammo (reserve)",
        ["rounds total maximum"] = "Maximum ammo (reserve)",
        ["rounds reloaded"] = "Rounds per reload",
        ["rounds recharged"] = "Ammo regeneration",
        ["rounds per second"] = "Fire rate",
        ["rounds per shot"] = "Ammo used per shot",
        ["shots per fire"] = "Shots per trigger pull",
        ["fire recovery time"] = "Time between bursts",
        ["error angle"] = "Spread",
        ["minimum error"] = "Minimum spread",
        ["acceleration time"] = "Spin-up time",
        ["deceleration time"] = "Spin-down time",
        ["autoaim angle"] = "Aim assist angle",
        ["autoaim range"] = "Aim assist range",
        ["magnetism angle"] = "Bullet magnetism angle",
        ["magnetism range"] = "Bullet magnetism range",
        ["magnification levels"] = "Zoom levels",
        ["magnification range"] = "Zoom range",
        ["ready time"] = "Draw time",
        ["heat generated per round"] = "Heat per shot",
        ["overheated threshold"] = "Overheat at",
        ["heat loss per second"] = "Cooling per second",
        ["maximum lunge range"] = "Melee lunge range",
        ["forward movement penalty"] = "Move speed penalty (forward)",
        ["sideways movement penalty"] = "Move speed penalty (sideways)",
        ["default spawn time"] = "Respawn time on map",
        ["default abandonment time"] = "Despawn time when dropped",
    };

    public static string For(string? rawName, string? displayName)
    {
        string raw = rawName ?? displayName ?? "";
        if (Names.TryGetValue(raw.Trim(), out string? friendly)) return friendly;
        string text = string.IsNullOrWhiteSpace(displayName) ? raw : displayName!;
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length == 0 ? "(unnamed)" : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    /// <summary>Splits the engine's "units" text: "[0,1]" and "0-1" are ranges, the rest are units.</summary>
    public static (string Units, string Range) SplitUnits(string? units)
    {
        string u = (units ?? "").Trim();
        if (u.Length == 0) return ("", "");
        if (Regex.IsMatch(u, @"^\[.*\]$") || Regex.IsMatch(u, @"^-?\d+(\.\d+)?\s*-\s*-?\d+(\.\d+)?$")) return ("", u);
        return (u, "");
    }
}
