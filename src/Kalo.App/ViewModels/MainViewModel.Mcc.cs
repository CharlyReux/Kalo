using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;

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

    public string PlayInMccSummary => $"Build {System.IO.Path.GetFileName(SelectedScenario ?? "?")} into a real map and play it with {SelectedGametype?.Label ?? "its default gametype"} in MCC's Custom Games (takes a few minutes; not live like Play).";

    [RelayCommand]
    public async Task PlayInMccAsync()
    {
        if (Paths == null || SelectedScenario == null || IsBuildingForMcc) return;
        IsBuildingForMcc = true;
        BottomPanel = "output";
        _mccBuild = new CancellationTokenSource();
        var mod = new MccMod(Paths);
        string scenario = SelectedScenario;
        string mapName = System.IO.Path.GetFileName(scenario);
        try
        {
            foreach (var doc in Tabs.Where(t => t.IsDirty).ToList()) await doc.SaveDocumentAsync();

            // The gametype first: a script error should not cost a five-minute build.
            (string File, byte[] Data, string Title)? gametype = null;
            if (SelectedGametype?.Path is { } script)
            {
                string? engine = await DeployGametypeAsync(script);
                if (engine == null) { Log("Play in MCC: stopped, the gametype does not compile (see Problems)."); return; }
                string bin = System.IO.Path.Combine(Paths.Workspace, "maps", "megalo", engine + ".bin");
                string title = GametypeTitle(script) ?? System.IO.Path.GetFileNameWithoutExtension(script).Replace('_', ' ');
                gametype = (engine + ".bin", Kalo.Core.Scripting.GameVariantFile.ToMcc(await File.ReadAllBytesAsync(bin), title, "Kalo playtest"), title);
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
            var placed = gametype is { } g ? mod.SetGameVariant(g.File, g.Data) : new List<string>();
            bool registered = mod.RegisterWithMcc();

            Log($"Play in MCC: the mod \"Kalo playtest\" is ready in {mod.ModFolder}" +
                (registered ? $" and MCC now looks for mods in {mod.Root} ({MccMod.ManifestFile})." : "."));
            if (gametype is { } gt)
                Log($"Play in MCC: game type \"{gt.Title}\" put in " + string.Join(" and ", placed) +
                    (placed.Count == 1 ? ". (MCC's own game type folder, LocalFiles\\<id>\\HaloReach\\GameType, appears once MCC has saved a game type; Kalo will put it there too then.)" : "."));
            Log("Play in MCC: start MCC from Steam with mods (anti-cheat off), then Custom Games → Halo: Reach: pick the map " +
                $"\"{mapName} (Kalo)\"" + (gametype is { } t ? $" and the game type \"{t.Title}\"" : "") + ". If MCC is already running, restart it so it sees the new files.");
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
