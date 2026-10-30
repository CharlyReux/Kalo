using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Core.Scripting;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// A gametype script (Megalo source, data\multiplayer\megalo\*.txt) with the compiler HREK's MegaloEdit uses:
/// errors with line numbers, budget stats, declared variables.
/// </summary>
public partial class ScriptEditorViewModel : DocumentViewModel
{
    private readonly MainViewModel _main;
    private Encoding _encoding = Encoding.Unicode;
    private string _savedText = "";

    /// <param name="path">Relative to the workspace, e.g. data\multiplayer\megalo\slayer.txt</param>
    public ScriptEditorViewModel(MainViewModel main, string path) : base(path)
    {
        _main = main;
    }


    // ---- completion --------------------------------------------------------------------------

    private static readonly string[] Keywords =
    {
        "trigger", "end", "action", "condition", "if", "not", "temporary", "include", "localized_include", "override", "option",
        "game_options", "string_table", "constants", "loadout", "loadout_palette", "teams", "team", "engine_data", "player_rating",
        "map_permissions", "variables", "requisition_palette", "hud_widgets", "map_object", "game_stats", "networked", "local",
        "true", "false", "number", "object", "player", "timer", "general", "initialization", "local_initialization", "pregame",
        "current_player", "current_team", "current_object", "for_each", "designator",
    };

    private static readonly string[] KaloKeywords =
    {
        "if", "then", "else", "elseif", "end", "for", "each", "do", "inline", "on", "init", "and", "or", "not", "temp", "declare", "alias",
        "megalo", "include", "with", "label", "networked", "local", "global", "player", "team", "object", "number", "timer",
        "current_player", "current_team", "current_object", "none", "true", "false", "random",
    };

    private readonly HashSet<string> _scriptNames = new(StringComparer.Ordinal);

    /// <summary>
    /// Suggestions for the word being typed at the end of <paramref name="lineBeforeCaret"/>: actions after "action",
    /// conditions after "condition", otherwise keywords, the engine's value lists and the script's own names.
    /// Returns where the word starts in the line.
    /// </summary>
    public (int Start, List<CompletionItem> Items) Complete(string lineBeforeCaret, bool explicitRequest = false)
    {
        int start = lineBeforeCaret.Length;
        while (start > 0 && (char.IsLetterOrDigit(lineBeforeCaret[start - 1]) || lineBeforeCaret[start - 1] == '_')) start--;
        string prefix = lineBeforeCaret[start..];
        string before = lineBeforeCaret[..start].TrimEnd();
        string last = before.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var catalog = _main.MegaloCatalog;
        IEnumerable<CompletionItem> pool;
        bool contextual = false;
        if (last == "action" && !before.EndsWith('.'))
        {
            pool = (catalog?.Actions ?? new()).Select(a => new CompletionItem(a.Name!, "action", a.Description));
            contextual = true;
        }
        else if (last == "condition")
        {
            pool = new[] { new CompletionItem("if", "keyword", "condition if <a> <op> <b>: compare two values") }
                .Concat((catalog?.Conditions ?? new()).Select(c => new CompletionItem(c.Name!, "condition", c.Description)));
            contextual = true;
        }
        else if (IsKaloScript)
        {
            // Kalo Script: actions and conditions are called like functions, so offer them everywhere.
            pool = KaloKeywords.Select(k => new CompletionItem(k, "keyword", null))
                .Concat((catalog?.Actions ?? new()).Select(a => new CompletionItem(a.Name!, "action", a.Description)))
                .Concat((catalog?.Conditions ?? new()).Where(c => c.Name != "if").Select(c => new CompletionItem(c.Name!, "condition", c.Description)))
                .Concat(_scriptNames.Select(n => new CompletionItem(n, "script", "Declared in this script (from the last compile)")))
                .Concat((catalog?.Lists ?? new()).SelectMany(kv => kv.Value.Select(v => new CompletionItem(v, kv.Key, null))));
        }
        else
        {
            pool = Keywords.Select(k => new CompletionItem(k, "keyword", null))
                .Concat(_scriptNames.Select(n => new CompletionItem(n, "script", "Declared in this script (from the last compile)")))
                .Concat((catalog?.Lists ?? new()).SelectMany(kv => kv.Value.Select(v => new CompletionItem(v, kv.Key, null))));
        }
        if (!contextual && !explicitRequest && prefix.Length < 2) return (start, new List<CompletionItem>());
        var items = pool
            .Where(i => prefix.Length == 0 || i.Text.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => i.Text).Select(g => g.First())
            .OrderBy(i => i.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(i => contextual && i.Kind == "keyword" ? 0 : 1) // "condition if" first
            .ThenBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
            .Take(300)
            .ToList();
        return (start, items);
    }

    public override string Badge => IsKaloScript ? "kalo" : "script";

    /// <summary>Kalo Script (.kscript, modern syntax) rather than Megalo text (.txt).</summary>
    public bool IsKaloScript => IsKaloScriptPath(Path);
    public bool IsMegaloText => !IsKaloScript;
    public static bool IsKaloScriptPath(string path) => path.EndsWith(KaloScript.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>For Kalo Script: the Megalo text it turns into (shown with "Megalo" in the toolbar).</summary>
    [ObservableProperty] private string _generatedMegalo = "";
    [ObservableProperty] private bool _showMegalo;
    private TranspileResult? _lastTranspile;

    /// <summary>The text for HREK's compiler: the script itself, or Kalo Script turned into Megalo (null + errors when it can't be).</summary>
    public (string? Megalo, TranspileResult? Kalo) MegaloText()
    {
        if (!IsKaloScript) return (Text, null);
        var r = KaloScript.Transpile(Text);
        _lastTranspile = r;
        if (r.Succeeded) GeneratedMegalo = r.Megalo;
        return (r.Succeeded ? r.Megalo : null, r);
    }
    public override string Breadcrumb => string.Join(" › ", Path.Split('\\'));
    public override string BaselinePath => Path;
    public string FullPath => System.IO.Path.Combine(_main.Paths!.Workspace, Path);

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _compileSummary = "Not compiled yet. Press Compile (F7).";
    [ObservableProperty] private bool _compileOk;
    [ObservableProperty] private string _catalogQuery = "";
    [ObservableProperty] private ScriptMessage? _selectedMessage;

    public ObservableCollection<ScriptMessage> Messages { get; } = new();
    public bool HasMessages => Messages.Count > 0;
    public ObservableCollection<StatRow> Stats { get; } = new();
    public ObservableCollection<MegaloEntry> CatalogResults { get; } = new();
    public ObservableCollection<string> Variables { get; } = new();

    /// <summary>The view moves the caret here (1-based line, offset in the text).</summary>
    public event Action<int, int, int>? RevealRequested;
    /// <summary>The view inserts this text at the caret.</summary>
    public event Action<string>? InsertRequested;

    public async Task LoadAsync()
    {
        var (text, encoding) = await Task.Run(() => ReadScript(FullPath));
        _encoding = encoding;
        _savedText = text;
        Text = text;
        IsDirty = false;
        Status = $"{text.Split('\n').Length:N0} lines · {(_encoding is UnicodeEncoding ? "UTF-16" : "UTF-8")}";
        UpdateCatalog();
    }

    /// <summary>Reads a Megalo script (usually UTF-16 with a byte order mark).</summary>
    public static (string Text, Encoding Encoding) ReadScript(string fullPath)
    {
        byte[] bytes = File.ReadAllBytes(fullPath);
        var encoding = Detect(bytes);
        string text = encoding.GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        return (text, encoding);
    }

    private static Encoding Detect(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode;
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode;
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return new UTF8Encoding(true);
        return new UTF8Encoding(false);
    }

    partial void OnTextChanged(string value) => IsDirty = value != _savedText;
    partial void OnCatalogQueryChanged(string value) => UpdateCatalog();

    partial void OnSelectedMessageChanged(ScriptMessage? value)
    {
        if (value != null && value.Line > 0) RevealRequested?.Invoke(value.Line, value.Start, value.Length);
    }

    public void UpdateCatalog()
    {
        CatalogResults.Clear();
        var catalog = _main.MegaloCatalog;
        if (catalog == null) return;
        string[] words = CatalogQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<MegaloEntry> all = (catalog.Actions ?? new()).Select(a => new MegaloEntry { Name = "action " + a.Name, Description = a.Description })
            .Concat((catalog.Conditions ?? new()).Select(c => new MegaloEntry { Name = "condition " + c.Name, Description = c.Description }));
        foreach (var e in all)
        {
            string hay = e.Name + " " + e.Description;
            if (words.All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase))) CatalogResults.Add(e);
            if (CatalogResults.Count >= 200) break;
        }
    }

    [RelayCommand]
    private void Insert(MegaloEntry? entry)
    {
        if (entry?.Name == null) return;
        // Kalo Script calls actions and conditions like functions: "action set_score" → "set_score(".
        InsertRequested?.Invoke(IsKaloScript ? entry.Name[(entry.Name.IndexOf(' ') + 1)..] + "(" : entry.Name + " ");
    }

    [RelayCommand]
    public Task Compile() => CompileAsync(null);

    private async Task CompileAsync(string? mgloFile, string? binFile = null)
    {
        if (_main.Host == null || _main.Paths == null) return;
        CompileSummary = "Compiling…";
        try
        {
            string output = System.IO.Path.Combine(_main.Paths.KaloData, "megalo", System.IO.Path.GetFileNameWithoutExtension(Path));
            var (megalo, kalo) = MegaloText();
            if (megalo == null)
            {
                // Kalo Script errors: no need to ask the Megalo compiler.
                Messages.Clear();
                foreach (var e in kalo!.Errors) Messages.Add(new ScriptMessage(e.Line, -1, 0, "error", e.Message, null));
                OnPropertyChanged(nameof(HasMessages));
                CompileOk = false;
                CompileSummary = $"{kalo.Errors.Count} error(s) in the Kalo Script. Fix the first one and compile again.";
                _main.ReportScriptProblems(this, Messages);
                SelectedMessage = Messages.FirstOrDefault();
                return;
            }
            var r = await _main.Host.MegaloCompileAsync(new MegaloCompileParams
            {
                ObjectLists = _main.Paths.MegaloObjectLists,
                IncludeFolder = _main.Paths.MegaloSource,
                Text = megalo,
                OutputFolder = output,
                MgloFile = mgloFile,
                BinFile = binFile
            });
            Messages.Clear();
            foreach (var m in r.Messages ?? new())
            {
                if (kalo != null)
                {
                    // Point at the Kalo Script line the Megalo line came from.
                    int line = kalo.SourceLine(m.Line);
                    string where = m.Line > 0 && line == 0 ? $" (generated Megalo line {m.Line})" : "";
                    Messages.Add(new ScriptMessage(line, -1, 0, m.Severity ?? "error", (m.Message ?? "") + where, m.Token));
                }
                else Messages.Add(new ScriptMessage(m.Line, m.Start, m.Length, m.Severity ?? "error", m.Message ?? "", m.Token));
            }
            Stats.Clear();
            var stats = r.Stats ?? new();
            void Stat(string label, string key, long? max = null)
            {
                if (stats.TryGetValue(key, out long v)) Stats.Add(new StatRow(label, max.HasValue ? $"{v:N0} / {max:N0}" : v.ToString("N0")));
            }
            Stat("Triggers", "TriggersUsed");
            Stat("Conditions", "ConditionsUsed");
            Stat("Actions", "ActionsUsed");
            Stat("Strings", "StringsUsed");
            Stat("String bytes", "StringBytesUsed");
            Stat("Game options", "GameOptionsUsed");
            Stat("Player traits", "PlayerTraitsUsed");
            Stat("Loadouts", "LoadoutsUsed");
            Stat("HUD widgets", "HudWidgetsUsed");
            Stat("Encoded size (bytes)", "EncodedSize");
            Variables.Clear();
            _scriptNames.Clear();
            foreach (var kv in r.Variables ?? new())
            {
                if (kv.Value.Count > 0) Variables.Add($"{kv.Key}: {string.Join(", ", kv.Value)}");
                foreach (var name in kv.Value) if (!string.IsNullOrWhiteSpace(name)) _scriptNames.Add(name);
            }
            OnPropertyChanged(nameof(HasMessages));
            CompileOk = r.Succeeded;
            int errors = Messages.Count(m => m.IsError);
            CompileSummary = r.Succeeded
                ? $"Compiled in {r.Ms} ms" + (Messages.Count > 0 ? $" with {Messages.Count} warning(s)" : "") + "."
                : $"{errors} error(s). Fix the first one and compile again.";
            _main.ReportScriptProblems(this, Messages);
            if (!r.Succeeded && Messages.Count > 0) SelectedMessage = Messages[0];
        }
        catch (HostException ex)
        {
            CompileOk = false;
            CompileSummary = "The compiler stopped: " + ex.Message;
            _main.AddProblem("error", ex.Message, Path);
            if (ex.EngineHalted) await _main.RestartHostAsync();
        }
    }

    /// <summary>
    /// Writes the gametype as an MCC custom game type file (.bin) in Kalo\exports\gametypes, ready to copy into
    /// MCC's game type folder. Experimental: built from the HREK compiler's variant.
    /// </summary>
    [RelayCommand]
    public async Task<string?> ExportBin()
    {
        if (_main.Paths == null) return null;
        string name = System.IO.Path.GetFileNameWithoutExtension(Path);
        string compiled = System.IO.Path.Combine(_main.Paths.KaloData, "megalo-bin", name + ".bin");
        try { if (File.Exists(compiled)) File.Delete(compiled); } catch { }
        await CompileAsync(null, compiled);
        if (!CompileOk || !File.Exists(compiled))
        {
            _main.AddProblem("error", "Export .bin: the gametype has to compile first.", Path);
            return null;
        }
        try
        {
            string title = TitleFromScript() ?? name.Replace('_', ' ');
            byte[] mcc = GameVariantFile.ToMcc(await File.ReadAllBytesAsync(compiled), title, "Made with Kalo");
            string folder = System.IO.Path.Combine(_main.Paths.Repo, "exports", "gametypes");
            Directory.CreateDirectory(folder);
            string file = System.IO.Path.Combine(folder, name + ".bin");
            await File.WriteAllBytesAsync(file, mcc);
            CompileSummary = $"Exported {name}.bin ({mcc.Length:N0} bytes) to Kalo\\exports\\gametypes.";
            _main.Log($"Exported {file}. To try it in MCC, copy it into your Halo Reach game types folder " +
                      @"(%USERPROFILE%\AppData\LocalLow\MCC\LocalFiles\<your id>\HaloReach\GameType) and pick it in Custom Games. Experimental.");
            return file;
        }
        catch (Exception ex)
        {
            _main.AddProblem("error", "Export .bin failed: " + ex.Message, Path);
            return null;
        }
    }

    /// <summary>A "-- title: My Mode" line at the top of the script names the exported game type.</summary>
    private string? TitleFromScript()
    {
        foreach (var raw in Text.Split('\n').Take(10))
        {
            string l = raw.Trim().TrimStart('-', ';', '/').Trim();
            if (l.StartsWith("title:", StringComparison.OrdinalIgnoreCase)) return l[6..].Trim();
        }
        return null;
    }

    /// <summary>Megalo text → a new Kalo Script file in the active mod (the original stays as it is).</summary>
    [RelayCommand]
    private async Task ConvertToKaloScript()
    {
        if (IsKaloScript) return;
        string kscript = KaloScript.FromMegalo(Text);
        var check = KaloScript.Transpile(kscript);
        if (!check.Succeeded)
        {
            _main.AddProblem("warning", $"Converted with {check.Errors.Count} problem(s) to fix by hand (first: line {check.Errors[0].Line}: {check.Errors[0].Message}).", Path);
        }
        await _main.CreateKaloScriptAsync(System.IO.Path.GetFileNameWithoutExtension(Path), kscript);
    }

    [RelayCommand]
    private void ToggleMegalo()
    {
        if (!ShowMegalo) MegaloText();
        ShowMegalo = !ShowMegalo;
    }

    [RelayCommand]
    private async Task Save()
    {
        try
        {
            byte[] preamble = _encoding.GetPreamble();
            byte[] body = _encoding.GetBytes(Text);
            await using (var f = File.Create(FullPath))
            {
                await f.WriteAsync(preamble);
                await f.WriteAsync(body);
            }
            _savedText = Text;
            IsDirty = false;
            Status = "Saved";
            _main.OnDocumentSaved(this);
        }
        catch (Exception ex)
        {
            _main.AddProblem("error", "Save failed: " + ex.Message, Path);
        }
    }

    public override Task SaveDocumentAsync() => SaveCommand.ExecuteAsync(null);

    /// <summary>Closing the script also clears its compile errors from the Problems panel.</summary>
    public override Task CloseAsync()
    {
        _main.ReportScriptProblems(this, Array.Empty<ScriptMessage>());
        return Task.CompletedTask;
    }

    public void Reveal(int line, int start, int length) => RevealRequested?.Invoke(line, start, length);
}

public sealed record ScriptMessage(int Line, int Start, int Length, string Severity, string Message, string? Token)
{
    public bool IsError => Severity.Contains("error", StringComparison.OrdinalIgnoreCase);
    public string Where => Line > 0 ? $"line {Line}" : "";
    public string Display => $"{Message}" + (string.IsNullOrEmpty(Token) ? "" : $"  ('{Token}')");
}

public sealed record StatRow(string Label, string Value);

/// <summary>One autocomplete suggestion: the word, where it comes from (action, condition, weapons...), its signature or help.</summary>
public sealed record CompletionItem(string Text, string Kind, string? Description);

/// <summary>A script in the Gametypes panel.</summary>
public sealed record ScriptFile(string Name, string RelativePath, string Folder);
