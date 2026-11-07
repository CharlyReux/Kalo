using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core.Scripting;
using Kalo.Protocol;
using Kalo.Core.Scripting.Campaign;

namespace Kalo.App.ViewModels;

/// <summary>
/// Campaign missions: map inspector › Patrol. A squad walks a path of script points (a point set) until it sees an enemy:
/// New path here starts a point set, Add point here extends it, Add patrol to mission script writes the command script.
/// The squad is remembered while points are selected and placed.
/// </summary>
public partial class MapEditorViewModel
{
    [ObservableProperty] private string? _patrolSquad;
    [ObservableProperty] private string? _patrolPath;
    [ObservableProperty] private bool _patrolWalk = true;
    [ObservableProperty] private string _patrolStatus = "";
    public List<string> PatrolSquadChoices { get; private set; } = new();
    public List<string> PatrolPathChoices { get; private set; } = new();

    /// <summary>Shown for a squad's spawn point or a script point of a mission.</summary>
    public bool ShowPatrol => IsCampaign && Selected is { } s && (s.IsAiSpawnPoint || s.Category == CampaignCategories.ScriptPoints);

    private const string SetPrefix = "point set ";

    private IEnumerable<PlacementItem> PointsOf(string set) =>
        Items.Where(i => i.Category == CampaignCategories.ScriptPoints && i.Label == SetPrefix + set).OrderBy(i => i.Index);

    /// <summary>Called when the selection changes: a squad's spawn point picks the squad, a script point picks its path.</summary>
    private void UpdatePatrol(PlacementItem? value)
    {
        OnPropertyChanged(nameof(ShowPatrol));
        if (!IsCampaign) return;
        PatrolSquadChoices = Squads.Select(s => s.Name).Where(n => !string.IsNullOrEmpty(n)).OrderBy(n => n).ToList();
        PatrolPathChoices = Items.Where(i => i.Category == CampaignCategories.ScriptPoints && i.Label?.StartsWith(SetPrefix) == true)
            .Select(i => i.Label![SetPrefix.Length..]).Distinct().OrderBy(n => n).ToList();
        OnPropertyChanged(nameof(PatrolSquadChoices));
        OnPropertyChanged(nameof(PatrolPathChoices));
        if (value == null) return;
        if (value.IsAiSpawnPoint && ScriptNameOf(value) is { } squad) PatrolSquad = squad;
        else if (value.Category == CampaignCategories.ScriptPoints && value.Label?.StartsWith(SetPrefix) == true) PatrolPath = value.Label[SetPrefix.Length..];
        // The combo boxes keep their pick only if it is in the list.
        if (PatrolSquad != null && !PatrolSquadChoices.Contains(PatrolSquad)) PatrolSquadChoices.Add(PatrolSquad);
        if (PatrolPath != null && !PatrolPathChoices.Contains(PatrolPath)) PatrolPathChoices.Add(PatrolPath);
    }

    private Vector3 PointInFront() =>
        (PlacementPointProvider?.Invoke() ?? CursorPoint ?? Selected?.Position ?? Bsp?.Center ?? Vector3.Zero) + new Vector3(0, 0, 0.1f);

    [RelayCommand]
    private async Task NewPatrolPath()
    {
        string? squad = PatrolSquad;
        using (await EditAsync("new path", new List<string> { CampaignCategories.ScriptPoints }))
            await RunOpAsync(new PlacementOpParams { Path = Path, Category = CampaignCategories.ScriptPoints, Op = PlacementOps.Add, Position = ToArray(PointInFront()) }, CampaignCategories.ScriptPoints);
        ShowScriptPoints = true;
        PatrolSquad = squad;
        UpdatePatrol(Selected);
        PatrolStatus = PatrolPath != null ? $"New path {PatrolPath} with its first point. Look where the squad should go next and Add point here." : "Could not add the path.";
    }

    [RelayCommand]
    private async Task AddPatrolPoint()
    {
        string? squad = PatrolSquad;
        var last = PatrolPath == null ? null : PointsOf(PatrolPath).LastOrDefault();
        if (last == null) { PatrolStatus = "Start a path first (New path here), or pick one."; return; }
        using (await EditAsync("add point", new List<string> { CampaignCategories.ScriptPoints }))
            await RunOpAsync(new PlacementOpParams { Path = Path, Category = CampaignCategories.ScriptPoints, Op = PlacementOps.Duplicate, Index = last.Index, Position = ToArray(PointInFront()) }, CampaignCategories.ScriptPoints);
        PatrolSquad = squad;
        UpdatePatrol(Selected);
        PatrolStatus = $"{PatrolPath} has {PointsOf(PatrolPath!).Count()} points. The squad walks them in order, then starts again from the first.";
    }

    [RelayCommand]
    private async Task AddPatrol()
    {
        string? squad = PatrolSquad, path = PatrolPath;
        bool walk = PatrolWalk;
        if (string.IsNullOrEmpty(squad)) { PatrolStatus = "Pick the squad (or select one of its spawn points)."; return; }
        var points = path == null ? new List<string>() : PointsOf(path).Select(p => p.PlacedName ?? "").Where(n => n.Length > 0).ToList();
        if (points.Count < 2) { PatrolStatus = "The path needs at least 2 points: New path here, then Add point here."; return; }
        var own = MissionScripts.FirstOrDefault(f => f.IsOwn);
        if (own == null) { PatrolStatus = "This mission has no script of its own: copy it into your mod (New map › Campaign mission) to get one."; return; }
        var doc = await _main.OpenMissionScriptAsync(own.Path, Path);
        if (!doc.IsKaloScript) doc.IsKaloScript = true;
        doc.Text = PatrolScripts.AddTo(doc.Text, squad, path!, points, walk);
        doc.IsDirty = true;
        doc.Check();
        _main.Log($"{Title}: patrol of {squad} along {path} ({points.Count} points) written into {own.Path}.");
        _main.ActiveDocument = this;
        PatrolSquad = squad;
        PatrolPath = path;
        PatrolStatus = $"Added to {doc.Title}: {squad} walks {path} ({string.Join(" › ", points)}) until it sees an enemy. Save the map and the script, then Play.";
    }
}
