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
    public MainViewModel Main => _main;
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

    /// <summary>Signature, then the plain-language help (for the autocomplete tooltip).</summary>
    private static string? WithDoc(string? signature, IReadOnlyDictionary<string, MegaloDocs.Doc> docs, string name) =>
        docs.TryGetValue(name, out var d) ? signature + "\n\n" + d.Text : signature;

    private static readonly string[] KaloKeywords =
    {
        "if", "then", "else", "elseif", "end", "for", "each", "do", "inline", "on", "init", "and", "or", "not", "temp", "declare", "alias",
        "megalo", "include", "with", "label", "networked", "local", "global", "player", "team", "object", "number", "timer", "enum", "function",
        "setting", "option", "value", "range", "traits", "hidden", "locked", "objects", "constant", "widget", "stat", "engine", "teams", "loadout",
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
            pool = (catalog?.Actions ?? new()).Select(a => new CompletionItem(a.Name!, "action", WithDoc(a.Description, MegaloDocs.Actions, a.Name!)));
            contextual = true;
        }
        else if (last == "condition")
        {
            pool = new[] { new CompletionItem("if", "keyword", "condition if <a> <op> <b>: compare two values") }
                .Concat((catalog?.Conditions ?? new()).Select(c => new CompletionItem(c.Name!, "condition", WithDoc(c.Description, MegaloDocs.Conditions, c.Name!))));
            contextual = true;
        }
        else if (IsKaloScript && lineBeforeCaret[..start].EndsWith('.')
                 && KaloCompletion.Members(BaseBeforeDot(lineBeforeCaret[..(start - 1)]), Text) is { } members)
        {
            // current_player. → its variables (declared in the script) and score / team.
            pool = members.Select(m => new CompletionItem(m.Text, m.Kind, m.Note));
            contextual = true;
        }
        else if (IsKaloScript && KaloCompletion.EnumValues(lineBeforeCaret, Text) is { } enumValues)
        {
            pool = enumValues.Select(m => new CompletionItem(m.Text, m.Kind, m.Note));
            contextual = true;
        }
        else if (IsKaloScript && ArgumentSuggestions(lineBeforeCaret) is { } inCall)
        {
            // Inside a call's parentheses: what this argument takes (a player, an object type...), not every action.
            pool = inCall;
            contextual = true;
        }
        else if (IsKaloScript)
        {
            // Kalo Script: actions and conditions are called like functions, so offer them everywhere.
            pool = KaloKeywords.Select(k => new CompletionItem(k, "keyword", null))
                .Concat((catalog?.Actions ?? new()).Select(a => new CompletionItem(a.Name!, "action", WithDoc(MegaloDocs.KaloSignature(a.Name!, a.Description), MegaloDocs.Actions, a.Name!))))
                .Concat((catalog?.Conditions ?? new()).Where(c => c.Name != "if").Select(c => new CompletionItem(c.Name!, "condition", WithDoc(MegaloDocs.KaloSignature(c.Name!, c.Description), MegaloDocs.Conditions, c.Name!))))
                .Concat(_scriptNames.Select(n => new CompletionItem(n, "script", "Declared in this script (from the last compile)")))
                .Concat(UserFunctions().Select(f => new CompletionItem(f.Name, "function", $"{f.Name}({f.Params}) — your function")))
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

    /// <summary>A user function's parameters as an argument list ("&lt;p&gt; &lt;points&gt;"), or null.</summary>
    private string? UserSignature(string name)
    {
        var f = UserFunctions().FirstOrDefault(x => x.Name == name);
        return f.Name == null ? null : string.Join(" ", f.Params.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(p => $"<{p}>"));
    }

    /// <summary>The functions this script defines: name and parameter list.</summary>
    private IEnumerable<(string Name, string Params)> UserFunctions() =>
        System.Text.RegularExpressions.Regex.Matches(Text, @"(?m)^\s*function\s+(\w+)\s*\(([^)]*)\)")
            .Select(m => (m.Groups[1].Value, string.Join(", ", m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))));

    /// <summary>"... killer" → "killer"; "x(current_player" → "current_player": the name before a dot.</summary>
    private static string BaseBeforeDot(string text)
    {
        int b = text.Length;
        while (b > 0 && (char.IsLetterOrDigit(text[b - 1]) || text[b - 1] == '_' || text[b - 1] == '.')) b--;
        return text[b..];
    }

    /// <summary>The argument hints of the call the caret is in (Kalo Script), or null when not in a known call.</summary>
    private List<CompletionItem>? ArgumentSuggestions(string lineBeforeCaret)
    {
        var catalog = _main.MegaloCatalog;
        string? SignatureOf(string name) =>
            catalog?.Actions?.FirstOrDefault(a => a.Name == name)?.Description ?? catalog?.Conditions?.FirstOrDefault(c => c.Name == name && name != "if")?.Description
            ?? UserSignature(name);
        var r = KaloCompletion.Suggest(lineBeforeCaret, Text, SignatureOf, catalog?.Lists);
        if (r == null) return null;
        var (call, slots, items) = r.Value;
        string signature = MegaloDocs.KaloSignature(call.Name, SignatureOf(call.Name));
        return items.Select(i => new CompletionItem(i.Text, i.Kind, $"{signature}\nArgument {call.Index + 1}: {i.Note}")).ToList();
    }

    /// <summary>For the status line while typing in a call: "player_set_unit(<player>, ▸<unit>)" (Kalo Script).</summary>
    public string? CallHint(string lineBeforeCaret)
    {
        if (!IsKaloScript) return null;
        var call = KaloCompletion.CallAt(lineBeforeCaret);
        var catalog = _main.MegaloCatalog;
        string? sig = call == null ? null : catalog?.Actions?.FirstOrDefault(a => a.Name == call.Name)?.Description ?? catalog?.Conditions?.FirstOrDefault(c => c.Name == call.Name)?.Description
            ?? UserSignature(call.Name);
        if (call == null || sig == null) return null;
        var args = MegaloDocs.KaloArguments(sig);
        var slots = KaloCompletion.Slots(sig);
        var current = KaloCompletion.Candidates(slots, call).FirstOrDefault();
        int at = current == null ? -1 : slots.IndexOf(current);
        return $"{call.Name}(" + string.Join(", ", args.Select((a, i) => i == at ? "▸" + a : a)) + ")" + (current == null ? "" : "   " + KaloCompletion.Describe(current.Kind));
    }

    // ---- hover help ---------------------------------------------------------------------------

    /// <summary>
    /// Help for the word at a 0-based column of a line: actions, conditions, keywords, built-in names, the script's
    /// own variables (where they are declared) and the engine's value lists. Null when there's nothing to say.
    /// </summary>
    public HoverInfo? Hover(string line, int column)
    {
        if (column < 0 || column > line.Length) return null;
        int start = column, end = column;
        while (start > 0 && IsWordChar(line[start - 1])) start--;
        while (end < line.Length && IsWordChar(line[end])) end++;
        if (end <= start) return null;
        string word = line[start..end];
        if (char.IsDigit(word[0])) return null;

        // Comments: nothing. Strings: only the engine's value names ("sniper_rifle").
        bool inString = false;
        for (int i = 0; i < start; i++)
        {
            char c = line[i];
            if (c == '"') inString = !inString;
            else if (!inString && (c == ';' && !IsKaloScript || (IsKaloScript && c == '-' && i + 1 < line.Length && line[i + 1] == '-'))) return null;
        }
        bool afterDot = start > 0 && line[start - 1] == '.';
        var catalog = _main.MegaloCatalog;

        if (!inString)
        {
            if (MegaloDocs.Find(word, IsKaloScript, afterDot) is { } found)
            {
                var (title, doc) = found;
                string? megalo = null, signature = null;
                if (title.StartsWith("action ") || title.StartsWith("condition "))
                {
                    bool condition = title.StartsWith("condition ");
                    megalo = (condition ? catalog?.Conditions : catalog?.Actions)?.FirstOrDefault(e => e.Name == word)?.Description;
                    signature = word == "if" && condition && IsKaloScript ? "a == b   (also != < > <= >=)"
                        : IsKaloScript ? MegaloDocs.KaloSignature(word, megalo) : $"{(condition ? "condition" : "action")} {word} {megalo}".TrimEnd();
                }
                return new HoverInfo(title, signature, IsKaloScript ? megalo : null, doc.Text, doc.Example);
            }
            if (Declaration(word) is { } declared) return declared;
        }
        foreach (var (list, values) in catalog?.Lists ?? new())
            if (values.Contains(word, StringComparer.OrdinalIgnoreCase))
                return new HoverInfo(word, null, null, $"A name from the game's {ListLabel(list)} list.", null);
        if (!inString && _scriptNames.Contains(word))
            return new HoverInfo(word, null, null, "Declared in this script (seen by the last compile).", null);
        return null;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string ListLabel(string key) => key switch
    {
        "objectTypes" => "object type", "weaponSets" => "weapon set", "vehicleSets" => "vehicle set", "hudWidgetIcons" => "HUD widget icon",
        "loadoutPalettes" => "loadout palette", "gameOptions" => "game option", "incidents" => "incident (medal, announcer, statistic)",
        _ => key.TrimEnd('s'),
    };

    /// <summary>Where the script declares this name: declare / temp / alias, Megalo variables, options, traits, map objects.</summary>
    private HoverInfo? Declaration(string word)
    {
        string w = System.Text.RegularExpressions.Regex.Escape(word);
        var lines = Text.Split('\n');
        string scope = "global";
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i].Trim();
            var v = System.Text.RegularExpressions.Regex.Match(l, @"^variables\s+(\w+)");
            if (v.Success) scope = v.Groups[1].Value;
            System.Text.RegularExpressions.Match m;
            string at = $" · line {i + 1}";
            if ((m = System.Text.RegularExpressions.Regex.Match(l, $@"^declare\s+(?:(networked\w*|local)\s+)?(\w+)\.(\w+)\s+{w}\b(?:\s*=\s*(.+))?")).Success)
                return new HoverInfo(word, $"{m.Groups[2].Value}.{m.Groups[3].Value} {word}" + at,
                    null, Describe(m.Groups[2].Value, m.Groups[3].Value, m.Groups[1].Value, m.Groups[4].Value), null);
            if ((m = System.Text.RegularExpressions.Regex.Match(l, $@"^(?:temp|temporary)\s+(\w+)\s+{w}\b(?:\s*=?\s*(.+))?")).Success)
                return new HoverInfo(word, $"temp {m.Groups[1].Value} {word}" + at, null,
                    $"A temporary {m.Groups[1].Value}: it only lives during this tick" + (m.Groups[2].Success ? $", starting at {m.Groups[2].Value.Trim()}." : "."), null);
            if ((m = System.Text.RegularExpressions.Regex.Match(l, $@"^(networked\w*|local)\s+(\w+)\s+{w}\b\s*(.*)")).Success)
                return new HoverInfo(word, $"{scope} {m.Groups[2].Value} {word}" + at, null, Describe(scope, m.Groups[2].Value, m.Groups[1].Value, m.Groups[3].Value), null);
            if ((m = System.Text.RegularExpressions.Regex.Match(l, $@"^function\s+{w}\s*\(([^)]*)\)")).Success)
            {
                // The comment just above the function, if any, describes it.
                string about = i > 0 && lines[i - 1].Trim().StartsWith("--") ? lines[i - 1].Trim()[2..].Trim() + " " : "";
                return new HoverInfo("function " + word, $"{word}({string.Join(", ", m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))})" + at,
                    null, about + "Your function: each call runs a copy of its body.", null);
            }
            if ((m = System.Text.RegularExpressions.Regex.Match(l, $@"^alias\s+{w}\s*=\s*(.+)")).Success)
                return new HoverInfo(word, "alias" + at, null, "Another name for " + m.Groups[1].Value.Trim() + ".", null);
            if ((m = System.Text.RegularExpressions.Regex.Match(l, $@"^(?:(?:hidden|locked)\s+)*(ranged_option|option|player_traits|traits|map_object|objects|constant)\s+{w}\b")).Success)
            {
                string kind = m.Groups[1].Value;
                string label = lines.Skip(i + 1).Take(2).Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith('"'))?.Trim('"') ?? "";
                string what = kind switch
                {
                    "ranged_option" or "option" => "A lobby option: use it like a number.",
                    "player_traits" or "traits" => "Player traits: apply them with apply_player_traits(player, " + word + ").",
                    "map_object" or "objects" => "A set of map objects (by label or type): loop with 'for each " + word + " do'.",
                    "constant" => "A constant number.",
                    _ => "Declared in the script.",
                };
                return new HoverInfo(word, kind + " " + word + at, null, (label.Length > 0 ? $"\"{label}\". " : "") + what, null);
            }
        }
        return null;
    }

    private static string Describe(string scope, string type, string network, string value)
    {
        string owner = scope switch
        {
            "player" => "Each player has their own (current_player." ,
            "team" => "Each team has its own (current_team.",
            "object" => "Each object has its own (current_object.",
            _ => "",
        };
        string text = scope == "global" ? $"A {type} kept between ticks." : $"A {type} kept between ticks. {owner}…).";
        if (network == "local") text += " Local: not sent to players.";
        if (!string.IsNullOrWhiteSpace(value)) text += $" Starts at {value.Trim()}.";
        return text;
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
    /// <summary>Format document (Ctrl+Shift+I): the view applies it as one undo step and keeps the caret's line.</summary>
    public event Action? FormatRequested;

    [RelayCommand]
    private void Format() => FormatRequested?.Invoke();

    /// <summary>The script formatted (indentation, spaces, blank lines); only whitespace changes.</summary>
    public ScriptFormatter.Result Formatted(string text) => ScriptFormatter.Format(text, IsKaloScript);

    /// <summary>Shows what Format did in the status line.</summary>
    public void FormatDone(string message) => CompileSummary = message + " (Ctrl+Shift+I)";

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

    partial void OnTextChanged(string value)
    {
        IsDirty = value != _savedText;
        ScheduleCheck();
    }
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
    public Task Compile()
    {
        _checkGeneration++; // a pending check as you type is replaced by this compile
        return CompileAsync(null);
    }

    // ---- checking as you type: errors underlined in red, listed below the code -----------------------------

    /// <summary>The latest errors and warnings, for the red / yellow underlines.</summary>
    public IReadOnlyList<ScriptMessage> Diagnostics { get; private set; } = Array.Empty<ScriptMessage>();
    public event Action? DiagnosticsChanged;
    private int _checkGeneration;
    /// <summary>The text the diagnostics belong to (the editor maps them onto the current text).</summary>
    public string DiagnosticsText { get; private set; } = "";

    /// <summary>HREK's compiler messages in plain words where they are cryptic (the original follows).</summary>
    private static string Friendly(string message)
    {
        if (message.Contains("'less_than', 'greater_than'") && !message.Contains("'equal_to'"))
            return "A timer can only be compared with < or > (or tested with timer_expired). " + message;
        return message;
    }

    /// <summary>
    /// Object types that don't exist in create_object / object_is_type (Kalo Script): a clear error with the closest
    /// real names (HREK's own message is hard to read).
    /// </summary>
    private List<ScriptMessage> UnknownObjectTypes(string text)
    {
        var result = new List<ScriptMessage>();
        var lists = _main.MegaloCatalog?.Lists;
        if (lists == null || !lists.TryGetValue("objectTypes", out var types) || types == null || types.Count == 0) return result;
        var known = new HashSet<string>(types, StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var calls = new[]
        {
            new System.Text.RegularExpressions.Regex(@"\bcreate_object\s*\(\s*""?(\w+)""?\s*[,)]"),
            new System.Text.RegularExpressions.Regex(@"\bobject_is_type\s*\(\s*[\w.]+\s*,\s*""?(\w+)""?\s*\)"),
        };
        for (int i = 0; i < lines.Length; i++)
        {
            string code = lines[i];
            int comment = code.IndexOf("--", StringComparison.Ordinal);
            if (comment >= 0) code = code[..comment];
            foreach (var call in calls)
            foreach (System.Text.RegularExpressions.Match m in call.Matches(code))
            {
                string type = m.Groups[1].Value;
                if (known.Contains(type)) continue;
                var close = types.Select(t => (t, d: Distance(t, type))).OrderBy(x => x.d).Take(3).Select(x => x.t).ToList();
                if (type.Contains("cam") || type.Contains("spect") || type.Contains("ghost")) close.Insert(0, "monitor");
                result.Add(new ScriptMessage(i + 1, -1, 0, "error",
                    $"\"{type}\" is not an object type. Close names: {string.Join(", ", close.Distinct().Take(3))}. (The flying Forge camera is \"monitor\"; all types: Reference › Value lists.)", type));
            }
        }
        return result;
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>Megalo's own members of players, teams and objects (anything else after a dot must be declared).</summary>
    private static readonly HashSet<string> BuiltinMembers = new(StringComparer.Ordinal) { "score", "team", "biped", "rating", "money", "spawn_sequence", "owner" };

    /// <summary>
    /// "Expected a variable reference ... but got 'current_player'" on a Kalo Script line that uses current_player.x:
    /// x is not a variable. Explains it and underlines the whole current_player.x. Null when it is something else.
    /// </summary>
    private (string Message, string Token)? UndeclaredMember(string message, string? token, int kaloLine)
    {
        if (token == null || !message.Contains("Expected a variable reference")) return null;
        var lines = Text.Replace("\r\n", "\n").Split('\n');
        if (kaloLine < 1 || kaloLine > lines.Length) return null;
        var m = System.Text.RegularExpressions.Regex.Match(lines[kaloLine - 1], $@"\b{System.Text.RegularExpressions.Regex.Escape(token)}\.(\w+)");
        if (!m.Success || BuiltinMembers.Contains(m.Groups[1].Value)) return null;
        string member = m.Groups[1].Value;
        var declared = KaloCompletion.Scan(Text).Members;
        if (declared.Any(d => d.Name == member)) return null; // declared: something else is wrong
        string scope = token switch { "current_team" => "team", "current_object" => "object", _ => KaloCompletion.Scan(Text).Of("team").Contains(token) ? "team" : KaloCompletion.Scan(Text).Of("object").Contains(token) ? "object" : "player" };
        string text = $"{token}.{member}: there is no {scope} variable called {member}. Declare it at the top of the script: declare {scope}.number {member} = 0";
        if (member is "deaths" or "kills" or "lives")
            text += member == "kills"
                ? $". Megalo doesn't count kills for you: add 1 to the killer when player_died(current_player, enemy) is true (see the starter script)."
                : $". Megalo doesn't count deaths for you: add 1 when player_died(current_player, any) is true, e.g. if player_died(current_player, any) then current_player.{member} += 1 end";
        return (text + ".", m.Value);
    }

    /// <summary>
    /// Shows a check's messages (list, Problems, underlines). Nothing is redrawn when they did not change. While typing,
    /// a new error waits for a longer pause first, so half-written lines don't flash red. False when dropped (text changed).
    /// </summary>
    private async Task<bool> ApplyMessagesAsync(List<ScriptMessage> fresh, string text, bool live, Func<bool> stale)
    {
        if (live && fresh.Any(m => m.IsError && !Messages.Contains(m)))
        {
            await Task.Delay(900);
            if (stale()) return false;
        }
        if (!live || !fresh.SequenceEqual(Messages))
        {
            Messages.Clear();
            foreach (var m in fresh) Messages.Add(m);
            OnPropertyChanged(nameof(HasMessages));
            _main.ReportScriptProblems(this, Messages, showPanel: !live);
            Diagnostics = Messages.ToList();
            DiagnosticsText = text;
            DiagnosticsChanged?.Invoke();
        }
        return true;
    }

    /// <summary>Checks the code a moment after typing stops: Kalo Script locally, then HREK's Megalo compiler.</summary>
    private async void ScheduleCheck()
    {
        int generation = ++_checkGeneration;
        try
        {
            await Task.Delay(IsKaloScript ? 350 : 700);
            if (generation != _checkGeneration) return;
            await CompileAsync(null, null, generation);
        }
        catch (Exception ex)
        {
            _main.Log("Checking the script: " + ex.Message);
        }
    }

    /// <param name="liveGeneration">Set for a check as you type: quiet (no "Compiling…", no jump to the first error), and dropped if the text changed since.</param>
    private async Task CompileAsync(string? mgloFile, string? binFile = null, int? liveGeneration = null)
    {
        bool live = liveGeneration.HasValue;
        bool Stale() => live && liveGeneration != _checkGeneration;
        string text = Text;
        if (!live) CompileSummary = "Compiling…";
        try
        {
            var (megalo, kalo) = MegaloText();
            if (megalo == null)
            {
                // Kalo Script errors: no need to ask the Megalo compiler.
                var found = kalo!.Errors.Select(e => new ScriptMessage(e.Line, -1, 0, "error", e.Message, null, e.Column)).ToList();
                if (!await ApplyMessagesAsync(found, text, live, Stale)) return;
                CompileOk = false;
                CompileSummary = live ? $"{kalo.Errors.Count} error(s), underlined in red. Point at one to read it."
                    : $"{kalo.Errors.Count} error(s) in the Kalo Script. Fix the first one and compile again.";
                if (!live) SelectedMessage = Messages.FirstOrDefault();
                return;
            }
            if (_main.Host == null || _main.Paths == null) { if (!live) CompileSummary = "The compiler is not ready yet."; return; }
            string output = System.IO.Path.Combine(_main.Paths.KaloData, "megalo", System.IO.Path.GetFileNameWithoutExtension(Path));
            var r = await _main.Host.MegaloCompileAsync(new MegaloCompileParams
            {
                ObjectLists = _main.Paths.MegaloObjectLists,
                IncludeFolder = _main.Paths.MegaloSource,
                Text = megalo,
                OutputFolder = output,
                MgloFile = mgloFile,
                BinFile = binFile
            });
            if (Stale()) return;
            var fresh = new List<ScriptMessage>();
            foreach (var m in r.Messages ?? new())
            {
                if (kalo != null)
                {
                    // Point at the Kalo Script line the Megalo line came from.
                    int line = kalo.SourceLine(m.Line);
                    string where = m.Line > 0 && line == 0 ? $" (generated Megalo line {m.Line})" : "";
                    if (UndeclaredMember(m.Message ?? "", m.Token, line) is { } explained)
                        fresh.Add(new ScriptMessage(line, -1, 0, m.Severity ?? "error", explained.Message, explained.Token));
                    else fresh.Add(new ScriptMessage(line, -1, 0, m.Severity ?? "error", Friendly(m.Message ?? "") + where, m.Token));
                }
                else fresh.Add(new ScriptMessage(m.Line, m.Start, m.Length, m.Severity ?? "error", Friendly(m.Message ?? ""), m.Token));
            }
            if (kalo != null) fresh.InsertRange(0, UnknownObjectTypes(text));
            if (!await ApplyMessagesAsync(fresh, text, live, Stale)) return;
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
            CompileSummary = live
                ? r.Succeeded ? "No errors" + (Messages.Count > 0 ? $" ({Messages.Count} warning(s) listed below)" : "") + ". Checked as you type."
                    : $"{errors} error(s), underlined in red. Point at one to read it."
                : r.Succeeded
                    ? $"Compiled in {r.Ms} ms" + (Messages.Count > 0 ? $" with {Messages.Count} warning(s)" : "") + "."
                    : $"{errors} error(s). Fix the first one and compile again.";
            if (!live && !r.Succeeded && Messages.Count > 0) SelectedMessage = Messages[0];
        }
        catch (HostException ex)
        {
            if (Stale()) return;
            CompileOk = false;
            CompileSummary = "The compiler stopped: " + ex.Message;
            if (!live) _main.AddProblem("error", ex.Message, Path);
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

    /// <summary>Kalo Script: the gametype's settings, options, traits, objects and constants as forms (instead of the code).</summary>
    public GametypeSettingsViewModel GametypeSettings => _settings ??= new GametypeSettingsViewModel(this);
    private GametypeSettingsViewModel? _settings;
    [ObservableProperty] private bool _showSettings;
    public bool ShowCode => !ShowSettings;
    partial void OnShowSettingsChanged(bool value) => OnPropertyChanged(nameof(ShowCode));

    [RelayCommand]
    private void ToggleSettings()
    {
        if (!ShowSettings) GametypeSettings.Reload();
        ShowSettings = !ShowSettings;
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

/// <summary>A compiler message. Start/Length: offsets in the text (Megalo), or -1; Column: 1-based column on Line (Kalo Script), or 0.</summary>
public sealed record ScriptMessage(int Line, int Start, int Length, string Severity, string Message, string? Token, int Column = 0)
{
    public bool IsError => Severity.Contains("error", StringComparison.OrdinalIgnoreCase);
    public string Where => Line > 0 ? $"line {Line}" : "";
    public string Display => $"{Message}" + (string.IsNullOrEmpty(Token) ? "" : $"  ('{Token}')");
}

public sealed record StatRow(string Label, string Value);

/// <summary>Hover help: a title, the call form, the Megalo argument list (Kalo Script), what it does, an example.</summary>
public sealed record HoverInfo(string Title, string? Signature, string? Megalo, string Text, string? Example);

/// <summary>One autocomplete suggestion: the word, where it comes from (action, condition, weapons...), its signature or help.</summary>
public sealed record CompletionItem(string Text, string Kind, string? Description);

/// <summary>A script in the Gametypes panel.</summary>
public sealed record ScriptFile(string Name, string RelativePath, string Folder);
