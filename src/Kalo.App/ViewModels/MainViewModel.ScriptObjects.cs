using Kalo.Core;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// "Use in scripts": objects the mod lets gametype scripts create (create_object("name", ...)). The names live in the
/// game's global object type list and in the script compiler's objects.txt (see <see cref="ScriptObjects"/>); entries
/// are only ever added at the end, renamed, or removed from the end, so existing gametypes keep their objects.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Object type names the gametype deployed last creates (create_object), for Play.</summary>
    private HashSet<string> _gametypeCreates = new(StringComparer.Ordinal);

    public async Task<ScriptObjectsResult?> GetScriptObjectsAsync()
    {
        if (await WaitForHostAsync() == null) return null;
        try { return await Host!.ScriptObjectsAsync(new ScriptObjectsParams { Op = ScriptObjectOps.List }); }
        catch (HostException ex) { AddProblem("error", "Script objects: " + ex.Message, ScriptObjects.ListTag); return null; }
    }

    /// <summary>The entry for an object tag, or null when scripts can't create it.</summary>
    public async Task<ScriptObjectEntry?> ScriptObjectForAsync(string tagPath)
    {
        var r = await GetScriptObjectsAsync();
        return r?.Entries.LastOrDefault(e => e.ObjectTag.Equals(tagPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Why an entry can't be removed, or null when it can (only the last one, and only one the mod added).</summary>
    public string? WhyNotRemovable(ScriptObjectEntry entry, int count)
    {
        if (Paths == null) return "Kalo is not set up.";
        if (entry.Index < ScriptObjects.VanillaCount(Paths)) return "This is one of the game's own objects.";
        if (entry.Index != count - 1) return "Only the last object added can be removed: scripts refer to these objects by position, so removing one before others would change them.";
        return null;
    }

    /// <summary>Lets scripts create the object under <paramref name="name"/>. Null when done, otherwise why not.</summary>
    public async Task<string?> RegisterScriptObjectAsync(string tagPath, string name)
    {
        if (Paths == null || Host == null) return "The Blam Host is not running.";
        if (!ScriptObjects.IsObjectTag(tagPath)) return "Only objects (crate, scenery, weapon, vehicle...) can be used in scripts.";
        if (!File.Exists(System.IO.Path.Combine(Paths.WorkspaceTags, tagPath))) return "Save the tag first.";
        var r = await GetScriptObjectsAsync();
        if (r == null) return "Could not read the object type list.";
        var names = ScriptObjects.ReadNames(Paths);
        if (names.Count != r.Entries.Count)
            return $"The object type list ({r.Entries.Count} entries) and objects.txt ({names.Count} names) don't match, so Kalo won't add to them. Remove both files from the mod to go back to HREK's versions.";
        if (r.Entries.Any(e => e.ObjectTag.Equals(tagPath, StringComparison.OrdinalIgnoreCase)))
            return "Scripts can already create this object.";
        if (r.Entries.Count >= r.MaximumCount) return $"The object type list is full ({r.MaximumCount} entries).";
        if (ScriptObjects.CheckName(name, names) is { } bad) return bad;
        try
        {
            await Host.ScriptObjectsAsync(new ScriptObjectsParams { Op = ScriptObjectOps.Add, Name = name, ObjectTag = tagPath });
            names.Add(name);
            ScriptObjects.WriteNames(Paths, names);
        }
        catch (Exception ex) { return ex.Message; }
        Log($"Scripts can now create {tagPath} as \"{name}\": create_object(\"{name}\", at ...). " +
            "A map must contain the object for a gametype to create it there: Play adds it to the playtest's copy of the map; the map editor's Script objects button adds it to the map itself.");
        await AfterScriptObjectsChangedAsync();
        return null;
    }

    public async Task<string?> RenameScriptObjectAsync(ScriptObjectEntry entry, string name)
    {
        if (Paths == null || Host == null) return "The Blam Host is not running.";
        var names = ScriptObjects.ReadNames(Paths);
        if (entry.Index < ScriptObjects.VanillaCount(Paths)) return "This is one of the game's own objects.";
        if (entry.Index >= names.Count) return "objects.txt doesn't list this object.";
        if (ScriptObjects.CheckName(name, names.Where((n, i) => i != entry.Index)) is { } bad) return bad;
        try
        {
            await Host.ScriptObjectsAsync(new ScriptObjectsParams { Op = ScriptObjectOps.Rename, Index = entry.Index, Name = name });
            names[entry.Index] = name;
            ScriptObjects.WriteNames(Paths, names);
        }
        catch (Exception ex) { return ex.Message; }
        Log($"Script object \"{entry.Name}\" is now \"{name}\". Scripts that still use \"{entry.Name}\" won't compile until they use the new name.");
        await AfterScriptObjectsChangedAsync();
        return null;
    }

    public async Task<string?> RemoveScriptObjectAsync(ScriptObjectEntry entry)
    {
        if (Paths == null || Host == null || Baseline == null) return "The Blam Host is not running.";
        var r = await GetScriptObjectsAsync();
        if (r == null) return "Could not read the object type list.";
        if (WhyNotRemovable(entry, r.Entries.Count) is { } why) return why;
        try
        {
            await Host.ScriptObjectsAsync(new ScriptObjectsParams { Op = ScriptObjectOps.RemoveLast, Index = entry.Index });
            var names = ScriptObjects.ReadNames(Paths);
            if (names.Count == r.Entries.Count) names.RemoveAt(names.Count - 1);
            ScriptObjects.WriteNames(Paths, names);
            if (r.Entries.Count - 1 == ScriptObjects.VanillaCount(Paths))
            {
                // Nothing of the mod's left: put HREK's exact files back so they leave the mod.
                await Host.CloseTagAsync(ScriptObjects.ListTag);
                Baseline.Revert(@"tags\" + ScriptObjects.ListTag);
                Baseline.Revert(ScriptObjects.NamesFile);
            }
        }
        catch (Exception ex) { return ex.Message; }
        Log($"Scripts can no longer create \"{entry.Name}\" ({entry.ObjectTag}). Maps keep it in their palette; that does no harm.");
        await AfterScriptObjectsChangedAsync();
        return null;
    }

    /// <summary>The compiler re-reads its object list, autocomplete follows, the mod records the two files.</summary>
    private async Task AfterScriptObjectsChangedAsync()
    {
        MegaloCatalog = null;
        await EnsureMegaloCatalogAsync();
        foreach (var s in Tabs.OfType<ScriptEditorViewModel>()) s.UpdateCatalog();
        foreach (var t in Tabs.OfType<TagEditorViewModel>()) await t.RefreshScriptNameAsync();
        // The object browser shows script names: read them again if it was opened.
        _scriptNames = null;
        if (_catalog != null) _ = LoadObjectNamesAsync();
        await RefreshChangesAsync();
        _ = AutoCaptureAsync();
    }

    /// <summary>Script objects the mod added (past HREK's own entries).</summary>
    public async Task<List<ScriptObjectEntry>> ModScriptObjectsAsync()
    {
        if (Paths == null) return new();
        var r = await GetScriptObjectsAsync();
        int vanilla = ScriptObjects.VanillaCount(Paths);
        return r?.Entries.Where(e => e.Index >= vanilla).ToList() ?? new();
    }

    /// <summary>
    /// The game drops a gametype that creates an object the map doesn't contain ("this script references object X,
    /// which is not in this map"). When the gametype creates objects the mod added, Play puts them in the palette of a
    /// throwaway copy of the map (the one Play from camera made, or a new one). Returns the copy, or null if none was needed.
    /// </summary>
    private async Task<string?> AddScriptObjectsForPlayAsync(string scenario, string? copy)
    {
        if (Host == null || _gametypeCreates.Count == 0) return null;
        var needed = (await ModScriptObjectsAsync()).Where(e => _gametypeCreates.Contains(e.Name)).ToList();
        if (needed.Count == 0) return null;
        try
        {
            if (copy == null)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(scenario);
                copy = $@"levels\mods\_kalo_play\{name}\{name}.scenario";
                if (IsGameRunning) Game?.Stop();
                await Host.PlacementOpAsync(new PlacementOpParams { Path = scenario, Op = PlacementOps.MakePlayCopy, Tag = copy });
            }
            foreach (var e in needed)
                if (ScriptObjects.PaletteCategory.TryGetValue(System.IO.Path.GetExtension(e.ObjectTag), out var category))
                    await Host.PlacementOpAsync(new PlacementOpParams { Path = copy, Category = category, Op = PlacementOps.AddPalette, Tag = e.ObjectTag });
            await Host.SaveTagAsync(copy);
            Log($"Play: {string.Join(", ", needed.Select(e => e.Name))} added to this playtest's copy of the map (a map must contain the objects a gametype creates). " +
                "To add them to the map itself, use Script objects in the map editor.");
            return copy;
        }
        catch (HostException ex)
        {
            AddProblem("warning", "Play: could not add the gametype's script objects to the map: " + ex.Message, scenario);
            return null;
        }
    }

    /// <summary>Puts every object the mod added for scripts in the map's palette (nothing is placed). Returns how many.</summary>
    public async Task<int> AddScriptObjectsToMapAsync(MapEditorViewModel map)
    {
        if (Host == null) return 0;
        var mine = await ModScriptObjectsAsync();
        int added = 0;
        foreach (var e in mine)
        {
            if (!ScriptObjects.PaletteCategory.TryGetValue(System.IO.Path.GetExtension(e.ObjectTag), out var category)) continue;
            try
            {
                await Host.PlacementOpAsync(new PlacementOpParams { Path = map.Path, Category = category, Op = PlacementOps.AddPalette, Tag = e.ObjectTag });
                added++;
            }
            catch (HostException ex) { AddProblem("warning", $"Script object {e.Name}: {ex.Message}", map.Path); }
        }
        return added;
    }
}
