using System.Collections.ObjectModel;
using System.Numerics;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// The map's object list as a tree: Sapien editor folders (saved in the scenario), then the objects that are in no
/// folder grouped by kind. Rows show whether an object is new or changed compared with the map it came from.
/// Also: copy / cut / paste, prefabs, and the selected object's own properties.
/// </summary>
public partial class MapEditorViewModel
{
    public List<EditorFolder> Folders { get; private set; } = new();
    public ObservableCollection<OutlinerNode> OutlinerRoots { get; } = new();
    public ObservableCollection<FolderChoice> FolderChoices { get; } = new();
    private readonly HashSet<string> _expanded = new() { "c:" + StartLocations.Category, "c:weapons", "c:vehicles", "c:equipment" };
    private readonly Dictionary<PlacementItem, OutlinerNode> _nodes = new();

    /// <summary>Raised when the tree was rebuilt or the selection moved, so the view can sync its selection.</summary>
    public event Action? OutlinerChanged;

    private static readonly (string Category, string Title)[] Groups =
    {
        (StartLocations.Category, "Start locations (first spawn)"),
        (CampaignCategories.TriggerVolumes, "Trigger volumes"), (CampaignCategories.AiSpawnPoints, "Squad spawn points"),
        (CampaignCategories.CutsceneFlags, "Cutscene flags"), (CampaignCategories.CutsceneCameras, "Cutscene cameras"),
        (CampaignCategories.ScriptPoints, "Script points"), ("weapons", "Weapons"), ("equipment", "Equipment"), ("vehicles", "Vehicles"), ("crates", "Crates and blocks"),
        ("scenery", "Scenery"), ("machines", "Machines"), ("controls", "Controls"), ("terminals", "Terminals"),
        ("bipeds", "Bipeds"), ("giants", "Giants"), ("effect scenery", "Effects"), ("sound scenery", "Sounds"),
    };

    /// <summary>Rebuilds the tree from OutlinerItems (already filtered). A search shows a flat list.</summary>
    private void BuildOutliner()
    {
        foreach (var n in EnumerateNodes(OutlinerRoots)) if (n.IsExpanded) _expanded.Add(n.Key); else _expanded.Remove(n.Key);
        OutlinerRoots.Clear();
        _nodes.Clear();
        FolderChoices.Clear();
        FolderChoices.Add(new FolderChoice(-1, "(no folder)"));
        if (Filter.Trim().Length > 0)
        {
            foreach (var item in OutlinerItems) OutlinerRoots.Add(ItemNode(item));
            OutlinerChanged?.Invoke();
            return;
        }
        var folderNodes = new Dictionary<int, OutlinerNode>();
        foreach (var f in Folders)
            folderNodes[f.Index] = new OutlinerNode("f:" + f.Index, f.Name ?? "folder", null, f.Index) { IsExpanded = _expanded.Contains("f:" + f.Index) };
        foreach (var f in Folders)
        {
            var node = folderNodes[f.Index];
            if (f.Parent >= 0 && folderNodes.TryGetValue(f.Parent, out var parent) && parent != node) parent.Children.Add(node);
            else OutlinerRoots.Add(node);
        }
        foreach (var f in Folders) FolderChoices.Add(new FolderChoice(f.Index, FolderPath(f)));
        var loose = new List<PlacementItem>();
        foreach (var item in OutlinerItems)
        {
            if (item.Folder >= 0 && folderNodes.TryGetValue(item.Folder, out var folder)) folder.Children.Add(ItemNode(item));
            else loose.Add(item);
        }
        foreach (var (category, title) in Groups)
        {
            var items = loose.Where(i => i.Category == category).ToList();
            if (items.Count == 0) continue;
            var group = new OutlinerNode("c:" + category, title, null, -1) { IsGroup = true, IsExpanded = _expanded.Contains("c:" + category) };
            foreach (var i in items) group.Children.Add(ItemNode(i));
            OutlinerRoots.Add(group);
        }
        foreach (var n in EnumerateNodes(OutlinerRoots).Where(n => !n.IsItem)) n.RefreshSummary();
        OutlinerChanged?.Invoke();
    }

    private OutlinerNode ItemNode(PlacementItem item)
    {
        var node = new OutlinerNode("i:" + item.Category + ":" + item.Index, item.Display, item, item.Folder);
        _nodes[item] = node;
        return node;
    }

    public OutlinerNode? NodeOf(PlacementItem? item) => item != null && _nodes.TryGetValue(item, out var n) ? n : null;

    /// <summary>Opens the folders above an object so its row can be shown.</summary>
    public void RevealInOutliner(PlacementItem item)
    {
        foreach (var root in OutlinerRoots) Reveal(root, item);
    }

    private static bool Reveal(OutlinerNode node, PlacementItem item)
    {
        if (node.Item == item) return true;
        foreach (var c in node.Children)
            if (Reveal(c, item)) { node.IsExpanded = true; return true; }
        return false;
    }

    private static IEnumerable<OutlinerNode> EnumerateNodes(IEnumerable<OutlinerNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in EnumerateNodes(n.Children)) yield return c;
        }
    }

    private string FolderPath(EditorFolder f)
    {
        var parts = new List<string>();
        var seen = new HashSet<int>();
        for (var cur = f; cur != null && seen.Add(cur.Index); cur = Folders.FirstOrDefault(x => x.Index == cur.Parent))
            parts.Insert(0, cur.Name ?? "folder");
        return string.Join(" / ", parts);
    }

    /// <summary>Objects selected in the tree (folders count as everything inside them).</summary>
    public void SelectFromOutliner(IReadOnlyList<OutlinerNode> nodes)
    {
        var items = nodes.SelectMany(n => n.IsItem ? new[] { n.Item! } : EnumerateNodes(n.Children).Where(c => c.IsItem).Select(c => c.Item!)).Distinct().ToList();
        SelectedFolder = nodes.Count == 1 && nodes[0].IsFolder ? nodes[0].FolderIndex : -1;
        SetSelection(items);
    }

    /// <summary>The editor folder picked in the tree (for New folder inside it, Rename, Delete), or -1.</summary>
    [ObservableProperty] private int _selectedFolder = -1;
    public bool HasSelectedFolder => SelectedFolder >= 0;
    partial void OnSelectedFolderChanged(int value) => OnPropertyChanged(nameof(HasSelectedFolder));

    /// <summary>Double-click in the list: put the camera in front of the object.</summary>
    public void GoTo(PlacementItem item)
    {
        Selected = item;
        FocusRequested?.Invoke(item.Center, item.PickRadius);
    }

    // ---- folders -----------------------------------------------------------------------------------

    public async Task NewFolderAsync(string name, int parent)
    {
        using var edit = await EditAsync("new folder");
        var r = await FolderOpAsync(new PlacementOpParams { Path = Path, Op = PlacementOps.AddFolder, Tag = name, Folder = parent });
        if (r == null) return;
        _expanded.Add("f:" + r.SelectIndex);
        if (parent >= 0) _expanded.Add("f:" + parent);
        // New folder from a selection: move the selected objects into it.
        if (_selection.Count > 0) await MoveSelectionToFolderAsync(r.SelectIndex);
        else BuildOutliner();
    }

    public async Task RenameFolderAsync(int folder, string name)
    {
        using var edit = await EditAsync("rename folder");
        if (folder < 0) return;
        await FolderOpAsync(new PlacementOpParams { Path = Path, Op = PlacementOps.RenameFolder, Folder = folder, Tag = name });
        BuildOutliner();
    }

    [RelayCommand]
    private async Task DeleteFolder()
    {
        using var edit = await EditAsync("delete folder");
        if (SelectedFolder < 0) return;
        var r = await FolderOpAsync(new PlacementOpParams { Path = Path, Op = PlacementOps.DeleteFolder, Folder = SelectedFolder });
        if (r?.Categories != null) foreach (var c in r.Categories) ReplaceCategory(c, rebuild: false);
        SelectedFolder = -1;
        ApplyFilter();
    }

    [RelayCommand]
    public async Task MoveSelectionToFolderAsync(int folder)
    {
        using var edit = await EditAsync("move to folder");
        if (_main.Host == null || _selection.Count == 0) return;
        var keys = _selection.Select(i => (i.Category, i.Index)).ToList();
        foreach (var (category, index) in keys)
        {
            try
            {
                var r = await _main.Host.PlacementOpAsync(new PlacementOpParams { Path = Path, Category = category, Op = PlacementOps.SetFolder, Index = index, Folder = folder });
                var item = Items.FirstOrDefault(i => i.Category == category && i.Index == index);
                var p = r.Category?.Items?.FirstOrDefault(x => x.Index == index);
                if (item != null && p != null) item.Update(p);
            }
            catch (HostException ex) { _main.AddProblem("error", "Move to folder: " + ex.Message, Path); }
        }
        IsDirty = true;
        if (folder >= 0) _expanded.Add("f:" + folder);
        BuildOutliner();
        foreach (var i in _selection) RevealInOutliner(i);
        OutlinerChanged?.Invoke();
    }

    private async Task<PlacementOpResult?> FolderOpAsync(PlacementOpParams p)
    {
        if (_main.Host == null) return null;
        try
        {
            var r = await _main.Host.PlacementOpAsync(p);
            if (r.Folders != null) Folders = r.Folders;
            IsDirty = true;
            return r;
        }
        catch (HostException ex)
        {
            _main.AddProblem("error", "Folders: " + ex.Message, Path);
            return null;
        }
    }

    // ---- new / changed compared with the map it came from -----------------------------------------

    [ObservableProperty] private string _changesText = "";
    private int _statusGeneration;

    /// <summary>Recomputes new / changed marks shortly after edits (several edits in a row trigger one refresh).</summary>
    public void QueueStatusRefresh()
    {
        RefreshOutsideSummary();
        int generation = ++_statusGeneration;
        _ = Task.Delay(600).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (generation == _statusGeneration) await RefreshStatesAsync();
        }));
    }

    public async Task RefreshStatesAsync()
    {
        if (_main.Host == null) return;
        try
        {
            string? baseline = _main.BaselineFileForMap(Path);
            var r = await _main.Host.PlacementStatusAsync(Path, baseline);
            int changed = 0, added = 0;
            foreach (var item in Items)
            {
                int state = r.States != null && r.States.TryGetValue(item.Category, out var list) && item.Index < list.Count ? list[item.Index] : PlacementStates.Same;
                item.State = baseline == null ? PlacementStates.Same : state;
                if (item.State == PlacementStates.Changed) changed++;
                if (item.State == PlacementStates.New) added++;
            }
            foreach (var n in EnumerateNodes(OutlinerRoots).Where(n => !n.IsItem)) n.RefreshSummary();
            ChangesText = baseline == null ? "" :
                added + changed + r.Removed == 0 ? "No object changes" :
                string.Join(" · ", new[] { added > 0 ? $"{added} new" : null, changed > 0 ? $"{changed} changed" : null, r.Removed > 0 ? $"{r.Removed} removed" : null }.Where(s => s != null));
        }
        catch (Exception ex)
        {
            _main.Log("Object change marks unavailable: " + ex.Message);
        }
    }

    // ---- copy, cut, paste, prefabs ------------------------------------------------------------------

    private static List<PrefabObject> s_clipboard = new();
    public static bool HasClipboard => s_clipboard.Count > 0;

    /// <summary>
    /// The selection as objects relative to its center on the ground, each with every field of its placement as it is
    /// now (scale, zone, label, team, spawn settings...), so pasting copies the object itself and not just its type.
    /// </summary>
    private async Task<List<PrefabObject>> SelectionAsObjectsAsync()
    {
        if (_selection.Count == 0) return new();
        var center = new Vector3(_selection.Average(i => i.Position.X), _selection.Average(i => i.Position.Y), _selection.Min(i => i.Position.Z));
        var list = new List<PrefabObject>();
        foreach (var i in _selection.ToList())
        {
            if (i.IsCampaignMarker) continue; // volumes, squad points...: duplicate them instead
            var o = new PrefabObject
            {
                Tag = i.Tag,
                Offset = new[] { i.Position.X - center.X, i.Position.Y - center.Y, i.Position.Z - center.Z },
                Rotation = new[] { i.Yaw, i.Pitch, i.Roll },
                SourceMap = Path,
            };
            if (_main.Host != null && i.IsObject)
            {
                try
                {
                    var r = await _main.Host.PlacementOpAsync(new PlacementOpParams { Path = Path, Category = i.Category, Op = PlacementOps.CopyElement, Index = i.Index });
                    o.Category = i.Category;
                    o.Data = r.Data;
                    if (!string.IsNullOrEmpty(r.Tag)) o.Tag = r.Tag;
                }
                catch (HostException ex) { _main.Log($"{Title}: copy of {i.Display} keeps only its type ({ex.Message})."); }
            }
            list.Add(o);
        }
        return list;
    }

    [RelayCommand]
    public async Task CopySelection()
    {
        if (_selection.Count == 0) return;
        s_clipboard = await SelectionAsObjectsAsync();
        Status = $"Copied {s_clipboard.Count} object{(s_clipboard.Count == 1 ? "" : "s")}.";
    }

    [RelayCommand]
    public async Task CutSelectionAsync()
    {
        using var edit = await EditAsync("cut");
        await CopySelection();
        await DeleteSelectedCommand.ExecuteAsync(null);
    }

    public async Task PasteAsync(Vector3? at = null)
    {
        if (s_clipboard.Count == 0) return;
        await PlaceObjectsAsync(s_clipboard, at ?? PlacementPointProvider?.Invoke() ?? Selected?.Position ?? Vector3.Zero, "Pasted");
    }

    [RelayCommand]
    private Task Paste() => PasteAsync();

    /// <summary>Places a group of objects (clipboard or prefab) around a point and selects them.</summary>
    public async Task PlaceObjectsAsync(IReadOnlyList<PrefabObject> objects, Vector3 at, string verb)
    {
        using var edit = await EditAsync("place");
        if (_main.Host == null) return;
        var placed = new List<(string Category, int Index)>();
        foreach (var o in objects)
        {
            var offset = o.Offset is { Length: >= 3 } v ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;
            if (o.Data != null && o.Category != null && CategoryFor(o.Tag) == o.Category)
            {
                // The whole copied placement (it already carries its rotation).
                var pasted = await PasteElementAsync(o, at + offset);
                if (pasted != null) { placed.Add((pasted.Category, pasted.Index)); continue; }
            }
            var item = await AddTagAtAsync(o.Tag, at + offset);
            if (item == null) continue;
            if (o.Rotation is { Length: >= 3 } r && (r[0] != 0 || r[1] != 0 || r[2] != 0))
            {
                item.SetRotationLocal(r[0], r[1], r[2]);
                await CommitMoveAsync(item);
            }
            placed.Add((item.Category, item.Index));
        }
        var items = Items.Where(i => placed.Contains((i.Category, i.Index))).ToList();
        SetSelection(items);
        Status = $"{verb} {items.Count} object{(items.Count == 1 ? "" : "s")}.";
        QueueStatusRefresh();
    }

    public async Task CreatePrefab(string name)
    {
        if (_selection.Count == 0 || string.IsNullOrWhiteSpace(name)) return;
        _main.SavePrefab(new Prefab { Name = name.Trim(), Created = DateTime.Now, Objects = await SelectionAsObjectsAsync() });
    }

    /// <summary>A copied placement, every field kept, as a new object at a point (host op pasteElement).</summary>
    private async Task<PlacementItem?> PasteElementAsync(PrefabObject o, Vector3 position)
    {
        if (_main.Host == null) return null;
        try
        {
            var r = await _main.Host.PlacementOpAsync(new PlacementOpParams
            {
                Path = Path, Category = o.Category, Op = PlacementOps.PasteElement, Tag = o.Tag, Data = o.Data,
                Position = new[] { position.X, position.Y, position.Z },
                Folder = string.Equals(o.SourceMap, Path, StringComparison.OrdinalIgnoreCase) ? -2 : -1,
            });
            if (r.Category != null) ReplaceCategory(r.Category);
            IsDirty = true;
            RefreshPalette();
            var item = Items.FirstOrDefault(i => i.Category == o.Category && i.Index == r.SelectIndex);
            if (item?.RenderModel != null && item.Model == null)
            {
                await _main.Models.LoadAsync(_main.Host, new[] { item.RenderModel });
                foreach (var i in Items.Where(i => i.RenderModel == item.RenderModel)) i.Model = _main.Models.Get(i.RenderModel);
                _ = EnsureTexturesAsync();
                SceneChanged?.Invoke();
            }
            return item;
        }
        catch (HostException ex)
        {
            _main.Log($"{Title}: pasting {System.IO.Path.GetFileName(o.Tag)} as a plain copy ({ex.Message}).");
            return null;
        }
    }

    public Task PlacePrefabAsync(Prefab prefab) =>
        PlaceObjectsAsync(prefab.Objects, PlacementPointProvider?.Invoke() ?? Selected?.Position ?? Vector3.Zero, "Placed " + prefab.Name + ":");

    // ---- the selected object's own properties ------------------------------------------------------------

    public ObservableCollection<PropertyRow> Properties { get; } = new();
    [ObservableProperty] private bool _hasProperties;
    private int _propertiesGeneration;

    private static readonly HashSet<string> HiddenProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "name", "position", "rotation", "unique id", "origin bsp index", "source", "bsp policy", "editor folder",
        "manual bsp flags", "canonical manual bsp index", "object id",
        // edited in the Zone section, with only the sizes its shape uses
        "boundary shape", "boundary width or radius", "boundary box length", "boundary positive height", "boundary negative height",
    };

    private async Task LoadPropertiesAsync()
    {
        int generation = ++_propertiesGeneration;
        var item = _selection.Count == 1 ? Selected : null;
        if (item == null || _main.Host == null) { Properties.Clear(); HasProperties = false; return; }
        try
        {
            var doc = item.Block != null
                ? await _main.Host.ReadElementAsync(Path, item.Block, item.BlockIndex)
                : await _main.Host.ReadElementAsync(Path, item.Category == StartLocations.Category ? StartLocations.Block : item.Category, item.Index);
            if (generation != _propertiesGeneration) return;
            Properties.Clear();
            void Walk(List<FieldNode>? nodes, string section)
            {
                if (nodes == null) return;
                foreach (var n in nodes)
                {
                    string name = n.Name ?? "";
                    if (HiddenProperties.Contains(name) || n.Visible == false && n.Kind != ValueKinds.Struct) continue;
                    if (n.Kind == ValueKinds.Struct || n.Kind == ValueKinds.Element)
                    {
                        string title = n.Kind == ValueKinds.Struct ? FieldLabels.For(n.Name, n.DisplayName) : section;
                        if (n.Kind == ValueKinds.Struct && n.Children is { Count: > 0 } && title != section)
                            Properties.Add(PropertyRow.Header(title));
                        Walk(n.Children, title);
                        continue;
                    }
                    if (n.Kind is ValueKinds.Block or ValueKinds.Array or ValueKinds.Explanation or ValueKinds.Other) continue;
                    if (!n.Editable) continue;
                    Properties.Add(new PropertyRow(this, n));
                }
            }
            Walk(doc.Fields, "");
            // Drop headers with nothing under them.
            for (int i = Properties.Count - 1; i >= 0; i--)
                if (Properties[i].IsHeader && (i == Properties.Count - 1 || Properties[i + 1].IsHeader)) Properties.RemoveAt(i);
            HasProperties = Properties.Count > 0;
        }
        catch (Exception ex)
        {
            Properties.Clear();
            HasProperties = false;
            _main.Log("Object properties unavailable: " + ex.Message);
        }
    }

    /// <summary>Names an object (Sapien's object names): shown in the object list, e.g. "hill_1" or "red_goal". Empty removes it.</summary>
    public async Task RenameAsync(PlacementItem item, string name)
    {
        if (_main.Host == null || item.Category == Kalo.Protocol.StartLocations.Category) return;
        name = (name ?? "").Trim();
        if (name == (item.PlacedName ?? "")) return;
        using var edit = await EditAsync("rename");
        try
        {
            var r = await _main.Host.PlacementOpAsync(new PlacementOpParams { Path = Path, Category = item.Category, Op = PlacementOps.Rename, Index = item.Index, Tag = name });
            var p = r.Category?.Items?.FirstOrDefault(x => x.Index == item.Index);
            if (p != null) item.Update(p);
            IsDirty = true;
            Status = name.Length == 0 ? $"{item.Name}: name removed." : $"Named {item.Name} \"{name}\".";
            BuildOutliner();
            RevealInOutliner(item);
            OutlinerChanged?.Invoke();
            QueueStatusRefresh();
        }
        catch (HostException ex)
        {
            _main.AddProblem("error", $"Rename {item.Display}: {ex.Message}", Path);
            item.ResetNameText();
        }
    }

    /// <summary>Changes one field of an object's objective zone (shape or a size), then reloads the object.</summary>
    public async Task SetZoneAsync(PlacementItem item, string field, string value)
    {
        if (_main.Host == null || !item.IsObject) return;
        using var edit = await EditAsync("change zone");
        string mp = $"Block:{item.Category}[{item.Index}]/Struct:multiplayer data[0]/";
        try
        {
            await _main.Host.SetFieldAsync(Path, mp + field, value.Trim());
            // A new zone with no size gets a visible default: 3 units around, 2 up, 1 down.
            if (field.StartsWith("CharEnum") && value != "0" && item.ZoneSize.X <= 0.001f)
            {
                await _main.Host.SetFieldAsync(Path, mp + "Real:boundary width or radius", "3");
                await _main.Host.SetFieldAsync(Path, mp + "Real:boundary box length", "3");
                await _main.Host.SetFieldAsync(Path, mp + "Real:boundary positive height", "2");
                await _main.Host.SetFieldAsync(Path, mp + "Real:boundary negative height", "1");
            }
            IsDirty = true;
            var p = await _main.Host.SetPlacementAsync(new SetPlacementParams { Path = Path, Category = item.Category, Index = item.Index });
            item.Update(p);
            SceneChanged?.Invoke();
            QueueStatusRefresh();
        }
        catch (HostException ex)
        {
            _main.AddProblem("error", $"Zone of {item.Display}: {ex.Message}", Path);
        }
    }

    public async Task<string?> SetPropertyAsync(PropertyRow row, string value)
    {
        using var edit = await EditAsync("change property");
        var item = Selected;
        if (_main.Host == null || item == null) return "No object.";
        try
        {
            var r = await _main.Host.SetFieldAsync(Path, row.Path, value);
            IsDirty = true;
            var p = await _main.Host.SetPlacementAsync(new SetPlacementParams { Path = Path, Category = item.Category, Index = item.Index });
            item.Update(p);
            SceneChanged?.Invoke();
            QueueStatusRefresh();
            return null;
        }
        catch (HostException ex)
        {
            return ex.Message;
        }
    }

    [RelayCommand]
    private async Task SetScale(string text)
    {
        using var edit = await EditAsync("scale");
        if (Selected == null || _main.Host == null) return;
        if (!float.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float scale) || scale <= 0) return;
        foreach (var item in _selection.ToList())
        {
            var p = await _main.Host.SetPlacementAsync(new SetPlacementParams { Path = Path, Category = item.Category, Index = item.Index, Scale = scale });
            item.Update(p);
        }
        IsDirty = true;
        SceneChanged?.Invoke();
        QueueStatusRefresh();
    }
}

/// <summary>A row of the object list: an editor folder, a group of loose objects of one kind, or an object.</summary>
public partial class OutlinerNode : ObservableObject
{
    public OutlinerNode(string key, string name, PlacementItem? item, int folderIndex)
    {
        Key = key;
        Name = name;
        Item = item;
        FolderIndex = item == null ? folderIndex : -1;
        if (item != null) item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PlacementItem.State)) { OnPropertyChanged(nameof(StateBrush)); OnPropertyChanged(nameof(StateText)); }
            if (e.PropertyName is nameof(PlacementItem.Display)) OnPropertyChanged(nameof(Name));
        };
    }

    public string Key { get; }
    public string Name { get; }
    public PlacementItem? Item { get; }
    public int FolderIndex { get; }
    public bool IsItem => Item != null;
    public bool IsFolder => Item == null && !IsGroup;
    public bool IsGroup { get; init; }
    public ObservableCollection<OutlinerNode> Children { get; } = new();
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private IBrush? _groupBrush;

    public string Icon => IsItem ? "" : IsGroup ? "" : "";
    public string Detail => Item?.Detail ?? "";
    public IBrush? StateBrush => Item?.StateBrush ?? GroupBrush;
    public string StateText => Item?.StateText ?? "";

    /// <summary>Folder rows: how many objects, and whether any of them changed.</summary>
    public void RefreshSummary()
    {
        var items = All().ToList();
        int changed = items.Count(i => i.State != PlacementStates.Same);
        Summary = changed > 0 ? $"{items.Count} · {changed} edited" : items.Count.ToString();
        GroupBrush = changed > 0 ? PlacementItem.ChangedBrush : null;
        OnPropertyChanged(nameof(StateBrush));
    }

    private IEnumerable<PlacementItem> All()
    {
        foreach (var c in Children)
        {
            if (c.Item != null) yield return c.Item;
            foreach (var i in c.All()) yield return i;
        }
    }
}

public sealed record FolderChoice(int Index, string Path);

/// <summary>One editable property of the selected object (or a section header).</summary>
public partial class PropertyRow : ObservableObject
{
    private readonly MapEditorViewModel? _map;
    private bool _suppress;

    public static PropertyRow Header(string title) => new(title);

    private PropertyRow(string title)
    {
        Label = title.ToUpperInvariant();
        IsHeader = true;
        Path = "";
        Options = Array.Empty<string>();
    }

    public PropertyRow(MapEditorViewModel map, FieldNode node)
    {
        _map = map;
        Path = node.Path ?? "";
        Label = FieldLabels.For(node.Name, node.DisplayName);
        Description = node.Description;
        Kind = node.Kind ?? "";
        Options = node.Kind == ValueKinds.Enum && node.Options is { Count: > 0 } o ? o : Array.Empty<string>();
        _suppress = true;
        Value = node.Value ?? "";
        if (IsEnum) SelectedOption = Options.ToList().FindIndex(x => string.Equals(x, Value, StringComparison.OrdinalIgnoreCase)) is int i and >= 0 ? i : int.TryParse(Value, out int n) ? n : -1;
        _suppress = false;
    }

    public string Path { get; }
    public string Label { get; }
    public string? Description { get; }
    public string Kind { get; } = "";
    public bool IsHeader { get; }
    public IReadOnlyList<string> Options { get; }
    public bool IsEnum => !IsHeader && Options.Count > 0;
    public bool IsText => !IsHeader && Options.Count == 0;

    [ObservableProperty] private string _value = "";
    [ObservableProperty] private int _selectedOption = -1;
    [ObservableProperty] private string? _error;

    partial void OnValueChanged(string value)
    {
        if (_suppress || _map == null || IsEnum) return;
        _ = CommitAsync(value);
    }

    partial void OnSelectedOptionChanged(int value)
    {
        if (_suppress || _map == null || value < 0) return;
        _ = CommitAsync(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task CommitAsync(string value)
    {
        Error = await _map!.SetPropertyAsync(this, value);
    }
}
