using System.Diagnostics;
using System.Numerics;
using Kalo.App.ViewModels;
using Kalo.App.Views;
using Kalo.Protocol;
using Kalo.Core.Scripting.Multiplayer;
using Kalo.Core.Scripting.Campaign;
using Kalo.Core.Scripting.Docs;

namespace Kalo.App;

/// <summary>
/// --tour: walks through the night's features with checks and screenshots (Kalo window only):
/// block editing and the reference picker, the map editor (geometry, select, move, duplicate, delete),
/// the gametype script editor (compile, error, fix) and mod projects (create, record, export, clean up).
/// Nothing is saved to the workspace; the map and the weapon are closed unsaved.
/// </summary>
public sealed class Tour
{
    private readonly MainWindow _window;
    private readonly MainViewModel _vm;
    private readonly string _dir;
    private readonly double _hold;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _failures, _shot;

    public Tour(MainWindow window, MainViewModel vm, AppArgs args)
    {
        _window = window;
        _vm = vm;
        _dir = args.Tour!;
        _hold = args.SelfTestHold;
    }

    public async Task<int> RunAsync()
    {
        Say("tour started");
        Directory.CreateDirectory(_dir);
        try
        {
            await Task.WhenAny(_vm.Ready, Task.Delay(TimeSpan.FromSeconds(60)));
            if (Check(_vm.Host != null, "Blam Host is running"))
            {
                await Section("blocks", Blocks);
                await Section("map", Map);
                await Section("script", Script);
                await Section("studio", Studio);
                await Section("split", SplitEditors);
                await Section("kscript", KaloScriptSection);
                await Section("script objects", ScriptObjectsSection);
                await Section("docs", DocsSection);
                await Section("forge", Forge);
                // Opens a whole campaign mission (minutes the first time): only when asked (--tour-sections campaign).
                if (AppArgs.Current.TourSections?.Contains("campaign", StringComparer.OrdinalIgnoreCase) == true) await Section("campaign", Campaign);
                if (AppArgs.Current.TourSections?.Contains("squads", StringComparer.OrdinalIgnoreCase) == true) await Section("squads", Squads);
                if (AppArgs.Current.TourSections?.Contains("mission", StringComparer.OrdinalIgnoreCase) == true) await Section("mission", MissionKaloScript);
                if (AppArgs.Current.TourSections?.Contains("squad-tools", StringComparer.OrdinalIgnoreCase) == true) await Section("squad-tools", SquadTools);
                if (AppArgs.Current.TourSections?.Contains("live-mission", StringComparer.OrdinalIgnoreCase) == true) await Section("live-mission", LiveMission);
                if (AppArgs.Current.TourSections?.Contains("campaign-perf", StringComparer.OrdinalIgnoreCase) == true) await Section("campaign-perf", CampaignPerf);
                if (AppArgs.Current.TourSections?.Contains("empty-mission", StringComparer.OrdinalIgnoreCase) == true) await Section("empty-mission", EmptyMission);
                if (AppArgs.Current.TourSections?.Contains("barn-view", StringComparer.OrdinalIgnoreCase) == true) await Section("barn-view", BarnView);
                if (AppArgs.Current.TourSections?.Contains("tutorial", StringComparer.OrdinalIgnoreCase) == true) await Section("tutorial", TutorialMission);
                if (AppArgs.Current.TourSections?.Contains("level-pieces", StringComparer.OrdinalIgnoreCase) == true) await Section("level-pieces", LevelPiecesTest);
                if (AppArgs.Current.TourSections?.Contains("weapon-shot", StringComparer.OrdinalIgnoreCase) == true) await Section("weapon-shot", WeaponShot);
                if (AppArgs.Current.TourSections?.Contains("loadouts", StringComparer.OrdinalIgnoreCase) == true) await Section("loadouts", LoadoutsSection);
                if (AppArgs.Current.TourSections?.Contains("templates", StringComparer.OrdinalIgnoreCase) == true) await Section("templates", TemplatesSection);
                if (AppArgs.Current.TourSections?.Contains("zones-play", StringComparer.OrdinalIgnoreCase) == true) await Section("zones-play", ZonesPlay);
                if (AppArgs.Current.TourSections?.Contains("volumes-play", StringComparer.OrdinalIgnoreCase) == true) await Section("volumes-play", VolumesPlay);
                if (AppArgs.Current.TourSections?.Contains("new-weapon", StringComparer.OrdinalIgnoreCase) == true) await Section("new-weapon", NewWeapon);
                if (AppArgs.Current.TourSections?.Contains("weapon-play", StringComparer.OrdinalIgnoreCase) == true) await Section("weapon-play", WeaponPlay);
                if (AppArgs.Current.TourSections?.Contains("ai-play", StringComparer.OrdinalIgnoreCase) == true) await Section("ai-play", AiPlay);
                if (AppArgs.Current.TourSections?.Contains("attach-units", StringComparer.OrdinalIgnoreCase) == true) await Section("attach-units", AttachUnits);
                if (AppArgs.Current.TourSections?.Contains("m10-ai", StringComparer.OrdinalIgnoreCase) == true) await Section("m10-ai", M10Ai);
                if (AppArgs.Current.TourSections?.Contains("ai-matrix", StringComparer.OrdinalIgnoreCase) == true) await Section("ai-matrix", AiMatrix);
                if (AppArgs.Current.TourSections?.Contains("ai-demo", StringComparer.OrdinalIgnoreCase) == true) await Section("ai-demo", AiDemoMission);
                if (AppArgs.Current.TourSections?.Contains("cameras", StringComparer.OrdinalIgnoreCase) == true) await Section("cameras", CamerasSection);
                if (AppArgs.Current.TourSections?.Contains("gear-team-picture", StringComparer.OrdinalIgnoreCase) == true) await Section("gear-team-picture", () => InTourModAsync(GearTeamPicture));
                if (AppArgs.Current.TourSections?.Contains("camera-match", StringComparer.OrdinalIgnoreCase) == true) await Section("camera-match", CameraMatch);
                if (AppArgs.Current.TourSections?.Contains("firefight", StringComparer.OrdinalIgnoreCase) == true) await Section("firefight", () => InTourModAsync(FirefightCopy));
                // Switching mods changes the shared workspace: never do it while someone else's Kalo is open.
                bool otherKalo = Process.GetProcessesByName("kalo").Length > 1;
                if (otherKalo) Say("another Kalo window is open: skipping the project and new-mod tests (they switch mods)");
                else
                {
                    await Section("projects", Projects);
                    await Section("new mod", NewMod);
                }
            }
        }
        catch (Exception ex)
        {
            Check(false, "unexpected error: " + ex);
        }
        Say(_failures == 0 ? "TOUR PASS" : $"TOUR FAIL ({_failures} failed check(s))");
        return _failures == 0 ? 0 : 1;
    }

    private async Task Section(string name, Func<Task> body)
    {
        var only = AppArgs.Current.TourSections;
        if (only != null && !only.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        Say("== " + name);
        try { await body(); }
        catch (Exception ex) { Check(false, name + " threw: " + ex); }
    }

    /// <summary>
    /// --play-map: plays a map exactly as saved (no edits, nothing saved) with Slayer or the given gametype, holds the
    /// game so the runner can take screenshots, then stops it. For diagnosing "my object isn't in the game".
    /// </summary>
    /// <summary>--play-mcc: Play in MCC on a map (and gametype), then checks the mod files it made.</summary>
    public async Task<int> PlayInMccAsync(string scenario, string? gametype)
    {
        Say("play-mcc started: " + scenario);
        try
        {
            await Task.WhenAny(_vm.Ready, Task.Delay(TimeSpan.FromSeconds(60)));
            string name = scenario.Replace('/', '\\');
            if (name.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)) name = name[..^".scenario".Length];
            if (!Check(_vm.Scenarios.Contains(name, StringComparer.OrdinalIgnoreCase), "the map is in the Play list")) return 1;
            _vm.SelectedScenario = _vm.Scenarios.First(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
            _vm.SelectedGametype = gametype == null ? _vm.GametypeChoices[0]
                : _vm.GametypeChoices.FirstOrDefault(g => g.Path?.Replace('/', '\\').EndsWith(gametype.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) == true);
            Check(_vm.SelectedGametype != null, "gametype " + (_vm.SelectedGametype?.Label ?? gametype));
            var sw = Stopwatch.StartNew();
            await _vm.PlayInMccAsync();
            Say($"Play in MCC took {sw.Elapsed.TotalMinutes:F1} min");
            foreach (var pr in _vm.Problems.Take(5)) Say($"problem: {pr.Severity}: {pr.Message}");
            var mod = new Kalo.Core.MccMod(_vm.Paths!);
            string map = System.IO.Path.GetFileName(name);
            Check(File.Exists(System.IO.Path.Combine(mod.ModFolder, "maps", map + ".map")), "the mod has the built map");
            Check(File.Exists(System.IO.Path.Combine(mod.ModFolder, "multiplayer", map + ".json")), "the mod has the map's description");
            Check(File.Exists(System.IO.Path.Combine(mod.ModFolder, "ModInfo.json")), "the mod has its ModInfo.json");
            int variants = Directory.GetFiles(System.IO.Path.Combine(mod.ModFolder, "haloreach", "game_variants"), "kalo_*.bin").Length;
            int mine = _vm.GametypeChoices.Count(g => g.Path?.Contains(@"\mods\", StringComparison.OrdinalIgnoreCase) == true);
            Check(variants == mine && !_vm.Problems.Any(p => p.Severity == "error"), $"the mod has all {mine} game type(s) of the mod, compiled in the Action Sack folder ({variants})");
            Check(File.Exists(Kalo.Core.MccMod.ManifestFile) && File.ReadAllLines(Kalo.Core.MccMod.ManifestFile).Any(l => l.Trim().Equals(mod.Root, StringComparison.OrdinalIgnoreCase)),
                "MCC's ModManifest.txt lists Kalo's mods folder");
            foreach (string l in _vm.OutputLines.Where(l => l.Contains("Play in MCC", StringComparison.Ordinal))) Say(l.Length > 300 ? l[..300] : l);
        }
        catch (Exception ex) { Check(false, "unexpected error: " + ex.Message); }
        return 0;
    }

    /// <summary>--export-forge: Export as Forge variant on a map, checks the file, and copies it to .kalo\inspect for diagnosis.</summary>
    public async Task<int> ExportForgeAsync(string scenario)
    {
        Say("export-forge started: " + scenario);
        try
        {
            await Task.WhenAny(_vm.Ready, Task.Delay(TimeSpan.FromSeconds(60)));
            string name = scenario.Replace('/', '\\');
            if (name.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)) name = name[..^".scenario".Length];
            if (!Check(_vm.Scenarios.Contains(name, StringComparer.OrdinalIgnoreCase), "the map is in the Play list")) return 1;
            _vm.SelectedScenario = _vm.Scenarios.First(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
            var sw = Stopwatch.StartNew();
            await _vm.ExportForgeVariantAsync();
            Say($"Export took {sw.Elapsed.TotalSeconds:F0} s");
            foreach (var pr in _vm.Problems.Take(8)) Say($"problem: {pr.Severity}: {pr.Message}");
            foreach (string l in _vm.OutputLines.Where(l => l.Contains("Forge", StringComparison.Ordinal) || l.Contains("map_variant", StringComparison.Ordinal))) Say(l.Length > 400 ? l[..400] : l);
            string? file = _vm.LastForgeVariant;
            if (Check(file != null && File.Exists(file), "the variant file was written: " + file))
            {
                Say($"variant size {new FileInfo(file!).Length} bytes");
                string inspect = System.IO.Path.Combine(_vm.Paths!.KaloData, "inspect");
                Directory.CreateDirectory(inspect);
                File.Copy(file!, System.IO.Path.Combine(inspect, System.IO.Path.GetFileName(file!)), true);
            }
            await _vm.RefreshChangesAsync();
            Check(!string.IsNullOrEmpty(_vm.VanillaSummary), "vanilla check: " + _vm.VanillaSummary);
            foreach (var item in _vm.Vanilla?.Items ?? Array.Empty<Kalo.Core.VanillaItem>()) Say($"vanilla: {item.Status} {item.Path}: {item.Reason}");
        }
        catch (Exception ex) { Check(false, "unexpected error: " + ex.Message); }
        return _failures == 0 ? 0 : 1;
    }

    public async Task<int> PlayMapAsync(string scenario, string? gametype)
    {
        Say("play-map started: " + scenario);
        try
        {
            await Task.WhenAny(_vm.Ready, Task.Delay(TimeSpan.FromSeconds(60)));
            string name = scenario.Replace('/', '\\');
            if (name.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)) name = name[..^".scenario".Length];
            // Test maps (levels\mods\_kalo_...) are kept out of the Play list: add this one, as opening it would.
            if (!_vm.Scenarios.Contains(name, StringComparer.OrdinalIgnoreCase) && name.Contains(@"\_kalo_", StringComparison.OrdinalIgnoreCase)) _vm.Scenarios.Insert(0, name);
            Check(_vm.Scenarios.Contains(name, StringComparer.OrdinalIgnoreCase), "the map is in the Play list");
            _vm.SelectedScenario = _vm.Scenarios.First(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (_vm.PlayIsCampaign) Say($"campaign mission: {_vm.SelectedDifficulty?.Label}, {_vm.SelectedInsertion?.Label}");
            var choice = gametype == null
                ? _vm.GametypeChoices.FirstOrDefault(g => string.Equals(System.IO.Path.GetFileName(g.Path), "slayer.txt", StringComparison.OrdinalIgnoreCase))
                : _vm.GametypeChoices.FirstOrDefault(g => g.Path?.Replace('/', '\\').EndsWith(gametype.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) == true);
            Check(choice != null, "gametype " + (choice?.Label ?? gametype ?? "slayer"));
            _vm.SelectedGametype = choice;
            Check(!_vm.Tabs.Any(t => t.IsDirty), "nothing unsaved (Play would save it)");
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _vm.Game!.Loaded += () => loaded.TrySetResult();
            // The game's own messages about the gametype (variant loading, megalo, script errors).
            var gametypeErrors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            _vm.Game.Output += l =>
            {
                if (l.Contains("main_game_start:") || (l.Contains("megalogamengine:") && l.Contains("fail"))) gametypeErrors.Enqueue(l);
                if (System.Text.RegularExpressions.Regex.IsMatch(l, "variant|megalo|main_game|FATAL|hs:|game engine|game_engine", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    && !l.Contains("objects: model") && !l.Contains("spawn influencer"))
                    Say("game: " + (l.Length > 220 ? l[..220] : l));
            };
            _vm.Game.Exited += _ => { exited.TrySetResult(); loaded.TrySetException(new Exception("the game exited while loading")); };
            var sw = Stopwatch.StartNew();
            await _vm.PlayAsync();
            if (!_vm.IsGameRunning)
            {
                // Play stopped before launching (usually the gametype): say why instead of waiting for a game that never comes.
                foreach (var pr in _vm.Problems.Take(5)) Say($"problem: {pr.Severity}: {pr.Message}");
                Check(false, "the game did not start");
                return 1;
            }
            var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6)));
            Check(done == loaded.Task && loaded.Task.IsCompletedSuccessfully, $"the game loaded the map ({sw.Elapsed.TotalSeconds:F0} s)");
            if (gametype != null) Check(gametypeErrors.IsEmpty, "the game started the gametype" + (gametypeErrors.TryPeek(out var ge) ? ": " + ge : ""));
            Say($"holding the game for {_hold:F0} s");
            await Task.Delay(TimeSpan.FromSeconds(_hold));
            _vm.StopGameCommand.Execute(null);
            await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Check(!_vm.Game.IsRunning, "stopped the game");
        }
        catch (Exception ex)
        {
            Check(false, "unexpected error: " + ex.Message);
        }
        finally
        {
            if (_vm.Game?.IsRunning == true) _vm.Game.Stop();
        }
        Say(_failures == 0 ? "PLAYMAP PASS" : $"PLAYMAP FAIL ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// --crate-probe: does a Forge block placed by Kalo exist in game on Forge World? Makes a throwaway map from
    /// forge_halo (empty), puts ff_plat_4x4_flat under the start location with the given multiplayer quota maximum,
    /// plays it (runner screenshots show whether the player stands or falls), then deletes the map.
    /// </summary>
    public async Task<int> CrateProbeAsync(string variant)
    {
        Say($"crate probe started ({variant})");
        string name = "probe_" + variant;
        string path = $@"levels\mods\_kalo_tour\{name}\{name}.scenario";
        string folder = System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, $@"levels\mods\_kalo_tour\{name}");
        const string block = @"objects\levels\forge\ff_plat_4x4_flat\ff_plat_4x4_flat.crate";
        try
        {
            await Task.WhenAny(_vm.Ready, Task.Delay(TimeSpan.FromSeconds(60)));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            if (variant == "ulcopy")
            {
                // Charly's map, copied (his file is not touched): opening it applies the fixes, then play it.
                const string ul = @"levels\mods\test_project\ul\ul.scenario";
                Directory.CreateDirectory(folder);
                await _vm.Host!.OpenTagAsync(ul);
                await _vm.Host.SaveTagAsAsync(ul, path);
                await _vm.Host.CloseTagAsync(path);
                var copy = await _vm.OpenMapAsync(path);
                Check(copy.IsDirty, "opening the copy repaired it (it no longer reads Forge World's object resources)");
                Say($"copy: {copy.Items.Count(i => i.Category == "crates")} crates, {copy.Items.Count(i => i.Category == "scenery")} scenery");
                await _vm.Host.SaveTagAsync(path);
                copy.IsDirty = false;
                foreach (var t in _vm.Tabs.ToList()) await _vm.CloseTabCommand.ExecuteAsync(t);
                if (!_vm.Scenarios.Contains(path[..^".scenario".Length])) _vm.Scenarios.Insert(0, path[..^".scenario".Length]);
                await PlayMapAsync(path, null);
                return _failures == 0 ? 0 : 1;
            }
            string template = variant.Contains("sword") ? @"levels\multi\20_sword_slayer\20_sword_slayer.scenario" : @"levels\multi\forge_halo\forge_halo.scenario";
            bool bare = variant.Contains("barebone");
            if (bare) template = BareboneMap.Template;
            var r = await _vm.Host!.CreateMapAsync(new CreateMapParams { Template = template, NewPath = path, Empty = !variant.Contains("full"), Barebone = bare, MapId = 7990 + Math.Abs(variant.GetHashCode() % 9) });
            Check(true, $"probe map created ({r.Kept} spawns kept)");
            var map = await _vm.OpenMapAsync(path);
            if (variant.StartsWith("dup"))
            {
                // A Bungie weapon (W), Kalo's duplicate of it (D) and a weapon added fresh by Kalo (F), side by side in
                // front of the start: which of them exist in game says which fields matter.
                var spawns = map.Items.Where(i => i.Category == "scenery" && i.Tag.Contains("respawn_point")).ToList();
                var weapons = map.Items.Where(i => i.Category == "weapons").ToList();
                var pair = weapons.SelectMany(w => spawns.Select(sp => (w, sp, d: Vector3.Distance(w.Position, sp.Position))))
                                  .Where(x => x.d > 2.5f && Math.Abs(x.w.Position.Z - x.sp.Position.Z) < 0.6f).OrderBy(x => x.d).FirstOrDefault();
                Check(pair.w != null, $"Bungie weapon {pair.w?.Tag} {pair.d:F1} units from a spawn point");
                if (pair.w == null) return 1;
                var wp = pair.w.Position; var sp0 = pair.sp.Position;
                var dir = Vector3.Normalize(new Vector3(wp.X - sp0.X, wp.Y - sp0.Y, 0));
                var side = new Vector3(-dir.Y, dir.X, 0);
                float yaw = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;
                Say($"W = {pair.w.Tag} [{pair.w.Index}] at {wp.X:F2}, {wp.Y:F2}, {wp.Z:F2}; start at {sp0.X:F2}, {sp0.Y:F2}, {sp0.Z:F2} facing {yaw:F0}");
                var dupR = await _vm.Host.PlacementOpAsync(new PlacementOpParams { Path = path, Category = "weapons", Op = PlacementOps.Duplicate, Index = pair.w.Index, Position = new[] { wp.X + side.X * 1.2f, wp.Y + side.Y * 1.2f, wp.Z } });
                Say($"D = duplicate [{dupR.SelectIndex}] on the left");
                var fresh = await map.AddTagAtAsync(pair.w.Tag, wp - side * 1.2f);
                Say($"F = fresh {fresh?.Tag} [{fresh?.Index}] on the right");
                async Task<int> Force(string field, string value) =>
                    (await _vm.Host.PlacementOpAsync(new PlacementOpParams { Path = path, Category = "weapons", Op = "debugForceField", Tag = field + "=" + value })).SelectIndex;
                string idOf(int i) => $"Block:weapons[{i}]/Struct:object data[0]/Struct:object id[0]/LongInteger:unique id";
                if (variant.Contains("idw"))
                {
                    // W keeps everything but gets a new unique id (as Kalo gives).
                    int back = await Force(idOf(pair.w.Index), "123456789");
                    Say($"W unique id changed, reads back {back}");
                }
                if (variant.Contains("rmw"))
                {
                    await _vm.Host.PlacementOpAsync(new PlacementOpParams { Path = path, Category = "weapons", Op = PlacementOps.Remove, Index = pair.w.Index });
                    Say("W removed: only D (left) and F (right) remain");
                }
                if (variant.Contains("idd"))
                {
                    // D gets W's original id (W keeps it too: a clash, just to see).
                    int back = await Force(idOf(dupR.SelectIndex), "-1538523124");
                    Say($"D unique id set to W's, reads back {back}");
                }
                var stl = map.Items.FirstOrDefault(i => i.Category == StartLocations.Category);
                if (stl != null)
                {
                    map.MoveLocal(stl, sp0 + new Vector3(0, 0, 0.1f));
                    stl.SetRotationLocal(yaw, 0, 0);
                    await map.CommitMoveAsync(stl);
                }
                async Task<TagDocument?> Read(int i) { try { return await _vm.Host.ReadElementAsync(path, "weapons", i); } catch { return null; } }
                bool removed = variant.Contains("rmw");
                var d1 = removed ? null : await Read(pair.w.Index);
                var d2 = await Read(dupR.SelectIndex - (removed ? 1 : 0));
                var d3 = fresh == null ? null : await Read(fresh.Index - (removed ? 1 : 0));
                string Row(TagDocument? d) => d == null ? "-" : string.Join(" | ", Flatten(d.Fields).Where(f => f.Value != null && f.Children == null || f.Children?.Count == 0).Select(f => (f.Name ?? "") + "=" + f.Value));
                Say("W fields: " + Row(d1));
                Say("D fields: " + Row(d2));
                Say("F fields: " + Row(d3));
                Directory.CreateDirectory(_dir);
                var until2 = DateTime.UtcNow.AddMinutes(3);
                while ((map.Bsp == null || map.ModelVersion == 0) && DateTime.UtcNow < until2) await Task.Delay(500);
                map.Shading = ViewShading.Textured;
                _vm.ActiveDocument = map;
                await Task.Delay(3000);
                map.LookFrom(sp0 + new Vector3(0, 0, 0.7f), yaw, -10);
                await Pause(1500);
                await map.CaptureViewAsync(System.IO.Path.Combine(_dir, variant + "-kalo-from-start.png"));
                await _vm.Host.SaveTagAsync(path);
                map.IsDirty = false;
                foreach (var t in _vm.Tabs.ToList()) await _vm.CloseTabCommand.ExecuteAsync(t);
                if (!_vm.Scenarios.Contains(path[..^".scenario".Length])) _vm.Scenarios.Insert(0, path[..^".scenario".Length]);
                await PlayMapAsync(path, null);
                return _failures == 0 ? 0 : 1;
            }
            if (variant.StartsWith("view"))
            {
                // view-nopalettes: no Forge palettes at all (like the barebone map); view-nocompat: palettes kept, compatibility CRCs cleared.
                if (variant.Contains("nopalettes")) await _vm.Host.BlockOpAsync(path, "Block:map variant palettes", BlockOps.Clear);
                if (variant.Contains("nocompat")) await _vm.Host.BlockOpAsync(path, "Block:map variant palettes compatibility", BlockOps.Clear);
                if (variant.Contains("nozonesets")) await _vm.Host.BlockOpAsync(path, "Block:zone sets", BlockOps.Clear);
                if (variant.Contains("nobudget")) await _vm.Host.SetFieldAsync(path, "Real:sandbox budget", "0");
                // On solid ground (a template spawn), facing +X: a tall Forge block, a packing crate and a DMR in front.
                // All around the start (whatever way the player faces): tall Forge blocks 7 units away, packing crates
                // and DMRs 3 units away.
                // Around a spawn point the template keeps (solid ground).
                var spawnPoint = map.Items.Where(i => i.Category == "scenery" && i.Tag.Contains("respawn_point")).Select(i => i.Position).DefaultIfEmpty(new Vector3(60.92f, 210.05f, 44.65f)).First();
                float cx = spawnPoint.X, cy = spawnPoint.Y, cz = spawnPoint.Z;
                Say($"probe center {cx:F2}, {cy:F2}, {cz:F2}");
                int placed = 0;
                foreach (var (dx, dy) in new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) })
                {
                    if (await map.AddTagAtAsync(@"objects\levels\forge\ff_plat_4x4_tall\ff_plat_4x4_tall.crate", new Vector3(cx + 7 * dx, cy + 7 * dy, cz)) != null) placed++;
                    if (await map.AddTagAtAsync(@"objects\gear\human\military\crate_packing\crate_packing.crate", new Vector3(cx + 3 * dx - 1.5f * dy, cy + 3 * dy + 1.5f * dx, cz)) != null) placed++;
                    if (await map.AddTagAtAsync(@"objects\weapons\rifle\dmr\dmr.weapon", new Vector3(cx + 2.5f * dx + 1f * dy, cy + 2.5f * dy - 1f * dx, cz + 0.25f)) != null) placed++;
                }
                Check(placed == 12, $"placed {placed}/12 objects around the start (tall blocks, packing crates, DMRs)");
                if (variant.Contains("bsp0"))
                {
                    // Bungie's own placements on Forge World have origin bsp index 0; Kalo's had -1.
                    int set = 0;
                    foreach (var it in map.Items.Where(i => i.Category is "crates" or "weapons").ToList())
                    {
                        await _vm.Host.SetFieldAsync(path, $"Block:{it.Category}[{it.Index}]/Struct:object data[0]/Struct:object id[0]/ShortBlockIndex:origin bsp index", "0");
                        set++;
                    }
                    Say($"origin bsp index = 0 on {set} objects");
                }
                // Kalo's own view from the start, to compare with the game's screenshot.
                Directory.CreateDirectory(_dir);
                var until = DateTime.UtcNow.AddMinutes(3);
                while ((map.Bsp == null || map.ModelVersion == 0) && DateTime.UtcNow < until) await Task.Delay(500);
                map.Shading = ViewShading.Textured;
                _vm.ActiveDocument = map;
                await Task.Delay(4000);
                map.LookFrom(new Vector3(cx, cy, cz + 0.7f), 0, 0);
                await Pause(1500);
                await map.CaptureViewAsync(System.IO.Path.Combine(_dir, variant + "-kalo-from-start.png"));
                map.LookFrom(new Vector3(cx - 9, cy, cz + 6), 0, -30);
                await Pause(1500);
                await map.CaptureViewAsync(System.IO.Path.Combine(_dir, variant + "-kalo-overview.png"));
                var st = map.Items.FirstOrDefault(i => i.Category == StartLocations.Category);
                if (st != null)
                {
                    map.MoveLocal(st, new Vector3(cx, cy, cz + 0.1f));
                    st.SetRotationLocal(0, 0, 0);
                    await map.CommitMoveAsync(st);
                }
                await _vm.Host.SaveTagAsync(path);
                map.IsDirty = false;
                foreach (var t in _vm.Tabs.ToList()) await _vm.CloseTabCommand.ExecuteAsync(t);
                if (!_vm.Scenarios.Contains(path[..^".scenario".Length])) _vm.Scenarios.Insert(0, path[..^".scenario".Length]);
                await PlayMapAsync(path, null);
                return _failures == 0 ? 0 : 1;
            }
            string tag = variant == "packing" ? @"objects\gear\human\military\crate_packing\crate_packing.crate" : block;
            var at = variant == "packing" ? new Vector3(64.64f, 139.29f, 42.92f) : new Vector3(64.23f, 138.6f, 42.92f);
            var obj = await map.AddTagAtAsync(tag, at);
            Check(obj != null, $"placed {System.IO.Path.GetFileName(tag)} at Charly's spot");
            if (obj != null && variant == "q0")
                await _vm.Host.SetFieldAsync(path, $"Block:crates[{obj.Index}]/Struct:multiplayer data[0]/CharInteger:quota maximum", "0");
            if (variant == "nopalette")
            {
                // Take the block out of the map's Forge palette (map variant palettes) so the game treats it as fixed scenery.
                var doc = await _vm.Host.OpenTagAsync(path);
                var hits = Flatten(doc.Fields).Where(f => f.Path != null && f.Path.StartsWith("Block:map variant palettes[") && f.Path.EndsWith("/Reference:object")
                                                         && string.Equals(f.Value, block, StringComparison.OrdinalIgnoreCase)).ToList();
                Check(hits.Count > 0, $"found the block in {hits.Count} Forge palette entr(ies)");
                foreach (var h in hits.OrderByDescending(h => h.Path, StringComparer.Ordinal))
                {
                    // ".../Block:variants[21]/Reference:object" → remove element 21 of ".../Block:variants".
                    string elem = h.Path![..h.Path.LastIndexOf('/')];
                    int open = elem.LastIndexOf('[');
                    int index = int.Parse(elem[(open + 1)..^1]);
                    await _vm.Host.BlockOpAsync(path, elem[..open], BlockOps.Remove, index);
                    Say("removed " + elem);
                }
            }
            var start = map.Items.FirstOrDefault(i => i.Category == StartLocations.Category);
            Check(start != null, "start location found");
            if (start != null)
            {
                map.MoveLocal(start, new Vector3(64.64f, 139.29f, variant == "packing" ? 45f : 43.3f));
                await map.CommitMoveAsync(start);
            }
            // Saved through the host directly: not recorded in the active mod project (it is deleted afterwards).
            await _vm.Host.SaveTagAsync(path);
            map.IsDirty = false;
            Check(true, "probe map saved");
            foreach (var t in _vm.Tabs.ToList()) await _vm.CloseTabCommand.ExecuteAsync(t);
            if (!_vm.Scenarios.Contains(path[..^".scenario".Length])) _vm.Scenarios.Insert(0, path[..^".scenario".Length]);
            await PlayMapAsync(path, null);
        }
        catch (Exception ex)
        {
            Check(false, "unexpected error: " + ex.Message);
        }
        finally
        {
            if (_vm.Game?.IsRunning == true) _vm.Game.Stop();
            await Task.Delay(3000);
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); Say("removed the probe map"); } catch (Exception ex) { Say("could not remove the probe map: " + ex.Message); }
        }
        Say(_failures == 0 ? "PROBE DONE" : $"PROBE FAIL ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// --playfrom-test: a throwaway Forge World map, the camera put on solid ground in the Coliseum facing a tall block,
    /// then Play from camera with N split-screen players. The runner screenshots the game; the copy must be gone after.
    /// </summary>
    public async Task<int> PlayFromTestAsync(int players)
    {
        Say($"play-from-camera test started ({players} player(s))");
        const string path = @"levels\mods\_kalo_tour\playfrom\playfrom.scenario";
        string folder = System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, @"levels\mods\_kalo_tour\playfrom");
        string copyFolder = System.IO.Path.Combine(_vm.Paths.WorkspaceTags, @"levels\mods\_kalo_play");
        try
        {
            await Task.WhenAny(_vm.Ready, Task.Delay(TimeSpan.FromSeconds(60)));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            await _vm.Host!.CreateMapAsync(new CreateMapParams { Template = @"levels\multi\forge_halo\forge_halo.scenario", NewPath = path, Empty = true, MapId = 7985 });
            var map = await _vm.OpenMapAsync(path);
            var until = DateTime.UtcNow.AddMinutes(3);
            while ((map.Bsp == null || map.ModelVersion == 0) && DateTime.UtcNow < until) await Task.Delay(500);
            // A template spawn on the Coliseum floor; a tall block 8 units ahead (+x) as a landmark.
            var center = map.Items.Where(i => i.Category == "scenery" && i.Tag.Contains("respawn_point")).Select(i => i.Position).DefaultIfEmpty(new Vector3(60.92f, 210.05f, 44.65f)).First();
            var block = await map.AddTagAtAsync(@"objects\levels\forge\ff_plat_4x4_tall\ff_plat_4x4_tall.crate", center + new Vector3(8, 0, 0));
            Check(block != null, "landmark block placed 8 units ahead");
            await _vm.Host.SaveTagAsync(path); // not through the map's Save: test maps stay out of the mod project
            map.IsDirty = false;
            _vm.ActiveDocument = map;
            map.Shading = ViewShading.Textured;
            map.LookFrom(center + new Vector3(0, 0, 1.6f), 0, -5);
            await Pause(2000);
            await map.CaptureViewAsync(System.IO.Path.Combine(_dir, "playfrom-kalo-camera.png"));
            Check(map.PlayFromProvider != null, "the 3D view gives the camera's start point");
            if (map.PlayFromProvider != null)
            {
                var (pos, facing) = map.PlayFromProvider();
                Check(Math.Abs(pos.Z - (center.Z + 0.1f)) < 0.6f, $"start point on the ground under the camera ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F2}; facing {facing:F0})");
            }
            // Safer Play here: high above the level there is no floor to land on.
            map.LookFrom(center + new Vector3(0, 0, 150f), 0, -5);
            await Pause(800);
            Check(map.PlayFromCheck?.Invoke() is { } noFloor && noFloor.Contains("no floor"), "Play here warns when there is no floor under the camera: " + map.PlayFromCheck?.Invoke());
            map.LookFrom(center + new Vector3(0, 0, 1.6f), 0, -5);
            await Pause(800);
            Check(map.PlayFromCheck?.Invoke() == null, "and not over the floor");
            _vm.SelectedPlayers = _vm.PlayerChoices.First(c => c.Count == players);
            _vm.SelectedGametype = _vm.GametypeChoices.FirstOrDefault(g => string.Equals(System.IO.Path.GetFileName(g.Path), "slayer.txt", StringComparison.OrdinalIgnoreCase)) ?? _vm.SelectedGametype;
            Check(_vm.SelectedGametype?.Path != null, $"gametype {_vm.SelectedGametype?.Label}");
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _vm.Game!.Loaded += () => loaded.TrySetResult();
            _vm.Game.Exited += _ => { exited.TrySetResult(); loaded.TrySetException(new Exception("the game exited while loading")); };
            var sw = Stopwatch.StartNew();
            await map.PlayFromCameraCommand.ExecuteAsync(null);
            Check(File.Exists(System.IO.Path.Combine(copyFolder, "playfrom", "playfrom.scenario")), "a play copy was made in levels\\mods\\_kalo_play");
            var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6)));
            Check(done == loaded.Task && loaded.Task.IsCompletedSuccessfully, $"the game loaded the play copy ({sw.Elapsed.TotalSeconds:F0} s)");
            if (players == 1 && loaded.Task.IsCompletedSuccessfully)
            {
                // Live game bar: move the player to another spot (on top of the landmark block), can't die.
                Check(map.IsGameRunning, "the Live game bar shows while the game runs");
                await Task.Delay(TimeSpan.FromSeconds(10));
                map.LookFrom(center + new Vector3(8, 0, 4f), 180, -10);
                await Pause(800);
                await map.LiveTeleportCommand.ExecuteAsync(null);
                Check(map.LiveStatus.StartsWith("Moved the player"), "Move me to the camera: " + map.LiveStatus);
                map.LiveInvincible = true;
                await Task.Delay(3000);
                Check(map.LiveStatus.Contains("can't die"), "Can't die: " + map.LiveStatus);
                Shot("live-bar");
            }
            Say($"holding the game for {_hold:F0} s");
            await Task.Delay(TimeSpan.FromSeconds(_hold));
            _vm.StopGameCommand.Execute(null);
            await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            await Task.Delay(1500);
            Check(!Directory.Exists(copyFolder), "the play copy is deleted when the game exits");
            Check(!_vm.Scenarios.Any(s => s.Contains("_kalo_play")), "the play copy is not in the Play list");
        }
        catch (Exception ex)
        {
            Check(false, "unexpected error: " + ex.Message);
        }
        finally
        {
            if (_vm.Game?.IsRunning == true) _vm.Game.Stop();
            foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
            await Task.Delay(1000);
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); Say("removed the test map"); } catch (Exception ex) { Say("could not remove the test map: " + ex.Message); }
        }
        Say(_failures == 0 ? "PLAYFROM PASS" : $"PLAYFROM FAIL ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    private static IEnumerable<FieldNode> Flatten(IEnumerable<FieldNode>? nodes)
    {
        foreach (var n in nodes ?? Enumerable.Empty<FieldNode>())
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    /// <summary>--map-play-test: edit Sword Slayer in the map view, save, play it, try a live edit, stop, revert.</summary>
    public async Task<int> MapPlayAsync()
    {
        Say("map play test started");
        const string scenario = @"levels\multi\20_sword_slayer\20_sword_slayer.scenario";
        try
        {
            await Task.WhenAny(_vm.Ready, Task.Delay(TimeSpan.FromSeconds(60)));
            var map = await _vm.OpenMapAsync(scenario);
            var weapon = map.Items.FirstOrDefault(i => i.Category == "weapons" && i.Name.Contains("rifle"))
                         ?? map.Items.FirstOrDefault(i => i.Category == "weapons");
            if (!Check(weapon != null, "found a weapon to copy")) return 1;
            map.Selected = weapon;
            await map.DuplicateSelectedCommand.ExecuteAsync(null);
            var copy = map.Selected;
            Check(copy != null && copy != weapon, $"placed a copy of {weapon!.Name} next to the original");
            await map.SaveCommand.ExecuteAsync(null);
            Check(!map.IsDirty && map.DiffersFromVanilla, "saved the scenario (now differs from vanilla)");

            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string>? reloaded = null;
            _vm.Game!.Loaded += () => loaded.TrySetResult();
            _vm.Game.Exited += _ => { exited.TrySetResult(); loaded.TrySetException(new Exception("the game exited while loading")); };
            _vm.Game.TagReloaded += t => { if (t.Contains("20_sword_slayer", StringComparison.OrdinalIgnoreCase)) reloaded?.TrySetResult(t); };
            var sw = Stopwatch.StartNew();
            map.PlayHereCommand.Execute(null);
            var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6)));
            Check(done == loaded.Task && loaded.Task.IsCompletedSuccessfully, $"the game loaded the edited map ({sw.Elapsed.TotalSeconds:F0} s)");
            await Task.Delay(TimeSpan.FromSeconds(8));

            // Live edit: move the copy and save while the game runs.
            if (copy != null)
            {
                reloaded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                map.MoveLocal(copy, copy.Position + new Vector3(0, 0, 1f));
                await map.CommitMoveAsync(copy);
                await map.SaveCommand.ExecuteAsync(null);
                var r = await Task.WhenAny(reloaded.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                Say(r == reloaded.Task ? "the running game reloaded the scenario after the save" : "the running game did not report a scenario reload within 30 s (placements may need a restart)");
            }
            Say($"holding the game for {_hold:F0} s");
            await Task.Delay(TimeSpan.FromSeconds(_hold));
            _vm.StopGameCommand.Execute(null);
            await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Check(!_vm.Game.IsRunning, "stopped the game");
        }
        catch (Exception ex)
        {
            Check(false, "unexpected error: " + ex.Message);
        }
        finally
        {
            if (_vm.Game?.IsRunning == true) _vm.Game.Stop();
            foreach (var t in _vm.Tabs.ToList()) await _vm.CloseTabCommand.ExecuteAsync(t);
            try { _vm.Baseline?.Revert(@"tags\" + scenario); } catch (Exception ex) { Say("revert failed: " + ex.Message); }
            await _vm.RefreshChangesAsync();
            Check(_vm.Baseline?.IsChanged(@"tags\" + scenario) == false, "scenario back to vanilla");
        }
        Say(_failures == 0 ? "MAPPLAY PASS" : $"MAPPLAY FAIL ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// --barebone-play-test: make a barebone map (platform + spawns on HREK's test floor) outside any mod, check it in
    /// the 3D view, play it, stop, delete it.
    /// </summary>
    public async Task<int> BarebonePlayAsync()
    {
        Say("barebone play test started");
        const string path = @"levels\mods\_kalo_tour\barebone\barebone.scenario";
        string folder = System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, @"levels\mods\_kalo_tour");
        try
        {
            await Task.WhenAny(_vm.Ready, Task.Delay(TimeSpan.FromSeconds(60)));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            var r = await _vm.Host!.CreateMapAsync(new CreateMapParams { Template = BareboneMap.Template, NewPath = path, Empty = true, Barebone = true, MapId = 7999 });
            Check(r.Kept == 4, $"barebone map created with {r.Kept} spawn points ({r.Ms} ms)");
            var map = await _vm.OpenMapAsync(path);
            var until = DateTime.UtcNow.AddMinutes(3);
            while ((map.Bsp == null || map.ModelVersion == 0) && DateTime.UtcNow < until) await Task.Delay(500);
            Check(map.Items.Count(i => i.Tag.Contains("respawn_point")) == 4 && map.Items.Any(i => i.Tag.Contains("ff_plat")), $"it has the platform and spawns ({map.Items.Count} objects)");
            // What Charly reported: start location moved, a weapon and a vehicle added, a scaled block, a weapon tag edit.
            var start = map.Items.FirstOrDefault(i => i.Category == StartLocations.Category);
            Check(start != null, "the start location is listed as an object");
            if (start != null)
            {
                map.MoveLocal(start, new Vector3(12, 12, 0.6f));
                start.SetRotationLocal(225, 0, 0);
                await map.CommitMoveAsync(start);
            }
            var rifle = await map.AddTagAtAsync(@"objects\weapons\rifle\dmr\dmr.weapon", new Vector3(10, 10, 0.2f));
            var hog = await map.AddTagAtAsync(@"objects\vehicles\human\warthog\warthog.vehicle", new Vector3(6.5f, 8.5f, 0.6f));
            Check(rifle != null && hog != null, "added a DMR and a Warthog");
            var block = await map.AddTagAtAsync(@"objects\levels\forge\ff_plat_2x2\ff_plat_2x2.crate", new Vector3(5, 3, 0));
            if (block != null) { map.SetSelection(new[] { block }); await map.SetScaleCommand.ExecuteAsync("3"); }
            var block2 = await map.AddTagAtAsync(@"objects\levels\forge\ff_plat_2x2\ff_plat_2x2.crate", new Vector3(3, 6, 0));
            Check(block2 != null && map.Items.Any(i => Math.Abs(i.PlacedScale - 3) < 0.01f), "two blocks, one scaled ×3");
            await map.SaveCommand.ExecuteAsync(null);
            var weapon = await _vm.OpenTagAsync(@"objects\weapons\rifle\assault_rifle\assault_rifle.weapon");
            var mag = weapon?.FindField("Block:magazines[0]/ShortInteger:rounds loaded maximum");
            if (weapon != null && mag != null)
            {
                mag.Value = "99";
                await Task.Delay(1500);
                await weapon.SaveCommand.ExecuteAsync(null);
                Check(!weapon.IsDirty, "assault rifle magazine set to 99 and saved");
            }
            map.Shading = ViewShading.Textured;
            await Task.Delay(4000);
            map.LookFrom(new Vector3(12, 12, 1.3f), 225, -8);
            await Pause(1500);
            await map.CaptureViewAsync(System.IO.Path.Combine(_dir, "barebone-from-start.png"));
            map.LookFrom(new Vector3(-9, -9, 6), 45, -25);
            await Pause(1500);
            await map.CaptureViewAsync(System.IO.Path.Combine(_dir, "barebone-view.png"));
            _vm.ActiveDocument = map;

            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _vm.Game!.Loaded += () => loaded.TrySetResult();
            _vm.Game.Exited += _ => { exited.TrySetResult(); loaded.TrySetException(new Exception("the game exited while loading")); };
            Check(_vm.SelectedGametype?.Path?.EndsWith("slayer.txt") == true, $"Play defaults to Slayer ({_vm.SelectedGametype?.Label})");
            var sw = Stopwatch.StartNew();
            map.PlayHereCommand.Execute(null);
            var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6)));
            Check(done == loaded.Task && loaded.Task.IsCompletedSuccessfully, $"the game loaded the barebone map ({sw.Elapsed.TotalSeconds:F0} s)");
            Say($"holding the game for {_hold:F0} s");
            await Task.Delay(TimeSpan.FromSeconds(_hold));
            _vm.StopGameCommand.Execute(null);
            await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Check(!_vm.Game.IsRunning, "stopped the game");
        }
        catch (Exception ex)
        {
            Check(false, "unexpected error: " + ex.Message);
        }
        finally
        {
            if (_vm.Game?.IsRunning == true) _vm.Game.Stop();
            foreach (var t in _vm.Tabs.ToList()) await _vm.CloseTabCommand.ExecuteAsync(t);
            try { _vm.Baseline?.Revert(@"tags\objects\weapons\rifle\assault_rifle\assault_rifle.weapon"); Say("assault rifle back to vanilla"); } catch (Exception ex) { Say("revert failed: " + ex.Message); }
            await Task.Delay(1000);
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); Say("removed the test map"); } catch (Exception ex) { Say("could not remove the test map: " + ex.Message); }
        }
        Say(_failures == 0 ? "BAREBONE PASS" : $"BAREBONE FAIL ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>--megalo-play-test: compile juggernaut, deploy it and start Sword Slayer with it; collect game log evidence.</summary>
    public async Task<int> MegaloPlayAsync()
    {
        Say("megalo play test started");
        string? mglo = null;
        try
        {
            await Task.WhenAny(_vm.Ready, Task.Delay(TimeSpan.FromSeconds(60)));
            var file = _vm.Scripts.FirstOrDefault(s => s.Name == "juggernaut") ?? _vm.Scripts.First();
            var script = await _vm.OpenScriptAsync(file.RelativePath);
            if (!Check(script != null, "opened " + file.Name)) return 1;
            var lines = new List<string>();
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _vm.Game!.Output += l => { lock (lines) lines.Add(l); };
            _vm.Game.Loaded += () => loaded.TrySetResult();
            _vm.Game.Exited += _ => loaded.TrySetException(new Exception("the game exited while loading"));
            _vm.SelectedScenario = @"levels\multi\20_sword_slayer\20_sword_slayer";
            Check(_vm.SelectedGametype?.Path == file.RelativePath, "the gametype picker follows the open script");
            await _vm.PlayCommand.ExecuteAsync(null);
            mglo = System.IO.Path.Combine(_vm.Paths!.Workspace, "maps", "megalo", "kalo_" + file.Name + ".mglo");
            Check(File.Exists(mglo), $"Play compiled and deployed {System.IO.Path.GetFileName(mglo)} ({(File.Exists(mglo) ? new FileInfo(mglo).Length : 0)} bytes)");
            Check(_vm.IsGameRunning, "started the game with the gametype");
            var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6)));
            Check(done == loaded.Task && loaded.Task.IsCompletedSuccessfully, "the map loaded");
            Say($"holding the game for {_hold:F0} s");
            await Task.Delay(TimeSpan.FromSeconds(_hold));
            string[] keys = { "megalo", "game engine", "game_engine", "variant", "juggernaut", "load_binary" };
            List<string> evidence;
            lock (lines) evidence = lines.Where(l => keys.Any(k => l.Contains(k, StringComparison.OrdinalIgnoreCase))).Take(25).ToList();
            Say($"{evidence.Count} game log lines mention the gametype:");
            foreach (var l in evidence) Say("   " + l);
            Check(!evidence.Any(l => l.Contains("load_binary_game_engine", StringComparison.OrdinalIgnoreCase) && (l.Contains("fail", StringComparison.OrdinalIgnoreCase) || l.Contains("unknown", StringComparison.OrdinalIgnoreCase))),
                "no load_binary_game_engine failure in the log");
        }
        catch (Exception ex)
        {
            Check(false, "unexpected error: " + ex.Message);
        }
        finally
        {
            if (_vm.Game?.IsRunning == true) _vm.Game.Stop();
            foreach (var t in _vm.Tabs.ToList()) await _vm.CloseTabCommand.ExecuteAsync(t);
            if (mglo != null) try { File.Delete(mglo); } catch { }
        }
        Say(_failures == 0 ? "MEGALOPLAY PASS" : $"MEGALOPLAY FAIL ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    // ---- blocks and references -------------------------------------------------------------------

    /// <summary>The weapon's Each shot box: damage and bullet speed from the bullet's files, edited from the weapon (nothing saved).</summary>
    private async Task WeaponShot()
    {
        const string weapon = @"objects\weapons\pistol\magnum\magnum.weapon";
        const string damage = @"objects\weapons\pistol\magnum\projectiles\magnum_bullet.damage_effect";
        var tab = await _vm.OpenTagAsync(weapon);
        if (!Check(tab is { IsLoading: false }, "opened the magnum")) return;
        for (int i = 0; i < 50 && tab!.Shot == null; i++) await Task.Delay(100);
        var shot = tab!.Shot;
        if (!Check(shot != null && tab.ShowShot, "the Each shot box is shown")) return;
        Check(shot!.Damage == "17.5" && shot.DamageLabel == "Damage per hit", $"damage per hit read from the bullet's damage file: {shot.Damage}");
        Check(shot.Speed == "3000", $"bullet speed: {shot.Speed}");
        Check(shot.Summary.Contains("4 hits to break"), "summary: " + shot.Summary);
        await Pause(600);
        Shot("weapon-shot");
        shot.Damage = "35";
        await Task.Delay(500);
        var d = await _vm.Host!.OpenTagAsync(damage);
        Check(TagEditorViewModel.Find(d.Fields, "damage lower bound")?.Value == "35" && TagEditorViewModel.Find(d.Fields, "damage upper bound")?.Value?.StartsWith("35") == true,
            "typing 35 sets the damage file's lower and upper bounds");
        Check(tab.IsDirty && shot.Summary.Contains("2 hits to break"), "the weapon tab is unsaved, summary follows: " + shot.Summary);
        shot.Speed = "abc";
        Check(shot.Error != null, "a wrong speed is refused: " + shot.Error);
        var grenade = await _vm.OpenTagAsync(@"objects\weapons\support_high\rocket_launcher\rocket_launcher.weapon");
        for (int i = 0; i < 50 && grenade?.Shot == null; i++) await Task.Delay(100);
        Check(grenade?.Shot is { Explosive: true, HasRadius: true }, $"the rocket launcher shows explosion damage and blast radius ({grenade?.Shot?.Damage}, {grenade?.Shot?.Radius}): {grenade?.Shot?.Summary}");
        await Pause(400);
        Shot("rocket-shot");
        // Close without saving: the edits are dropped.
        foreach (var t in new[] { tab, grenade }) if (t != null) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
        var again = await _vm.Host.OpenTagAsync(damage);
        Say("after close: damage lower bound " + TagEditorViewModel.Find(again.Fields, "damage lower bound")?.Value);
        await _vm.Host.CloseTagAsync(damage);
    }

    private async Task Blocks()
    {
        const string weapon = @"objects\weapons\rifle\assault_rifle\assault_rifle.weapon";
        var tab = await _vm.OpenTagAsync(weapon);
        if (!Check(tab is { IsLoading: false }, "opened the assault rifle")) return;
        // Simple view first: a weapon opens with the fields most people change, by plain names.
        var shown = tab!.AllFields().Where(f => f.IsVisible && !f.IsContainer).ToList();
        Check(tab.ShowSimple && shown.Count is > 8 and < 30, $"a weapon opens in the Simple view ({shown.Count} fields: {string.Join(", ", shown.Take(6).Select(f => f.DisplayText))}…)");
        var magSize = tab.FindField("Block:magazines[0]/ShortInteger:rounds loaded maximum");
        Check(magSize is { IsVisible: true, DisplayText: "Magazine size" } && magSize.Description?.Contains("full magazine") == true, "Magazine size with its help: " + magSize?.Description);
        Check(tab.FindField("Block:barrels[0]/Reference:projectile") is { IsVisible: true }, "the projectile (where the damage is) is in the Simple view");
        await Pause(800);
        Shot("simple-weapon");
        tab.ShowLabeled = true;
        Check(!tab.ShowSimple && tab.AllFields().Count(f => f.IsVisible && !f.IsContainer) > 60, "All fields shows the rest");
        var block = tab.FindField("Block:magazines");
        if (!Check(block != null && block.IsBlock, "magazines is a block")) return;
        tab.Reveal(block!);
        var first = tab.FindField("Block:magazines[0]");
        if (!Check(first != null, "magazine element found")) return;
        tab.Reveal(first!);
        await first!.DuplicateElementCommand.ExecuteAsync(null);
        var after = tab.FindField("Block:magazines");
        Check(after?.Node.ElementCount == 2, $"duplicate added a magazine ({after?.Node.ElementCount} elements)");
        Check(tab.IsDirty, "tab is unsaved after the block change");
        await Pause();
        Shot("block-duplicated");
        var second = tab.FindField("Block:magazines[1]");
        if (second != null) await second.DeleteElementCommand.ExecuteAsync(null);
        Check(tab.FindField("Block:magazines")?.Node.ElementCount == 1, "delete removed it");

        var model = tab.AllFields().FirstOrDefault(f => f.IsReference && f.RawName == "model");
        if (Check(model != null, "found the model reference"))
        {
            tab.Reveal(model!);
            await Pause(600);
            Check(tab.ReferenceResults.Count > 0 && tab.ReferenceResults.All(r => r.EndsWith(".model", StringComparison.OrdinalIgnoreCase)),
                $"reference picker lists only .model tags ({tab.ReferenceResults.Count})");
            tab.ReferenceQuery = "dmr";
            await Pause(300);
            Check(tab.ReferenceResults.Any(r => r.Contains("dmr")), "reference search finds the DMR model");
            Shot("reference-picker");
        }
        // Save as new: the copy gets the unsaved edit (2 magazines), the original on disk keeps 1.
        var firstMag = tab.FindField("Block:magazines[0]");
        if (firstMag != null) { tab.Reveal(firstMag); await firstMag.DuplicateElementCommand.ExecuteAsync(null); }
        string copyPath = _vm.NewTagPathFor(weapon, "kalo_tour_rifle");
        string copyFile = System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, copyPath);
        await _vm.SaveTagAsNewAsync(tab, "kalo_tour_rifle");
        var copy = _vm.Tabs.OfType<TagEditorViewModel>().FirstOrDefault(t => t.Path.Equals(copyPath, StringComparison.OrdinalIgnoreCase));
        if (copy == null) foreach (var pr in _vm.Problems.TakeLast(3)) Say($"problem: {pr.Severity}: {pr.Message}");
        Check(System.IO.File.Exists(copyFile) && copy != null, "Save as new wrote and opened " + copyPath);
        Check(copy?.FindField("Block:magazines")?.Node.ElementCount == 2, "the new tag has the unsaved edit (2 magazines)");
        Check(!_vm.Tabs.Any(t => t.Path.Equals(weapon, StringComparison.OrdinalIgnoreCase)), "the original's tab was replaced by the new tag");
        var original = await _vm.OpenTagAsync(weapon);
        Check(original?.FindField("Block:magazines")?.Node.ElementCount == 1, "the original on disk is unchanged (1 magazine)");
        if (original != null) await _vm.CloseTabCommand.ExecuteAsync(original);
        if (copy != null) await _vm.CloseTabCommand.ExecuteAsync(copy);
        try { System.IO.File.Delete(copyFile); System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(copyFile)!); } catch { }
        _vm.RefreshChangesAfterEdit();
    }

    // ---- Use in scripts ---------------------------------------------------------------------------------

    /// <summary>
    /// Use in scripts: a new object (a copy of the assault rifle) registered as kalo_tour_obj, a gametype that creates it
    /// played on Sword Slayer (which doesn't contain it: Play adds it to the playtest's copy), then renamed and removed.
    /// The two shared files go back to HREK's versions when nothing else of the mod's is in them.
    /// </summary>
    private async Task ScriptObjectsSection()
    {
        if (_vm.Paths == null || _vm.Baseline == null) return;
        const string rifle = @"objects\weapons\rifle\assault_rifle\assault_rifle.weapon";
        var ar = await _vm.OpenTagAsync(rifle);
        if (!Check(ar is { IsLoading: false }, "opened the assault rifle")) return;
        Check(ar!.ShowScriptName && ar.ScriptName == "assault_rifle" && !ar.CanRemoveScriptName,
            $"the rifle shows its script name and can't be removed ({ar.ScriptName}: {ar.WhyNotRemovable})");
        string copyPath = _vm.NewTagPathFor(rifle, "kalo_tour_script_object");
        string copyFile = System.IO.Path.Combine(_vm.Paths.WorkspaceTags, copyPath);
        string probeRel = @"data\multiplayer\megalo\mods\_kalo_tour\script_object_probe.kscript";
        string probeFile = System.IO.Path.Combine(_vm.Paths.Workspace, probeRel);
        bool listWasVanilla = !_vm.Baseline.IsChanged(@"tags\" + Kalo.Core.ScriptObjects.ListTag) && !_vm.Baseline.IsChanged(Kalo.Core.ScriptObjects.NamesFile);
        try
        {
            await _vm.SaveTagAsNewAsync(ar, "kalo_tour_script_object");
            var copy = _vm.Tabs.OfType<TagEditorViewModel>().FirstOrDefault(t => t.Path.Equals(copyPath, StringComparison.OrdinalIgnoreCase));
            if (!Check(copy != null && copy.ShowUseInScripts, "a new object offers Use in scripts")) return;
            Check(await _vm.RegisterScriptObjectAsync(copy!.Path, "Bad Name") != null, "a name with spaces and capitals is refused");
            Check(await _vm.RegisterScriptObjectAsync(copy.Path, "assault_rifle") != null, "a name already used is refused");
            string? error = await _vm.RegisterScriptObjectAsync(copy.Path, "kalo_tour_obj");
            if (!Check(error == null, "registered as kalo_tour_obj" + (error != null ? ": " + error : ""))) return;
            Check(copy.ScriptName == "kalo_tour_obj", "the tag editor shows the script name");
            Check(_vm.MegaloCatalog?.Lists?.TryGetValue("objectTypes", out var types) == true && types.Contains("kalo_tour_obj"), "the compiler and autocomplete know kalo_tour_obj");

            // A gametype that creates it, played on Sword Slayer.
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(probeFile)!);
            System.IO.File.WriteAllText(probeFile,
                "-- title: script object probe\r\ndeclare global.timer drop_timer = 3\r\ndo\r\n  timer_set_rate(drop_timer, 1)\r\n  if timer_expired(drop_timer) then\r\n" +
                "    timer_reset(drop_timer)\r\n    for each player do\r\n      create_object(\"kalo_tour_obj\", at current_player, offset 0 0 2)\r\n    end\r\n  end\r\nend\r\n");
            _vm.SelectedScenario = _vm.Scenarios.FirstOrDefault(sc => sc.EndsWith("20_sword_slayer", StringComparison.OrdinalIgnoreCase));
            _vm.SelectedGametype = new GametypeChoice("script object probe", probeRel);
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLine(string l) { if (l.Contains("main_game_start:") || l.Contains("megalogamengine:") && (l.Contains("fail") || l.Contains("not in this map"))) errors.Enqueue(l); }
            void OnLoaded() => loaded.TrySetResult();
            _vm.Game!.Output += OnLine;
            _vm.Game.Loaded += OnLoaded;
            try
            {
                await _vm.PlayAsync();
                if (Check(_vm.IsGameRunning, "Play started the game"))
                {
                    var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6)));
                    Check(done == loaded.Task, "the game loaded the map");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _hold)));
                    Check(errors.IsEmpty, "the gametype that creates kalo_tour_obj runs on Sword Slayer" + (errors.TryPeek(out var first) ? ": " + first : ""));
                    _vm.StopGameCommand.Execute(null);
                    await Task.Delay(2000);
                }
            }
            finally
            {
                _vm.Game.Output -= OnLine;
                _vm.Game.Loaded -= OnLoaded;
            }

            if (copy.ScriptEntry is { } entry)
            {
                Check(await _vm.RenameScriptObjectAsync(entry, "kalo_tour_obj2") == null && copy.ScriptName == "kalo_tour_obj2", "renamed to kalo_tour_obj2");
                Check(Kalo.Core.ScriptObjects.ReadNames(_vm.Paths).LastOrDefault() == "kalo_tour_obj2", "objects.txt follows the rename");
            }
            if (copy.ScriptEntry is { } renamed)
            {
                Check(await _vm.RemoveScriptObjectAsync(renamed) == null && copy.ShowUseInScripts, "removed from scripts");
                if (listWasVanilla)
                    Check(!_vm.Baseline.IsChanged(@"tags\" + Kalo.Core.ScriptObjects.ListTag) && !_vm.Baseline.IsChanged(Kalo.Core.ScriptObjects.NamesFile),
                        "the object type list and objects.txt are HREK's again");
            }
            await _vm.CloseTabCommand.ExecuteAsync(copy);
        }
        finally
        {
            foreach (var t in _vm.Tabs.OfType<TagEditorViewModel>().Where(t => t.Path.Equals(copyPath, StringComparison.OrdinalIgnoreCase) || t.Path.Equals(rifle, StringComparison.OrdinalIgnoreCase)).ToList())
                await _vm.CloseTabCommand.ExecuteAsync(t);
            try { System.IO.File.Delete(copyFile); System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(copyFile)!); } catch { }
            try { System.IO.File.Delete(probeFile); } catch { }
            _vm.RefreshChangesAfterEdit();
        }
    }

    // ---- map ----------------------------------------------------------------------------------------

    private async Task Map()
    {
        var map = await _vm.OpenMapAsync(@"levels\multi\20_sword_slayer\20_sword_slayer.scenario");
        Check(map.Items.Count > 100, $"map lists the placements ({map.Items.Count})");
        var until = DateTime.UtcNow.AddMinutes(10);
        while (map.Bsp == null && DateTime.UtcNow < until && !map.GeometryStatus.StartsWith("No level") && !map.GeometryStatus.Contains("unavailable"))
            await Task.Delay(500);
        Check(map.Bsp is { TriangleCount: > 1000 }, $"level geometry loaded: {map.GeometryStatus}");
        Say("renderer: " + map.RendererStatus);
        var modelsUntil = DateTime.UtcNow.AddMinutes(4);
        while (map.ModelVersion == 0 && DateTime.UtcNow < modelsUntil) await Task.Delay(500);
        Check(map.Items.Count(i => i.Model != null) > map.Items.Count / 2, $"object models loaded for {map.Items.Count(i => i.Model != null)} of {map.Items.Count} objects");
        // Shading: textured (color maps decoded by the host), then wireframe and solid + edges.
        map.Shading = ViewShading.Textured;
        var texUntil = DateTime.UtcNow.AddMinutes(6);
        var texClock = System.Diagnostics.Stopwatch.StartNew();
        await Pause(300);
        while ((map.TexturesLoading || map.TexturesVersion == 0) && DateTime.UtcNow < texUntil) await Task.Delay(500);
        int bspMaps = map.Bsp?.Materials.Count(m => !string.IsNullOrEmpty(m.BaseMap)) ?? 0;
        int bspReady = map.Bsp?.Materials.Count(m => map.TextureFor(m.BaseMap) != null) ?? 0;
        Check(bspMaps > 10 && bspReady > bspMaps * 3 / 4, $"level textures ready: {bspReady} of {bspMaps} materials ({texClock.ElapsedMilliseconds} ms) {map.TextureStatus}");
        var blends = (map.Bsp?.Materials ?? Array.Empty<Kalo.Core.Geometry.MeshMaterial>()).GroupBy(m => m.Blend).Select(g => $"{g.Key} {g.Count()}");
        Say("level material blend modes: " + string.Join(", ", blends) + $"; alpha-tested {map.Bsp?.Materials.Count(m => m.AlphaTest)}");
        Check(map.Bsp?.Materials.Any(m => m.IsTranslucent) == true, "shader blend modes read (glass and decals are drawn see-through)");
        int uvMeshes = map.Items.Select(i => i.Model).OfType<Kalo.Core.Geometry.Mesh>().Distinct().Count(m => m.Uvs != null);
        Check(map.Bsp?.Uvs != null && uvMeshes > 0, $"texture coordinates exported (level: {map.Bsp?.Uvs != null}, {uvMeshes} models)");

        // The view a player gets at the first spawn point, to compare with the game.
        map.ViewFromSpawnCommand.Execute(null);
        Say($"spawn view from {map.Spawn?.Position} facing {map.Spawn?.Facing}");
        await Pause(1000);
        Check(await map.CaptureViewAsync(System.IO.Path.Combine(_dir, "view-spawn.png")), "captured the 3D view from the spawn point (textured)");
        map.Shading = ViewShading.Wireframe;
        await Pause(600);
        Check(await map.CaptureViewAsync(System.IO.Path.Combine(_dir, "view-spawn-wire.png")), "captured the wireframe view");
        map.Shading = ViewShading.Solid;
        map.WireOverlay = true;
        await Pause(600);
        Check(await map.CaptureViewAsync(System.IO.Path.Combine(_dir, "view-spawn-solid-edges.png")), "captured the solid view with edges");
        map.WireOverlay = false;
        map.Shading = ViewShading.Textured;
        // A second angle: from above the spawn, looking down the hall.
        if (map.Spawn is { } sp)
        {
            map.LookFrom(sp.Position + new Vector3(-6, 0, 6), sp.Facing, -30);
            await Pause(800);
            await map.CaptureViewAsync(System.IO.Path.Combine(_dir, "view-above.png"));
        }
        map.FrameAllCommand.Execute(null);
        await Pause(1500);
        Shot("map-overview");

        // Real mouse input reaches the 3D view (it must be hit-testable, or look/move/select do nothing).
        var viewport = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(_window).OfType<Kalo.App.Viewport.MapViewport>().FirstOrDefault(v => v.IsEffectivelyVisible);
        if (Check(viewport != null, "the map view is on screen"))
        {
            var center = Avalonia.VisualExtensions.TranslatePoint(viewport!, new Avalonia.Point(viewport!.Bounds.Width / 2, viewport.Bounds.Height / 2), _window);
            var hit = center.HasValue ? Avalonia.Input.InputExtensions.InputHitTest(_window, center.Value) : null;
            bool reaches = hit is Avalonia.Visual v && (ReferenceEquals(v, viewport) || Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(v).Contains(viewport));
            Check(reaches, $"the mouse reaches the 3D view (hit: {hit?.GetType().Name ?? "nothing"})");
            Check(viewport.Focusable, "the 3D view can take keyboard focus (WASD)");
        }
        Say("holding the overview for 20 s");
        await Task.Delay(TimeSpan.FromSeconds(20));

        var weapon = map.Items.FirstOrDefault(i => i.Category == "weapons");
        if (!Check(weapon != null, "a weapon placement exists")) return;
        map.Selected = weapon;
        map.FocusSelectedCommand.Execute(null);
        var start = weapon!.Position;
        map.MoveLocal(weapon, start + new Vector3(1.5f, 0, 0));
        await map.CommitMoveAsync(weapon);
        Check(MathF.Abs(weapon.Position.X - (start.X + 1.5f)) < 0.01f, "moved the weapon 1.5 units");
        Check(map.IsDirty, "map is unsaved after the move");
        int count = map.Items.Count;
        await map.DuplicateSelectedCommand.ExecuteAsync(null);
        Check(map.Items.Count == count + 1 && map.Selected != null && map.Selected != weapon, "duplicated it");
        // Handles: the closest point on an axis to a ray (used by the arrow handles).
        float t = Kalo.App.Viewport.MapViewport.ClosestOnAxis(Vector3.Zero, Vector3.UnitX, new Vector3(5, -10, 0), Vector3.UnitY);
        Check(MathF.Abs(t - 5) < 1e-4f, $"arrow handle math: closest point on the x axis is 5 ({t})");

        // Multi-selection: move two weapons together.
        var others = map.Items.Where(i => i.Category == "weapons" && !map.IsSelected(i)).Take(2).ToList();
        if (others.Count == 2)
        {
            map.Selected = others[0];
            map.ToggleSelection(others[1]);
            Check(map.Selection.Count == 2 && map.HasMultiSelection, "Ctrl+click selects two objects");
            var origins = map.Selection.ToDictionary(i => i, i => i.Position);
            map.MoveSelectionLocal(origins, new Vector3(0, 2f, 0));
            await map.CommitSelectionAsync();
            Check(origins.All(kv => MathF.Abs(kv.Key.Position.Y - (kv.Value.Y + 2f)) < 0.01f), "both moved 2 units together");
            int before = map.Items.Count;
            await map.DuplicateSelectedCommand.ExecuteAsync(null);
            Check(map.Items.Count == before + 2, $"duplicating the selection added 2 objects ({map.Items.Count - before})");
        }

        // Drag & drop: place a DMR from the tag tree.
        int weaponsBefore = map.Items.Count(i => i.Category == "weapons");
        var dmr = await map.AddTagAtAsync(@"objects\weapons\rifle\dmr\dmr.weapon", weapon.Position + new Vector3(0, 0, 1));
        Check(dmr != null && map.Items.Count(i => i.Category == "weapons") == weaponsBefore + 1 && map.PaletteChoices.Any(p => p.Tag.EndsWith("dmr.weapon")),
            "dropping dmr.weapon placed a DMR (and added it to the weapon palette)");
        Check(MapEditorViewModel.CategoryFor("x.device_machine") == "machines" && MapEditorViewModel.CategoryFor("x.bitmap") == null, "tag types map to the right placement block");
        if (dmr != null) map.Selected = dmr;

        await Pause(1000);
        Say($"holding the map view for {_hold:F0} s");
        Shot("map-selected");
        await Task.Delay(TimeSpan.FromSeconds(_hold));
        int beforeDelete = map.Items.Count;
        await map.DeleteSelectedCommand.ExecuteAsync(null);
        Check(map.Items.Count == beforeDelete - 1, "Delete removed the selected object");
        await _vm.CloseTabCommand.ExecuteAsync(map); // unsaved
    }

    // ---- map studio: object list, change marks, copy/paste, folders, properties, prefabs, blocks, browser ----------

    private static IEnumerable<OutlinerNode> Flatten(IEnumerable<OutlinerNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    private async Task Studio()
    {
        var map = await _vm.OpenMapAsync(@"levels\multi\20_sword_slayer\20_sword_slayer.scenario");
        var until = DateTime.UtcNow.AddMinutes(5);
        while ((map.Bsp == null || map.ModelVersion == 0) && DateTime.UtcNow < until) await Task.Delay(500);
        var created = new List<string>();
        try
        {
            Check(map.OutlinerRoots.Count > 0, $"object list has {map.OutlinerRoots.Count} top-level rows");
            Check(map.Folders.Count > 0 && map.OutlinerRoots.Any(n => n.IsFolder), $"Sapien editor folders shown ({map.Folders.Count})");
            Check(map.OutlinerRoots.Any(n => n.IsGroup), "objects in no folder are grouped by kind");
            Check(!string.IsNullOrEmpty(AppSettings.Current.GestureText("forward")), $"keys: fly forward is {AppSettings.Current.GestureText("forward")} ({AppSettings.Current.Layout})");

            // Change marks: move one weapon, add one.
            await map.RefreshStatesAsync();
            Check(map.Items.All(i => i.State == Kalo.Protocol.PlacementStates.Same), $"a vanilla map has no changed objects ({map.Items.Count(i => i.State != 0)})");
            var weapon = map.Items.First(i => i.Category == "weapons");
            map.Selected = weapon;
            map.GoTo(weapon);
            map.MoveLocal(weapon, weapon.Position + new Vector3(0.5f, 0, 0));
            await map.CommitMoveAsync(weapon);
            var added = await map.AddTagAtAsync(@"objects\weapons\rifle\dmr\dmr.weapon", weapon.Position + new Vector3(0, 1, 0.5f));
            await map.RefreshStatesAsync();
            weapon = map.Items.First(i => i.Category == weapon.Category && i.Index == weapon.Index);
            Check(weapon.State == Kalo.Protocol.PlacementStates.Changed, $"moved weapon is marked changed ({weapon.StateText})");
            var addedNow = map.Items.FirstOrDefault(i => i.Tag.EndsWith("dmr.weapon") && i.Index == added?.Index);
            Check(addedNow?.State == Kalo.Protocol.PlacementStates.New, $"added DMR is marked new ({addedNow?.StateText})");
            Say("changes: " + map.ChangesText);

            // Properties of the selected object.
            map.Selected = weapon;
            var pu = DateTime.UtcNow.AddSeconds(10);
            while (!map.HasProperties && DateTime.UtcNow < pu) await Task.Delay(200);
            Check(map.HasProperties, $"the weapon's own properties are listed ({map.Properties.Count(p => !p.IsHeader)} fields: {string.Join(", ", map.Properties.Where(p => !p.IsHeader).Take(5).Select(p => p.Label))})");

            // Objective zones: the shape and size of an object's boundary, drawn in the view.
            var zoned = map.Items.FirstOrDefault(i => i.HasZone);
            Say(zoned != null ? $"a zone in the map: {zoned.Display} (shape {zoned.ZoneShape}, {zoned.ZoneSize})" : "no zone in this map");
            var zoneTarget = addedNow ?? weapon;
            await map.SetZoneAsync(zoneTarget, "CharEnum:boundary shape", "2");
            zoneTarget = map.Items.First(i => i.Category == zoneTarget.Category && i.Index == zoneTarget.Index);
            Check(zoneTarget.ZoneShape == 2 && zoneTarget.ZoneSize.X > 2.9f, $"a cylinder zone was given to the DMR with a default size ({zoneTarget.ZoneShape}, {zoneTarget.ZoneSize})");
            zoneTarget.ZoneWidth = "5";
            var zu = DateTime.UtcNow.AddSeconds(10);
            while (Math.Abs(zoneTarget.ZoneSize.X - 5) > 0.01f && DateTime.UtcNow < zu) await Task.Delay(200);
            Check(Math.Abs(zoneTarget.ZoneSize.X - 5) < 0.01f, $"typing a radius of 5 resized it ({zoneTarget.ZoneSize.X})");
            map.Selected = zoneTarget;
            map.LookFrom(zoneTarget.Position + new Vector3(-9, -9, 7), 45, -30);
            await Pause(1500);
            await map.CaptureViewAsync(System.IO.Path.Combine(_dir, "studio-zone.png"));
            Shot("studio-zone-inspector");
            Check(map.Properties.All(r => r.IsHeader || !r.Label.StartsWith("Boundary", StringComparison.OrdinalIgnoreCase)), "the raw boundary fields are left to the Zone section");
            zoneTarget.ZoneShapeIndex = 3;
            zu = DateTime.UtcNow.AddSeconds(10);
            while (!zoneTarget.IsBoxZone && DateTime.UtcNow < zu) await Task.Delay(200);
            Check(zoneTarget.IsBoxZone && zoneTarget.ZoneWidthLabel == "Width", "switching to a box asks for width and length");
            await Pause(800);
            await map.CaptureViewAsync(System.IO.Path.Combine(_dir, "studio-zone-box.png"));

            // Names: rename the DMR in the object list.
            await map.RenameAsync(zoneTarget, "kalo_tour_goal");
            Check(zoneTarget.PlacedName == "kalo_tour_goal" && zoneTarget.Display.StartsWith("kalo_tour_goal"), $"renamed the DMR ({zoneTarget.Display})");
            Check(Flatten(map.OutlinerRoots).Any(n => n.Item == zoneTarget && n.Name.Contains("kalo_tour_goal")), "the object list shows the new name");
            var other = map.Items.FirstOrDefault(i => i != zoneTarget && i.Category == "weapons");
            if (other != null)
            {
                await map.RenameAsync(other, "kalo_tour_goal");
                Check(other.PlacedName != "kalo_tour_goal", "a name already used is refused");
            }
            await Pause(600);
            Shot("studio-rename");
            await map.RenameAsync(zoneTarget, "");
            Check(string.IsNullOrEmpty(zoneTarget.PlacedName), "an empty name removes it");

            // Copy / paste.
            int count = map.Items.Count;
            map.Selected = weapon;
            await map.CopySelection();
            await map.PasteAsync(weapon.Position + new Vector3(2, 0, 0));
            Check(map.Items.Count == count + 1 && map.Selected?.Tag == weapon.Tag, "copy + paste made one more of the same weapon");

            // Folders.
            var pasted = map.Selected!;
            int folders = map.Folders.Count;
            await map.NewFolderAsync("kalo tour folder", -1);
            Check(map.Folders.Count == folders + 1, "new folder added to the map");
            var moved = map.Items.FirstOrDefault(i => i.Category == pasted.Category && i.Index == pasted.Index);
            Check(moved?.Folder == folders, $"the selected object went into the new folder ({moved?.Folder})");
            Check(map.OutlinerRoots.Any(n => n.IsFolder && n.Name == "kalo tour folder" && n.Children.Count == 1), "the folder shows in the object list with its object");
            map.SelectedFolder = folders;
            await map.DeleteFolderCommand.ExecuteAsync(null);
            Check(map.Folders.Count == folders, "folder deleted (its object stays in the map)");

            // Undo / redo: the folder comes back, then goes again; undoing the paste removes the copy.
            Check(map.CanUndo, "undo available: " + map.UndoText);
            await map.UndoAsync();
            Check(map.Folders.Count == folders + 1, "undo brought the deleted folder back");
            await map.RedoAsync();
            Check(map.Folders.Count == folders, "redo deleted it again");
            int beforeUndo = map.Items.Count;
            await map.UndoAsync(); // folder delete
            await map.UndoAsync(); // new folder
            await map.UndoAsync(); // paste
            Check(map.Items.Count == beforeUndo - 1, $"three undos removed the pasted copy ({beforeUndo} → {map.Items.Count})");
            await map.RedoAsync();
            Check(map.Items.Count == beforeUndo, "redo put the paste back");

            // Copy what the object is now, not its type: edit the pasted copy, copy it, paste: the new one has the edit.
            var copyOne = map.Items.FirstOrDefault(i => i.Category == pasted.Category && i.Index == pasted.Index) ?? pasted;
            string spawnField = $"Block:{copyOne.Category}[{copyOne.Index}]/Struct:multiplayer data[0]/ShortInteger:spawn time";
            await _vm.Host!.SetFieldAsync(map.Path, spawnField, "77");
            map.Selected = copyOne;
            await map.CopySelection();
            await map.PasteAsync(copyOne.Position + new Vector3(2, 0, 0));
            var copyTwo = map.Selected!;
            var copyDoc = await _vm.Host.ReadElementAsync(map.Path, copyTwo.Category, copyTwo.Index);
            string? spawnTime = Flatten(copyDoc.Fields).FirstOrDefault(f => f.Name == "spawn time")?.Value;
            Check(copyTwo.Index != copyOne.Index && spawnTime == "77", $"a copy of an edited copy keeps the edit (spawn time {spawnTime})");
            await map.UndoAsync(); // that paste

            // Multiple selection and shortcuts: Ctrl+D duplicates both selected weapons.
            var pair = map.Items.Where(i => i.Category == "weapons").Take(2).ToList();
            map.SetSelection(pair);
            int beforeDup = map.Items.Count;
            var ctrlD = new Avalonia.Input.KeyEventArgs { Key = AppSettings.Parse(AppSettings.Current.GestureText("duplicate"))!.Key, KeyModifiers = AppSettings.Parse(AppSettings.Current.GestureText("duplicate"))!.KeyModifiers };
            var dup = map.ShortcutFor(ctrlD);
            Check(dup != null, "the duplicate shortcut is recognised (" + AppSettings.Current.GestureText("duplicate") + ")");
            if (dup != null) await dup();
            Check(map.Items.Count == beforeDup + 2, $"duplicating a 2-object selection added 2 ({beforeDup} → {map.Items.Count})");
            await map.UndoAsync();
            Check(map.Items.Count == beforeDup, "one undo removed both duplicates");

            // Prefabs.
            var two = map.Items.Where(i => i.Category == "weapons").Take(2).ToList();
            map.SetSelection(two);
            await map.CreatePrefab("kalo tour prefab");
            var prefab = _vm.Prefabs.FirstOrDefault(p => p.Name == "kalo tour prefab");
            Check(prefab?.Objects.Count == 2, "prefab saved from two selected objects");
            if (prefab != null)
            {
                int before = map.Items.Count;
                await map.PlaceObjectsAsync(prefab.Objects, weapon.Position + new Vector3(0, -3, 0), "Placed");
                Check(map.Items.Count == before + 2, "placing the prefab added its two objects");
                _vm.DeletePrefabCommand.Execute(prefab);
                Check(_vm.Prefabs.All(p => p.Name != "kalo tour prefab"), "prefab deleted");
            }

            // Block tools: a floor, painted.
            var floorTool = MapEditorViewModel.BlockTools.First(t => t.Key == "floor");
            map.LookFrom(weapon.Position + new Vector3(-8, 0, 3), 0, -15);
            await Pause(500);
            await map.PlaceBlockCommand.ExecuteAsync(floorTool);
            var floor = map.Selected;
            Check(floor != null && floor.Tag.Contains("ff_plat_4x4_flat"), $"Floor tool placed a Forge platform ({floor?.Tag})");
            await _vm.EnsureMaterialsAsync();
            Check(_vm.Materials.Count > 10, $"material library ready ({_vm.Materials.Count} materials)");
            var material = _vm.Materials.FirstOrDefault(m => m.Shader.Contains("metal")) ?? _vm.Materials.FirstOrDefault();
            if (floor != null && material != null)
            {
                map.SetSelection(new[] { floor });
                await map.PaintCommand.ExecuteAsync(material);
                var painted = map.Selected;
                Check(painted != null && painted.Tag.Contains("__"), $"Paint made a painted copy ({painted?.Tag})");
                if (painted != null)
                {
                    created.Add(System.IO.Path.GetDirectoryName(painted.Tag)!);
                    Check(painted.Model != null && painted.Model.Materials.Any(m => m.Shader.Equals(material.Shader, StringComparison.OrdinalIgnoreCase)),
                        "the painted copy's model uses the material");
                    map.Shading = ViewShading.Textured;
                    await Task.Delay(3000);
                    map.GoTo(painted);
                    await Pause(1500);
                    await map.CaptureViewAsync(System.IO.Path.Combine(_dir, "studio-painted-floor.png"));
                }
            }

            // Object browser.
            _vm.ShowObjectsCommand.Execute(null);
            Check(_vm.ObjectCategories.Count >= 4, $"object browser categories: {string.Join(", ", _vm.ObjectCategories.Select(c => c.Display))}");
            _vm.ObjectFilter = "dmr";
            await Pause(300);
            var dmrEntry = _vm.ObjectResults.FirstOrDefault(o => o.Tag.EndsWith(@"rifle\dmr\dmr.weapon", StringComparison.OrdinalIgnoreCase));
            Check(dmrEntry != null, "search finds the DMR");
            if (dmrEntry != null)
            {
                _vm.SelectedObject = dmrEntry;
                var pv = DateTime.UtcNow.AddSeconds(30);
                while (_vm.PreviewMesh == null && DateTime.UtcNow < pv) await Task.Delay(300);
                Check(_vm.PreviewMesh != null, "the DMR preview model loaded: " + _vm.PreviewStatus);
            }
            // Forge menu and script names (read in the background, the first time from every multiplayer map).
            _vm.ObjectFilter = "flag stand";
            var namesBy = DateTime.UtcNow.AddMinutes(3);
            ObjectEntry? stand = null;
            while (DateTime.UtcNow < namesBy)
            {
                stand = _vm.ObjectResults.FirstOrDefault(o => o.Tag.EndsWith(@"mp_flag_base\mp_flag_base.crate", StringComparison.OrdinalIgnoreCase));
                if (stand?.ScriptName != null) break;
                await Task.Delay(500);
                _vm.ObjectFilter = "flag stand ";
                _vm.ObjectFilter = "flag stand";
            }
            Check(stand?.ForgeName == "Flag Stand" && stand.ScriptName == "stand",
                $"search by Forge name finds mp_flag_base: Forge {stand?.ForgeName ?? "none"}, script {stand?.ScriptName ?? "none"}");
            _vm.ObjectFilter = "destination";
            await Pause(300);
            var plate = _vm.ObjectResults.FirstOrDefault(o => o.Tag.EndsWith(@"mp_circle\mp_circle.crate", StringComparison.OrdinalIgnoreCase));
            Check(plate?.ForgeName == "Capture Plate", $"search by script name finds mp_circle: Forge {plate?.ForgeName ?? "none"}, script {plate?.ScriptName ?? "none"}");
            _vm.ObjectFilter = "hill marker";
            await Pause(300);
            var hill = _vm.ObjectResults.FirstOrDefault(o => o.Tag.EndsWith(@"mp_hill_beacon\mp_hill_beacon.crate", StringComparison.OrdinalIgnoreCase));
            Check(hill?.ScriptName == "area", $"mp_hill_beacon: Forge {hill?.ForgeName ?? "none"}, script {hill?.ScriptName ?? "none"}");
            _vm.ObjectFilter = "";
            await Pause(1500);
            Shot("studio-objects");
            _vm.ShowOutputCommand.Execute(null);
        }
        finally
        {
            await _vm.CloseTabCommand.ExecuteAsync(map); // unsaved
            // Painted copies are files in the shared workspace: remove the ones the tour made.
            foreach (string dir in created)
            {
                try
                {
                    string full = System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, dir);
                    if (Directory.Exists(full) && dir.Contains(@"\blocks\")) { Directory.Delete(full, true); Say("removed " + dir); }
                    string blocks = Directory.GetParent(full)!.FullName;
                    if (Directory.Exists(blocks) && !Directory.EnumerateFileSystemEntries(blocks).Any()) Directory.Delete(blocks);
                }
                catch (Exception ex) { Say("could not remove " + dir + ": " + ex.Message); }
            }
        }
    }

    // ---- split editors: map and code side by side, stacked, or in a second window -------------------------------

    private async Task SplitEditors()
    {
        var map = await _vm.OpenMapAsync(@"levels\multi\20_sword_slayer\20_sword_slayer.scenario");
        var until = DateTime.UtcNow.AddMinutes(3);
        while (map.Bsp == null && DateTime.UtcNow < until) await Task.Delay(500);
        var file = _vm.Scripts.FirstOrDefault(s => s.Name == "slayer") ?? _vm.Scripts.FirstOrDefault();
        if (!Check(file != null, "a gametype script to open")) return;
        var script = await _vm.OpenScriptAsync(file!.RelativePath);
        try
        {
            Check(!_vm.IsSplit && _vm.Primary.Tabs.Contains(map) && _vm.Primary.Tabs.Contains(script), "both open in the main editor");

            _vm.SetSplitCommand.Execute("right");
            await Pause(800);
            Check(_vm.IsSplit && _vm.Secondary.Current == script && _vm.Primary.Current == map, "split right: the code moved to the right, the map stays on the left");
            Check(_vm.ActiveScript == script, "the focused side is the code (inspector shows the script)");
            _vm.FocusGroup(_vm.Primary);
            Check(_vm.ActiveMap == map && _vm.Secondary.Current == script, "clicking the map side: inspector follows the map, the code stays shown");
            await Pause(2500);
            Shot("split-right");

            _vm.SwapGroupsCommand.Execute(null);
            await Pause(500);
            Check(_vm.Primary.Current == script && _vm.Secondary.Current == map && _vm.ActiveMap == map, "swap: code on the left, map on the right, focus kept on the map");
            _vm.SwapGroupsCommand.Execute(null);

            _vm.SetSplitCommand.Execute("down");
            await Pause(2000);
            Check(_vm.Split == SplitLayout.Down && _vm.Secondary.Current == script, "split down keeps the same two sides");
            Shot("split-down");

            // A file opened while the code side is focused opens there.
            _vm.FocusGroup(_vm.Secondary);
            var tag = await _vm.OpenTagAsync(@"objects\weapons\rifle\dmr\dmr.weapon");
            var dmr = _vm.Tabs.OfType<TagEditorViewModel>().FirstOrDefault(t => t.Path.EndsWith("dmr.weapon"));
            Check(dmr != null && _vm.EditorGroupOf(dmr) == _vm.Secondary, "a tag opened from the focused second side opens there");
            if (dmr != null) await _vm.CloseTabCommand.ExecuteAsync(dmr);

            _vm.SetSplitCommand.Execute("window");
            await Pause(3000);
            var detached = _window.DetachedEditor;
            Check(detached != null && detached.IsVisible, "the second editor opened in its own window");
            if (detached != null) MainWindow.SaveShot(detached, System.IO.Path.Combine(_dir, $"{++_shot:D2}-split-window.png"));
            Shot("split-window-main");
            detached?.Close();
            await Pause(800);
            Check(!_vm.IsSplit && _vm.Primary.Tabs.Contains(script) && _vm.Primary.Tabs.Contains(map), "closing that window brought its tabs back");

            // Move a tab across and back with the menu action.
            _vm.MoveToOtherGroup(map);
            Check(_vm.IsSplit && _vm.Secondary.Current == map, "Move to the other side splits and moves the tab");
            _vm.MoveToOtherGroup(map);
            Check(!_vm.IsSplit && _vm.Primary.Tabs.Contains(map), "moving the last tab back closes the split");
        }
        finally
        {
            if (_vm.IsSplit) _vm.Unsplit();
            await _vm.CloseTabCommand.ExecuteAsync(script);
            await _vm.CloseTabCommand.ExecuteAsync(map);
        }
    }

    /// <summary>
    /// Zones in game: a throwaway mod, an empty Sword Slayer map, one spawn point with a zone over the whole map and two
    /// actions (message, teleport), another spawn point 12 units up as the exit. Played: the player should get the message
    /// and fall from the sky (screenshots by the caller). The mod is deleted and the previous one restored.
    /// </summary>
    private async Task ZonesPlay()
    {
        var projects = _vm.ModProjects!;
        string marker = System.IO.Path.Combine(_vm.Paths!.KaloData, "tour-previous-project.txt");
        // An interrupted run left the tour mod active and the user's mod aside: the marker names it.
        string? interrupted = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        foreach (var old in projects.List().Where(x => x.Name == TourMod).ToList())
        {
            if (projects.ActiveName == old.Name) await _vm.SwitchProjectCommand.ExecuteAsync(null);
            projects.Delete(old);
        }
        _vm.RefreshProjects();
        if (projects.ActiveName == null && !string.IsNullOrEmpty(interrupted) && projects.Find(interrupted) != null)
            await _vm.SwitchProjectCommand.ExecuteAsync(_vm.Projects.FirstOrDefault(x => x.Name == interrupted));
        string? previous = projects.ActiveName;
        File.WriteAllText(marker, previous ?? "");
        _vm.NewModName = TourMod;
        await _vm.CreateModCommand.ExecuteAsync(null);
        try
        {
            var map = await _vm.CreateMapAsync("Zone Test", @"levels\multi\20_sword_slayer\20_sword_slayer", empty: true);
            if (!Check(map != null, "created an empty Sword Slayer map")) return;
            var until = DateTime.UtcNow.AddMinutes(3);
            while (map!.Bsp == null && DateTime.UtcNow < until) await Task.Delay(300);
            var spawns = map.Items.Where(i => i.Tag.Contains("respawn_point")).ToList();
            if (!Check(spawns.Count >= 2, $"{spawns.Count} spawn points")) return;
            // The zone: a flag stand at the first spawn point. The exit: an invisible marker at the spawn point farthest from it.
            // Named tp_zone / tp_exit so the game can be asked where the player is (spawn points are not measurable objects).
            var far = spawns.Skip(1).OrderByDescending(s => Vector3.Distance(s.Position, spawns[0].Position)).First();
            var zone = await map.AddTagAtAsync(@"objects\multi\generic\flag_spawn_point.crate", spawns[0].Position);
            var exit = await map.AddTagAtAsync(@"objects\multi\generic\invisible_marker.crate", far.Position + new Vector3(0, 0, 0.1f));
            if (!Check(zone != null && exit != null, "placed a flag stand (zone) and an invisible marker (exit)")) return;
            Say($"exit {Vector3.Distance(exit!.Position, zone!.Position):F0} units from the zone object");
            await map.RenameAsync(exit, "tp_exit");
            await map.RenameAsync(zone, "tp_zone");
            map.SetSelection(new[] { exit });
            for (int i = 0; i < 50 && !map.Properties.Any(r => r.Path.EndsWith("String:megalo label")); i++) await Task.Delay(100);
            var exitLabel = map.Properties.FirstOrDefault(r => r.Path.EndsWith("String:megalo label"));
            if (!Check(exitLabel != null, "the spawn point has a Megalo label field")) return;
            Check(await map.SetPropertyAsync(exitLabel!, "teleport_exit") == null, "labelled the exit teleport_exit");

            // The zone: a cylinder over the whole map around the first spawn.
            map.SetSelection(new[] { zone });
            await map.SetZoneAsync(zone, "CharEnum:boundary shape", "2");
            await map.SetZoneAsync(zone, "Real:boundary width or radius", "80");
            await map.SetZoneAsync(zone, "Real:boundary positive height", "40");
            await map.SetZoneAsync(zone, "Real:boundary negative height", "20");
            Check(zone.HasZone, $"the spawn point has a zone (shape {zone.ZoneShape}, {zone.ZoneSize})");
            for (int i = 0; i < 50 && !map.Properties.Any(r => r.Path.EndsWith("String:megalo label")); i++) await Task.Delay(100);
            await Task.Delay(500);

            map.ZoneAction = map.ZoneActionChoices.First(c => c.Action == Kalo.Core.Scripting.Multiplayer.ZoneAction.Message);
            map.ZoneParam = "KALO ZONE TEST";
            await map.AddZoneTriggerCommand.ExecuteAsync(null);
            Say("zone: " + map.ZoneTriggerStatus);
            bool teleport = Environment.GetEnvironmentVariable("KALO_ZONE_TEST") != "message";
            if (teleport)
            {
            map.ZoneAction = map.ZoneActionChoices.First(c => c.Action == Kalo.Core.Scripting.Multiplayer.ZoneAction.Teleport);
            map.ZoneParam = "teleport_exit";
            await map.AddZoneTriggerCommand.ExecuteAsync(null);
            Say("zone: " + map.ZoneTriggerStatus);
            }
            var script = _vm.Tabs.OfType<ScriptEditorViewModel>().FirstOrDefault(t => t.Path.Contains(@"\mods\kalo_tour_mod\"));
            Check(script != null && script.Text.Contains("KALO ZONE TEST") && (!teleport || script.Text.Contains("object_attach(body, exit")), "both actions are in the new gametype");
            Check(script?.CompileOk == true, "it compiles: " + script?.CompileSummary);
            Check(_vm.SelectedGametype?.Path == script?.Path, "Play uses it: " + _vm.SelectedGametype?.Label);
            if (script != null) await script.SaveDocumentAsync();
            await map.SaveCommand.ExecuteAsync(null);
            _vm.ActiveDocument = map;
            await Pause(500);
            Shot("zone-map");

            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _vm.Game!.Loaded += () => loaded.TrySetResult();
            _vm.Game.Exited += _ => loaded.TrySetException(new Exception("the game exited while loading"));
            await _vm.PlayCommand.ExecuteAsync(null);
            if (!Check(_vm.IsGameRunning, "Play started the game")) return;
            var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6)));
            Check(done == loaded.Task && loaded.Task.IsCompletedSuccessfully, "the game loaded the map with the zone gametype");
            Say("GAME LOADED");
            // Ask the game where the player is: probe lines come back through the game's log. objects_distance_to_object
            // returns -1 when it has nothing to measure (no living player yet: players appear some 30-60 s after the round
            // starts), so each round says whether it measured, and only rounds that measured both objects count.
            var probes = new List<string>();
            _vm.Game.Output += line => { if (line.Contains("kalo probe:") && !line.Contains("(print")) lock (probes) probes.Add(line[line.IndexOf("kalo probe:")..]); };
            static string[] Probe(int round, string name, string what) => new[]
            {
                // The console prints only from an if that has both branches.
                $"(if (< (objects_distance_to_object (players) {name}) 0) (print \"kalo probe: r{round} no player for {what}\") (print \"kalo probe: r{round} measured {what}\"))",
                $"(if (< (objects_distance_to_object (players) {name}) 2.5) (print \"kalo probe: r{round} near {what}\") (print \"kalo probe: r{round} far from {what}\"))",
            };
            bool Has(string s) { lock (probes) return probes.Contains("kalo probe: " + s); }
            var measured = new List<int>();
            await Task.Delay(TimeSpan.FromSeconds(20));
            for (int round = 1; round <= 14 && measured.Count < 2; round++)
            {
                bool ran = await _vm.Game.RunConsoleAsync(Probe(round, "tp_exit", "the exit").Concat(Probe(round, "tp_zone", "the zone object")).ToArray(), TimeSpan.FromSeconds(10));
                if (!ran) Say("the game did not run the probe");
                await Task.Delay(TimeSpan.FromSeconds(8));
                // The game writes its log in batches: earlier rounds' answers may arrive now.
                for (int r = 1; r <= round; r++)
                    if (!measured.Contains(r) && Has($"r{r} measured the exit") && Has($"r{r} measured the zone object")) measured.Add(r);
            }
            for (int i = 0; i < 20 && measured.Count == 0; i++) await Task.Delay(1000);
            lock (probes) foreach (var p in probes.Distinct()) Say(p);
            if (Check(measured.Count > 0, $"the game measured the player (rounds {string.Join(", ", measured)})"))
            {
                int r = measured[^1];
                // Without teleport (KALO_ZONE_TEST=message) the player stays away from the exit: the control run.
                Check(teleport ? Has($"r{r} near the exit") : Has($"r{r} far from the exit"),
                    teleport ? "the zone teleported the player to the exit" : "control: without the teleport action the player is not at the exit");
                Check(Has($"r{r} far from the zone object"), "the player is away from the zone object (the measure tells places apart)");
            }
            Say($"holding the game for {_hold:F0} s");
            await Task.Delay(TimeSpan.FromSeconds(_hold));
            _vm.StopGameCommand.Execute(null);
            await Task.Delay(3000);
        }
        finally
        {
            if (_vm.Game?.IsRunning == true) _vm.Game.Stop();
            foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
            await _vm.SwitchProjectCommand.ExecuteAsync(null);
            var p = projects.Find(TourMod);
            if (p != null) projects.Delete(p);
            _vm.RefreshProjects();
            if (previous != null)
            {
                await _vm.SwitchProjectCommand.ExecuteAsync(_vm.Projects.FirstOrDefault(x => x.Name == previous));
                Check(projects.ActiveName == previous, $"'{previous}' is active again");
            }
            try { File.Delete(marker); } catch { }
        }
    }

    /// <summary>Every New gametype template, in memory: converted to Kalo Script, preset applied, compiled by HREK.</summary>
    private async Task TemplatesSection()
    {
        await _vm.EnsureMegaloCatalogAsync();
        _vm.OpenNewGametypeFormCommand.Execute(null);
        _vm.IsNewGametypeOpen = false;
        Check(_vm.GametypeTemplates.Count >= 18, $"New gametype offers {_vm.GametypeTemplates.Count} templates");
        foreach (var t in _vm.GametypeTemplates)
        {
            var doc = new ScriptEditorViewModel(_vm, @"data\multiplayer\megalo\mods\_kalo_tour\template.kscript") { Text = "# title: T\n" + _vm.TemplateText(t, true) };
            await doc.CompileCommand.ExecuteAsync(null);
            Check(doc.CompileOk, $"{t.Label}: compiles ({doc.CompileSummary})");
            if (t.Preset == null) continue;
            var gs = doc.GametypeSettings;
            gs.Reload();
            var baseTraits = gs.Traits.FirstOrDefault();
            string primary = baseTraits?.Rows.FirstOrDefault(r => r.Key == "initial_primary_weapon")?.Value ?? "";
            Check(primary.Length > 0 && gs.Loadouts.Spartan?.Value == "empty_palette" && gs.Settings.Any(r => r.Name == "weapon_set" && r.Value == "none"),
                $"{t.Label}: Settings shows the preset (primary weapon {primary}, loadouts {gs.Loadouts.Spartan?.Label}, weapons on map none)");
        }
    }

    /// <summary>Settings › Loadouts on the starter (in memory): own loadouts, None for Elites, a refused mix; compiled by HREK.</summary>
    private async Task LoadoutsSection()
    {
        await _vm.EnsureMegaloCatalogAsync();
        var doc = new ScriptEditorViewModel(_vm, @"data\multiplayer\megalo\mods\_kalo_tour\loadouts.kscript") { Text = "# title: Loadouts\n" + Kalo.Core.Scripting.KaloScript.Starter };
        _vm.Tabs.Add(doc);
        _vm.ActiveDocument = doc;
        try
        {
            doc.ToggleSettingsCommand.Execute(null);
            var lo = doc.GametypeSettings.Loadouts;
            Check(lo.Spartan?.Value == null && !lo.ShowCustom, "a script without loadouts shows Game default: " + lo.Summary);
            lo.AddLoadoutCommand.Execute(null);
            lo.AddLoadoutCommand.Execute(null);
            Check(lo.Custom.Count == 2 && lo.Spartan?.Value == "kalo_loadouts" && lo.ShowCustom, "+ Loadout adds a loadout and Spartans pick from My loadouts");
            lo.Custom[1].Primary = "sniper_rifle";
            lo.Custom[1].Equipment = "jet_pack_equipment";
            lo.Elite = lo.EliteChoices.First(c => c.Value == "empty_palette");
            Check(doc.Text.Contains("loadout kalo_loadout_2:") && doc.Text.Contains("primary_weapon = sniper_rifle") && doc.Text.Contains("equipment = jet_pack_equipment")
                  && doc.Text.Contains("setting loadout_palette = spartan_tier1 kalo_loadouts") && doc.Text.Contains("setting loadout_palette = elite_tier1 kalo_no_loadouts"),
                "the form writes the loadouts and palettes into the script");
            await doc.CompileCommand.ExecuteAsync(null);
            Check(doc.CompileOk, "HREK's compiler accepts it: " + doc.CompileSummary);
            await Pause(600);
            Shot("loadouts");
            string before = doc.Text;
            lo.Elite = lo.EliteChoices.First(c => c.Value == "covy_slayer_loadouts");
            Check(lo.HasProblem && doc.Text == before, "mixing my loadouts with Bungie's is refused (script unchanged): " + lo.Problem);
            lo.Spartan = lo.SpartanChoices.First(c => c.Value == "slayer_loadouts");
            Check(!lo.HasProblem && doc.Text.Contains("include \"includes/multiplayer_loadouts.txt\"") && !doc.Text.Contains("loadout kalo_loadout_1"),
                "Bungie's for both: the include is added and my loadouts leave the script");
            await doc.CompileCommand.ExecuteAsync(null);
            Check(doc.CompileOk, "and it compiles: " + doc.CompileSummary);
            lo.Spartan = lo.SpartanChoices[0];
            lo.Elite = lo.EliteChoices[0];
            Check(!doc.Text.Contains("loadout_palette"), "Game default for both removes the loadout lines");
            doc.ToggleSettingsCommand.Execute(null);
            doc.ToggleSettingsCommand.Execute(null);
            Check(lo.Spartan?.Value == null, "reading the script back gives the same choices");
        }
        finally
        {
            doc.IsDirty = false;
            await _vm.CloseTabCommand.ExecuteAsync(doc);
        }
    }

    // ---- Kalo Script: the modern gametype language, compiled through Megalo -----------------------------------

    private async Task KaloScriptSection()
    {
        await _vm.EnsureMegaloCatalogAsync();
        // In memory only: nothing is written to the mod.
        var doc = new ScriptEditorViewModel(_vm, @"data\multiplayer\megalo\mods\_kalo_tour\tour.kscript") { Text = Kalo.Core.Scripting.KaloScript.ClassicStarter };
        Check(doc.IsKaloScript && doc.Badge == "kalo", "a .kscript opens as Kalo Script");
        await doc.CompileCommand.ExecuteAsync(null);
        Check(doc.CompileOk, "the starter compiles: " + doc.CompileSummary);
        Check(doc.GeneratedMegalo.Contains("trigger player") && doc.GeneratedMegalo.Contains("action set killer.score += kill_points"), "it turns into the expected Megalo");

        // Grouped conditions: a or (b and c) compiles (as (a or b) and (a or c)).
        doc.Text = Kalo.Core.Scripting.KaloScript.ClassicStarter.Replace("if killer.streak == 5 then",
            "if killer.streak == 5 or (killer.score > 10 and not current_player.team == none) then");
        await doc.CompileCommand.ExecuteAsync(null);
        Check(doc.CompileOk && doc.GeneratedMegalo.Contains("condition if killer.streak == 5 or"),
            "an if with parentheses compiles: " + doc.CompileSummary);

        // A timer compared with == : a clear message, with the number that was probably meant.
        doc.Text = "declare global.number placing_time = 0\ndeclare global.timer placing_timer = 30\ndo\n  if placing_timer == 1 then\n    placing_time = 0\n  end\nend\n";
        await doc.CompileCommand.ExecuteAsync(null);
        var timerError = doc.Messages.FirstOrDefault();
        Check(!doc.CompileOk && doc.Messages.Count == 1 && timerError?.Line == 4 && timerError.Message.Contains("Did you mean the number placing_time"),
            $"comparing a timer with == explains it ({timerError?.Line}: {timerError?.Message})");

        // An undeclared member (current_player.deaths): HREK's message is replaced by an explanation.
        doc.Text = "for each player do\n  temp number alive = 0\n  if current_player.deaths < 3 then\n    alive += 1\n  end\nend\n";
        await doc.CompileCommand.ExecuteAsync(null);
        var memberError = doc.Messages.FirstOrDefault(m => m.IsError);
        Check(!doc.CompileOk && memberError?.Line == 3 && memberError.Message.Contains("no player variable called deaths") && memberError.Token == "current_player.deaths",
            $"an undeclared member is explained ({memberError?.Line}: {memberError?.Message})");

        // Enums and values in messages compile through HREK.
        doc.Text = "declare player.enum{placer, player, spectator} applied_trait = spectator\nalias goal_message = \"{current_player} reached goal!\"\n\nfor each player do\n  if current_player.applied_trait == placer then\n    current_player.applied_trait = player\n    hud_post_message(everyone, ball_taken, \"{current_player.name} reached goal! {current_player.score} points\")\n    hud_post_message(everyone, ball_taken, goal_message)\n  end\nend\n";
        await doc.CompileCommand.ExecuteAsync(null);
        Check(doc.CompileOk && doc.GeneratedMegalo.Contains("\"%p reached goal! %n points\" current_player current_player.score") && doc.GeneratedMegalo.Contains("applied_trait == 0"),
            "enum values and {values} in messages compile: " + doc.CompileSummary);

        // Functions: each call is a copy of the body, compiled by HREK.
        doc.Text = "declare player.number streak = 0\n\n-- give points and say so\nfunction award(p, points)\n  temp number bonus = 0\n  bonus = points\n  p.score += bonus\n  hud_post_message(player p, none, \"+{points} for {p}\")\nend\n\nfunction reset(p)\n  p.streak = 0\n  award(p, 1)\nend\n\nfor each player do\n  if player_died(current_player, enemy) then\n    temp player killer = none\n    player_death_get_killing_player(current_player, killer)\n    award(killer, 2)\n    reset(current_player)\n  end\nend\n";
        await doc.CompileCommand.ExecuteAsync(null);
        Check(doc.CompileOk && doc.GeneratedMegalo.Contains("action set killer.score += bonus_award_") && doc.GeneratedMegalo.Contains("action set current_player.streak = 0") && doc.GeneratedMegalo.Contains("\"+2 for %p\" killer"),
            "functions compile (each call a copy): " + doc.CompileSummary + (doc.CompileOk ? "" : " " + string.Join(" | ", doc.Messages.Select(m => $"{m.Line}: {m.Message}"))));
        var fnHover = doc.Hover("    award(killer, 2)", 6);
        Check(fnHover?.Title == "function award" && fnHover.Text.StartsWith("give points and say so"), $"hovering a call shows the function ({fnHover?.Title}: {fnHover?.Text})");

        // An object type that doesn't exist: named, with the real one for a free camera.
        doc.Text = "for each player do\n  temp object mon = none\n  if not object_is_type(current_player, \"free_cam\") then\n    create_object(\"free_cam\", set mon, at current_player)\n  end\nend\n";
        await doc.CompileCommand.ExecuteAsync(null);
        var typeError = doc.Messages.FirstOrDefault(m => m.IsError);
        Check(!doc.CompileOk && typeError != null && typeError.Message.Contains("is not an object type") && typeError.Message.Contains("monitor"),
            $"an unknown object type is explained ({typeError?.Line}: {typeError?.Message})");
        doc.Text = doc.Text.Replace("free_cam", "monitor");
        await doc.CompileCommand.ExecuteAsync(null);
        Check(doc.CompileOk, "with \"monitor\" it compiles: " + doc.CompileSummary);

        doc.Text = Kalo.Core.Scripting.KaloScript.ClassicStarter.Replace("killer.streak += 1", "killer.streak += ");
        await doc.CompileCommand.ExecuteAsync(null);
        var first = doc.Messages.FirstOrDefault();
        int expected = Kalo.Core.Scripting.KaloScript.ClassicStarter.Split('\n').ToList().FindIndex(l => l.Contains("killer.streak += 1")) + 2;
        Check(!doc.CompileOk && first != null && first.Line >= expected - 1 && first.Line <= expected, $"a mistake is reported on the Kalo Script line ({first?.Line}: {first?.Message})");

        doc.Text = Kalo.Core.Scripting.KaloScript.ClassicStarter.Replace("player_death_get_killing_player", "player_death_get_killing_playr");
        await doc.CompileCommand.ExecuteAsync(null);
        first = doc.Messages.FirstOrDefault(m => m.IsError);
        int callLine = Kalo.Core.Scripting.KaloScript.ClassicStarter.Split('\n').ToList().FindIndex(l => l.Contains("player_death_get_killing_player")) + 1;
        Check(!doc.CompileOk && first?.Line == callLine, $"a Megalo compiler error points at the Kalo Script line ({first?.Line} vs {callLine}: {first?.Message})");

        // The Settings panel: the declarations as forms, written back into the code.
        doc.Text = Kalo.Core.Scripting.KaloScript.ClassicStarter;
        doc.ToggleSettingsCommand.Execute(null);
        var gs = doc.GametypeSettings;
        var score = gs.Settings.FirstOrDefault(r => r.Name == "score_to_win_round");
        Check(doc.ShowSettings && score?.Value == "25" && gs.Options.Count == 1 && gs.Options[0].Name == "kill_points",
            $"Settings shows the starter's settings and option ({score?.Value}, {gs.Options.Count} option)");
        if (score != null) score.Value = "50";
        if (gs.Options.Count > 0) gs.Options[0].Max = "20";
        gs.AddConstantCommand.Execute(null);
        gs.AddTraitsCommand.Execute(null);
        Check(doc.Text.Contains("setting score_to_win_round = 50") && doc.Text.Contains("range -10 20") && doc.Text.Contains("constant k_my_constant = 0") && doc.Text.Contains("traits my_traits"),
            "editing the forms rewrites the script");
        await doc.CompileCommand.ExecuteAsync(null);
        Check(doc.CompileOk, "the edited script still compiles: " + doc.CompileSummary);
        doc.ToggleSettingsCommand.Execute(null);

        doc.Text = Kalo.Core.Scripting.KaloScript.ClassicStarter;
        string? bin = await doc.ExportBin();
        Check(bin != null && File.Exists(bin) && Kalo.Core.Scripting.Multiplayer.GameVariantFile.HashIsValid(File.ReadAllBytes(bin)), "Export .bin wrote an MCC game type file: " + bin);
        Check(_vm.NewGametypeKaloScript, "new gametypes default to Kalo Script");

        // Convert a Bungie script and show both in the split view.
        var koth = _vm.Scripts.FirstOrDefault(s => s.RelativePath.EndsWith(@"megalo\juggernaut.txt", StringComparison.OrdinalIgnoreCase));
        if (koth != null)
        {
            var megalo = await _vm.OpenScriptAsync(koth.RelativePath);
            string converted = MainViewModel.PythonStyle(Kalo.Core.Scripting.KaloScript.FromMegalo(megalo!.Text));
            var kdoc = new ScriptEditorViewModel(_vm, @"data\multiplayer\megalo\mods\_kalo_tour\juggernaut.kscript") { Text = converted };
            Check(!kdoc.IsClassicStyle, "the conversion is in the Python style (no then/do/end)");
            await kdoc.CompileCommand.ExecuteAsync(null);
            Check(kdoc.CompileOk, "a converted Bungie script (Juggernaut) compiles: " + kdoc.CompileSummary);
            _vm.Tabs.Add(kdoc);
            _vm.ActiveDocument = kdoc;
            kdoc.ToggleMegaloCommand.Execute(null);
            _vm.MoveToOtherGroup(megalo);
            await Pause(1500);
            Shot("kscript-convert");
            kdoc.ToggleMegaloCommand.Execute(null);
            kdoc.ToggleSettingsCommand.Execute(null);
            Check(kdoc.GametypeSettings.Traits.Count > 1 && kdoc.GametypeSettings.Options.Count > 0, $"Juggernaut's traits and options show in Settings ({kdoc.GametypeSettings.Traits.Count} traits, {kdoc.GametypeSettings.Options.Count} options)");
            await Pause(1200);
            Shot("kscript-settings");
            _vm.Unsplit();
            await _vm.CloseTabCommand.ExecuteAsync(kdoc);
            await _vm.CloseTabCommand.ExecuteAsync(megalo);
        }
    }

    // ---- hover help and the Kalo Script documentation -------------------------------------------------------

    private async Task DocsSection()
    {
        // Output search: only lines containing every word, any case; clearing shows everything again.
        _vm.BottomPanel = "output";
        _vm.Log("tour search check: megalotrace Score 12");
        _vm.Log("tour search check: something else");
        int all = _vm.OutputLines.Count;
        _vm.OutputFilter = "TOUR SEARCH megalotrace";
        Check(_vm.VisibleOutput.Count == 1 && _vm.VisibleOutput[0].Contains("score 12", StringComparison.OrdinalIgnoreCase), "Output search keeps only the matching line");
        _vm.Log("tour search check: megalotrace again");
        Check(_vm.VisibleOutput.Count == 2 && _vm.OutputFilterSummary.StartsWith("2 of "), "a new matching line shows while searching (" + _vm.OutputFilterSummary + ")");
        await Pause(400);
        Shot("output-search");
        _vm.OutputFilter = "";
        Check(_vm.VisibleOutput.Count == _vm.OutputLines.Count && _vm.OutputLines.Count >= all + 1, "clearing the search shows every line");
        await _vm.EnsureMegaloCatalogAsync();
        var doc = new ScriptEditorViewModel(_vm, @"data\multiplayer\megalo\mods\_kalo_tour\hover.kscript") { Text = Kalo.Core.Scripting.KaloScript.ClassicStarter };
        _vm.Tabs.Add(doc);
        _vm.ActiveDocument = doc;
        await Pause(800);
        try
        {
            var view = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(_window).OfType<Views.ScriptEditorView>().FirstOrDefault(v => v.DataContext == doc);
            Check(view != null, "the Kalo Script tab is shown");
            if (view == null) return;
            var lines = doc.Text.Split('\n');
            int Line(string what) => Array.FindIndex(lines, l => l.Contains(what)) + 1;
            int callLine = Line("player_death_get_killing_player(");
            var call = view.HoverAt(callLine, lines[callLine - 1].IndexOf("player_death") + 3);
            Check(call?.Title == "action player_death_get_killing_player" && call.Signature?.StartsWith("player_death_get_killing_player(<dead_player>") == true,
                $"hovering an action shows its help ({call?.Title}: {call?.Signature})");
            int ifLine = Line("if player_died(");
            var keyword = view.HoverAt(ifLine, lines[ifLine - 1].IndexOf("if") + 1);
            Check(keyword?.Title == "keyword if", $"hovering a keyword explains it ({keyword?.Title})");
            int streakLine = Line("killer.streak += 1");
            var variable = view.HoverAt(streakLine, lines[streakLine - 1].IndexOf("streak") + 2);
            Check(variable?.Signature?.Contains("player.number streak") == true, $"hovering a variable shows where it is declared ({variable?.Signature}: {variable?.Text})");
            var comment = view.HoverAt(1, 6);
            Check(comment == null, "comments have no hover");
            Check(view.ShowHoverFor("player_death_get_killing_player"), "the hover popup opens");
            await Pause(600);
            view.SaveHoverShot(System.IO.Path.Combine(_dir, $"{++_shot:D2}-hover.png"));
            Shot("hover-editor");

            // Errors as you type: underlined in red a moment after typing stops, explained on hover.
            async Task<bool> Settle(Func<bool> done)
            {
                var until = DateTime.UtcNow.AddSeconds(10);
                while (!done() && DateTime.UtcNow < until) await Task.Delay(150);
                await Pause(300);
                return done();
            }
            // Grouped conditions: a or (b and c) compiles (as (a or b) and (a or c)).
        doc.Text = Kalo.Core.Scripting.KaloScript.ClassicStarter.Replace("if killer.streak == 5 then",
            "if killer.streak == 5 or (killer.score > 10 and not current_player.team == none) then");
        await doc.CompileCommand.ExecuteAsync(null);
        Check(doc.CompileOk && doc.GeneratedMegalo.Contains("condition if killer.streak == 5 or"),
            "an if with parentheses compiles: " + doc.CompileSummary);

        doc.Text = Kalo.Core.Scripting.KaloScript.ClassicStarter.Replace("killer.streak += 1", "killer.streak += ");
            bool underlined = await Settle(() => view.Underlined.Any(m => m.IsError));
            var marks = view.UnderlinedText;
            int streakAt = Line("killer.streak += 1");
            Check(underlined && marks.Any(m => m.Line == streakAt && m.Text == "+="),
                $"a Kalo Script mistake is underlined while typing, no Compile needed ({string.Join(", ", marks.Select(m => $"line {m.Line} '{m.Text}'"))})");
            doc.Text = Kalo.Core.Scripting.KaloScript.ClassicStarter.Replace("player_death_get_killing_player", "player_death_get_killing_playr");
            underlined = await Settle(() => view.UnderlinedText.Any(m => m.Text.Contains("player_death_get_killing_playr")));
            Check(underlined, $"an unknown action (found by HREK's compiler) is underlined on its word ({string.Join(", ", view.UnderlinedText.Select(m => $"line {m.Line} '{m.Text}'"))})");
            Check(view.UnderlinedText.Count(m => m.Line == callLine) == 1 && view.UnderlinedText.All(m => !m.Text.Contains("Kill Points")),
                "one underline per line, none for Bungie's localization warnings");
            Check(view.ShowHoverFor("player_death_get_killing_playr"), "pointing at it shows the error");
            await Pause(600);
            view.SaveHoverShot(System.IO.Path.Combine(_dir, $"{++_shot:D2}-error-hover.png"));
            Shot("error-underline");
            doc.Text = Kalo.Core.Scripting.KaloScript.ClassicStarter;
            Check(await Settle(() => view.Underlined.Count(m => m.IsError) == 0), "fixing it removes the underline");

            // Autocomplete by argument: inside a call, what that argument takes.
            var inCall = view.SuggestionsAfter("player_death_get_killing_player(");
            Check(inCall.Contains("current_player") && !inCall.Contains("create_object") && !inCall.Contains("for"),
                $"right after 'player_death_get_killing_player(' the suggestions are players ({string.Join(", ", inCall.Take(8))})");
            var deathTypes = view.SuggestionsAfter("player_died(current_player, ");
            Check(deathTypes.Contains("enemy") && deathTypes.Contains("suicide"), $"player_died's second argument offers death types ({string.Join(", ", deathTypes.Take(8))})");
            var targets = view.SuggestionsAfter("hud_post_message(");
            Check(targets.Contains("player killer") || targets.Contains("player current_player"), $"a message target offers 'player ...' ({string.Join(", ", targets.Take(8))})");
            // Arrow keys after typing a call (a suggestion list opened at "(" must not keep them).
            doc.Text = Kalo.Core.Scripting.KaloScript.ClassicStarter;
            await Pause(300);
            int startLine = Line("player_death_get_killing_player(");
            var (after, open) = view.TypeThenPress(startLine, "\n    hud_post_message(everyone, flag_captured, \"x\")", Avalonia.Input.Key.Down);
            Check(after == startLine + 2 && !open, $"Down moves to the next line after typing a call ({startLine + 1} → {after}, list open: {open})");
            var (back, _) = view.TypeThenPress(after, "", Avalonia.Input.Key.Up);
            Check(back == startLine + 1, $"Up moves back ({after} → {back})");
            doc.Text = Kalo.Core.Scripting.KaloScript.ClassicStarter;
            await Pause(200);

            // Format document (Ctrl+Shift+I).
            doc.Text = "for each player do\nif player_died(current_player,enemy) then\n      temp player killer=none\n\n\n\nkiller.score+=1\nend\nend\n";
            await Pause(200);
            view.FormatDocument();
            string formatted = doc.Text.Replace("\r\n", "\n");
            Check(formatted == "for each player do\n  if player_died(current_player, enemy) then\n    temp player killer = none\n\n    killer.score += 1\n  end\nend\n",
                "Ctrl+Shift+I formats the file (indentation, spaces, blank lines): " + formatted.Replace("\n", "⏎"));
            doc.Text = Kalo.Core.Scripting.KaloScript.ClassicStarter;
            await Pause(200);
            var members = view.SuggestionsAfter("killer.score += kill_points\n    killer.");
            Check(members.Contains("streak") && members.Contains("score") && !members.Contains("create_object"),
                $"after 'killer.' the suggestions are the player's variables ({string.Join(", ", members.Take(6))})");
            var sounds = view.SuggestionsAfter("hud_post_message(player killer, ");
            Check(sounds.Contains("flag_captured") && sounds.Contains("slayer"), $"a message's sound argument offers the game's sounds ({string.Join(", ", sounds.Take(6))}…)");
            var (wav, soundMessage) = await _vm.PrepareMegaloSoundAsync("flag_captured");
            long wavBytes = wav != null && File.Exists(wav) ? new FileInfo(wav).Length : 0;
            Check(wavBytes > 48000, $"the sound flag_captured is decoded from the game's banks for a preview ({soundMessage}; {wavBytes:N0} bytes)");
            var (wav2, m2) = await _vm.PrepareMegaloSoundAsync("ball_spawned");
            Check(wav2 != null, "another sound (ball_spawned → play_ball) is found: " + m2);
            string? hint = view.CallHintAfter("player_death_get_killing_player(current_player, ");
            Check(hint?.Contains("▸<killing_player>") == true, $"the call hint marks the current argument ({hint})");
            await Pause(500);
            Shot("call-hint");

            var docs = Views.DocsWindow.Open(_window, _vm, "create_object");
            await Pause(1200);
            Check(docs.Current?.Title == "Actions: Objects", $"F1 on create_object opens its page ({docs.Current?.Title})");
            MainWindow.SaveShot(docs, System.IO.Path.Combine(_dir, $"{++_shot:D2}-docs-reference.png"));
            docs.GoTo("start");
            await Pause(600);
            Check(docs.Current?.Title == "Getting started", "the guide opens at Getting started");
            MainWindow.SaveShot(docs, System.IO.Path.Combine(_dir, $"{++_shot:D2}-docs-guide.png"));
            docs.GoTo("recipe-zones");
            await Pause(600);
            Check(docs.ShowingRecipes && docs.Current?.Group == "Multiplayer", $"a recipe opens the Recipes book ({docs.Current?.Group} › {docs.Current?.Title})");
            MainWindow.SaveShot(docs, System.IO.Path.Combine(_dir, $"{++_shot:D2}-docs-recipes.png"));
            docs.GoTo("mission");
            await Pause(600);
            Check(!docs.ShowingRecipes && docs.Current?.Group == "Mission scripts", "the reference has a Mission scripts page");
            docs.GoTo("timer");
            await Pause(600);
            Check(docs.Current == null && docs.Query == "timer", "an unknown word searches the docs");
            MainWindow.SaveShot(docs, System.IO.Path.Combine(_dir, $"{++_shot:D2}-docs-search.png"));
            docs.Close();
        }
        finally
        {
            await _vm.CloseTabCommand.ExecuteAsync(doc);
        }
    }

    // ---- Forge World: terrain, foliage, water in the textured view ------------------------------------------

    private async Task Forge()
    {
        var map = await _vm.OpenMapAsync(@"levels\multi\forge_halo\forge_halo.scenario");
        map.Shading = ViewShading.Textured;
        var until = DateTime.UtcNow.AddMinutes(10);
        while (map.Bsp == null && DateTime.UtcNow < until && !map.GeometryStatus.Contains("unavailable")) await Task.Delay(500);
        var tu = DateTime.UtcNow.AddMinutes(6);
        await Pause(1000);
        while ((map.TexturesLoading || map.TexturesVersion == 0 || map.ModelVersion == 0) && DateTime.UtcNow < tu) await Task.Delay(500);
        var mats = map.Bsp?.Materials ?? Array.Empty<Kalo.Core.Geometry.MeshMaterial>();
        Check(mats.Count(m => m.IsTerrain) > 5, $"terrain materials with blended layers: {mats.Count(m => m.IsTerrain)}");
        Check(mats.Any(m => m.Color.W > 0), "water gets a sea color");
        var grass = mats.SelectMany(m => m.Layers).OfType<Kalo.Core.Geometry.TextureLayer>().FirstOrDefault(l => l.Bitmap.Contains("grass"));
        Check(grass != null && map.TextureFor(grass.Bitmap) != null, $"grass layer decoded ({grass?.Bitmap}, tiling {grass?.ScaleX}/{grass?.ScaleY})");
        Say("tiling seen: " + string.Join(", ", mats.SelectMany(m => m.Layers).OfType<Kalo.Core.Geometry.TextureLayer>().Select(l => l.ScaleX).Distinct().Take(8)));
        map.ViewFromSpawnCommand.Execute(null);
        await Pause(1500);
        await map.CaptureViewAsync(System.IO.Path.Combine(_dir, "forge-spawn.png"));
        if (map.Spawn is { } sp)
        {
            map.LookFrom(sp.Position + new Vector3(0, 0, 40), sp.Facing, -35);
            await Pause(1500);
            await map.CaptureViewAsync(System.IO.Path.Combine(_dir, "forge-above.png"));
        }
        map.FrameAllCommand.Execute(null);
        await Pause(1500);
        await map.CaptureViewAsync(System.IO.Path.Combine(_dir, "forge-overview.png"));
        await _vm.CloseTabCommand.ExecuteAsync(map);
    }

    // ---- gametype script -------------------------------------------------------------------------------

    private async Task Script()
    {
        var file = _vm.Scripts.FirstOrDefault(s => s.Name == "slayer");
        if (!Check(file != null, $"gametypes panel lists slayer ({_vm.Scripts.Count} scripts)")) return;
        _vm.ShowGametypesCommand.Execute(null);
        var script = await _vm.OpenScriptAsync(file!.RelativePath);
        if (!Check(script != null && script.Text.Length > 0, "opened slayer.txt")) return;
        await script!.CompileCommand.ExecuteAsync(null);
        Check(script.CompileOk, "slayer compiles: " + script.CompileSummary);
        Check(script.Stats.Count > 0, "budget stats shown");
        script.CatalogQuery = "score";
        await Pause(300);
        Check(script.CatalogResults.Any(), $"catalog search finds score actions ({script.CatalogResults.Count})");
        var (start2, actions) = script.Complete("\taction set_sc");
        Check(start2 == 8 && actions.Any(a => a.Text == "set_score" && a.Description != null), $"autocomplete after 'action set_sc' offers set_score ({actions.Count})");
        var (_, conditions) = script.Complete("\tcondition ");
        Check(conditions.FirstOrDefault()?.Text == "if" && conditions.Count > 5, $"autocomplete after 'condition ' offers if + conditions ({conditions.Count})");
        var (_, values) = script.Complete("\taction create_object warth");
        Check(values.Any(v => v.Text == "warthog"), "autocomplete offers engine values (warthog)");
        Check(_vm.SelectedGametype?.Path == script.Path, "the Play gametype picker follows the open script");
        await Pause(800);
        Shot("script-compiled");

        string good = script.Text;
        script.Text = good + "\r\ntrigger general\r\n  action kalo_not_an_action 1\r\nend\r\n";
        await script.CompileCommand.ExecuteAsync(null);
        Check(!script.CompileOk && script.Messages.Any(m => m.IsError && m.Line > 0), "a bad action is reported with its line: " + script.Messages.FirstOrDefault()?.Display);
        await Pause(800);
        Shot("script-error");
        script.Text = good;
        Check(!script.IsDirty, "text back to the saved file");
        await _vm.CloseTabCommand.ExecuteAsync(script);
    }

    // ---- projects ----------------------------------------------------------------------------------------

    private async Task Projects()
    {
        var projects = _vm.ModProjects;
        if (!Check(projects != null, "mod projects available")) return;
        // Leftovers from an interrupted tour: put the workspace back and remove them.
        if (projects!.ActiveName?.StartsWith("Kalo tour ") == true)
        {
            Say("cleaning up a previous tour project");
            await _vm.SwitchProjectCommand.ExecuteAsync(null);
        }
        foreach (var old in projects.List().Where(x => x.Name.StartsWith("Kalo tour ")).ToList()) projects.Delete(old);
        if (projects.ActiveName != null) { Say("a project is active; skipping the project test to leave it alone"); return; }
        string name = "Kalo tour " + DateTime.Now.ToString("HHmmss");
        _vm.ShowProjectsCommand.Execute(null);
        _vm.NewProjectName = name;
        _vm.NewProjectDescription = "Created by kalo --tour, deleted at the end.";
        await _vm.CreateProjectCommand.ExecuteAsync(null);
        var p = projects.Find(name);
        Check(p != null && projects.ActiveName == name, "created and activated a project");
        await Pause(500);
        Shot("projects");
        if (p == null) return;

        // Edit a weapon inside the project, then switch away and back: the workspace must follow.
        const string weapon = @"objects\weapons\rifle\assault_rifle\assault_rifle.weapon";
        const string magazine = "Block:magazines[0]/ShortInteger:rounds loaded maximum";
        string rel = @"tags\" + weapon;
        var tab = await _vm.OpenTagAsync(weapon);
        var field = tab?.FindField(magazine);
        if (!Check(field != null, "opened the weapon in the project")) return;
        field!.Value = "33";
        for (int i = 0; i < 100 && !field.IsDirty && field.Error == null; i++) await Task.Delay(50);
        await tab!.SaveCommand.ExecuteAsync(null);
        await _vm.CaptureProjectCommand.ExecuteAsync(null);
        p = projects.Find(name)!;
        Check(p.Manifest.Files.Contains(rel, StringComparer.OrdinalIgnoreCase), $"the project recorded the weapon ({p.Manifest.Files.Count} file)");

        await _vm.SwitchProjectCommand.ExecuteAsync(null);
        Check(projects.ActiveName == null && _vm.Baseline!.IsChanged(rel) == false, "switching to vanilla put the weapon back");
        var item = _vm.Projects.FirstOrDefault(x => x.Name == name);
        await _vm.SwitchProjectCommand.ExecuteAsync(item);
        Check(projects.ActiveName == name && _vm.Baseline!.IsChanged(rel), "switching back loaded the project's weapon");
        var again = await _vm.OpenTagAsync(weapon);
        Check(again?.FindField(magazine)?.Value == "33", "the weapon has the project's magazine size (33)");
        var diff = await _vm.CompareWithVanillaAsync(rel);
        var magRow = diff?.Rows.FirstOrDefault(r => r.Detail == magazine);
        Check(magRow != null && magRow.Vanilla == "32" && magRow.Current == "33", $"Compare shows magazine size 32 → 33 ({diff?.Summary})");
        await Pause(500);
        Shot("diff");
        var lines = Kalo.Core.TextDiff.Compare(new[] { "a", "b", "c" }, new[] { "a", "x", "c" });
        Check(lines.Count(l => l.Op == '-') == 1 && lines.Count(l => l.Op == '+') == 1, "text diff finds one changed line");
        await Pause(500);
        Shot("project-switched");

        string zip = await Task.Run(() => projects.Export(p));
        Check(File.Exists(zip) && new FileInfo(zip).Length > 0, "exported " + System.IO.Path.GetFileName(zip));
        var imported = await _vm.ImportProjectAsync(zip);
        Check(imported != null && imported.Name != name && imported.Manifest.Files.Contains(rel, StringComparer.OrdinalIgnoreCase),
            $"imported the zip as a new project ({imported?.Name}, {imported?.Manifest.Files.Count} file)");
        if (imported != null) projects.Delete(imported);
        try { File.Delete(zip); } catch { }

        await _vm.SwitchProjectCommand.ExecuteAsync(null);
        Check(!_vm.Baseline!.IsChanged(rel), "back to vanilla");
        projects.Delete(projects.Find(name)!);
        _vm.RefreshProjects();
        Check(projects.ActiveName == null && projects.Find(name) == null, "cleaned up the test project");
    }

    // ---- a new mod from templates ------------------------------------------------------------------------

    private const string TourMod = "Kalo tour mod";

    /// <summary>Creates a mod with a new empty map and a new gametype (and plays them when <paramref name="play"/>), then removes it all.</summary>
    private async Task NewMod() => await NewModCore(false);

    private async Task NewModCore(bool play)
    {
        var projects = _vm.ModProjects!;
        // The project that was active before an earlier, interrupted run (so it can be restored).
        string marker = System.IO.Path.Combine(_vm.Paths!.KaloData, "tour-previous-project.txt");
        string? interrupted = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        foreach (var old in projects.List().Where(x => x.Name.StartsWith("Kalo tour")).ToList())
        {
            if (projects.ActiveName == old.Name) await _vm.SwitchProjectCommand.ExecuteAsync(null);
            projects.Delete(old);
        }
        _vm.RefreshProjects();
        if (projects.ActiveName == null && !string.IsNullOrEmpty(interrupted) && projects.Find(interrupted) != null)
            await _vm.SwitchProjectCommand.ExecuteAsync(_vm.Projects.FirstOrDefault(x => x.Name == interrupted));
        // Someone's mod may be active: Create mod puts it aside (saved in its project folder); it is switched back at the end.
        string? previous = projects.ActiveName;
        if (previous != null) Say($"'{previous}' is active: it is saved and put aside, then restored after the test");
        File.WriteAllText(marker, previous ?? "");
        _vm.NewModName = TourMod;
        await _vm.CreateModCommand.ExecuteAsync(null);
        Check(_vm.HasActiveMod && _vm.ActiveModName == TourMod, "created the mod");
        Check(_vm.ModGroups.Select(g => g.Name).Take(2).SequenceEqual(new[] { "Maps", "Gametypes" }) && _vm.ModGroups.All(g => g.Items.Count == 0),
            "My mod shows empty Maps, Gametypes, Weapons… groups");
        try
        {
            // New map: the Sword Slayer level with only its spawn points.
            _vm.OpenNewMapFormCommand.Execute(null);
            Check(_vm.IsNewMapOpen && _vm.BaseLevels.Any(), $"New map form offers {_vm.BaseLevels.Count()} Reach levels");
            var map = await _vm.CreateMapAsync("Tour Arena", @"levels\multi\20_sword_slayer\20_sword_slayer", empty: true);
            Check(map != null && map.Path.StartsWith(@"levels\mods\kalo_tour_mod\tour_arena\"), "created levels\\mods\\kalo_tour_mod\\tour_arena");
            if (map != null)
            {
                var until = DateTime.UtcNow.AddMinutes(3);
                while (map.Bsp == null && DateTime.UtcNow < until) await Task.Delay(300);
                int spawns = map.Items.Count(i => i.Tag.Contains("respawn_point"));
                int objects = map.Items.Count(i => i.Category != Kalo.Protocol.StartLocations.Category); // start locations are listed too
                Check(spawns > 0 && objects == spawns, $"the empty map keeps only its {spawns} spawn points ({objects} objects)");
                Check(map.Bsp != null, "the new map shows the Sword Slayer level");
                var rifle = await map.AddTagAtAsync(@"objects\weapons\rifle\assault_rifle\assault_rifle.weapon", map.Items[0].Position + new Vector3(1, 0, 0.3f));
                Check(rifle != null, "placed an assault rifle on the new map");
                await map.SaveCommand.ExecuteAsync(null);
                Check(_vm.SelectedScenario == map.Scenario, "Play now targets the new map");
            }
            Check(_vm.ModGroups.First(g => g.Name == "Maps").Items.Any(e => e.Path.EndsWith("tour_arena.scenario")), "My mod › Maps lists the new map");
            await Pause(800);
            Shot("new-map");

            // New gametype from the simple starter template.
            _vm.OpenNewGametypeFormCommand.Execute(null);
            Check(_vm.GametypeTemplates.Count > 3, $"New gametype offers {_vm.GametypeTemplates.Count} templates");
            var script = await _vm.CreateGametypeAsync("Tour Rules", _vm.GametypeTemplates.First());
            Check(script != null && script.Path.EndsWith(@"mods\kalo_tour_mod\tour_rules.txt"), "created the gametype in the mod's folder");
            if (script != null)
            {
                await script.CompileCommand.ExecuteAsync(null);
                Check(script.CompileOk, "the new gametype compiles: " + script.CompileSummary);
                Check(_vm.SelectedGametype?.Path == script.Path, "Play now uses the new gametype");
            }
            Check(_vm.ModGroups.First(g => g.Name == "Gametypes").Items.Count == 1, "My mod › Gametypes lists it");
            await Pause(800);
            Shot("new-gametype");
            Say("play: " + _vm.PlaySummary);

            if (play)
            {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _vm.Game!.Loaded += () => loaded.TrySetResult();
                _vm.Game.Exited += _ => loaded.TrySetException(new Exception("the game exited while loading"));
                await _vm.PlayCommand.ExecuteAsync(null);
                if (!Check(_vm.IsGameRunning, "Play started the game")) return;
                var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6)));
                Check(done == loaded.Task && loaded.Task.IsCompletedSuccessfully, "the game loaded the new map with the new gametype");
                Say($"holding the game for {_hold:F0} s");
                await Task.Delay(TimeSpan.FromSeconds(_hold));
                _vm.StopGameCommand.Execute(null);
                await Task.Delay(3000);
            }
        }
        finally
        {
            if (_vm.Game?.IsRunning == true) _vm.Game.Stop();
            await _vm.SwitchProjectCommand.ExecuteAsync(null);
            var p = projects.Find(TourMod);
            if (p != null) projects.Delete(p);
            _vm.RefreshProjects();
            if (previous != null)
            {
                await _vm.SwitchProjectCommand.ExecuteAsync(_vm.Projects.FirstOrDefault(x => x.Name == previous));
                Check(projects.ActiveName == previous, $"'{previous}' is active again");
            }
            try { File.Delete(marker); } catch { }
            string newScenario = System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, @"levels\mods\kalo_tour_mod\tour_arena\tour_arena.scenario");
            string newScript = System.IO.Path.Combine(_vm.Paths.Workspace, @"data\multiplayer\megalo\mods\kalo_tour_mod\tour_rules.txt");
            Check(!File.Exists(newScenario) && !File.Exists(newScript), "cleaned up: the mod's map and gametype are gone from the workspace");
        }
    }

    /// <summary>--mod-play-test: the new-mod flow, then play the new map with the new gametype in reach_tag_test.</summary>
    public async Task<int> ModPlayAsync()
    {
        Say("mod play test started");
        await Task.WhenAny(_vm.Ready, Task.Delay(TimeSpan.FromSeconds(60)));
        await Section("new mod + play", () => NewModCore(true));
        Say(_failures == 0 ? "MODPLAY PASS" : $"MODPLAY FAIL ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static Task Pause(int ms = 400) => Task.Delay(ms);

    private void Shot(string name)
    {
        _shot++;
        _window.SaveShot(System.IO.Path.Combine(_dir, $"{_shot:D2}-{name}.png"));
    }

    /// <summary>
    /// Campaign: a copy of m10 (made by kalo-cli campaign-probe at levels\mods\_kalo_tour\cprobe) in the map view: its
    /// markers, zone sets, a box volume resized, a squad spawn point moved and undone, its own mission script opened and
    /// checked; the Play bar and the New map form for campaign missions. Nothing is saved.
    /// </summary>
    private async Task Campaign()
    {
        const string probe = @"levels\mods\_kalo_tour\cprobe\cprobe.scenario";
        if (!Check(File.Exists(System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, probe)), "the campaign probe map exists (kalo-cli campaign-probe)")) return;
        var map = await _vm.OpenMapAsync(probe);
        Check(map.IsCampaign, "the map is a campaign mission");
        foreach (string c in CampaignCategories.All)
            Check(map.Items.Any(i => i.Category == c), $"{c}: {map.Items.Count(i => i.Category == c)}");
        Check(map.ZoneSetChoices.Count > 5, $"zone sets listed ({map.ZoneSetChoices.Count - 1})");
        Check(map.MissionScripts.Count(s => s.IsOwn) >= 5, $"mission scripts: {map.MissionScripts.Count} ({map.MissionScripts.Count(s => s.IsOwn)} of them this map's own)");
        Check(map.Cutscenes.Count > 0, $"cutscenes: {string.Join(", ", map.Cutscenes.Select(c => c.Name))}");
        var until = DateTime.UtcNow.AddMinutes(12);
        while (map.Bsp == null && DateTime.UtcNow < until && !map.GeometryStatus.StartsWith("No level") && !map.GeometryStatus.Contains("unavailable"))
            await Task.Delay(500);
        Check(map.Bsp is { TriangleCount: > 1000 }, $"whole mission geometry: {map.GeometryStatus}");
        int whole = map.Bsp?.TriangleCount ?? 0;

        // A box volume: inspector help and size.
        var box = map.Items.FirstOrDefault(i => i.IsBoxVolume);
        if (Check(box != null, "a box trigger volume"))
        {
            map.Selected = box;
            Check(box!.MarkerHelp.Contains("volume_test_players"), "inspector explains trigger volumes");
            var before = box.ZoneSize;
            var clock = Stopwatch.StartNew();
            box.VolumeWidth = (before.X + 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
            PlacementItem resized = box;
            while (clock.Elapsed.TotalSeconds < 30)
            {
                await Pause(250);
                resized = map.Items.First(i => i.Category == box.Category && i.Index == box.Index);
                if (Math.Abs(resized.ZoneSize.X - before.X - 2) < 0.01f) break;
            }
            Say($"resize took {clock.ElapsedMilliseconds} ms");
            Check(Math.Abs(resized.ZoneSize.X - before.X - 2) < 0.01f, $"volume {box.Display} resized to width {resized.ZoneSize.X:0.##} (was {before.X:0.##})");
            map.FocusSelectedCommand.Execute(null);
            await Pause(1200);
            Shot("campaign-volume");
            await map.UndoCommand.ExecuteAsync(null);
        }
        // A squad spawn point moved, then undone.
        var ai = map.Items.FirstOrDefault(i => i.Category == CampaignCategories.AiSpawnPoints);
        if (Check(ai != null, "a squad spawn point"))
        {
            var p0 = ai!.Position;
            map.Selected = ai;
            map.MoveLocal(ai, p0 + new Vector3(1, 0, 0));
            var moveClock = Stopwatch.StartNew();
            await map.CommitMoveAsync(ai);
            Say($"move took {moveClock.ElapsedMilliseconds} ms");
            foreach (string l in _vm.OutputLines.Where(l => l.Contains("undo step")).TakeLast(3)) Say(l);
            var moved = map.Items.First(i => i.Category == ai.Category && i.Index == ai.Index);
            Check(Math.Abs(moved.Position.X - p0.X - 1) < 0.01f, $"spawn point {ai.Display} ({ai.Label}) moved 1 unit");
            await map.UndoCommand.ExecuteAsync(null);
            var back = map.Items.First(i => i.Category == ai.Category && i.Index == ai.Index);
            Check(Math.Abs(back.Position.X - p0.X) < 0.01f, "undo puts it back");
        }
        // One zone set: fewer level pieces.
        var zs = map.ZoneSetChoices.Skip(1).FirstOrDefault(z => z.Bsps is { Count: 1 });
        if (zs != null)
        {
            var old = map.Bsp;
            map.SelectedZoneSet = zs;
            var zUntil = DateTime.UtcNow.AddMinutes(6);
            while (ReferenceEquals(map.Bsp, old) && DateTime.UtcNow < zUntil) await Task.Delay(500);
            Check(map.Bsp != null && map.Bsp.TriangleCount < whole, $"zone set {zs.Name}: {map.Bsp?.TriangleCount:N0} triangles (whole mission {whole:N0})");
            map.FrameAllCommand.Execute(null);
            await Pause(1500);
            Shot("campaign-zoneset");
            map.SelectedZoneSet = map.ZoneSetChoices[0];
        }
        // Its own mission script.
        var own = map.MissionScripts.FirstOrDefault(s => s.IsOwn && s.Name == "m10") ?? map.MissionScripts.FirstOrDefault(s => s.IsOwn);
        if (Check(own != null, "the map has its own mission scripts"))
        {
            await map.OpenMissionScriptCommand.ExecuteAsync(own);
            var script = _vm.Tabs.OfType<MissionScriptViewModel>().FirstOrDefault();
            if (Check(script != null && script.Text.Length > 1000, $"mission script opened: {script?.Title}, {script?.Status}"))
            {
                Check(script!.Declarations.Count > 10, $"scripts and globals listed ({script.Declarations.Count}): {string.Join(", ", script.Declarations.Take(5).Select(d => d.Name))}…");
                Check(script.CheckOk, "brackets and strings OK: " + script.Summary);
                Check(!script.IsShared, "it is this map's own copy (no shared warning)");
                await Pause(800);
                Shot("campaign-script");
                string text = script.Text;
                script.Text = script.IsKaloScript ? text + "\nstartup kalo_tour_broken:\n    print(\"x\"\n" : text + "\r\n(script startup kalo_tour_broken\r\n    (print \"x\")\r\n";
                script.Check();
                Check(!script.CheckOk && script.Messages.Any(m => m.Message.Contains("never closed") || m.Message.Contains("expected , or )")), "an unclosed ( is reported: " + script.Summary);
                script.Text = text;
                script.Check();
                script.IsDirty = false;
            }
        }
        // The Play bar for a campaign mission.
        _vm.SelectedScenario = map.Scenario;
        Check(_vm.PlayIsCampaign && _vm.PlaySummary.Contains("campaign mission"), "Play shows campaign options: " + _vm.PlaySummary);
        Check(_vm.InsertionChoices.Count > 1 && _vm.SelectedDifficulty != null, $"difficulty {_vm.SelectedDifficulty?.Label}, {_vm.InsertionChoices.Count} insertion points");
        Shot("campaign-playbar");
        // New map: campaign missions to copy.
        _vm.NewMapKind = "campaign";
        Check(_vm.BaseLevels.Any(l => l.EndsWith(@"\m10", StringComparison.OrdinalIgnoreCase)) && _vm.NewMapBase?.StartsWith(@"levels\solo\") == true,
            $"New map › Campaign mission lists the missions ({_vm.BaseLevels.Count()}), picked {_vm.NewMapBase}");
        _vm.NewMapKind = "barebone";
        // Play here on the mission (the test map is saved first, as Play does): scripts off, the zone set around the
        // camera picked from the bsps' bounds. The runner takes the game screenshots.
        foreach (var t in _vm.Tabs.OfType<MissionScriptViewModel>().ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
        if (map.Spawn is { } spawn)
        {
            map.SelectedZoneSet = map.ZoneSetChoices[0];
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _vm.Game!.Loaded += () => loaded.TrySetResult();
            var clock = Stopwatch.StartNew();
            await _vm.PlayFromAsync(map, spawn.Position + new Vector3(0, 0, 0.5f), spawn.Facing);
            foreach (string l in _vm.OutputLines.Where(l => l.Contains("Play from camera", StringComparison.Ordinal)).TakeLast(1)) Say(l.Length > 400 ? l[..400] : l);
            Check(_vm.IsGameRunning, "Play here started the game");
            var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(5)));
            Check(done == loaded.Task, $"the mission loaded ({clock.Elapsed.TotalSeconds:F0} s)");
            Check(_vm.OutputLines.Any(l => l.Contains("game_initial_zone_set", StringComparison.Ordinal)) || _vm.OutputLines.Any(l => l.Contains(", zone set ", StringComparison.Ordinal)),
                "a zone set around the camera was picked");
            Check(!_vm.OutputLines.TakeLast(400).Any(l => l.Contains("hs: m10 go!", StringComparison.Ordinal)), "the mission scripts did not run (no intro)");
            Say($"holding the game for {_hold:F0} s");
            await Task.Delay(TimeSpan.FromSeconds(_hold));
            _vm.StopGameCommand.Execute(null);
            await Pause(3000);
        }
        foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
    }

    /// <summary>
    /// Campaign squads on the m10 copy (cprobe): the object list's AI squads tree, the squad editor in the inspector
    /// (a templated squad: barn_skirm_init, a Skirmisher), a spawn point's own character set to an Elite and read back,
    /// "All squad fields" in a tab showing that squad only, the tag editor search on the whole scenario. With
    /// the section squads-play, the Elite override is played (Play here next to the point, ai_place barn_skirm_init) and then reset.
    /// </summary>
    private async Task Squads()
    {
        const string probe = @"levels\mods\_kalo_tour\cprobe\cprobe.scenario";
        if (!Check(File.Exists(System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, probe)), "the campaign probe map exists (kalo-cli campaign-probe)")) return;
        var map = await _vm.OpenMapAsync(probe);
        var root = map.OutlinerRoots.FirstOrDefault(n => n.Key == "c:squads");
        if (!Check(root != null, $"AI squads in the object list ({map.Squads.Count} squads, {map.SquadGroups.Count} groups)")) return;
        IEnumerable<OutlinerNode> All(OutlinerNode n) => new[] { n }.Concat(n.Children.SelectMany(All));
        var squadNode = All(root!).FirstOrDefault(n => n.Key.StartsWith("s:") && n.Name == "barn_skirm_init");
        if (!Check(squadNode != null && squadNode.Children.Count > 0, "squad barn_skirm_init with its spawn points")) return;
        var chain = new List<string>();
        bool Find(OutlinerNode n)
        {
            if (n == squadNode) return true;
            foreach (var c in n.Children) if (Find(c)) { chain.Insert(0, n.Name); return true; }
            return false;
        }
        Find(root!);
        Check(chain.Count >= 2, "under its squad groups: " + string.Join(" › ", chain));
        Check(!map.OutlinerRoots.Any(n => n.Key == "c:" + CampaignCategories.AiSpawnPoints), "no flat list of spawn points any more");

        // The squad (its node selects its spawn points).
        map.SelectFromOutliner(new[] { squadNode! });
        async Task<SquadEditorViewModel?> WaitSquad(Func<SquadEditorViewModel, bool> ok)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until) { if (map.SquadEditor is { } e && ok(e)) return e; await Task.Delay(200); }
            return map.SquadEditor;
        }
        var sq = await WaitSquad(e => e.Name == "barn_skirm_init");
        if (!Check(sq is { Name: "barn_skirm_init" }, "the inspector shows the squad: " + sq?.About)) return;
        Check(sq!.IsTemplated && sq.HasTemplateNote, "templated squad noted: " + sq.TemplateNote);
        var cell = sq.Cells.FirstOrDefault();
        string Pick(SquadChoiceRow r) => r.SelectedIndex >= 0 && r.SelectedIndex < r.Options.Count ? r.Options[r.SelectedIndex] : "?";
        Check(cell != null && cell.Characters.Count > 0 && Pick(cell.Characters[0]) == "skirmisher",
            $"cell {cell?.Name}: {string.Join(", ", cell?.Characters.Select(Pick) ?? Enumerable.Empty<string>())} with {string.Join(", ", cell?.Weapons.Select(Pick) ?? Enumerable.Empty<string>())}, count {cell?.Count}");
        await Pause(800);
        Shot("squads-squad");

        // One spawn point: its own character.
        var pt = squadNode!.Children[0].Item!;
        map.SetSelection(new[] { pt });
        map.RevealInOutliner(pt);
        sq = await WaitSquad(e => e.HasPoint);
        if (!Check(sq?.Point != null, "the selected spawn point's own character and weapon")) return;
        Check(sq!.Point!.CharacterIndex == 0 && sq.Point.CharacterOptions[0] == "From its cell", "character: " + sq.Point.CharacterOptions[sq.Point.CharacterIndex]);
        await Pause(1500);
        Check(!map.Properties.Any(p => p.Label.Contains("haracter type", StringComparison.OrdinalIgnoreCase)), "the raw character type number is no longer in Properties");
        int elite = sq.Characters.ToList().IndexOf("elite");
        Check(elite >= 0, $"the palette has an Elite (#{elite}) among {sq.Characters.Count} characters");
        var clock = Stopwatch.StartNew();
        bool viaCell = AppArgs.Current.TourSections?.Contains("squads-cell", StringComparer.OrdinalIgnoreCase) == true;
        bool viaDetach = AppArgs.Current.TourSections?.Contains("squads-detach", StringComparer.OrdinalIgnoreCase) == true;
        string scenarioFile = System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, probe);
        if (viaDetach)
        {
            // Variant: Use its own cells (detach from the template), then the cell's character. The test map is put back
            // from a copy at the end.
            File.Copy(scenarioFile, scenarioFile + ".kalo_tour_bak", true);
            await map.DetachSquadCommand.ExecuteAsync(null);
            map.SetSelection(new[] { pt });
            sq = await WaitSquad(e => e.HasPoint && !e.IsTemplated);
            Check(sq is { IsTemplated: false, Cells.Count: > 0 }, "detached: the squad has its own cells");
            viaCell = true;
        }
        if (viaCell)
        {
            // Variant: the templated cell's character instead of the spawn point's own.
            sq.Cells[0].Characters[0].SelectedIndex = elite;
            await Pause(2500);
            var c = await _vm.Host!.SquadAsync(map.Path, sq.Index);
            Check(c.Cells[0].Characters[0].Palette == elite, "cell character set to the Elite");
            map.SetSelection(new[] { pt });
            sq = await WaitSquad(e => e.HasPoint && e.Cells.Count > 0 && e.Cells[0].Characters[0].SelectedIndex == elite);
        }
        else sq.Point.CharacterIndex = elite + 1;
        int squadIndex = sq.Index;
        SquadDetail? back = null;
        while (clock.Elapsed.TotalSeconds < 30)
        {
            await Pause(300);
            back = await _vm.Host!.SquadAsync(map.Path, squadIndex);
            if (viaCell || back.SpawnPoints[pt.BlockIndex].Character == elite) break;
        }
        Say($"set took {clock.ElapsedMilliseconds} ms");
        if (!viaCell) Check(back?.SpawnPoints[pt.BlockIndex].Character == elite && map.IsDirty, "spawn point character set to the Elite (read back from the scenario)");
        Check(sq.Error == null, "no error: " + sq.Error);
        await Pause(800);
        Shot("squads-point");

        // All squad fields: a tab with that squad only.
        clock.Restart();
        await map.OpenSquadFieldsCommand.ExecuteAsync(null);
        var tab = _vm.ActiveDocument as TagEditorViewModel;
        Say($"squad fields opened in {clock.ElapsedMilliseconds} ms");
        if (Check(tab is { IsScoped: true } && tab.Fields.Count > 0, $"squad fields tab: {tab?.Title} · {tab?.Status}"))
        {
            if (!viaDetach) Check(tab!.AllFields().Any(f => f.RawName == "template" && f.Value == "sq_camp_skirmisher_nr_1"), "it shows the squad's own fields (template sq_camp_skirmisher_nr_1)");
            Check(tab.AllFields().Count() < 3000, $"{tab.AllFields().Count()} rows, not the whole scenario");
            await Pause(1200);
            Say($"root rows visible {tab.Fields.Count(f => f.IsVisible)}/{tab.Fields.Count}: {string.Join(", ", tab.Fields.Take(12).Select(f => f.Label))}");
            Shot("squads-fields");
            tab.Filter = "character";
            await Pause(2000);
            Say("filter: " + tab.Status + $"; root rows visible {tab.Fields.Count(f => f.IsVisible)}: {string.Join(", ", tab.Fields.Where(f => f.IsVisible).Select(f => f.Label + (f.IsExpanded ? "+" : "")))}");
            if (!viaDetach) Check(tab.Fields.Any(f => f.IsVisible && f.RawName == "templated"), "the templated cells show (character matches under them)");
            Shot("squads-fields-filter");
            await _vm.CloseTabCommand.ExecuteAsync(tab);
        }
        // Search in the whole scenario's tag editor.
        var full = await _vm.OpenTagAsync(probe);
        if (full != null)
        {
            clock.Restart();
            full.Filter = "a";
            await Pause(1500);
            Say($"whole scenario, filter a: {full.Status} ({clock.ElapsedMilliseconds} ms incl. 1.5 s pause)");
            Check(full.Status.Contains("More than"), "a search matching thousands of fields does not open them all");
            full.Filter = "chapter title";
            await Pause(2000);
            Say("filter chapter title: " + full.Status);
            Shot("squads-search");
            full.IsDirty = false;
            await _vm.CloseTabCommand.ExecuteAsync(full);
        }

        bool play = AppArgs.Current.TourSections?.Contains("squads-play", StringComparer.OrdinalIgnoreCase) == true;
        if (play)
        {
            // In game: the squad placed next to the player, as an Elite.
            await _vm.SaveActiveCommand.ExecuteAsync(null);
            foreach (var t in _vm.Tabs.Where(t => t.IsDirty).ToList()) await t.SaveDocumentAsync();
            double yaw = pt.Yaw * Math.PI / 180;
            // Two units behind the point (it may be on a roof: in front of it the player could fall), looking the same way.
            var at = pt.Position - new Vector3((float)Math.Cos(yaw) * 2, (float)Math.Sin(yaw) * 2, -0.3f);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _vm.Game!.Loaded += () => loaded.TrySetResult();
            await _vm.PlayFromAsync(map, at, pt.Yaw);
            Check(_vm.IsGameRunning, "Play here started the game");
            var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(5)));
            Check(done == loaded.Task, "the mission loaded");
            await Task.Delay(8000);
            // The squad, placed by hand (mission scripts are off in Play here); the AI ignores the player.
            bool ran = await _vm.Game.RunConsoleAsync(new[] { "ai_place barn_skirm_init", "ai_disregard (players) true" }, TimeSpan.FromSeconds(20));
            Check(ran, "console: ai_place barn_skirm_init");
            await Task.Delay(2000);
            // Elites have energy shields, Skirmishers none.
            await _vm.Game.RunConsoleAsync(new[]
            {
                "(if (> (ai_living_count barn_skirm_init) 0) (print \"kalo probe: squad alive\") (print \"kalo probe: squad not alive\"))",
                "(if (> (object_get_shield (ai_get_object barn_skirm_init)) 0) (print \"kalo probe: shielded (elite)\") (print \"kalo probe: no shield\"))",
            }, TimeSpan.FromSeconds(20));
            await Task.Delay(3000);
            foreach (string l in _vm.OutputLines.Where(l => l.Contains("kalo probe", StringComparison.Ordinal)).TakeLast(4)) Say("game: " + l);
            Say($"holding the game for {_hold:F0} s");
            await Task.Delay(TimeSpan.FromSeconds(_hold));
            _vm.StopGameCommand.Execute(null);
            await Pause(3000);
        }
        // Back to the cell's character (the test map stays as it was).
        map.SetSelection(new[] { pt });
        sq = await WaitSquad(e => e.HasPoint);
        if (sq?.Point != null) sq.Point.CharacterIndex = 0;
        await Pause(2500);
        if (viaCell)
        {
            sq = await WaitSquad(e => e.HasPoint);
            if (sq is { Cells.Count: > 0 }) sq.Cells[0].Characters[0].SelectedIndex = 10;
            await Pause(2500);
            var c = await _vm.Host!.SquadAsync(map.Path, squadIndex);
            Check(c.Cells[0].Characters[0].Palette == 10, "cell character back to the Skirmisher");
        }
        back = await _vm.Host!.SquadAsync(map.Path, squadIndex);
        Check(back.SpawnPoints[pt.BlockIndex].Character == -1, "reset to From its cell");
        if (play && !viaDetach) await _vm.SaveActiveCommand.ExecuteAsync(null);
        foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
        if (viaDetach && File.Exists(scenarioFile + ".kalo_tour_bak"))
        {
            await Task.Delay(1000);
            File.Copy(scenarioFile + ".kalo_tour_bak", scenarioFile, true);
            File.Delete(scenarioFile + ".kalo_tour_bak");
            Say("the test map is back as it was (templated squad)");
        }
    }

    /// <summary>
    /// Help › Recipes › Tutorial: make a small mission, done by the tour on m10 (levels\mods\_kalo_tour\tutorial): empty
    /// mission, Add › Squad at the barn, Add › Volume renamed tv_start_fight, the first start moved into it, the
    /// tutorial's script. The runner then plays it (--play-map) and looks for the script's prints and the Elite.
    /// </summary>
    /// <summary>Level pieces on the tutorial map (run the tutorial section first): find, hide a building, show it again.</summary>
    private async Task LevelPiecesTest()
    {
        const string path = @"levels\mods\_kalo_tour\tutorial\tutorial.scenario";
        _vm.RememberScenarioType(path[..^".scenario".Length], "solo");
        var map = await _vm.OpenMapAsync(path);
        _vm.ActiveDocument = map;
        map.SelectedZoneSet = map.ZoneSetChoices.FirstOrDefault(z => z.Bsps != null && z.Bsps.Contains(2)) ?? map.SelectedZoneSet;
        var until = DateTime.UtcNow.AddMinutes(4);
        while (map.Bsp == null && DateTime.UtcNow < until) await Task.Delay(500);
        // In front of the barn's garage door (m10_030 instance 147), looking at it.
        Vector3 door = new(138.94f, 58.89f, -1.13f), facing = Vector3.Normalize(new Vector3(0.965f, -0.262f, 0));
        map.LookFrom(door + facing * 7 + new Vector3(0, 0, 1.5f), MathF.Atan2(-facing.Y, -facing.X) * 180 / MathF.PI, -5);
        await Pause(2000);
        var sw = Stopwatch.StartNew();
        await map.FindLevelPiecesCommand.ExecuteAsync(null);
        Say($"Here: {map.LevelPieces.Count} rows in {sw.ElapsedMilliseconds} ms: {map.LevelPiecesTitle}");
        foreach (var row in map.LevelPieces.Take(6)) Say($"  {row.Name} · {row.Detail} · {row.GroupText}");
        if (!Check(map.LevelPieces.Count > 0 && map.ShowLevelPieces, "pieces where the view is aimed")) return;
        Shot("level-pieces-here");

        var target = map.LevelPieces.FirstOrDefault(r => r.HasGroup && r.Piece.GroupCount < 3000) ?? map.LevelPieces[0];
        int version = map.GeometryVersion;
        sw.Restart();
        if (target.HasGroup)
        {
            await map.ToggleGroupCommand.ExecuteAsync(target);
            Say($"hide group {target.Piece.Group}: {sw.ElapsedMilliseconds} ms; {map.Status}");
            Check(target.Hidden && map.IsDirty, "the building is hidden (the map points at its own copy: unsaved)");
            Check(File.Exists(System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, @"levels\mods\_kalo_tour\tutorial\m10_030.scenario_structure_bsp")) || target.Piece.Bsp != 2,
                "the map has its own copy of the bsp");
            await map.HiddenLevelPiecesCommand.ExecuteAsync(null);
            Check(map.LevelPieces.Count == 1 && map.LevelPieces[0].IsWholeGroup && map.LevelPieces[0].Hidden, $"Hidden lists the building as one row: {map.LevelPieces.FirstOrDefault()?.Name} · {map.LevelPieces.FirstOrDefault()?.Detail}");
            var geo = DateTime.UtcNow.AddMinutes(4);
            while (map.GeometryVersion == version && DateTime.UtcNow < geo) await Task.Delay(500);
            Check(map.GeometryVersion != version, $"the 3D view read the level again ({sw.ElapsedMilliseconds} ms after Hide): {map.GeometryStatus}");
            await Pause(1500);
            Shot("level-pieces-hidden");
            sw.Restart();
            await map.ShowAllPiecesCommand.ExecuteAsync(null);
            Say($"show all: {sw.ElapsedMilliseconds} ms; {map.Status}");
            await map.HiddenLevelPiecesCommand.ExecuteAsync(null);
            Check(map.LevelPieces.Count == 0, "Show them all: nothing hidden any more");
        }

        await map.FindLevelPiecesCommand.ExecuteAsync(null);
        var one = map.LevelPieces.First();
        await map.TogglePieceCommand.ExecuteAsync(one);
        Check(one.Hidden && one.ToggleText == "Show", "one piece hidden: " + one.Name);
        await map.HiddenLevelPiecesCommand.ExecuteAsync(null);
        Check(map.LevelPieces.Count == 1, "Hidden lists it");
        await map.TogglePieceCommand.ExecuteAsync(map.LevelPieces[0]);
        await map.HiddenLevelPiecesCommand.ExecuteAsync(null);
        Check(map.LevelPieces.Count == 0, "Show: nothing hidden any more");
        foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
    }

    private async Task TutorialMission()
    {
        const string path = @"levels\mods\_kalo_tour\tutorial\tutorial.scenario";
        string folder = System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, @"levels\mods\_kalo_tour\tutorial");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        var r = await _vm.Host!.CreateMapAsync(new CreateMapParams { Template = @"levels\solo\m10\m10.scenario", NewPath = path, Empty = true, MapId = 7992 });
        Check(r.Type == "solo", "step 1: empty campaign mission on m10");
        _vm.RememberScenarioType(path[..^".scenario".Length], "solo");
        var map = await _vm.OpenMapAsync(path);
        _vm.ActiveDocument = map;
        map.SelectedZoneSet = map.ZoneSetChoices.FirstOrDefault(z => z.Bsps != null && z.Bsps.Contains(2)) ?? map.SelectedZoneSet;
        Check(map.SelectedZoneSet?.Bsps?.Contains(2) == true, "step 2: a zone set with bsp 2: " + map.SelectedZoneSet?.Label);
        var until = DateTime.UtcNow.AddMinutes(4);
        while ((map.Bsp == null) && DateTime.UtcNow < until) await Task.Delay(500);
        map.LookFrom(new Vector3(134.03f, 35.20f, 3.0f), 0, -60);
        await Pause(2000);
        await map.AddSquadCommand.ExecuteAsync(null);
        Check(map.Squads.Count == 1 && map.Squads[0].Name == "squad", "step 3: Add › Squad made squad");
        map.LookFrom(new Vector3(146f, 26f, 3.0f), 0, -60);
        await Pause(1500);
        await map.AddMarkerCommand.ExecuteAsync(CampaignCategories.TriggerVolumes);
        var volume = map.Selected;
        if (!Check(volume is { IsVolume: true }, "step 4: Add › Volume")) return;
        await map.RenameAsync(volume!, "tv_start_fight");
        volume = map.Items.FirstOrDefault(i => i.IsVolume && i.PlacedName == "tv_start_fight");
        Check(volume != null, "renamed tv_start_fight");
        var start = map.Items.FirstOrDefault(i => i.Category == StartLocations.Category);
        if (!Check(start != null, "step 5: the start locations are in the object list")) return;
        map.SetSelection(new[] { start! });
        map.MoveLocal(start!, volume!.Position - new Vector3(0, 0, 1.2f)); // the box stands on the ground; its middle is 1.5 up
        await map.CommitMoveAsync(start!);
        Check(Vector3.Distance(map.Items.First(i => i.Category == StartLocations.Category).Position, volume.Position) < 1.5f, "the first start is in the volume");
        await map.SaveCommand.ExecuteAsync(null);
        var own = map.MissionScripts.First(m => m.IsOwn);
        var doc = await _vm.OpenMissionScriptAsync(own.Path, map.Path);
        var recipe = Kalo.Core.Scripting.Docs.KaloRecipes.All().First(x => x.Id == "recipe-small-mission");
        string code = recipe.Blocks.First(b => b.Kind == Kalo.Core.Scripting.Docs.GuideKind.Code).Text;
        doc.Text = code.Replace("\r\n", "\n") + "\n";
        doc.Check();
        Check(doc.CheckOk && doc.Messages.Count == 0, "step 6: the tutorial's script checks clean: " + doc.Summary);
        // For the runner: a print once the fight starts (the player stands in the volume at the start).
        doc.Text = doc.Text.Replace("    ai_place(squad)\n", "    ai_place(squad)\n    print(\"fight started\")\n");
        await doc.SaveDocumentAsync();
        var back = await _vm.Host.ReadMissionScriptAsync(own.Path);
        Check(back.Text?.Contains("(game_won)") == true && back.Text.Contains("(volume_test_players tv_start_fight)"), "saved as HaloScript");
        Shot("tutorial-map");
        foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
    }

    /// <summary>Runs <paramref name="body"/> in a throwaway mod (Kalo tour mod); the mod is deleted and the previous one restored.</summary>
    private async Task InTourModAsync(Func<Task> body)
    {
        var projects = _vm.ModProjects!;
        string marker = System.IO.Path.Combine(_vm.Paths!.KaloData, "tour-previous-project.txt");
        // An interrupted run left the tour mod active and the user's mod aside: the marker names it.
        string? interrupted = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        foreach (var old in projects.List().Where(x => x.Name == TourMod).ToList())
        {
            if (projects.ActiveName == old.Name) await _vm.SwitchProjectCommand.ExecuteAsync(null);
            projects.Delete(old);
        }
        _vm.RefreshProjects();
        if (projects.ActiveName == null && !string.IsNullOrEmpty(interrupted) && projects.Find(interrupted) != null)
            await _vm.SwitchProjectCommand.ExecuteAsync(_vm.Projects.FirstOrDefault(x => x.Name == interrupted));
        string? previous = projects.ActiveName;
        File.WriteAllText(marker, previous ?? "");
        _vm.NewModName = TourMod;
        await _vm.CreateModCommand.ExecuteAsync(null);
        try { await body(); }
        finally
        {
            if (_vm.Game?.IsRunning == true) _vm.Game.Stop();
            foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
            await _vm.SwitchProjectCommand.ExecuteAsync(null);
            var p = projects.Find(TourMod);
            if (p != null) projects.Delete(p);
            _vm.RefreshProjects();
            if (previous != null)
            {
                await _vm.SwitchProjectCommand.ExecuteAsync(_vm.Projects.FirstOrDefault(x => x.Name == previous));
                Check(projects.ActiveName == previous, $"'{previous}' is active again");
            }
            try { File.Delete(marker); } catch { }
        }
    }

    /// <summary>
    /// New weapon from this one: the magnum with an unsaved damage of 50 becomes hand_cannon with its own bullet and damage
    /// files; the magnum keeps 17.5. Then Add to map palette on a campaign mission: a new squad's weapon list offers it.
    /// </summary>
    private Task NewWeapon() => InTourModAsync(async () =>
    {
        const string magnum = @"objects\weapons\pistol\magnum\magnum.weapon";
        const string magnumDamage = @"objects\weapons\pistol\magnum\projectiles\magnum_bullet.damage_effect";
        var tab = await _vm.OpenTagAsync(magnum);
        for (int i = 0; i < 50 && tab?.Shot == null; i++) await Task.Delay(100);
        if (!Check(tab?.Shot != null && tab.IsWeapon, "the magnum shows Each shot and New weapon from this")) return;
        tab!.Shot!.Damage = "50";
        await Task.Delay(500);
        string? error = await _vm.NewWeaponFromAsync(tab, "Hand Cannon");
        if (!Check(error == null, "New weapon from this: hand_cannon" + (error != null ? ": " + error : ""))) return;
        const string folder = @"objects\mods\kalo_tour_mod\hand_cannon";
        string Tags(string rel) => System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, rel);
        Check(File.Exists(Tags(folder + @"\hand_cannon.weapon")) && File.Exists(Tags(folder + @"\hand_cannon_bullet.projectile"))
              && Directory.GetFiles(Tags(folder), "*.damage_effect").Length >= 1, "the weapon, its bullet and its damage file are in the mod: " + string.Join(", ", Directory.GetFiles(Tags(folder)).Select(System.IO.Path.GetFileName)));
        var copy = _vm.Tabs.OfType<TagEditorViewModel>().FirstOrDefault(t => t.Path.EndsWith("hand_cannon.weapon"));
        for (int i = 0; i < 50 && copy?.Shot == null; i++) await Task.Delay(100);
        Check(copy?.Shot?.Projectile.EndsWith("hand_cannon_bullet.projectile") == true && copy.Shot.Damage == "50",
            $"the new weapon fires its own bullet with the edited damage ({copy?.Shot?.Files}, {copy?.Shot?.Damage})");
        var orig = await _vm.Host!.OpenTagAsync(magnumDamage);
        Check(TagEditorViewModel.Find(orig.Fields, "damage lower bound")?.Value == "17.5", "the magnum's own damage file still says 17.5");
        await _vm.Host.CloseTagAsync(magnumDamage);
        Shot("new-weapon");

        // Look › Paint: the mod's own weapon is painted in place, world and first person models.
        await _vm.EnsureMaterialsAsync();
        var material = _vm.Materials.FirstOrDefault(m => m.Name.Contains("metal", StringComparison.OrdinalIgnoreCase)) ?? _vm.Materials.First();
        Check(copy!.IsModObject && copy.IsPlaceable, "the new weapon offers Look (painted in place: it is the mod's own)");
        await copy.PaintWithCommand.ExecuteAsync(material);
        Say("look: " + copy.LookStatus);
        copy = _vm.Tabs.OfType<TagEditorViewModel>().First(t => t.Path.EndsWith("hand_cannon.weapon"));
        string Ref(TagEditorViewModel t, string raw, int nth = 0) => t.AllFields().Where(f => f.IsReference && f.RawName == raw).Skip(nth).FirstOrDefault()?.Value ?? "";
        Check(Ref(copy, "model").StartsWith(folder + @"\hand_cannon.model"), "its model is now the painted copy: " + Ref(copy, "model"));
        Check(Ref(copy, "first person model").StartsWith(folder) && Ref(copy, "first person model", 1).StartsWith(folder),
            "what the player holds is painted too (first person models): " + Ref(copy, "first person model"));
        Check(File.Exists(Tags(folder + @"\hand_cannon.render_model")), "the painted render model is in the mod");
        await Pause(400);
        Shot("painted-weapon");

        // Look › Looks like: the DMR's models (unsaved, then saved).
        copy.ToggleLooksLikeCommand.Execute(null);
        copy.LooksLikeQuery = "dmr weapon";
        string? dmr = copy.LooksLikeResults.FirstOrDefault(r => r.EndsWith(@"\dmr.weapon"));
        Check(dmr != null, "Looks like lists the DMR: " + string.Join(", ", copy.LooksLikeResults.Take(4)));
        if (dmr != null)
        {
            await copy.LooksLikeCommand.ExecuteAsync(dmr);
            Say("look: " + copy.LookStatus);
            Check(Ref(copy, "model").Contains(@"\dmr\") && Ref(copy, "first person model").Contains(@"\dmr\") && copy.IsDirty, "it takes the DMR's model and first person models: " + Ref(copy, "model"));
            await copy.SaveCommand.ExecuteAsync(null);
        }

        // Paint a game weapon: a painted copy goes into the mod, the game's file stays.
        var ar = await _vm.OpenTagAsync(@"objects\weapons\rifle\assault_rifle\assault_rifle.weapon");
        Check(ar != null && !ar.IsModObject, "the assault rifle is a game file");
        await ar!.PaintWithCommand.ExecuteAsync(material);
        var painted = _vm.Tabs.OfType<TagEditorViewModel>().FirstOrDefault(t => t.Path.Contains(@"\mods\kalo_tour_mod\assault_rifle_"));
        Check(painted != null && Ref(painted, "model").Contains(@"\mods\kalo_tour_mod\"), "painting it made a painted copy in the mod: " + painted?.Path);
        var arAgain = await _vm.Host.OpenTagAsync(@"objects\weapons\rifle\assault_rifle\assault_rifle.weapon");
        Check(TagEditorViewModel.Find(arAgain.Fields, "object/model")?.Value?.Contains(@"\mods\") != true, "the game's assault rifle keeps its own model");

        // Campaign: the tour's empty m10 mission (volumes-play makes it) gets a squad, then the weapon in its palette.
        const string mission = @"levels\mods\_kalo_tour\volumes\volumes.scenario";
        if (!File.Exists(Tags(mission))) { Say("no tour mission (run volumes-play first): campaign palette part skipped"); return; }
        _vm.RememberScenarioType(mission[..^".scenario".Length], "solo");
        var map = await _vm.OpenMapAsync(mission);
        var until = DateTime.UtcNow.AddMinutes(4);
        while (map.Bsp == null && DateTime.UtcNow < until) await Task.Delay(500);
        await map.AddSquadCommand.ExecuteAsync(null);
        _vm.ActiveDocument = copy;
        await copy!.AddToMapPaletteCommand.ExecuteAsync(null);
        Say("use: " + copy.UseStatus);
        var squad = await _vm.Host.SquadAsync(map.Path, map.Squads.Count - 1);
        Check(squad.Weapons?.Any(w => w.EndsWith("hand_cannon.weapon")) == true, "the mission's squads can now pick hand_cannon: " + string.Join(", ", (squad.Weapons ?? new()).Select(System.IO.Path.GetFileNameWithoutExtension)));
        map.IsDirty = false;
    });

    /// <summary>
    /// A new, painted weapon in game: hand_cannon (from the magnum, painted) placed by Use it › Place in map in an empty m10
    /// mission (a stock magnum next to it as the control), named kalo_hc; the game is asked whether both exist.
    /// </summary>
    private Task WeaponPlay() => InTourModAsync(async () =>
    {
        // A campaign mission: placed objects exist there by name (on multiplayer maps the game respawns weapons itself, nameless).
        var map = await _vm.CreateMapAsync("Weapon Test", @"levels\solo\m10\m10", empty: true);
        if (!Check(map != null && map.IsCampaign, "created an empty campaign mission on m10")) return;
        map!.SelectedZoneSet = map.ZoneSetChoices.FirstOrDefault(z => z.Bsps != null && z.Bsps.Contains(2)) ?? map.SelectedZoneSet;
        var until = DateTime.UtcNow.AddMinutes(4);
        while (map.Bsp == null && DateTime.UtcNow < until) await Task.Delay(500);
        var start = map.Items.First(i => i.Category == StartLocations.Category);
        var tab = await _vm.OpenTagAsync(@"objects\weapons\pistol\magnum\magnum.weapon");
        if (!Check(await _vm.NewWeaponFromAsync(tab!, "Hand Cannon") == null, "made hand_cannon")) return;
        var hc = _vm.Tabs.OfType<TagEditorViewModel>().First(t => t.Path.EndsWith("hand_cannon.weapon"));
        await _vm.EnsureMaterialsAsync();
        await hc.PaintWithCommand.ExecuteAsync(_vm.Materials.FirstOrDefault(m => m.Name.Contains("metal", StringComparison.OrdinalIgnoreCase)) ?? _vm.Materials.First());
        hc = _vm.Tabs.OfType<TagEditorViewModel>().First(t => t.Path.EndsWith("hand_cannon.weapon"));
        // Place it at the first start location: Use it › Place in map puts it in front of the map's camera.
        map.LookFrom(start.Position + new Vector3(-2, 0, 1.6f), 0, -35);
        await Pause(1500);
        await hc.PlaceInMapCommand.ExecuteAsync(null);
        Say("use: " + hc.UseStatus);
        var placed = map.Items.FirstOrDefault(i => i.Tag.EndsWith("hand_cannon.weapon"));
        if (!Check(placed != null, "Place in map put hand_cannon on the map")) return;
        await map.RenameAsync(placed!, "kalo_hc");
        await map.SaveCommand.ExecuteAsync(null);

        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _vm.Game!.Loaded += () => loaded.TrySetResult();
        var probes = new List<string>();
        _vm.Game.Output += line => { if (line.Contains("kalo probe:") && !line.Contains("(print")) lock (probes) probes.Add(line[line.IndexOf("kalo probe:")..]); };
        await _vm.PlayCommand.ExecuteAsync(null);
        var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6)));
        if (!Check(done == loaded.Task, "the game loaded the map with the painted weapon")) return;
        bool ran = false;
        for (int round = 1; round <= 10; round++)
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            // A name the game doesn't know makes the command fail; -1 means no player yet.
            ran = await _vm.Game.RunConsoleAsync(new[]
            {
                $"(if (< (objects_distance_to_object (players) kalo_hc) 0) (print \"kalo probe: r{round} no player\") (print \"kalo probe: r{round} measured\"))",
                $"(if (< (objects_distance_to_object (players) kalo_hc) 30) (print \"kalo probe: r{round} weapon near\") (print \"kalo probe: r{round} weapon far\"))",
                $"(if (> (list_count (players)) 0) (print \"kalo probe: r{round} has players\") (print \"kalo probe: r{round} no players\"))",
            }, TimeSpan.FromSeconds(10));
            lock (probes) if (probes.Any(p => p.Contains("measured"))) break;
        }
        for (int i = 0; i < 20; i++) { lock (probes) if (probes.Any(p => p.Contains("measured"))) break; await Task.Delay(1000); }
        lock (probes) foreach (var p in probes.Distinct()) Say(p);
        Check(ran, "the game knows the object kalo_hc (the probe ran)");
        lock (probes) Check(probes.Any(p => p.Contains("measured")), "the painted weapon exists in the running mission (the game measures the distance to it)");
        Check(_vm.IsGameRunning, "the game is still running (no crash)");
        _vm.StopGameCommand.Execute(null);
        await Pause(3000);
    });

    /// <summary>
    /// Campaign AI in game, on an empty m10 mission (levels\mods\_kalo_tour\aiplay): squad_walker patrols a 2-point path
    /// (Patrol panel), squad_rider's cell spawns in a ghost (Squad section › Vehicle, the ghost added with Use it › Add to map
    /// palette); a volume around the start spawns both. Flags at the path's points let the game measure where the patrol is.
    /// </summary>
    private async Task AiPlay()
    {
        const string path = @"levels\mods\_kalo_tour\aiplay\aiplay.scenario";
        string folder = System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, @"levels\mods\_kalo_tour\aiplay");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        var r = await _vm.Host!.CreateMapAsync(new CreateMapParams { Template = @"levels\solo\m10\m10.scenario", NewPath = path, Empty = true, MapId = 7994 });
        Check(r.Type == "solo", "empty campaign mission on m10");
        _vm.RememberScenarioType(path[..^".scenario".Length], "solo");
        var map = await _vm.OpenMapAsync(path);
        _vm.ActiveDocument = map;
        map.SelectedZoneSet = map.ZoneSetChoices.FirstOrDefault(z => z.Bsps != null && z.Bsps.Contains(2)) ?? map.SelectedZoneSet;
        var until = DateTime.UtcNow.AddMinutes(4);
        while (map.Bsp == null && DateTime.UtcNow < until) await Task.Delay(500);
        try
        {
            async Task Look(float x, float y) { map.LookFrom(new Vector3(x, y, 3.0f), 0, -60); await Pause(1200); }
            // The walker and its path: p0 where it spawns, p1 some 15 units away; a flag on each point to measure.
            await Look(134.03f, 35.20f);
            await map.AddSquadCommand.ExecuteAsync(null);
            var walkerPoint = map.Selected;
            await map.AddMarkerCommand.ExecuteAsync(CampaignCategories.CutsceneFlags);
            await map.RenameAsync(map.Selected!, "fl_p0");
            map.SetSelection(new[] { walkerPoint! });
            await Pause(500);
            string walker = map.PatrolSquad ?? "";
            Check(walker.Length > 0 && map.ShowPatrol, "selecting the squad's spawn point shows Patrol for " + walker);
            await map.NewPatrolPathCommand.ExecuteAsync(null);
            Say("patrol: " + map.PatrolStatus);
            await Look(140f, 29f);
            await map.AddPatrolPointCommand.ExecuteAsync(null);
            Say("patrol: " + map.PatrolStatus);
            string set = map.PatrolPath ?? "";
            var pts = map.Items.Where(i => i.Category == CampaignCategories.ScriptPoints && i.Label == "point set " + set).ToList();
            Check(set.StartsWith("ps_path") && pts.Count == 2 && pts.Select(p => p.PlacedName).SequenceEqual(new[] { "p0", "p1" }), $"New path here + Add point here: {set} with {string.Join(", ", pts.Select(p => p.PlacedName))}");
            await map.AddMarkerCommand.ExecuteAsync(CampaignCategories.CutsceneFlags);
            await map.RenameAsync(map.Selected!, "fl_p1");
            map.PatrolSquad = walker;
            map.PatrolPath = set;
            await map.AddPatrolCommand.ExecuteAsync(null);
            Say("patrol: " + map.PatrolStatus);

            // The rider: a squad whose cell spawns in a ghost.
            var ghost = await _vm.OpenTagAsync(@"objects\vehicles\covenant\ghost\ghost.vehicle");
            await ghost!.AddToMapPaletteCommand.ExecuteAsync(null);
            _vm.ActiveDocument = map;
            await Look(141f, 31f);
            await map.AddSquadCommand.ExecuteAsync(null);
            for (int i = 0; i < 50 && map.SquadEditor == null; i++) await Task.Delay(100);
            var cell = map.SquadEditor?.Cells.FirstOrDefault();
            string rider = map.SquadEditor?.Name ?? "";
            int ghostChoice = cell?.VehicleChoices.ToList().FindIndex(v => v == "ghost") ?? -1;
            Check(cell is { HasVehicleField: true } && ghostChoice > 0, $"the Squad section offers Vehicle: {string.Join(", ", cell?.VehicleChoices ?? new List<string>())}");
            if (cell != null && ghostChoice > 0) cell.Vehicle = ghostChoice;
            await Task.Delay(800);
            var detail = await _vm.Host.SquadAsync(map.Path, map.Squads.Count - 1);
            Check(detail.Cells?.FirstOrDefault()?.Vehicle == ghostChoice - 1, $"{rider}'s cell spawns in the ghost (vehicle {detail.Cells?.FirstOrDefault()?.Vehicle})");
            // Control: a squad on foot next to it.
            await Look(143f, 33f);
            await map.AddSquadCommand.ExecuteAsync(null);
            for (int i = 0; i < 50 && map.SquadEditor?.Name == rider; i++) await Task.Delay(100);
            string footer = map.SquadEditor?.Name ?? "";
            Say($"squads: walker {walker}, rider {rider}, control {footer}");

            // The volume around the start spawns both squads.
            await Look(146f, 26f);
            await map.AddMarkerCommand.ExecuteAsync(CampaignCategories.TriggerVolumes);
            var volume = map.Selected!;
            var start = map.Items.First(i => i.Category == StartLocations.Category);
            map.MoveLocal(start, volume.Position - new Vector3(0, 0, 1.2f));
            await map.CommitMoveAsync(start);
            map.SetSelection(new[] { map.Items.First(i => i.IsVolume && i.Index == volume.Index) });
            foreach (string squad in new[] { walker, rider, footer })
            {
                map.VolumeAction = map.VolumeActionChoices.First(c => c.Action == Kalo.Core.Scripting.Campaign.VolumeAction.SpawnSquad);
                map.VolumeParam = squad;
                await map.AddVolumeTriggerCommand.ExecuteAsync(null);
            }
            if (Environment.GetEnvironmentVariable("KALO_FORCE_BSP") is { Length: > 0 } forced)
                foreach (var sq in map.Squads.Where(s => s.Name == walker || s.Name == rider))
                    Say("force bsp: " + (await _vm.Host.SetFieldAsync(map.Path, $"Block:squads[{sq.Index}]/Block:spawn points[0]/ShortBlockIndex:structure bsp", forced)).Value);
            if (Environment.GetEnvironmentVariable("KALO_AI_ZONE") == "1")
                foreach (var sq in map.Squads.Where(s => s.Name == walker || s.Name == rider))
                    Say("zone: " + (await _vm.Host.SetFieldAsync(map.Path, $"Block:squads[{sq.Index}]/ShortBlockIndex:initial zone", "3")).Value);
            var doc = _vm.Tabs.OfType<MissionScriptViewModel>().FirstOrDefault();
            Check(doc != null && doc.Text.Contains($"ai_place({walker})") && doc.Text.Contains($"ai_place({rider})") && doc.Text.Contains($"cs_go_to({set}/p1)"),
                "the mission script places both squads and has the patrol");
            Check(doc?.CheckOk == true, "it checks clean: " + doc?.Summary);
            if (doc != null) await doc.SaveDocumentAsync();
            foreach (var t in _vm.Tabs.OfType<TagEditorViewModel>().ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
            await map.SaveCommand.ExecuteAsync(null);
            _vm.ActiveDocument = map;
            Shot("ai-map");

            string name = path[..^".scenario".Length];
            if (!_vm.Scenarios.Contains(name, StringComparer.OrdinalIgnoreCase)) _vm.Scenarios.Insert(0, name);
            _vm.SelectedScenario = _vm.Scenarios.First(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _vm.Game!.Loaded += () => loaded.TrySetResult();
            var probes = new List<string>();
            if (Environment.GetEnvironmentVariable("KALO_GAME_LOG") == "1")
                _vm.Game.Output += line => { if (System.Text.RegularExpressions.Regex.IsMatch(line, @"ai|ai:|ERROR|actor|pathfind|squad|cs_", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && !line.Contains("kalo probe")) Say("game: " + (line.Length > 220 ? line[..220] : line)); };
            _vm.Game.Output += line => { if (line.Contains("kalo probe:") && !line.Contains("(print")) lock (probes) probes.Add(line[line.IndexOf("kalo probe:")..]); };
            await _vm.PlayCommand.ExecuteAsync(null);
            if (!Check(await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6))) == loaded.Task, "the mission loaded")) return;
            Say($"play: insertion {_vm.SelectedInsertion?.Label}; view zone set {map.SelectedZoneSet?.Name} (bsps {string.Join(",", map.SelectedZoneSet?.Bsps ?? new List<int>())})");
            if (Environment.GetEnvironmentVariable("KALO_AI_ZS") == "1" && map.SelectedZoneSet is { Name.Length: > 0 } zs)
                Say("switch: " + await _vm.Game.RunConsoleAsync(new[] { "switch_zone_set " + zs.Name }, TimeSpan.FromSeconds(10)));
            // The AI ignore the player (so the patrol isn't aborted by an alert) and the player can't die.
            await _vm.Game.RunConsoleAsync(Environment.GetEnvironmentVariable("KALO_AI_SEE") == "1" ? new[] { "(set cheat_deathless_player true)" } : new[] { "(ai_disregard (players) true)", "(set cheat_deathless_player true)" }, TimeSpan.FromSeconds(10));
            // The squads are placed from the console too: the volume only spawns them if the player starts inside it, and where
            // the test puts the start depends on the camera ray (the volume script itself is checked above).
            await _vm.Game.RunConsoleAsync(new[] { $"(if (= (ai_living_count {walker}) 0) (ai_place {walker}))", $"(if (= (ai_living_count {rider}) 0) (ai_place {rider}))", "(ai_disregard (players) true)" }, TimeSpan.FromSeconds(10));
            for (int round = 1; round <= 16; round++)
            {
                await Task.Delay(TimeSpan.FromSeconds(4));
                // Experiment: once the rider is placed (round 4), order it into its vehicle; does it stay in?
                if (round == int.Parse(Environment.GetEnvironmentVariable("KALO_AI_CMDS_ROUND") ?? "4") && Environment.GetEnvironmentVariable("KALO_AI_CMDS") is { Length: > 0 } cmds)
                    foreach (string c in cmds.Split(";;").Select(x => x.Replace("{walker}", walker).Replace("{rider}", rider)))
                        Say($"cmd {(await _vm.Game.RunConsoleAsync(new[] { c }, TimeSpan.FromSeconds(10)) ? "ok" : "failed")}: {c}");
                if (round == 4 && Environment.GetEnvironmentVariable("KALO_AI_ENTER") == "1")
                    Say("enter: " + await _vm.Game.RunConsoleAsync(new[] { $"(ai_vehicle_enter_immediate {rider} (ai_vehicle_get_from_squad {rider} 0))" }, TimeSpan.FromSeconds(10)));
                await _vm.Game.RunConsoleAsync(new[]
                {
                    $"(if (< (objects_distance_to_flag (ai_actors {walker}) fl_p0) 0) (print \"kalo probe: r{round} walker missing\") (print \"kalo probe: r{round} walker there\"))",
                    $"(if (< (objects_distance_to_flag (ai_actors {walker}) fl_p0) 3) (print \"kalo probe: r{round} walker at p0\") (print \"kalo probe: r{round} walker not at p0\"))",
                    $"(if (< (objects_distance_to_flag (ai_actors {walker}) fl_p1) 3) (print \"kalo probe: r{round} walker at p1\") (print \"kalo probe: r{round} walker not at p1\"))",
                    $"(if (> (ai_in_vehicle_count {rider}) 0) (print \"kalo probe: r{round} rider in a vehicle\") (print \"kalo probe: r{round} rider on foot\"))",
                    $"(if (> (ai_living_count {rider}) 0) (print \"kalo probe: r{round} rider alive\") (print \"kalo probe: r{round} rider none\"))",
                    $"(if (> (ai_living_count {footer}) 0) (print \"kalo probe: r{round} control on foot alive\") (print \"kalo probe: r{round} control on foot none\"))",
                    $"(if (ai_enabled) (print \"kalo probe: r{round} ai enabled\") (print \"kalo probe: r{round} ai disabled\"))",
                    $"(if (cs_command_script_running {walker} {Kalo.Core.Scripting.Campaign.PatrolScripts.CommandScript(walker)}) (print \"kalo probe: r{round} patrol script running\") (print \"kalo probe: r{round} patrol script not running\"))",
                    $"(if (> (ai_combat_status {walker}) 2) (print \"kalo probe: r{round} walker alerted\") (print \"kalo probe: r{round} walker calm\"))",
                    $"(if (= (ai_combat_status {walker}) (ai_combat_status_asleep)) (print \"kalo probe: r{round} walker asleep\") (print \"kalo probe: r{round} walker not asleep\"))",
                    $"(if (= (ai_combat_status {walker}) (ai_combat_status_idle)) (print \"kalo probe: r{round} walker idle\") (print \"kalo probe: r{round} walker not idle\"))",
                    $"(if (> (ai_vehicle_get_squad_count {rider}) 0) (print \"kalo probe: r{round} rider squad has a vehicle\") (print \"kalo probe: r{round} rider squad has no vehicle\"))",
                }, TimeSpan.FromSeconds(10));
            }
            for (int i = 0; i < 20; i++) { lock (probes) if (probes.Any(p => p.Contains("r16"))) break; await Task.Delay(1000); }
            List<string> got;
            lock (probes) got = probes.Distinct().ToList();
            foreach (var p in got.Where(p => !p.Contains(" not at ") && !p.Contains("walker there"))) Say(p);
            bool Seen(string what) => got.Any(p => p.EndsWith(what));
            Check(Seen("walker there"), "the walker squad is in the game");
            Check(Seen("patrol script running"), "the patrol command script runs on the walker");
            Check(Seen("rider alive") && Seen("rider squad has a vehicle"), "the rider squad spawned with its ghost");
            // reach_tag_test plays from tags: Bungie's levels ship without pathfinding data (tool.exe generates it only when it
            // builds the .map), so AI stand still here: walking the path and boarding are for the built map (Play in MCC).
            Say($"movement (needs a built map): walker reached p1: {Seen("walker at p1")}, rider in its vehicle: {Seen("rider in a vehicle")}");
            _vm.StopGameCommand.Execute(null);
            await Pause(3000);
        }
        finally
        {
            if (_vm.Game?.IsRunning == true) _vm.Game.Stop();
            foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
        }
    }

    /// <summary>
    /// What unit object_attach's offsets use: a mover attached every tick at offset (10, 0, 0) to an anchor, then the
    /// game is asked how far apart they are (1: tenths of a world unit, ~3: feet, 10: world units).
    /// </summary>
    private Task AttachUnits() => InTourModAsync(async () =>
    {
        var map = await _vm.CreateMapAsync("Attach Test", @"levels\multi\20_sword_slayer\20_sword_slayer", empty: true);
        if (!Check(map != null, "created an empty Sword Slayer map")) return;
        var until = DateTime.UtcNow.AddMinutes(3);
        while (map!.Bsp == null && DateTime.UtcNow < until) await Task.Delay(300);
        var spawn = map.Items.First(i => i.Tag.Contains("respawn_point"));
        var anchor = await map.AddTagAtAsync(@"objects\multi\generic\invisible_marker.crate", spawn.Position + new Vector3(3, 0, 0.1f));
        string moverTag = Environment.GetEnvironmentVariable("KALO_MOVER") ?? @"objects\multi\generic\flag_spawn_point.crate";
        var mover = await map.AddTagAtAsync(moverTag, spawn.Position + new Vector3(-3, 0, 0.1f));
        if (!Check(anchor != null && mover != null, "placed the anchor and the mover")) return;
        // Adding the second object rebuilt the item list: look both up again.
        await map.RenameAsync(map.Items.First(i => i.Tag.EndsWith("invisible_marker.crate")), "kalo_anchor");
        await map.RenameAsync(map.Items.First(i => i.Tag.Equals(moverTag, StringComparison.OrdinalIgnoreCase)), "kalo_mv");
        foreach (var (item, label) in new[] { (map.Items.First(i => i.PlacedName == "kalo_anchor"), "kalo_anchor"), (map.Items.First(i => i.PlacedName == "kalo_mv"), "kalo_mover") })
        {
            map.SetSelection(new[] { item });
            // Wait for this object's own property rows (the previous object's rows point at its fields).
            string own = $"Block:{item.Category}[{item.Index}]/";
            for (int i = 0; i < 80 && !map.Properties.Any(r => r.Path.StartsWith(own) && r.Path.EndsWith("String:megalo label")); i++) await Task.Delay(100);
            await map.SetPropertyAsync(map.Properties.First(r => r.Path.StartsWith(own) && r.Path.EndsWith("String:megalo label")), label);
        }
        Say("labels: " + string.Join(", ", map.Items.Where(i => i.PlacedName?.StartsWith("kalo_") == true).Select(i => $"{i.PlacedName}={i.Label}")));
        await map.SaveCommand.ExecuteAsync(null);
        _vm.OpenNewGametypeFormCommand.Execute(null);
        _vm.IsNewGametypeOpen = false;
        var script = await _vm.CreateGametypeAsync("Attach Rules", _vm.GametypeTemplates.First(t => t.Label == "Slayer"), true);
        if (!Check(script != null, "made a gametype")) return;
        string offset = Environment.GetEnvironmentVariable("KALO_OFFSET") ?? "10";
        // The mover goes to the anchor at the offset every tick; for its first 60 ticks the player is put onto the mover
        // (the console measures the player: a name lookup only finds the map's own placement, not the gametype's copy).
        string code =
            "declare player.number kalo_moved = 0\n" +
            "objects kalo_anchors with label \"kalo_anchor\"\n" +
            "for each object with label \"kalo_mover\" do\n" +
            "  temp object a = none\n" +
            "  get_random_object(kalo_anchors, none, a)\n" +
            "  if a != none then\n" +
            $"    object_attach(current_object, a, {offset}, 0, 0)\n" +
            "    object_detach(current_object)\n" +
            "    for each player do\n" +
            "      temp object body = current_player\n" +
            "      if body != none and current_player.kalo_moved < 60 then\n" +
            "        current_player.kalo_moved += 1\n" +
            "        object_attach(body, current_object, 0, 0, 0)\n" +
            "        object_detach(body)\n" +
            "      end\n" +
            "    end\n" +
            "  end\n" +
            "end\n";
        script!.Text = script.Text.TrimEnd() + "\n\n" + (Kalo.Core.Scripting.KaloScriptStyle.FromClassic(code) ?? code);
        await script.CompileCommand.ExecuteAsync(null);
        if (!Check(script.CompileOk, "it compiles: " + script.CompileSummary)) return;
        await script.SaveDocumentAsync();
        _vm.ActiveDocument = map;

        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _vm.Game!.Loaded += () => loaded.TrySetResult();
        var probes = new List<string>();
        _vm.Game.Output += line => { if (line.Contains("kalo probe:") && !line.Contains("(print")) lock (probes) probes.Add(line[line.IndexOf("kalo probe:")..].Trim()); };
        await _vm.PlayCommand.ExecuteAsync(null);
        if (!Check(await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6))) == loaded.Task, "the game loaded")) return;
        await Task.Delay(TimeSpan.FromSeconds(70));
        var bands = new[] { 0.5, 1.5, 2.5, 3.5, 5.0, 7.0, 9.0, 11.0, 40.0 };
        int measuredRound = -1;
        for (int round = 1; round <= 14 && measuredRound < 0; round++)
        {
            var cmds = bands.Select(b => $"(if (< (objects_distance_to_object (players) kalo_anchor) {b.ToString(System.Globalization.CultureInfo.InvariantCulture)}) (print \"kalo probe: r{round} under {b}\") (print \"kalo probe: r{round} over {b}\"))").ToList();
            cmds.Insert(0, $"(if (< (objects_distance_to_object (players) kalo_anchor) 0) (print \"kalo probe: r{round} not measurable\") (print \"kalo probe: r{round} measured\"))");
            cmds.Insert(0, $"(if (> (list_count (players)) 0) (print \"kalo probe: r{round} has players\") (print \"kalo probe: r{round} no players\"))");
            await _vm.Game.RunConsoleAsync(cmds, TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromSeconds(6));
            lock (probes) for (int k = 1; k <= round; k++) if (probes.Contains($"kalo probe: r{k} measured")) { measuredRound = k; break; }
        }
        for (int i = 0; i < 90 && measuredRound < 0; i++) { await Task.Delay(1000); lock (probes) for (int k = 1; k <= 14; k++) if (probes.Contains($"kalo probe: r{k} measured")) { measuredRound = k; break; } }
        lock (probes) foreach (var p in probes.Distinct().TakeLast(30)) Say(p);
        double? firstUnder = null;
        lock (probes) foreach (double b in bands) if (probes.Contains($"kalo probe: r{measuredRound} under {b}")) { firstUnder = b; break; }
        Check(measuredRound > 0, $"the player was measured (round {measuredRound}); distance to the anchor under {firstUnder}");
        _vm.StopGameCommand.Execute(null);
        await Pause(3000);
    });

    /// <summary>
    /// Do Bungie's own AI work in a playtest? A full copy of m10 (levels\mods\_kalo_tour\m10full), Play here at the barn
    /// (no mission scripts), then barn_skirm_init placed from the console; its alert state and position are watched.
    /// </summary>
    private async Task M10Ai()
    {
        const string path = @"levels\mods\_kalo_tour\m10full\m10full.scenario";
        string folder = System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, @"levels\mods\_kalo_tour\m10full");
        if (!File.Exists(System.IO.Path.Combine(_vm.Paths.WorkspaceTags, path)))
        {
            var r = await _vm.Host!.CreateMapAsync(new CreateMapParams { Template = @"levels\solo\m10\m10.scenario", NewPath = path, Empty = false, MapId = 7995 });
            Check(r.Type == "solo", "full copy of m10");
        }
        _vm.RememberScenarioType(path[..^".scenario".Length], "solo");
        var map = await _vm.OpenMapAsync(path);
        _vm.ActiveDocument = map;
        map.SelectedZoneSet = map.ZoneSetChoices.FirstOrDefault(z => z.Bsps != null && z.Bsps.Contains(2)) ?? map.SelectedZoneSet;
        var until = DateTime.UtcNow.AddMinutes(4);
        while (map.Bsp == null && DateTime.UtcNow < until) await Task.Delay(500);
        try
        {
            // Two squads of our own at the barn flag: one from scratch (Add › Squad in an empty mission), one copied from
            // barn_skirm_init (team, group, zone, objective kept). Compared with Bungie's own squad in game.
            var flag = map.Items.FirstOrDefault(i => i.PlacedName == "fl_barn_skirm_init");
            var bungie = map.Squads.FirstOrDefault(s => s.Name == "barn_skirm_init");
            if (flag != null && bungie != null && !map.Squads.Any(s => s.Name == "kalo_scratch"))
            {
                var at = new[] { flag.Position.X + 1, flag.Position.Y, flag.Position.Z + 0.2f, 0f };
                await _vm.Host!.SquadOpAsync(new SquadOpParams { Path = map.Path, Op = SquadOps.Create, Name = "kalo_scratch", Position = at });
                await _vm.Host.SquadOpAsync(new SquadOpParams { Path = map.Path, Op = SquadOps.New, Index = bungie.Index, Name = "kalo_copy", Position = new[] { flag.Position.X - 1, flag.Position.Y, flag.Position.Z + 0.2f, 0f } });
                map.IsDirty = true;
                await map.SaveCommand.ExecuteAsync(null);
                Say("made kalo_scratch and kalo_copy at the barn flag");
            }
            map.LookFrom(new Vector3(146f, 26f, 3.0f), 180, -20);
            await Pause(2000);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _vm.Game!.Loaded += () => loaded.TrySetResult();
            var probes = new List<string>();
            _vm.Game.Output += line => { if (line.Contains("kalo probe:") && !line.Contains("(print")) lock (probes) probes.Add(line[line.IndexOf("kalo probe:")..].Trim()); };
            await map.PlayFromCameraCommand.ExecuteAsync(null);
            if (!Check(await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6))) == loaded.Task, "Play here loaded the mission")) return;
            await Task.Delay(TimeSpan.FromSeconds(8));
            string sq = Environment.GetEnvironmentVariable("KALO_M10_SQUAD") ?? "barn_skirm_init";
            await _vm.Game.RunConsoleAsync(new[] { "(ai_place kalo_scratch)", "(ai_place kalo_copy)" }, TimeSpan.FromSeconds(10));
            Say("place: " + await _vm.Game.RunConsoleAsync(new[] { Environment.GetEnvironmentVariable("KALO_M10_MORTAL") == "1" ? "(print \"mortal\")" : "(set cheat_deathless_player true)", $"(ai_place {sq})", "(object_teleport (player_get 0) fl_barn_skirm_init)", $"(ai_magically_see_object {sq} (player_get 0))" }, TimeSpan.FromSeconds(10)));
            for (int round = 1; round <= 10; round++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                await _vm.Game.RunConsoleAsync(new[]
                {
                    $"(if (> (ai_living_count {sq}) 0) (print \"kalo probe: r{round} squad alive\") (print \"kalo probe: r{round} squad none\"))",
                    $"(if (> (ai_combat_status {sq}) 2) (print \"kalo probe: r{round} squad alerted\") (print \"kalo probe: r{round} squad calm\"))",
                    $"(if (< (unit_get_shield (player_get 0)) 0.95) (print \"kalo probe: r{round} player shot (shield down)\") (print \"kalo probe: r{round} player shield full\"))",
                    $"(if (< (unit_get_health (player_get 0)) 0.95) (print \"kalo probe: r{round} player hurt\") (print \"kalo probe: r{round} player health full\"))",
                    $"(if (> (ai_combat_status kalo_scratch) 2) (print \"kalo probe: r{round} scratch alerted\") (print \"kalo probe: r{round} scratch calm\"))",
                    $"(if (> (ai_combat_status kalo_copy) 2) (print \"kalo probe: r{round} copy alerted\") (print \"kalo probe: r{round} copy calm\"))",
                    $"(if (> (ai_living_count kalo_scratch) 0) (print \"kalo probe: r{round} scratch alive\") (print \"kalo probe: r{round} scratch none\"))",
                    $"(if (> (ai_living_count kalo_copy) 0) (print \"kalo probe: r{round} copy alive\") (print \"kalo probe: r{round} copy none\"))",
                    $"(if (< (objects_distance_to_object (ai_actors {sq}) (player_get 0)) 6) (print \"kalo probe: r{round} squad within 6 of the player\") (print \"kalo probe: r{round} squad farther than 6\"))",
                    $"(if (< (objects_distance_to_object (ai_actors {sq}) (player_get 0)) 15) (print \"kalo probe: r{round} squad within 15\") (print \"kalo probe: r{round} squad farther than 15\"))",
                }, TimeSpan.FromSeconds(10));
            }
            for (int i = 0; i < 30; i++) { lock (probes) if (probes.Any(p => p.StartsWith("kalo probe: r10"))) break; await Task.Delay(1000); }
            lock (probes) foreach (var p in probes.Distinct()) Say(p);
            _vm.StopGameCommand.Execute(null);
            await Pause(3000);
        }
        finally
        {
            if (_vm.Game?.IsRunning == true) _vm.Game.Stop();
            foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
        }
    }

    /// <summary>
    /// Add › Camera on the m10 copy at the barn (a level shot and one tilted down), saved, read back, then shown in game
    /// with the Live game bar and moved between with camera_pan. KALO_SHOTS_DIR is where the game is captured from outside.
    /// </summary>
    private async Task CamerasSection()
    {
        const string path = @"levels\mods\_kalo_tour\m10full\m10full.scenario";
        _vm.RememberScenarioType(path[..^".scenario".Length], "solo");
        var map = await _vm.OpenMapAsync(path);
        _vm.ActiveDocument = map;
        map.SelectedZoneSet = map.ZoneSetChoices.FirstOrDefault(z => z.Bsps != null && z.Bsps.Contains(2)) ?? map.SelectedZoneSet;
        var until = DateTime.UtcNow.AddMinutes(4);
        while (map.Bsp == null && DateTime.UtcNow < until) await Task.Delay(500);
        try
        {
            foreach (var old in map.Items.Where(i => i.IsCamera && i.PlacedName is "tour_a" or "tour_b").OrderByDescending(i => i.Index).ToList())
            { map.SetSelection(new[] { old }); await map.DeleteSelectedCommand.ExecuteAsync(null); }
            async Task<PlacementItem> Add(string name, Vector3 eye, float yaw, float pitch)
            {
                map.LookFrom(eye, yaw, pitch);
                await Pause(1500);
                await map.AddCameraHereCommand.ExecuteAsync(null);
                await map.RenameAsync(map.Selected!, name);
                return map.Items.First(i => i.IsCamera && i.PlacedName == name);
            }
            var a = await Add("tour_a", new Vector3(146f, 26f, 12f), 90, -60);
            var b = await Add("tour_b", new Vector3(146f, 26f, 12f), 90, 0);
            Check(Vector3.Distance(a.Position, new Vector3(146f, 26f, 12f)) < 0.01f && MathF.Abs(a.Yaw - 90) < 0.1f, $"tour_a is at the view: {a.Position} yaw {a.Yaw} pitch {a.Pitch}");
            Check(MathF.Abs(a.Pitch + 60) < 0.1f && MathF.Abs(b.Pitch) < 0.1f, $"the tilts are kept: {a.Pitch}, {b.Pitch}");
            // A capture from outside the game: the capture script takes one when the request file appears and deletes it.
            async Task GameShot(string name)
            {
                string request = System.IO.Path.Combine(_dir, "request-" + name + ".txt");
                File.WriteAllText(request, name);
                for (int i = 0; i < 60 && File.Exists(request); i++) await Task.Delay(500);
            }
            foreach (var (cam, shot) in new[] { (a, "editor-down.png"), (b, "editor-up.png") })
            {
                map.SetSelection(new[] { cam });
                map.LookThroughCameraCommand.Execute(null);
                await Pause(1500);
                await map.CaptureViewAsync(System.IO.Path.Combine(_dir, shot));
            }
            await map.SaveCommand.ExecuteAsync(null);
            map.LookFrom(new Vector3(146f, 26f, 3f), 90, 0); // the player faces the same way as tour_b: compare the two
            await Pause(1500);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _vm.Game!.Loaded += () => loaded.TrySetResult();
            await map.PlayFromCameraCommand.ExecuteAsync(null);
            if (!Check(await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6))) == loaded.Task, "Play here loaded the mission")) return;
            await _vm.Game.RunConsoleAsync(new[] { "(set cheat_deathless_player true)" }, TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromSeconds(8));
            await GameShot("game-start");
            Say("errors shown: " + await _vm.Game.RunConsoleAsync(new[] { "(error_geometry_show_all)" }, TimeSpan.FromSeconds(10)));
            await Task.Delay(TimeSpan.FromSeconds(3));
            await GameShot("errors-shown");
            Say("errors hidden: " + await _vm.Game.RunConsoleAsync(new[] { "(error_geometry_hide_all)" }, TimeSpan.FromSeconds(10)));
            await Task.Delay(TimeSpan.FromSeconds(3));
            await GameShot("errors-hidden");
            Say("wire off: " + await _vm.Game.RunConsoleAsync(new[] { "render_debug_structure_all_cluster_errors 0", "render_debug_structure_line_opacity 0 0 0 0", "render_debug_structure_opacity 0 0 0 0", "render_debug_structure_text_opacity 0 0 0 0" }, TimeSpan.FromSeconds(10)));
            await Task.Delay(TimeSpan.FromSeconds(3));
            await GameShot("wire-off");
            // The recipe's commands, in order, as console commands.
            Say("recipe: " + await _vm.Game.RunConsoleAsync(new[] { "player_enable_input 0", "chud_show 0", "camera_control 1", "camera_set tour_a 0" }, TimeSpan.FromSeconds(10)));
            await Task.Delay(TimeSpan.FromSeconds(3));
            await GameShot("game-down");
            map.SetSelection(new[] { map.Items.First(i => i.IsCamera && i.PlacedName == "tour_b") });
            await map.LivePreviewCameraCommand.ExecuteAsync(null);
            Say("preview b: " + map.LiveStatus);
            await Task.Delay(TimeSpan.FromSeconds(3));
            await GameShot("game-up");
            Say("recipe end: " + await _vm.Game.RunConsoleAsync(new[] { "camera_control 0", "chud_show 1", "player_enable_input 1" }, TimeSpan.FromSeconds(10)));
            await Task.Delay(TimeSpan.FromSeconds(3));
            await GameShot("game-back");
            _vm.StopGameCommand.Execute(null);
            await Pause(3000);
        }
        finally
        {
            if (_vm.Game?.IsRunning == true) _vm.Game.Stop();
            foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
        }
    }

    /// <summary>
    /// A copy of the assault rifle painted with a picture, given to the player through Player gear, and a human squad next
    /// to a Covenant one: in game, does the player hold it and do the two squads fight?
    /// </summary>
    private async Task GearTeamPicture()
    {
        string png = System.IO.Path.Combine(_dir, "my_skin.png");
        using (var bitmap = new SkiaSharp.SKBitmap(700, 500))
        {
            for (int y = 0; y < 500; y++) for (int x = 0; x < 700; x++) bitmap.SetPixel(x, y, ((x / 100) + (y / 100)) % 2 == 0 ? new SkiaSharp.SKColor(255, 0, 180) : new SkiaSharp.SKColor(255, 230, 0));
            using var stream = File.Create(png);
            bitmap.Encode(stream, SkiaSharp.SKEncodedImageFormat.Png, 100);
        }
        var rifle = await _vm.OpenTagAsync(@"objects\weapons\rifle\assault_rifle\assault_rifle.weapon") as TagEditorViewModel;
        if (Environment.GetEnvironmentVariable("KALO_GEAR_PLAIN") == "2") { await _vm.EnsureMaterialsAsync(); await rifle!.PaintWithCommand.ExecuteAsync(_vm.Materials[0]); } else await rifle!.PaintWithImageAsync(png);
        Say("picture: " + rifle.LookStatus);
        var painted = _vm.Tabs.OfType<TagEditorViewModel>().FirstOrDefault(t => t.Path.Contains(@"\mods\"));
        if (!Check(painted != null, "the picture made a painted copy: " + painted?.Path)) return;
        Check(File.Exists(System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, $@"levels\mods\{_vm.ModSlugForContent}\textures\my_skin.bitmap")), "tool.exe made the bitmap");

        var r = await _vm.CreateMapAsync("gear_test", @"levels\solo\m10\m10", false);
        var map = r!;
        _vm.ActiveDocument = map;
        map.SelectedZoneSet = map.ZoneSetChoices.FirstOrDefault(z => z.Bsps != null && z.Bsps.Contains(2)) ?? map.SelectedZoneSet;
        var until = DateTime.UtcNow.AddMinutes(4);
        while (map.Bsp == null && DateTime.UtcNow < until) await Task.Delay(500);
        await map.LoadGear();
        map.GearPrimary = map.GearWeapons.FirstOrDefault(g => Environment.GetEnvironmentVariable("KALO_GEAR_PLAIN") == "1" ? g.Path.EndsWith(@"dmr\dmr.weapon", StringComparison.OrdinalIgnoreCase) : g.Path.Equals(painted!.Path, StringComparison.OrdinalIgnoreCase));
        map.GearSecondary = map.GearWeapons.FirstOrDefault(g => g.Path.EndsWith("rocket_launcher.weapon", StringComparison.OrdinalIgnoreCase));
        map.GearAbility = map.GearEquipment.FirstOrDefault(g => g.Path.EndsWith(@"jetpack.equipment", StringComparison.OrdinalIgnoreCase)) ?? map.GearAbility;
        map.GearFrags = "4";
        if (!Check(map.GearPrimary != null && map.GearSecondary != null, "Player gear lists the painted rifle and the rocket launcher")) return;
        await map.ApplyGear();
        Say("gear: " + map.GearStatus);
        var back = await _vm.Host!.StartingGearAsync(map.Path); Say($"read back: primary {back.Primary} secondary {back.Secondary} equipment {back.Equipment} profiles {back.Profiles}");

        var flag = map.Items.First(i => i.PlacedName == "fl_barn_skirm_init");
        var at = flag.Position + new Vector3(1, 0, 0.2f);
        await _vm.Host!.SquadOpAsync(new SquadOpParams { Path = map.Path, Op = SquadOps.Create, Name = "tour_enemy", Position = new[] { at.X, at.Y, at.Z, 0f } });
        await _vm.Host.SquadOpAsync(new SquadOpParams { Path = map.Path, Op = SquadOps.Create, Name = "tour_ally", Position = new[] { at.X + 1.5f, at.Y, at.Z, 180f } });
        await map.SaveCommand.ExecuteAsync(null);
        await _vm.CloseTabCommand.ExecuteAsync(map);
        map = await _vm.OpenMapAsync(r!.Path);
        _vm.ActiveDocument = map;
        map.SelectedZoneSet = map.ZoneSetChoices.FirstOrDefault(z => z.Bsps != null && z.Bsps.Contains(2)) ?? map.SelectedZoneSet;
        until = DateTime.UtcNow.AddMinutes(4);
        while (map.Bsp == null && DateTime.UtcNow < until) await Task.Delay(500);
        var allySquad = map.Squads.First(s => s.Name == "tour_ally");
        map.SetSelection(new[] { map.Items.Last(i => i.IsAiSpawnPoint) });
        for (int i = 0; i < 40 && map.SquadEditor?.Name != "tour_ally"; i++) await Task.Delay(250);
        if (!Check(map.SquadEditor?.Name == "tour_ally", "the Squad section shows tour_ally")) return;
        Check(map.SquadEditor!.TeamIndex == 3, "a new squad shows the Covenant team");
        map.SquadEditor.TeamIndex = 2;
        await Pause(1500);
        Check(map.SquadEditor?.TeamIndex == 2, "the team reads back as Human");
        await map.SaveCommand.ExecuteAsync(null);
        map.LookFrom(flag.Position + new Vector3(0.3f, -0.8f, 1.2f), 0, -45);
        await Pause(1500);
        await painted!.PlaceInMapCommand.ExecuteAsync(null);
        Say("placed: " + painted.UseStatus);
        var placedRifle = map.Selected ?? map.Items.Last(i => i.Label != null || true);
        Say("selected: " + placedRifle.Display);
        await map.RenameAsync(placedRifle, "tour_rifle");
        var rifleAt = map.Items.First(i => i.PlacedName == "tour_rifle").Position;
        map.LookFrom(rifleAt + new Vector3(-0.1f, 0.6f, 1.0f), 0, -60);
        await Pause(800);
        await rifle!.PlaceInMapCommand.ExecuteAsync(null);
        Say("vanilla at " + map.Selected?.Position + ", painted at " + rifleAt);
        map.LookFrom(rifleAt + new Vector3(-0.5f, 0.3f, 1.3f), 0, -65);
        await Pause(1200);
        await map.AddCameraHereCommand.ExecuteAsync(null);
        await map.RenameAsync(map.Selected!, "tour_look");
        await map.SaveCommand.ExecuteAsync(null);
        map.LookFrom(new Vector3(146f, 26f, 3.0f), 180, -5);
        await Pause(1500);

        async Task GameShot(string name)
        {
            string request = System.IO.Path.Combine(_dir, "request-" + name + ".txt");
            File.WriteAllText(request, name);
            for (int i = 0; i < 60 && File.Exists(request); i++) await Task.Delay(500);
        }
        var probes = new List<string>();
        _vm.Game!.Output += line => { if (line.Contains("mods", StringComparison.OrdinalIgnoreCase) || line.Contains("my_skin", StringComparison.OrdinalIgnoreCase)) File.AppendAllText(System.IO.Path.Combine(_dir, "game.txt"), line + "\r\n"); if (line.Contains("kalo probe:") && !line.Contains("(print")) lock (probes) probes.Add(line[line.IndexOf("kalo probe:")..].Trim()); };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _vm.Game.Loaded += () => loaded.TrySetResult();
        await map.PlayFromCameraCommand.ExecuteAsync(null);
        if (!Check(await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6))) == loaded.Task, "the mission loaded")) return;
        await _vm.Game.RunConsoleAsync(new[] { "(set cheat_deathless_player true)", "(ai_place tour_enemy)", "(ai_place tour_ally)", "(ai_disregard (players) true)", "(ai_magically_see tour_enemy tour_ally)", "(ai_magically_see tour_ally tour_enemy)", "(print \"placed\")" }, TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromSeconds(4));
        await _vm.Game.RunConsoleAsync(new[] { "(if (>= (objects_distance_to_flag tour_rifle fl_barn_skirm_init) 0) (print \"kalo probe: rifle exists\") (print \"kalo probe: rifle missing\"))", "camera_control 1", "camera_set tour_look 0" }, TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromSeconds(2));
        await GameShot("rifle-look");
        await _vm.Game.RunConsoleAsync(new[] { "camera_control 0" }, TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromSeconds(10));
        await GameShot("gear-held");
        await _vm.Game.RunConsoleAsync(new[] { "(unit_add_equipment (player_get 0) player_starting_profile_0 true false)" }, TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromSeconds(4));
        await GameShot("gear-readd");
        for (int round = 1; round <= 12; round++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            await _vm.Game.RunConsoleAsync(new[]
            {
                $"(if (> (ai_living_count tour_enemy) 0) (print \"kalo probe: r{round} enemy alive\") (print \"kalo probe: r{round} enemy dead\"))",
                $"(if (> (ai_living_count tour_ally) 0) (print \"kalo probe: r{round} ally alive\") (print \"kalo probe: r{round} ally dead\"))",
                $"(if (> (ai_combat_status tour_enemy) 2) (print \"kalo probe: r{round} enemy alerted\") (print \"kalo probe: r{round} enemy calm\"))",
            }, TimeSpan.FromSeconds(10));
        }
        await Task.Delay(TimeSpan.FromSeconds(4));
        lock (probes) foreach (var p in probes.Distinct()) Say(p);
        await GameShot("gear-end");
        _vm.StopGameCommand.Execute(null);
        await Pause(3000);
    }

    /// <summary>Can a modder copy a Firefight map and start it? What type it is, what the list offers, and a game frame.</summary>
    private async Task FirefightCopy()
    {
        const string level = @"levels\firefight\ff20_courtyard\ff20_courtyard";
        Say("type of ff20: " + await _vm.Host!.ScenarioTypeAsync(level + ".scenario"));
        _vm.NewMapKind = "campaign"; Say("campaign list has ff20: " + _vm.BaseLevels.Any(l => l.Contains("ff20")));
        _vm.NewMapKind = "full"; Say("remix list has ff20: " + _vm.BaseLevels.Any(l => l.Contains("ff20")));
        var map = await _vm.CreateMapAsync("ff_test", level, false);
        if (!Check(map != null, "a full copy of ff20")) return;
        Say("campaign bar: " + map!.IsCampaign);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _vm.Game!.Loaded += () => loaded.TrySetResult();
        await _vm.PlayAsync();
        if (!Check(await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(5))) == loaded.Task, "the Firefight copy loaded")) return;
        await Task.Delay(TimeSpan.FromSeconds(25)); Say("running: " + _vm.Game.IsRunning);
        string request = System.IO.Path.Combine(_dir, "request-ff.txt");
        File.WriteAllText(request, "ff");
        for (int i = 0; i < 60 && File.Exists(request); i++) await Task.Delay(500);
        _vm.StopGameCommand.Execute(null);
        await Pause(3000);
    }

    /// <summary>Camera points against the game's debug camera (given the true direction): each pair of captures must match.</summary>
    private async Task CameraMatch()
    {
        const string path = @"levels\mods\_kalo_tour\m10full\m10full.scenario";
        _vm.RememberScenarioType(path[..^".scenario".Length], "solo");
        var map = await _vm.OpenMapAsync(path);
        _vm.ActiveDocument = map;
        map.SelectedZoneSet = map.ZoneSetChoices.FirstOrDefault(z => z.Bsps != null && z.Bsps.Contains(2)) ?? map.SelectedZoneSet;
        var until = DateTime.UtcNow.AddMinutes(4);
        while (map.Bsp == null && DateTime.UtcNow < until) await Task.Delay(500);
        var eye = new Vector3(140f, 30f, 8f);
        var views = new (string Name, float Yaw, float Pitch)[] { ("m_a", 45, -40), ("m_b", 250, -40), ("m_c", 0, 50), ("m_d", 90, -75) };
        foreach (var old in map.Items.Where(i => i.IsCamera && views.Any(v => v.Name == i.PlacedName)).OrderByDescending(i => i.Index).ToList())
        { map.SetSelection(new[] { old }); await map.DeleteSelectedCommand.ExecuteAsync(null); }
        foreach (var v in views)
        {
            map.LookFrom(eye, v.Yaw, v.Pitch);
            await Pause(800);
            await map.AddCameraHereCommand.ExecuteAsync(null);
            await map.RenameAsync(map.Selected!, v.Name);
            var c = map.Items.First(i => i.IsCamera && i.PlacedName == v.Name);
            float dy = MathF.Abs(((c.Yaw - v.Yaw) % 360 + 540) % 360 - 180);
            Check(dy < 0.1f && MathF.Abs(c.Pitch - v.Pitch) < 0.1f && MathF.Abs(c.Roll) < 0.1f, $"{v.Name} reads back: {c.Yaw:F1} {c.Pitch:F1} {c.Roll:F1}");
        }
        await map.SaveCommand.ExecuteAsync(null);
        map.LookFrom(new Vector3(146f, 26f, 3f), 90, 0);
        await Pause(1500);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var game = _vm.Game!;
        game.Loaded += () => loaded.TrySetResult();
        await map.PlayFromCameraCommand.ExecuteAsync(null);
        if (!Check(await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6))) == loaded.Task, "the mission loaded")) return;
        await game.RunConsoleAsync(new[] { "(set cheat_deathless_player true)", "chud_show 0" }, TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromSeconds(6));
        async Task Shot2(string name)
        {
            string request = System.IO.Path.Combine(_dir, "request-" + name + ".txt");
            File.WriteAllText(request, name);
            for (int i = 0; i < 60 && File.Exists(request); i++) await Task.Delay(500);
        }
        static string F(float x) => x.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var v in views)
        {
            float y = v.Yaw * MathF.PI / 180, p = v.Pitch * MathF.PI / 180;
            var f = new Vector3(MathF.Cos(y) * MathF.Cos(p), MathF.Sin(y) * MathF.Cos(p), MathF.Sin(p));
            var u = new Vector3(-MathF.Cos(y) * MathF.Sin(p), -MathF.Sin(y) * MathF.Sin(p), MathF.Cos(p));
            string file = System.IO.Path.Combine(_vm.Paths!.Workspace, "camera_" + System.IO.Path.GetFileName(game.Scenario!) + ".txt");
            File.WriteAllText(file, $"{F(eye.X)} {F(eye.Y)} {F(eye.Z)}\r\n{F(f.X)} {F(f.Y)} {F(f.Z)}\r\n{F(u.X)} {F(u.Y)} {F(u.Z)}\r\n1.361357\r\n");
            await game.RunConsoleAsync(new[] { "debug_camera_load" }, TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromSeconds(2));
            await Shot2("truth-" + v.Name);
            await game.RunConsoleAsync(new[] { "director_debug_camera 0", "camera_control 1", "camera_set " + v.Name + " 0" }, TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromSeconds(2));
            await Shot2("cam-" + v.Name);
            await game.RunConsoleAsync(new[] { "camera_control 0" }, TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        _vm.StopGameCommand.Execute(null);
        await Pause(3000);
        foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
    }

    /// <summary>
    /// A mission left in the active mod for checking AI by eye: a full copy of m10 with two Kalo-made squads at the barn,
    /// one from scratch and one copied from Bungie's barn_skirm_init.
    /// </summary>
    private async Task AiDemoMission()
    {
        var map = await _vm.CreateMapAsync("test_ai_squads", @"levels\solo\m10\m10", false);
        if (!Check(map != null, "full copy of m10 in the active mod")) return;
        var until = DateTime.UtcNow.AddMinutes(4);
        while (map!.Bsp == null && DateTime.UtcNow < until) await Task.Delay(500);
        var flag = map.Items.FirstOrDefault(i => i.PlacedName == "fl_barn_skirm_init");
        var bungie = map.Squads.FirstOrDefault(s => s.Name == "barn_skirm_init");
        if (!Check(flag != null && bungie != null, "barn flag and squad found")) return;
        await _vm.Host!.SquadOpAsync(new SquadOpParams { Path = map.Path, Op = SquadOps.Create, Name = "kalo_scratch", Position = new[] { flag!.Position.X + 1, flag.Position.Y, flag.Position.Z + 0.2f, 0f } });
        await _vm.Host.SquadOpAsync(new SquadOpParams { Path = map.Path, Op = SquadOps.New, Index = bungie!.Index, Name = "kalo_copy", Position = new[] { flag.Position.X - 1, flag.Position.Y, flag.Position.Z + 0.2f, 0f } });
        map.IsDirty = true;
        await map.SaveCommand.ExecuteAsync(null);
        Say("saved " + map.Path);
        map.IsDirty = false;
        await _vm.CloseTabCommand.ExecuteAsync(map);
    }

    /// <summary>
    /// Which missions have AI that obey? Starts one scenario as it is on disk (KALO_MX=scenario|squad|flag|point), places the
    /// squad, puts the player at the flag, orders the squad to the point and watches its distance from the flag.
    /// </summary>
    private async Task AiMatrix()
    {
        string[] mx = (Environment.GetEnvironmentVariable("KALO_MX") ?? @"levels\solo\m10\m10|barn_skirm_init|fl_barn_skirm_init|pts_barn/spartan01").Split('|');
        var (scenario, squad, flag, point) = (mx[0], mx[1], mx[2], mx[3]);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var game = _vm.Game!;
        game.Loaded += () => loaded.TrySetResult();
        var probes = new List<string>();
        game.Output += line => { if (line.Contains("kalo probe:") && !line.Contains("(print")) lock (probes) probes.Add(line[line.IndexOf("kalo probe:")..].Trim()); };
        game.Campaign = true;
        game.RunScripts = Environment.GetEnvironmentVariable("KALO_MX_SCRIPTS") == "1";
        game.Start(scenario, new[] { "game_difficulty normal" }, new[] { "switch_zone_set zoneset_intro_cinematic" });
        try
        {
            if (!Check(await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6))) == loaded.Task, "loaded " + scenario)) return;
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (Environment.GetEnvironmentVariable("KALO_MX_WATCH") is { Length: > 0 } watch)
            {
                // No orders: each squad's distance to the player, in fine steps, over a minute. Moving AI change band.
                var squads = watch.Split(',');
                await game.RunConsoleAsync(new[] { "(set cheat_deathless_player true)", $"(object_teleport (player_get 0) {flag})" }.Concat(squads.Select(s => $"(ai_place {s})")).ToArray(), TimeSpan.FromSeconds(10));
                int[] steps = { 3, 6, 10, 15 };
                for (int round = 1; round <= 15; round++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(4));
                    var cmds = squads.SelectMany(s => steps.Select(b =>
                        $"(if (and (>= (objects_distance_to_object (ai_actors {s}) (player_get 0)) 0) (< (objects_distance_to_object (ai_actors {s}) (player_get 0)) {b})) (print \"kalo probe: r{round} {s} u{b}\") (print \"kalo probe: r{round} {s} x\"))")).ToList();
                    await game.RunConsoleAsync(cmds, TimeSpan.FromSeconds(15));
                }
                await Task.Delay(TimeSpan.FromSeconds(15));
                lock (probes)
                    foreach (string s in squads)
                        Say($"{s}: " + string.Join(" ", Enumerable.Range(1, 15).Select(r => probes.Where(p => p.StartsWith($"kalo probe: r{r} {s} u")).Select(p => int.Parse(p[(p.LastIndexOf(" u") + 2)..])).DefaultIfEmpty(-1).Min())));
                return;
            }
            await game.RunConsoleAsync(new[] { "(set cheat_deathless_player true)", $"(object_teleport (player_get 0) {flag})", $"(ai_place {squad})", "(ai_disregard (players) true)" }, TimeSpan.FromSeconds(10));
            for (int round = 1; round <= 12; round++)
            {
                await Task.Delay(TimeSpan.FromSeconds(4));
                if (round == 3) Say("order: " + await game.RunConsoleAsync(new[] { $"(cs_go_to {squad} true {point})" }, TimeSpan.FromSeconds(10)));
                var cmds = new List<string> { $"(if (> (ai_living_count {squad}) 0) (print \"kalo probe: r{round} alive\") (print \"kalo probe: r{round} none\"))" };
                foreach (int b in new[] { 3, 6, 10, 15 })
                    cmds.Add($"(if (< (objects_distance_to_flag (ai_actors {squad}) {flag}) {b}) (print \"kalo probe: r{round} under {b}\") (print \"kalo probe: r{round} over {b}\"))");
                await game.RunConsoleAsync(cmds, TimeSpan.FromSeconds(10));
            }
            for (int i = 0; i < 40; i++) { lock (probes) if (probes.Any(p => p.StartsWith("kalo probe: r12 "))) break; await Task.Delay(1000); }
            lock (probes)
                for (int r = 1; r <= 12; r++)
                    Say($"r{r}: " + string.Join(" ", probes.Distinct().Where(p => p.StartsWith($"kalo probe: r{r} ")).Select(p => p[$"kalo probe: r{r} ".Length..]).Where(p => !p.StartsWith("over"))));
        }
        finally { if (game.IsRunning) game.Stop(); await Pause(3000); }
    }

    /// <summary>
    /// Campaign volumes in game: an empty m10 mission (levels\mods\_kalo_tour\volumes, outside the mod), an unnamed volume
    /// around the player's start with Checkpoint and Teleport to a flag (fl_exit) from the inspector, a control flag inside
    /// the volume. Played: the game is asked how far the player is from each flag.
    /// </summary>
    private async Task VolumesPlay()
    {
        const string path = @"levels\mods\_kalo_tour\volumes\volumes.scenario";
        string folder = System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, @"levels\mods\_kalo_tour\volumes");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        var r = await _vm.Host!.CreateMapAsync(new CreateMapParams { Template = @"levels\solo\m10\m10.scenario", NewPath = path, Empty = true, MapId = 7993 });
        Check(r.Type == "solo", "empty campaign mission on m10");
        _vm.RememberScenarioType(path[..^".scenario".Length], "solo");
        var map = await _vm.OpenMapAsync(path);
        _vm.ActiveDocument = map;
        map.SelectedZoneSet = map.ZoneSetChoices.FirstOrDefault(z => z.Bsps != null && z.Bsps.Contains(2)) ?? map.SelectedZoneSet;
        var until = DateTime.UtcNow.AddMinutes(4);
        while (map.Bsp == null && DateTime.UtcNow < until) await Task.Delay(500);
        try
        {
            // The exit flag where the tutorial's squad stands; the volume and a control flag where its volume is.
            map.LookFrom(new Vector3(134.03f, 35.20f, 3.0f), 0, -60);
            await Pause(1500);
            await map.AddMarkerCommand.ExecuteAsync(CampaignCategories.CutsceneFlags);
            if (!Check(map.Selected is { Category: CampaignCategories.CutsceneFlags }, "Add › Flag")) return;
            await map.RenameAsync(map.Selected!, "fl_exit");
            map.LookFrom(new Vector3(146f, 26f, 3.0f), 0, -60);
            await Pause(1500);
            await map.AddMarkerCommand.ExecuteAsync(CampaignCategories.CutsceneFlags);
            await map.RenameAsync(map.Selected!, "fl_control");
            var exitFlag = map.Items.First(i => i.PlacedName == "fl_exit");
            Say($"fl_exit at {exitFlag.Position}, fl_control at {map.Selected!.Position}, {Vector3.Distance(exitFlag.Position, map.Selected.Position):F1} apart");
            await map.AddMarkerCommand.ExecuteAsync(CampaignCategories.TriggerVolumes);
            var volume = map.Selected;
            if (!Check(volume is { IsVolume: true }, "Add › Volume (left unnamed)")) return;
            var start = map.Items.First(i => i.Category == StartLocations.Category);
            map.MoveLocal(start, volume!.Position - new Vector3(0, 0, 1.2f));
            await map.CommitMoveAsync(start);
            map.SetSelection(new[] { volume });

            map.VolumeAction = map.VolumeActionChoices.First(c => c.Action == Kalo.Core.Scripting.Campaign.VolumeAction.Checkpoint);
            await map.AddVolumeTriggerCommand.ExecuteAsync(null);
            Say("volume: " + map.VolumeTriggerStatus);
            int volumeIndex = volume.Index;
            volume = map.Items.FirstOrDefault(i => i.IsVolume && i.Index == volumeIndex);
            Check(!string.IsNullOrEmpty(volume?.PlacedName), "the volume has a script name: " + volume?.PlacedName);
            map.SetSelection(new[] { volume! });
            map.VolumeAction = map.VolumeActionChoices.First(c => c.Action == Kalo.Core.Scripting.Campaign.VolumeAction.TeleportToFlag);
            Check(map.VolumeParamChoices.Contains("fl_exit"), "the flag list offers fl_exit: " + string.Join(", ", map.VolumeParamChoices));
            map.VolumeParam = "fl_exit";
            await map.AddVolumeTriggerCommand.ExecuteAsync(null);
            Say("volume: " + map.VolumeTriggerStatus);
            var doc = _vm.Tabs.OfType<MissionScriptViewModel>().FirstOrDefault();
            Check(doc != null && doc.Text.Contains("object_teleport(player_get(0), fl_exit)") && doc.Text.Contains("game_save()"), "both actions are in the mission script");
            Check(doc?.CheckOk == true, "the mission script checks clean: " + doc?.Summary);
            if (doc != null) await doc.SaveDocumentAsync();
            await map.SaveCommand.ExecuteAsync(null);
            _vm.ActiveDocument = map;
            await Pause(500);
            Shot("volume-map");

            string name = path[..^".scenario".Length];
            if (!_vm.Scenarios.Contains(name, StringComparer.OrdinalIgnoreCase)) _vm.Scenarios.Insert(0, name);
            _vm.SelectedScenario = _vm.Scenarios.First(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _vm.Game!.Loaded += () => loaded.TrySetResult();
            var probes = new List<string>();
            _vm.Game.Output += line => { if (line.Contains("kalo probe:") && !line.Contains("(print")) lock (probes) probes.Add(line[line.IndexOf("kalo probe:")..]); };
            await _vm.PlayCommand.ExecuteAsync(null);
            var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6)));
            if (!Check(done == loaded.Task, "the mission loaded")) return;
            static string[] Probe(int round, string flag) => new[]
            {
                $"(if (< (objects_distance_to_flag (players) {flag}) 0) (print \"kalo probe: r{round} no player for {flag}\") (print \"kalo probe: r{round} measured {flag}\"))",
                $"(if (< (objects_distance_to_flag (players) {flag}) 2.5) (print \"kalo probe: r{round} near {flag}\") (print \"kalo probe: r{round} far from {flag}\"))",
                $"(if (< (objects_distance_to_flag (players) {flag}) 6) (print \"kalo probe: r{round} {flag} under 6\") (print \"kalo probe: r{round} {flag} over 6\"))",
                $"(if (< (objects_distance_to_flag (players) {flag}) 15) (print \"kalo probe: r{round} {flag} under 15\") (print \"kalo probe: r{round} {flag} over 15\"))",
            };
            bool Has(string s) { lock (probes) return probes.Contains("kalo probe: " + s); }
            var measured = new List<int>();
            for (int round = 1; round <= 12 && measured.Count < 2; round++)
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                await _vm.Game.RunConsoleAsync(Probe(round, "fl_exit").Concat(Probe(round, "fl_control")).ToArray(), TimeSpan.FromSeconds(10));
                for (int k = 1; k <= round; k++) if (!measured.Contains(k) && Has($"r{k} measured fl_exit") && Has($"r{k} measured fl_control")) measured.Add(k);
            }
            for (int i = 0; i < 20 && measured.Count == 0; i++) await Task.Delay(1000);
            lock (probes) foreach (var p in probes.Distinct()) Say(p);
            if (Check(measured.Count > 0, $"the game measured the player (rounds {string.Join(", ", measured)})"))
            {
                int k = measured[^1];
                // The player lands next to the flag (a few units: the flag stands on uneven ground); the flags are 17 units apart.
                Check(Has($"r{k} fl_exit under 6"), "the volume teleported the player to fl_exit (within 6 units)");
                Check(Has($"r{k} fl_control over 15"), "the player left the volume (over 15 units from the control flag)");
            }
            _vm.StopGameCommand.Execute(null);
            await Pause(3000);
        }
        finally
        {
            if (_vm.Game?.IsRunning == true) _vm.Game.Stop();
            foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
        }
    }

    /// <summary>Play here on the empty m10 mission, looking at the barn (the runner screenshots the game): for level geometry probes.</summary>
    private async Task BarnView()
    {
        const string path = @"levels\mods\_kalo_tour\emptym\emptym.scenario";
        _vm.RememberScenarioType(path[..^".scenario".Length], "solo");
        var map = await _vm.OpenMapAsync(path);
        _vm.ActiveDocument = map;
        map.SelectedZoneSet = map.ZoneSetChoices.FirstOrDefault(z => z.Bsps != null && z.Bsps.Contains(2)) ?? map.SelectedZoneSet;
        // Stand 7 units in front of the barn's garage door (an instanced geometry piece of bsp 2), facing it.
        var bspRef = (await _vm.Host!.ReadElementAsync(path, "structure bsps", 2)).Fields.First(f => f.Name == "structure bsp").Value!;
        Vector3 doorAt = new(127.34f, 40.17f, 0f), facing = new(1, 0, 0);
        for (int i = 0; i < 400; i++)
        {
            var inst = await _vm.Host.ReadElementAsync(bspRef, "instanced geometry instances", i);
            if (inst.Fields.FirstOrDefault(f => f.Name == "name")?.Value?.StartsWith("kiva_garagedoor_14") != true) continue;
            float[] P(string n) => (inst.Fields.First(f => f.Name == n).Value ?? "0,0,0").Split(',').Select(x => float.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var pos = P("position"); var fw = P("forward");
            doorAt = new Vector3(pos[0], pos[1], pos[2]);
            facing = Vector3.Normalize(new Vector3(fw[0], fw[1], 0));
            Say($"garage door {i} at {doorAt}, forward {facing}");
            break;
        }
        // Try both sides: the side with floor under it.
        var eye = doorAt + facing * 7 + new Vector3(0, 0, 1.5f);
        float yaw = MathF.Atan2(-facing.Y, -facing.X) * 180 / MathF.PI;
        map.LookFrom(eye, yaw, -5);
        await Pause(1500);
        if (map.PlayFromCheck?.Invoke() != null)
        {
            eye = doorAt - facing * 7 + new Vector3(0, 0, 1.5f);
            yaw = MathF.Atan2(facing.Y, facing.X) * 180 / MathF.PI;
            map.LookFrom(eye, yaw, -5);
        }
        await Pause(3000);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _vm.Game!.Loaded += () => loaded.TrySetResult();
        await map.PlayFromCameraCommand.ExecuteAsync(null);
        var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(5)));
        Check(done == loaded.Task, "the mission loaded");
        Say($"holding the game for {_hold:F0} s");
        await Task.Delay(TimeSpan.FromSeconds(_hold));
        _vm.StopGameCommand.Execute(null);
        await Pause(3000);
        foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
    }

    /// <summary>
    /// Empty campaign mission on m10 (levels\mods\_kalo_tour\emptym, outside the mod): no squads, one starter script;
    /// a squad added from scratch at the barn and a starter script that places it. The runner then plays it
    /// (--play-map) and looks for "mission started" and "squad placed" in the game output.
    /// </summary>
    private async Task EmptyMission()
    {
        const string path = @"levels\mods\_kalo_tour\emptym\emptym.scenario";
        string folder = System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, @"levels\mods\_kalo_tour\emptym");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        var r = await _vm.Host!.CreateMapAsync(new CreateMapParams { Template = @"levels\solo\m10\m10.scenario", NewPath = path, Empty = true, MapId = 7991 });
        Check(r.Type == "solo" && r.Scripts?.Count == 1 && r.Scripts[0].EndsWith(@"scripts\emptym.scenario_hs_source_file"), $"empty mission made in {r.Ms} ms: {r.Removed} AI and cutscene entries removed, scripts {string.Join(", ", r.Scripts ?? new())}");
        Check(Directory.GetFiles(System.IO.Path.Combine(folder, "scripts")).Length == 1, "only the starter script file is next to it");
        _vm.RememberScenarioType(path[..^".scenario".Length], "solo");
        var map = await _vm.OpenMapAsync(path);
        _vm.ActiveDocument = map;
        Check(map.IsCampaign && map.Squads.Count == 0 && !map.Items.Any(i => i.IsAiSpawnPoint), $"no squads ({map.Squads.Count}), {map.Items.Count(i => i.IsVolume)} trigger volumes, {map.ZoneSetChoices.Count - 1} zone sets kept");
        var own = map.MissionScripts.Where(m => m.IsOwn).ToList();
        Check(own.Count == 1 && own[0].Name == "emptym", "one script of its own: " + string.Join(", ", own.Select(m => m.Name)));
        // A squad from scratch at m10's barn (where barn_skirm_init stands).
        map.LookFrom(new Vector3(134.03f, 35.20f, 3.0f), 0, -60);
        await Pause(2000);
        await map.AddSquadCommand.ExecuteAsync(null);
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && map.SquadEditor?.Name != "squad") await Task.Delay(200);
        var sq = map.SquadEditor;
        Check(map.Squads.Count == 1 && sq is { Name: "squad", IsTemplated: false, Cells.Count: 1 }, $"Add › Squad: {sq?.Name}, {sq?.Cells.Count} cell, {sq?.Cells.FirstOrDefault()?.Characters.FirstOrDefault()?.Options.ElementAtOrDefault(sq.Cells[0].Characters[0].SelectedIndex)}");
        await map.SaveCommand.ExecuteAsync(null);
        // The starter script, in Kalo Script, then a version that places the squad.
        var doc = await _vm.OpenMissionScriptAsync(own[0].Path, map.Path);
        Check(doc.IsKaloScript && doc.Text.Contains("startup mission_start:"), "the starter script opens in Kalo Script: " + doc.Text.Replace("\n", " | "));
        doc.Text = "# Tour: the squad placed at the start.\nstartup mission_start:\n    print(\"mission started\")\n    ai_place(squad)\n    sleep(30)\n    if ai_living_count(squad) > 0:\n        print(\"squad placed\")\n";
        doc.Check();
        Check(doc.CheckOk, "the mission script checks clean (squad is a name of the map): " + doc.Summary);
        await doc.SaveDocumentAsync();
        var back = await _vm.Host.ReadMissionScriptAsync(own[0].Path);
        Check(back.Text != null && back.Text.Contains("(ai_place squad)"), "saved as HaloScript");
        await Pause(800);
        Shot("empty-mission");
        foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
    }

    /// <summary>Timings of everyday campaign edits on the m10 copy (nothing saved): layer chips, add / duplicate / delete a spawn point, undo, redo.</summary>
    private async Task CampaignPerf()
    {
        const string probe = @"levels\mods\_kalo_tour\cprobe\cprobe.scenario";
        var map = await _vm.OpenMapAsync(probe);
        _vm.ActiveDocument = map;
        await Pause(3000);
        async Task<long> Ui(Action a)
        {
            var sw = Stopwatch.StartNew();
            a();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            return sw.ElapsedMilliseconds;
        }
        Say($"perf: {map.Items.Count} items");
        Say($"perf: Volumes off {await Ui(() => map.ShowVolumes = false)} ms, on {await Ui(() => map.ShowVolumes = true)} ms");
        Say($"perf: AI off {await Ui(() => map.ShowAi = false)} ms, on {await Ui(() => map.ShowAi = true)} ms");
        var pt = map.Items.First(i => i.IsAiSpawnPoint);
        Say($"perf: select a spawn point {await Ui(() => map.SetSelection(new[] { pt }))} ms");
        int before = map.Items.Count;
        var sw = Stopwatch.StartNew();
        await map.AddMarkerCommand.ExecuteAsync(CampaignCategories.AiSpawnPoints);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Say($"perf: add spawn point {sw.ElapsedMilliseconds} ms ({before} → {map.Items.Count}, selected {map.Selected?.Display})");
        sw.Restart();
        await map.DuplicateSelectedCommand.ExecuteAsync(null);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Say($"perf: duplicate {sw.ElapsedMilliseconds} ms ({map.Items.Count})");
        sw.Restart();
        await map.DeleteSelectedCommand.ExecuteAsync(null);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Say($"perf: delete {sw.ElapsedMilliseconds} ms ({map.Items.Count})");
        sw.Restart();
        await map.UndoAsync();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Say($"perf: undo {sw.ElapsedMilliseconds} ms ({map.Items.Count}) {map.Status}");
        Check(map.Items.Count == before + 2, $"undo brought the deleted spawn point back ({map.Items.Count})");
        sw.Restart();
        await map.RedoAsync();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Say($"perf: redo {sw.ElapsedMilliseconds} ms ({map.Items.Count}) {map.Status}");
        Check(map.Items.Count == before + 1, $"redo deleted it again ({map.Items.Count})");
        await map.UndoAsync(); await map.UndoAsync(); await map.UndoAsync();
        Check(map.Items.Count == before, $"three more undos: back to the start ({map.Items.Count} of {before})");
        foreach (string l in _vm.OutputLines.Where(l => l.Contains("undo step")).TakeLast(6)) Say(l);
        foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
    }

    /// <summary>
    /// In game on the m10 copy: New squad here (an Elite cell), saved, Play here next to it, then the Live game bar:
    /// spawn the new squad (probe: alive, shielded), move to the camera, load the zone set, remove all AI (probe: gone).
    /// The test map is put back from a copy at the end.
    /// </summary>
    private async Task LiveMission()
    {
        const string probe = @"levels\mods\_kalo_tour\cprobe\cprobe.scenario";
        string file = System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, probe);
        if (!Check(File.Exists(file), "the campaign probe map exists")) return;
        File.Copy(file, file + ".kalo_tour_bak", true);
        try
        {
            var map = await _vm.OpenMapAsync(probe);
            _vm.ActiveDocument = map;
            var pt = map.Items.FirstOrDefault(i => i.IsAiSpawnPoint && map.Squads.FirstOrDefault(q => q.Index == MapEditorViewModel.SquadOf(i))?.Name == "barn_skirm_init");
            if (!Check(pt != null, "a spawn point of barn_skirm_init")) return;
            map.SetSelection(new[] { pt! });
            var until = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < until && map.SquadEditor?.Name != "barn_skirm_init") await Task.Delay(200);
            // The new squad's point goes where the camera looks: 3 units in front of barn_skirm_init's point.
            double yaw = pt!.Yaw * Math.PI / 180;
            var dir = new Vector3((float)Math.Cos(yaw), (float)Math.Sin(yaw), 0);
            map.LookFrom(pt.Position + new Vector3(0, 0, 1.2f), pt.Yaw, -40);
            await Pause(1500);
            await map.NewSquadHereCommand.ExecuteAsync(null);
            until = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < until && map.SquadEditor?.Name.StartsWith("barn_skirm_init_new") != true) await Task.Delay(200);
            var sq = map.SquadEditor;
            if (!Check(sq is { IsTemplated: false, Cells.Count: > 0 } && sq.Name.StartsWith("barn_skirm_init_new"), "new squad: " + sq?.Name)) return;
            string name = sq!.Name;
            int elite = sq.Characters.ToList().IndexOf("elite");
            sq.Cells[0].Characters[0].SelectedIndex = elite;
            await Pause(2500);
            var back = await _vm.Host!.SquadAsync(map.Path, sq.Index);
            Check(back.Cells[0].Characters[0].Palette == elite, "its cell spawns an Elite");
            // Play here loads the zone set picked in the Campaign bar: one with the barn's bsp (2).
            map.SelectedZoneSet = map.ZoneSetChoices.FirstOrDefault(z => z.Bsps != null && z.Bsps.Contains(2)) ?? map.SelectedZoneSet;
            await Pause(2000);
            await map.SaveCommand.ExecuteAsync(null);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _vm.Game!.Loaded += () => loaded.TrySetResult();
            await _vm.PlayFromAsync(map, pt.Position - dir * 2 + new Vector3(0, 0, 0.3f), pt.Yaw);
            var done = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(5)));
            if (!Check(done == loaded.Task, "the mission loaded")) return;
            await Task.Delay(8000);
            Check(map.IsGameRunning, "the Live game bar shows");
            var newPoint = map.Items.FirstOrDefault(i => i.IsAiSpawnPoint && map.Squads.FirstOrDefault(q => q.Index == MapEditorViewModel.SquadOf(i))?.Name == name);
            if (newPoint != null) map.SetSelection(new[] { newPoint });
            await Pause(1000);
            await map.LiveSpawnSquadCommand.ExecuteAsync(null);
            Check(map.LiveStatus.StartsWith("Placed squad " + name), "Spawn this squad: " + map.LiveStatus);
            await _vm.Game.RunConsoleAsync(new[] { "ai_disregard (players) true" }, TimeSpan.FromSeconds(10));
            await Task.Delay(2000);
            await _vm.Game.RunConsoleAsync(new[]
            {
                $"(if (> (ai_living_count {name}) 0) (print \"kalo probe: new squad alive\") (print \"kalo probe: new squad not alive\"))",
                $"(if (> (object_get_shield (ai_get_object {name})) 0) (print \"kalo probe: new squad shielded\") (print \"kalo probe: new squad no shield\"))",
            }, TimeSpan.FromSeconds(20));
            await Task.Delay(2500);
            Check(_vm.OutputLines.Any(l => l.Contains("kalo probe: new squad alive")), "the new squad is in the game");
            Check(_vm.OutputLines.Any(l => l.Contains("kalo probe: new squad shielded")), "as an Elite (shielded)");
            Shot("live-mission-squad");
            map.LookFrom(pt.Position + dir * 4 + new Vector3(0, 0, 1.5f), pt.Yaw + 180, -10);
            await Pause(1000);
            await map.LiveTeleportCommand.ExecuteAsync(null);
            Check(map.LiveStatus.StartsWith("Moved the player"), "Move me to the camera: " + map.LiveStatus);
            await map.LiveSwitchZoneSetCommand.ExecuteAsync(null);
            Check(map.LiveStatus.StartsWith("Switched the game"), "Load this zone set: " + map.LiveStatus);
            await Task.Delay(3000);
            await map.LiveRemoveAiCommand.ExecuteAsync(null);
            await Task.Delay(1500);
            await _vm.Game.RunConsoleAsync(new[] { $"(if (> (ai_living_count {name}) 0) (print \"kalo probe: still alive\") (print \"kalo probe: all removed\"))" }, TimeSpan.FromSeconds(20));
            await Task.Delay(2500);
            Check(_vm.OutputLines.Any(l => l.Contains("kalo probe: all removed")), "Remove all AI removed it");
            foreach (string l in _vm.OutputLines.Where(l => l.Contains("kalo probe", StringComparison.Ordinal)).TakeLast(6)) Say("game: " + l);
            Say($"holding the game for {_hold:F0} s");
            await Task.Delay(TimeSpan.FromSeconds(_hold));
            _vm.StopGameCommand.Execute(null);
            await Pause(3000);
        }
        finally
        {
            if (_vm.Game?.IsRunning == true) _vm.Game.Stop();
            foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
            await Task.Delay(1000);
            File.Copy(file + ".kalo_tour_bak", file, true);
            File.Delete(file + ".kalo_tour_bak");
            Say("the test map is back as it was");
        }
    }

    /// <summary>
    /// Squad tools and the cleaner campaign inspector on the m10 copy (nothing is saved): the squad's plain summary
    /// (placed by which script line, its objective), Use its own cells, New squad here, a volume's purpose, no
    /// multiplayer "outside the level" warning, and the Activity panel.
    /// </summary>
    private async Task SquadTools()
    {
        const string probe = @"levels\mods\_kalo_tour\cprobe\cprobe.scenario";
        if (!Check(File.Exists(System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, probe)), "the campaign probe map exists")) return;
        var map = await _vm.OpenMapAsync(probe);
        Check(!map.HasOutside, "a mission has no multiplayer 'outside the level' warning");
        int squads = map.Squads.Count;
        var pt = map.Items.FirstOrDefault(i => i.IsAiSpawnPoint && map.Squads.FirstOrDefault(s => s.Index == MapEditorViewModel.SquadOf(i))?.Name == "barn_skirm_init");
        if (!Check(pt != null, "a spawn point of barn_skirm_init")) return;
        map.SetSelection(new[] { pt! });
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until && !(map.SquadEditor is { Name: "barn_skirm_init", HasPlacedBy: true })) await Task.Delay(250);
        var sq = map.SquadEditor;
        Check(sq is { HasPlacedBy: true } && sq.PlacedBy.Contains("m10 line"), "placed by: " + sq?.PlacedBy);
        Check(sq is { HasStarts: true }, "objective and zone: " + sq?.Starts);
        Check(sq is { IsTemplated: true }, "the squad is made from a template");
        await Pause(800);
        Shot("squad-summary");
        await map.DetachSquadCommand.ExecuteAsync(null);
        until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until && map.SquadEditor is { IsTemplated: true }) await Task.Delay(200);
        sq = map.SquadEditor;
        Check(sq is { IsTemplated: false } && sq.Cells.Count > 0 && sq.Cells.All(c => c.CanEdit), $"Use its own cells: {sq?.Cells.Count} editable cells, no template ({map.Status})");
        var back = await _vm.Host!.SquadAsync(map.Path, sq!.Index);
        Check(back.CellsKind == "designer" && string.IsNullOrEmpty(back.Template), $"the host reads designer cells and no template ({back.CellsKind}, '{back.Template}')");
        await map.NewSquadHereCommand.ExecuteAsync(null);
        Check(map.Squads.Count == squads + 1 && map.Selected is { IsAiSpawnPoint: true }, $"New squad here: {map.Squads.Count - squads} new squad, its spawn point selected ({map.Status})");
        until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until && map.SquadEditor?.Name.EndsWith("_new") != true) await Task.Delay(200);
        Check(map.SquadEditor is { IsTemplated: false } && map.SquadEditor.Name.StartsWith("barn_skirm_init_new"), "the new squad has its own cells: " + map.SquadEditor?.Name);
        Check(!map.IsGameRunning, "no Live game bar while the game is not running");
        await Pause(800);
        Shot("squad-new");
        // A zone set switch volume says what it does.
        var zone = map.Items.FirstOrDefault(i => i.IsVolume && (i.PlacedName ?? "").StartsWith("zone_set:", StringComparison.OrdinalIgnoreCase));
        if (zone != null)
        {
            map.SetSelection(new[] { zone });
            await Pause(1500);
            Check(zone.HasPurpose && zone.Purpose.StartsWith("Zone set switch"), $"{zone.PlacedName}: {zone.Purpose}");
            Check(!map.Properties.Any(p => p.Label.Equals("Extents", StringComparison.OrdinalIgnoreCase) || p.Label.Equals("Forward", StringComparison.OrdinalIgnoreCase)), "no raw extents / forward rows (the size is edited above)");
            Shot("volume-purpose");
        }
        _vm.BottomPanel = "activity";
        Check(_vm.Activity.Count > 0 && _vm.Activity.Any(a => a.Text.Contains("new squad")), $"Activity: {_vm.Activity.Count} lines, last: {_vm.Activity.LastOrDefault()?.Text}");
        await Pause(600);
        Shot("activity");
        foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
    }

    /// <summary>
    /// Mission scripts in Kalo Script on the m10 copy (cprobe): the view, hover help and autocomplete from the game's
    /// function list and the map's names, checks, Ctrl+click to the map, HaloScript view and back, a save that the host
    /// reads back as HaloScript, and the inspector's "Used by scripts".
    /// </summary>
    private async Task MissionKaloScript()
    {
        const string probe = @"levels\mods\_kalo_tour\cprobe\cprobe.scenario";
        if (!Check(File.Exists(System.IO.Path.Combine(_vm.Paths!.WorkspaceTags, probe)), "the campaign probe map exists")) return;
        var map = await _vm.OpenMapAsync(probe);
        var own = map.MissionScripts.FirstOrDefault(s => s.IsOwn && s.Name == "m10");
        if (!Check(own != null, "the map has its own m10 script")) return;
        var clock = Stopwatch.StartNew();
        var doc = await _vm.OpenMissionScriptAsync(own!.Path, map.Path);
        Say($"opened in {clock.ElapsedMilliseconds} ms: {doc.Status}");
        Check(doc.IsKaloScript && doc.Text.Contains("dormant barn_skirm_init_start:") && doc.Text.Contains("ai_place(barn_skirm_init)"), "shown in Kalo Script (dormant barn_skirm_init_start:, ai_place(barn_skirm_init))");
        var until = DateTime.UtcNow.AddSeconds(60);
        while (doc.Names == null && DateTime.UtcNow < until) await Task.Delay(250);
        Check(doc.Names != null && doc.Names.TryGetValue("squad", out var squads) && squads.Contains("barn_skirm_init"), $"the map's names are known ({doc.Names?.Sum(n => n.Value.Count) ?? 0} names, {doc.Others?.Count ?? 0} declarations in the other files)");
        Check(doc.CheckOk, "no errors in the converted script: " + doc.Summary);
        var hover = doc.Hover("    ai_place(barn_skirm_init)", 6);
        Check(hover != null && hover.Text.Contains("places the specified squad"), "hover on ai_place: " + hover?.Signature + " · " + hover?.Text);
        var hoverName = doc.Hover("    ai_place(barn_skirm_init)", 16);
        Check(hoverName != null && hoverName.Title.Contains("squad"), "hover on barn_skirm_init: " + hoverName?.Title);
        var (_, items) = doc.Complete("    ai_pla");
        Check(items.Any(i => i.Text == "ai_place"), $"autocomplete ai_pla: {string.Join(", ", items.Take(5).Select(i => i.Text))}");
        var (_, names) = doc.Complete("    ai_place(barn_sk");
        Check(names.Any(i => i.Text == "barn_skirm_init" && i.Kind == "squad"), $"autocomplete barn_sk: {string.Join(", ", names.Take(5).Select(i => i.Text + " (" + i.Kind + ")"))}");
        // A typo: a squad that doesn't exist, a function with too few values.
        string text = doc.Text;
        doc.Text = text.Replace("ai_place(barn_skirm_init)", "ai_place(barn_skirm_typo)").Replace("wake(md_motion_tracker)", "wake()");
        doc.Check();
        Check(doc.Messages.Any(m => m.Message.Contains("barn_skirm_typo")), "a squad name that isn't in the map is flagged: " + doc.Messages.FirstOrDefault(m => m.Message.Contains("barn_skirm_typo"))?.Message);
        Check(doc.Messages.Any(m => m.Message.StartsWith("wake takes")), "wake() without a script is an error: " + doc.Messages.FirstOrDefault(m => m.Message.StartsWith("wake"))?.Message);
        doc.SelectedMessage = doc.Messages.FirstOrDefault(m => m.Message.Contains("barn_skirm_typo"));
        await Pause(1000);
        Shot("mission-check");
        doc.Text = text;
        doc.Check();
        // Ctrl+click on a squad name: the map selects its spawn points.
        bool went = await doc.GoToNameAsync("barn_skirm_init");
        await Pause(1500);
        Check(went && map.Selection.Count > 0 && map.Selection.All(i => i.IsAiSpawnPoint), $"Ctrl+click barn_skirm_init selects its spawn point in the map ({map.Selection.Count})");
        Shot("mission-goto");
        // Used by scripts.
        var until2 = DateTime.UtcNow.AddSeconds(60);
        while (map.ScriptUses.Count == 0 && DateTime.UtcNow < until2) await Task.Delay(250);
        Check(map.ScriptUses.Count > 0, $"inspector: used by {map.ScriptUses.Count} script lines, e.g. {map.ScriptUses.FirstOrDefault()?.Where} {map.ScriptUses.FirstOrDefault()?.Text}");
        // HaloScript view and back.
        _vm.ActiveDocument = doc;
        doc.SwitchViewCommand.Execute(null);
        Check(!doc.IsKaloScript && doc.Text.Contains("(script dormant barn_skirm_init_start"), "HaloScript view");
        doc.SwitchViewCommand.Execute(null);
        Check(doc.IsKaloScript && !doc.IsDirty, "back to Kalo Script, nothing changed");
        // Save a small change; the host reads HaloScript back.
        doc.Text = doc.Text.Replace("dormant barn_skirm_init_start:", "dormant barn_skirm_init_start:\n    # kalo tour was here");
        await doc.SaveDocumentAsync();
        var back = await _vm.Host!.ReadMissionScriptAsync(own.Path);
        Check(back.Text != null && back.Text.Contains("; kalo tour was here") && back.Text.Contains("(script dormant barn_skirm_init_start"), "saved as HaloScript (the host reads the comment back)");
        doc.Text = doc.Text.Replace("\n    # kalo tour was here", "");
        await doc.SaveDocumentAsync();
        await Pause(800);
        Shot("mission-script");
        foreach (var t in _vm.Tabs.ToList()) { t.IsDirty = false; await _vm.CloseTabCommand.ExecuteAsync(t); }
    }

    private bool Check(bool ok, string what)
    {
        if (!ok) _failures++;
        Say((ok ? "ok    " : "FAIL  ") + what);
        return ok;
    }

    private void Say(string message)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss} {_clock.Elapsed.TotalSeconds,6:F1}s] {message}");
        Console.Out.Flush();
    }
}
