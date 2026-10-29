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
    public string PlaySummary => $"Play {System.IO.Path.GetFileName(SelectedScenario ?? "?")} with {SelectedGametype?.Label ?? "its default gametype"} in reach_tag_test (F5). Unsaved changes are saved first.";

    partial void OnSelectedGametypeChanged(GametypeChoice? value) => OnPropertyChanged(nameof(PlaySummary));
    partial void OnSelectedScenarioChanged(string? value) => OnPropertyChanged(nameof(PlaySummary));

    private void BuildGametypeChoices()
    {
        var keep = SelectedGametype?.Path;
        GametypeChoices.Clear();
        GametypeChoices.Add(new GametypeChoice("Map default", null));
        foreach (var s in Scripts.Where(IsGametype).OrderBy(s => s.RelativePath.Contains(@"\mods\", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(s => s.Name))
            GametypeChoices.Add(new GametypeChoice(s.Name + (s.RelativePath.Contains(@"\mods\", StringComparison.OrdinalIgnoreCase) ? "  (my mod)" : ""), s.RelativePath));
        SelectedGametype = GametypeChoices.FirstOrDefault(g => g.Path == keep) ?? GametypeChoices[0];
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
            var commands = new List<string>();
            string note = "";
            if (SelectedGametype?.Path is { } gametype)
            {
                string? engine = await DeployGametypeAsync(gametype);
                if (engine == null) return;
                commands.Add("load_binary_game_engine " + engine);
                note = $" with gametype {System.IO.Path.GetFileNameWithoutExtension(gametype)}";
            }
            StartGame(commands, $"Play: {SelectedScenario}{note}.");
        }
        finally
        {
            IsPreparingPlay = false;
        }
    }

    /// <summary>Compiles a gametype script (the open tab's text if it is open) and writes maps\megalo\kalo_&lt;name&gt;.mglo. Returns the engine name.</summary>
    public async Task<string?> DeployGametypeAsync(string relativePath)
    {
        if (Host == null || Paths == null) return null;
        var open = Tabs.OfType<ScriptEditorViewModel>().FirstOrDefault(t => t.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
        string text = open?.Text ?? ScriptEditorViewModel.ReadScript(System.IO.Path.Combine(Paths.Workspace, relativePath)).Text;
        string name = "kalo_" + System.IO.Path.GetFileNameWithoutExtension(relativePath);
        string mglo = System.IO.Path.Combine(Paths.Workspace, "maps", "megalo", name + ".mglo");
        await EnsureMegaloCatalogAsync();
        var r = await Host.MegaloCompileAsync(new MegaloCompileParams
        {
            ObjectLists = Paths.MegaloObjectLists,
            IncludeFolder = Paths.MegaloSource,
            Text = text,
            OutputFolder = System.IO.Path.Combine(Paths.KaloData, "megalo", name),
            MgloFile = mglo
        });
        if (!r.Succeeded || !File.Exists(mglo))
        {
            var first = r.Messages?.FirstOrDefault();
            AddProblem("error", $"The gametype {System.IO.Path.GetFileName(relativePath)} does not compile" + (first != null ? $": line {first.Line}: {first.Message}" : "."), relativePath);
            if (open == null) await OpenScriptAsync(relativePath);
            return null;
        }
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
            if (name == "Other files" && entries.Count == 0) continue;
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
            Baseline.Revert(entry.Path);
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
    [ObservableProperty] private bool _newMapEmpty = true;
    public bool NewMapFull { get => !NewMapEmpty; set => NewMapEmpty = !value; }
    partial void OnNewMapEmptyChanged(bool value) => OnPropertyChanged(nameof(NewMapFull));

    /// <summary>Reach multiplayer levels a new map can start from.</summary>
    public IEnumerable<string> BaseLevels => Scenarios.Where(s => s.StartsWith(@"levels\multi\", StringComparison.OrdinalIgnoreCase));

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
        var map = await CreateMapAsync(NewMapName, NewMapBase, NewMapEmpty);
        if (map != null) { IsNewMapOpen = false; NewMapName = ""; }
    }

    private string ModSlug => Slug(ModProjects?.ActiveName ?? "my_mod");

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.Trim().ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        string s = sb.ToString().Trim('_');
        while (s.Contains("__")) s = s.Replace("__", "_");
        return s.Length == 0 ? "untitled" : s;
    }

    /// <summary>A new map in levels\mods\&lt;mod&gt;\&lt;map&gt;, from a Reach level (empty: spawns only; or a full copy).</summary>
    public async Task<MapEditorViewModel?> CreateMapAsync(string name, string? baseScenario, bool empty)
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
            var r = await Host.CreateMapAsync(new CreateMapParams { Template = baseScenario + ".scenario", NewPath = path, Empty = empty, MapId = mapId });
            Log(empty
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

    [RelayCommand]
    private async Task CreateGametype()
    {
        var script = await CreateGametypeAsync(NewGametypeName, NewGametypeTemplate);
        if (script != null) { IsNewGametypeOpen = false; NewGametypeName = ""; }
    }

    /// <summary>A new gametype script in data\multiplayer\megalo\mods\&lt;mod&gt;, copied from a template.</summary>
    public async Task<ScriptEditorViewModel?> CreateGametypeAsync(string name, GametypeTemplate? template)
    {
        if (Paths == null) return null;
        if (string.IsNullOrWhiteSpace(name) || template == null) { AddProblem("warning", "Give the gametype a name and pick a template.", "New gametype"); return null; }
        if (ModProjects?.ActiveName == null) { AddProblem("warning", "Create a mod first (Project panel or start page).", "New gametype"); return null; }
        string rel = System.IO.Path.Combine("data", "multiplayer", "megalo", "mods", ModSlug, Slug(name) + ".txt");
        string full = System.IO.Path.Combine(Paths.Workspace, rel);
        if (File.Exists(full)) { AddProblem("warning", "A gametype with that name already exists in this mod.", "New gametype"); return null; }
        var (text, _) = ScriptEditorViewModel.ReadScript(System.IO.Path.Combine(Paths.Workspace, template.Path));
        string header = $"; {name.Trim()} — made with Kalo from the \"{template.Label}\" template\r\n\r\n";
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
