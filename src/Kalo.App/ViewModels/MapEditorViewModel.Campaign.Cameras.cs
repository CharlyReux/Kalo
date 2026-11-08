using System.Numerics;
using CommunityToolkit.Mvvm.Input;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// Campaign missions: cutscene camera points framed from the 3D view. Add › Camera saves what the view shows; the Live
/// game bar shows a camera point in the running game.
/// </summary>
public partial class MapEditorViewModel
{
    /// <summary>Set by the 3D view: where it looks from, and its yaw and pitch in degrees.</summary>
    public Func<(Vector3 Eye, float Yaw, float Pitch)>? ViewProvider { get; set; }

    /// <summary>A new camera point exactly where the 3D view is looking from.</summary>
    [RelayCommand]
    private async Task AddCameraHere()
    {
        if (ViewProvider == null) { Status = "Open the 3D view first."; return; }
        using var edit = await EditAsync("add camera", new List<string> { CampaignCategories.CutsceneCameras });
        var (eye, _, _) = ViewProvider();
        ShowCutscene = true;
        await RunOpAsync(new PlacementOpParams { Path = Path, Category = CampaignCategories.CutsceneCameras, Op = PlacementOps.Add, Position = ToArray(eye) }, CampaignCategories.CutsceneCameras);
        if (Selected is not { IsCamera: true } camera) return;
        await FrameFromViewAsync(camera);
        Status = $"New camera point {camera.PlacedName}: it shows this view. Rename it, then a script uses camera_set({camera.PlacedName}, 0).";
    }

    /// <summary>Moves the selected camera point to the 3D view.</summary>
    [RelayCommand]
    private async Task SetCameraToView()
    {
        if (ViewProvider == null || Selected is not { IsCamera: true } camera) return;
        await FrameFromViewAsync(camera);
        Status = $"{camera.PlacedName} now shows this view.";
    }

    /// <summary>Moves the 3D view to the selected camera point.</summary>
    [RelayCommand]
    private void LookThroughCamera()
    {
        if (Selected is { IsCamera: true } camera) LookFrom(camera.Position, camera.Yaw, camera.Pitch);
    }

    private async Task FrameFromViewAsync(PlacementItem camera)
    {
        var (eye, yaw, pitch) = ViewProvider!();
        MoveLocal(camera, eye);
        camera.SetRotationLocal(yaw, pitch, 0);
        await CommitMoveAsync(camera);
    }

    /// <summary>Shows the selected camera point in the running game.</summary>
    [RelayCommand]
    private async Task LivePreviewCamera()
    {
        if (Selected is not { IsCamera: true } camera) { LiveStatus = "Select a camera point first (Cutscenes in the Campaign bar)."; return; }
        await LiveAsync($"Showing {camera.PlacedName} in the game (save and restart with Play for a camera added or moved since the game started).", "camera_control 1", "camera_set " + camera.PlacedName + " 0");
    }

    [RelayCommand]
    private Task LiveCameraBack() => LiveAsync("The view is back on the player.", "camera_control 0");
}
