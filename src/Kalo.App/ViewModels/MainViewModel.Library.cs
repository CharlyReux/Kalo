using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Core.Geometry;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// Bottom tool windows next to Output: the object browser (every placeable object, with a 3D preview) and the
/// prefabs of the active mod. Also the material library the block tools paint with.
/// </summary>
public partial class MainViewModel
{
    public bool IsObjects => BottomPanel == "objects";
    public bool IsPrefabs => BottomPanel == "prefabs";
    [RelayCommand] private void ShowObjects() { BottomPanel = "objects"; EnsureObjectCatalog(); }
    [RelayCommand] private void ShowPrefabs() { BottomPanel = "prefabs"; RefreshPrefabs(); }

    // ---- which file a map is compared with ---------------------------------------------------------

    /// <summary>
    /// The scenario a map's objects are compared with: Reach's own file for a vanilla map, the level a new map was
    /// copied from for a mod map (recorded when it was created), or null.
    /// </summary>
    public string? BaselineFileForMap(string scenarioPath)
    {
        if (Paths == null) return null;
        string vanilla = Path.Combine(Paths.HrekTags, scenarioPath);
        if (File.Exists(vanilla)) return vanilla;
        var templates = ModProjects?.Active?.Manifest.MapTemplates;
        if (templates != null && templates.TryGetValue(scenarioPath, out var template))
        {
            string file = Path.Combine(Paths.HrekTags, template);
            if (File.Exists(file)) return file;
        }
        return null;
    }

    // ---- object browser --------------------------------------------------------------------------------

    public ObservableCollection<ObjectCategory> ObjectCategories { get; } = new();
    public ObservableCollection<ObjectEntry> ObjectResults { get; } = new();
    [ObservableProperty] private ObjectCategory? _selectedObjectCategory;
    [ObservableProperty] private string _objectFilter = "";
    [ObservableProperty] private ObjectEntry? _selectedObject;
    [ObservableProperty] private Mesh? _previewMesh;
    [ObservableProperty] private string _previewStatus = "Pick an object to preview it.";
    [ObservableProperty] private int _previewVersion;
    private Dictionary<string, List<ObjectEntry>>? _catalog;

    private static readonly (string Key, string Title, Func<string, bool> Match)[] CategoryRules =
    {
        ("blocks", "Forge blocks", t => t.StartsWith(@"objects\levels\forge\ff_", StringComparison.OrdinalIgnoreCase) && (t.EndsWith(".crate") || t.EndsWith(".scenery") || t.EndsWith(".device_machine"))),
        ("weapons", "Weapons", t => t.EndsWith(".weapon")),
        ("vehicles", "Vehicles", t => t.EndsWith(".vehicle")),
        ("equipment", "Equipment", t => t.EndsWith(".equipment")),
        ("spawning", "Spawning and game", t => t.StartsWith(@"objects\multi\", StringComparison.OrdinalIgnoreCase) && (t.EndsWith(".scenery") || t.EndsWith(".crate") || t.EndsWith(".device_machine"))),
        ("scenery", "Scenery", t => t.EndsWith(".scenery")),
        ("crates", "Crates", t => t.EndsWith(".crate")),
        ("devices", "Devices", t => t.EndsWith(".device_machine") || t.EndsWith(".device_control") || t.EndsWith(".device_terminal")),
        ("effects", "Effects and sounds", t => t.EndsWith(".effect_scenery") || t.EndsWith(".sound_scenery")),
        ("bipeds", "Characters", t => t.EndsWith(".biped") || t.EndsWith(".giant")),
    };

    /// <summary>Sorts every placeable object tag of the workspace into categories (once).</summary>
    private void EnsureObjectCatalog()
    {
        if (_catalog != null && _catalog.Values.Sum(v => v.Count) > 0) return;
        var catalog = CategoryRules.ToDictionary(r => r.Key, _ => new List<ObjectEntry>());
        foreach (string tag in TagIndex)
        {
            if (MapEditorViewModel.CategoryFor(tag) == null) continue;
            if (tag.StartsWith("_kalo", StringComparison.OrdinalIgnoreCase)) continue;
            var rule = CategoryRules.FirstOrDefault(r => r.Match(tag.ToLowerInvariant()));
            if (rule.Key == null) continue;
            catalog[rule.Key].Add(new ObjectEntry(tag));
        }
        _catalog = catalog;
        ObjectCategories.Clear();
        foreach (var r in CategoryRules)
            if (catalog[r.Key].Count > 0) ObjectCategories.Add(new ObjectCategory(r.Key, r.Title, catalog[r.Key].Count));
        SelectedObjectCategory ??= ObjectCategories.FirstOrDefault();
        FilterObjects();
    }

    partial void OnSelectedObjectCategoryChanged(ObjectCategory? value) => FilterObjects();
    partial void OnObjectFilterChanged(string value) => FilterObjects();

    private void FilterObjects()
    {
        ObjectResults.Clear();
        if (_catalog == null) return;
        string[] words = ObjectFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<ObjectEntry> source = words.Length > 0
            ? _catalog.Values.SelectMany(v => v)
            : SelectedObjectCategory != null ? _catalog[SelectedObjectCategory.Key] : Enumerable.Empty<ObjectEntry>();
        foreach (var e in source.Where(e => words.All(w => e.Tag.Contains(w, StringComparison.OrdinalIgnoreCase))).OrderBy(e => e.Name).Take(600))
            ObjectResults.Add(e);
    }

    partial void OnSelectedObjectChanged(ObjectEntry? value) => _ = LoadPreviewAsync(value);

    private int _previewRequest;

    private async Task LoadPreviewAsync(ObjectEntry? entry)
    {
        int request = ++_previewRequest;
        if (entry == null || Host == null) { PreviewMesh = null; return; }
        PreviewStatus = "Loading " + entry.Name + "…";
        try
        {
            var info = (await Host.ObjectInfoAsync(new[] { entry.Tag })).Objects?.FirstOrDefault();
            if (request != _previewRequest) return;
            if (info?.RenderModel == null) { PreviewMesh = null; PreviewStatus = entry.Name + " has no model (shown as a box on the map)."; return; }
            await Models.LoadAsync(Host, new[] { info.RenderModel });
            var mesh = Models.Get(info.RenderModel);
            if (mesh != null) await Textures.LoadAsync(Host, mesh.Materials.SelectMany(m => m.Bitmaps()));
            if (request != _previewRequest) return;
            PreviewMesh = mesh;
            PreviewVersion++;
            var size = mesh == null ? System.Numerics.Vector3.Zero : mesh.Max - mesh.Min;
            PreviewStatus = mesh == null ? "No preview." : $"{entry.Name} · {size.X:0.#} × {size.Y:0.#} × {size.Z:0.#} units · drag it onto the map or click Add";
        }
        catch (Exception ex)
        {
            if (request == _previewRequest) PreviewStatus = "No preview: " + ex.Message;
        }
    }

    public TextureImage? TextureFor(string? bitmap) => string.IsNullOrEmpty(bitmap) ? null : Textures.Get(bitmap);

    [RelayCommand]
    private async Task AddSelectedObject()
    {
        if (SelectedObject == null) return;
        if (ActiveMap == null) { AddProblem("warning", "Open a map first, then add objects to it.", "Objects"); return; }
        var at = ActiveMap.PlacementPointProvider?.Invoke() ?? ActiveMap.Selected?.Position ?? System.Numerics.Vector3.Zero;
        await ActiveMap.AddTagAtAsync(SelectedObject.Tag, at + new System.Numerics.Vector3(0, 0, 0.05f));
    }

    // ---- prefabs ----------------------------------------------------------------------------------------

    public ObservableCollection<Prefab> Prefabs { get; } = new();
    [ObservableProperty] private Prefab? _selectedPrefab;

    private PrefabLibrary? PrefabStore()
    {
        if (Paths == null) return null;
        string folder = ModProjects?.Active is { } p ? Path.Combine(p.Folder, "prefabs") : Path.Combine(Paths.KaloData, "prefabs");
        return new PrefabLibrary(folder);
    }

    public void RefreshPrefabs()
    {
        var store = PrefabStore();
        Prefabs.Clear();
        if (store == null) return;
        foreach (var p in store.List()) Prefabs.Add(p);
    }

    public void SavePrefab(Prefab prefab)
    {
        var store = PrefabStore();
        if (store == null) return;
        store.Save(prefab);
        RefreshPrefabs();
        SelectedPrefab = Prefabs.FirstOrDefault(p => p.Name == prefab.Name);
        BottomPanel = "prefabs";
        Log($"Prefab \"{prefab.Name}\" saved ({prefab.Objects.Count} objects) in {store.Folder}.");
    }

    [RelayCommand]
    private async Task PlacePrefab(Prefab? prefab)
    {
        prefab ??= SelectedPrefab;
        if (prefab == null) return;
        if (ActiveMap == null) { AddProblem("warning", "Open a map first, then place prefabs in it.", "Prefabs"); return; }
        await ActiveMap.PlacePrefabAsync(prefab);
    }

    [RelayCommand]
    private void DeletePrefab(Prefab? prefab)
    {
        prefab ??= SelectedPrefab;
        if (prefab == null) return;
        PrefabStore()?.Delete(prefab.Name);
        RefreshPrefabs();
    }

    // ---- materials for the block tools --------------------------------------------------------------------

    public ObservableCollection<MaterialChoice> Materials { get; } = new();
    private bool _materialsLoaded;

    /// <summary>Reach's shared level materials (and Forge's), with their color map as the swatch.</summary>
    public async Task EnsureMaterialsAsync()
    {
        if (_materialsLoaded || Host == null) return;
        _materialsLoaded = true;
        var shaders = TagIndex
            .Where(t => t.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) &&
                        (t.StartsWith(@"levels\shared\shaders\", StringComparison.OrdinalIgnoreCase) || t.StartsWith(@"objects\levels\forge\shaders\", StringComparison.OrdinalIgnoreCase)))
            .Where(t => !t.Contains("decal") && !t.Contains("glass") && !t.Contains("_fx") && !t.Contains("illum"))
            .OrderBy(t => t)
            .Take(240)
            .ToList();
        try
        {
            var info = await Host.ShaderInfoAsync(shaders);
            var withMaps = (info.Shaders ?? new()).Where(s => !string.IsNullOrEmpty(s.BaseMap)).ToList();
            await Textures.LoadAsync(Host, withMaps.Select(s => s.BaseMap!));
            Materials.Clear();
            foreach (var s in withMaps)
            {
                var image = Textures.Get(s.BaseMap);
                if (image != null) Materials.Add(new MaterialChoice(s.Shader!, image));
            }
            Log($"{Materials.Count} materials ready for the block tools.");
        }
        catch (Exception ex)
        {
            _materialsLoaded = false;
            Log("Materials unavailable: " + ex.Message);
        }
    }
}

public sealed record ObjectCategory(string Key, string Title, int Count)
{
    public string Display => $"{Title}  {Count}";
}

public sealed class ObjectEntry(string tag)
{
    public string Tag { get; } = tag;
    public string Name { get; } = Path.GetFileNameWithoutExtension(tag);
    public string Kind { get; } = Path.GetExtension(tag).TrimStart('.');
    public string Folder { get; } = Path.GetDirectoryName(tag) ?? "";
}

/// <summary>A material the block tools can paint with: a shader and its color map (as a small swatch).</summary>
public sealed class MaterialChoice
{
    public MaterialChoice(string shader, TextureImage image)
    {
        Shader = shader;
        Name = Path.GetFileNameWithoutExtension(shader).Replace('_', ' ');
        Swatch = Swatches.From(image);
    }

    public string Shader { get; }
    public string Name { get; }
    public Avalonia.Media.Imaging.Bitmap? Swatch { get; }
}

internal static class Swatches
{
    /// <summary>An RGBA texture as an Avalonia bitmap (for swatches in lists).</summary>
    public static Avalonia.Media.Imaging.Bitmap? From(TextureImage image)
    {
        try
        {
            var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(image.Width, image.Height), new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Opaque);
            using var fb = bitmap.Lock();
            for (int y = 0; y < image.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(image.Rgba, y * image.Width * 4, fb.Address + y * fb.RowBytes, image.Width * 4);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
