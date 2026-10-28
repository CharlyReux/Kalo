using System.Diagnostics;
using Kalo.Core;
using Kalo.Protocol;

// kalo-cli <command>
//   host-test   start the Blam Host, open a weapon tag, edit, save as a test copy, check, clean up
//   changes     list files in the shared workspace that differ from vanilla
//   play-test   launch the game, wait for the map, save a tag, wait for the hot reload, revert, close

var argsList = args.ToList();
string command = argsList.FirstOrDefault() ?? "";
var paths = KaloPaths.Discover();
Console.WriteLine($"Kalo folder: {paths.Repo}");
Console.WriteLine($"Workspace:   {paths.Workspace}");
Console.WriteLine($"HREK:        {paths.Hrek}");

int failures = 0;
void Check(string what, bool ok)
{
    Console.WriteLine($"  [{(ok ? "x" : " ")}] {what}");
    if (!ok) failures++;
}

const string Weapon = @"objects\weapons\rifle\assault_rifle\assault_rifle.weapon";
const string TestCopy = @"objects\weapons\rifle\assault_rifle\assault_rifle_kalo_cli_test.weapon";
const string Magazine = "Block:magazines[0]/ShortInteger:rounds loaded maximum";

switch (command)
{
    case "host-test":
        await HostTest();
        break;
    case "changes":
        Changes();
        break;
    case "play-test":
        await PlayTest(argsList.Skip(1).FirstOrDefault() ?? @"levels\multi\20_sword_slayer\20_sword_slayer");
        break;
    default:
        Console.WriteLine("usage: kalo-cli host-test | changes | play-test [scenario]");
        return 2;
}

Console.WriteLine(failures == 0 ? "RESULT: all checks passed" : $"RESULT: {failures} check(s) failed");
return failures == 0 ? 0 : 1;

async Task HostTest()
{
    var sw = Stopwatch.StartNew();
    await using var host = await HostClient.StartAsync(paths, line => Console.WriteLine("  host> " + line));
    Console.WriteLine($"Host up in {sw.ElapsedMilliseconds} ms: engine start {host.Info!.StartMs} ms, {host.Info.TagGroups} tag groups, pid {host.Info.ProcessId}");
    Check("Host tag root is the workspace", KaloPaths.IsUnder(host.Info.TagRoot, paths.Workspace));

    sw.Restart();
    TagDocument doc = await host.OpenTagAsync(Weapon);
    long roundTrip = sw.ElapsedMilliseconds;
    Console.WriteLine($"Opened {doc.Path}: {doc.FieldCount} fields, engine load {doc.LoadMs} ms, round trip incl. JSON {roundTrip} ms");
    FieldNode? mag = Find(doc.Fields, Magazine);
    Check("Magazine field found in the tree", mag != null);
    if (mag == null) return;
    Console.WriteLine($"  magazine: value {mag.Value}, kind {mag.Kind}, units '{mag.Units}', editable {mag.Editable}");
    FieldNode? real = Flatten(doc.Fields).FirstOrDefault(f => f.Kind == ValueKinds.Real && f.Value != null && f.Value.Contains('.'));
    Check("Real values use '.' as decimal separator" + (real != null ? $" ({real.Name} = {real.Value})" : ""), real == null || !real.Value!.Contains(','));
    Console.WriteLine($"  kinds: {string.Join(", ", Flatten(doc.Fields).GroupBy(f => f.Kind).Select(g => $"{g.Key} {g.Count()}"))}");

    SetFieldResult set = await host.SetFieldAsync(Weapon, Magazine, "36");
    Check($"setField returned 36 (got {set.Value})", set.Value == "36");

    string copyFile = Path.Combine(paths.WorkspaceTags, TestCopy);
    if (File.Exists(copyFile)) File.Delete(copyFile);
    SaveResult saved = await host.SaveTagAsAsync(Weapon, TestCopy);
    Check($"Saved test copy in the workspace ({saved.Ms} ms)", File.Exists(copyFile));
    Check("Nothing written to HREK", !File.Exists(Path.Combine(paths.HrekTags, TestCopy)));
    await host.CloseTagAsync(TestCopy);

    TagDocument reloaded = await host.OpenTagAsync(TestCopy);
    Check("Test copy reads back 36", Find(reloaded.Fields, Magazine)?.Value == "36");
    await host.CloseTagAsync(TestCopy);
    File.Delete(copyFile);

    TagDocument original = await host.OpenTagAsync(Weapon);
    Check("Original still 32", Find(original.Fields, Magazine)?.Value == "32");
    await host.CloseTagAsync(Weapon);

    try
    {
        await host.SetFieldAsync(Weapon, Magazine, "1");
        Check("Editing a closed tag is refused", false);
    }
    catch (HostException ex)
    {
        Check($"Editing a closed tag is refused ({ex.Message})", true);
    }

    var baseline = Baseline.Load(paths);
    var changes = baseline.Scan();
    Check($"Workspace back to vanilla ({changes.Count} differences)", changes.Count == 0);
}

void Changes()
{
    var sw = Stopwatch.StartNew();
    var baseline = Baseline.Load(paths);
    var changes = baseline.Scan();
    Console.WriteLine($"{baseline.Count} vanilla files, {changes.Count} differences, {sw.ElapsedMilliseconds} ms");
    foreach (var c in changes.Take(100)) Console.WriteLine($"  {c.Kind,-8} {c.Path}");
}

async Task PlayTest(string scenario)
{
    var baseline = Baseline.Load(paths);
    using var game = new GameSession(paths);
    var loaded = new TaskCompletionSource();
    var reloaded = new TaskCompletionSource<string>();
    int lines = 0;
    game.Output += _ => Interlocked.Increment(ref lines);
    game.Loaded += () => loaded.TrySetResult();
    game.TagReloaded += t => reloaded.TrySetResult(t);

    var sw = Stopwatch.StartNew();
    game.Start(scenario);
    Console.WriteLine($"Game started on {scenario}");
    bool isLoaded = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(6))) == loaded.Task;
    Check($"Map loaded ({sw.Elapsed.TotalSeconds:0} s, {lines} output lines)", isLoaded);
    if (!isLoaded) return;
    await Task.Delay(3000);

    await using var host = await HostClient.StartAsync(paths);
    await host.OpenTagAsync(Weapon);
    await host.SetFieldAsync(Weapon, Magazine, "60");
    sw.Restart();
    await host.SaveTagAsync(Weapon);
    bool hot = await Task.WhenAny(reloaded.Task, Task.Delay(TimeSpan.FromSeconds(30))) == reloaded.Task;
    Check($"Game hot-reloaded the saved tag ({(hot ? reloaded.Task.Result + $" after {sw.ElapsedMilliseconds} ms" : "no message in 30 s")})", hot);
    await host.CloseTagAsync(Weapon);

    string rel = Path.Combine("tags", Weapon);
    baseline.Revert(rel);
    Check("Weapon reverted to vanilla", !baseline.IsChanged(rel));
    game.Stop();
}

static FieldNode? Find(List<FieldNode>? nodes, string path) => Flatten(nodes).FirstOrDefault(n => n.Path == path);

static IEnumerable<FieldNode> Flatten(List<FieldNode>? nodes)
{
    if (nodes == null) yield break;
    foreach (var n in nodes)
    {
        yield return n;
        foreach (var c in Flatten(n.Children)) yield return c;
    }
}
