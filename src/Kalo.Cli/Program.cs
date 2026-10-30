using System.Diagnostics;
using Kalo.Core;
using Kalo.Protocol;

// kalo-cli <command>
//   host-test   start the Blam Host, open a weapon tag, edit, save as a test copy, check, clean up
//   changes     list files in the shared workspace that differ from vanilla
//   play-test   launch the game, wait for the map, save a tag, wait for the hot reload, revert, close
//   dump <tag> [elements]   print a tag's field tree (paths, types, values), first N elements per block (default 2)

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
    case "megalo-test":
        await MegaloTest(argsList.ElementAtOrDefault(1) ?? "slayer.txt");
        break;
    case "block-test":
        await BlockTest();
        break;
    case "placements-test":
        await PlacementsTest(argsList.ElementAtOrDefault(1) ?? @"levels\multi\20_sword_slayer\20_sword_slayer.scenario");
        break;
    case "level-mesh-test":
        await LevelMeshTest(argsList.ElementAtOrDefault(1) ?? @"levels\multi\20_sword_slayer\20_sword_slayer.scenario");
        break;
    case "materials-report":
        await MaterialsReport(argsList.ElementAtOrDefault(1) ?? @"levels\multi\forge_halo\forge_halo.scenario");
        break;
    case "dump":
        await Dump(argsList.ElementAtOrDefault(1) ?? Weapon, int.Parse(argsList.ElementAtOrDefault(2) ?? "2"));
        break;
    default:
        Console.WriteLine("usage: kalo-cli host-test | changes | play-test [scenario] | dump <tag> [elements]");
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

async Task MegaloTest(string file)
{
    await using var host = await HostClient.StartAsync(paths, line => Console.WriteLine("  host> " + line));
    var sw = Stopwatch.StartNew();
    var cat = await host.MegaloCatalogAsync(paths.MegaloObjectLists);
    Console.WriteLine($"Catalog in {sw.ElapsedMilliseconds} ms: {cat.Actions?.Count} actions, {cat.Conditions?.Count} conditions, engine {cat.EngineBuild}, encoding {cat.EncodingVersion}, deploy dir '{cat.DeployDirectory}'");
    foreach (var kv in cat.Lists ?? new()) Console.WriteLine($"  {kv.Key}: {kv.Value.Count} e.g. {string.Join(", ", kv.Value.Take(4))}");
    foreach (var a in (cat.Actions ?? new()).Take(3)) Console.WriteLine($"  action {a.Name}: {a.Description}");
    Check("Catalog has actions and conditions", cat.Actions?.Count > 0 && cat.Conditions?.Count > 0);

    string source = Path.Combine(paths.MegaloSource, file);
    string text = File.ReadAllText(source); // detects the UTF-16 byte order mark
    string outDir = Path.Combine(paths.KaloData, "megalo-test");
    sw.Restart();
    var r = await host.MegaloCompileAsync(new MegaloCompileParams { ObjectLists = paths.MegaloObjectLists, IncludeFolder = paths.MegaloSource, Text = text, OutputFolder = outDir });
    Console.WriteLine($"Compiled {file} in {r.Ms} ms (round trip {sw.ElapsedMilliseconds} ms): succeeded={r.Succeeded}, {r.Messages?.Count} messages");
    foreach (var m in (r.Messages ?? new()).Take(15)) Console.WriteLine($"  {m.Severity} line {m.Line} pos {m.Start} '{m.Token}': {m.Message}");
    foreach (var kv in r.Stats ?? new()) Console.Write($"{kv.Key}={kv.Value} ");
    Console.WriteLine();
    foreach (var kv in r.Variables ?? new()) Console.WriteLine($"  {kv.Key}: {string.Join(", ", kv.Value.Take(8))}");
    foreach (var f in r.OutputFiles ?? new()) Console.WriteLine($"  wrote {f} ({new FileInfo(f).Length} bytes)");
    Check("slayer compiles", r.Succeeded);

    // A broken script must report an error with a line number.
    var bad = await host.MegaloCompileAsync(new MegaloCompileParams { ObjectLists = paths.MegaloObjectLists, IncludeFolder = paths.MegaloSource, Text = text + "\r\non_tick\r\n  this_is_not_an_action 3\r\nend\r\n" });
    foreach (var m in (bad.Messages ?? new()).Take(5)) Console.WriteLine($"  bad: {m.Severity} line {m.Line} pos {m.Start} '{m.Token}': {m.Message}");
    Check("broken script fails with a message", !bad.Succeeded && bad.Messages?.Count > 0);
}

async Task BlockTest()
{
    await using var host = await HostClient.StartAsync(paths, line => Console.WriteLine("  host> " + line));
    await host.OpenTagAsync(Weapon);
    var add = await host.BlockOpAsync(Weapon, "Block:magazines", BlockOps.Add);
    Console.WriteLine($"add: {add.Block?.Value}, select {add.SelectIndex}");
    Check("add makes 2 magazines", add.Block?.ElementCount == 2);
    var full = false;
    try { await host.BlockOpAsync(Weapon, "Block:magazines", BlockOps.Add); } catch (HostException ex) { full = ex.Message.Contains("full"); Console.WriteLine("  " + ex.Message); }
    Check("a full block refuses more elements", full);
    var rem = await host.BlockOpAsync(Weapon, "Block:magazines", BlockOps.Remove, 1);
    Check("remove back to 1", rem.Block?.ElementCount == 1);
    var dup = await host.BlockOpAsync(Weapon, "Block:magazines", BlockOps.Duplicate, 0);
    Check("duplicate makes 2, selects the copy", dup.Block?.ElementCount == 2 && dup.SelectIndex == 1);
    var dupMag = Find(dup.Block?.Children, "Block:magazines[1]/ShortInteger:rounds loaded maximum");
    Check("duplicate copied the magazine size (32)", dupMag?.Value == "32");
    var rem2 = await host.BlockOpAsync(Weapon, "Block:magazines", BlockOps.Remove, 1);
    Check("remove back to 1 again", rem2.Block?.ElementCount == 1);
    try { await host.BlockOpAsync(Weapon, "Block:magazines", BlockOps.Remove, 5); Check("bad index rejected", false); }
    catch (HostException ex) { Check("bad index rejected: " + ex.Message, true); }
    var doc = await host.OpenTagAsync(Weapon);
    var refs = Flatten(doc.Fields).Where(n => n.Kind == ValueKinds.Reference).Take(5).ToList();
    foreach (var r in refs) Console.WriteLine($"  ref {r.Name}: {r.Value} allowed [{string.Join(",", r.Allowed ?? new())}]");
    Check("references list their allowed tag types", refs.Any(r => r.Allowed is { Count: > 0 }));
    await host.CloseTagAsync(Weapon); // nothing saved: the file on disk is untouched
}

async Task PlacementsTest(string scenario)
{
    await using var host = await HostClient.StartAsync(paths, line => Console.WriteLine("  host> " + line));
    var sw = Stopwatch.StartNew();
    var data = await host.GetPlacementsAsync(scenario);
    Console.WriteLine($"Placements in {data.Ms} ms (round trip {sw.ElapsedMilliseconds} ms), bsps: {string.Join(", ", data.Bsps ?? new())}");
    foreach (var c in data.Categories ?? new())
    {
        Console.WriteLine($"  {c.Name}: {c.Items?.Count} placed, palette {c.Palette?.Count}");
        foreach (var e in (c.Palette ?? new()).Take(2)) Console.WriteLine($"     palette {e.Tag} → {e.RenderModel} r={e.BoundingRadius}");
        foreach (var it in (c.Items ?? new()).Take(1)) Console.WriteLine($"     [{it.Index}] type {it.Type} '{it.Name}' at {string.Join(", ", it.Position ?? Array.Empty<float>())} rot {string.Join(", ", it.Rotation ?? Array.Empty<float>())} label {it.Label}");
    }
    foreach (var sp in data.SpawnPoints ?? new()) Console.WriteLine($"  start {string.Join(", ", sp.Position ?? Array.Empty<float>())}");
    var weapons = data.Categories?.FirstOrDefault(c => c.Name == "weapons");
    Check("weapons are listed with positions", weapons?.Items?.Count > 0 && weapons.Items[0].Position?.Length == 3);
    Check("palette entries resolve a render model", data.Categories?.Any(c => c.Palette?.Any(p => p.RenderModel != null) == true) == true);
    if (weapons?.Items?.Count > 0)
    {
        var p0 = weapons.Items[0];
        var moved = await host.SetPlacementAsync(new SetPlacementParams { Path = scenario, Category = "weapons", Index = 0, Position = new[] { p0.Position![0] + 1f, p0.Position[1], p0.Position[2] } });
        Check("move a weapon 1 unit along x", Math.Abs(moved.Position![0] - (p0.Position[0] + 1f)) < 0.001f);
        var dup = await host.PlacementOpAsync(new PlacementOpParams { Path = scenario, Category = "weapons", Op = PlacementOps.Duplicate, Index = 0 });
        Check("duplicate a weapon", dup.Category?.Items?.Count == weapons.Items.Count + 1 && dup.SelectIndex == 1);
        var del = await host.PlacementOpAsync(new PlacementOpParams { Path = scenario, Category = "weapons", Op = PlacementOps.Remove, Index = 1 });
        Check("delete it again", del.Category?.Items?.Count == weapons.Items.Count);
        var pal = await host.PlacementOpAsync(new PlacementOpParams { Path = scenario, Category = "weapons", Op = PlacementOps.AddPalette, Tag = Weapon });
        var add = await host.PlacementOpAsync(new PlacementOpParams { Path = scenario, Category = "weapons", Op = PlacementOps.Add, Type = pal.SelectIndex, Position = p0.Position });
        Check("add an assault rifle placement", add.Category?.Items?.Count == weapons.Items.Count + 1 && add.Category.Items[^1].Type == pal.SelectIndex);
    }
    await host.CloseTagAsync(scenario); // not saved
}

async Task LevelMeshTest(string scenario)
{
    await using var host = await HostClient.StartAsync(paths, line => Console.WriteLine("  host> " + line));
    string file = Path.Combine(paths.KaloData, "cache", "geometry", "test.kmesh");
    var r = await host.ExportLevelMeshAsync(scenario, file);
    Console.WriteLine($"{r.Vertices:N0} vertices, {r.Triangles:N0} triangles, {r.Clusters} clusters, {r.Instances} instances in {r.Ms} ms. {r.Notes}");
    var mesh = Kalo.Core.Geometry.Mesh.TryLoad(file);
    Check("mesh file loads", mesh != null && mesh.TriangleCount == r.Triangles);
    if (mesh != null) Console.WriteLine($"bounds {mesh.Min} .. {mesh.Max}");
    if (argsList.Contains("--keep") && mesh != null)
    {
        string keep = Path.Combine(paths.KaloData, "cache", "geometry", Path.GetFileNameWithoutExtension(scenario) + ".probe.kmesh");
        File.Copy(file, keep, true);
        Console.WriteLine("kept " + keep);
    }
    Check("level has triangles", r.Triangles > 1000);
}

// Every material of a level: triangles, blend mode, color map and whether that bitmap decodes (for the textured view).
async Task MaterialsReport(string scenario)
{
    await using var host = await HostClient.StartAsync(paths, line => Console.WriteLine("  host> " + line));
    string file = Path.Combine(paths.KaloData, "cache", "geometry", "report.kmesh");
    var r = await host.ExportLevelMeshAsync(scenario, file);
    Console.WriteLine($"{r.Triangles:N0} triangles in {r.Ms} ms. {r.Notes}");
    var mesh = Kalo.Core.Geometry.Mesh.TryLoad(file);
    Check("mesh loads", mesh != null);
    if (mesh == null) return;
    var textures = new Kalo.Core.Geometry.TextureLibrary(paths);
    await textures.LoadAsync(host, mesh.Materials.SelectMany(m => m.Bitmaps()));
    var tris = new int[mesh.Materials.Count + 1];
    foreach (var g in mesh.Groups) tris[g.Material < 0 ? mesh.Materials.Count : g.Material] += g.Count / 3;
    Console.WriteLine($"{"tris",8} {"blend",-14} {"at",-2} {"tex",-5} shader -> base map");
    foreach (var (m, i) in mesh.Materials.Select((m, i) => (m, i)).OrderByDescending(x => tris[x.i]))
    {
        var img = textures.Get(m.BaseMap);
        string tex = string.IsNullOrEmpty(m.BaseMap) ? "none" : img == null ? "FAIL" : $"{img.Width}";
        Console.WriteLine($"{tris[i],8} {m.Blend,-14} {(m.AlphaTest ? "a" : ""),-2} {tex,-5} {m.Shader} -> {m.BaseMap} {(m.ScaleX != 1 || m.ScaleY != 1 ? $"x{m.ScaleX}/{m.ScaleY}" : "")}");
        if (m.IsTerrain)
            Console.WriteLine($"{"",8} terrain blend {m.BlendMap!.Bitmap} ({(textures.Get(m.BlendMap.Bitmap) != null ? "ok" : "FAIL")}): " +
                string.Join(", ", m.Layers.Select((l, n) => l == null ? $"m{n} -" : $"m{n} {Path.GetFileNameWithoutExtension(l.Bitmap)} x{l.ScaleX}/{l.ScaleY} {(textures.Get(l.Bitmap) != null ? "ok" : "FAIL")}")));
        if (m.Color.W > 0) Console.WriteLine($"{"",8} flat color {m.Color}");
    }
    Console.WriteLine($"{tris[^1],8} (no material)");
    foreach (var e in textures.LastErrors.Take(20)) Console.WriteLine("  texture error: " + e);
}

async Task Dump(string tag, int elements)
{
    await using var host = await HostClient.StartAsync(paths, line => Console.Error.WriteLine("  host> " + line));
    TagDocument doc = await host.OpenTagAsync(tag, elements);
    Console.WriteLine($"# {doc.Path}: {doc.FieldCount} fields, {doc.LoadMs} ms");
    void Walk(List<FieldNode>? nodes, int depth)
    {
        if (nodes == null) return;
        foreach (var n in nodes)
        {
            string pad = new string(' ', depth * 2);
            string extra = n.Options is { Count: > 0 } ? "  {" + string.Join("|", n.Options.Take(12)) + (n.Options.Count > 12 ? "|…" : "") + "}" : "";
            Console.WriteLine($"{pad}{n.Name} [{n.Type}/{n.Kind}] = {n.Value}{extra}    <{n.Path}>");
            if (n.Kind == ValueKinds.Block || n.Kind == ValueKinds.Array) Walk(n.Children?.Take(elements).ToList(), depth + 1);
            else Walk(n.Children, depth + 1);
        }
    }
    Walk(doc.Fields, 0);
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
