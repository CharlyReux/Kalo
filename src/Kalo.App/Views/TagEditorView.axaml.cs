using Avalonia.Controls;
using Avalonia.Input;

namespace Kalo.App.Views;

public partial class TagEditorView : UserControl
{
    public TagEditorView() => InitializeComponent();

    private async void CopyPath_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Kalo.App.ViewModels.FieldViewModel f } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(f.Path);
    }

    private async void SaveAsNew_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not Kalo.App.ViewModels.TagEditorViewModel tab || TopLevel.GetTopLevel(this) is not Window owner) return;
        var main = tab.Main;
        string current = System.IO.Path.GetFileNameWithoutExtension(tab.Path);
        string? name = await Dialogs.PromptAsync(owner, "Save as new tag",
            $"Name of the new tag. It is saved as {main.NewTagPathFor(tab.Path, "<name>")}, with the edits you made here; {current} stays as it is on disk.",
            current + "_copy");
        if (!string.IsNullOrWhiteSpace(name)) await main.SaveTagAsNewAsync(tab, name);
    }

    /// <summary>Enter commits the value (by moving focus away, which pushes the text to the field).</summary>
    private void ValueBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FieldTree.Focus();
            e.Handled = true;
        }
    }
}
