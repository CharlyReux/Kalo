using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kalo.App.ViewModels;

namespace Kalo.App.Views;

public partial class MapEditorView : UserControl
{
    private MapEditorViewModel? _vm;
    private bool _syncing;

    public MapEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.OutlinerChanged -= SyncFromModel;
            _vm = DataContext as MapEditorViewModel;
            if (_vm != null) _vm.OutlinerChanged += SyncFromModel;
            SyncFromModel();
        };
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    /// <summary>Selection made in the 3D view → highlight the same rows (opening their folders).</summary>
    private void SyncFromModel()
    {
        if (_vm == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm == null) return;
            _syncing = true;
            try
            {
                var wanted = _vm.Selection.Select(i => _vm.NodeOf(i)).OfType<OutlinerNode>().ToList();
                foreach (var item in _vm.Selection) _vm.RevealInOutliner(item);
                var current = Outliner.SelectedItems.OfType<OutlinerNode>().ToList();
                bool foldersOnly = current.Count > 0 && current.All(n => !n.IsItem) && wanted.Count == 0;
                if (!foldersOnly && !current.SequenceEqual(wanted))
                {
                    Outliner.SelectedItems.Clear();
                    foreach (var n in wanted) Outliner.SelectedItems.Add(n);
                }
                if (wanted.Count > 0) Outliner.ScrollIntoView(wanted[^1]);
            }
            catch (Exception)
            {
                // The tree can be rebuilt while syncing; the next change syncs again.
            }
            finally
            {
                _syncing = false;
            }
        }, DispatcherPriority.Background);
    }

    private void Outliner_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm == null) return;
        _vm.SelectFromOutliner(Outliner.SelectedItems.OfType<OutlinerNode>().ToList());
    }

    private void Outliner_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm == null) return;
        if ((e.Source as Control)?.DataContext is OutlinerNode { Item: { } item }) _vm.GoTo(item);
    }

    private void GoTo_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm?.Selected is { } item) _vm.GoTo(item);
    }

    private async void NewFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || Owner == null) return;
        string hint = _vm.Selection.Count > 0 ? $"The {_vm.Selection.Count} selected object(s) will go in it." : "Folders are saved in the map (Sapien uses them too).";
        string? name = await Dialogs.PromptAsync(Owner, "New folder", "Folder name. " + hint);
        if (name != null) await _vm.NewFolderAsync(name, _vm.SelectedFolder);
    }

    private void Outliner_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2 || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        if (_vm?.Selection.Count == 1) Rename_Click(sender, new RoutedEventArgs());
        else if (_vm?.SelectedFolder >= 0) RenameFolder_Click(sender, new RoutedEventArgs());
    }

    /// <summary>Names the selected object (e.g. a hill "hill_1"): its row in the list shows the name.</summary>
    private async void Rename_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || Owner == null) return;
        if (_vm.Selection.Count != 1 || _vm.Selected is not { } item)
        {
            if (_vm.SelectedFolder >= 0) RenameFolder_Click(sender, e);
            else _vm.Status = "Select one object to rename.";
            return;
        }
        if (item.Category == Kalo.Protocol.StartLocations.Category) { _vm.Status = "Start locations have no name."; return; }
        string? name = await Dialogs.PromptAsync(Owner, "Rename", $"Name for this {item.Name} (up to 31 characters). Empty: no name.", item.PlacedName ?? "");
        if (name != null) await _vm.RenameAsync(item, name);
    }

    private async void RenameFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || Owner == null || _vm.SelectedFolder < 0) return;
        string current = _vm.Folders.FirstOrDefault(f => f.Index == _vm.SelectedFolder)?.Name ?? "";
        string? name = await Dialogs.PromptAsync(Owner, "Rename folder", "New name", current);
        if (name != null) await _vm.RenameFolderAsync(_vm.SelectedFolder, name);
    }

    private async void CreatePrefab_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || Owner == null) return;
        if (_vm.Selection.Count == 0) { _vm.Status = "Select the objects to make a prefab from."; return; }
        string? name = await Dialogs.PromptAsync(Owner, "New prefab", $"Prefab name ({_vm.Selection.Count} object(s)). You will find it in the Prefabs tab.");
        if (name != null) _vm.CreatePrefab(name);
    }
}
