using System.Collections.ObjectModel;
using System.Numerics;
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
    }

    public override string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
    public override string Badge => "map";
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

    public event Action? SceneChanged;
    public event Action<Vector3, float>? FocusRequested;
    public event Action? FrameAllRequested;

    [RelayCommand]
    private void FrameAll() => FrameAllRequested?.Invoke();

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
        if (_main.Host == null) { Status = "The Blam Host is not running."; return; }
        try
        {
            var data = await _main.Host.GetPlacementsAsync(Path);
            int keepCategory = -1;
            string? selCat = Selected?.Category; int selIndex = Selected?.Index ?? -1;
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
            Selected = Items.FirstOrDefault(i => i.Category == selCat && i.Index == selIndex);
            Status = $"{Items.Count:N0} objects · {data.Bsps?.Count ?? 0} bsp · loaded in {data.Ms} ms";
            ApplyFilter();
            _ = LoadGeometryAsync(data.Bsps ?? new());
            _ = LoadModelsAsync(data);
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
            GeometryStatus = mesh == null ? "No level geometry (objects only)." : $"{mesh.TriangleCount:N0} triangles";
            SceneChanged?.Invoke();
        }
        catch (Exception ex)
        {
            GeometryStatus = "Level geometry unavailable: " + ex.Message;
        }
    }

    public int ModelVersion { get; private set; }

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
        }
        catch (HostException ex)
        {
            _main.AddProblem("error", $"Move {item.Display}: {ex.Message}", Path);
        }
    }

    public async Task RotateSelectedAsync(float degrees)
    {
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
        if (_selection.Count == 0) return;
        float offset = Math.Max(0.5f, _selection.Max(i => i.PickRadius) * 2f);
        foreach (var k in SelectionKeys())
            await RunOpAsync(new PlacementOpParams { Path = Path, Category = k.Category, Op = PlacementOps.Duplicate, Index = k.Index, Position = ToArray(k.Position + new Vector3(offset, 0, 0)) }, k.Category);
    }

    [RelayCommand]
    private async Task DeleteSelected()
    {
        if (_selection.Count == 0) return;
        foreach (var k in SelectionKeys())
            await RunOpAsync(new PlacementOpParams { Path = Path, Category = k.Category, Op = PlacementOps.Remove, Index = k.Index }, k.Category);
        Selected = null;
    }

    /// <summary>Places the chosen palette object where the level was last clicked (or in front of the selection).</summary>
    [RelayCommand]
    private async Task AddObject()
    {
        if (SelectedPalette == null) { Status = "Pick an object type first."; return; }
        Vector3 at = CursorPoint ?? Selected?.Position ?? Bsp?.Center ?? Vector3.Zero;
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

    private void ReplaceCategory(PlacementCategory c)
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
        ApplyFilter();
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

    private static float[] ToArray(Vector3 v) => new[] { v.X, v.Y, v.Z };
}

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
        Label = p.Label;
        var pos = p.Position is { Length: >= 3 } ? new Vector3(p.Position[0], p.Position[1], p.Position[2]) : Vector3.Zero;
        Position = pos;
        var r = p.Rotation ?? new float[3];
        Yaw = r.Length > 0 ? r[0] : 0; Pitch = r.Length > 1 ? r[1] : 0; Roll = r.Length > 2 ? r[2] : 0;
        PlacedScale = p.Scale > 0.001f ? p.Scale : 1f;
        X = Fmt(pos.X); Y = Fmt(pos.Y); Z = Fmt(pos.Z);
        YawText = Fmt(Yaw); PitchText = Fmt(Pitch); RollText = Fmt(Roll);
        _suppress = false;
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(Detail));
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
        _ = _map.CommitMoveAsync(this);
    }

    public void SetPositionLocal(Vector3 p)
    {
        Position = p;
        _suppress = true;
        X = Fmt(p.X); Y = Fmt(p.Y); Z = Fmt(p.Z);
        _suppress = false;
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
