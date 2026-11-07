using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Core.Scripting.Multiplayer;

namespace Kalo.App.ViewModels;

/// <summary>
/// Play in MCC: builds the chosen map into a real .map with HREK's tool.exe, puts it with the chosen gametype in
/// Kalo's local MCC mod ("Kalo Playtest", see <see cref="MccMod"/>), and says how to start it. Slower than Play (the
/// build takes minutes, nothing is reloaded live), but the game runs as a real custom game: the map's Forge setup
/// and Megalo labels work there, unlike in reach_tag_test started on a map.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private bool _isBuildingForMcc;
    private CancellationTokenSource? _mccBuild;

    public string PlayInMccSummary => PlayIsCampaign
        ? $"Build the campaign mission {System.IO.Path.GetFileName(SelectedScenario ?? "?")} into a real map and play it in MCC as a mod campaign (takes several minutes; not live like Play)."
        : $"Build {System.IO.Path.GetFileName(SelectedScenario ?? "?")} into a real map and play it in MCC's Custom Games, with all your gametypes in the Action Sack folder (takes a few minutes; not live like Play).";

    [RelayCommand]
    public async Task PlayInMccAsync()
    {
        if (Paths == null || SelectedScenario == null || IsBuildingForMcc) return;
        IsBuildingForMcc = true;
        BottomPanel = "activity";
        _mccBuild = new CancellationTokenSource();
        var mod = new MccMod(Paths);
        string scenario = SelectedScenario;
        string mapName = System.IO.Path.GetFileName(scenario);
        try
        {
            foreach (var doc in Tabs.Where(t => t.IsDirty).ToList()) await doc.SaveDocumentAsync();
            if (await IsCampaignScenarioAsync(scenario))
            {
                await PlayCampaignInMccAsync(mod, scenario);
                return;
            }

            // The gametypes first: a script error should not cost a five-minute build. Every gametype of the mod goes
            // in, listed in the game's Action Sack folder (the menus' folders are fixed, and MCC shows only the ones it knows: Community and
            // Insane don't appear; Action Sack does, with Bungie's few party modes).
            var variants = new List<(string File, byte[] Data)>();
            var titles = new List<string>();
            foreach (var choice in GametypeChoices.Where(g => g.Path != null && g.Path.Contains(@"\mods\", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                string script = choice.Path!;
                string? engine = await DeployGametypeAsync(script, MccCategory);
                if (engine == null) { Log($"Play in MCC: stopped, the gametype {System.IO.Path.GetFileName(script)} does not compile (see Problems)."); return; }
                string bin = System.IO.Path.Combine(Paths.Workspace, "maps", "megalo", engine + ".bin");
                string title = GametypeTitle(script) ?? System.IO.Path.GetFileNameWithoutExtension(script).Replace('_', ' ');
                variants.Add((engine + ".bin", Kalo.Core.Scripting.Multiplayer.GameVariantFile.ToMcc(await File.ReadAllBytesAsync(bin), title, "Made with Kalo")));
                titles.Add(title);
            }

            Log($"Play in MCC: building {scenario} into a map with tool.exe. This takes a few minutes; Stop build in the Play menu cancels it.");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool built = await mod.BuildMapAsync(scenario, line => Post(() => AppendOutput("[tool] " + line)), _mccBuild.Token);
            if (!built)
            {
                AddProblem("error", $"Play in MCC: tool.exe could not build {mapName}.map. Its messages are in Output (search for [tool]).", scenario + ".scenario");
                return;
            }
            Log($"Play in MCC: {mapName}.map built in {clock.Elapsed.TotalMinutes:F1} min.");

            int objectTypes = (await GetScriptObjectsAsync())?.Entries.Count ?? 256;
            mod.SetMap(scenario, mapName + " (Kalo)", objectTypes);
            var folders = mod.SetGameVariants(variants);
            bool registered = mod.RegisterWithMcc();

            Log($"Play in MCC: the mod \"Kalo playtest\" is ready in {mod.ModFolder}" +
                (registered ? $" and MCC now looks for mods in {mod.Root} ({MccMod.ManifestFile})." : "."));
            if (variants.Count > 0)
                Log($"Play in MCC: your game types ({string.Join(", ", titles)}) are in the Action Sack folder of the game type list. Put in " + string.Join(" and ", folders) +
                    (folders.Count == 1 ? ". MCC lists game types from its own folder (LocalFiles\\<id>\\HaloReach\\GameType), which it makes the first time you save a game type in Custom Games: save any one once, then Play in MCC again." : "."));
            Log("Play in MCC: start MCC from Steam with mods (anti-cheat off), then Custom Games → Halo: Reach: map " +
                $"\"{mapName} (Kalo)\", game type folder Action Sack. If MCC is already running, restart it so it sees the new files.");
        }
        catch (Exception ex)
        {
            AddProblem("error", "Play in MCC: " + ex.Message, scenario + ".scenario");
        }
        finally
        {
            IsBuildingForMcc = false;
            _mccBuild?.Dispose();
            _mccBuild = null;
        }
    }

    /// <summary>
    /// A campaign mission in MCC: built like a multiplayer map, then listed as the mod's campaign (one mission) with an
    /// insertion point per zone set the mission starts in (its first zone set).
    /// </summary>
    private async Task PlayCampaignInMccAsync(MccMod mod, string scenario)
    {
        string mapName = System.IO.Path.GetFileName(scenario);
        var points = new List<(string Title, string ZoneSet)>();
        try
        {
            var data = await Host!.GetPlacementsAsync(scenario + ".scenario");
            if (data.ZoneSets is { Count: > 0 } zs) points.Add(("Start", zs[0].Name ?? ""));
        }
        catch (HostException ex) { Log("Play in MCC: zone sets unavailable (" + ex.Message + ")."); }
        Log($"Play in MCC: building the campaign mission {scenario} with tool.exe. A mission takes longer than a multiplayer map (often 10 minutes or more); Stop build in the Play menu cancels it.");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool built = await mod.BuildMapAsync(scenario, line => Post(() => AppendOutput("[tool] " + line)), _mccBuild!.Token);
        if (!built)
        {
            AddProblem("error", $"Play in MCC: tool.exe could not build {mapName}.map. Its messages are in Output (search for [tool]).", scenario + ".scenario");
            return;
        }
        Log($"Play in MCC: {mapName}.map built in {clock.Elapsed.TotalMinutes:F1} min.");
        mod.SetCampaignMap(scenario, mapName + " (Kalo)", points);
        bool registered = mod.RegisterWithMcc();
        Log($"Play in MCC: the mod \"Kalo playtest\" now holds the campaign mission {mapName} ({mod.ModFolder})" +
            (registered ? $"; MCC now looks for mods in {mod.Root}." : ".") +
            " Start MCC from Steam with mods (anti-cheat off), then Campaign → Halo: Reach and pick the mod's campaign \"Kalo playtest\". " +
            "Kalo has not yet checked campaign mods in MCC itself: if the mission is not listed, tell Claude what MCC shows.");
    }

    /// <summary>The game menu folder (Megalo category) Kalo lists your game types in, in MCC.</summary>
    public const string MccCategory = "action_sack";

    [RelayCommand]
    private void StopMccBuild() => _mccBuild?.Cancel();

    partial void OnIsBuildingForMccChanged(bool value) => OnPropertyChanged(nameof(PlayInMccSummary));

    /// <summary>A "-- title: My Mode" line near the top of a gametype script names it in MCC.</summary>
    private string? GametypeTitle(string relativePath)
    {
        var open = Tabs.OfType<ScriptEditorViewModel>().FirstOrDefault(t => t.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
        string text = open?.Text ?? ScriptEditorViewModel.ReadScript(System.IO.Path.Combine(Paths!.Workspace, relativePath)).Text;
        foreach (var raw in text.Split('\n').Take(10))
        {
            string l = raw.Trim().TrimStart('-', ';', '/').Trim();
            if (l.StartsWith("title:", StringComparison.OrdinalIgnoreCase)) return l[6..].Trim();
        }
        return null;
    }
}
