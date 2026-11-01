using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;

namespace Kalo.App.ViewModels;

/// <summary>The Kalo window: workspace, Blam Host, open tags, play test, output.</summary>
public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxOutputLines = 4000;
    private readonly AppArgs _args;
    private List<string> _tagIndex = new();
    /// <summary>Every tag in the workspace, relative to tags\ (filled in the background at start).</summary>
    public IReadOnlyList<string> TagIndex => _tagIndex;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public KaloPaths? Paths { get; }
    public HostClient? Host { get; private set; }
    public Baseline? Baseline { get; private set; }
    public GameSession? Game { get; }
    public Kalo.Core.Geometry.LevelGeometry Geometry { get; private set; } = null!;
    public Kalo.Core.Geometry.ModelLibrary Models { get; private set; } = null!;
    public Kalo.Core.Geometry.TextureLibrary Textures { get; private set; } = null!;

    public ObservableCollection<ExplorerNode> ExplorerRoots { get; } = new();
    public ObservableCollection<SearchResult> SearchResults { get; } = new();
    public ObservableCollection<DocumentViewModel> Tabs { get; } = new();
    public ObservableCollection<string> OutputLines { get; } = new();
    public ObservableCollection<ProblemItem> Problems { get; } = new();
    public ObservableCollection<ChangeItem> Changes { get; } = new();
    public ObservableCollection<string> Scenarios { get; } = new();
    public ObservableCollection<ScriptFile> Scripts { get; } = new();
    public ObservableCollection<ProjectItem> Projects { get; } = new();
    public ModProjects? ModProjects { get; private set; }
    [ObservableProperty] private string _newProjectName = "";
    [ObservableProperty] private string _newProjectDescription = "";
    [ObservableProperty] private string _activeProjectText = "No project: edits stay in the workspace until you create one.";
    [ObservableProperty] private bool _isSwitching;
    /// <summary>Megalo actions, conditions and value lists (loaded once from ManagedMegalo).</summary>
    public Kalo.Protocol.MegaloCatalog? MegaloCatalog { get; private set; }
    public ObservableCollection<string> QuickOpen { get; } = new()
    {
        @"objects\weapons\rifle\assault_rifle\assault_rifle.weapon",
        @"objects\weapons\rifle\dmr\dmr.weapon",
        @"objects\weapons\pistol\magnum\magnum.weapon",
        @"objects\weapons\rifle\sniper_rifle\sniper_rifle.weapon",
        @"objects\weapons\rifle\shotgun\shotgun.weapon",
        @"globals\globals.globals",
    };

    /// <summary>Completes once the host is up and the --open tag (if any) is loaded; used for screenshots.</summary>
    public Task Ready => _ready.Task;

    [ObservableProperty] private DocumentViewModel? _activeDocument;
    /// <summary>The active tab when it is a tag editor (the field inspector binds to it).</summary>
    public TagEditorViewModel? ActiveTab => ActiveDocument as TagEditorViewModel;
    public MapEditorViewModel? ActiveMap => ActiveDocument as MapEditorViewModel;
    public ScriptEditorViewModel? ActiveScript => ActiveDocument as ScriptEditorViewModel;
    public DiffViewModel? ActiveDiff => ActiveDocument as DiffViewModel;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _sidePanel = "explorer";
    [ObservableProperty] private string _bottomPanel = "output";
    [ObservableProperty] private string? _selectedScenario;
    [ObservableProperty] private string _hostStatus = "Blam Host starting…";
    [ObservableProperty] private IBrush _hostBrush = Brushes.Orange;
    [ObservableProperty] private string _gameStatus = "Game not running";
    [ObservableProperty] private IBrush _gameBrush = Brushes.Gray;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopGameCommand))]
    private bool _isGameRunning;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _changesSummary = "";
    [ObservableProperty] private string _indexStatus = "Indexing tags…";
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;

    public bool HasTab => ActiveTab != null;
    public bool HasMap => ActiveMap != null;
    public bool HasScript => ActiveScript != null;
    public bool HasDiff => ActiveDiff != null;
    public bool HasDocument => ActiveDocument != null;
    public string InspectorTitle => HasMap ? "OBJECT" : HasScript ? "GAMETYPE SCRIPT" : "FIELD";
    public bool NoTab => ActiveDocument == null;
    public bool IsExplorer => SidePanel == "explorer";
    public bool IsChangesPanel => SidePanel == "changes";
    public bool IsGametypesPanel => SidePanel == "gametypes";
    public bool IsProjectsPanel => SidePanel == "projects";
    public bool IsOutput => BottomPanel == "output";
    public bool IsProblems => BottomPanel == "problems";
    public bool NotSearching => !IsSearching;
    public string WorkspaceText => Paths == null ? "no workspace" : Paths.Workspace;
    public string SidePanelTitle => SidePanel switch { "explorer" => "PROJECT", "gametypes" => "GAMETYPES", "projects" => "MOD PROJECTS", _ => "CHANGES" };

    public MainViewModel(AppArgs args)
    {
        InitGroups();
        SelectedPlayers = PlayerChoices[0];
        AppSettings.Changed += () => Post(OnSettingsChanged);
        _args = args;
        try
        {
            Paths = KaloPaths.Discover(args.Workspace, args.Hrek);
        }
        catch (Exception ex)
        {
            HostStatus = "No workspace";
            HostBrush = Brushes.IndianRed;
            AddProblem("error", ex.Message, "Kalo");
            _hostStarting = false;
            _ready.TrySetResult();
            return;
        }

        Game = new GameSession(Paths);
        Geometry = new Kalo.Core.Geometry.LevelGeometry(Paths);
        Models = new Kalo.Core.Geometry.ModelLibrary(Paths);
        Textures = new Kalo.Core.Geometry.TextureLibrary(Paths);
        Geometry.TagSource = async (scenario, bsps) =>
        {
            if (Host == null) return null;
            string file = Geometry.CacheFile(scenario) + ".host";
            var r = await Host.ExportLevelMeshAsync(scenario, file);
            Post(() => Log($"Level geometry for {scenario}: {r.Triangles:N0} triangles ({r.Clusters} clusters, {r.Instances} instances) in {r.Ms} ms" + (string.IsNullOrEmpty(r.Notes) ? "" : " — " + r.Notes)));
            var mesh = Kalo.Core.Geometry.Mesh.TryLoad(file);
            try { File.Delete(file); } catch { }
            return mesh;
        };
        Game.Output += line => Post(() =>
        {
            AppendOutput("[game] " + line);
            // The gametype didn't load: the game then runs with no gametype at all (no script, default traits).
            if (line.Contains("main_game_start:", StringComparison.Ordinal) && !line.Contains("-ERROR-", StringComparison.Ordinal))
                AddProblem("error", "The game could not start the gametype (" + line[(line.IndexOf("main_game_start:", StringComparison.Ordinal) + 16)..].Trim() + "): it runs without it.", "Play");
            else if (line.Contains("megalogamengine:", StringComparison.Ordinal) && !line.Contains("-ERROR-", StringComparison.Ordinal)
                     && (line.Contains("failed", StringComparison.Ordinal) || line.Contains("not valid", StringComparison.Ordinal) || line.Contains("doesn't match", StringComparison.Ordinal)))
                AddProblem("error", "Gametype: " + line[line.IndexOf("megalogamengine:", StringComparison.Ordinal)..].Trim(), "Play");
        });
        Game.Loaded += () => Post(() => { GameStatus = "Game running · map loaded"; GameBrush = Brushes.LimeGreen; Log("Map loaded in the game."); });
        Game.TagReloaded += tag => Post(() => { StatusMessage = "Game reloaded " + System.IO.Path.GetFileName(tag); Log("Game hot-reloaded " + tag); });
        Game.Exited += code => Post(() =>
        {
            Log($"Game exited (code {code}).");
            if (Game?.IsRunning == true) return; // the old game, replaced by the one just started
            IsGameRunning = false; GameStatus = "Game not running"; GameBrush = Brushes.Gray;
            // Play-from-camera copies are throwaway.
            // Not when this exit is the old game being replaced by a new one (which may be using the copy).
            if (Game?.IsRunning != true && !IsPreparingPlay)
                try { string play = System.IO.Path.Combine(Paths!.WorkspaceTags, @"levels\mods\_kalo_play"); if (Directory.Exists(play)) Directory.Delete(play, true); } catch { }
        });

        string tagsRoot = Paths.WorkspaceTags;
        if (Directory.Exists(tagsRoot))
        {
            foreach (string d in Directory.GetDirectories(tagsRoot).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                ExplorerRoots.Add(new ExplorerNode(d, tagsRoot, true, IsChangedSafe));
            FindScenarios(tagsRoot);
            FindScripts();
            BuildGametypeChoices();
        }
        else
        {
            AddProblem("error", "The workspace has no tags folder: " + tagsRoot, "Kalo");
        }

        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        // --open-map opens the map at once, before the Blam Host is up (as a quick click in the Project panel does).
        var earlyMap = string.IsNullOrEmpty(_args.OpenMap) ? null : OpenMapAsync(_args.OpenMap);
        try
        {
            Baseline = await Task.Run(() => Kalo.Core.Baseline.Load(Paths!));
            ModProjects = new ModProjects(Paths!, Baseline);
            RefreshProjects();
            await RefreshChangesAsync();
        }
        catch (Exception ex)
        {
            AddProblem("warning", "No baseline: changes cannot be tracked (" + ex.Message + ")", "Kalo");
        }

        _ = Task.Run(BuildIndex);
        await StartHostAsync();

        if (!string.IsNullOrEmpty(_args.Open))
        {
            var tab = await OpenTagAsync(_args.Open);
            if (tab != null && !string.IsNullOrEmpty(_args.Select))
            {
                var field = tab.FindField(_args.Select);
                if (field != null) tab.Reveal(field);
            }
        }
        if (earlyMap != null)
        {
            var map = await earlyMap;
            Log($"{map.Title}: " + string.Join(", ", map.Items.GroupBy(i => i.Category).Select(g => $"{g.Key} {g.Count()}")) + (map.HasSpawn ? ", start location" : ", no start location"));
        }
        _ready.TrySetResult();
    }

    /// <summary>True from launch until the Blam Host is up (or failed), and while it restarts.</summary>
    private volatile bool _hostStarting = true;

    /// <summary>
    /// The Blam Host, waiting for it while it starts. A map or tag opened in the first seconds after launch (before
    /// the host is ready) used to stay empty without a word; now it waits. Null if the host failed to start.
    /// </summary>
    public async Task<HostClient?> WaitForHostAsync()
    {
        var until = DateTime.UtcNow.AddMinutes(2);
        while (Host == null && _hostStarting && DateTime.UtcNow < until) await Task.Delay(200);
        return Host;
    }

    private async Task StartHostAsync()
    {
        _hostStarting = true;
        HostStatus = "Blam Host starting…";
        HostBrush = Brushes.Orange;
        try
        {
            var sw = Stopwatch.StartNew();
            Host = await HostClient.StartAsync(Paths!, line => Post(() => AppendOutput(line)));
            Host.Exited += () => Post(() => { HostStatus = "Blam Host stopped"; HostBrush = Brushes.IndianRed; });
            HostStatus = "Blam Host";
            HostBrush = Brushes.LimeGreen;
            Log($"Blam Host ready in {sw.ElapsedMilliseconds} ms (ManagedBlam {Host.Info?.StartMs} ms, {Host.Info?.TagGroups} tag groups).");
        }
        catch (Exception ex)
        {
            HostStatus = "Blam Host failed";
            HostBrush = Brushes.IndianRed;
            AddProblem("error", "Could not start the Blam Host: " + ex.Message, "Kalo");
        }
        finally
        {
            _hostStarting = false;
        }
    }

    public async Task RestartHostAsync()
    {
        AddProblem("warning", "The engine stopped; restarting the Blam Host. Unsaved edits were lost.", "Kalo");
        _hostStarting = true;
        if (Host != null) await Host.DisposeAsync();
        Host = null;
        await StartHostAsync();
        foreach (var tab in Tabs.OfType<TagEditorViewModel>().ToList()) await ReloadTabAsync(tab);
        foreach (var map in Tabs.OfType<MapEditorViewModel>().ToList()) await map.ReloadAsync();
    }

    // ---- tags ------------------------------------------------------------------------------

    public async Task<TagEditorViewModel?> OpenTagAsync(string tagPath)
    {
        tagPath = tagPath.Replace('/', '\\').TrimStart('\\');
        var existing = Tabs.OfType<TagEditorViewModel>().FirstOrDefault(t => t.Path.Equals(tagPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            ActiveDocument = existing;
            return existing;
        }
        var tab = new TagEditorViewModel(this, tagPath);
        Tabs.Add(tab);
        ActiveDocument = tab;
        await ReloadTabAsync(tab);
        return tab;
    }

    private async Task ReloadTabAsync(TagEditorViewModel tab)
    {
        if (await WaitForHostAsync() == null)
        {
            tab.Fail("the Blam Host is not running");
            AddProblem("error", "Could not open " + tab.Path + ": the Blam Host is not running.", tab.Path);
            return;
        }
        try
        {
            var doc = await Host.OpenTagAsync(tab.Path);
            tab.Load(doc);
            if (tab.IsObjectTag) await tab.RefreshScriptNameAsync();
            tab.DiffersFromVanilla = Baseline?.IsChanged("tags\\" + tab.Path) ?? false;
            Log($"Opened {tab.Path} ({doc.FieldCount:N0} fields, {doc.LoadMs} ms).");
        }
        catch (Exception ex)
        {
            tab.Fail(ex.Message);
            AddProblem("error", "Could not open " + tab.Path + ": " + ex.Message, tab.Path);
        }
    }

    [RelayCommand]
    private async Task OpenNode(object? item)
    {
        if (item is ExplorerNode { IsFolder: false, TagPath: not null } node) await OpenAnyAsync(node.TagPath);
        else if (item is SearchResult r) await OpenAnyAsync(r.TagPath);
        else if (item is ChangeItem { TagPath: not null } c) await OpenAnyAsync(c.TagPath);
        else if (item is ChangeItem ch && ch.Path.StartsWith(@"data\multiplayer\megalo\", StringComparison.OrdinalIgnoreCase)) await OpenScriptAsync(ch.Path);
        else if (item is ScriptFile sf) await OpenScriptAsync(sf.RelativePath);
        else if (item is string s) await OpenTagAsync(s);
    }

    [RelayCommand]
    private void ActivateTab(DocumentViewModel tab) => ActiveDocument = tab;

    [RelayCommand]
    private async Task CloseTab(DocumentViewModel tab)
    {
        var group = EditorGroupOf(tab);
        Tabs.Remove(tab); // also leaves its group, which shows its neighbour
        if (group != null && !group.IsPrimary && group.Tabs.Count == 0 && IsSplit) Unsplit();
        else if (ReferenceEquals(ActiveDocument, tab) || ActiveDocument == null)
        {
            var next = FocusedGroup.Current ?? Other(FocusedGroup).Current;
            if (next != null && !ReferenceEquals(FocusedGroup.Current, next)) FocusedGroup = EditorGroupOf(next) ?? Primary;
            ShowActive(next);
        }
        // A map and a tag editor can share one open scenario in the Blam Host: close it only when the last one goes.
        if (!Tabs.Any(t => t.Path.Equals(tab.Path, StringComparison.OrdinalIgnoreCase))) await tab.CloseAsync();
    }

    [RelayCommand]
    private async Task SaveActive()
    {
        if (ActiveDocument != null) await ActiveDocument.SaveDocumentAsync();
    }

    /// <summary>Scenarios open as maps; everything else in the tag editor.</summary>
    public async Task OpenAnyAsync(string tagPath)
    {
        if (tagPath.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)) await OpenMapAsync(tagPath);
        else await OpenTagAsync(tagPath);
    }

    public async Task<MapEditorViewModel> OpenMapAsync(string scenarioPath)
    {
        scenarioPath = scenarioPath.Replace('/', '\\').TrimStart('\\');
        var existing = Tabs.OfType<MapEditorViewModel>().FirstOrDefault(t => t.Path.Equals(scenarioPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { ActiveDocument = existing; return existing; }
        var map = new MapEditorViewModel(this, scenarioPath);
        Tabs.Add(map);
        ActiveDocument = map;
        map.DiffersFromVanilla = Baseline?.IsChanged("tags\\" + scenarioPath) ?? false;
        await map.ReloadAsync();
        Log($"Opened map {scenarioPath}: {map.Status}");
        return map;
    }

    // ---- compare with vanilla --------------------------------------------------------------------

    [RelayCommand]
    private async Task CompareChange(ChangeItem? change)
    {
        if (change != null) await CompareWithVanillaAsync(change.Path);
    }

    /// <summary>Opens a diff tab: tag fields that differ from HREK's version, or changed lines of a text file.</summary>
    public async Task<DiffViewModel?> CompareWithVanillaAsync(string workspacePath)
    {
        if (Paths == null) return null;
        var existing = Tabs.OfType<DiffViewModel>().FirstOrDefault(t => t.File.Equals(workspacePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null) Tabs.Remove(existing);
        var diff = new DiffViewModel(workspacePath);
        Tabs.Add(diff);
        ActiveDocument = diff;
        string vanilla = System.IO.Path.Combine(Paths.Hrek, workspacePath);
        string current = System.IO.Path.Combine(Paths.Workspace, workspacePath);
        try
        {
            if (!File.Exists(vanilla)) { diff.Summary = "This file is new: it does not exist in vanilla HREK."; return diff; }
            if (!File.Exists(current)) { diff.Summary = "This mod deletes the vanilla file."; return diff; }
            if (workspacePath.StartsWith(@"tags\", StringComparison.OrdinalIgnoreCase))
            {
                if (Host == null) { diff.Summary = "The Blam Host is not running."; return diff; }
                var r = await Host.DiffTagAsync(workspacePath.Substring(5), vanilla);
                foreach (var e in r.Entries ?? new())
                    diff.Rows.Add(new DiffRow(e.Kind ?? "changed", FieldLabels.For(e.Name, e.Name), e.Path ?? "", e.Vanilla ?? "", e.Current ?? ""));
                int changed = diff.Rows.Count(x => x.IsChanged), added = diff.Rows.Count(x => x.IsAdded), removed = diff.Rows.Count(x => x.IsRemoved);
                diff.Summary = diff.Rows.Count == 0
                    ? $"No field differs from vanilla ({r.FieldsCompared:N0} fields compared): only the file itself was rewritten."
                    : $"{changed} changed, {added} added, {removed} removed field(s) out of {r.FieldsCompared:N0} compared in {r.Ms} ms" + (r.Truncated ? " (list cut short)" : "") + ".";
            }
            else
            {
                diff.IsText = true;
                var a = await File.ReadAllLinesAsync(vanilla);
                var b = await File.ReadAllLinesAsync(current);
                var lines = await Task.Run(() => TextDiff.Compare(a, b));
                foreach (var l in lines)
                    diff.Rows.Add(new DiffRow(l.Op == ' ' ? "same" : l.Op.ToString(),
                        l.Op == '…' ? "…" : $"{l.OldNumber?.ToString() ?? "·"} → {l.NewNumber?.ToString() ?? "·"}", "",
                        l.Op == '+' ? "" : l.Text, l.Op == '-' ? "" : l.Text));
                diff.Summary = $"{lines.Count(l => l.Op == '+')} line(s) added, {lines.Count(l => l.Op == '-')} removed ({a.Length} → {b.Length} lines).";
            }
        }
        catch (Exception ex)
        {
            diff.Summary = "Could not compare: " + ex.Message;
        }
        return diff;
    }

    // ---- import a mod ---------------------------------------------------------------------------------

    public async Task<ModProject?> ImportProjectAsync(string zipPath)
    {
        if (ModProjects == null) return null;
        try
        {
            var p = await Task.Run(() => ModProjects.Import(zipPath));
            Log($"Imported {System.IO.Path.GetFileName(zipPath)} as project {p.Name} ({p.Manifest.Files.Count} files). Switch to it to load it into the workspace.");
            RefreshProjects();
            return p;
        }
        catch (Exception ex)
        {
            AddProblem("error", "Import failed: " + ex.Message, "Projects");
            return null;
        }
    }

    private void FindScripts()
    {
        string root = Paths!.MegaloSource;
        if (!Directory.Exists(root)) return;
        string ws = Paths.Workspace;
        IEnumerable<string> files = Directory.EnumerateFiles(root, "*.txt", SearchOption.TopDirectoryOnly)
            .Concat(Directory.Exists(System.IO.Path.Combine(root, "simple")) ? Directory.EnumerateFiles(System.IO.Path.Combine(root, "simple"), "*.txt", SearchOption.AllDirectories) : Array.Empty<string>())
            .Concat(Directory.Exists(System.IO.Path.Combine(root, "includes")) ? Directory.EnumerateFiles(System.IO.Path.Combine(root, "includes"), "*.txt") : Array.Empty<string>())
            .Concat(Directory.Exists(System.IO.Path.Combine(root, "mods")) ? Directory.EnumerateFiles(System.IO.Path.Combine(root, "mods"), "*.txt", SearchOption.AllDirectories) : Array.Empty<string>())
            .Concat(Directory.Exists(System.IO.Path.Combine(root, "mods")) ? Directory.EnumerateFiles(System.IO.Path.Combine(root, "mods"), "*" + Kalo.Core.Scripting.KaloScript.Extension, SearchOption.AllDirectories) : Array.Empty<string>());
        foreach (string f in files.OrderBy(f => f.Contains("includes") ? 1 : 0).ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string rel = f.Substring(ws.Length + 1);
            string folder = System.IO.Path.GetDirectoryName(f.Substring(root.Length + 1)) ?? "";
            Scripts.Add(new ScriptFile(System.IO.Path.GetFileNameWithoutExtension(f), rel, folder.Length == 0 ? "gametype" : folder));
        }
    }

    public async Task<ScriptEditorViewModel?> OpenScriptAsync(string relativePath)
    {
        var existing = Tabs.OfType<ScriptEditorViewModel>().FirstOrDefault(t => t.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { ActiveDocument = existing; return existing; }
        var script = new ScriptEditorViewModel(this, relativePath);
        try
        {
            await script.LoadAsync();
        }
        catch (Exception ex)
        {
            AddProblem("error", "Could not open " + relativePath + ": " + ex.Message, relativePath);
            return null;
        }
        script.DiffersFromVanilla = Baseline?.IsChanged(relativePath) ?? false;
        Tabs.Add(script);
        ActiveDocument = script;
        await EnsureMegaloCatalogAsync();
        script.UpdateCatalog();
        return script;
    }

    public async Task EnsureMegaloCatalogAsync()
    {
        if (MegaloCatalog != null || Host == null || Paths == null) return;
        try
        {
            MegaloCatalog = await Host.MegaloCatalogAsync(Paths.MegaloObjectLists);
            Kalo.Core.Scripting.MegaloDocs.Fix(MegaloCatalog);
            Log($"Gametype compiler ready: {MegaloCatalog.Actions?.Count} actions, {MegaloCatalog.Conditions?.Count} conditions.");
        }
        catch (Exception ex)
        {
            AddProblem("warning", "Gametype compiler unavailable: " + ex.Message, "Megalo");
        }
    }

    /// <summary>Replaces the Problems entries of one script with its latest compile messages.</summary>
    /// <param name="showPanel">Switch the bottom panel to Problems when there are errors (a Compile; not a check while typing).</param>
    public void ReportScriptProblems(ScriptEditorViewModel script, IEnumerable<ScriptMessage> messages, bool showPanel = true)
    {
        foreach (var p in Problems.Where(p => p.Source == script.Path).ToList()) Problems.Remove(p);
        foreach (var m in messages)
            Problems.Insert(0, new ProblemItem(m.IsError ? "error" : "warning", $"{m.Where}: {m.Display}", script.Path, DateTime.Now.ToString("HH:mm:ss")));
        ErrorCount = Problems.Count(p => p.Severity == "error");
        WarningCount = Problems.Count(p => p.Severity == "warning");
        if (showPanel && messages.Any(m => m.IsError)) BottomPanel = "problems";
    }

    public Task PlayScenarioAsync(string scenario)
    {
        if (!Scenarios.Contains(scenario)) Scenarios.Add(scenario);
        SelectedScenario = scenario;
        return PlayAsync();
    }

    /// <summary>Any document saved: refresh the vanilla markers and the Changes panel.</summary>
    public void OnDocumentSaved(DocumentViewModel doc)
    {
        doc.DiffersFromVanilla = Baseline?.IsChanged(doc.BaselinePath) ?? true;
        Log($"Saved {doc.Path}." + (IsGameRunning ? " The running game should reload it." : ""));
        StatusMessage = "Saved " + doc.Title;
        _ = RefreshChangesAsync();
        _ = AutoCaptureAsync();
    }

    public void OnTagSaved(TagEditorViewModel tab)
    {
        tab.DiffersFromVanilla = Baseline?.IsChanged("tags\\" + tab.Path) ?? true;
        Log($"Saved {tab.Path}." + (IsGameRunning ? " The running game should reload it." : ""));
        StatusMessage = "Saved " + tab.Title;
        _ = RefreshChangesAsync();
        _ = AutoCaptureAsync();
    }

    /// <summary>Where "Save as new" puts a copy of a tag: objects\mods\&lt;mod&gt;\&lt;name&gt;\&lt;name&gt;.&lt;ext&gt; (same top folder as the original).</summary>
    public string NewTagPathFor(string original, string name)
    {
        string top = original.Contains('\\') ? original[..original.IndexOf('\\')] : "objects";
        string slug = Slug(name);
        return $@"{top}\mods\{ModSlugForContent}\{slug}\{slug}{System.IO.Path.GetExtension(original)}";
    }

    /// <summary>
    /// Saves the tab's tag (with its unsaved edits) as a new tag and opens that one instead. The original file keeps what
    /// was last saved in it, so a new object can start as a copy of an existing one without changing the existing one.
    /// </summary>
    public async Task SaveTagAsNewAsync(TagEditorViewModel tab, string name)
    {
        if (Host == null || Paths == null || string.IsNullOrWhiteSpace(name)) return;
        string newPath = NewTagPathFor(tab.Path, name);
        if (File.Exists(System.IO.Path.Combine(Paths.WorkspaceTags, newPath)))
        {
            AddProblem("error", $"Save as new: {newPath} already exists. Pick another name.", tab.Path);
            return;
        }
        try
        {
            string oldPath = tab.Path;
            await Host.SaveTagAsAsync(oldPath, newPath);
            // The Blam Host now holds the copy under its new path: the old tab is closed and the copy opened.
            await CloseTab(tab); // the host no longer has the old path open, so this only closes the tab
            var opened = await OpenTagAsync(newPath);
            if (opened != null) OnTagSaved(opened);
            Log($"Saved {oldPath} as a new tag: {newPath}. {oldPath} itself is unchanged by this.");
            StatusMessage = "New tag " + newPath;
        }
        catch (HostException ex)
        {
            AddProblem("error", "Save as new failed: " + ex.Message, tab.Path);
        }
    }

    public async Task RevertTagAsync(TagEditorViewModel tab)
    {
        if (Baseline == null) return;
        try
        {
            if (Host != null) await Host.CloseTagAsync(tab.Path);
            Baseline.Revert("tags\\" + tab.Path);
            Log("Reverted " + tab.Path + " to vanilla.");
            await ReloadTabAsync(tab);
            await RefreshChangesAsync();
        }
        catch (Exception ex)
        {
            AddProblem("error", "Revert failed: " + ex.Message, tab.Path);
        }
    }

    [RelayCommand]
    private async Task RevertChange(ChangeItem change)
    {
        if (Baseline == null) return;
        var tab = Tabs.OfType<TagEditorViewModel>().FirstOrDefault(t => change.TagPath != null && t.Path.Equals(change.TagPath, StringComparison.OrdinalIgnoreCase));
        if (tab != null) { await RevertTagAsync(tab); return; }
        try
        {
            Baseline.Revert(change.Path);
            Log("Reverted " + change.Path + " to vanilla.");
        }
        catch (Exception ex)
        {
            AddProblem("error", "Revert failed: " + ex.Message, change.Path);
        }
        await RefreshChangesAsync();
    }

    [RelayCommand]
    public async Task RefreshChangesAsync()
    {
        if (Baseline == null) return;
        var list = await Task.Run(() => Baseline.Scan());
        Changes.Clear();
        foreach (var c in list) Changes.Add(new ChangeItem { Path = c.Path, Kind = c.Kind.ToString() });
        ChangesSummary = list.Count == 0 ? "No changes from vanilla" : $"{list.Count} changed file{(list.Count == 1 ? "" : "s")}";
        foreach (var root in ExplorerRoots) root.RefreshChanged();
        BuildModTree(list);
    }

    private bool IsChangedSafe(string relative)
    {
        try { return Baseline?.IsChanged(relative) ?? false; } catch { return false; }
    }

    // ---- search ----------------------------------------------------------------------------

    private void BuildIndex()
    {
        var sw = Stopwatch.StartNew();
        string root = Paths!.WorkspaceTags;
        var list = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => f.Substring(root.Length + 1))
            .ToList();
        _tagIndex = list;
        Post(() => IndexStatus = $"{list.Count:N0} tags indexed in {sw.ElapsedMilliseconds} ms");
    }

    partial void OnSearchTextChanged(string value)
    {
        string q = value.Trim();
        IsSearching = q.Length > 0;
        SearchResults.Clear();
        if (q.Length < 2) return;
        string[] words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (string p in _tagIndex)
        {
            string name = System.IO.Path.GetFileName(p);
            if (words.All(w => p.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                SearchResults.Add(new SearchResult(name, p, System.IO.Path.GetDirectoryName(p) ?? ""));
                if (SearchResults.Count >= 200) break;
            }
        }
    }

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(NotSearching));

    // ---- play test -------------------------------------------------------------------------

    private void FindScenarios(string tagsRoot)
    {
        // Maps made in Kalo (levels\mods) first, then Reach's multiplayer maps.
        foreach (string folder in new[] { "mods", "multi" })
        {
            string dir = System.IO.Path.Combine(tagsRoot, "levels", folder);
            if (!Directory.Exists(dir)) continue;
            foreach (string f in Directory.EnumerateFiles(dir, "*.scenario", SearchOption.AllDirectories).OrderBy(x => x))
            {
                string rel = f.Substring(tagsRoot.Length + 1);
                if (rel.Contains(@"\_kalo_", StringComparison.OrdinalIgnoreCase)) continue; // play copies and test maps
                Scenarios.Add(rel.Substring(0, rel.Length - ".scenario".Length));
            }
        }
        SelectedScenario = Scenarios.FirstOrDefault(s => s.EndsWith("20_sword_slayer", StringComparison.OrdinalIgnoreCase)) ?? Scenarios.FirstOrDefault();
    }


    /// <summary>Starts reach_tag_test on the selected map, optionally running console commands first.</summary>
    public void StartGame(IReadOnlyList<string> initCommands, string? note)
    {
        if (SelectedScenario != null) StartGame(SelectedScenario, initCommands, note);
    }

    public void StartGame(string scenario, IReadOnlyList<string> initCommands, string? note)
    {
        if (Game == null) return;
        if (IsGameRunning) Game.Stop();
        try
        {
            ApplyPlaytestInput(PlayerCount);
            Game.Start(scenario, initCommands);
            if (note != null) Log(note);
            IsGameRunning = true;
            GameStatus = "Game loading " + System.IO.Path.GetFileName(scenario) + "…";
            GameBrush = Brushes.Orange;
            Log("Play: launched reach_tag_test on " + scenario + ". Saved tags reload in the game automatically.");
            BottomPanel = "output";
        }
        catch (Exception ex)
        {
            AddProblem("error", "Could not start the game: " + ex.Message, "Play");
        }
    }

    [RelayCommand(CanExecute = nameof(IsGameRunning))]
    private void StopGame() => Game?.Stop();

    private int _runningPlayers = 1;

    /// <summary>
    /// Gamepads for the playtest: installs Kalo's xinput1_4.dll next to the game (once) and writes which player each
    /// gamepad drives (kalo_input.txt), from Settings › Playtest gamepads and the players picker.
    /// </summary>
    private void ApplyPlaytestInput(int players)
    {
        if (Paths == null) return;
        _runningPlayers = players;
        var map = PlaytestInput.Resolve(AppSettings.Current.GamepadPlayers, players);
        string dll = System.IO.Path.Combine(AppContext.BaseDirectory, "xinput", PlaytestInput.DllName);
        if (!PlaytestInput.Deploy(Paths.Workspace, dll, out string why)) { AddProblem("warning", "Gamepads: " + why, "Play"); return; }
        try
        {
            PlaytestInput.WriteConfig(Paths.Workspace, players, map);
            Log("Gamepads: " + PlaytestInput.Describe(players, map) + ". In game: Back + D-pad right/left (or F8 for gamepad 1) moves a gamepad to another player.");
        }
        catch (Exception ex) { AddProblem("warning", "Gamepads: " + ex.Message, "Play"); }
    }

    /// <summary>Settings saved while the game runs: the game picks up the new gamepad players within a second.</summary>
    private void OnSettingsChanged()
    {
        if (!IsGameRunning || Paths == null) return;
        try
        {
            var map = PlaytestInput.Resolve(AppSettings.Current.GamepadPlayers, _runningPlayers);
            PlaytestInput.WriteConfig(Paths.Workspace, _runningPlayers, map);
            Log("Gamepads (live): " + PlaytestInput.Describe(_runningPlayers, map) + ".");
        }
        catch { }
    }

    // ---- panels ----------------------------------------------------------------------------

    [RelayCommand] private void ShowExplorer() => SidePanel = "explorer";
    [RelayCommand] private void ShowChanges() { SidePanel = "changes"; _ = RefreshChangesAsync(); }
    [RelayCommand] private void ShowGametypes() => SidePanel = "gametypes";
    [RelayCommand] private void ShowProjects() { SidePanel = "projects"; RefreshProjects(); }

    // ---- mod projects ------------------------------------------------------------------------

    public void RefreshProjects()
    {
        Projects.Clear();
        if (ModProjects == null) return;
        string? active = ModProjects.ActiveName;
        foreach (var p in ModProjects.List())
            Projects.Add(new ProjectItem(p.Name, p.Manifest.Description, p.Manifest.Files.Count, p.Name.Equals(active, StringComparison.OrdinalIgnoreCase), p.Manifest.Updated));
        OnPropertyChanged(nameof(HasActiveMod));
        OnPropertyChanged(nameof(NoActiveMod));
        OnPropertyChanged(nameof(ActiveModName));
        ActiveProjectText = active == null
            ? "No project: edits stay in the workspace until you create one."
            : $"Working on {active}. Every save is recorded in the project.";
    }

    [RelayCommand]
    private async Task CreateProject()
    {
        if (ModProjects == null) return;
        try
        {
            string name = NewProjectName.Trim();
            bool adopt = ModProjects.ActiveName == null;
            var p = await Task.Run(() => ModProjects.Create(name, Environment.UserName, NewProjectDescription.Trim()));
            Log(adopt ? $"Created project {p.Name} with the workspace's {p.Manifest.Files.Count} changed file(s)." : $"Created project {p.Name}. Switch to it to start working on it.");
            NewProjectName = "";
            NewProjectDescription = "";
            RefreshProjects();
        }
        catch (Exception ex)
        {
            AddProblem("error", ex.Message, "Projects");
        }
    }

    [RelayCommand]
    private async Task SwitchProject(ProjectItem? item)
    {
        if (ModProjects == null || IsSwitching) return;
        if (Tabs.Any(t => t.IsDirty)) { AddProblem("warning", "Save or close the edited tabs before switching projects.", "Projects"); return; }
        if (IsGameRunning) Game?.Stop();
        IsSwitching = true;
        try
        {
            foreach (var t in Tabs.ToList()) await CloseTab(t);
            var target = item == null ? null : ModProjects.Find(item.Name);
            await Task.Run(() => ModProjects.Switch(target, msg => Post(() => StatusMessage = msg)));
            Log(target == null ? "Workspace is back to vanilla (no project)." : $"Switched to project {target.Name} ({target.Manifest.Files.Count} files).");
            StatusMessage = target == null ? "Vanilla workspace" : "Project " + target.Name;
        }
        catch (Exception ex)
        {
            AddProblem("error", "Switch failed: " + ex.Message, "Projects");
        }
        finally
        {
            IsSwitching = false;
            RefreshProjects();
            RefreshPrefabs();
            await RefreshChangesAsync();
        }
    }

    [RelayCommand]
    private async Task CaptureProject()
    {
        var p = ModProjects?.ActiveName is { } n ? ModProjects.Find(n) : null;
        if (p == null) return;
        var r = await Task.Run(() => ModProjects!.Capture(p));
        Log($"Project {p.Name}: {r.Files} file(s), {r.Copied} updated, {r.Removed} back to vanilla.");
        RefreshProjects();
    }

    [RelayCommand]
    private async Task ExportProject(ProjectItem? item)
    {
        if (ModProjects == null) return;
        var p = item != null ? ModProjects.Find(item.Name) : ModProjects.ActiveName is { } n ? ModProjects.Find(n) : null;
        if (p == null) { AddProblem("warning", "Create a project first: the export packs a project's files.", "Projects"); return; }
        try
        {
            string zip = await Task.Run(() => ModProjects.Export(p));
            Log($"Exported {p.Name} to {zip}");
            StatusMessage = "Exported " + System.IO.Path.GetFileName(zip);
            RefreshProjects();
        }
        catch (Exception ex)
        {
            AddProblem("error", "Export failed: " + ex.Message, "Projects");
        }
    }

    private async Task AutoCaptureAsync()
    {
        if (ModProjects?.ActiveName is not { } n || ModProjects.Find(n) is not { } p) return;
        try { await Task.Run(() => ModProjects.Capture(p)); } catch (Exception ex) { Log("Could not record the save in the project: " + ex.Message); }
        Post(RefreshProjects);
    }
    [RelayCommand] private void ShowOutput() => BottomPanel = "output";
    [RelayCommand] private void ShowProblems() => BottomPanel = "problems";
    [RelayCommand] private void ClearOutput() => OutputLines.Clear();

    partial void OnSidePanelChanged(string value)
    {
        OnPropertyChanged(nameof(IsExplorer));
        OnPropertyChanged(nameof(IsChangesPanel));
        OnPropertyChanged(nameof(IsGametypesPanel));
        OnPropertyChanged(nameof(IsProjectsPanel));
        OnPropertyChanged(nameof(SidePanelTitle));
    }

    partial void OnBottomPanelChanged(string value)
    {
        OnPropertyChanged(nameof(IsOutput));
        OnPropertyChanged(nameof(IsProblems));
        OnPropertyChanged(nameof(IsObjects));
        OnPropertyChanged(nameof(IsPrefabs));
    }

    partial void OnActiveDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        PlaceActive(newValue);
        FollowActiveDocument(newValue);
        OnPropertyChanged(nameof(ActiveTab));
        OnPropertyChanged(nameof(ActiveMap));
        OnPropertyChanged(nameof(ActiveScript));
        OnPropertyChanged(nameof(ActiveDiff));
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(HasTab));
        OnPropertyChanged(nameof(HasMap));
        OnPropertyChanged(nameof(HasScript));
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(InspectorTitle));
        OnPropertyChanged(nameof(NoTab));
    }

    // ---- output and problems ---------------------------------------------------------------

    public void Log(string message) => AppendOutput($"[kalo {DateTime.Now:HH:mm:ss}] {message}");

    private void AppendOutput(string line)
    {
        OutputLines.Add(line);
        while (OutputLines.Count > MaxOutputLines) OutputLines.RemoveAt(0);
    }

    public void AddProblem(string severity, string message, string source)
    {
        Post(() =>
        {
            Problems.Insert(0, new ProblemItem(severity, message, source, DateTime.Now.ToString("HH:mm:ss")));
            ErrorCount = Problems.Count(p => p.Severity == "error");
            WarningCount = Problems.Count(p => p.Severity == "warning");
            if (severity == "error") BottomPanel = "problems";
        });
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    public async ValueTask DisposeAsync()
    {
        Game?.Dispose();
        if (Host != null) await Host.DisposeAsync();
    }
}

public sealed record ProjectItem(string Name, string Description, int FileCount, bool IsActive, DateTime Updated)
{
    public string Detail => $"{FileCount} file{(FileCount == 1 ? "" : "s")} · updated {Updated:g}";
}
