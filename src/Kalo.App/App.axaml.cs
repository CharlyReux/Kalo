using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kalo.App.ViewModels;
using Kalo.App.Views;

namespace Kalo.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel(AppArgs.Current);
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += async (_, _) => await vm.DisposeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
