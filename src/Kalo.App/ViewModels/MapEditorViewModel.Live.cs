using System.Globalization;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kalo.App.ViewModels;

/// <summary>
/// The live game strip of a map tab: while the game runs, test things in it without restarting. Each button sends
/// console commands to the running game (GameSession.RunConsoleAsync, Kalo's console channel).
/// </summary>
public partial class MapEditorViewModel
{
    private bool _liveHooked;

    /// <summary>The game is running (any map): the live strip shows.</summary>
    public bool IsGameRunning
    {
        get
        {
            if (!_liveHooked)
            {
                _liveHooked = true;
                _main.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsGameRunning)) OnPropertyChanged(nameof(IsGameRunning)); };
            }
            return _main.IsGameRunning;
        }
    }

    [ObservableProperty] private bool _liveInvincible;
    [ObservableProperty] private bool _liveInfiniteAmmo;
    [ObservableProperty] private string _liveStatus = "";

    partial void OnLiveInvincibleChanged(bool value) =>
        _ = LiveAsync(value ? "You can't die now." : "You can die again.", $"(set cheat_deathless_player {(value ? "true" : "false")})");

    partial void OnLiveInfiniteAmmoChanged(bool value) =>
        _ = LiveAsync(value ? "Infinite ammo on." : "Infinite ammo off.", $"(set cheat_infinite_ammo {(value ? "true" : "false")})");

    /// <summary>Places the selected spawn point's squad in the running game (ai_place).</summary>
    [RelayCommand]
    private async Task LiveSpawnSquad()
    {
        string? squad = Squads.FirstOrDefault(s => s.Index == SquadOf(Selected))?.Name;
        if (squad == null) { LiveStatus = "Select a squad's spawn point first (AI in the Campaign bar)."; return; }
        await LiveAsync($"Placed squad {squad} in the game (a squad added since the game started is not in it: restart with Play).", "ai_place " + squad);
    }

    [RelayCommand]
    private Task LiveRemoveAi() => LiveAsync("Removed every AI from the game.", "ai_erase_all");

    /// <summary>
    /// Moves the player to the editor camera: Kalo writes the spot as the game's saved debug camera (camera_&lt;map&gt;.txt),
    /// loads it and teleports the player there (cheat_teleport_to_camera), then gives the view back to the player.
    /// </summary>
    [RelayCommand]
    private async Task LiveTeleport()
    {
        if (PlayFromProvider == null || _main.Game?.Scenario is not { } scenario || _main.Paths == null) return;
        if (PlayFromCheck?.Invoke() is { } problem) { LiveStatus = problem; return; }
        var (ground, facing) = PlayFromProvider();
        float yaw = facing * MathF.PI / 180f;
        var eye = ground + new Vector3(0, 0, 0.62f);
        static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        string file = System.IO.Path.Combine(_main.Paths.Workspace, "camera_" + System.IO.Path.GetFileName(scenario) + ".txt");
        // position, forward, up, field of view (radians): the format debug_camera_save writes.
        File.WriteAllText(file, $"{F(eye.X)} {F(eye.Y)} {F(eye.Z)}\r\n{F(MathF.Cos(yaw))} {F(MathF.Sin(yaw))} 0\r\n0 0 1\r\n1.361357\r\n");
        // The view goes back to the player a moment later: in the same batch, the teleport used the player's own camera.
        if (!await LiveAsync($"Moved the player to the camera ({ground.X:F1}, {ground.Y:F1}, {ground.Z:F1}).", LiveTeleportCommands.ToArray())) return;
        await Task.Delay(LiveTeleportDelayMs);
        await _main.Game!.RunConsoleAsync(LiveViewBackCommands, TimeSpan.FromSeconds(10));
    }

    public static int LiveTeleportDelayMs { get; set; } = 500;
    public static string[] LiveViewBackCommands { get; set; } = { "director_debug_camera 0" };

    /// <summary>
    /// Teleport: debug_camera_load switches the view to a debug camera at the saved spot, cheat_teleport_to_camera moves
    /// the player there, and director_debug_camera 0 gives the view back to the player (checked in reach_tag_test on
    /// Sword Base; camera_control 0 does not).
    /// </summary>
    public static List<string> LiveTeleportCommands { get; set; } = new() { "debug_camera_load", "cheat_teleport_to_camera" };

    /// <summary>Campaign: loads the zone set picked in the Campaign bar (switch_zone_set).</summary>
    [RelayCommand]
    private async Task LiveSwitchZoneSet()
    {
        if (SelectedZoneSet is not { Bsps: not null } zs) { LiveStatus = "Pick a zone set in the Campaign bar first."; return; }
        await LiveAsync($"Switched the game to zone set {zs.Name}.", "switch_zone_set " + zs.Name);
    }

    private async Task<bool> LiveAsync(string done, params string[] commands)
    {
        if (_main.Game is not { IsRunning: true } game) { LiveStatus = "The game is not running: press Play (F5) or Play here."; return false; }
        LiveStatus = "Sending to the game…";
        bool ok = await game.RunConsoleAsync(commands, TimeSpan.FromSeconds(10));
        LiveStatus = ok ? done : "The game didn't answer (is it still loading?). Try again in a moment.";
        if (ok) _main.Log("Live game: " + done);
        else _main.AddProblem("warning", "Live game: the game didn't run " + string.Join("; ", commands) + ". If this keeps happening, rebuild Kalo so the game gets Kalo's current xinput1_4.dll.", Path);
        return ok;
    }
}
