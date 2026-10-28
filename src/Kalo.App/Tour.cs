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
                await Section("projects", Projects);
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
        await Pause(1500);
        Shot("map-overview");
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
        await Pause(1000);
        Say($"holding the map view for {_hold:F0} s");
        Shot("map-selected");
        await Task.Delay(TimeSpan.FromSeconds(_hold));
        await map.DeleteSelectedCommand.ExecuteAsync(null);
        Check(map.Items.Count == count, "deleted the copy");
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
        await Pause(500);
        Shot("project-switched");

        string zip = await Task.Run(() => projects.Export(p));
        Check(File.Exists(zip) && new FileInfo(zip).Length > 0, "exported " + System.IO.Path.GetFileName(zip));
        try { File.Delete(zip); } catch { }

        await _vm.SwitchProjectCommand.ExecuteAsync(null);
        Check(!_vm.Baseline!.IsChanged(rel), "back to vanilla");
        projects.Delete(projects.Find(name)!);
        _vm.RefreshProjects();
        Check(projects.ActiveName == null && projects.Find(name) == null, "cleaned up the test project");
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
