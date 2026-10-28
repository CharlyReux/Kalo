using Avalonia.Controls;
using Avalonia.Input;
using Kalo.App.ViewModels;
using Kalo.Protocol;

namespace Kalo.App.Views;

public partial class ScriptInspector : UserControl
{
    public ScriptInspector() => InitializeComponent();

    private void Catalog_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ScriptEditorViewModel vm && Catalog.SelectedItem is MegaloEntry entry) vm.InsertCommand.Execute(entry);
    }
}
