using System.Diagnostics;
using Kalo.Core;
using Kalo.Protocol;
using Kalo.Core.Scripting;

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
    case "map-report":
        await MapReport(argsList.ElementAtOrDefault(1) ?? @"levels\mods\test_project\ulti\ulti.scenario");
        break;
    case "materials-report":
        await MaterialsReport(argsList.ElementAtOrDefault(1) ?? @"levels\multi\forge_halo\forge_halo.scenario");
        break;
    case "megalo-catalog":
        await MegaloCatalogDump(argsList.ElementAtOrDefault(1) ?? Path.Combine(paths.KaloData, "megalo-catalog.json"));
        break;
    case "megalo-bin":
        await MegaloBin(argsList.ElementAtOrDefault(1) ?? "slayer.txt", argsList.Skip(2).ToList());
        break;
    case "kscript-roundtrip":
        await KscriptRoundTrip(argsList.ElementAtOrDefault(1));
        break;
    case "kscript-guide":
        await KscriptGuide();
        break;
    case "kscript-starter":
        await KscriptStarter(argsList.ElementAtOrDefault(1));
        break;
    case "fsb-info":
        FsbInfo(argsList.ElementAtOrDefault(1) ?? "", int.Parse(argsList.ElementAtOrDefault(2) ?? "20"));
        break;
    case "sound-wav":
        SoundWav(argsList.ElementAtOrDefault(1) ?? @"sound\dialog\multiplayer\capture_the_flag\flag_captured.sound");
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

// A map's placements: per palette entry, whether its tag exists, its render model, and whether the model exports.
async Task MapReport(string scenario)
{
    await using var host = await HostClient.StartAsync(paths, line => Console.WriteLine("  host> " + line));
    var data = await host.GetPlacementsAsync(scenario);
    var models = new Kalo.Core.Geometry.ModelLibrary(paths);
    foreach (var c in data.Categories ?? new())
    {
        if ((c.Items?.Count ?? 0) == 0) continue;
        Console.WriteLine($"== {c.Name}: {c.Items!.Count} placed");
        for (int i = 0; i < (c.Palette?.Count ?? 0); i++)
        {
            var p = c.Palette![i];
            int used = c.Items.Count(x => x.Type == i);
            if (used == 0) continue;
            bool exists = p.Tag != null && File.Exists(Path.Combine(paths.WorkspaceTags, p.Tag));
            string model = "-";
            if (p.RenderModel != null)
            {
                await models.LoadAsync(host, new[] { p.RenderModel });
                var m = models.Get(p.RenderModel);
                model = m == null ? "EXPORT FAILED" : $"{m.TriangleCount} tris";
            }
            Console.WriteLine($"  [{i}] x{used} {p.Tag} exists={exists} render={p.RenderModel ?? "NONE"} {model}");
        }
        foreach (var it in c.Items.Take(40))
            Console.WriteLine($"    #{it.Index} type {it.Type} at {string.Join(", ", (it.Position ?? new float[0]).Select(v => v.ToString("0.##")))} scale {it.Scale} folder {it.Folder}");
    }
    foreach (var sp in data.SpawnPoints ?? new()) Console.WriteLine($"  start location at {string.Join(", ", sp.Position!.Select(v => v.ToString("0.##")))}");
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

async Task MegaloCatalogDump(string output)
{
    await using var host = await HostClient.StartAsync(paths, line => Console.WriteLine("  host> " + line));
    var cat = await host.MegaloCatalogAsync(paths.MegaloObjectLists);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(cat, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Catalog written to {output}: {cat.Actions?.Count} actions, {cat.Conditions?.Count} conditions");
    foreach (var c in cat.Conditions ?? new()) Console.WriteLine($"condition {c.Name}: {c.Description}");
    foreach (var a in cat.Actions ?? new()) Console.WriteLine($"action {a.Name}: {a.Description}");
    Check("catalog dumped", cat.Actions?.Count > 0);
}

/// <summary>Compiles a script and writes the compiler's serialized variant (.bin), then shows its chunks next to an MCC variant's.</summary>
async Task MegaloBin(string file, List<string> compare)
{
    await using var host = await HostClient.StartAsync(paths, line => Console.WriteLine("  host> " + line));
    string source = Path.Combine(paths.MegaloSource, file);
    string text = File.ReadAllText(source);
    string bin = Path.Combine(paths.KaloData, "megalo-bin", Path.GetFileNameWithoutExtension(file) + ".bin");
    var r = await host.MegaloCompileAsync(new MegaloCompileParams { ObjectLists = paths.MegaloObjectLists, IncludeFolder = paths.MegaloSource, Text = text, BinFile = bin });
    Console.WriteLine($"Compiled {file}: succeeded={r.Succeeded}; files: {string.Join(", ", r.OutputFiles ?? new())}");
    foreach (var kv in r.Stats ?? new()) Console.Write($"{kv.Key}={kv.Value} ");
    Console.WriteLine();
    Check("the compiler produced a .bin", File.Exists(bin));
    foreach (string f in new[] { bin }.Concat(compare))
    {
        if (!File.Exists(f)) { Console.WriteLine("missing " + f); continue; }
        byte[] d = File.ReadAllBytes(f);
        Console.WriteLine($"== {f}: {d.Length} bytes");
        Console.WriteLine("   head: " + Convert.ToHexString(d, 0, Math.Min(96, d.Length)));
        int i = 0;
        while (i + 12 <= d.Length)
        {
            string tag = System.Text.Encoding.ASCII.GetString(d, i, 4);
            int size = (d[i + 4] << 24) | (d[i + 5] << 16) | (d[i + 6] << 8) | d[i + 7];
            if (size <= 0 || !tag.All(ch => ch >= 32 && ch < 127)) { Console.WriteLine($"   no chunk at {i} ({tag})"); break; }
            Console.WriteLine($"   chunk {tag} at {i}: {size} bytes, version {Convert.ToHexString(d, i + 8, 4)}");
            i += size;
        }
    }
}

/// <summary>
/// Every Bungie gametype script: compile it, convert it to Kalo Script, turn that back into Megalo, compile again,
/// and compare what the compiler built (triggers, conditions, actions, encoded size).
/// </summary>
async Task KscriptRoundTrip(string? only)
{
    await using var host = await HostClient.StartAsync(paths, line => { if (!line.Contains("Megalo")) return; Console.WriteLine("  host> " + line); });
    var files = Directory.EnumerateFiles(paths.MegaloSource, "*.txt", SearchOption.TopDirectoryOnly)
        .Concat(Directory.EnumerateFiles(Path.Combine(paths.MegaloSource, "simple"), "*.txt", SearchOption.AllDirectories))
        .Where(f => only == null || f.Contains(only, StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f).ToList();
    int same = 0, differ = 0, skipped = 0, failed = 0;
    string outDir = Path.Combine(paths.KaloData, "kscript-roundtrip");
    Directory.CreateDirectory(outDir);
    async Task<MegaloCompileResult> Compile(string text) => await host.MegaloCompileAsync(new MegaloCompileParams { ObjectLists = paths.MegaloObjectLists, IncludeFolder = paths.MegaloSource, Text = text });
    string Key(MegaloCompileResult r) => string.Join(" ", new[] { "TriggersUsed", "ConditionsUsed", "ActionsUsed", "EncodedSize" }.Select(k => $"{k[..4]}={r.Stats?.GetValueOrDefault(k)}"));
    foreach (string f in files)
    {
        string rel = Path.GetRelativePath(paths.MegaloSource, f);
        string text = File.ReadAllText(f);
        var original = await Compile(text);
        if (!original.Succeeded) { skipped++; continue; } // includes and broken scripts do not compile alone
        string ks = KaloScript.FromMegalo(text);
        var t = KaloScript.Transpile(ks);
        string safe = rel.Replace('\\', '_');
        File.WriteAllText(Path.Combine(outDir, safe + KaloScript.Extension), ks);
        if (!t.Succeeded)
        {
            failed++;
            Console.WriteLine($"  FAIL {rel}: Kalo Script errors: {string.Join("; ", t.Errors.Take(3))}");
            continue;
        }
        var back = await Compile(t.Megalo);
        if (!back.Succeeded)
        {
            failed++;
            File.WriteAllText(Path.Combine(outDir, safe + ".megalo.txt"), t.Megalo);
            var m = back.Messages?.FirstOrDefault(x => x.Severity?.Contains("rror") == true) ?? back.Messages?.FirstOrDefault();
            Console.WriteLine($"  FAIL {rel}: does not compile back: Megalo line {m?.Line} (Kalo Script line {t.SourceLine(m?.Line ?? 0)}): {m?.Message} '{m?.Token}'");
            continue;
        }
        if (Key(original) == Key(back)) same++;
        else { differ++; Console.WriteLine($"  DIFF {rel}: {Key(original)}  vs  {Key(back)}"); }
    }
    Console.WriteLine($"Round trip: {same} identical, {differ} different, {failed} failed, {skipped} skipped (do not compile alone) of {files.Count}");
    Check("every compiling script converts and compiles back", failed == 0);
    Check("the compiled result is the same size and shape", differ == 0);
}

/// <summary>Compiles a Kalo Script file (default: Kalo's starter), plays it through Megalo and exports the MCC .bin.</summary>
async Task KscriptStarter(string? file)
{
    await using var host = await HostClient.StartAsync(paths, line => Console.WriteLine("  host> " + line));
    string text = file != null ? File.ReadAllText(file) : KaloScript.Starter;
    var t = KaloScript.Transpile(text);
    Check("Kalo Script turns into Megalo", t.Succeeded);
    foreach (var e in t.Errors) Console.WriteLine("  " + e);
    Console.WriteLine(t.Megalo);
    string bin = Path.Combine(paths.KaloData, "megalo-bin", "kscript_starter.bin");
    var r = await host.MegaloCompileAsync(new MegaloCompileParams { ObjectLists = paths.MegaloObjectLists, IncludeFolder = paths.MegaloSource, Text = t.Megalo, BinFile = bin });
    foreach (var m in r.Messages ?? new()) Console.WriteLine($"  {m.Severity} Megalo line {m.Line} (Kalo Script line {t.SourceLine(m.Line)}): {m.Message} '{m.Token}'");
    Check("HREK's compiler accepts it", r.Succeeded);
    if (!File.Exists(bin)) return;
    byte[] mcc = GameVariantFile.ToMcc(File.ReadAllBytes(bin), "Kalo starter", "Made with Kalo");
    string outFile = Path.Combine(paths.Repo, "exports", "gametypes", "kalo_starter_test.bin");
    Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
    File.WriteAllBytes(outFile, mcc);
    Console.WriteLine($"MCC file: {outFile}, {mcc.Length} bytes, chunks {string.Join(" ", GameVariantFile.Chunks(mcc).Select(c => c.Tag))}");
    Check("the MCC file's hash is valid", GameVariantFile.HashIsValid(mcc));
    if (paths.Mcc is { } mccDir)
    {
        string vanilla = Path.Combine(mccDir, "haloreach", "game_variants", "slayer_054.bin");
        if (File.Exists(vanilla)) Check("MCC's own slayer variant reads the same way (hash valid)", GameVariantFile.HashIsValid(File.ReadAllBytes(vanilla)));
    }
}

/// <summary>Every complete example in the documentation compiles with HREK's compiler.</summary>
async Task KscriptGuide()
{
    await using var host = await HostClient.StartAsync(paths, line => { });
    var cat = await host.MegaloCatalogAsync(paths.MegaloObjectLists);
    var sections = KaloScriptGuide.Build(cat);
    Console.WriteLine($"{sections.Count} pages, {sections.Sum(s => s.Blocks.Count)} blocks");
    int n = 0, bad = 0;
    foreach (var s in sections)
        foreach (var b in s.Blocks.Where(b => b.Kind == GuideKind.Program))
        {
            n++;
            var t = KaloScript.Transpile(b.Text);
            if (!t.Succeeded) { bad++; Console.WriteLine($"  {s.Title}: Kalo Script errors: {string.Join("; ", t.Errors)}"); continue; }
            var r = await host.MegaloCompileAsync(new MegaloCompileParams { ObjectLists = paths.MegaloObjectLists, IncludeFolder = paths.MegaloSource, Text = t.Megalo });
            if (!r.Succeeded)
            {
                bad++;
                var m = r.Messages?.FirstOrDefault(x => x.Severity?.Contains("rror") == true);
                Console.WriteLine($"  {s.Title}: does not compile: line {t.SourceLine(m?.Line ?? 0)}: {m?.Message} '{m?.Token}'\n{b.Text}");
            }
        }
    Console.WriteLine($"{n} examples, {bad} failing");
    Check("every documentation example compiles", bad == 0);
    var names = (cat.Actions ?? new()).Select(a => a.Name!).Concat((cat.Conditions ?? new()).Select(c => c.Name!)).ToList();
    var missing = names.Where(x => !MegaloDocs.Actions.ContainsKey(x) && !MegaloDocs.Conditions.ContainsKey(x)).ToList();
    Console.WriteLine("without help: " + string.Join(", ", missing));
    Check("every action and condition has help", missing.Count == 0);
}

// ---- fsb-info: what is in an FMOD sound bank (for the sound preview) ----------------------------------------
static void FsbInfo(string path, int show)
{
    using (var s = File.OpenRead(path))
    {
        var head = new byte[96];
        s.Read(head, 0, head.Length);
        Console.WriteLine("first bytes: " + BitConverter.ToString(head).Replace("-", " "));
        // Several banks glued together? List where "FSB5" appears (first 2 MB, then every 64 MB sampled).
        var buf = new byte[1 << 21];
        s.Position = 0;
        int n = s.Read(buf, 0, buf.Length);
        var hits = new List<long>();
        for (int i = 0; i + 4 <= n; i++) if (buf[i] == 'F' && buf[i + 1] == 'S' && buf[i + 2] == 'B' && buf[i + 3] == '5') hits.Add(i);
        Console.WriteLine($"FSB5 marks in the first 2 MB: {hits.Count} at {string.Join(", ", hits.Take(10))}; file {s.Length:N0} bytes");
    }
    var fsb = Kalo.Core.Audio.Fsb5.Read(path);
    Console.WriteLine($"version {fsb.Version}, codec {fsb.Codec}, {fsb.Samples.Count} sounds, data at {fsb.DataStart:N0}");
    foreach (var x in fsb.Samples.Take(show))
        Console.WriteLine($"  #{x.Index} {x.Name ?? "(no name)"}  {x.Frequency} Hz  {x.Channels} ch  {x.SampleCount} samples  at {x.DataOffset:N0} size {x.DataSize:N0}  chunks {string.Join(",", x.Chunks.Select(c => $"{c.Type}:{c.Size}"))}");
    var last = fsb.Samples.LastOrDefault();
    if (last != null) Console.WriteLine($"  last #{last.Index} {last.Name} at {last.DataOffset:N0} size {last.DataSize:N0}");
}

// ---- sound-wav: a sound tag's audio from the game's FMOD banks, decoded to .kalo\cache\sounds ------------------
void SoundWav(string tag)
{
    var lib = new Kalo.Core.Audio.SoundLibrary(new[] { Path.Combine(paths.Mcc ?? "", "haloreach", "fmod", "pc"), Path.Combine(paths.Hrek, "fmod", "pc") },
        Path.Combine(paths.KaloData, "cache", "sounds"));
    Console.WriteLine("banks: " + lib.Banks);
    var sw = Stopwatch.StartNew();
    var found = lib.Find(tag);
    Check($"{tag} is in a bank ({found?.Bank} #{found?.Index} {found?.Source}, {sw.ElapsedMilliseconds} ms)", found != null);
    if (found == null) return;
    sw.Restart();
    string wav = lib.Wav(found.Value.Bank, found.Value.Index);
    long size = new FileInfo(wav).Length;
    using var f = File.OpenRead(wav);
    var head = new byte[44];
    f.Read(head, 0, 44);
    int rate = BitConverter.ToInt32(head, 24), channels = BitConverter.ToInt16(head, 22);
    double seconds = (size - 44) / 2.0 / channels / rate;
    Check($"decoded to {wav}: {channels} ch {rate} Hz {seconds:0.00} s ({sw.ElapsedMilliseconds} ms)", seconds > 0.02 && seconds < 60);
}
