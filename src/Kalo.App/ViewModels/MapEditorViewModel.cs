using System.Collections.ObjectModel;
using System.Numerics;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Core.Geometry;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// A scenario opened as a map: level geometry plus every object placement, editable in the 3D view.
/// Edits go to the scenario tag in the Blam Host (shared with a tag editor tab on the same scenario)
/// and reach the disk on Save, like any tag.
/// </summary>
public partial class MapEditorViewModel : DocumentViewModel
{
    private readonly MainViewModel _main;
    private readonly Dictionary<string, PlacementCategory> _categories = new();

    public MapEditorViewModel(MainViewModel main, string scenarioPath) : base(scenarioPath)
    {
        _main = main;
        Status = "Loading…";
        AppSettings.Changed += () => OnPropertyChanged(nameof(KeyHint));
    }

    public override string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
    public override string Badge => "map";
    /// <summary>The help line under the view, from the current key settings.</summary>
    public string KeyHint => AppSettings.Current.MapHint();
    /// <summary>Scenario path without extension, as reach_tag_test wants it.</summary>
    public string Scenario => Path[..^".scenario".Length];

    public ObservableCollection<PlacementItem> Items { get; } = new();
    public ObservableCollection<PlacementItem> OutlinerItems { get; } = new();
    public ObservableCollection<PaletteChoice> PaletteChoices { get; } = new();
    public IReadOnlyList<PlacementItem> VisibleItems { get; private set; } = Array.Empty<PlacementItem>();

    public Mesh? Bsp { get; private set; }
    public int GeometryVersion { get; private set; }
    /// <summary>Last point clicked on the level geometry (where "Add" puts new objects).</summary>
    public Vector3? CursorPoint { get; set; }
    /// <summary>Set by the 3D view: where a new object should go (the surface in front of the camera).</summary>
    public Func<Vector3>? PlacementPointProvider { get; set; }
    /// <summary>Set by the 3D view: where "Play from camera" starts (ground under the camera) and the facing.</summary>
    public Func<(Vector3 Position, float FacingDegrees)>? PlayFromProvider { get; set; }

    public event Action? SceneChanged;
    public event Action<Vector3, float>? FocusRequested;
    public event Action? FrameAllRequested;
    /// <summary>Camera at a point, looking along a yaw (degrees, 0 = +x) and pitch.</summary>
    public event Action<Vector3, float, float>? CameraRequested;

    /// <summary>The 3D view saves its next frame to this file (the view only, as drawn by the GPU).</summary>
    public event Action<string, TaskCompletionSource<bool>>? CaptureRequested;

    public Task<bool> CaptureViewAsync(string path)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (CaptureRequested == null) done.TrySetResult(false);
        else { CaptureRequested.Invoke(path, done); SceneChanged?.Invoke(); }
        return done.Task;
    }

    /// <summary>The scenario's first player starting location and its facing (degrees).</summary>
    public (Vector3 Position, float Facing)? Spawn { get; private set; }
    public bool HasSpawn => Spawn != null;

    /// <summary>Puts the camera where a player spawns, at eye height, looking the way they face: the view you get in game.</summary>
    public void LookFrom(Vector3 eye, float yawDegrees, float pitchDegrees) => CameraRequested?.Invoke(eye, yawDegrees, pitchDegrees);

    [RelayCommand]
    private void ViewFromSpawn()
    {
        if (Spawn is { } s) CameraRequested?.Invoke(s.Position + new Vector3(0, 0, 0.62f), s.Facing, 0f);
    }

    [RelayCommand]
    private void FrameAll() => FrameAllRequested?.Invoke();

    // ---- viewport shading (like Blender's: wireframe, solid, material preview) ---------------------

    /// <summary>The last shading picked, used for the next map opened.</summary>
    private static ViewShading s_lastShading = ViewShading.Textured;
    private static bool s_lastWire;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWireframe), nameof(IsSolid), nameof(IsTextured))]
    private ViewShading _shading = s_lastShading;
    [ObservableProperty] private bool _wireOverlay = s_lastWire;
    [ObservableProperty] private string _textureStatus = "";

    public bool IsWireframe { get => Shading == ViewShading.Wireframe; set { if (value) Shading = ViewShading.Wireframe; } }
    public bool IsSolid { get => Shading == ViewShading.Solid; set { if (value) Shading = ViewShading.Solid; } }
    public bool IsTextured { get => Shading == ViewShading.Textured; set { if (value) Shading = ViewShading.Textured; } }

    /// <summary>Bumped whenever more textures are ready, so the view retries the ones it had no image for.</summary>
    public int TexturesVersion { get; private set; }
    public TextureImage? TextureFor(string? bitmap) => string.IsNullOrEmpty(bitmap) ? null : _main.Textures.Get(bitmap);

    partial void OnShadingChanged(ViewShading value)
    {
        s_lastShading = value;
        if (value == ViewShading.Textured) _ = EnsureTexturesAsync();
        SceneChanged?.Invoke();
    }

    partial void OnWireOverlayChanged(bool value)
    {
        s_lastWire = value;
        SceneChanged?.Invoke();
    }

    [RelayCommand]
    private void SetShading(string name)
    {
        if (Enum.TryParse<ViewShading>(name, true, out var s)) Shading = s;
    }

    private readonly HashSet<string> _texturesRequested = new(StringComparer.OrdinalIgnoreCase);
    private bool _texturesLoading, _texturesAgain;
    public bool TexturesLoading => _texturesLoading;

    /// <summary>
    /// Decodes the color maps of the level and of the loaded object models (once; cached on disk after that).
    /// Safe to call often: it only asks for bitmaps it has not asked for yet.
    /// </summary>
    public async Task EnsureTexturesAsync()
    {
        if (Shading != ViewShading.Textured || _main.Host == null) return;
        if (_texturesLoading) { _texturesAgain = true; return; }
        _texturesLoading = true;
        try
        {
            do
            {
                _texturesAgain = false;
                var meshes = new List<Mesh>();
                if (Bsp != null) meshes.Add(Bsp);
                meshes.AddRange(Items.Select(i => i.Model).OfType<Mesh>().Distinct());
                var wanted = meshes.SelectMany(m => m.Materials).SelectMany(m => m.Bitmaps())
                    .Where(b => !string.IsNullOrEmpty(b) && _texturesRequested.Add(b)).ToList();
                if (wanted.Count == 0) continue;
                TextureStatus = $"Loading {wanted.Count} textures…";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await _main.Textures.LoadAsync(_main.Host, wanted);
                    int ready = wanted.Count(b => _main.Textures.Get(b) != null);
                    TextureStatus = "";
                    _main.Log($"{Title}: {ready}/{wanted.Count} textures ready in {sw.ElapsedMilliseconds} ms." +
                              (ready < wanted.Count ? " Shown plain: " + string.Join("; ", _main.Textures.LastErrors.Take(4)) : ""));
                }
                catch (Exception ex)
                {
                    TextureStatus = "Textures unavailable: " + ex.Message;
                    _main.Log("Textures unavailable: " + ex.Message);
                }
                TexturesVersion++;
                SceneChanged?.Invoke();
            } while (_texturesAgain);
        }
        finally
        {
            _texturesLoading = false;
        }
    }

    [ObservableProperty] private PlacementItem? _selected;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _geometryStatus = "";
    [ObservableProperty] private string _rendererStatus = "";
    [ObservableProperty] private PaletteChoice? _selectedPalette;
    [ObservableProperty] private bool _showGeometry = true;
    [ObservableProperty] private bool _showScenery = true;
    [ObservableProperty] private bool _showItems = true;
    [ObservableProperty] private bool _showVehicles = true;
    [ObservableProperty] private bool _showDevices = true;
    [ObservableProperty] private bool _showSoundAndEffects;

    public bool HasSelection => Selected != null;

    // ---- multi-selection ----------------------------------------------------------------------
    // Selected is the primary object (inspector); Selection holds everything Ctrl+click added.

    private readonly List<PlacementItem> _selection = new();
    private bool _keepSelection;
    public IReadOnlyList<PlacementItem> Selection => _selection;
    public bool IsSelected(PlacementItem item) => _selection.Contains(item);
    public bool HasMultiSelection => _selection.Count > 1;
    public string SelectionText => $"{_selection.Count} objects selected. Drag, R, Ctrl+D and Delete apply to all of them; Ctrl+click adds or removes one.";

    /// <summary>Ctrl+click: adds the object to the selection, or removes it.</summary>
    public void ToggleSelection(PlacementItem item)
    {
        _keepSelection = true;
        if (_selection.Contains(item))
        {
            _selection.Remove(item);
            Selected = _selection.LastOrDefault();
        }
        else
        {
            _selection.Add(item);
            Selected = item;
        }
        _keepSelection = false;
        SelectionChanged();
    }

    /// <summary>Makes an already selected object the one the inspector shows, keeping the group.</summary>
    public void MakePrimary(PlacementItem item)
    {
        _keepSelection = true;
        Selected = item;
        _keepSelection = false;
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(HasMultiSelection));
        OnPropertyChanged(nameof(SelectionText));
        SceneChanged?.Invoke();
        _ = LoadPropertiesAsync();
        if (!_syncingFromOutliner) OutlinerChanged?.Invoke();
    }

    private bool _syncingFromOutliner;

    /// <summary>Replaces the selection (from the object list); the last object becomes the primary one.</summary>
    public void SetSelection(IReadOnlyList<PlacementItem> items)
    {
        _syncingFromOutliner = true;
        _keepSelection = true;
        _selection.Clear();
        _selection.AddRange(items);
        Selected = items.LastOrDefault();
        _keepSelection = false;
        SelectionChanged();
        _syncingFromOutliner = false;
    }

    /// <summary>Moves every selected object by the same offset from where it was when the drag started.</summary>
    public void MoveSelectionLocal(IReadOnlyDictionary<PlacementItem, Vector3> start, Vector3 delta)
    {
        foreach (var (item, origin) in start) item.SetPositionLocal(origin + delta);
        SceneChanged?.Invoke();
    }

    public void RotateSelectionLocal(IReadOnlyDictionary<PlacementItem, float> startYaw, float delta)
    {
        foreach (var (item, yaw) in startYaw) item.SetRotationLocal(yaw + delta, item.Pitch, item.Roll);
        SceneChanged?.Invoke();
    }

    public async Task CommitSelectionAsync()
    {
        using var edit = await EditAsync("move");
        foreach (var item in _selection.ToList()) await CommitMoveAsync(item);
    }

    // ---- drag & drop from the project tree ------------------------------------------------------

    /// <summary>Placement block for an object tag, from its extension (weapon → weapons...). Null if it cannot be placed.</summary>
    public static string? CategoryFor(string tagPath) => System.IO.Path.GetExtension(tagPath).TrimStart('.').ToLowerInvariant() switch
    {
        "weapon" => "weapons",
        "vehicle" => "vehicles",
        "scenery" => "scenery",
        "crate" => "crates",
        "equipment" => "equipment",
        "device_machine" => "machines",
        "device_control" => "controls",
        "device_terminal" => "terminals",
        "biped" => "bipeds",
        "giant" => "giants",
        "effect_scenery" => "effect scenery",
        "sound_scenery" => "sound scenery",
        _ => null,
    };

    /// <summary>Places any object tag at a point: adds it to the right palette first if needed.</summary>
    public async Task<PlacementItem?> AddTagAtAsync(string tagPath, Vector3 position)
    {
        using var edit = await EditAsync("add object");
        string? category = CategoryFor(tagPath);
        if (category == null || _main.Host == null)
        {
            Status = $"{System.IO.Path.GetFileName(tagPath)} is not an object that can be placed.";
            return null;
        }
        try
        {
            var pal = await _main.Host.PlacementOpAsync(new PlacementOpParams { Path = Path, Category = category, Op = PlacementOps.AddPalette, Tag = tagPath });
            if (pal.Category != null) ReplaceCategory(pal.Category);
            var add = await _main.Host.PlacementOpAsync(new PlacementOpParams { Path = Path, Category = category, Op = PlacementOps.Add, Type = pal.SelectIndex, Position = ToArray(position) });
            if (add.Category != null) ReplaceCategory(add.Category);
            IsDirty = true;
            RefreshPalette();
            var item = Items.FirstOrDefault(i => i.Category == category && i.Index == add.SelectIndex);
            Selected = item;
            if (item?.RenderModel != null && item.Model == null)
            {
                await _main.Models.LoadAsync(_main.Host, new[] { item.RenderModel });
                foreach (var i in Items.Where(i => i.RenderModel == item.RenderModel)) i.Model = _main.Models.Get(i.RenderModel);
                _ = EnsureTexturesAsync();
                SceneChanged?.Invoke();
            }
            _main.Log($"{Title}: placed {System.IO.Path.GetFileName(tagPath)} in {category}.");
            return item;
        }
        catch (HostException ex)
        {
            _main.AddProblem("error", $"Place {tagPath}: {ex.Message}", Path);
            return null;
        }
    }

    private void RefreshPalette()
    {
        PaletteChoices.Clear();
        foreach (var c in _categories.Values)
            for (int i = 0; i < (c.Palette?.Count ?? 0); i++)
                if (!string.IsNullOrEmpty(c.Palette![i].Tag)) PaletteChoices.Add(new PaletteChoice(c.Name!, i, c.Palette[i].Tag!));
    }

    public void ReportRenderer(string text) => Avalonia.Threading.Dispatcher.UIThread.Post(() => RendererStatus = text);

    // ---- loading ---------------------------------------------------------------------------------

    public async Task ReloadAsync()
    {
        if (_main.Host == null)
        {
            // Opened right after launch: wait for the host instead of showing an empty map.
            Status = "Waiting for the Blam Host…";
            if (await _main.WaitForHostAsync() == null)
            {
                Status = "The Blam Host is not running: the map could not be read. Don't edit it here; reopen it once the host runs.";
                _main.AddProblem("error", $"{Title}: the Blam Host is not running, so the map could not be read.", Path);
                return;
            }
        }
        try
        {
            var data = await _main.Host!.GetPlacementsAsync(Path);
            bool isModMap = Path.StartsWith(@"levels\mods\", StringComparison.OrdinalIgnoreCase);
            if (isModMap && await RebuildMissingBlocksAsync(data) > 0) data = await _main.Host.GetPlacementsAsync(Path);
            if (isModMap)
            {
                // Objects placed by older Kalo versions had blank multiplayer settings and never spawned in game.
                var repair = await _main.Host.PlacementOpAsync(new PlacementOpParams { Path = Path, Op = PlacementOps.RepairMultiplayer });
                if (repair.SelectIndex > 0)
                {
                    data = await _main.Host.GetPlacementsAsync(Path);
                    IsDirty = true;
                    _main.Log($"{Title}: {repair.SelectIndex} object(s) placed by an earlier Kalo now have multiplayer settings, so they spawn in game. Save the map to keep this.");
                }
                if (repair.Detached > 0)
                {
                    // Maps made by an earlier Kalo from a Reach level kept that level's object resource files, so the
                    // game showed the level's original objects and none of the ones edited here.
                    IsDirty = true;
                    _main.Log($"{Title}: this map was still reading its objects from the original level's resource files, so the game ignored the objects placed in Kalo. Fixed: save the map, then Play shows your objects.");
                    _main.AddProblem("warning", $"{Title}: save the map once so the game uses the objects placed in Kalo (it was still using the original level's).", Path);
                }
            }
            int keepCategory = -1;
            string? selCat = Selected?.Category; int selIndex = Selected?.Index ?? -1;
            var start = data.SpawnPoints?.FirstOrDefault(p => p.Position is { Length: >= 3 });
            Spawn = start == null ? null : (new Vector3(start.Position![0], start.Position[1], start.Position[2]), start.Rotation is { Length: > 0 } r ? r[0] : 0f);
            OnPropertyChanged(nameof(HasSpawn));
            Items.Clear();
            _categories.Clear();
            PaletteChoices.Clear();
            foreach (var c in data.Categories ?? new())
            {
                _categories[c.Name!] = c;
                foreach (var p in c.Items ?? new()) Items.Add(new PlacementItem(this, c, p));
                for (int i = 0; i < (c.Palette?.Count ?? 0); i++)
                    if (!string.IsNullOrEmpty(c.Palette![i].Tag)) PaletteChoices.Add(new PaletteChoice(c.Name!, i, c.Palette[i].Tag!));
            }
            _ = keepCategory;
            Folders = data.Folders ?? new();
            Selected = Items.FirstOrDefault(i => i.Category == selCat && i.Index == selIndex);
            Status = $"{Items.Count:N0} objects · {data.Bsps?.Count ?? 0} bsp · loaded in {data.Ms} ms";
            ApplyFilter();
            _ = LoadGeometryAsync(data.Bsps ?? new());
            _ = LoadModelsAsync(data);
            _ = RefreshStatesAsync();
        }
        catch (Exception ex)
        {
            Status = "Could not read the scenario: " + ex.Message;
            _main.AddProblem("error", Status, Path);
        }
    }

    private async Task LoadGeometryAsync(List<string> bsps)
    {
        if (Bsp != null) return;
        GeometryStatus = "Loading level geometry…";
        try
        {
            var mesh = await _main.Geometry.LoadLevelAsync(Path, bsps, s => Avalonia.Threading.Dispatcher.UIThread.Post(() => GeometryStatus = s));
            Bsp = mesh;
            GeometryVersion++;
            foreach (var item in Items) item.RefreshOutside();
            RefreshOutsideSummary();
            GeometryStatus = mesh == null ? "No level geometry (objects only)." : $"{mesh.TriangleCount:N0} triangles";
            _main.Log($"{Title}: level geometry {GeometryStatus}");
            SceneChanged?.Invoke();
            _ = EnsureTexturesAsync();
        }
        catch (Exception ex)
        {
            GeometryStatus = "Level geometry unavailable: " + ex.Message;
            _main.Log($"{Title}: {GeometryStatus}");
        }
    }

    public int ModelVersion { get; private set; }

    /// <summary>
    /// True when a point is off the level geometry: beyond its footprint (1 unit of slack) or more than
    /// 2 units below its lowest point. Reach removes objects out there when the game starts.
    /// </summary>
    public bool IsOutsidePlayArea(Vector3 p)
    {
        var bsp = Bsp;
        if (bsp == null || bsp.TriangleCount == 0) return false;
        const float slack = 1f;
        return p.X < bsp.Min.X - slack || p.X > bsp.Max.X + slack || p.Y < bsp.Min.Y - slack || p.Y > bsp.Max.Y + slack || p.Z < bsp.Min.Z - 2f;
    }

    /// <summary>"3 objects are outside the map…" for the toolbar and Play, or "".</summary>
    public string OutsideText
    {
        get
        {
            int n = Items.Count(i => i.IsOutside);
            return n == 0 ? "" : $"{n} object{(n == 1 ? " is" : "s are")} outside the level geometry: Reach removes them in game (and players spawning there die). Move them over the floor.";
        }
    }
    public bool HasOutside => OutsideText.Length > 0;

    public void RefreshOutsideSummary()
    {
        OnPropertyChanged(nameof(OutsideText));
        OnPropertyChanged(nameof(HasOutside));
    }

    private async Task LoadModelsAsync(ScenarioPlacements data)
    {
        var models = (data.Categories ?? new()).SelectMany(c => c.Palette ?? new()).Select(p => p.RenderModel).OfType<string>().ToList();
        if (models.Count == 0) return;
        try
        {
            int loaded = await _main.Models.LoadAsync(_main.Host, models);
            foreach (var item in Items) item.Model = _main.Models.Get(item.RenderModel);
            ModelVersion++;
            _main.Log($"{Title}: {loaded} object models ready.");
            SceneChanged?.Invoke();
            _ = EnsureTexturesAsync();
        }
        catch (Exception ex)
        {
            _main.Log("Object models unavailable (boxes are shown instead): " + ex.Message);
        }
    }

    // ---- outliner and layers -----------------------------------------------------------------------

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnShowGeometryChanged(bool value) => SceneChanged?.Invoke();
    partial void OnShowSceneryChanged(bool value) => ApplyFilter();
    partial void OnShowItemsChanged(bool value) => ApplyFilter();
    partial void OnShowVehiclesChanged(bool value) => ApplyFilter();
    partial void OnShowDevicesChanged(bool value) => ApplyFilter();
    partial void OnShowSoundAndEffectsChanged(bool value) => ApplyFilter();

    partial void OnSelectedChanged(PlacementItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (!_keepSelection)
        {
            _selection.Clear();
            if (value != null) _selection.Add(value);
            SelectionChanged();
        }
        SceneChanged?.Invoke();
    }

    private bool LayerVisible(string category) => category switch
    {
        StartLocations.Category => true,
        "scenery" or "crates" => ShowScenery,
        "weapons" or "equipment" => ShowItems,
        "vehicles" or "bipeds" or "giants" => ShowVehicles,
        "machines" or "controls" or "terminals" => ShowDevices,
        _ => ShowSoundAndEffects,
    };

    private void ApplyFilter()
    {
        string[] words = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var visible = Items.Where(i => LayerVisible(i.Category)).ToList();
        VisibleItems = visible;
        OutlinerItems.Clear();
        foreach (var i in visible)
            if (words.All(w => i.SearchText.Contains(w, StringComparison.OrdinalIgnoreCase))) OutlinerItems.Add(i);
        BuildOutliner();
        SceneChanged?.Invoke();
    }

    // ---- editing --------------------------------------------------------------------------------------

    public void MoveLocal(PlacementItem item, Vector3 position)
    {
        item.SetPositionLocal(position);
        SceneChanged?.Invoke();
    }

    public async Task CommitMoveAsync(PlacementItem item)
    {
        using var edit = await EditAsync("move");
        if (_main.Host == null) return;
        try
        {
            var p = await _main.Host.SetPlacementAsync(new SetPlacementParams
            {
                Path = Path, Category = item.Category, Index = item.Index,
                Position = new[] { item.Position.X, item.Position.Y, item.Position.Z },
                Rotation = new[] { item.Yaw, item.Pitch, item.Roll }
            });
            item.Update(p);
            IsDirty = true;
            SceneChanged?.Invoke();
            QueueStatusRefresh();
        }
        catch (HostException ex)
        {
            _main.AddProblem("error", $"Move {item.Display}: {ex.Message}", Path);
        }
    }

    public async Task RotateSelectedAsync(float degrees)
    {
        using var edit = await EditAsync("turn");
        foreach (var item in _selection.ToList())
        {
            item.SetRotationLocal(item.Yaw + degrees, item.Pitch, item.Roll);
            await CommitMoveAsync(item);
        }
    }

    /// <summary>The selection as (category, index), highest index first, so earlier operations do not shift later ones.</summary>
    private List<(string Category, int Index, Vector3 Position, float Radius)> SelectionKeys() =>
        _selection.Select(i => (i.Category, i.Index, i.Position, i.PickRadius)).OrderBy(k => k.Category).ThenByDescending(k => k.Index).ToList();

    [RelayCommand]
    private async Task DuplicateSelected()
    {
        using var edit = await EditAsync("duplicate");
        if (_selection.Count == 0) return;
        float offset = Math.Max(0.5f, _selection.Max(i => i.PickRadius) * 2f);
        foreach (var k in SelectionKeys())
            await RunOpAsync(new PlacementOpParams { Path = Path, Category = k.Category, Op = PlacementOps.Duplicate, Index = k.Index, Position = ToArray(k.Position + new Vector3(offset, 0, 0)) }, k.Category);
    }

    [RelayCommand]
    private async Task DeleteSelected()
    {
        using var edit = await EditAsync("delete");
        if (_selection.Count == 0) return;
        foreach (var k in SelectionKeys())
            await RunOpAsync(new PlacementOpParams { Path = Path, Category = k.Category, Op = PlacementOps.Remove, Index = k.Index }, k.Category);
        Selected = null;
    }

    /// <summary>Places the chosen palette object on the surface in front of the camera.</summary>
    [RelayCommand]
    private async Task AddObject()
    {
        using var edit = await EditAsync("add object");
        if (SelectedPalette == null) { Status = "Pick an object type first."; return; }
        Vector3 at = PlacementPointProvider?.Invoke() ?? CursorPoint ?? Selected?.Position ?? Bsp?.Center ?? Vector3.Zero;
        at += new Vector3(0, 0, 0.1f);
        await RunOpAsync(new PlacementOpParams { Path = Path, Category = SelectedPalette.Category, Op = PlacementOps.Add, Type = SelectedPalette.Index, Position = ToArray(at) }, SelectedPalette.Category);
    }

    [RelayCommand]
    private void FocusSelected()
    {
        if (Selected != null) FocusRequested?.Invoke(Selected.Center, Selected.PickRadius);
    }

    [RelayCommand]
    private async Task CommitSelected()
    {
        if (Selected != null) await CommitMoveAsync(Selected);
    }

    private async Task RunOpAsync(PlacementOpParams p, string category)
    {
        if (_main.Host == null) return;
        try
        {
            var r = await _main.Host.PlacementOpAsync(p);
            if (r.Category != null) ReplaceCategory(r.Category);
            IsDirty = true;
            Selected = r.SelectIndex >= 0 ? Items.FirstOrDefault(i => i.Category == category && i.Index == r.SelectIndex) : null;
            _main.Log($"{Title}: {p.Op} {category} → {r.Category?.Items?.Count} placed");
        }
        catch (HostException ex)
        {
            _main.AddProblem("error", $"{p.Op} {category}: {ex.Message}", Path);
        }
    }

    private void ReplaceCategory(PlacementCategory c) => ReplaceCategory(c, rebuild: true);

    private void ReplaceCategory(PlacementCategory c, bool rebuild)
    {
        _categories[c.Name!] = c;
        int at = Items.ToList().FindIndex(i => i.Category == c.Name);
        foreach (var old in Items.Where(i => i.Category == c.Name).ToList()) Items.Remove(old);
        if (at < 0 || at > Items.Count) at = Items.Count;
        foreach (var p in c.Items ?? new())
        {
            var item = new PlacementItem(this, c, p);
            item.Model = _main.Models.Get(item.RenderModel);
            Items.Insert(at++, item);
        }
        if (rebuild) ApplyFilter();
        QueueStatusRefresh();
    }

    [RelayCommand]
    private async Task Save()
    {
        if (_main.Host == null) return;
        try
        {
            var r = await _main.Host.SaveTagAsync(Path);
            IsDirty = false;
            Status = _main.IsGameRunning
                ? $"Saved in {r.Ms} ms. The running game keeps the old placements: press Play (F5) to restart it."
                : $"Saved in {r.Ms} ms";
            _main.OnDocumentSaved(this);
        }
        catch (HostException ex)
        {
            _main.AddProblem("error", "Save failed: " + ex.Message, Path);
        }
    }

    public override Task SaveDocumentAsync() => SaveCommand.ExecuteAsync(null);

    public override async Task CloseAsync()
    {
        try { if (_main.Host != null) await _main.Host.CloseTagAsync(Path); } catch { }
    }

    [RelayCommand]
    private async Task OpenFields() => await _main.OpenTagAsync(Path);

    [RelayCommand]
    private Task PlayHere() => _main.PlayScenarioAsync(Scenario);

    /// <summary>Play this map starting where the editor camera is (on the ground below it), without moving the Start location.</summary>
    [RelayCommand]
    private async Task PlayFromCamera()
    {
        if (PlayFromProvider == null) { Status = "Open the 3D view first."; return; }
        var (position, facing) = PlayFromProvider();
        await _main.PlayFromAsync(this, position, facing);
    }

    private static float[] ToArray(Vector3 v) => new[] { v.X, v.Y, v.Z };
}

/// <summary>How the map view draws surfaces.</summary>
public enum ViewShading { Wireframe, Solid, Textured }

public sealed record PaletteChoice(string Category, int Index, string Tag)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Tag);
    public string Display => $"{Name}  ·  {Category}";
}

/// <summary>One object placement in the map view and outliner.</summary>
public partial class PlacementItem : ObservableObject
{
    private readonly MapEditorViewModel _map;
    private bool _suppress;

    public string Category { get; }
    public int Index { get; private set; }
    public string Tag { get; }
    public string? RenderModel { get; }
    /// <summary>The object's model, once loaded; the map view draws a box until then.</summary>
    public Kalo.Core.Geometry.Mesh? Model { get; set; }
    public float PlacedScale { get; private set; } = 1;
    public string ScaleText => PlacedScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    public string Name { get; }
    public string? PlacedName { get; private set; }
    public string? Label { get; private set; }
    public float Radius { get; }
    public Vector3 Offset { get; }
    public Vector3 Color { get; }
    public Vector3 Position { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public float Roll { get; private set; }
    public int Folder { get; private set; } = -1;
    public int UniqueId { get; private set; }

    public static readonly IBrush NewBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#5FB865"));
    public static readonly IBrush ChangedBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#E5A50A"));

    /// <summary>Compared with the map it came from: PlacementStates.Same, Changed or New.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBrush), nameof(StateText))]
    private int _state;
    public static readonly IBrush OutsideBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#E5534B"));
    public IBrush? StateBrush => IsOutside ? OutsideBrush : State == PlacementStates.New ? NewBrush : State == PlacementStates.Changed ? ChangedBrush : null;
    public string StateText => IsOutside ? "outside map" : State == PlacementStates.New ? "new" : State == PlacementStates.Changed ? "edited" : "";

    /// <summary>
    /// Outside the level geometry's footprint (or below it): Reach deletes such objects when the game starts,
    /// and a player spawning there dies. Only known once the level geometry is loaded.
    /// </summary>
    public bool IsOutside => _map.IsOutsidePlayArea(Position);

    /// <summary>Re-evaluates the outside mark (after a move, or once the level geometry loads).</summary>
    public void RefreshOutside()
    {
        OnPropertyChanged(nameof(IsOutside));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(StateText));
    }

    public PlacementItem(MapEditorViewModel map, PlacementCategory category, Placement p)
    {
        _map = map;
        Category = category.Name ?? "";
        var entry = p.Type >= 0 && p.Type < (category.Palette?.Count ?? 0) ? category.Palette![p.Type] : null;
        Tag = entry?.Tag ?? "";
        RenderModel = entry?.RenderModel;
        Name = Tag.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(Tag) : "(no type)";
        // Small objects still get a box you can see and click.
        Radius = Math.Clamp(entry?.BoundingRadius ?? 0.5f, 0.3f, 30f);
        var o = entry?.BoundingOffset;
        Offset = o is { Length: >= 3 } ? new Vector3(o[0], o[1], o[2]) : Vector3.Zero;
        Color = ColorFor(Category, Tag);
        Update(p);
    }

    public void Update(Placement p)
    {
        _suppress = true;
        Index = p.Index;
        PlacedName = p.Name;
        NameText = p.Name ?? "";
        Label = p.Label;
        var pos = p.Position is { Length: >= 3 } ? new Vector3(p.Position[0], p.Position[1], p.Position[2]) : Vector3.Zero;
        Position = pos;
        var r = p.Rotation ?? new float[3];
        Yaw = r.Length > 0 ? r[0] : 0; Pitch = r.Length > 1 ? r[1] : 0; Roll = r.Length > 2 ? r[2] : 0;
        PlacedScale = p.Scale > 0.001f ? p.Scale : 1f;
        Folder = p.Folder;
        UniqueId = p.UniqueId;
        ZoneShape = Math.Clamp(p.Shape, 0, 3);
        var zs = p.ShapeSize is { Length: >= 4 } ? p.ShapeSize : new float[4];
        ZoneSize = new Vector4(zs[0], zs[1], zs[2], zs[3]);
        ZoneShapeIndex = ZoneShape;
        ZoneWidth = Fmt(zs[0]); ZoneLength = Fmt(zs[1]); ZoneTop = Fmt(zs[2]); ZoneBottom = Fmt(zs[3]);
        OnPropertyChanged(nameof(HasZone)); OnPropertyChanged(nameof(IsBoxZone)); OnPropertyChanged(nameof(IsSphereZone)); OnPropertyChanged(nameof(ZoneWidthLabel));
        X = Fmt(pos.X); Y = Fmt(pos.Y); Z = Fmt(pos.Z);
        YawText = Fmt(Yaw); PitchText = Fmt(Pitch); RollText = Fmt(Roll);
        _suppress = false;
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(ScaleText));
        RefreshOutside();
    }

    public string Display => PlacedName is { Length: > 0 } ? $"{PlacedName} ({Name})" : Name;
    public string Detail => $"{Category} [{Index}]" + (string.IsNullOrEmpty(Label) ? "" : " · " + Label);
    public string SearchText => $"{Display} {Category} {Tag} {Label}";
    /// <summary>Middle of the object: its model's bounds when loaded, else the tag's bounding sphere.</summary>
    public Vector3 Center => Model != null ? Vector3.Transform(Model.Center, ModelMatrix) : Position + Vector3.Transform(Offset, RotationMatrix);
    /// <summary>Radius used for clicking and framing.</summary>
    public float PickRadius => Model != null ? Math.Max(0.15f, (Model.Max - Model.Min).Length() * 0.5f * PlacedScale) : Radius;

    /// <summary>Unit cube (−1..1) → the selection outline around the model (or the bounding box).</summary>
    public Matrix4x4 OutlineMatrix => Model != null
        ? Matrix4x4.CreateScale(Vector3.Max((Model.Max - Model.Min) * 0.5f, new Vector3(0.02f))) * Matrix4x4.CreateTranslation(Model.Center) * ModelMatrix
        : BoxMatrix;

    /// <summary>Halo euler angles (degrees): roll about x, then pitch about y (positive = nose up), then yaw about z.</summary>
    // ---- objective zone (multiplayer boundary) ----

    /// <summary>0 none, 1 sphere, 2 cylinder, 3 box.</summary>
    public int ZoneShape { get; private set; }
    /// <summary>Width or radius, box length, height above, depth below (world units).</summary>
    public Vector4 ZoneSize { get; private set; }
    public bool HasZone => ZoneShape > 0;
    public bool IsBoxZone => ZoneShape == 3;
    public bool IsSphereZone => ZoneShape == 1;
    public string ZoneWidthLabel => ZoneShape == 3 ? "Width" : "Radius";
    [ObservableProperty] private int _zoneShapeIndex;
    [ObservableProperty] private string _zoneWidth = "";
    [ObservableProperty] private string _zoneLength = "";
    [ObservableProperty] private string _zoneTop = "";
    [ObservableProperty] private string _zoneBottom = "";


    /// <summary>The object's name (inspector box); typing one renames it.</summary>
    [ObservableProperty] private string _nameText = "";
    partial void OnNameTextChanged(string value) { if (!_suppress) _ = _map.RenameAsync(this, value); }
    internal void ResetNameText()
    {
        _suppress = true;
        NameText = PlacedName ?? "";
        _suppress = false;
    }

    partial void OnZoneShapeIndexChanged(int value)
    {
        if (_suppress || value < 0 || value == ZoneShape) return;
        _ = _map.SetZoneAsync(this, "CharEnum:boundary shape", Math.Clamp(value, 0, 3).ToString());
    }
    partial void OnZoneWidthChanged(string value) { if (!_suppress) _ = _map.SetZoneAsync(this, "Real:boundary width or radius", value); }
    partial void OnZoneLengthChanged(string value) { if (!_suppress) _ = _map.SetZoneAsync(this, "Real:boundary box length", value); }
    partial void OnZoneTopChanged(string value) { if (!_suppress) _ = _map.SetZoneAsync(this, "Real:boundary positive height", value); }
    partial void OnZoneBottomChanged(string value) { if (!_suppress) _ = _map.SetZoneAsync(this, "Real:boundary negative height", value); }

    /// <summary>Object space (rotation + position, no scale) → world: zones are drawn in it.</summary>
    public Matrix4x4 PoseMatrix => RotationMatrix * Matrix4x4.CreateTranslation(Position);

    private Matrix4x4 RotationMatrix =>
        Matrix4x4.CreateRotationX(Deg(Roll)) * Matrix4x4.CreateRotationY(-Deg(Pitch)) * Matrix4x4.CreateRotationZ(Deg(Yaw));

    /// <summary>Unit cube (−1..1) → the object's bounding box in the world.</summary>
    public Matrix4x4 BoxMatrix
    {
        get
        {
            float s = Radius * 0.7f;
            return Matrix4x4.CreateScale(s) * Matrix4x4.CreateTranslation(Offset) * RotationMatrix * Matrix4x4.CreateTranslation(Position);
        }
    }

    /// <summary>Model space → world.</summary>
    public Matrix4x4 ModelMatrix => Matrix4x4.CreateScale(PlacedScale) * RotationMatrix * Matrix4x4.CreateTranslation(Position);

    // Inspector fields (text, committed when the box loses focus).
    [ObservableProperty] private string _x = "";
    [ObservableProperty] private string _y = "";
    [ObservableProperty] private string _z = "";
    [ObservableProperty] private string _yawText = "";
    [ObservableProperty] private string _pitchText = "";
    [ObservableProperty] private string _rollText = "";

    partial void OnXChanged(string value) => FromText();
    partial void OnYChanged(string value) => FromText();
    partial void OnZChanged(string value) => FromText();
    partial void OnYawTextChanged(string value) => FromText();
    partial void OnPitchTextChanged(string value) => FromText();
    partial void OnRollTextChanged(string value) => FromText();

    private void FromText()
    {
        if (_suppress) return;
        if (!TryF(X, out float x) || !TryF(Y, out float y) || !TryF(Z, out float z) ||
            !TryF(YawText, out float yaw) || !TryF(PitchText, out float pitch) || !TryF(RollText, out float roll)) return;
        Position = new Vector3(x, y, z);
        Yaw = yaw; Pitch = pitch; Roll = roll;
        RefreshOutside();
        _ = _map.CommitMoveAsync(this);
    }

    public void SetPositionLocal(Vector3 p)
    {
        Position = p;
        _suppress = true;
        X = Fmt(p.X); Y = Fmt(p.Y); Z = Fmt(p.Z);
        _suppress = false;
        RefreshOutside();
    }

    public void SetRotationLocal(float yaw, float pitch, float roll)
    {
        Yaw = ((yaw % 360) + 360) % 360; Pitch = pitch; Roll = roll;
        _suppress = true;
        YawText = Fmt(Yaw); PitchText = Fmt(Pitch); RollText = Fmt(Roll);
        _suppress = false;
    }

    private static float Deg(float d) => d * MathF.PI / 180f;
    private static string Fmt(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    private static bool TryF(string s, out float v) =>
        float.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);

    private static Vector3 ColorFor(string category, string tag)
    {
        if (category == StartLocations.Category) return new(0.4f, 0.95f, 1f);
        if (tag.Contains("respawn_point", StringComparison.OrdinalIgnoreCase)) return new(0.35f, 0.85f, 0.55f);
        return category switch
        {
            "weapons" => new(0.95f, 0.55f, 0.2f),
            "equipment" => new(0.3f, 0.8f, 0.9f),
            "vehicles" => new(0.45f, 0.75f, 0.3f),
            "crates" => new(0.7f, 0.5f, 0.32f),
            "scenery" => new(0.45f, 0.55f, 0.75f),
            "machines" or "controls" or "terminals" => new(0.7f, 0.45f, 0.85f),
            "bipeds" or "giants" => new(0.9f, 0.4f, 0.45f),
            _ => new(0.5f, 0.5f, 0.5f),
        };
    }
}
