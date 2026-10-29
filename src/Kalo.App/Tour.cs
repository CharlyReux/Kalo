using System.Diagnostics;
using System.Numerics;
using Kalo.App.ViewModels;
using Kalo.App.Views;
using Kalo.Protocol;

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
        try
        {
            await Task.WhenAny(_vm.Ready, Task.Delay(TimeSpan.FromSeconds(60)));
            if (Check(_vm.Host != null, "Blam Host is running"))
            {
                await Section("blocks", Blocks);
                await Section("map", Map);
                await Section("script", Script);
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
        Say("== " + name);
        try { await body(); }
        catch (Exception ex) { Check(false, name + " threw: " + ex.Message); }
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

    private async Task Blocks()
    {
        const string weapon = @"objects\weapons\rifle\assault_rifle\assault_rifle.weapon";
        var tab = await _vm.OpenTagAsync(weapon);
        if (!Check(tab is { IsLoading: false }, "opened the assault rifle")) return;
        var block = tab!.FindField("Block:magazines");
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
        await _vm.CloseTabCommand.ExecuteAsync(tab); // unsaved: the file on disk is untouched
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
        // The view a player gets at the first spawn point, to compare with the game.
        map.ViewFromSpawnCommand.Execute(null);
        Say($"spawn view from {map.Spawn?.Position} facing {map.Spawn?.Facing}");
        await Pause(1000);
        Check(await map.CaptureViewAsync(System.IO.Path.Combine(_dir, "view-spawn.png")), "captured the 3D view from the spawn point");
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
                Check(spawns > 0 && map.Items.Count == spawns, $"the empty map keeps only its {spawns} spawn points ({map.Items.Count} objects)");
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
