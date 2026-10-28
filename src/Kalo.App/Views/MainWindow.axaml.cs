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
        Opened += OnOpened;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.OutputLines.CollectionChanged += OnOutputChanged;
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

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

    private async void Changes_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm != null && ChangesList.SelectedItem is ChangeItem c) await Vm.OpenNodeCommand.ExecuteAsync(c);
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
        if (!string.IsNullOrEmpty(args.SelfTest) && Vm != null)
        {
            int code = await new SelfTest(this, Vm, args).RunAsync();
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
