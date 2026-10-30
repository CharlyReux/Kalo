using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Kalo.App.Views;

/// <summary>Small dialogs built in code: a one-line text prompt, and the settings (keys) window.</summary>
public static class Dialogs
{
    private static IBrush Brush(string key) =>
        Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    /// <summary>Asks for one line of text (folder name, prefab name...). Null when cancelled.</summary>
    public static async Task<string?> PromptAsync(Window owner, string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 320 };
        var ok = new Button { Content = "OK", Classes = { "primary" }, IsDefault = true, MinWidth = 70 };
        var cancel = new Button { Content = "Cancel", Classes = { "tool" }, IsCancel = true };
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("KPanel"),
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = label, Foreground = Brush("KText") },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
                },
            },
        };
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        window.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await window.ShowDialog(owner);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    /// <summary>Settings: the map view's keys, with QWERTY / AZERTY presets and click-to-rebind.</summary>
    public static async Task ShowSettingsAsync(Window owner)
    {
        var settings = AppSettings.Current.Clone();
        var rows = new StackPanel { Spacing = 2 };
        var layout = new ComboBox
        {
            ItemsSource = new[] { "QWERTY (WASD)", "AZERTY (ZQSD)" },
            SelectedIndex = settings.Layout == "azerty" ? 1 : 0,
            MinWidth = 200,
        };
        Button? listening = null;
        string? listeningAction = null;

        void Build()
        {
            rows.Children.Clear();
            string? group = null;
            foreach (var action in AppSettings.Actions)
            {
                if (action.Group != group)
                {
                    group = action.Group;
                    rows.Children.Add(new TextBlock { Text = group.ToUpperInvariant(), Classes = { "header" }, Margin = new Thickness(0, 10, 0, 4) });
                }
                var key = new Button { Content = settings.GestureText(action.Id), MinWidth = 140, Classes = { "outline" }, HorizontalContentAlignment = HorizontalAlignment.Center };
                string id = action.Id;
                key.Click += (_, _) =>
                {
                    if (listening != null) listening.Content = settings.GestureText(listeningAction!);
                    listening = key;
                    listeningAction = id;
                    key.Content = "Press a key…";
                };
                var row = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(key, Dock.Right);
                row.Children.Add(key);
                row.Children.Add(new TextBlock { Text = action.Name, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("KText") });
                rows.Children.Add(row);
            }
        }

        layout.SelectionChanged += (_, _) =>
        {
            settings.ApplyPreset(layout.SelectedIndex == 1 ? "azerty" : "qwerty");
            listening = null;
            Build();
        };
        Build();

        var save = new Button { Content = "Save", Classes = { "primary" }, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", Classes = { "tool" }, IsCancel = true };
        var window = new Window
        {
            Title = "Kalo settings",
            Width = 460,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("KPanel"),
        };
        var body = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "MAP VIEW KEYS", Classes = { "header" } },
                new TextBlock { Text = "Pick your keyboard, then click a key to change it.", Foreground = Brush("KMuted"), TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Keyboard", VerticalAlignment = VerticalAlignment.Center }, layout } },
            },
        };
        DockPanel.SetDock(top, Dock.Top);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { cancel, save } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(top);
        body.Children.Add(buttons);
        body.Children.Add(new ScrollViewer { Content = rows, Margin = new Thickness(0, 8, 0, 0) });
        window.Content = body;

        window.AddHandler(InputElement.KeyDownEvent, (object? _, KeyEventArgs e) =>
        {
            if (listening == null || listeningAction == null) return;
            if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
            var action = AppSettings.Actions.First(a => a.Id == listeningAction);
            var mods = action.Hold ? KeyModifiers.None : e.KeyModifiers & ~KeyModifiers.Meta;
            string text = new KeyGesture(e.Key, mods).ToString();
            settings.Keys[listeningAction] = text;
            listening.Content = text;
            listening = null;
            listeningAction = null;
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        save.Click += (_, _) => { settings.Save(); window.Close(); };
        cancel.Click += (_, _) => window.Close();
        await window.ShowDialog(owner);
    }
}
