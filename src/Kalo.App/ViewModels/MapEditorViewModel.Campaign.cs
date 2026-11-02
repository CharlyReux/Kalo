using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// Campaign missions in the map view: trigger volumes, squad spawn points, cutscene flags and cameras and script points
/// are shown and edited like objects; the level can be shown one zone set at a time; the mission's scripts and
/// cutscenes are a click away.
/// </summary>
public partial class MapEditorViewModel
{
    /// <summary>True for a campaign mission (scenario type solo).</summary>
    [ObservableProperty] private bool _isCampaign;

    [ObservableProperty] private bool _showVolumes = true;
    [ObservableProperty] private bool _showAi = true;
    [ObservableProperty] private bool _showCutscene = true;
    [ObservableProperty] private bool _showScriptPoints;
    partial void OnShowVolumesChanged(bool value) => ApplyFilter();
    partial void OnShowAiChanged(bool value) => ApplyFilter();
    partial void OnShowCutsceneChanged(bool value) => ApplyFilter();
    partial void OnShowScriptPointsChanged(bool value) => ApplyFilter();

    public ObservableCollection<ZoneSetChoice> ZoneSetChoices { get; } = new();
    [ObservableProperty] private ZoneSetChoice? _selectedZoneSet;
    public ObservableCollection<MissionFile> MissionScripts { get; } = new();
    public ObservableCollection<MissionFile> Cutscenes { get; } = new();
    public bool HasMissionScripts => MissionScripts.Count > 0;
    public bool HasCutscenes => Cutscenes.Count > 0;

    private List<string> _bsps = new();
    private bool _zoneSetsFilled;

    private void ApplyCampaign(ScenarioPlacements data)
    {
        IsCampaign = data.Type == "solo";
        _bsps = data.Bsps ?? new();
        _squads = data.Squads ?? new();
        _squadGroups = data.SquadGroups ?? new();
        _main.RememberScenarioType(Scenario, data.Type);
        MissionScripts.Clear();
        foreach (var s in data.Scripts ?? new()) MissionScripts.Add(new MissionFile(s, IsOwnScript(s)));
        Cutscenes.Clear();
        foreach (var c in data.Cinematics ?? new()) Cutscenes.Add(new MissionFile(c, false));
        OnPropertyChanged(nameof(HasMissionScripts));
        OnPropertyChanged(nameof(HasCutscenes));
        if (_zoneSetsFilled) return;
        _zoneSetsFilled = true;
        ZoneSetChoices.Clear();
        if (!IsCampaign) return;
        ZoneSetChoices.Add(new ZoneSetChoice(-1, "Whole mission (every bsp)", null));
        foreach (var z in data.ZoneSets ?? new())
            if (z.Bsps is { Count: > 0 })
                ZoneSetChoices.Add(new ZoneSetChoice(z.Index, $"{z.Name}  ·  bsp {string.Join(", ", z.Bsps)}", z.Bsps, z.Name ?? ""));
        _suppressZoneSet = true;
        SelectedZoneSet = ZoneSetChoices[0];
        _suppressZoneSet = false;
    }

    /// <summary>A script in this map's own folder (copied for it): editing it changes only this map.</summary>
    private bool IsOwnScript(string script) =>
        script.StartsWith(System.IO.Path.GetDirectoryName(Path) + "\\", StringComparison.OrdinalIgnoreCase);

    private bool _suppressZoneSet;

    /// <summary>Shows only the level pieces (bsps) a zone set loads, like Sapien's "switch zone set".</summary>
    partial void OnSelectedZoneSetChanged(ZoneSetChoice? value)
    {
        if (_suppressZoneSet || value == null) return;
        _ = LoadGeometryAsync(_bsps, value.Bsps, reload: true);
        _main.Log($"{Title}: showing {(value.Bsps == null ? "every bsp" : "zone set " + value.Label)}.");
    }

    [RelayCommand]
    private async Task OpenMissionScript(MissionFile? file)
    {
        if (file == null) return;
        if (!file.IsOwn)
            _main.AddProblem("warning", $"{System.IO.Path.GetFileName(file.Path)} is shared with other missions: changing it changes them too.", file.Path);
        await _main.OpenMissionScriptAsync(file.Path);
    }

    [RelayCommand]
    private async Task OpenCutscene(MissionFile? file)
    {
        if (file != null) await _main.OpenTagAsync(file.Path);
    }

    /// <summary>
    /// Adds a campaign marker in front of the camera: a trigger volume or a cutscene flag; a squad spawn point or a
    /// script point goes in the squad / point set of the selected one (they are copies of it).
    /// </summary>
    [RelayCommand]
    private async Task AddMarker(string? category)
    {
        if (category == null) return;
        using var edit = await EditAsync("add " + category);
        Vector3 at = PlacementPointProvider?.Invoke() ?? CursorPoint ?? Selected?.Position ?? Bsp?.Center ?? Vector3.Zero;
        at += new Vector3(0, 0, 0.1f);
        if (category is CampaignCategories.AiSpawnPoints or CampaignCategories.ScriptPoints)
        {
            var from = Selected?.Category == category ? Selected : null;
            if (from == null)
            {
                Status = category == CampaignCategories.AiSpawnPoints
                    ? "Select a squad's spawn point first: the new one joins that squad (as a copy of it)."
                    : "Select a script point first: the new one joins its point set (as a copy of it).";
                return;
            }
            await RunOpAsync(new PlacementOpParams { Path = Path, Category = category, Op = PlacementOps.Duplicate, Index = from.Index, Position = ToArray(at) }, category);
            return;
        }
        await RunOpAsync(new PlacementOpParams { Path = Path, Category = category, Op = PlacementOps.Add, Position = ToArray(at) }, category);
    }

    // ---- squads ---------------------------------------------------------------------------------------

    private List<SquadSummary> _squads = new();
    private List<SquadGroupInfo> _squadGroups = new();
    public IReadOnlyList<SquadSummary> Squads => _squads;
    public IReadOnlyList<SquadGroupInfo> SquadGroups => _squadGroups;

    /// <summary>The squad (index in the scenario's squads block) of an ai spawn point, or -1.</summary>
    public static int SquadOf(PlacementItem? item)
    {
        if (item?.Block == null || item.Category != CampaignCategories.AiSpawnPoints || !item.Block.StartsWith("squads[", StringComparison.Ordinal)) return -1;
        int end = item.Block.IndexOf(']');
        return end > 7 && int.TryParse(item.Block[7..end], out int s) ? s : -1;
    }

    /// <summary>The squad editor in the inspector: the squad of the selected spawn points (all of one squad).</summary>
    [ObservableProperty] private SquadEditorViewModel? _squadEditor;
    public bool HasSquadEditor => SquadEditor != null;
    partial void OnSquadEditorChanged(SquadEditorViewModel? value) => OnPropertyChanged(nameof(HasSquadEditor));
    private int _squadGeneration;

    private async Task LoadSquadAsync()
    {
        int generation = ++_squadGeneration;
        var squads = _selection.Select(SquadOf).Distinct().ToList();
        if (_main.Host == null || squads.Count != 1 || squads[0] < 0) { SquadEditor = null; return; }
        try
        {
            var d = await _main.Host.SquadAsync(Path, squads[0]);
            if (generation != _squadGeneration) return;
            int? point = _selection.Count == 1 ? _selection[0].BlockIndex : null;
            SquadEditor = new SquadEditorViewModel(this, d, point);
        }
        catch (HostException ex)
        {
            if (generation == _squadGeneration) SquadEditor = null;
            _main.Log("Squad unavailable: " + ex.Message);
        }
    }

    /// <summary>Sets one squad field (a cell's character, weapon, count or chance, a spawn point's override). Null = done.</summary>
    public async Task<string?> SetSquadFieldAsync(string fieldPath, string value, string what)
    {
        if (_main.Host == null) return "The Blam Host is not running.";
        using var edit = await EditAsync(what);
        try
        {
            await _main.Host.SetFieldAsync(Path, fieldPath, value);
            IsDirty = true;
            Status = $"{SquadEditor?.Name}: {what} set.";
            QueueStatusRefresh();
            // Read it again: a squad read still running from the selection would show the old value.
            _ = LoadSquadAsync();
            return null;
        }
        catch (HostException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Every field of the selected spawn point's squad in a tag editor tab that shows only that squad (the whole scenario
    /// is too big to browse): its team, cells, initial zone and objective, spawn points...
    /// </summary>
    [RelayCommand]
    private async Task OpenSquadFields()
    {
        int squad = SquadOf(Selected);
        if (squad < 0) return;
        string name = _squads.FirstOrDefault(s => s.Index == squad)?.Name ?? "squad " + squad;
        await _main.OpenTagElementAsync(Path, "squads", squad, "squad " + name);
    }

    /// <summary>Resizes a box trigger volume from the inspector's width / length / height (its middle stays put).</summary>
    public async Task SetVolumeSizeAsync(PlacementItem item)
    {
        if (_main.Host == null || !item.IsBoxVolume) return;
        var size = item.VolumeSizeFromText();
        if (size == null) return;
        if (Math.Abs(size[0] - item.ZoneSize.X) < 1e-4 && Math.Abs(size[1] - item.ZoneSize.Y) < 1e-4 && Math.Abs(size[2] - (item.ZoneSize.Z + item.ZoneSize.W)) < 1e-4) return;
        using var edit = await EditAsync("resize volume");
        try
        {
            var p = await _main.Host.SetPlacementAsync(new SetPlacementParams { Path = Path, Category = item.Category, Index = item.Index, Size = size });
            item.Update(p);
            IsDirty = true;
            SceneChanged?.Invoke();
        }
        catch (HostException ex)
        {
            _main.AddProblem("error", $"Resize {item.Display}: {ex.Message}", Path);
        }
    }
}

/// <summary>A zone set in the map view's picker; Bsps null = every bsp.</summary>
public sealed record ZoneSetChoice(int Index, string Label, IReadOnlyList<int>? Bsps, string Name = "")
{
    public override string ToString() => Label;
}

/// <summary>A mission script or cutscene of the open map. IsOwn: kept in the map's own folder (only this map uses it).</summary>
public sealed record MissionFile(string Path, bool IsOwn)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Hint => IsOwn ? "This map's own copy" : "Shared: " + Path;
    public override string ToString() => Name;
}
