using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// Undo / redo for map edits: before each edit the Blam Host keeps a snapshot of the scenario's placement blocks
/// (objects, palettes, folders, names, start locations); undo puts the previous one back and reloads the list.
/// Also the map's keyboard shortcuts, shared by the 3D view and the window (so Ctrl+C works from the object list).
/// </summary>
public partial class MapEditorViewModel
{
    private readonly List<(int Id, string What, List<string>? Categories)> _undo = new();
    private readonly List<(int Id, string What, List<string>? Categories)> _redo = new();
    private int _editDepth;
    private bool _undoing;

    /// <summary>The scenario blocks holding a campaign category (what an undo step of that category keeps).</summary>
    private static string? BlockOf(string category) => category switch
    {
        CampaignCategories.AiSpawnPoints => "squads",
        CampaignCategories.TriggerVolumes => "trigger volumes",
        CampaignCategories.CutsceneFlags => "cutscene flags",
        CampaignCategories.CutsceneCameras => "cutscene camera points",
        CampaignCategories.ScriptPoints => "scripting data",
        _ => null,
    };

    /// <summary>The campaign categories of these items, or null when any is an object (then the whole map is snapshot).</summary>
    public static List<string>? CampaignScope(IEnumerable<PlacementItem> items)
    {
        var list = items.ToList();
        if (list.Count == 0 || list.Any(i => !i.IsCampaignMarker)) return null;
        return list.Select(i => i.Category).Distinct().ToList();
    }

    // ---- busy: what the map is doing, shown under the view --------------------------------------------
    [ObservableProperty] private string _busyText = "";
    public bool IsBusy => BusyText.Length > 0;
    partial void OnBusyTextChanged(string value) => OnPropertyChanged(nameof(IsBusy));
    private int _busyDepth;

    /// <summary>Shows "text…" under the map view until disposed (nested: the outermost text stays).</summary>
    public IDisposable Busy(string text)
    {
        if (_busyDepth++ == 0) BusyText = text;
        return new BusyScope(this);
    }

    private sealed class BusyScope(MapEditorViewModel map) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--map._busyDepth <= 0) { map._busyDepth = 0; map.BusyText = ""; }
        }
    }

    [ObservableProperty] private string _undoText = "";
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Call at the start of an edit. Nested calls (paste → add → move) record one step.
    /// Dispose the result when the edit is done.
    /// </summary>
    public async Task<IDisposable> EditAsync(string what, List<string>? categories = null)
    {
        var busy = Busy(char.ToUpperInvariant(what[0]) + what[1..] + "…");
        if (_editDepth++ == 0 && _main.Host != null)
        {
            try
            {
                var blocks = categories?.Select(BlockOf).ToList();
                if (blocks != null && blocks.Any(b => b == null)) { blocks = null; categories = null; }
                var snap = await _main.Host.SnapshotAsync(Path, blocks?.Distinct().ToList()!);
                if (snap.Ms > 300) _main.Trace($"{Title}: undo step for {what} took {snap.Ms} ms ({snap.Bytes / 1024:N0} KB).");
                _undo.Add((snap.Id, what, categories));
                if (_undo.Count > 60) _undo.RemoveAt(0);
                _redo.Clear();
                RefreshUndo();
            }
            catch (Exception ex)
            {
                _main.Log("Undo step not recorded: " + ex.Message);
            }
        }
        return new EditScope(this, busy);
    }

    private sealed class EditScope(MapEditorViewModel map, IDisposable busy) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            map._editDepth = Math.Max(0, map._editDepth - 1);
            busy.Dispose();
        }
    }

    [RelayCommand]
    public async Task UndoAsync()
    {
        if (_undo.Count == 0 || _main.Host == null) return;
        // One at a time: a second Ctrl+Z while the first runs would restore over a half-reloaded list.
        if (_undoing || _editDepth > 0) { Status = "Wait: " + (BusyText.Length > 0 ? BusyText : "still working…"); return; }
        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        await SwapAsync(step, _redo, "Undid");
    }

    [RelayCommand]
    public async Task RedoAsync()
    {
        if (_redo.Count == 0 || _main.Host == null) return;
        if (_undoing || _editDepth > 0) { Status = "Wait: " + (BusyText.Length > 0 ? BusyText : "still working…"); return; }
        var step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        await SwapAsync(step, _undo, "Redid");
    }

    /// <summary>Snapshots the current state onto the other stack, then puts the step's state back.</summary>
    private async Task SwapAsync((int Id, string What, List<string>? Categories) step, List<(int Id, string What, List<string>? Categories)> other, string verb)
    {
        _undoing = true;
        using var busy = Busy((verb == "Undid" ? "Undoing " : "Redoing ") + step.What + "…");
        RefreshUndo();
        try
        {
            var blocks = step.Categories?.Select(BlockOf).Distinct().ToList();
            var now = await _main.Host!.SnapshotAsync(Path, blocks!);
            other.Add((now.Id, step.What, step.Categories));
            await _main.Host.RestoreAsync(Path, step.Id);
            IsDirty = true;
            if (step.Categories != null)
            {
                // Only what the step touched (a whole mission takes ~13 s to read again).
                var r = await _main.Host.CategoriesAsync(Path, step.Categories);
                if (r.Squads != null) _squads = r.Squads;
                Selected = null;
                foreach (var c in r.Categories ?? new()) ReplaceCategory(c);
            }
            else await ReloadAsync();
            Status = $"{verb}: {step.What}";
        }
        catch (Exception ex)
        {
            _main.AddProblem("error", $"{verb} failed: {ex.Message}", Path);
        }
        finally
        {
            _undoing = false;
        }
        RefreshUndo();
    }

    private void RefreshUndo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoText = _undo.Count > 0 ? "Undo " + _undo[^1].What : "";
    }

    /// <summary>
    /// The map's shortcuts (from AppSettings), for the 3D view and the window: the action for this key, or null.
    /// Decided synchronously so the caller can mark the key handled before running it. Flying stays in the view.
    /// </summary>
    public Func<Task>? ShortcutFor(KeyEventArgs e, Func<System.Numerics.Vector3>? placementPoint = null)
    {
        var keys = AppSettings.Current;
        bool has = Selected != null;
        Func<Task> Do(Action a) => () => { a(); return Task.CompletedTask; };
        if (keys.Matches("undo", e)) return UndoAsync;
        if (keys.Matches("redo", e) || keys.Matches("redoAlt", e)) return RedoAsync;
        if (has && keys.Matches("frameSelection", e)) return Do(() => FocusSelectedCommand.Execute(null));
        if (keys.Matches("frameAll", e)) return Do(() => FrameAllCommand.Execute(null));
        if (has && keys.Matches("delete", e)) return () => DeleteSelectedCommand.ExecuteAsync(null);
        if (keys.Matches("selectAll", e)) return Do(() => SetSelection(OutlinerItems.ToList()));
        if (has && keys.Matches("duplicate", e)) return () => DuplicateSelectedCommand.ExecuteAsync(null);
        if (has && keys.Matches("copy", e)) return CopySelection;
        if (has && keys.Matches("cut", e)) return CutSelectionAsync;
        if (keys.Matches("paste", e)) return () => PasteAsync(placementPoint?.Invoke() ?? PlacementPointProvider?.Invoke());
        if (has && keys.Matches("turn", e)) return () => RotateSelectedAsync(15f);
        if (has && keys.Matches("turnBack", e)) return () => RotateSelectedAsync(-15f);
        if (keys.Matches("wireframe", e)) return Do(() => Shading = ViewShading.Wireframe);
        if (keys.Matches("shading", e)) return Do(() => Shading = (ViewShading)(((int)Shading + 1) % 3));
        if (keys.Matches("deselect", e)) return Do(() => Selected = null);
        return null;
    }
}
