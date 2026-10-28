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
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public KaloPaths? Paths { get; }
    public HostClient? Host { get; private set; }
    public Baseline? Baseline { get; private set; }
    public GameSession? Game { get; }

    public ObservableCollection<ExplorerNode> ExplorerRoots { get; } = new();
    public ObservableCollection<SearchResult> SearchResults { get; } = new();
    public ObservableCollection<TagEditorViewModel> Tabs { get; } = new();
    public ObservableCollection<string> OutputLines { get; } = new();
    public ObservableCollection<ProblemItem> Problems { get; } = new();
    public ObservableCollection<ChangeItem> Changes { get; } = new();
    public ObservableCollection<string> Scenarios { get; } = new();
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

    [ObservableProperty] private TagEditorViewModel? _activeTab;
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
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopGameCommand))]
    private bool _isGameRunning;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _changesSummary = "";
    [ObservableProperty] private string _indexStatus = "Indexing tags…";
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;

    public bool HasTab => ActiveTab != null;
    public bool NoTab => ActiveTab == null;
    public bool IsExplorer => SidePanel == "explorer";
    public bool IsChangesPanel => SidePanel == "changes";
    public bool IsOutput => BottomPanel == "output";
    public bool IsProblems => BottomPanel == "problems";
    public bool NotSearching => !IsSearching;
    public string WorkspaceText => Paths == null ? "no workspace" : Paths.Workspace;
    public string SidePanelTitle => IsExplorer ? "PROJECT" : "CHANGES";

    public MainViewModel(AppArgs args)
    {
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
            _ready.TrySetResult();
            return;
        }

        Game = new GameSession(Paths);
        Game.Output += line => Post(() => AppendOutput("[game] " + line));
        Game.Loaded += () => Post(() => { GameStatus = "Game running · map loaded"; GameBrush = Brushes.LimeGreen; Log("Map loaded in the game."); });
        Game.TagReloaded += tag => Post(() => { StatusMessage = "Game reloaded " + System.IO.Path.GetFileName(tag); Log("Game hot-reloaded " + tag); });
        Game.Exited += code => Post(() => { IsGameRunning = false; GameStatus = "Game not running"; GameBrush = Brushes.Gray; Log($"Game exited (code {code})."); });

        string tagsRoot = Paths.WorkspaceTags;
        if (Directory.Exists(tagsRoot))
        {
            foreach (string d in Directory.GetDirectories(tagsRoot).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                ExplorerRoots.Add(new ExplorerNode(d, tagsRoot, true, IsChangedSafe));
            FindScenarios(tagsRoot);
        }
        else
        {
            AddProblem("error", "The workspace has no tags folder: " + tagsRoot, "Kalo");
        }

        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            Baseline = await Task.Run(() => Kalo.Core.Baseline.Load(Paths!));
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
        _ready.TrySetResult();
    }

    private async Task StartHostAsync()
    {
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
    }

    public async Task RestartHostAsync()
    {
        AddProblem("warning", "The engine stopped; restarting the Blam Host. Unsaved edits were lost.", "Kalo");
        if (Host != null) await Host.DisposeAsync();
        Host = null;
        await StartHostAsync();
        foreach (var tab in Tabs.ToList()) await ReloadTabAsync(tab);
    }

    // ---- tags ------------------------------------------------------------------------------

    public async Task<TagEditorViewModel?> OpenTagAsync(string tagPath)
    {
        tagPath = tagPath.Replace('/', '\\').TrimStart('\\');
        var existing = Tabs.FirstOrDefault(t => t.Path.Equals(tagPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            ActiveTab = existing;
            return existing;
        }
        var tab = new TagEditorViewModel(this, tagPath);
        Tabs.Add(tab);
        ActiveTab = tab;
        await ReloadTabAsync(tab);
        return tab;
    }

    private async Task ReloadTabAsync(TagEditorViewModel tab)
    {
        if (Host == null)
        {
            tab.Fail("the Blam Host is not running");
            return;
        }
        try
        {
            var doc = await Host.OpenTagAsync(tab.Path);
            tab.Load(doc);
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
        if (item is ExplorerNode { IsFolder: false, TagPath: not null } node) await OpenTagAsync(node.TagPath);
        else if (item is SearchResult r) await OpenTagAsync(r.TagPath);
        else if (item is ChangeItem { TagPath: not null } c) await OpenTagAsync(c.TagPath);
        else if (item is string s) await OpenTagAsync(s);
    }

    [RelayCommand]
    private void ActivateTab(TagEditorViewModel tab) => ActiveTab = tab;

    [RelayCommand]
    private async Task CloseTab(TagEditorViewModel tab)
    {
        int index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (ActiveTab == tab) ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Max(0, index - 1)];
        try { if (Host != null) await Host.CloseTagAsync(tab.Path); } catch { }
    }

    [RelayCommand]
    private async Task SaveActive()
    {
        if (ActiveTab != null) await ActiveTab.SaveCommand.ExecuteAsync(null);
    }

    public void OnTagSaved(TagEditorViewModel tab)
    {
        tab.DiffersFromVanilla = Baseline?.IsChanged("tags\\" + tab.Path) ?? true;
        Log($"Saved {tab.Path}." + (IsGameRunning ? " The running game should reload it." : ""));
        StatusMessage = "Saved " + tab.Title;
        _ = RefreshChangesAsync();
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
        var tab = Tabs.FirstOrDefault(t => change.TagPath != null && t.Path.Equals(change.TagPath, StringComparison.OrdinalIgnoreCase));
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
        string multi = System.IO.Path.Combine(tagsRoot, "levels", "multi");
        if (!Directory.Exists(multi)) return;
        foreach (string f in Directory.EnumerateFiles(multi, "*.scenario", SearchOption.AllDirectories).OrderBy(x => x))
        {
            string rel = f.Substring(tagsRoot.Length + 1);
            Scenarios.Add(rel.Substring(0, rel.Length - ".scenario".Length));
        }
        SelectedScenario = Scenarios.FirstOrDefault(s => s.EndsWith("20_sword_slayer", StringComparison.OrdinalIgnoreCase)) ?? Scenarios.FirstOrDefault();
    }

    private bool CanPlay() => !IsGameRunning;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        if (Game == null || SelectedScenario == null) return;
        try
        {
            Game.Start(SelectedScenario);
            IsGameRunning = true;
            GameStatus = "Game loading " + System.IO.Path.GetFileName(SelectedScenario) + "…";
            GameBrush = Brushes.Orange;
            Log("Play: launched reach_tag_test on " + SelectedScenario + ". Saved tags reload in the game automatically.");
            BottomPanel = "output";
        }
        catch (Exception ex)
        {
            AddProblem("error", "Could not start the game: " + ex.Message, "Play");
        }
    }

    [RelayCommand(CanExecute = nameof(IsGameRunning))]
    private void StopGame() => Game?.Stop();

    // ---- panels ----------------------------------------------------------------------------

    [RelayCommand] private void ShowExplorer() => SidePanel = "explorer";
    [RelayCommand] private void ShowChanges() { SidePanel = "changes"; _ = RefreshChangesAsync(); }
    [RelayCommand] private void ShowOutput() => BottomPanel = "output";
    [RelayCommand] private void ShowProblems() => BottomPanel = "problems";
    [RelayCommand] private void ClearOutput() => OutputLines.Clear();

    partial void OnSidePanelChanged(string value)
    {
        OnPropertyChanged(nameof(IsExplorer));
        OnPropertyChanged(nameof(IsChangesPanel));
        OnPropertyChanged(nameof(SidePanelTitle));
    }

    partial void OnBottomPanelChanged(string value)
    {
        OnPropertyChanged(nameof(IsOutput));
        OnPropertyChanged(nameof(IsProblems));
    }

    partial void OnActiveTabChanged(TagEditorViewModel? oldValue, TagEditorViewModel? newValue)
    {
        if (oldValue != null) oldValue.IsActive = false;
        if (newValue != null) newValue.IsActive = true;
        OnPropertyChanged(nameof(HasTab));
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
