using Avalonia.Controls;
using Avalonia.Interactivity;
using Kalo.App.ViewModels;

namespace Kalo.App.Views;

public partial class GametypeSettingsView : UserControl
{
    public GametypeSettingsView() => InitializeComponent();

    /// <summary>The ▾ next to a setting or trait: a menu of its common values.</summary>
    private void Suggest_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        (string[] values, Action<string> pick) = b.DataContext switch
        {
            SettingRow s => (s.Suggestions, (Action<string>)(v => s.Value = v)),
            TraitRow t => (t.Suggestions, (Action<string>)(v => t.Value = v)),
            _ => (Array.Empty<string>(), (Action<string>)(_ => { })),
        };
        if (values.Length == 0) return;
        var menu = new MenuFlyout();
        foreach (var v in values)
        {
            var item = new MenuItem { Header = v };
            item.Click += (_, _) => pick(v);
            menu.Items.Add(item);
        }
        menu.ShowAt(b);
    }
}
