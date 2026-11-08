using Avalonia.Controls;
using Kalo.App.ViewModels;

namespace Kalo.App.Views;

public partial class StartPage : UserControl
{
    public StartPage() => InitializeComponent();

    private void Recipes_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && TopLevel.GetTopLevel(this) is Window owner) DocsWindow.Open(owner, vm, "recipe-start");
    }
}
