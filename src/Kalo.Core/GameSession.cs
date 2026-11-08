using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kalo.Core;

/// <summary>
/// Play test: runs reach_tag_test from the shared workspace on a scenario, the way HREK's Foundation
/// does (bonobo_init.txt next to the exe). The game reloads tags saved while it runs (hot reload).
/// </summary>
public sealed class GameSession : IDisposable
{
    private readonly KaloPaths _paths;
    private Process? _process;
    private CancellationTokenSource? _tail;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _consumed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastReload = new(StringComparer.OrdinalIgnoreCase);
    private string? _logFolder;
    private bool _loadedRaised;

    public event Action<string>? Output;
    /// <summary>Raised with the tag path when the game reports it reloaded a changed tag.</summary>
    public event Action<string>? TagReloaded;
    public event Action? Loaded;
    public event Action<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };
    /// <summary>
    /// Set before Start for a campaign mission: it has no "new round", so the map counts as loaded once game time runs
    /// (log lines carry a game tick, e.g. "g000000") or its mission scripts start.
    /// </summary>
    public bool Campaign { get; set; }
    private bool _campaign;
    private static readonly Regex GameTick = new(@"\sg\d{6}\s", RegexOptions.Compiled);
    public string? Scenario { get; private set; }

    public GameSession(KaloPaths paths) => _paths = paths;

    /// <summary>Set before Start: the game starts with the engine's on-screen text and error markers hidden.</summary>
    public bool CleanScreen { get; set; }

    /// <summary>Console commands that hide (or show again) the engine's text and error geometry drawn over the game. The log files keep everything.</summary>
    public static IReadOnlyList<string> ScreenCommands(bool clean) => new[]
    {
        $"(events_suppress_display {(clean ? "true" : "false")})",
        $"(events_suppress_console_display {(clean ? "true" : "false")})",
        clean ? "(error_geometry_hide_all)" : "(error_geometry_show_all)",
    };

    /// <summary>Test option (--init-order after): the console commands go after game_start instead of before.</summary>
    public static string InitOrder { get; set; } = "before";

    /// <param name="scenario">Tag path without extension, e.g. levels\multi\20_sword_slayer\20_sword_slayer</param>
    public void Start(string scenario) => Start(scenario, Array.Empty<string>());

    /// <param name="initCommands">Console commands run before the map starts (e.g. load_binary_game_engine).</param>
    public void Start(string scenario, IReadOnlyList<string> initCommands) => Start(scenario, initCommands, null);

    /// <summary>
    /// False: the map's scripts do not run (run_game_scripts 0), e.g. to walk around a campaign mission without its
    /// cutscenes and AI. Reset to true by each Start that does not set it.
    /// </summary>
    public bool RunScripts { get; set; } = true;

    /// <param name="afterCommands">Console commands run once the map has started (e.g. switch_zone_set).</param>
    public void Start(string scenario, IReadOnlyList<string> initCommands, IReadOnlyList<string>? afterCommands)
    {
        if (IsRunning) throw new InvalidOperationException("The game is already running.");
        string exe = Path.Combine(_paths.Workspace, "reach_tag_test.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("reach_tag_test.exe not found in the workspace", exe);
        if (!File.Exists(Path.Combine(_paths.WorkspaceTags, scenario + ".scenario")))
            throw new FileNotFoundException("Scenario not found: " + scenario);

        string commands = string.Concat(initCommands.Select(c => c + "\r\n"));
        string after = string.Concat((afterCommands ?? Array.Empty<string>()).Select(c => c + "\r\n"));
        string scripts = $"run_game_scripts {(RunScripts ? 1 : 0)}\r\n";
        // Both apply to this start only.
        _campaign = Campaign;
        Campaign = false;
        RunScripts = true;
        if (CleanScreen) commands = string.Concat(ScreenCommands(true).Select(c => c + "\r\n")) + commands;
        string init = InitOrder == "after"
            ? scripts + $"game_start {scenario}\r\n" + commands + after
            : scripts + commands + $"game_start {scenario}\r\n" + after;
        File.WriteAllText(Path.Combine(_paths.Workspace, "bonobo_init.txt"), init);
        Scenario = scenario;

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = _paths.Workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process = process;
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.Exited += (_, _) =>
        {
            int code = 0;
            try { code = process.ExitCode; } catch { }
            // A restart replaces the process: only the current one's exit stops the log tail.
            if (ReferenceEquals(_process, process)) StopTail();
            Exited?.Invoke(code);
        };
        lock (_gate)
        {
            _consumed.Clear();
            _lastReload.Clear();
            _logFolder = null;
            _loadedRaised = false;
        }
        DateTime startUtc = DateTime.UtcNow;
        _process.Start();
        StartTail(startUtc);
        _process.StandardInput.Close();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>A line from the game's console output or from its log files. Each event is raised once.</summary>
    private void OnLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Output?.Invoke(line);
        OnEvent(line);
    }

    private void OnEvent(string line)
    {
        const string marker = "tags:xsync: reloaded ";
        int i = line.IndexOf(marker, StringComparison.Ordinal);
        if (i >= 0)
        {
            string tag = line.Substring(i + marker.Length).Trim().TrimEnd('.', ' ');
            bool raise;
            lock (_gate)
            {
                // The same reload can show up in the console and in a log file.
                raise = !_lastReload.TryGetValue(tag, out var last) || DateTime.UtcNow - last > TimeSpan.FromSeconds(3);
                _lastReload[tag] = DateTime.UtcNow;
            }
            if (raise) TagReloaded?.Invoke(tag);
        }
        if (line.Contains("END initialize for new round", StringComparison.Ordinal) || (_campaign && (GameTick.IsMatch(line) || line.Contains(" hs: ", StringComparison.Ordinal))))
        {
            bool raise;
            lock (_gate) { raise = !_loadedRaised; _loadedRaised = true; }
            if (raise) Loaded?.Invoke();
        }
    }

    // ---- the engine's log files ------------------------------------------------------------
    // reach_tag_test writes most of its log to workspace\reports\reports_NN\*.txt rather than the console.
    // The files are reused from run to run (new lines are appended after the old ones), kept open, and
    // padded with NULs. So they are re-read whole and cleaned, only complete lines past what was already
    // seen are considered, and only lines stamped after this game started count (the old runs' lines
    // also contain "END initialize for new round" and reload messages).

    /// <summary>Logs shown in the Output panel; the others are only scanned for events.</summary>
    private static readonly HashSet<string> ShownLogs = new(StringComparer.OrdinalIgnoreCase) { "debug", "tag_debug", "hs_log", "system_log" };
    private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);
    /// <summary>"21:29:44.510 0001008 g000005 f0000003  message tags:..." → "21:29:44 tags:..."</summary>
    private static readonly Regex Prefix = new(@"^(\d\d:\d\d:\d\d)\.\d+\s+\d+\s+(?:g\d+\s+)?f\d+\s+(?:networking info\s+)?(?:message\s+)?", RegexOptions.Compiled);

    private void StartTail(DateTime startUtc)
    {
        var cts = new CancellationTokenSource();
        _tail = cts;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { PollLogs(startUtc); } catch { }
                try { await Task.Delay(700, cts.Token); } catch (OperationCanceledException) { break; }
            }
            try { PollLogs(startUtc); } catch { }
        });
    }

    private void StopTail()
    {
        try { _tail?.Cancel(); } catch { }
    }

    private void PollLogs(DateTime startUtc)
    {
        string reports = Path.Combine(_paths.Workspace, "reports");
        if (!Directory.Exists(reports)) return;
        DateTime since = startUtc.AddSeconds(-2);
        string? folder = Directory.GetDirectories(reports, "reports_*")
            .Select(d => new DirectoryInfo(d))
            .Select(d => new { d.FullName, Newest = d.GetFiles("*.txt").Select(f => f.LastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max() })
            .Where(d => d.Newest >= since)
            .OrderByDescending(d => d.Newest)
            .Select(d => d.FullName)
            .FirstOrDefault();
        if (folder == null) return;

        var lines = new List<string>();
        lock (_gate)
        {
            if (!string.Equals(folder, _logFolder, StringComparison.OrdinalIgnoreCase))
            {
                _logFolder = folder;
                _consumed.Clear();
            }
            foreach (string file in Directory.GetFiles(folder, "*.txt"))
            {
                var info = new FileInfo(file);
                if (info.Length > 64L * 1024 * 1024) continue;
                string text;
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    text = reader.ReadToEnd().Replace("\0", "");
                }
                catch (IOException) { continue; }

                string name = info.Name;
                _consumed.TryGetValue(name, out int done);
                if (text.Length < done) done = 0; // the file was rewritten
                int end = text.LastIndexOf('\n');
                if (end < done) continue;
                string log = Path.GetFileNameWithoutExtension(name);
                bool full = log.Equals("debug_full", StringComparison.OrdinalIgnoreCase); // a copy of the others
                foreach (string raw in text.Substring(done, end - done).Split('\n'))
                {
                    string line = raw.Trim();
                    if (full || line.Length < 22) continue;
                    if (!DateTime.TryParseExact(line.Substring(0, 21), "MM.dd.yy HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime stamp)) continue;
                    if (stamp.ToUniversalTime() < since) continue;
                    lines.Add((ShownLogs.Contains(log) ? "" : "\u0001") + Prefix.Replace(Spaces.Replace(line.Substring(9), " "), "$1 "));
                }
                _consumed[name] = end + 1;
            }
        }
        foreach (string line in lines)
        {
            if (line[0] == '\u0001') OnEvent(line.Substring(1));
            else OnLine(line);
        }
    }

    /// <summary>
    /// Runs console commands in the running game. Kalo's xinput1_4.dll (next to the game) reads kalo_command.txt on the
    /// game's thread and appends "ok &lt;command&gt;" or "failed &lt;command&gt;" to kalo_command.log. Returns true when every
    /// command was run (whatever its result) within the timeout.
    /// </summary>
    public async Task<bool> RunConsoleAsync(IReadOnlyList<string> commands, TimeSpan timeout, CancellationToken cancel = default)
    {
        if (!IsRunning) return false;
        string file = Path.Combine(_paths.Workspace, "kalo_command.txt");
        string log = Path.Combine(_paths.Workspace, "kalo_command.log");
        int before = File.Exists(log) ? File.ReadAllLines(log).Length : 0;
        File.WriteAllText(file, string.Join("\r\n", commands) + "\r\n");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < timeout && IsRunning)
        {
            await Task.Delay(200, cancel);
            if (File.Exists(file)) continue;
            int lines = 0;
            try { lines = File.Exists(log) ? File.ReadAllLines(log).Length : 0; } catch (IOException) { continue; }
            if (lines - before >= commands.Count) return true;
        }
        try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
        return false;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        try
        {
            _process!.CloseMainWindow();
            if (!_process.WaitForExit(5000)) _process.Kill(true);
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        StopTail();
        _process?.Dispose();
    }
}
