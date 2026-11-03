using System.Globalization;
using System.Text;

namespace Kalo.Core.Scripting;

/// <summary>
/// Kalo Script: a modern language for gametypes, written in a Python style (indentation, if x:, def, # comments; see
/// KaloScriptStyle; the older then / do / end style still works) (if/then/else, for each, function-call
/// syntax, operators, named temporaries), turned into the Megalo text that HREK's compiler understands.
/// Declarations Megalo already writes well (options, traits, map objects, HUD widgets...) go through unchanged
/// inside <c>megalo [[ ... ]]</c> blocks.
///
/// <code>
/// -- 1 point per kill, 2 for a headshot
/// declare player.number streak = 0
///
/// for each player do
///   if player_died(current_player, enemy) then
///     temp player killer = none
///     player_death_get_killing_player(current_player, killer)
///     killer.score += 1
///     killer.streak += 1
///   else
///     current_player.streak = 0
///   end
/// end
/// </code>
///
/// Mapping: a top-level <c>for each player do</c> is <c>trigger player</c>; <c>on init do</c> is
/// <c>trigger initialization</c>; a nested <c>for each</c> is <c>action for_each</c>; <c>if</c> is an
/// <c>action for_each general</c> block whose conditions gate its body (else / elseif use a temporary flag);
/// <c>a.f(b)</c> is <c>f a b</c>; <c>x += 1</c> is <c>action set x += 1</c>.
/// Megalo joins conditions left to right with no precedence: <c>a or b and c</c> means (a or b) and c.
/// </summary>
public static class KaloScript
{
    public const string Extension = ".kscript";

    /// <summary>Top-level Megalo declarations kept as they are when converting from Megalo text.</summary>
    public static readonly HashSet<string> MegaloDeclarations = new(StringComparer.Ordinal)
    {
        "game_options", "map_object", "constants", "teams", "hud_widgets", "game_stats", "engine_data",
        "map_permissions", "base", "localized_include", "object_filter", "loadout_palettes", "loadout", "loadout_palette",
        "player_rating", "string_table",
    };

    /// <summary>What a top-level <c>on X do</c> becomes.</summary>
    public static readonly IReadOnlyDictionary<string, string> Events = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["init"] = "initialization",
        ["initialization"] = "initialization",
        ["local"] = "local",
        ["host_migration"] = "host_migration",
        ["double_migration"] = "double_migration",
        ["pregame"] = "pregame",
    };

    /// <summary>The starter for new gametypes: points per kill, a streak bonus and a message, with comments.</summary>
    public const string Starter = """
    # Settings and lobby options (the Settings button shows them as a form).
    setting score_to_win_round = 25
    setting round_time_limit = 10
    option kill_points "Kill Points" "How many points you get for killing an enemy" = 1 range -10 10

    # Variables: each player keeps their own kill streak.
    player.streak: number = 0

    for each player:
        # player_died is true on the tick this player was killed by an enemy
        if player_died(current_player, enemy):
            killer: player = none
            player_death_get_killing_player(current_player, killer)
            killer.score += kill_points
            killer.streak += 1
            current_player.streak = 0

            # every 5 kills in a row: a bonus point and a message
            if killer.streak == 5:
                killer.score += 1
                killer.streak = 0
                hud_post_message(player killer, none, "Killing spree! +1")

    """;

    /// <summary>The same starter in the classic style (then / do / end), used by the tour's checks.</summary>
    public const string ClassicStarter = """
    -- Settings and lobby options (the Settings button shows them as a form).
    setting score_to_win_round = 25
    setting round_time_limit = 10
    option kill_points "Kill Points" "How many points you get for killing an enemy" = 1 range -10 10

    -- Variables: each player keeps their own kill streak.
    declare player.number streak = 0

    for each player do
      -- player_died is true on the tick this player was killed by an enemy
      if player_died(current_player, enemy) then
        temp player killer = none
        player_death_get_killing_player(current_player, killer)
        killer.score += kill_points
        killer.streak += 1
        current_player.streak = 0

        -- every 5 kills in a row: a bonus point and a message
        if killer.streak == 5 then
          killer.score += 1
          killer.streak = 0
          hud_post_message(player killer, none, "Killing spree! +1")
        end
      end
    end

    """;

    /// <summary>
    /// Kalo Script → Megalo. Python-style files (the default) are first turned into the classic style, line for line,
    /// so line numbers in errors are the file's own.
    /// </summary>
    public static TranspileResult Transpile(string source)
    {
        if (KaloScriptStyle.IsClassic(source)) return new Transpiler(source).Run();
        var (classic, styleErrors) = KaloScriptStyle.ToClassic(source);
        var r = new Transpiler(classic).Run();
        if (styleErrors.Count == 0 && r.Errors.Count == 0) return r;
        var errors = styleErrors.Concat(r.Errors.Where(e => styleErrors.All(s => s.Line != e.Line)).Select(e => e with { Message = KaloScriptStyle.PythonMessage(e.Message) }))
            .Distinct().OrderBy(e => e.Line).ToList();
        return new TranspileResult { Megalo = styleErrors.Count == 0 ? r.Megalo : "", LineMap = r.LineMap, Errors = errors };
    }

    /// <summary>Converts one Megalo declaration block, or says why it stays Megalo (diagnostics, tests).</summary>
    public static (string? Kalo, string? Reason) ConvertDeclaration(IReadOnlyList<string> megaloLines)
    {
        string? k = DeclarationConverter.Convert(megaloLines);
        return (k, k == null ? DeclarationConverter.LastReason : null);
    }

    /// <summary>Converts Megalo text (a Bungie script or one of yours) to Kalo Script.</summary>
    public static string FromMegalo(string megalo) => Decompiler.Convert(megalo);
}

public sealed record ScriptError(int Line, int Column, string Message)
{
    public override string ToString() => $"line {Line}: {Message}";
}

public sealed class TranspileResult
{
    /// <summary>The Megalo text for the HREK compiler (empty when there are errors).</summary>
    public string Megalo { get; init; } = "";
    public List<ScriptError> Errors { get; init; } = new();
    /// <summary>For each Megalo line (0-based), the Kalo Script line (1-based) it came from; 0 when generated.</summary>
    public List<int> LineMap { get; init; } = new();
    public bool Succeeded => Errors.Count == 0;

    /// <summary>The Kalo Script line for a 1-based Megalo line (compiler messages), or 0.</summary>
    public int SourceLine(int megaloLine) => megaloLine >= 1 && megaloLine <= LineMap.Count ? LineMap[megaloLine - 1] : 0;
}

// ---- lexer ------------------------------------------------------------------------------------------------

internal enum T { Word, Number, String, Op, Punct, Raw, End }

internal sealed record Tok(T Kind, string Text, int Line, int Col)
{
    public bool Is(string text) => (Kind is T.Word or T.Op or T.Punct) && Text == text;
    public override string ToString() => Kind == T.End ? "end of file" : Kind == T.String ? $"\"{Text}\"" : Text;
}

internal static class Lexer
{
    private static readonly string[] Ops =
    {
        "<<=", ">>=", "==", "!=", "<=", ">=", "+=", "-=", "*=", "/=", "%=", "|=", "&=", "^=", "~=", "<", ">", "=", "+", "-", "*", "/", "%",
    };

    public static List<Tok> Run(string src, List<ScriptError> errors)
    {
        var list = new List<Tok>();
        int i = 0, line = 1, lineStart = 0;
        int Col() => i - lineStart + 1;
        while (i < src.Length)
        {
            char c = src[i];
            if (c == '\n') { i++; line++; lineStart = i; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if ((c == '-' && At(src, i, "--")) || (c == '/' && At(src, i, "//")))
            {
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            // megalo [[ raw text ]]
            if (At(src, i, "[["))
            {
                int startLine = line, startCol = Col();
                int close = src.IndexOf("]]", i + 2, StringComparison.Ordinal);
                if (close < 0) { errors.Add(new ScriptError(line, Col(), "[[ without ]]")); close = src.Length; }
                string raw = src.Substring(i + 2, Math.Min(close, src.Length) - i - 2);
                list.Add(new Tok(T.Raw, raw, startLine, startCol));
                for (int k = i; k < Math.Min(close + 2, src.Length); k++)
                    if (src[k] == '\n') { line++; lineStart = k + 1; }
                i = Math.Min(close + 2, src.Length);
                continue;
            }
            if (c == '"')
            {
                int startCol = Col();
                var sb = new StringBuilder();
                i++;
                while (i < src.Length && src[i] != '"' && src[i] != '\n')
                {
                    if (src[i] == '\\' && i + 1 < src.Length && src[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                    sb.Append(src[i++]);
                }
                if (i >= src.Length || src[i] != '"') errors.Add(new ScriptError(line, startCol, "string without a closing quote"));
                else i++;
                list.Add(new Tok(T.String, sb.ToString(), line, startCol));
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
            {
                int s = i, startCol = Col();
                while (i < src.Length && (char.IsDigit(src[i]) || src[i] == '.')) i++;
                list.Add(new Tok(T.Number, src[s..i], line, startCol));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int s = i, startCol = Col();
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                list.Add(new Tok(T.Word, src[s..i], line, startCol));
                continue;
            }
            if (c is '(' or ')' or ',' or '.' or '[' or ']' or '{' or '}')
            {
                list.Add(new Tok(T.Punct, c.ToString(), line, Col()));
                i++;
                continue;
            }
            string? op = Ops.FirstOrDefault(o => At(src, i, o));
            if (op != null)
            {
                list.Add(new Tok(T.Op, op, line, Col()));
                i += op.Length;
                continue;
            }
            errors.Add(new ScriptError(line, Col(), $"unexpected character '{c}'"));
            i++;
        }
        list.Add(new Tok(T.End, "", line, Col()));
        return list;
    }

    private static bool At(string s, int i, string what) => string.CompareOrdinal(s, i, what, 0, what.Length) == 0;
}

// ---- Kalo Script → Megalo ---------------------------------------------------------------------------------

internal sealed class ParseError(Tok at, string message) : Exception(message)
{
    public Tok At { get; } = at;
}

internal sealed partial class Transpiler
{
    private readonly string _source;
    private readonly List<ScriptError> _errors = new();
    private List<Tok> _t = new();
    private int _p;

    // Output: Megalo lines with the source line each came from.
    private readonly List<(string Text, int Line)> _out = new();
    private int _indent;

    // Declarations gathered from the whole file (emitted before the triggers).
    private readonly Dictionary<string, List<(string Text, int Line)>> _vars = new(StringComparer.Ordinal);
    private readonly List<(string Name, string Kind, string Value, int Line)> _mapObjects = new();
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private int _flags;
    private int _varsAt = int.MaxValue;

    private static readonly HashSet<string> Scopes = new() { "global", "player", "team", "object" };
    private static readonly HashSet<string> Types = new() { "number", "object", "player", "team", "timer" };
    private static readonly HashSet<string> Reserved = new()
    {
        "if", "then", "else", "elseif", "end", "for", "each", "do", "on", "and", "or", "not", "declare", "alias",
        "megalo", "include", "with",
    };
    /// <summary>Words that can never be a value (others, like on / type / label, can be passed to Megalo).</summary>
    private static readonly HashSet<string> Structural = new()
    {
        "if", "then", "else", "elseif", "end", "for", "do", "and", "or", "not", "declare", "alias", "megalo",
    };
    private static readonly HashSet<string> AssignOps = new() { "=", "+=", "-=", "*=", "/=", "%=", "|=", "&=", "^=", "~=", "<<=", ">>=" };
    private static readonly HashSet<string> CompareOps = new() { "==", "!=", "<", ">", "<=", ">=" };
    private static readonly Dictionary<string, string> Arithmetic = new() { ["+"] = "+=", ["-"] = "-=", ["*"] = "*=", ["/"] = "/=", ["%"] = "%=" };

    public Transpiler(string source) => _source = source.Replace("\r\n", "\n");

    public TranspileResult Run()
    {
        _t = Lexer.Run(_source, _errors);
        ScanTypes();
        ExtractFunctions();
        var body = new List<(string, int)>();
        // Top level: declarations collected, triggers and raw blocks written in order.
        while (Peek.Kind != T.End)
        {
            int before = _p;
            try
            {
                TopLevel();
            }
            catch (ParseError e)
            {
                _errors.Add(new ScriptError(e.At.Line, e.At.Col, e.Message));
                SkipToTopLevel();
            }
            if (_p == before) _p++;
        }
        if (_errors.Count > 0) return new TranspileResult { Errors = _errors.Distinct().OrderBy(e => e.Line).ToList() };

        // Map objects and variables first, then everything else in source order.
        var final = new List<(string Text, int Line)>
        {
            ("; generated by Kalo from Kalo Script: edit the .kscript file, not this text", 0),
        };
        foreach (var m in _mapObjects)
        {
            final.Add(($"map_object {m.Name}", m.Line));
            final.Add(($"\t{m.Kind} \"{m.Value}\"", m.Line));
            final.Add(("end", m.Line));
        }
        // Variables go where the first declare was (they may use options declared above them);
        // game options in one game_options block where the first one was.
        var vars = new List<(string Text, int Line)>();
        foreach (var (scope, lines) in _vars)
        {
            vars.Add(($"variables {scope}", lines[0].Line));
            vars.AddRange(lines.Select(l => ("\t" + l.Text, l.Line)));
            vars.Add(("end", lines[^1].Line));
        }
        foreach (var line in _out)
        {
            if (line.Text == VarsMarker) final.AddRange(vars);
            else if (line.Text == OptionsMarker)
            {
                final.Add(("game_options", line.Line));
                final.AddRange(_options.Select(o => ("\t" + o.Text, o.Line)));
                final.Add(("end", _options.Count > 0 ? _options[^1].Line : line.Line));
            }
            else final.Add(line);
        }
        return new TranspileResult
        {
            Megalo = string.Join("\r\n", final.Select(l => l.Text)) + "\r\n",
            LineMap = final.Select(l => l.Line).ToList(),
            Errors = _errors,
        };
    }

    // ---- tokens -------------------------------------------------------------------------------------------

    private Tok Peek => _t[_p];
    private Tok PeekAt(int k) => _t[Math.Min(_p + k, _t.Count - 1)];
    private Tok Next() => _t[Math.Min(_p++, _t.Count - 1)];

    private Tok Expect(string text, string? why = null)
    {
        var t = Peek;
        if (!t.Is(text)) throw new ParseError(t, why ?? $"expected '{text}' but found {t}");
        return Next();
    }

    private string Word(string what)
    {
        var t = Peek;
        if (t.Kind != T.Word) throw new ParseError(t, $"expected {what} but found {t}");
        Next();
        return t.Text;
    }

    private void SkipToTopLevel()
    {
        // Resume at the next line that starts at column 1 with a top-level keyword.
        while (Peek.Kind != T.End)
        {
            var t = Next();
            var n = Peek;
            if (n.Col == 1 && n.Line > t.Line && (n.Is("for") || n.Is("on") || n.Is("do") || n.Is("function") || n.Is("declare") || n.Is("alias") || n.Is("megalo") || n.Is("include") ||
                                                    (n.Kind == T.Word && DeclarationWords.Contains(n.Text)))) return;
        }
    }

    private void Emit(string text, int line) => _out.Add((new string('\t', _indent) + text, line));

    // ---- top level ----------------------------------------------------------------------------------------

    private void TopLevel()
    {
        _prevGroup = _group;
        _group = null;
        var t = Peek;
        if (t.Is("megalo"))
        {
            Next();
            var raw = Next();
            if (raw.Kind != T.Raw) throw new ParseError(raw, "expected [[ after megalo");
            var lines = raw.Text.Split('\n');
            // Text after [[ on its own line: only a -- comment makes sense there (it isn't Megalo).
            int dash = lines[0].IndexOf("--", StringComparison.Ordinal);
            if (dash >= 0) lines[0] = lines[0][..dash];
            for (int k = 1; k < lines.Length; k++)
                if (lines[k].TrimStart().StartsWith("--", StringComparison.Ordinal)) lines[k] = lines[k].Replace("--", ";");
            // Keep the raw text's own line numbers so compiler errors point inside the block.
            int first = lines.Length > 0 && lines[0].Trim().Length == 0 ? 1 : 0;
            int last = lines.Length;
            while (last > first && lines[last - 1].Trim().Length == 0) last--;
            int common = lines.Skip(first).Take(last - first).Where(l => l.Trim().Length > 0)
                .Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
            for (int k = first; k < last; k++)
                _out.Add((lines[k].Length >= common ? lines[k][common..].TrimEnd() : lines[k].TrimEnd(), raw.Line + k));
            return;
        }
        if (t.Is("include"))
        {
            Next();
            var file = Next();
            if (file.Kind != T.String) throw new ParseError(file, "include needs a file name in quotes");
            _out.Add(($"include \"{file.Text}\"", t.Line));
            return;
        }
        if (t.Is("declare")) { Declare(); return; }
        if (Declaration()) return;
        if (t.Is("alias"))
        {
            Next();
            string name = Word("an alias name");
            Expect("=");
            // alias goal_message = "{current_player} reached the goal!": a text used in messages (filled in where used).
            _aliases[name] = Peek.Kind == T.String ? "\"" + Next().Text + "\"" : Path();
            return;
        }
        if (t.Is("for"))
        {
            Next();
            Expect("each");
            string kind = ForKind(out _);
            Expect("do");
            Trigger(kind, t.Line);
            return;
        }
        if (t.Is("on"))
        {
            Next();
            var ev = Peek;
            string name = Word("an event (init, local, host_migration, double_migration, pregame)");
            if (!KaloScript.Events.TryGetValue(name, out string? trigger))
                throw new ParseError(ev, $"unknown event '{name}': use init, local, host_migration, double_migration or pregame");
            Expect("do");
            Trigger(trigger, t.Line);
            return;
        }
        if (t.Is("do"))
        {
            Next();
            Trigger("general", t.Line);
            return;
        }
        if (t.Kind == T.Word && !Reserved.Contains(t.Text))
            throw new ParseError(t, $"'{t.Text}' must be inside a trigger: put it in 'do ... end' or 'for each player do ... end'");
        throw new ParseError(t, $"unexpected {t} at the top level");
    }

    /// <summary>declare [networked|local] scope.type name [= value]</summary>
    private void Declare()
    {
        var at = Next();
        string network = "networked";
        if (Peek.Kind == T.Word && (Peek.Text.StartsWith("networked", StringComparison.Ordinal) || Peek.Text == "local")) network = Next().Text;
        var scopeTok = Peek;
        string scope = Word("global, player, team or object");
        if (!Scopes.Contains(scope)) throw new ParseError(scopeTok, $"'{scope}' is not a variable scope: use global, player, team or object");
        Expect(".");
        var typeTok = Peek;
        string type = Word("a type");
        List<string>? choices = null;
        if (type == "enum")
        {
            // declare player.enum{placer, player, spectator} applied_trait = spectator: a number whose values have names.
            Expect("{", "expected { after enum, e.g. enum{red, blue}");
            choices = new List<string>();
            while (true)
            {
                var c = Peek;
                string word = Word("a value name");
                if (choices.Contains(word)) throw new ParseError(c, $"'{word}' is already in this enum");
                choices.Add(word);
                if (Peek.Is(",")) { Next(); continue; }
                break;
            }
            Expect("}", "expected } to close the enum");
            type = "number";
        }
        if (!Types.Contains(type)) throw new ParseError(typeTok, $"'{type}' is not a variable type: use number, timer, object, player, team or enum{{a, b}}");
        var nameTok = Peek;
        string name = Word("a variable name");
        string value = type is "number" or "timer" ? "0" : "none";
        if (Peek.Is("=")) { Next(); value = choices != null ? EnumValue(name, Atom(), nameTok) : Atom(); }
        if (_varsAt == int.MaxValue) { _varsAt = _out.Count; _out.Add((VarsMarker, at.Line)); }
        if (!_vars.TryGetValue(scope, out var list)) _vars[scope] = list = new();
        list.Add(($"{network} {type} {name} {value}", at.Line));
    }

    /// <summary>After "for each": player, team, object, random player, a map object name, or object with label/type "x".</summary>
    private string ForKind(out bool isGeneral)
    {
        isGeneral = false;
        var t = Peek;
        string kind = Word("player, team, object or a map object name");
        if (kind == "random" && Peek.Is("player")) { Next(); return "random_player"; }
        if (kind == "object" && Peek.Is("with"))
        {
            Next();
            var what = Peek;
            string by = Word("label or type");
            if (by is not ("label" or "type")) throw new ParseError(what, "use 'with label \"name\"' or 'with type \"name\"'");
            var v = Next();
            if (v.Kind != T.String) throw new ParseError(v, $"{by} needs a name in quotes");
            string name = "kalo_" + by + "_" + new string(v.Text.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_').ToArray());
            if (_mapObjects.All(m => m.Name != name)) _mapObjects.Add((name, by, v.Text, t.Line));
            return name;
        }
        if (kind == "general") isGeneral = true;
        return kind;
    }

    private void Trigger(string kind, int line)
    {
        Emit("trigger " + kind, line);
        _indent++;
        Block();
        _indent--;
        Emit("end", Expect("end", "missing 'end' for this trigger").Line);
    }

    // ---- statements ---------------------------------------------------------------------------------------

    /// <summary>Statements until end / else / elseif.</summary>
    private void Block()
    {
        while (!(Peek.Is("end") || Peek.Is("else") || Peek.Is("elseif") || Peek.Kind == T.End))
        {
            int before = _p;
            try
            {
                Statement();
            }
            catch (ParseError e)
            {
                _errors.Add(new ScriptError(e.At.Line, e.At.Col, e.Message));
                var first = _t[before];
                if (first.Is("if") || first.Is("for") || first.Is("do") || first.Is("inline"))
                {
                    // A block whose header failed: skip it to its own end, so its body and end aren't
                    // read as the rest of the enclosing block (no knock-on errors further down).
                    _p = before;
                    SkipBlock();
                }
                else
                {
                    // Skip the rest of that line.
                    int line = e.At.Line;
                    while (Peek.Kind != T.End && Peek.Line == line && !Peek.Is("end")) Next();
                }
            }
            if (_p == before && !_expanded) Next();
            _expanded = false;
        }
    }

    /// <summary>Set when a function call was just replaced by its body (parsing goes on at the same place).</summary>
    private bool _expanded;

    /// <summary>Skips a block statement (if / for each / do / inline) up to and including its matching end.</summary>
    private void SkipBlock()
    {
        int depth = 0;
        while (Peek.Kind != T.End)
        {
            var t = Next();
            bool opens = t.Is("if") || t.Is("do") || (t.Is("inline") && Peek.Line > t.Line);
            if (opens) depth++;
            else if (t.Is("end") && --depth <= 0) return;
        }
    }

    private void Statement()
    {
        var t = Peek;
        if (ExpandCall()) { _expanded = true; return; }
        if (t.Is("if")) { If(); return; }
        if (t.Is("for"))
        {
            Next();
            Expect("each");
            string kind = ForKind(out _);
            Expect("do");
            Emit("action for_each " + kind, t.Line);
            _indent++;
            Block();
            _indent--;
            Emit("end", Expect("end", "missing 'end' for this for each").Line);
            return;
        }
        if (t.Is("do"))
        {
            Next();
            Emit("action for_each general", t.Line);
            _indent++;
            Block();
            _indent--;
            Emit("end", Expect("end", "missing 'end' for this do").Line);
            return;
        }
        if (t.Is("inline") && PeekAt(1).Line > t.Line)
        {
            // Megalo's begin ... end: a block that doesn't use a trigger from the budget.
            Next();
            Emit("begin", t.Line);
            _indent++;
            Block();
            _indent--;
            Emit("end", Expect("end", "missing 'end' for this inline block").Line);
            return;
        }
        if (t.Is("temp") && PeekAt(1).Kind == T.Word && Types.Contains(PeekAt(1).Text) && PeekAt(2).Kind == T.Word)
        {
            Next();
            var typeTok = Peek;
            string type = Word("a type");
            if (!Types.Contains(type) || type == "timer") throw new ParseError(typeTok, $"'{type}' can't be a temporary: use number, object, player or team");
            string name = Word("a name");
            string value = type == "number" ? "0" : "none";
            if (Peek.Is("=")) { Next(); value = Atom(); }
            Emit($"temporary {type} {name} {value}", t.Line);
            return;
        }
        if (t.Is("declare") || t.Is("alias")) throw new ParseError(t, $"'{t.Text}' goes at the top of the file, outside triggers");
        if (t.Is("then") || t.Is("and") || t.Is("or")) throw new ParseError(t, $"unexpected '{t.Text}'");
        if (t.Kind != T.Word) throw new ParseError(t, $"unexpected {t}");

        // A call, or an assignment.
        int start = _p;
        string path = Path();
        if (Peek.Is("("))
        {
            _p = start;
            var (name, args) = Call();
            Emit($"action {name}{Args(args)}", t.Line);
            return;
        }
        var op = Peek;
        if (op.Kind == T.Op && AssignOps.Contains(op.Text))
        {
            Next();
            if (Peek.Kind == T.End || Peek.Line != op.Line) throw new ParseError(op, $"missing the value after {op.Text}");
            var valueTok = Peek;
            string value = Atom();
            if (value.StartsWith('"'))
                throw new ParseError(valueTok, "Megalo has no text variables. Name a text with alias at the top of the file (alias message = \"{current_player} reached the goal!\") and use the name in the call, or write the text in the call itself");
            value = EnumValue(path, value, valueTok);
            if (Peek.Kind == T.Op && Arithmetic.TryGetValue(Peek.Text, out string? then) && Peek.Line == op.Line)
            {
                var arith = Next();
                string right = Atom();
                if (op.Text != "=") throw new ParseError(arith, "write one operation per line, e.g. x += a then x += b");
                if (right == path)
                {
                    if (arith.Text is "+" or "*") { Emit($"action set {path} {then} {value}", t.Line); return; }
                    throw new ParseError(arith, $"{path} = {value} {arith.Text} {path} needs a temporary: Megalo applies one operation at a time");
                }
                Emit($"action set {path} = {value}", t.Line);
                Emit($"action set {path} {then} {right}", t.Line);
                return;
            }
            Emit($"action set {path} {op.Text} {value}", t.Line);
            return;
        }
        throw new ParseError(op, $"expected '(' to call {path}, or = += -= to change it");
    }

    private void If()
    {
        var start = Next(); // if
        var conditions = Conditions();
        Expect("then", "expected 'then' after the condition");
        bool chain = Peek.Kind != T.End && HasElse();
        string? flag = null;
        if (chain)
        {
            flag = $"kalo_if_{++_flags}";
            Emit($"temporary number {flag} 0", start.Line);
        }
        int wrapper = _out.Count;
        Emit("action for_each general", start.Line);
        _indent++;
        foreach (var c in conditions) Emit(c.Text, c.Line);
        if (flag != null) Emit($"action set {flag} = 1", start.Line);
        Block();
        _indent--;
        Emit("end", Peek.Line);
        if (flag == null)
        {
            Expect("end", "missing 'end' for this if");
            // An if that ends its block needs no block of its own: Megalo conditions gate what follows them.
            // (Saves a trigger; converted Bungie scripts compile back to the same structure.)
            if (_indent > 0 && (Peek.Is("end") || Peek.Is("else") || Peek.Is("elseif")))
            {
                _out.RemoveAt(_out.Count - 1);
                _out.RemoveAt(wrapper);
                for (int k = wrapper; k < _out.Count; k++)
                    if (_out[k].Text.StartsWith('\t')) _out[k] = (_out[k].Text[1..], _out[k].Line);
            }
            return;
        }
        while (Peek.Is("elseif"))
        {
            var e = Next();
            var more = Conditions();
            Expect("then", "expected 'then' after the condition");
            Emit("action for_each general", e.Line);
            _indent++;
            Emit($"condition if {flag} == 0", e.Line);
            foreach (var c in more) Emit(c.Text, c.Line);
            Emit($"action set {flag} = 1", e.Line);
            Block();
            _indent--;
            Emit("end", Peek.Line);
        }
        if (Peek.Is("else"))
        {
            var e = Next();
            Emit("action for_each general", e.Line);
            _indent++;
            Emit($"condition if {flag} == 0", e.Line);
            Block();
            _indent--;
            Emit("end", Peek.Line);
        }
        Expect("end", "missing 'end' for this if");
    }

    /// <summary>True when the if starting here has an else or elseif at its own level.</summary>
    private bool HasElse()
    {
        int depth = 0;
        for (int k = _p; k < _t.Count; k++)
        {
            var t = _t[k];
            if (t.Kind == T.End) return false;
            if (t.Is("if") || t.Is("do") || (t.Is("inline") && k + 1 < _t.Count && _t[k + 1].Line > t.Line)) depth++;
            else if (t.Is("end"))
            {
                if (depth == 0) return false;
                depth--;
            }
            else if (depth == 0 && (t.Is("else") || t.Is("elseif"))) return true;
        }
        return false;
    }

    /// <summary>
    /// A condition expression → Megalo condition lines. and binds tighter than or, parentheses group, not negates
    /// (also a group). Megalo can only AND groups of ORed conditions, so the expression is rewritten in that form:
    /// a or (b and c) becomes (a or b) and (a or c). "or" at the end of a line joins it with the next.
    /// </summary>
    private List<(string Text, int Line)> Conditions()
    {
        var expr = OrExpr(out _);
        var clauses = Cnf(expr, false);
        int count = clauses.Sum(c => c.Count);
        if (count > 32) throw new ParseError(Peek, $"this condition becomes {count} Megalo conditions: split it into nested ifs");
        var list = new List<(string Text, int Line)>();
        foreach (var clause in clauses)
            for (int k = 0; k < clause.Count; k++)
            {
                var (lit, negated) = clause[k];
                list.Add(((negated ? "condition not " : "condition ") + lit.Text + (k < clause.Count - 1 ? " or" : ""), lit.Line));
            }
        return list;
    }

    private abstract record CondNode;
    private sealed record CondLit(string Text, int Line) : CondNode;
    private sealed record CondNot(CondNode A) : CondNode;
    private sealed record CondAnd(CondNode A, CondNode B) : CondNode;
    private sealed record CondOr(CondNode A, CondNode B) : CondNode;

    /// <param name="mixed">True when the expression is an unparenthesized "and" chain (for the and/or mixing check).</param>
    private CondNode OrExpr(out bool mixed)
    {
        var left = AndExpr(out bool leftAnd);
        mixed = leftAnd;
        bool anyOr = false, anyAnd = leftAnd;
        Tok? orTok = null;
        while (Peek.Is("or"))
        {
            orTok ??= Peek;
            Next();
            anyOr = true;
            var right = AndExpr(out bool rightAnd);
            anyAnd |= rightAnd;
            left = new CondOr(left, right);
        }
        if (anyOr && anyAnd)
            throw new ParseError(orTok!, "and / or are mixed: add parentheses to say which you mean, e.g. (a or b) and c, or a or (b and c)");
        if (anyOr) mixed = false;
        return left;
    }

    private CondNode AndExpr(out bool hasAnd)
    {
        var left = Unary();
        hasAnd = false;
        while (Peek.Is("and"))
        {
            Next();
            hasAnd = true;
            left = new CondAnd(left, Unary());
        }
        return left;
    }

    private CondNode Unary()
    {
        if (Peek.Is("not")) { Next(); return new CondNot(Unary()); }
        if (Peek.Is("("))
        {
            Next();
            var inner = OrExpr(out _);
            Expect(")", "expected ')' to close the parentheses");
            return inner;
        }
        var t = Peek;
        return new CondLit(Condition(), t.Line);
    }

    /// <summary>The expression as ANDed groups of ORed conditions (each possibly negated).</summary>
    private static List<List<(CondLit Lit, bool Negated)>> Cnf(CondNode node, bool negate)
    {
        switch (node)
        {
            case CondLit lit:
                return new() { new() { (lit, negate) } };
            case CondNot n:
                return Cnf(n.A, !negate);
            case CondAnd when !negate:
            case CondOr when negate: // not (a or b) = not a and not b
            {
                var (x, y) = node is CondAnd an ? (an.A, an.B) : (((CondOr)node).A, ((CondOr)node).B);
                return Cnf(x, negate).Concat(Cnf(y, negate)).ToList();
            }
            default:
            {
                // a or b (or not (a and b) = not a or not b): every group of one side with every group of the other.
                var (x, y) = node is CondOr either ? (either.A, either.B) : (((CondAnd)node).A, ((CondAnd)node).B);
                var left = Cnf(x, negate);
                var right = Cnf(y, negate);
                return left.SelectMany(l => right.Select(r => l.Concat(r).ToList())).ToList();
            }
        }
    }

    /// <summary>Declared variable names → type (number, timer, object, player, team), from declare and temp anywhere in the script.</summary>
    private readonly Dictionary<string, string> _types = new(StringComparer.Ordinal);

    private void ScanTypes()
    {
        for (int k = 0; k + 2 < _t.Count; k++)
        {
            if (_t[k].Is("declare"))
            {
                int j = k + 1;
                while (j < _t.Count && _t[j].Kind == T.Word && (_t[j].Text.StartsWith("networked", StringComparison.Ordinal) || _t[j].Text == "local")) j++;
                if (j + 3 < _t.Count && _t[j + 1].Is(".") && _t[j + 2].Is("enum") && _t[j + 3].Is("{"))
                {
                    // scope.enum{a, b, c} name
                    var values = new List<string>();
                    int e = j + 4;
                    for (; e < _t.Count && !_t[e].Is("}") && _t[e].Kind != T.End; e++) if (_t[e].Kind == T.Word) values.Add(_t[e].Text);
                    if (e + 1 < _t.Count && _t[e + 1].Kind == T.Word)
                    {
                        _types[_t[e + 1].Text] = "number";
                        _enums[_t[e + 1].Text] = values;
                    }
                }
                else if (j + 3 < _t.Count && _t[j + 1].Is(".") && _t[j + 2].Kind == T.Word && _t[j + 3].Kind == T.Word) _types[_t[j + 3].Text] = _t[j + 2].Text;
            }
            else if (_t[k].Is("temp") && _t[k + 1].Kind == T.Word && Types.Contains(_t[k + 1].Text) && _t[k + 2].Kind == T.Word)
                _types[_t[k + 2].Text] = _t[k + 1].Text;
            else if ((_t[k].Is("option") || _t[k].Is("constant")) && (k == 0 || _t[k - 1].Line < _t[k].Line || _t[k - 1].Is("hidden") || _t[k - 1].Is("locked"))
                     && _t[k + 1].Kind == T.Word && !_types.ContainsKey(_t[k + 1].Text))
                _types[_t[k + 1].Text] = "number"; // lobby options and constants are numbers
        }
    }

    /// <summary>Enum variables → their value names (index = value).</summary>
    private readonly Dictionary<string, List<string>> _enums = new(StringComparer.Ordinal);

    /// <summary>
    /// A value given to (or compared with) an enum variable: its value names become their numbers. Numbers and other
    /// variables pass through; an unknown bare name is an error listing the names.
    /// </summary>
    private string EnumValue(string variable, string value, Tok at)
    {
        string last = variable[(variable.LastIndexOf('.') + 1)..];
        if (!_enums.TryGetValue(last, out var names)) return value;
        int i = names.IndexOf(value);
        if (i >= 0) return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool other = value.Length == 0 || !char.IsLetter(value[0]) && value[0] != '_' || value.Contains('.') || _types.ContainsKey(value)
            || value is "current_player" or "current_team" or "current_object" or "none";
        if (!other) throw new ParseError(at, $"'{value}' is not a value of {last}: use {string.Join(", ", names)}");
        return value;
    }

    /// <summary>The declared type of a value written in the script (x, current_player.x), or null.</summary>
    private string? TypeOf(string value)
    {
        string last = value[(value.LastIndexOf('.') + 1)..];
        return _types.TryGetValue(last, out var type) ? type : null;
    }

    /// <summary>A declared name close to this one (one or two letters different), for "did you mean".</summary>
    private string? SimilarName(string name, string type)
    {
        static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }
        string last = name[(name.LastIndexOf('.') + 1)..];
        return _types.Where(kv => kv.Value == type && kv.Key != last).Select(kv => (kv.Key, D: Distance(kv.Key, last)))
            .Where(x => x.D <= 2).OrderBy(x => x.D).Select(x => x.Key).FirstOrDefault();
    }

    private string Condition()
    {
        var t = Peek;
        int start = _p;
        if (t.Kind == T.Word && !Reserved.Contains(t.Text))
        {
            Path();
            if (Peek.Is("("))
            {
                _p = start;
                if (_functions.ContainsKey(t.Text))
                    throw new ParseError(t, $"{t.Text} is a function: call it on its own line (functions run actions and give no true / false)");
                var (name, args) = Call();
                return name + Args(args);
            }
            _p = start;
        }
        string left = Atom();
        var op = Peek;
        if (op.Kind != T.Op || !CompareOps.Contains(op.Text))
        {
            if (op.Is("=")) throw new ParseError(op, "use == to compare (= changes a value)");
            throw new ParseError(op, $"expected a comparison (== != < > <= >=) after {left}");
        }
        Next();
        var rightTok = Peek;
        string right = Atom();
        right = EnumValue(left, right, rightTok);
        left = EnumValue(right, left, t);
        // Megalo compares timers only with < and > (the compiler's message for == is hard to read).
        if (op.Text is "==" or "!=" or "<=" or ">=")
            foreach (string side in new[] { left, right })
                if (TypeOf(side) == "timer")
                {
                    string? other = SimilarName(side, "number");
                    throw new ParseError(op, $"{side} is a timer: a timer can only be compared with < or > (e.g. {side} < 5), or tested with timer_expired({side})."
                        + (other != null ? $" Did you mean the number {other}?" : ""));
                }
        return $"if {left} {op.Text} {right}";
    }

    /// <summary>name(args) or subject.name(args) → (name, [subject, args...]).</summary>
    private (string Name, List<string> Args) Call()
    {
        var parts = new List<string> { Word("a name") };
        while (Peek.Is(".")) { Next(); parts.Add(Word("a name")); }
        string name = parts[^1];
        var args = new List<string>();
        if (parts.Count > 1) args.Add(Resolve(string.Join(".", parts.Take(parts.Count - 1))));
        var open = Expect("(");
        if (!Peek.Is(")"))
        {
            while (true)
            {
                args.Add(Argument(open));
                if (Peek.Is(",")) { Next(); continue; }
                break;
            }
        }
        Expect(")", $"expected ')' to close {name}(");
        CheckPlayerFilter(name, args, open);
        CheckButton(name, args, open);
        CheckObjectGroup(name, args, open);
        // "{current_player} reached the goal!": Megalo's format tokens (%p player, %t team, %n number, %s timer), the
        // values added after the text. At most two per text.
        for (int i = 0; i < args.Count; i++)
        {
            if (!args[i].StartsWith('"') || !args[i].Contains('{')) continue;
            var (format, values) = Interpolate(args[i], open);
            args[i] = format;
            args.InsertRange(i + 1, values);
            i += values.Count;
        }
        return (name, args);
    }

    /// <summary>Actions whose second argument says who sees / can use something: no_one, everyone, allies, enemies, or player P B.</summary>
    private static readonly HashSet<string> PlayerFilterActions = new(StringComparer.Ordinal)
    {
        "boundary_set_visible", "navpoint_set_visible", "set_pickup_filter", "set_respawn_filter", "set_progress_bar",
    };

    /// <summary>
    /// boundary_set_visible(obj, player) used to fail in HREK's compiler with "Failed to parse action!" plus confusing errors
    /// on the next line (it read the next line as the missing player): the player form needs the player and a 1/0 after it.
    /// </summary>
    private void CheckPlayerFilter(string name, List<string> args, Tok at)
    {
        if (!PlayerFilterActions.Contains(name)) return;
        int filter = args.FindIndex(a => a == "player");
        if (filter < 0) return;
        bool complete = args.Count >= filter + 3 && args[filter + 2] is "0" or "1" or "true" or "false";
        if (complete) return;
        string first = string.Join(", ", args.Take(filter));
        throw new ParseError(at, $"'player' here means one player only: write which player, then 1 or 0, e.g. {name}({first}, player, current_player, 1). " +
                                 "Or use no_one, everyone, allies or enemies");
    }

    /// <summary>
    /// get_button_time takes a button word without quotes (fire_primary, jump...). A quoted or unknown name made HREK's
    /// compiler fail the whole line and then complain about the next lines' variables.
    /// </summary>
    private void CheckButton(string name, List<string> args, Tok at)
    {
        if (name != "get_button_time" || args.Count < 2) return;
        string button = args[1].Trim();
        string bare = button.Trim('"');
        if (button == bare && MegaloDocs.Buttons.Contains(bare)) return;
        string hint = bare is "fire" or "fire_weapon" or "shoot" or "attack" ? "fire_primary" : bare is "melee" ? "melee_attack" : bare is "zoom" ? "scope_zoom" : "";
        string what = button != bare && MegaloDocs.Buttons.Contains(bare) ? $"write the button without quotes: {bare}"
            : $"\"{bare}\" is not a button" + (hint != "" ? $" (did you mean {hint}?)" : "");
        throw new ParseError(at, $"get_button_time: {what}. Buttons: {string.Join(", ", MegaloDocs.Buttons)}");
    }

    /// <summary>
    /// "objects NAME with label ..." declares a group of map objects (a Megalo filter), which only for each and
    /// object_matches_filter accept. Passed as an object, HREK's compiler reads it as an unknown player name
    /// ("Invalid explicit player") and then fails on everything after it.
    /// </summary>
    private static readonly HashSet<string> SingleObjectActions = new(StringComparer.Ordinal)
    {
        "object_attach", "object_detach", "object_destroy", "delete_object", "object_set_invincibility", "object_get_distance",
        "object_set_scale", "object_copy_rotation", "object_face_object", "object_get_orientation", "object_get_velocity",
        "object_set_shape", "set_boundary", "boundary_set_visible", "boundary_set_player_color", "navpoint_set_visible",
        "navpoint_set_icon", "navpoint_set_priority", "navpoint_set_timer", "navpoint_set_text", "navpoint_set_visible_range",
        "set_pickup_filter", "set_respawn_filter", "set_progress_bar", "object_set_health", "object_get_health",
        "object_set_shield", "object_get_shield", "object_set_progress_bar", "object_set_spawn_location_permissions",
    };

    private void CheckObjectGroup(string name, List<string> args, Tok at)
    {
        // Only actions that take one object: others legitimately take a filter (get_random_object, for_each...) or
        // words that can share a group's name (sounds, incidents).
        if (!SingleObjectActions.Contains(name)) return;
        foreach (string arg in args)
        {
            if (!_objectFilters.Contains(arg.Trim())) continue;
            string g = arg.Trim();
            throw new ParseError(at, $"{g} is a group of map objects (objects {g} with label ...), not one object, so it can't be passed to {name}. " +
                $"Pick one of its objects into an object variable first: declare global.object my_{g} = none, then get_random_object({g}, none, my_{g}) " +
                $"(or for each {g} do ... current_object ... end)");
        }
    }

    private (string Format, List<string> Values) Interpolate(string text, Tok at)
    {
        var sb = new StringBuilder();
        var values = new List<string>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{' && i + 1 < text.Length && text[i + 1] == '{') { sb.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < text.Length && text[i + 1] == '}') { sb.Append('}'); i++; continue; }
            if (c != '{') { sb.Append(c); continue; }
            int close = text.IndexOf('}', i + 1);
            if (close < 0) throw new ParseError(at, "a { in a text is not closed (write {{ for a { character)");
            string expr = text[(i + 1)..close].Trim();
            if (expr.EndsWith(".name", StringComparison.Ordinal)) expr = expr[..^5]; // {current_player.name}: the player shows as their name
            expr = Resolve(expr);
            if (System.Text.RegularExpressions.Regex.IsMatch(expr, @"^-?\d+(\.\d+)?$"))
            {
                // A number written in the code (e.g. a function's argument): just part of the text.
                sb.Append(expr);
                i = close;
                continue;
            }
            string? type = expr switch
            {
                "current_player" => "player",
                "current_team" => "team",
                "current_object" => "object",
                _ when expr.Length > 0 && (char.IsDigit(expr[0]) || expr[0] == '-') => "number",
                _ when expr.EndsWith(".score", StringComparison.Ordinal) || expr == "score" => "number",
                _ when expr.EndsWith(".team", StringComparison.Ordinal) => "team",
                _ => TypeOf(expr),
            };
            string spec = type switch
            {
                "player" => "%p",
                "team" => "%t",
                "number" => "%n",
                "timer" => "%s",
                "object" => throw new ParseError(at, $"{{{expr}}}: objects can't be shown in a text (players, teams, numbers and timers can)"),
                _ when _checkingBody => "%n", // a function's parameter: known once called
                _ => throw new ParseError(at, $"{{{expr}}}: Kalo doesn't know what {expr} is. Declare it (declare / temp), or use current_player, current_team..."),
            };
            sb.Append(spec);
            values.Add(expr);
            if (values.Count > 2) throw new ParseError(at, "a text can show at most two values ({...})");
            i = close;
        }
        return (sb.ToString(), values);
    }

    /// <summary>
    /// One argument: usually a value, but Megalo arguments can be several words (player killer, = 1,
    /// at current_object offset 0 0 5): everything up to the next comma or closing parenthesis.
    /// </summary>
    private string Argument(Tok open)
    {
        var parts = new List<string>();
        while (!(Peek.Is(",") || Peek.Is(")")))
        {
            var t = Peek;
            if (t.Kind == T.End || t.Kind == T.Raw || (t.Line != open.Line && parts.Count == 0 && t.Is("end")))
                throw new ParseError(t, "expected ')' to close the call");
            if (t.Is("("))
                throw new ParseError(t, "Megalo can't nest calls: store the result in a temp first");
            if (t.Kind == T.Op && t.Text == "-" && PeekAt(1).Kind == T.Number) { Next(); parts.Add("-" + Next().Text); continue; }
            if (t.Kind == T.Word) { parts.Add(t.Text == "end" ? Next().Text : PathOrWord()); continue; }
            Next();
            parts.Add(t.Kind == T.String ? "\"" + t.Text + "\"" : t.Text);
        }
        if (parts.Count == 0) throw new ParseError(Peek, "missing argument");
        return string.Join(" ", parts);
    }

    /// <summary>A name or path inside call arguments, where any word is allowed (not, on, and...).</summary>
    private string PathOrWord()
    {
        var parts = new List<string> { Next().Text };
        while (Peek.Is(".") && PeekAt(1).Kind == T.Word) { Next(); parts.Add(Next().Text); }
        return Resolve(string.Join(".", parts));
    }

    private static string Args(List<string> args) => args.Count == 0 ? "" : " " + string.Join(" ", args);

    /// <summary>a.b.c (variables, members), with aliases applied to the first part.</summary>
    private string Path()
    {
        var t = Peek;
        if (t.Kind != T.Word) throw new ParseError(t, $"expected a name but found {t}");
        var parts = new List<string> { Next().Text };
        while (Peek.Is(".") && PeekAt(1).Kind == T.Word) { Next(); parts.Add(Next().Text); }
        if (Peek.Is("[")) throw new ParseError(Peek, "Megalo has no arrays: use a named variable");
        return Resolve(string.Join(".", parts));
    }

    private string Resolve(string path)
    {
        int dot = path.IndexOf('.');
        string head = dot < 0 ? path : path[..dot];
        return _aliases.TryGetValue(head, out string? real) ? real + (dot < 0 ? "" : path[dot..]) : path;
    }

    /// <summary>A value: number, "string", a name or path, or a negative number.</summary>
    private string Atom()
    {
        var t = Peek;
        switch (t.Kind)
        {
            case T.Number:
                Next();
                return t.Text;
            case T.String:
                Next();
                return "\"" + t.Text + "\"";
            case T.Op when t.Text == "-" && PeekAt(1).Kind == T.Number:
                Next();
                return "-" + Next().Text;
            case T.Word when !Structural.Contains(t.Text):
                return Path();
            default:
                throw new ParseError(t, $"expected a value but found {t}");
        }
    }
}

// ---- Megalo → Kalo Script ---------------------------------------------------------------------------------

internal static class Decompiler
{
    private static readonly Dictionary<string, string> SetOps = new()
    {
        ["set_to"] = "=", ["add"] = "+=", ["subtract"] = "-=", ["multiply"] = "*=", ["divide"] = "/=", ["modulo"] = "%=",
        ["="] = "=", ["+="] = "+=", ["-="] = "-=", ["*="] = "*=", ["/="] = "/=", ["%="] = "%=",
    };
    private static readonly Dictionary<string, string> CompareOps = new()
    {
        ["equal_to"] = "==", ["not_equal_to"] = "!=", ["greater_than"] = ">", ["less_than"] = "<",
        ["greater_than_or_equal_to"] = ">=", ["less_than_or_equal_to"] = "<=",
        ["=="] = "==", ["!="] = "!=", [">"] = ">", ["<"] = "<", [">="] = ">=", ["<="] = "<=",
    };
    private static readonly HashSet<string> TopKeywords = new(KaloScript.MegaloDeclarations) { "trigger", "variables", "include" };

    private sealed record Line(int No, string Text, string Comment, string Raw)
    {
        public string[] Words { get; } = Split(Text);
    }

    public static string Convert(string megalo)
    {
        var lines = megalo.Replace("\r\n", "\n").Split('\n')
            .Select((l, i) => { var (code, comment) = StripComment(l); return new Line(i + 1, code, comment, l.TrimEnd()); }).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("-- Kalo Script (converted from Megalo text).");
        int i = 0;
        var raw = new List<string>();
        void FlushRaw()
        {
            while (raw.Count > 0 && raw[^1].Trim().Length == 0) raw.RemoveAt(raw.Count - 1);
            if (raw.Count == 0) return;
            sb.AppendLine("megalo [[");
            foreach (var r in raw) sb.AppendLine(r.TrimEnd());
            sb.AppendLine("]]");
            raw.Clear();
        }
        while (i < lines.Count)
        {
            var l = lines[i];
            string first = l.Words.FirstOrDefault() ?? "";
            if (first == "trigger" && l.Words.Length >= 2)
            {
                FlushRaw();
                string kind = l.Words[1];
                string head = kind switch
                {
                    "general" => "do",
                    "player" or "team" or "object" => $"for each {kind} do",
                    "random_player" => "for each random player do",
                    _ => KaloScript.Events.Values.Contains(kind) ? $"on {(kind == "initialization" ? "init" : kind)} do" : $"for each {kind} do",
                };
                sb.AppendLine(head + Trail(l.Comment));
                i++;
                var body = ReadBlock(lines, ref i);
                WriteBody(sb, body, 1);
                sb.AppendLine("end");
                continue;
            }
            if (first == "variables" && l.Words.Length >= 2 && IsUnindented(megalo, l))
            {
                FlushRaw();
                string scope = l.Words[1];
                i++;
                while (i < lines.Count && lines[i].Words.FirstOrDefault() != "end")
                {
                    var v = lines[i];
                    if (v.Words.Length >= 3 && (v.Words[0].StartsWith("networked", StringComparison.Ordinal) || v.Words[0] == "local"))
                        sb.AppendLine($"declare {v.Words[0]} {scope}.{v.Words[1]} {v.Words[2]}" + (v.Words.Length >= 4 ? " = " + v.Words[3] : "") + Trail(v.Comment));
                    else if (v.Text.Trim().Length > 0)
                        sb.AppendLine("-- (kept as Megalo) " + v.Text.Trim());
                    else if (v.Comment.Length > 0) sb.AppendLine("--" + v.Comment);
                    i++;
                }
                i++; // end
                continue;
            }
            if (first == "include" && l.Words.Length >= 2)
            {
                FlushRaw();
                sb.AppendLine($"include {l.Words[1]}" + Trail(l.Comment));
                i++;
                continue;
            }
            if (l.Text.Trim().Length == 0 && raw.Count == 0)
            {
                // Blank lines and comments between top-level items.
                sb.AppendLine(l.Comment.Length > 0 ? "--" + l.Comment : "");
                i++;
                continue;
            }
            // Any other top-level declaration: keep the Megalo text until the next top-level item.
            int from = i;
            i++;
            while (i < lines.Count)
            {
                var n = lines[i];
                string w = n.Words.FirstOrDefault() ?? "";
                if (TopKeywords.Contains(w) && IsUnindented(megalo, n)) break;
                i++;
            }
            // Comment-only lines just before the next item belong to it: give them back.
            int to = i;
            while (to > from + 1 && lines[to - 1].Text.Trim().Length == 0) to--;
            var block = lines.Skip(from).Take(to - from).Select(x => x.Raw).ToList();
            // Written in Kalo Script when it converts back to exactly the same Megalo; otherwise kept as Megalo.
            string? kalo = DeclarationConverter.Convert(block);
            if (kalo != null) sb.Append(kalo);
            else
            {
                raw.AddRange(block);
                FlushRaw();
            }
            for (int k = to; k < i; k++) sb.AppendLine(lines[k].Comment.Length > 0 ? "--" + lines[k].Comment : "");
        }
        FlushRaw();
        return sb.ToString();
    }

    private static bool IsUnindented(string _, Line l) => l.Text.Length > 0 && !char.IsWhiteSpace(l.Text[0]);

    private static string Trail(string comment) => comment.Length > 0 ? "  --" + comment : "";

    /// <summary>Lines of a block up to its matching end (consumed).</summary>
    private static List<Line> ReadBlock(List<Line> lines, ref int i)
    {
        var body = new List<Line>();
        int depth = 0;
        while (i < lines.Count)
        {
            var l = lines[i++];
            string w0 = l.Words.FirstOrDefault() ?? "";
            if (w0 == "end")
            {
                if (depth == 0) return body;
                depth--;
            }
            else if ((w0 == "action" && l.Words.Length >= 2 && l.Words[1] == "for_each") || w0 == "begin") depth++;
            body.Add(l);
        }
        return body;
    }

    /// <summary>Writes a Megalo block: a condition gates everything after it, so it becomes an if around the rest.</summary>
    private static void WriteBody(StringBuilder sb, List<Line> body, int level)
    {
        string pad = new string(' ', level * 2);
        int i = 0;
        while (i < body.Count)
        {
            var l = body[i];
            var w = l.Words;
            if (w.Length == 0)
            {
                sb.AppendLine(l.Comment.Length > 0 ? pad + "--" + l.Comment : "");
                i++;
                continue;
            }
            if (w[0] == "condition" || (w[0] == "or" && w.Length > 1 && w[1] == "condition"))
            {
                // Consecutive conditions (comments between them allowed) form one if; the rest of the block is its body.
                var conds = new List<string>();
                var trailing = new List<string>();
                static bool IsCondition(string[] w) => w.FirstOrDefault() == "condition" || (w.Length > 1 && w[0] == "or" && w[1] == "condition");
                while (i < body.Count && (IsCondition(body[i].Words) || body[i].Words.Length == 0))
                {
                    if (body[i].Words.Length == 0) { if (body[i].Comment.Length > 0) trailing.Add(body[i].Comment); i++; continue; }
                    var cw = body[i].Words;
                    if (cw[0] == "or")
                    {
                        // "or condition x" joins x with the condition before it.
                        if (conds.Count > 0 && !conds[^1].EndsWith(" or", StringComparison.Ordinal)) conds[^1] += " or";
                        cw = cw[1..];
                    }
                    conds.Add(ConditionText(cw));
                    if (body[i].Comment.Length > 0) trailing.Add(body[i].Comment);
                    i++;
                }
                // Megalo ANDs groups of ORed conditions: (a or b) and c.
                var groups = new List<List<string>> { new() };
                for (int k = 0; k < conds.Count; k++)
                {
                    string c = conds[k];
                    bool or = c.EndsWith(" or", StringComparison.Ordinal);
                    if (or) c = c[..^3];
                    groups[^1].Add(c);
                    if (!or && k < conds.Count - 1) groups.Add(new());
                }
                var text = new StringBuilder(string.Join(" and ", groups.Select(g =>
                    g.Count > 1 && groups.Count > 1 ? "(" + string.Join(" or ", g) + ")" : string.Join(" or ", g))));
                foreach (var c in trailing) sb.AppendLine(pad + "--" + c);
                sb.AppendLine($"{pad}if {text} then");
                WriteBody(sb, body.Skip(i).ToList(), level + 1);
                sb.AppendLine(pad + "end");
                return;
            }
            if ((w[0] == "action" && w.Length >= 2 && w[1] == "for_each") || (w[0] == "begin" && w.Length == 1))
            {
                // "begin ... end" is an inline block (no trigger of its own).
                string kind = w[0] == "begin" ? "general" : w.Length >= 3 ? w[2] : "general";
                string head = w[0] == "begin" ? "inline" : kind == "general" ? "do" : kind == "random_player" ? "for each random player do" : $"for each {kind} do";
                sb.AppendLine(pad + head + Trail(l.Comment));
                i++;
                var inner = ReadBlock(body, ref i);
                WriteBody(sb, inner, level + 1);
                sb.AppendLine(pad + "end");
                continue;
            }
            sb.AppendLine(pad + StatementText(w) + Trail(l.Comment));
            i++;
        }
    }

    private static string ConditionText(string[] w)
    {
        // condition [not] (if a op b | name args) [or]
        int k = 1;
        bool or = w[^1] == "or";
        int end = or ? w.Length - 1 : w.Length;
        string prefix = "";
        while (k < end && w[k] == "not") { prefix += "not "; k++; }
        string body;
        if (k < end && w[k] == "if" && end - k == 4 && CompareOps.TryGetValue(w[k + 2], out string? op))
            body = $"{w[k + 1]} {op} {w[k + 3]}";
        else
            body = CallText(w[k], w.Skip(k + 1).Take(end - k - 1));
        return prefix + body + (or ? " or" : "");
    }

    private static string StatementText(string[] w)
    {
        if (w[0] == "temporary" && w.Length >= 3)
            return $"temp {w[1]} {w[2]}" + (w.Length >= 4 ? " = " + w[3] : "");
        if (w[0] == "action" && w.Length >= 2)
        {
            if (w[1] == "set" && w.Length == 5 && SetOps.TryGetValue(w[3], out string? op))
                return $"{w[2]} {op} {w[4]}";
            if (w[1] == "set" && w.Length == 5 && w[3] is "|=" or "&=" or "^=" or "~=" or "<<=" or ">>=")
                return $"{w[2]} {w[3]} {w[4]}";
            return CallText(w[1], w.Skip(2));
        }
        return "-- (not converted) " + string.Join(" ", w);
    }

    private static string CallText(string name, IEnumerable<string> args) => $"{name}({string.Join(", ", args)})";

    private static (string Code, string Comment) StripComment(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inString = !inString;
            else if (line[i] == ';' && !inString) return (line[..i].TrimEnd(), line[(i + 1)..].TrimEnd());
        }
        return (line.TrimEnd(), "");
    }

    /// <summary>Splits on whitespace, keeping "quoted strings" whole.</summary>
    private static string[] Split(string text)
    {
        var list = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            int s = i;
            if (text[i] == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"') i++;
                i = Math.Min(i + 1, text.Length);
            }
            else while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            list.Add(text[s..i]);
        }
        return list.ToArray();
    }
}
