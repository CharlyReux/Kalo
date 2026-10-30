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
                await Section("docs", DocsSection);
                await Section("forge", Forge);
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
            map.CopySelection();
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
            map.CreatePrefab("kalo tour prefab");
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

    // ---- Kalo Script: the modern gametype language, compiled through Megalo -----------------------------------

    private async Task KaloScriptSection()
    {
        await _vm.EnsureMegaloCatalogAsync();
        // In memory only: nothing is written to the mod.
        var doc = new ScriptEditorViewModel(_vm, @"data\multiplayer\megalo\mods\_kalo_tour\tour.kscript") { Text = Kalo.Core.Scripting.KaloScript.Starter };
        Check(doc.IsKaloScript && doc.Badge == "kalo", "a .kscript opens as Kalo Script");
        await doc.CompileCommand.ExecuteAsync(null);
        Check(doc.CompileOk, "the starter compiles: " + doc.CompileSummary);
        Check(doc.GeneratedMegalo.Contains("trigger player") && doc.GeneratedMegalo.Contains("action set killer.score += kill_points"), "it turns into the expected Megalo");

        // Grouped conditions: a or (b and c) compiles (as (a or b) and (a or c)).
        doc.Text = Kalo.Core.Scripting.KaloScript.Starter.Replace("if killer.streak == 5 then",
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

        // An object type that doesn't exist: named, with the real one for a free camera.
        doc.Text = "for each player do\n  temp object mon = none\n  if not object_is_type(current_player, \"free_cam\") then\n    create_object(\"free_cam\", set mon, at current_player)\n  end\nend\n";
        await doc.CompileCommand.ExecuteAsync(null);
        var typeError = doc.Messages.FirstOrDefault(m => m.IsError);
        Check(!doc.CompileOk && typeError != null && typeError.Message.Contains("is not an object type") && typeError.Message.Contains("monitor"),
            $"an unknown object type is explained ({typeError?.Line}: {typeError?.Message})");
        doc.Text = doc.Text.Replace("free_cam", "monitor");
        await doc.CompileCommand.ExecuteAsync(null);
        Check(doc.CompileOk, "with \"monitor\" it compiles: " + doc.CompileSummary);

        doc.Text = Kalo.Core.Scripting.KaloScript.Starter.Replace("killer.streak += 1", "killer.streak += ");
        await doc.CompileCommand.ExecuteAsync(null);
        var first = doc.Messages.FirstOrDefault();
        int expected = Kalo.Core.Scripting.KaloScript.Starter.Split('\n').ToList().FindIndex(l => l.Contains("killer.streak += 1")) + 2;
        Check(!doc.CompileOk && first != null && first.Line >= expected - 1 && first.Line <= expected, $"a mistake is reported on the Kalo Script line ({first?.Line}: {first?.Message})");

        doc.Text = Kalo.Core.Scripting.KaloScript.Starter.Replace("player_death_get_killing_player", "player_death_get_killing_playr");
        await doc.CompileCommand.ExecuteAsync(null);
        first = doc.Messages.FirstOrDefault(m => m.IsError);
        int callLine = Kalo.Core.Scripting.KaloScript.Starter.Split('\n').ToList().FindIndex(l => l.Contains("player_death_get_killing_player")) + 1;
        Check(!doc.CompileOk && first?.Line == callLine, $"a Megalo compiler error points at the Kalo Script line ({first?.Line} vs {callLine}: {first?.Message})");

        // The Settings panel: the declarations as forms, written back into the code.
        doc.Text = Kalo.Core.Scripting.KaloScript.Starter;
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

        doc.Text = Kalo.Core.Scripting.KaloScript.Starter;
        string? bin = await doc.ExportBin();
        Check(bin != null && File.Exists(bin) && Kalo.Core.Scripting.GameVariantFile.HashIsValid(File.ReadAllBytes(bin)), "Export .bin wrote an MCC game type file: " + bin);
        Check(_vm.NewGametypeKaloScript, "new gametypes default to Kalo Script");

        // Convert a Bungie script and show both in the split view.
        var koth = _vm.Scripts.FirstOrDefault(s => s.RelativePath.EndsWith(@"megalo\juggernaut.txt", StringComparison.OrdinalIgnoreCase));
        if (koth != null)
        {
            var megalo = await _vm.OpenScriptAsync(koth.RelativePath);
            string converted = Kalo.Core.Scripting.KaloScript.FromMegalo(megalo!.Text);
            var kdoc = new ScriptEditorViewModel(_vm, @"data\multiplayer\megalo\mods\_kalo_tour\juggernaut.kscript") { Text = converted };
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
        await _vm.EnsureMegaloCatalogAsync();
        var doc = new ScriptEditorViewModel(_vm, @"data\multiplayer\megalo\mods\_kalo_tour\hover.kscript") { Text = Kalo.Core.Scripting.KaloScript.Starter };
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
        doc.Text = Kalo.Core.Scripting.KaloScript.Starter.Replace("if killer.streak == 5 then",
            "if killer.streak == 5 or (killer.score > 10 and not current_player.team == none) then");
        await doc.CompileCommand.ExecuteAsync(null);
        Check(doc.CompileOk && doc.GeneratedMegalo.Contains("condition if killer.streak == 5 or"),
            "an if with parentheses compiles: " + doc.CompileSummary);

        doc.Text = Kalo.Core.Scripting.KaloScript.Starter.Replace("killer.streak += 1", "killer.streak += ");
            bool underlined = await Settle(() => view.Underlined.Any(m => m.IsError));
            var marks = view.UnderlinedText;
            int streakAt = Line("killer.streak += 1");
            Check(underlined && marks.Any(m => m.Line == streakAt && m.Text == "+="),
                $"a Kalo Script mistake is underlined while typing, no Compile needed ({string.Join(", ", marks.Select(m => $"line {m.Line} '{m.Text}'"))})");
            doc.Text = Kalo.Core.Scripting.KaloScript.Starter.Replace("player_death_get_killing_player", "player_death_get_killing_playr");
            underlined = await Settle(() => view.UnderlinedText.Any(m => m.Text.Contains("player_death_get_killing_playr")));
            Check(underlined, $"an unknown action (found by HREK's compiler) is underlined on its word ({string.Join(", ", view.UnderlinedText.Select(m => $"line {m.Line} '{m.Text}'"))})");
            Check(view.UnderlinedText.Count(m => m.Line == callLine) == 1 && view.UnderlinedText.All(m => !m.Text.Contains("Kill Points")),
                "one underline per line, none for Bungie's localization warnings");
            Check(view.ShowHoverFor("player_death_get_killing_playr"), "pointing at it shows the error");
            await Pause(600);
            view.SaveHoverShot(System.IO.Path.Combine(_dir, $"{++_shot:D2}-error-hover.png"));
            Shot("error-underline");
            doc.Text = Kalo.Core.Scripting.KaloScript.Starter;
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
            doc.Text = Kalo.Core.Scripting.KaloScript.Starter;
            await Pause(300);
            int startLine = Line("player_death_get_killing_player(");
            var (after, open) = view.TypeThenPress(startLine, "\n    hud_post_message(everyone, flag_captured, \"x\")", Avalonia.Input.Key.Down);
            Check(after == startLine + 2 && !open, $"Down moves to the next line after typing a call ({startLine + 1} → {after}, list open: {open})");
            var (back, _) = view.TypeThenPress(after, "", Avalonia.Input.Key.Up);
            Check(back == startLine + 1, $"Up moves back ({after} → {back})");
            doc.Text = Kalo.Core.Scripting.KaloScript.Starter;
            await Pause(200);

            // Format document (Ctrl+Shift+I).
            doc.Text = "for each player do\nif player_died(current_player,enemy) then\n      temp player killer=none\n\n\n\nkiller.score+=1\nend\nend\n";
            await Pause(200);
            view.FormatDocument();
            string formatted = doc.Text.Replace("\r\n", "\n");
            Check(formatted == "for each player do\n  if player_died(current_player, enemy) then\n    temp player killer = none\n\n    killer.score += 1\n  end\nend\n",
                "Ctrl+Shift+I formats the file (indentation, spaces, blank lines): " + formatted.Replace("\n", "⏎"));
            doc.Text = Kalo.Core.Scripting.KaloScript.Starter;
            await Pause(200);
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
