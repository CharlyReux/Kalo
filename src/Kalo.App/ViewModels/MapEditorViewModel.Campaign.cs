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
        await _main.OpenMissionScriptAsync(file.Path, Path);
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

    // ---- where mission scripts use the selection --------------------------------------------------------

    /// <summary>Lines of this mission's own scripts that name the selected squad / volume / flag / object / point.</summary>
    public ObservableCollection<ScriptUse> ScriptUses { get; } = new();
    [ObservableProperty] private string _scriptUsesText = "";
    public bool HasScriptUses => ScriptUses.Count > 0 || ScriptUsesText.Length > 0;
    private int _usesGeneration;

    /// <summary>The name scripts use for an item: a squad's name for its spawn points, set/point for script points.</summary>
    private string? ScriptNameOf(PlacementItem item)
    {
        if (item.IsAiSpawnPoint) return Squads.FirstOrDefault(s => s.Index == SquadOf(item))?.Name;
        if (item.Category == CampaignCategories.ScriptPoints && item.Label is { } l && l.StartsWith("point set ", StringComparison.Ordinal))
            return l["point set ".Length..] + "/" + item.PlacedName;
        return string.IsNullOrEmpty(item.PlacedName) ? null : item.PlacedName;
    }

    private async Task LoadScriptUsesAsync()
    {
        int generation = ++_usesGeneration;
        ScriptUses.Clear();
        ScriptUsesText = "";
        var names = _selection.Select(ScriptNameOf).Where(n => n != null).Distinct().Take(3).ToList();
        if (!IsCampaign || names.Count == 0) { OnPropertyChanged(nameof(HasScriptUses)); return; }
        var found = new List<ScriptUse>();
        foreach (var file in MissionScripts.Where(f => f.IsOwn))
        {
            string text = await _main.MissionKaloScriptAsync(file.Path);
            if (generation != _usesGeneration) return;
            var lines = text.Split('\n');
            foreach (var name in names)
            {
                var re = new System.Text.RegularExpressions.Regex(@"(?<![\w/])" + System.Text.RegularExpressions.Regex.Escape(name!) + @"(?![\w/])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                for (int i = 0; i < lines.Length && found.Count < 40; i++)
                {
                    string code = lines[i].TrimStart();
                    if (code.StartsWith('#') || !re.IsMatch(lines[i])) continue;
                    found.Add(new ScriptUse(file.Path, file.Name, i + 1, code.Length > 90 ? code[..90] + "…" : code, name!));
                }
            }
        }
        if (generation != _usesGeneration) return;
        foreach (var u in found) ScriptUses.Add(u);
        ScriptUsesText = found.Count == 0 ? $"No script of this mission names {string.Join(", ", names)}." : found.Count >= 40 ? "First 40 lines:" : "";
        OnPropertyChanged(nameof(HasScriptUses));
        _usesLoadedFor = names;
        UpdateSquadSummary();
    }

    private List<string?> _usesLoadedFor = new();

    /// <summary>The squad editor's "placed by" line, from the script lines that ai_place the squad.</summary>
    private void UpdateSquadSummary()
    {
        if (SquadEditor is not { } squad || !_usesLoadedFor.Contains(squad.Name)) return;
        var re = new System.Text.RegularExpressions.Regex(@"ai_place\(\s*" + System.Text.RegularExpressions.Regex.Escape(squad.Name) + @"(?![\w/])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var places = ScriptUses.Where(u => re.IsMatch(u.Text)).ToList();
        squad.PlacedBy = places.Count > 0
            ? "Placed by " + string.Join(", ", places.Take(3).Select(u => $"{u.File} line {u.Line}")) + (places.Count > 3 ? $" and {places.Count - 3} more" : "") + " (ai_place)."
            : ScriptUses.Count > 0
                ? "No script line places it by name (ai_place): it may come with its squad group or objective, or be placed by a script of another file."
                : "No script of this mission names it: the game never places it unless a script does (ai_place).";
    }

    // ---- squad tools ----------------------------------------------------------------------------------

    /// <summary>
    /// The selected squad gets its own cells (a copy of its template's) and loses its template: its cells then decide
    /// what spawns, and can be edited here.
    /// </summary>
    [RelayCommand]
    private async Task DetachSquad()
    {
        if (_main.Host == null || SquadEditor is not { IsTemplated: true } squad) return;
        using var edit = await EditAsync("detach squad from its template");
        try
        {
            var r = await _main.Host.SquadOpAsync(new SquadOpParams { Path = Path, Op = SquadOps.Detach, Index = squad.Index });
            IsDirty = true;
            Status = $"{r.Name} now has its own {r.Cells} cell{(r.Cells == 1 ? "" : "s")}: what they say is what spawns.";
            _main.Log($"{Title}: squad {r.Name} detached from its template ({r.Cells} cells copied).");
            await LoadSquadAsync();
        }
        catch (HostException ex)
        {
            _main.AddProblem("error", "Detach squad: " + ex.Message, Path);
        }
    }

    /// <summary>A new squad like the selected one (team, group, zone, objective), with its own cells and one spawn point in front of the camera.</summary>
    [RelayCommand]
    private async Task NewSquadHere()
    {
        if (_main.Host == null || SquadEditor is not { } squad) return;
        using var edit = await EditAsync("new squad");
        Vector3 at = PlacementPointProvider?.Invoke() ?? CursorPoint ?? Selected?.Position ?? Vector3.Zero;
        at += new Vector3(0, 0, 0.1f);
        try
        {
            var r = await _main.Host.SquadOpAsync(new SquadOpParams { Path = Path, Op = SquadOps.New, Index = squad.Index, Name = squad.Name + "_new", Position = new[] { at.X, at.Y, at.Z } });
            if (r.Squads != null) _squads = r.Squads;
            if (r.Category != null) ReplaceCategory(r.Category);
            IsDirty = true;
            Selected = Items.FirstOrDefault(i => i.Category == CampaignCategories.AiSpawnPoints && i.Index == r.SpawnPoint);
            Status = $"New squad {r.Name} (a copy of {squad.Name}) with one spawn point. Scripts place it with ai_place({r.Name}).";
            _main.Log($"{Title}: new squad {r.Name}, like {squad.Name}. Place it from a script, ai_place({r.Name}), or with the live game panel.");
        }
        catch (HostException ex)
        {
            _main.AddProblem("error", "New squad: " + ex.Message, Path);
        }
    }

    [RelayCommand]
    private async Task OpenScriptUse(ScriptUse? use)
    {
        if (use == null) return;
        var doc = await _main.OpenMissionScriptAsync(use.Script, Path);
        if (!doc.IsKaloScript) doc.SwitchViewCommand.Execute(null);
        doc.RevealLine(use.Line, use.Name);
    }

    // ---- names used by mission scripts -------------------------------------------------------------------

    /// <summary>
    /// Selects and frames what a mission script names: a squad (its spawn points), a squad group, a trigger volume, a
    /// cutscene flag or camera, an object, a point (set/point) or a point set. False when it isn't shown in the map view.
    /// </summary>
    public bool RevealName(string name, string kind)
    {
        List<PlacementItem> items;
        switch (kind)
        {
            case "squad":
            {
                int squad = Squads.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))?.Index ?? -1;
                items = Items.Where(i => SquadOf(i) == squad && squad >= 0).ToList();
                break;
            }
            case "squad group":
            {
                int group = SquadGroups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))?.Index ?? -1;
                var groups = new HashSet<int> { group };
                for (bool grew = true; grew;)
                {
                    grew = false;
                    foreach (var g in SquadGroups) if (groups.Contains(g.Parent) && groups.Add(g.Index)) grew = true;
                }
                var squads = Squads.Where(s => groups.Contains(s.Group)).Select(s => s.Index).ToHashSet();
                items = group < 0 ? new() : Items.Where(i => squads.Contains(SquadOf(i))).ToList();
                break;
            }
            case "point":
            {
                int slash = name.IndexOf('/');
                string set = slash > 0 ? name[..slash] : "", point = slash > 0 ? name[(slash + 1)..] : name;
                items = Items.Where(i => i.Category == CampaignCategories.ScriptPoints && string.Equals(i.PlacedName, point, StringComparison.OrdinalIgnoreCase)
                    && (i.Label ?? "").EndsWith(" " + set, StringComparison.OrdinalIgnoreCase)).ToList();
                break;
            }
            case "point set":
                items = Items.Where(i => i.Category == CampaignCategories.ScriptPoints && string.Equals(i.Label, "point set " + name, StringComparison.OrdinalIgnoreCase)).ToList();
                break;
            case "zone set":
            {
                var z = ZoneSetChoices.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (z == null) return false;
                SelectedZoneSet = z;
                Status = $"Showing zone set {name}.";
                return true;
            }
            default:
                items = Items.Where(i => string.Equals(i.PlacedName, name, StringComparison.OrdinalIgnoreCase)).ToList();
                break;
        }
        if (items.Count == 0)
        {
            Status = $"{name} ({kind}) is not shown in the map view.";
            return false;
        }
        // Their layer on, so they can be seen.
        if (items[0].Category == CampaignCategories.TriggerVolumes) ShowVolumes = true;
        if (items[0].Category == CampaignCategories.AiSpawnPoints) ShowAi = true;
        if (items[0].Category is CampaignCategories.CutsceneFlags or CampaignCategories.CutsceneCameras) ShowCutscene = true;
        if (items[0].Category == CampaignCategories.ScriptPoints) ShowScriptPoints = true;
        SetSelection(items);
        foreach (var i in items) RevealInOutliner(i);
        OutlinerChanged?.Invoke();
        var center = new Vector3(items.Average(i => i.Center.X), items.Average(i => i.Center.Y), items.Average(i => i.Center.Z));
        float radius = Math.Max(items.Max(i => i.PickRadius), items.Max(i => Vector3.Distance(i.Center, center)) + 2);
        FocusRequested?.Invoke(center, radius);
        Status = items.Count == 1 ? $"{name} ({kind})." : $"{name} ({kind}): {items.Count} markers.";
        return true;
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
            UpdateSquadSummary();
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
        if (_main.Host == null) return "The Engine is not running.";
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

/// <summary>A line of a mission script that names the selection (Kalo Script line numbers).</summary>
public sealed record ScriptUse(string Script, string File, int Line, string Text, string Name)
{
    public string Where => $"{File}:{Line}";
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
