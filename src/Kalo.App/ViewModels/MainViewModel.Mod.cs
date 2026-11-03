using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// The mod-centric side of Kalo: one smart Play, "My mod" grouped by kind, and new maps / gametypes from templates.
/// </summary>
public partial class MainViewModel
{
    // ---- one Play ------------------------------------------------------------------------------------

    public ObservableCollection<GametypeChoice> GametypeChoices { get; } = new();
    [ObservableProperty] private GametypeChoice? _selectedGametype;
    [ObservableProperty] private bool _isPreparingPlay;

    /// <summary>What Play will launch, in words, for the title bar tooltip.</summary>
    public string PlaySummary => PlayIsCampaign
        ? $"Play the campaign mission {System.IO.Path.GetFileName(SelectedScenario ?? "?")} on {SelectedDifficulty?.Label ?? "Normal"}{(PlayerCount > 1 ? $", {PlayerCount} players split-screen" : "")} in reach_tag_test (F5), with its mission scripts. Ctrl+F5 in a map: walk around from the camera without the mission scripts. Unsaved changes are saved first."
        : $"Play {System.IO.Path.GetFileName(SelectedScenario ?? "?")} with {SelectedGametype?.Label ?? "its default gametype"}{(PlayerCount > 1 ? $", {PlayerCount} players split-screen" : "")} in reach_tag_test (F5). Ctrl+F5 in a map: play from the camera. Unsaved changes are saved first.";
    partial void OnSelectedDifficultyChanged(CampaignDifficulty? value) => OnPropertyChanged(nameof(PlaySummary));

    partial void OnSelectedGametypeChanged(GametypeChoice? value) { OnPropertyChanged(nameof(PlaySummary)); OnPropertyChanged(nameof(PlayInMccSummary)); }
    partial void OnSelectedScenarioChanged(string? value) { RefreshCampaignPlay(); OnPropertyChanged(nameof(PlaySummary)); OnPropertyChanged(nameof(PlayInMccSummary)); }

    private void BuildGametypeChoices()
    {
        var keep = SelectedGametype?.Path;
        GametypeChoices.Clear();
        GametypeChoices.Add(new GametypeChoice("No gametype (no weapons, no score)", null));
        foreach (var s in Scripts.Where(IsGametype).OrderBy(s => s.RelativePath.Contains(@"\mods\", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(s => s.Name))
            GametypeChoices.Add(new GametypeChoice(s.Name + (s.RelativePath.Contains(@"\mods\", StringComparison.OrdinalIgnoreCase) ? "  (my mod)" : ""), s.RelativePath));
        // Default to plain Slayer: without a gametype players spawn with no weapons, which looks like nothing works.
        SelectedGametype = (keep == null ? null : GametypeChoices.FirstOrDefault(g => g.Path == keep))
                           ?? GametypeChoices.FirstOrDefault(g => g.Path != null && System.IO.Path.GetFileName(g.Path).Equals("slayer.txt", StringComparison.OrdinalIgnoreCase))
                           ?? GametypeChoices[0];
    }

    /// <summary>Scripts that are whole gametypes (not includes or string tables).</summary>
    private static bool IsGametype(ScriptFile s) =>
        !s.RelativePath.Contains(@"\includes\", StringComparison.OrdinalIgnoreCase) && !s.RelativePath.Contains(@"\strings\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The map and gametype pickers follow the tab you are working in.</summary>
    private void FollowActiveDocument(DocumentViewModel? doc)
    {
        if (doc is MapEditorViewModel map)
        {
            if (!Scenarios.Contains(map.Scenario)) Scenarios.Insert(0, map.Scenario);
            SelectedScenario = map.Scenario;
        }
        else if (doc is ScriptEditorViewModel script)
        {
            var choice = GametypeChoices.FirstOrDefault(g => string.Equals(g.Path, script.Path, StringComparison.OrdinalIgnoreCase));
            if (choice != null) SelectedGametype = choice;
        }
    }

    // ---- players (split-screen) ---------------------------------------------------------------------

    public IReadOnlyList<PlayerChoice> PlayerChoices { get; } = new[]
    {
        new PlayerChoice(1, "1 player"), new PlayerChoice(2, "2 players"), new PlayerChoice(3, "3 players"), new PlayerChoice(4, "4 players"),
    };
    [ObservableProperty] private PlayerChoice? _selectedPlayers;
    public int PlayerCount => SelectedPlayers?.Count ?? 1;
    partial void OnSelectedPlayersChanged(PlayerChoice? value) => OnPropertyChanged(nameof(PlaySummary));

    /// <summary>
    /// Saves what is unsaved, compiles and deploys the chosen gametype (if any), then starts the chosen map.
    /// Restarts the game if it is already running.
    /// </summary>
    [RelayCommand]
    public async Task PlayAsync()
    {
        if (Game == null || SelectedScenario == null || IsPreparingPlay) return;
        IsPreparingPlay = true;
        try
        {
            foreach (var doc in Tabs.Where(t => t.IsDirty).ToList()) await doc.SaveDocumentAsync();
            // Warn (without blocking) about objects the game will delete because they are off the level.
            var openMap = Tabs.OfType<MapEditorViewModel>().FirstOrDefault(m => m.Scenario.Equals(SelectedScenario, StringComparison.OrdinalIgnoreCase));
            if (openMap?.HasOutside == true) AddProblem("warning", openMap.OutsideText, openMap.Path);
            if (await IsCampaignScenarioAsync(SelectedScenario))
            {
                // A campaign mission: no gametype; the mission's own scripts run (cutscenes, AI, objectives).
                var commands = CampaignPlayCommands(out string campaignNote);
                StartGame(SelectedScenario, commands, $"Play: {SelectedScenario}{campaignNote}. A mission takes a minute or two to load; its scripts' messages and errors show in Output (hs: …).", null, true);
                return;
            }
            var init = await PlayInitAsync();
            if (init == null) return;
            string scenario = SelectedScenario;
            var copy = await AddScriptObjectsForPlayAsync(scenario + ".scenario", null);
            if (copy != null) scenario = copy[..^".scenario".Length];
            StartGame(scenario, init.Value.Commands, $"Play: {SelectedScenario}{init.Value.Note}.");
        }
        finally
        {
            IsPreparingPlay = false;
        }
    }

    /// <summary>
    /// Play from camera (Ctrl+F5): saves, makes a throwaway copy of the map (levels\mods\_kalo_play) whose start
    /// locations are on the ground under the editor camera, and plays that copy. The map's own Start location stays.
    /// </summary>
    public async Task PlayFromAsync(MapEditorViewModel map, System.Numerics.Vector3 position, float facing)
    {
        if (Game == null || Host == null || IsPreparingPlay) return;
        IsPreparingPlay = true;
        try
        {
            foreach (var doc in Tabs.Where(t => t.IsDirty).ToList()) await doc.SaveDocumentAsync();
            if (map.IsDirty) { AddProblem("warning", "Save the map first: Play from camera plays the saved map.", map.Path); return; }
            string name = System.IO.Path.GetFileNameWithoutExtension(map.Path);
            string copy = $@"levels\mods\_kalo_play\{name}\{name}.scenario";
            if (IsGameRunning) Game.Stop();
            var r = await Host.PlacementOpAsync(new PlacementOpParams
            {
                Path = map.Path, Op = PlacementOps.MakePlayCopy, Tag = copy, Index = PlayerCount,
                Position = new[] { position.X, position.Y, position.Z, facing },
            });
            if (map.IsCampaign)
            {
                // A campaign mission from the camera: its scripts would play the intro and move the player, so they are off
                // (placed objects only, no AI); the zone set picked in the map view is loaded.
                RememberScenarioType(copy[..^".scenario".Length], "solo");
                var commands = CampaignPlayCommands(out _);
                var after = new List<string>();
                // "debug map launching: sets the initial bsp for the next map" (takes the zone set's name).
                var zs = await ZoneSetForAsync(map, position);
                if (zs != null) commands.Add("game_initial_zone_set " + zs.Name);
                StartGame(copy[..^".scenario".Length], commands,
                    $"Play from camera: {map.Scenario} at {position.X:F1}, {position.Y:F1}, {position.Z:F1}, without the mission scripts (no cutscenes, no AI){(zs != null ? ", zone set " + zs.Name : "")}. Press Play (F5) for the mission with its scripts.",
                    after, scripts: false);
                return;
            }
            var init = await PlayInitAsync();
            if (init == null) return;
            await AddScriptObjectsForPlayAsync(map.Path, copy);
            StartGame(copy[..^".scenario".Length], init.Value.Commands,
                $"Play from camera: {map.Scenario} at {position.X:F1}, {position.Y:F1}, {position.Z:F1}{init.Value.Note}. The map's own Start location is unchanged.");
        }
        catch (HostException ex)
        {
            AddProblem("error", "Play from camera: " + ex.Message, map.Path);
        }
        finally
        {
            IsPreparingPlay = false;
        }
    }

    /// <summary>Ctrl+F5: Play from camera in the map tab that is open.</summary>
    [RelayCommand]
    private async Task PlayFromCamera()
    {
        if (ActiveDocument is MapEditorViewModel map) await map.PlayFromCameraCommand.ExecuteAsync(null);
        else AddProblem("warning", "Play from camera works from a map tab: open a map and place the camera where you want to start.", "Play");
    }

    /// <summary>
    /// The console commands that load the gametype ({engine}, {bin}, {binrel} are replaced); --engine-cmd changes it for tests.
    /// "game_multiplayer megalo" makes the next map a Megalo game, and "game_set_variant" makes game_start load
    /// HotReload\&lt;engine&gt;.mglo as its variant (base settings and script). Without game_multiplayer, game_start
    /// reports "variant was not of the correct gametype" and runs with no gametype.
    /// load_binary_game_engine only works on a game that already runs a Megalo gametype, so it does nothing at startup.
    /// </summary>
    public static string EngineCommands { get; set; } = "game_multiplayer megalo;game_set_variant {engine}";

    /// <summary>Console commands run before the map starts: split-screen players and the chosen gametype. Null if the gametype failed.</summary>
    private async Task<(List<string> Commands, string Note)?> PlayInitAsync()
    {
        var commands = new List<string>();
        string note = "";
        _gametypeCreates.Clear();
        if (PlayerCount > 1)
        {
            // Reach's test build: "sets the number of multiplayer splitscreen players for the next map".
            commands.Add("game_splitscreen " + PlayerCount);
            note += $", {PlayerCount} players split-screen";
        }
        if (SelectedGametype?.Path is { } gametype)
        {
            string? engine = await DeployGametypeAsync(gametype);
            if (engine == null) return null;
            string bin = System.IO.Path.Combine(Paths!.Workspace, "maps", "megalo", engine + ".bin");
            foreach (string c in EngineCommands.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                commands.Add(c.Replace("{engine}", engine).Replace("{binrel}", @"maps\megalo\" + engine + ".bin").Replace("{bin}", bin));
            note = $" with gametype {System.IO.Path.GetFileNameWithoutExtension(gametype)}" + note;
        }
        return (commands, note);
    }

    /// <summary>Compiles a gametype script (the open tab's text if it is open) and writes maps\megalo\kalo_&lt;name&gt;.mglo. Returns the engine name.</summary>
    /// <param name="category">For MCC: the game menu folder to list it in (e.g. "community"), replacing the script's own.</param>
    public async Task<string?> DeployGametypeAsync(string relativePath, string? category = null)
    {
        if (Host == null || Paths == null) return null;
        var open = Tabs.OfType<ScriptEditorViewModel>().FirstOrDefault(t => t.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
        string source = open?.Text ?? ScriptEditorViewModel.ReadScript(System.IO.Path.Combine(Paths.Workspace, relativePath)).Text;
        string text = source;
        Kalo.Core.Scripting.TranspileResult? kalo = null;
        if (ScriptEditorViewModel.IsKaloScriptPath(relativePath))
        {
            kalo = Kalo.Core.Scripting.KaloScript.Transpile(source);
            if (!kalo.Succeeded)
            {
                var e = kalo.Errors[0];
                AddProblem("error", $"The gametype {System.IO.Path.GetFileName(relativePath)} has errors: line {e.Line}: {e.Message}", relativePath);
                if (open == null) await OpenScriptAsync(relativePath);
                return null;
            }
            text = kalo.Megalo;
        }
        int added = 0;
        if (category != null) (text, added) = Kalo.Core.Scripting.MegaloCategory.Set(text, category);
        string name = "kalo_" + System.IO.Path.GetFileNameWithoutExtension(relativePath);
        string mglo = System.IO.Path.Combine(Paths.Workspace, "maps", "megalo", name + ".mglo");
        await EnsureMegaloCatalogAsync();
        var r = await Host.MegaloCompileAsync(new MegaloCompileParams
        {
            ObjectLists = Paths.MegaloObjectLists,
            IncludeFolder = Paths.MegaloSource,
            Text = text,
            OutputFolder = System.IO.Path.Combine(Paths.KaloData, "megalo", name),
            MgloFile = mglo,
            // The whole game variant (base settings + script), as a packed BLF file.
            BinFile = System.IO.Path.ChangeExtension(mglo, ".bin")
        });
        if (r.Succeeded) _gametypeCreates = Kalo.Core.ScriptObjects.CreatedIn(text);
        if (!r.Succeeded || !File.Exists(mglo))
        {
            var first = r.Messages?.FirstOrDefault();
            int megaloLine = first == null ? 0 : Math.Max(1, first.Line - added);
            int line = first == null ? 0 : kalo?.SourceLine(megaloLine) ?? megaloLine;
            AddProblem("error", $"The gametype {System.IO.Path.GetFileName(relativePath)} does not compile" + (first != null ? $": line {line}: {first.Message}" : "."), relativePath);
            if (open == null) await OpenScriptAsync(relativePath);
            return null;
        }
        // reach_tag_test starts a gametype with "game_set_variant <name>", which opens HotReload\<name>.mglo next to the game.
        string hotReload = System.IO.Path.Combine(Paths.Workspace, "HotReload");
        Directory.CreateDirectory(hotReload);
        File.Copy(mglo, System.IO.Path.Combine(hotReload, name + ".mglo"), true);
        return name;
    }

    // ---- My mod -----------------------------------------------------------------------------------------

    public ObservableCollection<ModGroup> ModGroups { get; } = new();
    [ObservableProperty] private string _explorerTab = "mod";
    public bool IsModTab => ExplorerTab == "mod";
    public bool IsAllFilesTab => ExplorerTab == "all";
    public bool HasActiveMod => ModProjects?.ActiveName != null;
    public bool NoActiveMod => !HasActiveMod;
    public string ActiveModName => ModProjects?.ActiveName ?? "";

    partial void OnExplorerTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsModTab));
        OnPropertyChanged(nameof(IsAllFilesTab));
    }

    [RelayCommand] private void ShowModTab() { SidePanel = "explorer"; ExplorerTab = "mod"; }
    [RelayCommand] private void ShowAllFilesTab() { SidePanel = "explorer"; ExplorerTab = "all"; }

    private static readonly (string Name, string Glyph, string Hint)[] GroupInfo =
    {
        ("Maps", "\uE909", "New map…"),
        ("Gametypes", "\uE943", "New gametype…"),
        ("Mission scripts", "\uE8A5", "New campaign map…"),
        ("Painted blocks", "\uE771", "Paint blocks in a map…"),
        ("Weapons & objects", "\uE7FC", "Tweak a weapon or object…"),
        ("Sounds", "\uE767", "Change a sound…"),
        ("UI & text", "\uE8D2", "Change UI or text…"),
        ("Effects & visuals", "\uE790", "Change an effect or texture…"),
        ("Other files", "\uE8A5", "Browse all game files…"),
    };

    /// <summary>Which "My mod" group a changed file belongs to.</summary>
    public static string GroupOf(string workspacePath)
    {
        string p = workspacePath.ToLowerInvariant();
        string ext = System.IO.Path.GetExtension(p).TrimStart('.');
        if (ext == "scenario") return "Maps";
        if (ext == "scenario_hs_source_file") return "Mission scripts";
        // A painted block is three tags (object, model, render model); the group shows the object only.
        if (p.StartsWith(@"tags\levels\mods\") && p.Contains(@"\blocks\")) return ext is "model" or "render_model" ? "" : "Painted blocks";
        if (p.StartsWith(@"data\multiplayer\megalo\")) return "Gametypes";
        if (p.StartsWith(@"tags\ui\") || ext is "multilingual_unicode_string_list" || ext.StartsWith("cui_") || ext.StartsWith("chud")) return "UI & text";
        if (ext.StartsWith("sound") || ext is "sound_looping" or "sound_effect_collection" or "sound_mix" or "sound_classes") return "Sounds";
        if (ext is "weapon" or "vehicle" or "equipment" or "biped" or "scenery" or "crate" or "projectile" or "giant" or "creature" or "model" or "render_model"
            or "collision_model" or "physics_model" or "model_animation_graph" or "damage_effect" or "multiplayer_object_type_list" || ext.StartsWith("device")) return "Weapons & objects";
        if (ext is "effect" or "particle" or "bitmap" or "light" or "contrail_system" or "decal_system" || ext.StartsWith("shader") || ext.StartsWith("beam")) return "Effects & visuals";
        return "Other files";
    }

    private void BuildModTree(IReadOnlyList<WorkspaceChange> changes)
    {
        var expanded = ModGroups.Where(g => g.IsExpanded).Select(g => g.Name).ToHashSet();
        bool first = ModGroups.Count == 0;
        ModGroups.Clear();
        foreach (var (name, glyph, hint) in GroupInfo)
        {
            var entries = changes.Where(c => GroupOf(c.Path) == name)
                // A map's own helper tags (bsp, lighting...) stay out of the way: show the scenario.
                .Where(c => name != "Other files" || !changes.Any(s => s.Path.EndsWith(".scenario") && System.IO.Path.GetDirectoryName(s.Path) == System.IO.Path.GetDirectoryName(c.Path)))
                .Select(c => new ModEntry(this, c.Path, c.Kind.ToString()))
                .ToList();
            if ((name == "Other files" || name == "Mission scripts") && entries.Count == 0) continue;
            var group = new ModGroup(name, glyph, hint) { IsExpanded = first || expanded.Contains(name) || entries.Count > 0 };
            foreach (var e in entries) group.Items.Add(e);
            ModGroups.Add(group);
        }
        OnPropertyChanged(nameof(HasActiveMod));
        OnPropertyChanged(nameof(NoActiveMod));
        OnPropertyChanged(nameof(ActiveModName));
    }

    [RelayCommand]
    private async Task OpenModEntry(ModEntry? entry)
    {
        if (entry == null) return;
        if (entry.Path.StartsWith(@"data\multiplayer\megalo\", StringComparison.OrdinalIgnoreCase)) await OpenScriptAsync(entry.Path);
        else if (entry.Path.StartsWith(@"tags\", StringComparison.OrdinalIgnoreCase) && entry.Kind != "Deleted") await OpenAnyAsync(entry.Path.Substring(5));
        else await CompareWithVanillaAsync(entry.Path);
    }

    [RelayCommand]
    private async Task CompareModEntry(ModEntry? entry)
    {
        if (entry != null) await CompareWithVanillaAsync(entry.Path);
    }

    /// <summary>Takes a file out of the mod: vanilla content back (or the new file deleted).</summary>
    [RelayCommand]
    private async Task RemoveModEntry(ModEntry? entry)
    {
        if (entry == null || Baseline == null) return;
        foreach (var t in Tabs.Where(t => t.BaselinePath.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)).ToList()) await CloseTab(t);
        try
        {
            if (GroupOf(entry.Path) == "Painted blocks")
            {
                // The whole painted copy goes; a map that still uses it rebuilds it when opened.
                string dir = System.IO.Path.GetDirectoryName(entry.Path)!;
                foreach (var c in Changes.Where(c => System.IO.Path.GetDirectoryName(c.Path)!.Equals(dir, StringComparison.OrdinalIgnoreCase)).ToList())
                    Baseline.Revert(c.Path);
                Log($"Removed the painted block {System.IO.Path.GetFileNameWithoutExtension(entry.Path)}. Maps that still use it rebuild it when they open.");
            }
            else Baseline.Revert(entry.Path);
            Log($"Removed {entry.Path} from the mod (vanilla version restored).");
        }
        catch (Exception ex)
        {
            AddProblem("error", "Could not remove: " + ex.Message, entry.Path);
        }
        await RefreshChangesAsync();
        _ = AutoCaptureAsync();
    }

    /// <summary>The "New…" action of a group: forms for maps and gametypes, the game file browser for the rest.</summary>
    [RelayCommand]
    private void GroupAction(ModGroup? group)
    {
        switch (group?.Name)
        {
            case "Maps": OpenNewMapForm(); break;
            case "Gametypes": OpenNewGametypeForm(); break;
            case "Mission scripts":
                // A campaign map comes with its own copy of the mission scripts (open them from the map's Campaign bar).
                OpenNewMapForm();
                NewMapKind = "campaign";
                break;
            default:
                ShowAllFilesTab();
                SearchText = group?.Name switch
                {
                    "Weapons & objects" => "weapons .weapon",
                    "Sounds" => ".sound",
                    "UI & text" => "ui\\",
                    "Effects & visuals" => ".effect",
                    _ => "",
                };
                StatusMessage = "Open any game file and save it: it becomes part of your mod.";
                break;
        }
    }

    // ---- new map ----------------------------------------------------------------------------------------

    [ObservableProperty] private bool _isNewMapOpen;
    [ObservableProperty] private string _newMapName = "";
    [ObservableProperty] private string? _newMapBase;
    /// <summary>New map starting point: "barebone" (a platform and spawns), "empty" (a Reach level, spawns only) or "full".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewMapEmpty), nameof(NewMapFull), nameof(NewMapBarebone), nameof(NewMapCampaign), nameof(NeedsBaseLevel), nameof(BaseLevels), nameof(NewMapLevelLabel))]
    private string _newMapKind = "barebone";
    public bool NewMapCampaign { get => NewMapKind == "campaign"; set { if (value) NewMapKind = "campaign"; } }
    public string NewMapLevelLabel => NewMapCampaign ? "Mission to copy (m05 Noble Actual … m70 The Pillar of Autumn)" : "Level to build on";
    partial void OnNewMapKindChanged(string value)
    {
        // The level list follows the kind (campaign missions or multiplayer levels).
        var levels = BaseLevels.ToList();
        if (NewMapBase == null || !levels.Contains(NewMapBase))
            NewMapBase = value == "campaign" ? levels.FirstOrDefault(s => s.EndsWith(@"\m10")) ?? levels.FirstOrDefault()
                                             : levels.FirstOrDefault(s => s.EndsWith("20_sword_slayer")) ?? levels.FirstOrDefault();
    }
    public bool NewMapBarebone { get => NewMapKind == "barebone"; set { if (value) NewMapKind = "barebone"; } }
    public bool NewMapEmpty { get => NewMapKind == "empty"; set { if (value) NewMapKind = "empty"; } }
    public bool NewMapFull { get => NewMapKind == "full"; set { if (value) NewMapKind = "full"; } }
    public bool NeedsBaseLevel => NewMapKind != "barebone";

    /// <summary>Reach levels a new map can start from: multiplayer levels, or campaign missions for a campaign map.</summary>
    public IEnumerable<string> BaseLevels => Scenarios.Where(s => s.StartsWith(NewMapCampaign ? @"levels\solo\" : @"levels\multi\", StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private void OpenNewMapForm()
    {
        ShowModTab();
        IsNewGametypeOpen = false;
        NewMapBase ??= BaseLevels.FirstOrDefault(s => s.EndsWith("20_sword_slayer")) ?? BaseLevels.FirstOrDefault();
        OnPropertyChanged(nameof(BaseLevels));
        IsNewMapOpen = true;
    }

    [RelayCommand] private void CancelNewContent() { IsNewMapOpen = false; IsNewGametypeOpen = false; }

    [RelayCommand]
    private async Task CreateMap()
    {
        var map = NewMapBarebone
            ? await CreateMapAsync(NewMapName, BareboneMap.Template[..^".scenario".Length], empty: true, barebone: true)
            : await CreateMapAsync(NewMapName, NewMapBase, NewMapEmpty && !NewMapCampaign);
        if (map != null) { IsNewMapOpen = false; NewMapName = ""; }
    }

    private string ModSlug => Slug(ModProjects?.ActiveName ?? "my_mod");
    /// <summary>Folder name of the active mod under levels\mods (for tags Kalo creates for it).</summary>
    public string ModSlugForContent => ModSlug;
    public void RefreshChangesAfterEdit() => _ = RefreshChangesAsync();

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.Trim().ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        string s = sb.ToString().Trim('_');
        while (s.Contains("__")) s = s.Replace("__", "_");
        return s.Length == 0 ? "untitled" : s;
    }

    /// <summary>A new map in levels\mods\&lt;mod&gt;\&lt;map&gt;, from a Reach level (empty: spawns only; or a full copy).</summary>
    public async Task<MapEditorViewModel?> CreateMapAsync(string name, string? baseScenario, bool empty, bool barebone = false)
    {
        if (Host == null || Paths == null) return null;
        if (string.IsNullOrWhiteSpace(name) || baseScenario == null) { AddProblem("warning", "Give the map a name and pick a level to start from.", "New map"); return null; }
        if (ModProjects?.ActiveName == null) { AddProblem("warning", "Create a mod first (Project panel or start page).", "New map"); return null; }
        string slug = Slug(name);
        string path = $@"levels\mods\{ModSlug}\{slug}\{slug}.scenario";
        var tab = Tabs.FirstOrDefault(t => t.Path.Equals(baseScenario + ".scenario", StringComparison.OrdinalIgnoreCase));
        if (tab != null) await CloseTab(tab);
        try
        {
            int mapId = 7000 + (int)((uint)path.GetHashCode() % 2000);
            var r = await Host.CreateMapAsync(new CreateMapParams { Template = baseScenario + ".scenario", NewPath = path, Empty = empty, MapId = mapId, Barebone = barebone });
            ModProjects.UpdateActive(m => m.MapTemplates[path] = baseScenario + ".scenario");
            RememberScenarioType(path[..^".scenario".Length], r.Type);
            if (r.Type == "solo")
                Log($"New campaign map {path}: a full copy of the {System.IO.Path.GetFileName(baseScenario)} mission, with its own copy of {r.Scripts?.Count ?? 0} mission script(s): "
                    + string.Join(", ", (r.Scripts ?? new()).Select(System.IO.Path.GetFileNameWithoutExtension)) + ".");
            else Log(barebone
                ? $"New map {path}: a platform with {r.Kept} spawn points under an open sky."
                : empty
                ? $"New map {path} on the {System.IO.Path.GetFileName(baseScenario)} level: {r.Removed} objects removed, {r.Kept} spawn points kept."
                : $"New map {path}: a full copy of {System.IO.Path.GetFileName(baseScenario)}.");
            string scenario = path[..^".scenario".Length];
            if (!Scenarios.Contains(scenario)) Scenarios.Insert(0, scenario);
            SelectedScenario = scenario;
            await RefreshChangesAsync();
            _ = AutoCaptureAsync();
            return await OpenMapAsync(path);
        }
        catch (HostException ex)
        {
            AddProblem("error", "Could not create the map: " + ex.Message, "New map");
            return null;
        }
    }

    // ---- new gametype -----------------------------------------------------------------------------------

    [ObservableProperty] private bool _isNewGametypeOpen;
    [ObservableProperty] private string _newGametypeName = "";
    [ObservableProperty] private GametypeTemplate? _newGametypeTemplate;
    public ObservableCollection<GametypeTemplate> GametypeTemplates { get; } = new();

    [RelayCommand]
    private void OpenNewGametypeForm()
    {
        ShowModTab();
        IsNewMapOpen = false;
        if (GametypeTemplates.Count == 0)
        {
            var known = new (string File, string Label)[]
            {
                (@"simple\slayer\2.txt", "Simple starter: points per kill (short, commented)"),
                ("slayer.txt", "Slayer"), ("ctf.txt", "Capture the flag"), ("koth_team.txt", "King of the hill"),
                ("oddball.txt", "Oddball"), ("juggernaut.txt", "Juggernaut"), ("infection.txt", "Infection"),
                ("headhunter.txt", "Headhunter"), ("assault.txt", "Assault"), ("race.txt", "Race"),
            };
            foreach (var (file, label) in known)
            {
                string rel = System.IO.Path.Combine("data", "multiplayer", "megalo", file);
                if (Paths != null && File.Exists(System.IO.Path.Combine(Paths.Workspace, rel))) GametypeTemplates.Add(new GametypeTemplate(label, rel));
            }
        }
        NewGametypeTemplate ??= GametypeTemplates.FirstOrDefault();
        IsNewGametypeOpen = true;
    }

    /// <summary>New gametypes are written in Kalo Script (modern syntax) unless unticked.</summary>
    [ObservableProperty] private bool _newGametypeKaloScript = true;

    [RelayCommand]
    private async Task CreateGametype()
    {
        var script = await CreateGametypeAsync(NewGametypeName, NewGametypeTemplate, NewGametypeKaloScript);
        if (script != null) { IsNewGametypeOpen = false; NewGametypeName = ""; }
    }

    /// <summary>A new gametype script in data\multiplayer\megalo\mods\&lt;mod&gt;, copied from a template.</summary>
    public async Task<ScriptEditorViewModel?> CreateGametypeAsync(string name, GametypeTemplate? template, bool kaloScript = false)
    {
        if (Paths == null) return null;
        if (string.IsNullOrWhiteSpace(name) || template == null) { AddProblem("warning", "Give the gametype a name and pick a template.", "New gametype"); return null; }
        if (ModProjects?.ActiveName == null) { AddProblem("warning", "Create a mod first (Project panel or start page).", "New gametype"); return null; }
        string ext = kaloScript ? Kalo.Core.Scripting.KaloScript.Extension : ".txt";
        string rel = System.IO.Path.Combine("data", "multiplayer", "megalo", "mods", ModSlug, Slug(name) + ext);
        string full = System.IO.Path.Combine(Paths.Workspace, rel);
        if (File.Exists(full)) { AddProblem("warning", "A gametype with that name already exists in this mod.", "New gametype"); return null; }
        var (text, _) = ScriptEditorViewModel.ReadScript(System.IO.Path.Combine(Paths.Workspace, template.Path));
        string header = $"; {name.Trim()} — made with Kalo from the \"{template.Label}\" template\r\n\r\n";
        if (kaloScript)
        {
            header = $"# title: {name.Trim()}\r\n# Made with Kalo from the \"{template.Label}\" template. Kalo Script: if x:, for each player:, f(a, b), x += 1.\r\n\r\n";
            text = template.Path.EndsWith(@"simple\slayer\2.txt", StringComparison.OrdinalIgnoreCase)
                ? KaloScriptStarter
                : PythonStyle(Kalo.Core.Scripting.KaloScript.FromMegalo(text));
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var unicode = Encoding.Unicode;
        await File.WriteAllBytesAsync(full, unicode.GetPreamble().Concat(unicode.GetBytes(header + text)).ToArray());
        Scripts.Add(new ScriptFile(Slug(name), rel, System.IO.Path.Combine("mods", ModSlug)));
        BuildGametypeChoices();
        SelectedGametype = GametypeChoices.FirstOrDefault(g => g.Path == rel) ?? SelectedGametype;
        Log($"New gametype {rel} from {template.Label}.");
        await RefreshChangesAsync();
        _ = AutoCaptureAsync();
        return await OpenScriptAsync(rel);
    }

    /// <summary>Kalo Script in the Python style (the classic text when it can't be rewritten).</summary>
    public static string PythonStyle(string classic) => Kalo.Core.Scripting.KaloScriptStyle.FromClassic(classic) is { } py
        && Kalo.Core.Scripting.KaloScript.Transpile(py).Megalo == Kalo.Core.Scripting.KaloScript.Transpile(classic).Megalo ? py : classic;

    /// <summary>The Kalo Script starter (points per kill, a streak bonus, a message).</summary>
    public static string KaloScriptStarter => Kalo.Core.Scripting.KaloScript.Starter;

    /// <summary>A Kalo Script converted from a Megalo script: saved next to the mod's gametypes and opened.</summary>
    public async Task<ScriptEditorViewModel?> CreateKaloScriptAsync(string baseName, string text)
    {
        if (Paths == null) return null;
        if (ModProjects?.ActiveName == null) { AddProblem("warning", "Create a mod first: the converted script goes into it.", "Kalo Script"); return null; }
        string folder = System.IO.Path.Combine("data", "multiplayer", "megalo", "mods", ModSlug);
        string slug = Slug(baseName);
        string rel = System.IO.Path.Combine(folder, slug + Kalo.Core.Scripting.KaloScript.Extension);
        for (int n = 2; File.Exists(System.IO.Path.Combine(Paths.Workspace, rel)); n++)
            rel = System.IO.Path.Combine(folder, $"{slug}_{n}{Kalo.Core.Scripting.KaloScript.Extension}");
        string full = System.IO.Path.Combine(Paths.Workspace, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var unicode = Encoding.Unicode;
        await File.WriteAllBytesAsync(full, unicode.GetPreamble().Concat(unicode.GetBytes($"-- title: {baseName}\r\n" + text)).ToArray());
        Scripts.Add(new ScriptFile(System.IO.Path.GetFileNameWithoutExtension(rel), rel, System.IO.Path.Combine("mods", ModSlug)));
        BuildGametypeChoices();
        Log($"Converted to Kalo Script: {rel}.");
        await RefreshChangesAsync();
        _ = AutoCaptureAsync();
        return await OpenScriptAsync(rel);
    }

    // ---- create a mod -----------------------------------------------------------------------------------

    [ObservableProperty] private string _newModName = "";
    [ObservableProperty] private string _newModDescription = "";

    /// <summary>
    /// Starts a new mod: the current one (if any) is put aside and the workspace goes back to vanilla, so the
    /// new mod starts empty. Then the My mod panel shows its (empty) groups with the New… actions.
    /// </summary>
    [RelayCommand]
    private async Task CreateMod()
    {
        if (ModProjects == null) return;
        string name = NewModName.Trim();
        if (name.Length == 0) { AddProblem("warning", "Give your mod a name.", "New mod"); return; }
        if (ModProjects.ActiveName != null) await SwitchProjectCommand.ExecuteAsync(null);
        NewProjectName = name;
        NewProjectDescription = NewModDescription;
        await CreateProjectCommand.ExecuteAsync(null);
        NewModName = "";
        NewModDescription = "";
        await RefreshChangesAsync();
        ShowModTab();
    }
}

public sealed record GametypeChoice(string Label, string? Path)
{
    public override string ToString() => Label;
}

public sealed record GametypeTemplate(string Label, string Path)
{
    public override string ToString() => Label;
}

/// <summary>A group of the "My mod" tree: Maps, Gametypes, Weapons & objects...</summary>
public partial class ModGroup(string name, string glyph, string actionLabel) : ObservableObject
{
    public string Name { get; } = name;
    public string Glyph { get; } = glyph;
    public string ActionLabel { get; } = actionLabel;
    public ObservableCollection<ModEntry> Items { get; } = new();
    public bool IsEmpty => Items.Count == 0;
    public string CountText => Items.Count == 0 ? "" : Items.Count.ToString();
    [ObservableProperty] private bool _isExpanded;
}

/// <summary>A file of the mod: new, changed or deleted compared with vanilla.</summary>
public sealed record ModEntry(MainViewModel Main, string Path, string Kind)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string TypeText => System.IO.Path.GetExtension(Path).TrimStart('.').Replace('_', ' ');
    public string Badge => Kind switch { "Added" => "new", "Deleted" => "deleted", _ => "changed" };
    public bool IsNew => Kind == "Added";
}

/// <summary>How many local players Play starts (split-screen in one game window).</summary>
public sealed record PlayerChoice(int Count, string Label)
{
    public override string ToString() => Label;
}
