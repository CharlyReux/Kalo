using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// Look: paint an object (weapon, vehicle, crate...) with any level material, or make it look like another object of the
/// same kind. Works for multiplayer and campaign alike: it changes the object file, which every map uses.
/// </summary>
public partial class TagEditorViewModel
{
    [ObservableProperty] private bool _isPaintOpen;
    [ObservableProperty] private bool _isLooksLikeOpen;
    [ObservableProperty] private string _looksLikeQuery = "";
    [ObservableProperty] private string _lookStatus = "";
    public ObservableCollection<string> LooksLikeResults { get; } = new();

    /// <summary>The mod's own objects are painted in place; the game's get a painted copy in the mod.</summary>
    public bool IsModObject => Path.Contains(@"\mods\", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task TogglePaint()
    {
        IsPaintOpen = !IsPaintOpen;
        IsLooksLikeOpen = false;
        if (IsPaintOpen) await _main.EnsureMaterialsAsync();
    }

    [RelayCommand]
    private void ToggleLooksLike()
    {
        IsLooksLikeOpen = !IsLooksLikeOpen;
        IsPaintOpen = false;
        UpdateLooksLike();
    }

    partial void OnLooksLikeQueryChanged(string value) => UpdateLooksLike();

    /// <summary>Objects of the same kind (weapons for a weapon...), filtered by the query words.</summary>
    private void UpdateLooksLike()
    {
        LooksLikeResults.Clear();
        if (!IsLooksLikeOpen) return;
        string ext = System.IO.Path.GetExtension(Path);
        string[] words = LooksLikeQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (string p in _main.TagIndex)
        {
            if (!p.EndsWith(ext, StringComparison.OrdinalIgnoreCase) || p.Equals(Path, StringComparison.OrdinalIgnoreCase)) continue;
            if (words.Length > 0 && !words.All(w => p.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            LooksLikeResults.Add(p);
            if (LooksLikeResults.Count >= 120) break;
        }
    }

    /// <summary>Paints every material of the object (and, for a weapon, of what the player holds) with <paramref name="material"/>.</summary>
    [RelayCommand]
    private async Task PaintWith(MaterialChoice? material)
    {
        if (material == null || _main.Host == null) return;
        if (IsDirty) { LookStatus = "Save first: painting copies the file as it is on disk."; return; }
        string shader = MainViewModel.Slug(System.IO.Path.GetFileNameWithoutExtension(material.Shader));
        string target = IsModObject ? Path : _main.NewTagPathFor(Path, System.IO.Path.GetFileNameWithoutExtension(Path) + "_" + shader);
        try
        {
            var r = await _main.Host.MakeVariantAsync(new MakeVariantParams { Object = Path, Shader = material.Shader, NewObject = target });
            IsPaintOpen = false;
            _main.Log($"Painted {target} with {material.Name} ({r.Materials} material(s), model {r.RenderModel}).");
            if (IsModObject)
            {
                await _main.ReloadTabAsync(this);
                LookStatus = $"Painted with {material.Name}. Maps that use it show it next time they open; a running game reloads it.";
            }
            else
            {
                var copy = await _main.OpenTagAsync(target);
                if (copy != null) { _main.OnTagSaved(copy); copy.LookStatus = $"A painted copy of {System.IO.Path.GetFileNameWithoutExtension(Path)} ({material.Name}). Use it: Place in map, Add to map palette, or Use in scripts."; }
            }
            await _main.RefreshChangesAsync();
        }
        catch (HostException ex)
        {
            LookStatus = "Could not paint it: " + ex.Message;
        }
    }

    /// <summary>
    /// Takes the model of <paramref name="other"/> (and, for weapons, its first person models): this object then looks like
    /// it, and keeps everything else (stats, sounds, animations). Unsaved: Save to keep it.
    /// </summary>
    [RelayCommand]
    private async Task LooksLike(string? other)
    {
        if (other == null || _main.Host == null) return;
        try
        {
            var doc = await _main.Host.OpenTagAsync(other);
            var theirs = Flatten(doc.Fields).Where(n => n.Kind == ValueKinds.Reference && n.Name is "model" or "first person model").ToList();
            var mine = AllFields().Where(f => f.IsReference && f.RawName is "model" or "first person model").ToList();
            int changed = 0;
            var model = theirs.FirstOrDefault(n => n.Name == "model");
            var myModel = mine.FirstOrDefault(f => f.RawName == "model");
            if (model?.Value is { Length: > 0 } && myModel != null) { await CommitAsync(myModel, model.Value); changed++; }
            var theirFp = theirs.Where(n => n.Name == "first person model").ToList();
            var myFp = mine.Where(f => f.RawName == "first person model").ToList();
            for (int i = 0; i < myFp.Count && theirFp.Count > 0; i++)
            {
                string? v = theirFp[Math.Min(i, theirFp.Count - 1)].Value;
                if (!string.IsNullOrEmpty(v)) { await CommitAsync(myFp[i], v); changed++; }
            }
            if (!_main.Tabs.OfType<TagEditorViewModel>().Any(t => t.Path.Equals(other, StringComparison.OrdinalIgnoreCase)))
                await _main.Host.CloseTagAsync(other);
            IsLooksLikeOpen = false;
            LookStatus = changed == 0 ? $"{System.IO.Path.GetFileNameWithoutExtension(other)} has no model to take."
                : $"Looks like {System.IO.Path.GetFileNameWithoutExtension(other)} now ({changed} model reference{(changed == 1 ? "" : "s")}). Save to keep it." +
                  (myFp.Count > 0 ? " In first person it keeps its own animations: hands may not line up with a very different weapon." : "") +
                  (IsModObject ? "" : " This changes the game's own file in your mod: New weapon from this… (or Save as new…) first keeps the original as it is.");
        }
        catch (HostException ex)
        {
            LookStatus = "Could not read it: " + ex.Message;
        }
    }

    private static IEnumerable<FieldNode> Flatten(IEnumerable<FieldNode>? nodes)
    {
        foreach (var n in nodes ?? Enumerable.Empty<FieldNode>())
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }
}
