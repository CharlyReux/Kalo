using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kalo.Core;

/// <summary>
/// "Play in MCC": a local MCC mod that Kalo keeps up to date with the map and gametype being tested, so they can be
/// played in the full game (Custom Games lobby), where the map's Forge setup and Megalo labels work as in a real game.
///
/// How MCC finds local mods (MCC-Win64-Shipping.exe, checked 2026-11): %USERPROFILE%\AppData\LocalLow\MCC\Config\
/// ModManifest.txt lists folders, one per line; each mod is a folder directly inside one of them, with a ModInfo.json.
/// A multiplayer map is a description (multiplayer\&lt;name&gt;.json: MapGuid, ScenarioFile, Title, Description, Images,
/// ValidMultiplayerObjectTypes = indices into the global object type list, without which only Infection is offered)
/// plus maps\&lt;name&gt;.map. Game types are put in the mod's haloreach\game_variants folder and, when MCC has made it,
/// in the player's own game type folder (LocalFiles\&lt;id&gt;\HaloReach\GameType).
/// </summary>
public sealed class MccMod
{
    public const string ModFolderName = "Kalo Playtest";
    private readonly KaloPaths _paths;

    public MccMod(KaloPaths paths) => _paths = paths;

    /// <summary>The folder listed in MCC's ModManifest.txt (holds Kalo's mod folder).</summary>
    public string Root => Path.Combine(_paths.KaloData, "mcc-mods");
    public string ModFolder => Path.Combine(Root, ModFolderName);
    public static string MccLocalLow => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "MCC");
    public static string ManifestFile => Path.Combine(MccLocalLow, "Config", "ModManifest.txt");

    /// <summary>The map file tool.exe builds for a scenario (workspace\maps\&lt;name&gt;.map).</summary>
    public string BuiltMap(string scenario) => Path.Combine(_paths.Workspace, "maps", Path.GetFileName(scenario) + ".map");

    /// <summary>Adds Kalo's mods folder to MCC's ModManifest.txt (keeping the other lines). True if it was added now.</summary>
    public bool RegisterWithMcc()
    {
        Directory.CreateDirectory(Root);
        var lines = File.Exists(ManifestFile) ? File.ReadAllLines(ManifestFile).ToList() : new List<string>();
        if (lines.Any(l => string.Equals(l.Trim().TrimEnd('\\'), Root, StringComparison.OrdinalIgnoreCase))) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestFile)!);
        lines.RemoveAll(string.IsNullOrWhiteSpace);
        lines.Add(Root);
        File.WriteAllLines(ManifestFile, lines);
        return true;
    }

    /// <summary>
    /// Puts the built map in the mod (moved, not copied: these files are hundreds of MB), with its description, and
    /// rewrites ModInfo.json so the mod holds this map only.
    /// </summary>
    public void SetMap(string scenario, string title, int objectTypeCount)
    {
        string name = Path.GetFileName(scenario);
        string built = BuiltMap(scenario);
        if (!File.Exists(built)) throw new FileNotFoundException("The built map is missing: " + built);
        Directory.CreateDirectory(Path.Combine(ModFolder, "maps"));
        Directory.CreateDirectory(Path.Combine(ModFolder, "multiplayer"));
        // Only this map: older playtest maps would take disk space and clutter MCC's map list.
        foreach (var old in Directory.GetFiles(Path.Combine(ModFolder, "maps"), "*.map")) File.Delete(old);
        foreach (var old in Directory.GetFiles(Path.Combine(ModFolder, "multiplayer"), "*.json")) File.Delete(old);
        File.Move(built, Path.Combine(ModFolder, "maps", name + ".map"), true);

        var map = new JsonObject
        {
            ["MapGuid"] = GuidFor("map:" + scenario.ToLowerInvariant()).ToString(),
            ["ScenarioFile"] = scenario.Replace('/', '\\'),
            ["Title"] = Neutral(title),
            ["Description"] = Neutral("Built by Kalo for a playtest (" + scenario + ")."),
            ["Images"] = new JsonObject { ["Large"] = "", ["Thumbnail"] = "", ["LoadingScreen"] = "", ["TopDown"] = "" },
            ["Flags"] = new JsonArray(),
            ["InsertionPoints"] = new JsonArray(),
            // Every object type: every game type (Kalo's own included) can be picked on this map.
            ["ValidMultiplayerObjectTypes"] = new JsonArray(Enumerable.Range(0, Math.Max(1, objectTypeCount)).Select(i => (JsonNode)i).ToArray()),
        };
        File.WriteAllText(Path.Combine(ModFolder, "multiplayer", name + ".json"), map.ToJsonString(JsonOptions));
        WriteModInfo(new[] { "multiplayer/" + name + ".json" });
    }

    /// <summary>
    /// Puts MCC game type files (name, bytes) in the mod and in the player's own game types, replacing the ones Kalo
    /// put there before (kalo_*.bin). Returns the folders they went to.
    /// </summary>
    public List<string> SetGameVariants(IReadOnlyList<(string File, byte[] Data)> variants)
    {
        var folders = new List<string> { Path.Combine(ModFolder, "haloreach", "game_variants") };
        folders.AddRange(PlayerGameTypeFolders());
        foreach (string folder in folders)
        {
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.GetFiles(folder, "kalo_*.bin")) File.Delete(old);
            foreach (var (file, data) in variants) File.WriteAllBytes(Path.Combine(folder, file), data);
        }
        return folders;
    }

    /// <summary>
    /// Puts a map variant file (.mvar) in the player's own map variants (LocalFiles\&lt;id&gt;\HaloReach\Map, made when
    /// missing), where Custom Games lists it under its map. Returns the folders it went to.
    /// </summary>
    public static List<string> SetMapVariant(string file, byte[] data)
    {
        var folders = new List<string>();
        string local = Path.Combine(MccLocalLow, "LocalFiles");
        if (!Directory.Exists(local)) return folders;
        foreach (string player in Directory.GetDirectories(local))
        {
            string reach = Path.Combine(player, "HaloReach");
            if (!Directory.Exists(reach)) continue;
            string maps = Path.Combine(reach, "Map");
            Directory.CreateDirectory(maps);
            File.WriteAllBytes(Path.Combine(maps, file), data);
            folders.Add(maps);
        }
        return folders;
    }

    /// <summary>MCC's per-player Halo Reach game type folders (LocalLow\MCC\LocalFiles\&lt;id&gt;\HaloReach\GameType), when MCC has made them.</summary>
    public static IEnumerable<string> PlayerGameTypeFolders()
    {
        string local = Path.Combine(MccLocalLow, "LocalFiles");
        if (!Directory.Exists(local)) yield break;
        foreach (string player in Directory.GetDirectories(local))
        {
            string reach = Path.Combine(player, "HaloReach");
            if (!Directory.Exists(reach)) continue;
            string gameTypes = Path.Combine(reach, "GameType");
            Directory.CreateDirectory(gameTypes);
            yield return gameTypes;
        }
    }

    private void WriteModInfo(IEnumerable<string> multiplayerMaps)
    {
        string file = Path.Combine(ModFolder, "ModInfo.json");
        // Keep the mod's id across updates: MCC tells mods apart by it.
        string guid = Guid.NewGuid().ToString();
        if (File.Exists(file))
        {
            try
            {
                var old = JsonNode.Parse(File.ReadAllText(file));
                string? g = old?["ModIdentifier"]?["ModGuid"]?.GetValue<string>();
                if (Guid.TryParse(g, out _)) guid = g!;
            }
            catch (JsonException) { }
        }
        var info = new JsonObject
        {
            ["ModIdentifier"] = new JsonObject { ["ModGuid"] = guid },
            ["ModVersion"] = Version(1, 0, 0),
            ["MinAppVersion"] = Version(0, 0, 0),
            ["MaxAppVersion"] = Version(0, 0, 0),
            ["Engine"] = "HaloReach",
            ["Title"] = Neutral("Kalo playtest"),
            ["Description"] = Neutral("The map and game type you are testing in Kalo. Kalo rewrites this mod at each Play in MCC."),
            ["InheritSharedFiles"] = "FromMCC",
            ["ModContents"] = new JsonObject { ["HasBackgroundVideos"] = false, ["HasNameplates"] = false },
            ["GameModContents"] = new JsonObject
            {
                ["HasSharedFiles"] = false,
                ["HasCampaign"] = false,
                ["HasSpartanOps"] = false,
                ["HasController"] = false,
                ["MultiplayerMaps"] = new JsonArray(multiplayerMaps.Select(m => (JsonNode)m).ToArray()),
            },
        };
        File.WriteAllText(file, info.ToJsonString(JsonOptions));
    }

    /// <summary>
    /// Builds the map with HREK's tool.exe (build-cache-file &lt;scenario&gt; pc english), in the workspace, into
    /// workspace\maps\&lt;name&gt;.map. Takes minutes. Lines of output go to <paramref name="line"/>. True when the map was built.
    /// </summary>
    public async Task<bool> BuildMapAsync(string scenario, Action<string> line, CancellationToken cancel)
    {
        string tool = Path.Combine(_paths.Workspace, "tool.exe");
        if (!File.Exists(tool)) { line("tool.exe is missing from the workspace."); return false; }
        string built = BuiltMap(scenario);
        if (File.Exists(built)) File.Delete(built);
        var psi = new ProcessStartInfo(tool)
        {
            WorkingDirectory = _paths.Workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string a in new[] { "build-cache-file", scenario.Replace('/', '\\'), "pc", "english" }) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        bool success = false;
        void Out(string? l)
        {
            if (string.IsNullOrWhiteSpace(l)) return;
            if (l.Contains("successfully built cache file", StringComparison.OrdinalIgnoreCase)) success = true;
            line(l);
        }
        p.OutputDataReceived += (_, e) => Out(e.Data);
        // tool.exe writes its whole log to stderr; only errors and crashes are worth showing.
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null && (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase) || e.Data.Contains("crash", StringComparison.OrdinalIgnoreCase))
                && !e.Data.Contains("WARNING", StringComparison.Ordinal))
                line(e.Data);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(cancel); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            line("Stopped.");
            return false;
        }
        return success && File.Exists(built);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static JsonObject Neutral(string text) => new() { ["Neutral"] = text };
    private static JsonObject Version(int major, int minor, int patch) => new() { ["Major"] = major, ["Minor"] = minor, ["Patch"] = patch };

    /// <summary>A stable GUID for a name (so a map keeps its id from one build to the next).</summary>
    public static Guid GuidFor(string name)
    {
        byte[] h = MD5.HashData(Encoding.UTF8.GetBytes("kalo:" + name));
        h[6] = (byte)((h[6] & 0x0F) | 0x30);
        h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return new Guid(h);
    }
}
