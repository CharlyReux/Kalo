using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kalo.App.ViewModels;

namespace Kalo.App.Views;

/// <summary>
/// One editor group. A click or focus inside it makes it the focused group (the inspector follows);
/// tabs can be dragged onto the other group, or moved with their right-click menu.
/// </summary>
public partial class EditorGroupView : UserControl
{
    /// <summary>In-process drag format carrying a DocumentViewModel.</summary>
    public const string TabFormat = "kalo/tab";

    private Point? _dragStart;
    private DocumentViewModel? _dragDoc;

    public EditorGroupView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, (_, _) => FocusMe(), RoutingStrategies.Tunnel, true);
        AddHandler(GotFocusEvent, (_, _) => FocusMe(), RoutingStrategies.Bubble, true);
        TabStrip.AddHandler(PointerPressedEvent, Tab_Pressed, RoutingStrategies.Tunnel, true);
        TabStrip.AddHandler(PointerMovedEvent, Tab_Moved, RoutingStrategies.Tunnel, true);
        TabStrip.AddHandler(PointerReleasedEvent, (_, _) => { _dragStart = null; _dragDoc = null; }, RoutingStrategies.Tunnel, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, true);
    }

    private EditorGroup? Group => DataContext as EditorGroup;

    private void FocusMe()
    {
        if (Group is { } g) g.Main.FocusGroup(g);
    }

    private static DocumentViewModel? DocOf(object? sender) => (sender as StyledElement)?.DataContext as DocumentViewModel;

    private void Tab_Click(object? sender, RoutedEventArgs e)
    {
        if (DocOf(sender) is { } doc && Group is { } g) g.Main.MoveTab(doc, g); // shows it here and focuses this group
    }

    private async void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if (DocOf(sender) is { } doc && Group is { } g) await g.Main.CloseTabCommand.ExecuteAsync(doc);
    }

    private void MoveTab_Click(object? sender, RoutedEventArgs e)
    {
        if (DocOf(sender) is { } doc && Group is { } g) g.Main.MoveToOtherGroup(doc);
    }

    private void SplitRight_Click(object? sender, RoutedEventArgs e) => SplitWith(sender, "right");
    private void SplitDown_Click(object? sender, RoutedEventArgs e) => SplitWith(sender, "down");
    private void SplitWindow_Click(object? sender, RoutedEventArgs e) => SplitWith(sender, "window");

    /// <summary>Splits (or changes the split) and puts this tab in the second group.</summary>
    private void SplitWith(object? sender, string mode)
    {
        if (DocOf(sender) is not { } doc || Group is not { } g) return;
        var main = g.Main;
        main.SetSplitCommand.Execute(mode);
        if (!ReferenceEquals(main.EditorGroupOf(doc), main.Secondary)) main.MoveTab(doc, main.Secondary);
    }

    // ---- dragging tabs between groups ----------------------------------------------------------------

    private void Tab_Pressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStart = null;
        _dragDoc = (e.Source as StyledElement)?.DataContext as DocumentViewModel;
        if (_dragDoc != null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) _dragStart = e.GetPosition(this);
    }

    private async void Tab_Moved(object? sender, PointerEventArgs e)
    {
        if (_dragStart == null || _dragDoc == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.Value.X) + Math.Abs(p.Y - _dragStart.Value.Y) < 12) return;
        var doc = _dragDoc;
        _dragStart = null;
        _dragDoc = null;
        var data = new DataObject();
        data.Set(TabFormat, doc);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(TabFormat)) return;
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Data.Get(TabFormat) is not DocumentViewModel doc || Group is not { } g) return;
        e.Handled = true;
        g.Main.MoveTab(doc, g);
    }
}
