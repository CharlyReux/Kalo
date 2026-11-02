namespace Kalo.Core;

/// <summary>How a file of the mod can reach players.</summary>
public enum VanillaStatus
{
    /// <summary>Plays in unmodded MCC as it is (a game type becomes a game type file).</summary>
    Vanilla,
    /// <summary>Needs mods as it is, but can be exported to something vanilla (a map built on a Reach map → Forge variant).</summary>
    Exportable,
    /// <summary>Changes the game itself: only plays with mods (anti-cheat off).</summary>
    NeedsMods,
}

public sealed record VanillaItem(string Path, VanillaStatus Status, string Reason);

public sealed record VanillaReport(IReadOnlyList<VanillaItem> Items)
{
    public bool IsVanilla => Items.All(i => i.Status == VanillaStatus.Vanilla);
    public bool CanBeVanilla => Items.All(i => i.Status != VanillaStatus.NeedsMods);
    public int NeedingMods => Items.Count(i => i.Status == VanillaStatus.NeedsMods);
    public int Exportable => Items.Count(i => i.Status == VanillaStatus.Exportable);

    /// <summary>One line for the mod panel.</summary>
    public string Summary =>
        Items.Count == 0 ? "Vanilla: nothing in the mod yet."
        : IsVanilla ? "Vanilla: plays in MCC without mods."
        : CanBeVanilla ? $"Vanilla once exported: {Exportable} map{(Exportable == 1 ? "" : "s")} to export as Forge variant{(Exportable == 1 ? "" : "s")}."
        : $"Needs mods: {NeedingMods} file{(NeedingMods == 1 ? " changes" : "s change")} the game.";
}

/// <summary>
/// Whether a mod can be played in vanilla MCC (mods off, anti-cheat on), file by file. Vanilla MCC accepts two kinds of
/// custom content: game type files (any Megalo script that fits the format and uses the game's own objects) and Forge
/// map variants of Reach's own maps. Every other changed file (a new map level, a changed weapon, a painted block, a new
/// sound...) only works with mods.
/// </summary>
public static class VanillaCheck
{
    /// <param name="changes">The workspace changes (Baseline.Scan).</param>
    /// <param name="mapTemplates">New maps (tag path) → the Reach map they were made from (ModProjects manifest).</param>
    /// <param name="gametypeTooBig">For a gametype source (workspace path): a reason when its compiled variant is too big for a game type file, else null.</param>
    public static VanillaReport Check(IReadOnlyList<WorkspaceChange> changes, IReadOnlyDictionary<string, string> mapTemplates,
        Func<string, string?>? gametypeTooBig = null)
    {
        var items = new List<VanillaItem>();
        // Folders of the new maps: their helper tags (bsp, lighting, resources) belong to the map.
        var mapFolders = changes.Where(c => c.Path.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase) && c.Kind != ChangeKind.Deleted)
            .Select(c => Path.GetDirectoryName(c.Path) ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in changes)
        {
            string p = c.Path;
            string ext = Path.GetExtension(p).TrimStart('.').ToLowerInvariant();
            // Build outputs (tool.exe's reports), not content of the mod.
            if (p.StartsWith(@"tags\reports\", StringComparison.OrdinalIgnoreCase)) continue;
            // A painted block is three tags (object, model, render model): count the object only.
            if (p.Contains(@"\blocks\", StringComparison.OrdinalIgnoreCase) && ext is "model" or "render_model") continue;
            if (c.Kind == ChangeKind.Deleted)
            {
                items.Add(new VanillaItem(p, VanillaStatus.NeedsMods, "a file of the game is removed"));
                continue;
            }
            if (p.StartsWith(@"data\multiplayer\megalo\", StringComparison.OrdinalIgnoreCase) && ext is "kscript" or "txt")
            {
                string? big = gametypeTooBig?.Invoke(p);
                items.Add(big == null
                    ? new VanillaItem(p, VanillaStatus.Vanilla, "a game type: Play in MCC writes it as a game type file")
                    : new VanillaItem(p, VanillaStatus.NeedsMods, big));
                continue;
            }
            if (ext == "scenario")
            {
                string tagPath = p.StartsWith(@"tags\", StringComparison.OrdinalIgnoreCase) ? p[5..] : p;
                if (c.Kind == ChangeKind.Modified)
                    items.Add(new VanillaItem(p, VanillaStatus.NeedsMods, "one of Reach's own maps is changed (edit a copy made with New map instead)"));
                else if (mapTemplates.TryGetValue(tagPath, out var template) && !template.StartsWith(@"levels\mods\", StringComparison.OrdinalIgnoreCase))
                    items.Add(new VanillaItem(p, VanillaStatus.Exportable,
                        $"a new map: needs mods as it is; Export as Forge variant makes a vanilla version on {Path.GetFileNameWithoutExtension(template)}"));
                else
                    items.Add(new VanillaItem(p, VanillaStatus.NeedsMods, "a new map level: only plays with mods"));
                continue;
            }
            if (mapFolders.Contains(Path.GetDirectoryName(p) ?? "") && !p.Contains(@"\blocks\", StringComparison.OrdinalIgnoreCase))
                continue; // part of a map listed above
            if (p.StartsWith(@"data\", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new VanillaItem(p, VanillaStatus.NeedsMods, "a source file of the game's data"));
                continue;
            }
            string what = p.Contains(@"\blocks\", StringComparison.OrdinalIgnoreCase) ? "a painted block: a new object, which vanilla MCC doesn't have"
                : c.Kind == ChangeKind.Added ? "a new game file (object, sound, texture...), which vanilla MCC doesn't have"
                : "a changed game file: vanilla MCC uses its own";
            items.Add(new VanillaItem(p, VanillaStatus.NeedsMods, what));
        }
        return new VanillaReport(items);
    }
}
