using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kalo.Core;

namespace Kalo.App.ViewModels;

/// <summary>
/// Campaign missions: knowing which maps are missions (scenario type solo), playing them in reach_tag_test (no
/// gametype; a difficulty and an insertion point instead), and opening their mission scripts.
/// </summary>
public partial class MainViewModel
{
    private readonly Dictionary<string, string> _scenarioTypes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers a scenario's type (solo, multiplayer...) as read by the Blam Host. Scenario: tag path without extension.</summary>
    public void RememberScenarioType(string scenario, string? type)
    {
        if (string.IsNullOrEmpty(type)) return;
        _scenarioTypes[scenario] = type;
        if (string.Equals(scenario, SelectedScenario, StringComparison.OrdinalIgnoreCase)) RefreshCampaignPlay();
    }

    /// <summary>
    /// A campaign mission: Reach's own (levels\solo), a map copied from one (its template, or its type once read),
    /// or any scenario the host reported as solo.
    /// </summary>
    public bool IsCampaignScenario(string? scenario)
    {
        if (string.IsNullOrEmpty(scenario)) return false;
        if (_scenarioTypes.TryGetValue(scenario, out var type)) return type == "solo";
        if (scenario.StartsWith(@"levels\solo\", StringComparison.OrdinalIgnoreCase)) return true;
        if (ModProjects?.Active?.Manifest.MapTemplates.TryGetValue(scenario + ".scenario", out var template) == true)
            return template.StartsWith(@"levels\solo\", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    /// <summary>
    /// Like IsCampaignScenario, asking the Blam Host when the map's type is not known yet (a map made outside this
    /// Kalo, e.g. by another mod or by kalo-cli). The answer is remembered.
    /// </summary>
    public async Task<bool> IsCampaignScenarioAsync(string? scenario)
    {
        if (string.IsNullOrEmpty(scenario)) return false;
        if (_scenarioTypes.ContainsKey(scenario) || scenario.StartsWith(@"levels\", StringComparison.OrdinalIgnoreCase) && !scenario.StartsWith(@"levels\mods\", StringComparison.OrdinalIgnoreCase) || Host == null)
            return IsCampaignScenario(scenario);
        try { RememberScenarioType(scenario, await Host.ScenarioTypeAsync(scenario + ".scenario")); }
        catch (Exception ex) { Log($"Could not read the type of {scenario}: {ex.Message}"); }
        return IsCampaignScenario(scenario);
    }

    /// <summary>True when Play will start a campaign mission (the gametype picker does not apply).</summary>
    public bool PlayIsCampaign => IsCampaignScenario(SelectedScenario);
    public bool PlayIsMultiplayer => !PlayIsCampaign;

    public IReadOnlyList<CampaignDifficulty> Difficulties { get; } = new[]
    {
        new CampaignDifficulty("easy", "Easy"), new CampaignDifficulty("normal", "Normal"),
        new CampaignDifficulty("heroic", "Heroic"), new CampaignDifficulty("legendary", "Legendary"),
    };
    [ObservableProperty] private CampaignDifficulty? _selectedDifficulty;

    /// <summary>Insertion points of the mission to play (where it starts: 0 = the beginning), from its start locations.</summary>
    public ObservableCollection<InsertionChoice> InsertionChoices { get; } = new();
    [ObservableProperty] private InsertionChoice? _selectedInsertion;

    /// <summary>
    /// The console command that sets the difficulty of the next map in reach_tag_test ({0} = easy, normal, heroic or
    /// legendary). Checked in the exe's strings; --difficulty-cmd changes it for tests.
    /// </summary>
    public static string DifficultyCommand { get; set; } = "game_difficulty {0}";

    private void RefreshCampaignPlay()
    {
        // A map of the mod whose type is not known yet: ask the host, then refresh again.
        if (SelectedScenario is { } sel && Host != null && !_scenarioTypes.ContainsKey(sel) && sel.StartsWith(@"levels\mods\", StringComparison.OrdinalIgnoreCase))
            _ = IsCampaignScenarioAsync(sel);
        OnPropertyChanged(nameof(PlayIsCampaign));
        OnPropertyChanged(nameof(PlayIsMultiplayer));
        OnPropertyChanged(nameof(PlaySummary));
        SelectedDifficulty ??= Difficulties[1];
        var keep = SelectedInsertion?.Index ?? 0;
        InsertionChoices.Clear();
        if (!PlayIsCampaign) return;
        // Reach missions start at insertion 0; their insertion script (e.g. m10_insertion) decides what 1, 2... do.
        for (int i = 0; i < 10; i++) InsertionChoices.Add(new InsertionChoice(i, i == 0 ? "From the start" : $"Insertion point {i}"));
        SelectedInsertion = InsertionChoices.FirstOrDefault(c => c.Index == keep) ?? InsertionChoices[0];
    }

    /// <summary>Console commands before game_start for a campaign mission: difficulty and insertion point.</summary>
    private List<string> CampaignPlayCommands(out string note)
    {
        var commands = new List<string>();
        string difficulty = (SelectedDifficulty ?? Difficulties[1]).Value;
        commands.Add(string.Format(DifficultyCommand, difficulty));
        if (PlayerCount > 1)
        {
            // reach_tag_test: "sets the number of coop players for the next map" and "the number of splitscreen players
            // for the next map; does NOT set game mode" (game_splitscreen is the multiplayer one).
            commands.Add("game_coop_players " + PlayerCount);
            commands.Add("game_player_count " + PlayerCount);
        }
        int insertion = SelectedInsertion?.Index ?? 0;
        // "debug map launching: sets the map insertion_point" (reach_tag_test)
        if (insertion > 0) commands.Add("game_insertion_point_set " + insertion);
        note = $" (campaign, {difficulty}" + (insertion > 0 ? $", insertion {insertion}" : "") + (PlayerCount > 1 ? $", {PlayerCount} players co-op" : "") + ")";
        return commands;
    }

    /// <summary>
    /// The zone set to start "Play here" in: the one picked in the map's Campaign bar; else the one named by the smallest
    /// zone switch volume around the camera ("zone_set:name" / "begin_zone_set:name" trigger volumes, which missions put
    /// where each zone set is active); else one loading the smallest level piece (bsp) whose world bounds hold the camera.
    /// </summary>
    private async Task<ZoneSetChoice?> ZoneSetForAsync(MapEditorViewModel map, System.Numerics.Vector3 at)
    {
        if (map.SelectedZoneSet is { Index: >= 0 } chosen) return chosen;
        var byVolume = map.Items
            .Where(i => i.IsVolume && i.PlacedName is { } n && (n.StartsWith("zone_set:", StringComparison.OrdinalIgnoreCase) || n.StartsWith("begin_zone_set:", StringComparison.OrdinalIgnoreCase)) && Inside(i, at))
            .OrderBy(i => i.ZoneSize.X * i.ZoneSize.Y * (i.ZoneSize.Z + i.ZoneSize.W))
            .Select(i => i.PlacedName![(i.PlacedName!.IndexOf(':') + 1)..].Trim())
            .Select(name => map.ZoneSetChoices.FirstOrDefault(z => z.Index >= 0 && z.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(z => z != null);
        if (byVolume != null) return byVolume;
        if (Host == null) return null;
        try
        {
            var r = await Host.BspBoundsAsync(map.Path);
            var around = new List<(int Bsp, double Size)>();
            for (int i = 0; i < r.Bounds.Count; i++)
                if (r.Bounds[i] is { Length: 6 } b && at.X >= b[0] && at.X <= b[1] && at.Y >= b[2] && at.Y <= b[3] && at.Z >= b[4] - 5 && at.Z <= b[5] + 5)
                    around.Add((i, (double)(b[1] - b[0]) * (b[3] - b[2]) * (b[5] - b[4])));
            if (around.Count == 0) return null;
            int smallest = around.OrderBy(a => a.Size).First().Bsp;
            return map.ZoneSetChoices.Where(z => z.Bsps != null && z.Bsps.Contains(smallest))
                .OrderByDescending(z => around.Count(a => z.Bsps!.Contains(a.Bsp))).ThenBy(z => z.Bsps!.Count).FirstOrDefault();
        }
        catch (HostException ex)
        {
            Log("Play here: bsp bounds unavailable (" + ex.Message + ").");
            return null;
        }
    }

    /// <summary>Is a point inside a trigger volume as the map view draws it (a box turned by its yaw)?</summary>
    private static bool Inside(PlacementItem v, System.Numerics.Vector3 p)
    {
        var d = p - v.Position;
        double yaw = -v.Yaw * Math.PI / 180;
        double lx = d.X * Math.Cos(yaw) - d.Y * Math.Sin(yaw), ly = d.X * Math.Sin(yaw) + d.Y * Math.Cos(yaw);
        return Math.Abs(lx) <= v.ZoneSize.X / 2 && Math.Abs(ly) <= v.ZoneSize.Y / 2 && d.Z <= v.ZoneSize.Z && d.Z >= -v.ZoneSize.W;
    }

    /// <summary>Opens a campaign mission script (.scenario_hs_source_file) in a text editor tab.</summary>
    public async Task<MissionScriptViewModel> OpenMissionScriptAsync(string path)
    {
        path = path.Replace('/', '\\').TrimStart('\\');
        var existing = Tabs.OfType<MissionScriptViewModel>().FirstOrDefault(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { ActiveDocument = existing; return existing; }
        var doc = new MissionScriptViewModel(this, path);
        Tabs.Add(doc);
        ActiveDocument = doc;
        doc.DiffersFromVanilla = Baseline?.IsChanged("tags\\" + path) ?? false;
        await doc.LoadAsync();
        Log($"Opened mission script {path}: {doc.Status}");
        return doc;
    }
}

public sealed record CampaignDifficulty(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record InsertionChoice(int Index, string Label)
{
    public override string ToString() => Label;
}
