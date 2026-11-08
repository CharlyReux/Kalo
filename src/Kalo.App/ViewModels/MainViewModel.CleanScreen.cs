using CommunityToolkit.Mvvm.ComponentModel;
using Kalo.Core;

namespace Kalo.App.ViewModels;

/// <summary>
/// The Clean screen checkbox next to Play: hides the engine's text and error markers drawn over the game. Remembered in
/// the settings, applied when a game loads (error markers only exist by then) and at once in a running game.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private bool _cleanGameScreen = AppSettings.Current.CleanGameScreen;

    private void InitCleanScreen()
    {
        Game!.CleanScreen = CleanGameScreen;
        Game.Loaded += () => _ = ApplyCleanScreenAsync();
    }

    partial void OnCleanGameScreenChanged(bool value)
    {
        AppSettings.Current.CleanGameScreen = value;
        AppSettings.Current.Save();
        if (Game == null) return;
        Game.CleanScreen = value;
        if (Game.IsRunning) _ = ApplyCleanScreenAsync();
    }

    private async Task ApplyCleanScreenAsync()
    {
        if (Game == null) return;
        try { await Game.RunConsoleAsync(GameSession.ScreenCommands(CleanGameScreen), TimeSpan.FromSeconds(10)); }
        catch (Exception ex) { Log("Clean screen: " + ex.Message); }
    }
}
