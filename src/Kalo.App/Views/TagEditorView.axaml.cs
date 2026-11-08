using Avalonia.Platform.Storage;
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

    private async void UseInScripts_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not Kalo.App.ViewModels.TagEditorViewModel tab || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (tab.IsDirty) { tab.Main.AddProblem("warning", "Use in scripts: save the tag first.", tab.Path); return; }
        string label = "Name scripts will use for this object: create_object(\"name\", at ...).\n\n" +
                       "This edits two files shared by every map and gametype of your mod: the game's object type list and objects.txt " +
                       "(the script compiler's list). The object goes at the end, so existing gametypes are not affected. " +
                       "It works in your mod only, not in a gametype shared on its own. A map must contain the object for a gametype to create it there " +
                       "(Play takes care of that for playtests).";
        string? name = Kalo.Core.ScriptObjects.SuggestName(tab.Path);
        string? error = null;
        while (true)
        {
            name = await Dialogs.PromptAsync(owner, "Use in scripts", error == null ? label : label + "\n\n" + error, name ?? "");
            if (string.IsNullOrWhiteSpace(name)) return;
            error = await tab.Main.RegisterScriptObjectAsync(tab.Path, name.Trim());
            if (error == null) return;
        }
    }

    private async void RenameScriptName_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not Kalo.App.ViewModels.TagEditorViewModel { ScriptEntry: { } entry } tab || TopLevel.GetTopLevel(this) is not Window owner) return;
        string label = $"New name for {entry.Name}. Scripts that use the old name will need the new one.";
        string? name = entry.Name;
        string? error = null;
        while (true)
        {
            name = await Dialogs.PromptAsync(owner, "Rename script object", error == null ? label : label + "\n\n" + error, name ?? "");
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == entry.Name) return;
            error = await tab.Main.RenameScriptObjectAsync(entry, name.Trim());
            if (error == null) return;
        }
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

    private async void PaintPicture_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not Kalo.App.ViewModels.TagEditorViewModel tab || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Paint it with a picture",
            AllowMultiple = false,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Pictures") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg" } } }
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await tab.PaintWithImageAsync(path);
    }

    private async void LooksLike_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is Kalo.App.ViewModels.TagEditorViewModel tab && sender is ListBox { SelectedItem: string other })
            await tab.LooksLikeCommand.ExecuteAsync(other);
    }

    private async void NewWeapon_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not Kalo.App.ViewModels.TagEditorViewModel tab || TopLevel.GetTopLevel(this) is not Window owner) return;
        string current = System.IO.Path.GetFileNameWithoutExtension(tab.Path);
        string label = "Name of the new weapon. It goes into your mod with its own bullet and damage files (your edits here come with it); " +
                       current + " stays as it is. Then: Place in map, Add to map palette (squads), or Use in scripts (gametypes).";
        string? name = current + "_custom", error = null;
        while (true)
        {
            name = await Dialogs.PromptAsync(owner, "New weapon from this one", error == null ? label : label + "\n\n" + error, name ?? "");
            if (string.IsNullOrWhiteSpace(name)) return;
            error = await tab.Main.NewWeaponFromAsync(tab, name.Trim());
            if (error == null) return;
        }
    }

    /// <summary>Each shot box: Enter commits the number (focus moves away, which pushes the text).</summary>
    private void ShotBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { FieldTree.Focus(); e.Handled = true; }
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
