using System.Diagnostics;
using Kalo.App.ViewModels;
using Kalo.App.Views;

namespace Kalo.App;

/// <summary>
/// --self-test: drives the real view models through the whole beginner loop, the way a user would:
/// open a weapon, change the magazine size, save, Play, change it again while the game runs (hot reload),
/// stop, revert to vanilla. Prints one line per check and returns 0 when everything passed.
/// Screenshots are of the Kalo window only.
/// </summary>
public sealed class SelfTest
{
    private const string Weapon = @"objects\weapons\rifle\assault_rifle\assault_rifle.weapon";
    private const string MagazineField = "Block:magazines[0]/ShortInteger:rounds loaded maximum";

    private readonly MainWindow _window;
    private readonly MainViewModel _vm;
    private readonly AppArgs _args;
    private readonly string _dir;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _failures;
    private int _shot;

    public SelfTest(MainWindow window, MainViewModel vm, AppArgs args)
    {
        _window = window;
        _vm = vm;
        _args = args;
        _dir = args.SelfTest!;
    }

    public async Task<int> RunAsync()
    {
        Say("self-test started");
        try
        {
            await RunStepsAsync();
        }
        catch (Exception ex)
        {
            Check(false, "unexpected error: " + ex);
        }
        finally
        {
            if (_vm.Game?.IsRunning == true) _vm.Game.Stop();
        }
        Shot("end");
        Say(_failures == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({_failures} failed check{(_failures == 1 ? "" : "s")})");
        return _failures == 0 ? 0 : 1;
    }

    private async Task RunStepsAsync()
    {
        await Task.WhenAny(_vm.Ready, Task.Delay(TimeSpan.FromSeconds(60)));
        if (!Check(_vm.Host != null, "Blam Host is running")) return;
        if (!Check(_vm.Paths != null && _vm.Baseline != null, "workspace and baseline loaded")) return;
        var paths = _vm.Paths!;
        string hrekFile = Path.Combine(paths.HrekTags, Weapon);
        var hrekBefore = Stamp(hrekFile);

        // A clean start: if an earlier run left the weapon modified, put it back first.
        var tab = await _vm.OpenTagAsync(Weapon);
        if (!Check(tab != null && !tab.IsLoading && tab.Fields.Count > 0, "opened " + Weapon)) return;
        if (tab!.DiffersFromVanilla)
        {
            Say("weapon was already modified, reverting it first");
            await tab.RevertToVanillaCommand.ExecuteAsync(null);
        }
        var field = tab.FindField(MagazineField);
        if (!Check(field != null, "found the magazine size field")) return;
        tab.Reveal(field!);
        string original = field!.Value ?? "";
        Say($"magazine size is {original}");
        await Settle();
        Shot("opened");

        // 1. Edit and save.
        if (!await EditAsync(tab, field, "99")) return;
        Check(tab.IsDirty, "tab is marked unsaved after the edit");
        await tab.SaveCommand.ExecuteAsync(null);
        Check(!tab.IsDirty, "saved");
        Check(tab.DiffersFromVanilla, "tag is marked as changed from vanilla");
        await _vm.RefreshChangesAsync();
        Check(_vm.Changes.Any(c => c.Path.EndsWith("assault_rifle.weapon", StringComparison.OrdinalIgnoreCase)), "Changes panel lists the weapon");
        Shot("saved");

        // 2. Play.
        if (_vm.SelectedScenario == null) { Check(false, "a map is selected"); return; }
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string>? reloaded = null; // armed right before the save that should reload
        _vm.Game!.Loaded += () => loaded.TrySetResult();
        _vm.Game.Exited += _ => { exited.TrySetResult(); loaded.TrySetException(new Exception("the game exited while loading")); };
        _vm.Game.TagReloaded += t => { if (t.Contains("assault_rifle", StringComparison.OrdinalIgnoreCase)) reloaded?.TrySetResult(t); };

        var sw = Stopwatch.StartNew();
        _vm.PlayCommand.Execute(null);
        if (!Check(_vm.IsGameRunning, "Play started reach_tag_test on " + _vm.SelectedScenario)) return;
        await Settle(4000);
        Shot("game-loading");
        bool mapLoaded = await Within(loaded.Task, TimeSpan.FromMinutes(6));
        if (!Check(mapLoaded, $"map loaded in the game ({sw.Elapsed.TotalSeconds:F0} s)")) return;
        await Task.Delay(TimeSpan.FromSeconds(8)); // let the game settle into the round

        // 3. Edit while the game runs: the game should reload the saved tag.
        if (!await EditAsync(tab, field, "7")) return;
        reloaded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        sw.Restart();
        await tab.SaveCommand.ExecuteAsync(null);
        bool reloadSeen = await Within(reloaded.Task, TimeSpan.FromSeconds(30));
        Check(reloadSeen, reloadSeen ? $"game hot-reloaded the weapon {sw.ElapsedMilliseconds} ms after the save" : "game hot-reloaded the weapon");
        await Settle(1000);
        Shot("hot-reloaded");

        Say($"holding the game open for {_args.SelfTestHold:F0} s");
        await Task.Delay(TimeSpan.FromSeconds(_args.SelfTestHold));

        // 4. Stop.
        _vm.StopGameCommand.Execute(null);
        bool stopped = await Within(exited.Task, TimeSpan.FromSeconds(30));
        Check(stopped, "Stop closed the game");
        await Settle(1000);
        Check(!_vm.IsGameRunning, "Kalo shows the game as stopped");

        // 5. Revert to vanilla.
        await tab.RevertToVanillaCommand.ExecuteAsync(null);
        await _vm.RefreshChangesAsync();
        var after = tab.FindField(MagazineField);
        Check(after?.Value == original, $"revert restored the magazine size ({after?.Value} = {original})");
        Check(!tab.DiffersFromVanilla, "tag no longer differs from vanilla");
        Check(!_vm.Changes.Any(c => c.Path.EndsWith("assault_rifle.weapon", StringComparison.OrdinalIgnoreCase)), "Changes panel no longer lists the weapon");
        if (after != null) tab.Reveal(after);
        await Settle();
        Shot("reverted");

        // 6. HREK untouched.
        Check(Stamp(hrekFile) == hrekBefore, "HREK's copy of the weapon was not touched");
        Check(_vm.ErrorCount == 0, $"no errors in the Problems panel ({_vm.ErrorCount})");
    }

    private async Task<bool> EditAsync(TagEditorViewModel tab, FieldViewModel field, string value)
    {
        field.Value = value; // what the text box does when it loses focus
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && field.Error == null && !(field.Value == value && field.IsDirty)) await Task.Delay(50);
        return Check(field.Error == null && field.Value == value, $"set magazine size to {value}" + (field.Error != null ? ": " + field.Error : ""));
    }

    private static async Task<bool> Within(Task task, TimeSpan timeout)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        return done == task && task.IsCompletedSuccessfully;
    }

    private static Task Settle(int ms = 800) => Task.Delay(ms);

    private static string Stamp(string file)
    {
        var info = new FileInfo(file);
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
    }

    private void Shot(string name)
    {
        _shot++;
        _window.SaveShot(Path.Combine(_dir, $"{_shot:D2}-{name}.png"));
    }

    private bool Check(bool ok, string what)
    {
        if (!ok) _failures++;
        Say((ok ? "ok    " : "FAIL  ") + what);
        return ok;
    }

    private void Say(string message)
    {
        Console.WriteLine($"[{_clock.Elapsed.TotalSeconds,6:F1}s] {message}");
        Console.Out.Flush();
    }
}
