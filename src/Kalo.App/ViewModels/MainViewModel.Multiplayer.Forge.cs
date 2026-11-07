using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Protocol;
using Kalo.Core.Scripting.Multiplayer;

namespace Kalo.App.ViewModels;

/// <summary>
/// Export as Forge variant: turns a map made from one of Reach's own maps (e.g. a Forge World copy) into a Forge map
/// variant of that map, which vanilla MCC can load (no mods). The game itself writes the variant, so its format is
/// exactly the game's: Kalo makes a copy of the map that is the stock map (its map id) with only objects of the map's
/// Forge menu, plays it in reach_tag_test, has the game encode its map variant (kalo_write_map_variant, a command of
/// Kalo's xinput1_4.dll), and writes the MCC .mvar file (MapVariantFile) to Kalo's folder and MCC's map variants.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The Forge object that replaces invisible markers (not in any Forge menu): Objectives › Hill Marker.</summary>
    public const string ForgeMarker = @"objects\multi\models\mp_hill_beacon\mp_hill_beacon.crate";
    private const string ForgeExportGametype = @"data\multiplayer\megalo\mods\_kalo_forge\forge_export.kscript";

    [ObservableProperty] private bool _isExportingForge;

    /// <summary>The last exported variant file (Kalo's copy), for tests.</summary>
    public string? LastForgeVariant { get; private set; }

    [RelayCommand]
    public async Task ExportForgeVariantAsync()
    {
        if (Paths == null || Host == null || Game == null || SelectedScenario == null || IsExportingForge) return;
        IsExportingForge = true;
        BottomPanel = "activity";
        string scenario = SelectedScenario;
        string map = System.IO.Path.GetFileName(scenario);
        string copy = $@"levels\mods\_kalo_play\{map}_forge\{map}_forge.scenario";
        try
        {
            foreach (var doc in Tabs.Where(t => t.IsDirty).ToList()) await doc.SaveDocumentAsync();
            if (IsGameRunning) Game.Stop();

            var r = await Host.PlacementOpAsync(new PlacementOpParams
            {
                Path = scenario + ".scenario", Op = PlacementOps.MakeForgeCopy, Tag = copy, Data = ForgeMarker,
            });
            Log($"Export as Forge variant: {map} is built on {r.BaseScenario} (map id {r.MapId}); {r.Kept} Forge objects kept.");
            if (r.Clamped > 0)
                Log($"Export as Forge variant: {r.Clamped} boundar{(r.Clamped == 1 ? "y is" : "ies are")} larger than Forge allows (200): set to 200, as MCC does.");
            if (r.Replaced?.Count > 0)
                Log($"Export as Forge variant: {r.Replaced.Count} invisible marker(s) become Hill Markers, with their labels: " + Describe(r.Replaced) + ".");
            if (r.Removed?.Count > 0)
                AddProblem("warning", $"Export as Forge variant: {r.Removed.Count} object(s) aren't in {System.IO.Path.GetFileNameWithoutExtension(r.BaseScenario)}'s Forge menu, so the Forge version leaves them out: " + Describe(r.Removed) + ".", scenario + ".scenario");

            // Any Megalo gametype will do: the variant is the map's objects. A tiny one keeps the start fast.
            string gt = System.IO.Path.Combine(Paths.Workspace, ForgeExportGametype);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(gt)!);
            await File.WriteAllTextAsync(gt, "-- title: Kalo Forge export\r\ndeclare global.number kalo_unused = 0\r\ndo\r\n  kalo_unused = 0\r\nend\r\n");
            string? engine = await DeployGametypeAsync(ForgeExportGametype);
            if (engine == null) return;
            var commands = EngineCommands.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.Replace("{engine}", engine)).ToList();

            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLoaded() => loaded.TrySetResult();
            Game.Loaded += OnLoaded;
            string chunkFile = System.IO.Path.Combine(Paths.Workspace, $"kalo_forge_{map}.mvar_chunk");
            if (File.Exists(chunkFile)) File.Delete(chunkFile);
            try
            {
                StartGame(copy[..^".scenario".Length], commands, $"Export as Forge variant: loading the Forge version of {map} in the game.");
                if (await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(3))) != loaded.Task)
                {
                    AddProblem("error", "Export as Forge variant: the game didn't finish loading the map.", scenario + ".scenario");
                    return;
                }
                // The map variant settles a few seconds after the load (zone set activation). Then the game encodes it
                // (Kalo's own console command, see tools\xinput: the test build's net_build_map_variant can't write files).
                await Task.Delay(TimeSpan.FromSeconds(8));
                if (!await Game.RunConsoleAsync(new[] { $"kalo_write_map_variant {chunkFile}" }, TimeSpan.FromSeconds(30)))
                {
                    AddProblem("error", "Export as Forge variant: the game didn't run the export command. Rebuild Kalo so the game gets Kalo's current xinput1_4.dll.", scenario + ".scenario");
                    return;
                }
            }
            finally
            {
                Game.Loaded -= OnLoaded;
                Game.Stop();
            }
            if (!File.Exists(chunkFile))
            {
                AddProblem("error", "Export as Forge variant: the game couldn't build the map variant (see Output for map_variant messages).", scenario + ".scenario");
                return;
            }
            byte[] chunk = await File.ReadAllBytesAsync(chunkFile);
            File.Delete(chunkFile);
            string title = map;
            byte[] mvar = Kalo.Core.Scripting.Multiplayer.MapVariantFile.ToMcc(chunk, title, "Made with Kalo from " + map + ".");
            string folder = System.IO.Path.Combine(Paths.KaloData, "forge-variants");
            System.IO.Directory.CreateDirectory(folder);
            string mine = System.IO.Path.Combine(folder, map + ".mvar");
            await File.WriteAllBytesAsync(mine, mvar);
            LastForgeVariant = mine;
            var mcc = MccMod.SetMapVariant("kalo_" + map + ".mvar", mvar);
            Log($"Export as Forge variant: \"{title}\" is a Forge variant of {System.IO.Path.GetFileNameWithoutExtension(r.BaseScenario)} " +
                $"({Kalo.Core.Scripting.Multiplayer.MapVariantFile.ChunkData(chunk).Length:N0} bytes), saved in {mine}" +
                (mcc.Count > 0 ? " and in MCC's map variants (" + string.Join(", ", mcc) + ")." : ". MCC's map folder (LocalFiles\\<id>\\HaloReach\\Map) wasn't found: copy the file there."));
            Log($"Export as Forge variant: in MCC (mods off is fine), Custom Games \u203a Halo: Reach \u203a map: pick {System.IO.Path.GetFileNameWithoutExtension(r.BaseScenario)}, then the variant \"{title}\". Restart MCC if it was running.");
        }
        catch (HostException ex)
        {
            AddProblem("error", "Export as Forge variant: " + ex.Message, scenario + ".scenario");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AddProblem("error", "Export as Forge variant: " + ex.Message, scenario + ".scenario");
        }
        finally
        {
            IsExportingForge = false;
        }
    }

    /// <summary>"tag|label" entries as "name (label)", grouped.</summary>
    private static string Describe(IEnumerable<string> entries) =>
        string.Join(", ", entries.Select(e =>
        {
            string[] parts = e.Split('|');
            string what = System.IO.Path.GetFileNameWithoutExtension(parts[0]);
            return parts.Length > 1 && parts[1].Length > 0 ? $"{what} \"{parts[1]}\"" : what;
        }).GroupBy(s => s).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key));
}
