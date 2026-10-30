using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Kalo.App.ViewModels;

namespace Kalo.App.Views;

/// <summary>
/// The second editor group in its own window (Split › Open in a new window), e.g. the gametype code on another
/// screen while the map stays in the main window. Closing it puts its tabs back in the main window.
/// </summary>
public sealed class EditorWindow : Window
{
    private readonly MainWindow _owner;

    public EditorWindow(MainViewModel vm, MainWindow owner)
    {
        _owner = owner;
        Title = "Kalo · second editor";
        Width = 1100;
        Height = 780;
        MinWidth = 500;
        MinHeight = 320;
        DataContext = vm;
        if (Application.Current?.TryFindResource("KBg", out var bg) == true && bg is IBrush brush) Background = brush;
        Content = new EditorGroupView { DataContext = vm.Secondary };
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, KeyModifiers.Control), Command = vm.SaveActiveCommand });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F5), Command = vm.PlayCommand });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F5, KeyModifiers.Shift), Command = vm.StopGameCommand });
        Activated += (_, _) => vm.FocusGroup(vm.Secondary);
    }

    /// <summary>The map shortcuts work here too (when a map is in this window).</summary>
    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled) await _owner.HandleSharedKeysAsync(this, e);
    }
}
