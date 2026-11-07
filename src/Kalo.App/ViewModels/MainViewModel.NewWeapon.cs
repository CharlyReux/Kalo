using Kalo.Core;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// "New weapon from this one" and "use it": a weapon copied with its own bullet and damage files (so its damage can change
/// without changing the original), then placed on a map or added to a map's weapon palette (which is also what campaign
/// squads pick their weapons from).
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// Copies the weapon of <paramref name="tab"/> (with its unsaved edits) to objects\mods\&lt;mod&gt;\&lt;name&gt;, and its bullet
    /// and damage files next to it; the copy fires the copied bullet. The tab then shows the new weapon. Returns an error or null.
    /// </summary>
    public async Task<string?> NewWeaponFromAsync(TagEditorViewModel tab, string name)
    {
        if (Host == null || Paths == null) return "The engine is not running.";
        if (ModProjects?.ActiveName == null) return "Create a mod first: the new weapon goes into it.";
        string slug = Slug(name);
        if (slug.Length == 0) return "Give the weapon a name.";
        string folder = $@"objects\mods\{ModSlugForContent}\{slug}";
        string weapon = $@"{folder}\{slug}.weapon";
        if (File.Exists(System.IO.Path.Combine(Paths.WorkspaceTags, weapon))) return $"{weapon} already exists. Pick another name.";
        try
        {
            // The weapon as the tab has it (unsaved edits included), and the bullets its barrels fire.
            var doc = await Host.OpenTagAsync(tab.Path);
            var barrels = AllNodes(doc.Fields).Where(n => n.Kind == ValueKinds.Reference && SimpleView.Normalize(n.Path).EndsWith("barrels/projectile", StringComparison.OrdinalIgnoreCase)
                                                         && !string.IsNullOrWhiteSpace(n.Value)).ToList();
            // Copy the bullets first, while the Blam Host still holds them with any edit made in the Each shot box.
            var bulletCopies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string bullet in barrels.Select(b => b.Value!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string newBullet = bulletCopies.Count == 0 ? $@"{folder}\{slug}_bullet.projectile" : $@"{folder}\{slug}_bullet_{bulletCopies.Count + 1}.projectile";
                await Host.OpenTagAsync(bullet);
                await Host.SaveTagAsAsync(bullet, newBullet);
                // Its damage files too.
                var pdoc = await Host.OpenTagAsync(newBullet);
                foreach (var dmg in AllNodes(pdoc.Fields).Where(n => n.Kind == ValueKinds.Reference && !string.IsNullOrWhiteSpace(n.Value) &&
                             n.Value!.EndsWith(".damage_effect", StringComparison.OrdinalIgnoreCase) &&
                             (n.Name is "impact damage" or "detonation damage" or "attached detonation damage")).ToList())
                {
                    string newDamage = $@"{folder}\{System.IO.Path.GetFileNameWithoutExtension(newBullet)}_{dmg.Name!.Replace(' ', '_')}.damage_effect";
                    if (!File.Exists(System.IO.Path.Combine(Paths.WorkspaceTags, newDamage)))
                    {
                        await Host.OpenTagAsync(dmg.Value);
                        await Host.SaveTagAsAsync(dmg.Value, newDamage);
                        await Host.CloseTagAsync(newDamage);
                    }
                    await Host.SetFieldAsync(newBullet, dmg.Path, newDamage);
                }
                await Host.SaveTagAsync(newBullet);
                await Host.CloseTagAsync(newBullet);
                bulletCopies[bullet] = newBullet;
            }
            // Then the weapon, pointing at the copied bullets.
            await Host.SaveTagAsAsync(tab.Path, weapon);
            foreach (var b in barrels) await Host.SetFieldAsync(weapon, b.Path, bulletCopies[b.Value!]);
            await Host.SaveTagAsync(weapon);
            string original = tab.Path;
            await CloseTab(tab);
            var opened = await OpenTagAsync(weapon);
            if (opened != null) OnTagSaved(opened);
            Log($"New weapon {weapon} from {original}, with its own bullet{(bulletCopies.Count == 1 ? "" : "s")} ({string.Join(", ", bulletCopies.Values)}) and damage files. {original} is unchanged.");
            StatusMessage = "New weapon " + slug;
            await RefreshChangesAsync();
            return null;
        }
        catch (HostException ex)
        {
            return ex.Message;
        }
    }

    private static IEnumerable<FieldNode> AllNodes(IEnumerable<FieldNode>? nodes)
    {
        foreach (var n in nodes ?? Enumerable.Empty<FieldNode>())
        {
            yield return n;
            foreach (var c in AllNodes(n.Children)) yield return c;
        }
    }

    /// <summary>The map tab the "use it" buttons act on: the most recently opened one.</summary>
    public MapEditorViewModel? LastMap => Tabs.OfType<MapEditorViewModel>().LastOrDefault();

    /// <summary>Places the object in front of the camera of the last map tab, and shows that map.</summary>
    public async Task<string?> PlaceInLastMapAsync(string tag)
    {
        var map = LastMap;
        if (map == null) return "Open a map first (it can stay in its tab).";
        var at = map.PlacementPointProvider?.Invoke() ?? map.Selected?.Position ?? System.Numerics.Vector3.Zero;
        ActiveDocument = map;
        var item = await map.AddTagAtAsync(tag, at + new System.Numerics.Vector3(0, 0, 0.05f));
        return item == null ? map.Status : null;
    }

    /// <summary>
    /// Adds the weapon to the last map's weapon palette without placing it: campaign squads (Squad section › Weapon) and
    /// the Objects list can then use it.
    /// </summary>
    public async Task<string?> AddToLastMapPaletteAsync(string tag)
    {
        var map = LastMap;
        if (map == null || Host == null) return "Open a map first (it can stay in its tab).";
        string? category = MapEditorViewModel.CategoryFor(tag);
        if (category == null) return "This file can't be placed in a map.";
        try
        {
            await Host.PlacementOpAsync(new PlacementOpParams { Path = map.Path, Category = category, Op = PlacementOps.AddPalette, Tag = tag });
            map.IsDirty = true;
            await map.RefreshSquadPalettesAsync();
            return null;
        }
        catch (HostException ex) { return ex.Message; }
    }
}
