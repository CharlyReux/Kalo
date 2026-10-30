using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Kalo.App.ViewModels;

namespace Kalo.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Tags can be dragged from the project tree and the search results (onto the map view).
        foreach (var source in new Control[] { ExplorerTree, SearchList, ObjectList })
        {
            source.AddHandler(PointerPressedEvent, DragSource_Pressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
            source.AddHandler(PointerMovedEvent, DragSource_Moved, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        }
        Opened += OnOpened;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.OutputLines.CollectionChanged += OnOutputChanged;
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private Point? _dragStart;
    private string? _dragTag;

    private void DragSource_Pressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStart = null;
        _dragTag = (e.Source as StyledElement)?.DataContext switch
        {
            ExplorerNode { IsFolder: false, TagPath: { } t } => t,
            SearchResult r => r.TagPath,
            ObjectEntry o => o.Tag,
            _ => null,
        };
        if (_dragTag != null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) _dragStart = e.GetPosition(this);
    }

    private async void DragSource_Moved(object? sender, PointerEventArgs e)
    {
        if (_dragStart == null || _dragTag == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.Value.X) + Math.Abs(p.Y - _dragStart.Value.Y) < 8) return;
        _dragStart = null;
        var data = new DataObject();
        data.Set(DataFormats.Text, _dragTag);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
    }

    private void OnOutputChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || Vm == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (Vm.OutputLines.Count > 0) OutputList.ScrollIntoView(Vm.OutputLines.Count - 1);
        }, DispatcherPriority.Background);
    }

    private async void Explorer_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm != null && ExplorerTree.SelectedItem is ExplorerNode node && !node.IsFolder)
            await Vm.OpenNodeCommand.ExecuteAsync(node);
    }

    private async void Search_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm != null && SearchList.SelectedItem is SearchResult r) await Vm.OpenNodeCommand.ExecuteAsync(r);
    }

    private async void Search_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm != null && SearchList.SelectedItem is SearchResult r) await Vm.OpenNodeCommand.ExecuteAsync(r);
    }

    private async void Scripts_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm != null && ScriptList.SelectedItem is ScriptFile s) await Vm.OpenScriptAsync(s.RelativePath);
    }

    private async void Changes_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm != null && ChangesList.SelectedItem is ChangeItem c) await Vm.OpenNodeCommand.ExecuteAsync(c);
    }

    private async void Reference_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm?.ActiveTab != null && ReferenceList.SelectedItem is string tag)
            await Vm.ActiveTab.PickReferenceCommand.ExecuteAsync(tag);
    }

    private async void Settings_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await Dialogs.ShowSettingsAsync(this);

    private async void ObjectList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm != null && ObjectList.SelectedItem is ObjectEntry) await Vm.AddSelectedObjectCommand.ExecuteAsync(null);
    }

    private async void Prefab_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm?.SelectedPrefab != null) await Vm.PlacePrefabCommand.ExecuteAsync(Vm.SelectedPrefab);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.P && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Vm?.ShowExplorerCommand.Execute(null);
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    /// <summary>--screenshot: once Kalo is ready, render this window (only this window) to a PNG.</summary>
    private async void OnOpened(object? sender, EventArgs e)
    {
        var args = AppArgs.Current;
        if ((!string.IsNullOrEmpty(args.SelfTest) || !string.IsNullOrEmpty(args.Tour)) && Vm != null)
        {
            int code = args.BarebonePlay ? await new Tour(this, Vm, args).BarebonePlayAsync() : args.ModPlay ? await new Tour(this, Vm, args).ModPlayAsync() : args.MegaloPlay ? await new Tour(this, Vm, args).MegaloPlayAsync() : args.MapPlay ? await new Tour(this, Vm, args).MapPlayAsync() : !string.IsNullOrEmpty(args.Tour) ? await new Tour(this, Vm, args).RunAsync() : await new SelfTest(this, Vm, args).RunAsync();
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime life)
                life.Shutdown(code);
            return;
        }
        if (string.IsNullOrEmpty(args.Screenshot) || Vm == null) return;
        await Task.WhenAny(Vm.Ready, Task.Delay(TimeSpan.FromSeconds(60)));
        await Task.Delay(TimeSpan.FromSeconds(args.ScreenshotDelay));
        SaveShot(args.Screenshot);
        if (args.Exit) Close();
    }

    /// <summary>Renders this window (only this window) to a PNG.</summary>
    public bool SaveShot(string path)
    {
        try
        {
            var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            bitmap.Render(this);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            bitmap.Save(path);
            Console.WriteLine("Screenshot saved: " + path);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Screenshot failed: " + ex.Message);
            return false;
        }
    }
}
