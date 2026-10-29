using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Kalo.App.ViewModels;

namespace Kalo.App.Views;

public partial class ProjectsPanel : UserControl
{
    public ProjectsPanel() => InitializeComponent();

    private async void Import_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a Kalo mod",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Kalo mod (zip)") { Patterns = new[] { "*.zip" } } }
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) await vm.ImportProjectAsync(path);
    }
}
