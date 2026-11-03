using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kalo.Core.Scripting;

/// <summary>
/// Kalo Script for campaign missions: HaloScript (Bungie's Lisp-like mission language) shown and edited in a
/// Python-like syntax, and turned back into HaloScript (the game only reads HaloScript). The two map one to one:
/// <code>
/// (script dormant barn_start                 dormant barn_start:
///     (sleep_until                               sleep_until(volume_test_players(tv_barn), 5)
///         (volume_test_players tv_barn) 5)       if game_difficulty_get() == legendary:
///     (if (= (game_difficulty_get) legendary)        ai_place(barn_elites)
///         (ai_place barn_elites)                 else:
///         (begin                                     ai_place(barn_grunts)
///             (ai_place barn_grunts)                 b_barn_started = True
///             (set b_barn_started TRUE))))
///
/// (global boolean b_barn_started FALSE)      global b_barn_started: boolean = False
/// (script static boolean (ready (short n))   def ready(n: short) -> boolean:
///     (&gt; n 3))                                      return n > 3
/// </code>
/// Comments and blank lines are kept. Anything without a Python form (a cond used as a value, an unusual name) is
/// written as raw HaloScript between backticks: `(...)`.
/// </summary>
public static class MissionScript
{
    /// <summary>HaloScript → Kalo Script (Python style).</summary>
    public static string ToPython(string haloScript)
    {
        var errors = new List<ScriptError>();
        var (forms, comments) = HsReader.Read(haloScript, errors);
        var w = new PyWriter(comments);
        foreach (var f in forms) w.TopLevel(f);
        w.Finish();
        return w.Text;
    }

    /// <summary>Kalo Script (Python style) → HaloScript. LineMap[i] = the Kalo Script line of HaloScript line i + 1.</summary>
    public static MissionCompileResult ToHaloScript(string python)
    {
        var parser = new PyParser(python);
        var (forms, comments) = parser.Run();
        var w = new HsWriter(comments);
        foreach (var f in forms) w.TopLevel(f);
        w.Finish();
        return new MissionCompileResult(w.Text, parser.Errors, w.LineMap);
    }

    /// <summary>The HaloScript forms of a Kalo Script mission file, with positions (for checks and help).</summary>
    internal static (List<HsNode> Forms, List<ScriptError> Errors) Parse(string python)
    {
        var parser = new PyParser(python);
        var (forms, _) = parser.Run();
        return (forms, parser.Errors);
    }

    /// <summary>
    /// A HaloScript file in a canonical form (lower case, one form per line, begin / cond / nested begins flattened),
    /// to check that a conversion kept the meaning.
    /// </summary>
    public static string Canonical(string haloScript)
    {
        var errors = new List<ScriptError>();
        var (forms, _) = HsReader.Read(haloScript, errors);
        var sb = new StringBuilder();
        foreach (var f in forms) sb.AppendLine(Canon(f, statement: false));
        return sb.ToString();
    }

    private static string Canon(HsNode n, bool statement)
    {
        if (n is HsAtom a) return a.IsString ? "\"" + a.Text + "\"" : a.Text.ToLowerInvariant();
        var l = (HsList)n;
        string head = l.Head?.ToLowerInvariant() ?? "";
        // (begin x) is x; begins inside a sequence are spliced into it; cond is a chain of ifs.
        if (head == "begin" && l.Items.Count == 2) return Canon(l.Items[1], statement);
        if (head == "cond")
        {
            HsNode? chain = null;
            for (int k = l.Items.Count - 1; k >= 1; k--)
            {
                if (l.Items[k] is not HsList clause || clause.Items.Count == 0) continue;
                var body = clause.Items.Count == 2 ? clause.Items[1] : new HsList { Items = new List<HsNode> { HsAtom.Name("begin") }.Concat(clause.Items.Skip(1)).ToList() };
                var test = clause.Items[0];
                if (chain == null && test is HsAtom { IsString: false } t && t.Text.Equals("true", StringComparison.OrdinalIgnoreCase)) { chain = body; continue; }
                var ifs = new HsList { Items = new List<HsNode> { HsAtom.Name("if"), test, body } };
                if (chain != null) ifs.Items.Add(chain);
                chain = ifs;
            }
            return chain == null ? "(begin)" : Canon(chain, statement);
        }
        var parts = new List<string>();
        // Begins are spliced into sequences, but not into begin_random (there each child is one random choice).
        bool sequence = head is "begin" or "script";
        // (script static void name) and (script static void (name)) are the same.
        if (head == "script" && l.Items.Count > 3 && l.Items[3] is HsList { Items.Count: 1 } nameOnly) l = l.WithItem(3, nameOnly.Items[0]);
        void Splice(HsNode item)
        {
            if (item is HsList { Head: { } h } inner && h.Equals("begin", StringComparison.OrdinalIgnoreCase))
                foreach (var x in inner.Items.Skip(1)) Splice(x);
            else parts.Add(Canon(item, true));
        }
        for (int k = 0; k < l.Items.Count; k++)
        {
            if (sequence && k > 0) Splice(l.Items[k]);
            else parts.Add(Canon(l.Items[k], false));
        }
        return "(" + string.Join(" ", parts) + ")";
    }
}

public sealed record MissionCompileResult(string HaloScript, List<ScriptError> Errors, List<int> LineMap)
{
    public bool Ok => Errors.Count == 0;
    public int SourceLine(int haloScriptLine) => haloScriptLine >= 1 && haloScriptLine <= LineMap.Count ? LineMap[haloScriptLine - 1] : 0;
}

// ---- HaloScript tree ------------------------------------------------------------------------------------

internal abstract class HsNode
{
    public int Line;
    public int EndLine;
    /// <summary>Last line of a block's header (the condition of an if, the name of a script).</summary>
    public int HeaderEnd;
    /// <summary>Kalo Script side: column (1-based) and length of a name or value, for underlines and hovers.</summary>
    public int Col;
    public int Len;
}

internal sealed class HsAtom : HsNode
{
    public string Text = "";
    public bool IsString;
    public static HsAtom Name(string text, int line = 0) => new() { Text = text, Line = line, EndLine = line, HeaderEnd = line };
}

internal sealed class HsList : HsNode
{
    public List<HsNode> Items = new();
    public string? Head => Items.Count > 0 && Items[0] is HsAtom { IsString: false } a ? a.Text : null;
    public bool HeadIs(string name) => Head != null && Head.Equals(name, StringComparison.OrdinalIgnoreCase);
    public HsList WithItem(int index, HsNode item)
    {
        var copy = new HsList { Line = Line, EndLine = EndLine, HeaderEnd = HeaderEnd, Items = new List<HsNode>(Items) };
        copy.Items[index] = item;
        return copy;
    }
}

/// <summary>A comment: its line, its text (without ; or #), and whether it is alone on its line.</summary>
internal sealed record HsComment(int Line, string Text, bool Own);

internal static class HsReader
{
    public static (List<HsNode> Forms, List<HsComment> Comments) Read(string text, List<ScriptError> errors)
    {
        text = text.Replace("\r\n", "\n");
        var comments = new List<HsComment>();
        var stack = new Stack<HsList>();
        var top = new List<HsNode>();
        int line = 1;
        bool codeOnLine = false;
        int i = 0;
        void Add(HsNode n)
        {
            if (stack.Count > 0) stack.Peek().Items.Add(n);
            else top.Add(n);
        }
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n') { line++; codeOnLine = false; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == ';')
            {
                if (i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*;", i + 2, StringComparison.Ordinal);
                    int stop = end < 0 ? text.Length : end;
                    var body = text.Substring(i + 2, stop - i - 2).Split('\n');
                    for (int k = 0; k < body.Length; k++)
                    {
                        string t = body[k].TrimEnd();
                        if (k == 0 && t.Trim().Length == 0 && body.Length > 1) continue;
                        if (k == body.Length - 1 && t.Trim().Length == 0 && body.Length > 1) continue;
                        comments.Add(new HsComment(line + k, t, k > 0 || !codeOnLine));
                    }
                    for (int k = i; k < Math.Min(stop + 2, text.Length); k++) if (text[k] == '\n') line++;
                    i = Math.Min(stop + 2, text.Length);
                    codeOnLine = true;
                    continue;
                }
                int eol = text.IndexOf('\n', i);
                if (eol < 0) eol = text.Length;
                comments.Add(new HsComment(line, text.Substring(i + 1, eol - i - 1).TrimEnd(), !codeOnLine));
                i = eol;
                continue;
            }
            codeOnLine = true;
            if (c == '(')
            {
                stack.Push(new HsList { Line = line });
                i++;
                continue;
            }
            if (c == ')')
            {
                if (stack.Count == 0) { errors.Add(new ScriptError(line, 0, "this ) closes nothing")); i++; continue; }
                var l = stack.Pop();
                l.EndLine = line;
                l.HeaderEnd = HeaderEndOf(l);
                Add(l);
                i++;
                continue;
            }
            if (c == '"')
            {
                int start = ++i;
                while (i < text.Length && text[i] != '"') { if (text[i] == '\n') line++; i++; }
                Add(new HsAtom { Text = text[start..Math.Min(i, text.Length)], IsString = true, Line = line, EndLine = line, HeaderEnd = line });
                i++;
                continue;
            }
            int s = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('(' or ')' or ';' or '"')) i++;
            Add(HsAtom.Name(text[s..i], line));
        }
        while (stack.Count > 0)
        {
            var l = stack.Pop();
            errors.Add(new ScriptError(l.Line, 0, "this ( is never closed"));
            l.EndLine = line;
            l.HeaderEnd = HeaderEndOf(l);
            Add(l);
        }
        return (top, comments);
    }

    /// <summary>The header of a block: (if cond ...) up to its condition, (script kind name ...) up to its name.</summary>
    private static int HeaderEndOf(HsList l)
    {
        if (l.HeadIs("if") && l.Items.Count > 1) return l.Items[1].EndLine;
        if (l.HeadIs("script") && l.Items.Count > 2)
        {
            bool typed = l.Items[1] is HsAtom k && (k.Text.Equals("static", StringComparison.OrdinalIgnoreCase) || k.Text.Equals("stub", StringComparison.OrdinalIgnoreCase));
            int name = typed ? 3 : 2;
            return l.Items[Math.Min(name, l.Items.Count - 1)].EndLine;
        }
        if ((l.HeadIs("begin_random_count") || l.HeadIs("begin_count")) && l.Items.Count > 1) return l.Items[1].EndLine;
        return l.Line;
    }
}

/// <summary>Hands out comments by source line, so both writers keep them near the code they were next to.</summary>
internal sealed class CommentFeed(List<HsComment> comments)
{
    private readonly List<HsComment> _list = comments.OrderBy(c => c.Line).ToList();
    private int _i;
    public IEnumerable<HsComment> Before(int line)
    {
        while (_i < _list.Count && _list[_i].Line < line) yield return _list[_i++];
    }
    public List<HsComment> Within(int a, int b)
    {
        var list = new List<HsComment>();
        while (_i < _list.Count && _list[_i].Line >= a && _list[_i].Line <= b) list.Add(_list[_i++]);
        return list;
    }
    public IEnumerable<HsComment> Rest() => Before(int.MaxValue);
}

/// <summary>Writes lines with comments and blank lines placed by source line.</summary>
internal abstract class LineWriter
{
    protected readonly StringBuilder Sb = new();
    protected readonly CommentFeed Feed;
    private int _lastLine;
    private bool _wrote;
    private int _lines;
    public readonly List<int> LineMap = new();
    protected abstract string CommentMark { get; }
    protected abstract string Indent(int level);

    protected LineWriter(List<HsComment> comments) => Feed = new CommentFeed(comments);

    public string Text => Sb.ToString();

    private void Raw(int level, string text, int source)
    {
        Sb.Append(text.Length == 0 ? "" : Indent(level) + text).Append('\n');
        _lines++;
        LineMap.Add(source);
        _wrote = true;
    }

    private void Gap(int line)
    {
        if (_wrote && line > _lastLine + 1 && _lastLine > 0) Raw(0, "", line - 1);
    }

    /// <summary>Comments before a line (as their own lines).</summary>
    public void FlushBefore(int line, int level)
    {
        foreach (var c in Feed.Before(line))
        {
            Gap(c.Line);
            Raw(level, (CommentMark + " " + c.Text.TrimStart()).TrimEnd(), c.Line);
            _lastLine = c.Line;
        }
    }

    /// <summary>One line of code standing for source lines a..b, with the comments of those lines.</summary>
    public void Emit(int level, string text, int a, int b)
    {
        if (a > 0) FlushBefore(a, level);
        if (a > 0) Gap(a);
        var within = a > 0 && b >= a ? Feed.Within(a, b) : new List<HsComment>();
        var trailing = within.Where(c => !c.Own).Select(c => c.Text.Trim()).Where(t => t.Length > 0).ToList();
        Raw(level, trailing.Count > 0 ? text + "  " + CommentMark + " " + string.Join(" " + CommentMark + " ", trailing) : text, a);
        foreach (var c in within.Where(c => c.Own)) Raw(level, (CommentMark + " " + c.Text.TrimStart()).TrimEnd(), c.Line);
        if (a > 0) _lastLine = Math.Max(_lastLine, Math.Max(a, b));
    }

    public void Finish()
    {
        FlushBefore(int.MaxValue, 0);
    }
}

// ---- HaloScript → Python style ---------------------------------------------------------------------------

internal sealed class PyWriter(List<HsComment> comments) : LineWriter(comments)
{
    protected override string CommentMark => "#";
    protected override string Indent(int level) => new(' ', level * 4);

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "elif", "else", "and", "or", "not", "def", "return", "pass", "global", "stub", "startup", "dormant",
        "continuous", "command_script", "true", "false", "none", "in", "is", "while", "for", "lambda", "class", "import",
    };
    private static readonly HashSet<string> ScriptKinds = new(StringComparer.OrdinalIgnoreCase) { "startup", "dormant", "continuous", "command_script" };
    private static readonly HashSet<string> BlockForms = new(StringComparer.OrdinalIgnoreCase) { "begin_random", "begin_random_count", "begin_count" };
    private static readonly Regex SimpleName = new(@"^[A-Za-z0-9_]+(/[A-Za-z0-9_]+)?$", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"^-?(\d+\.?\d*|\.\d+)$", RegexOptions.Compiled);

    public static bool IsName(string s) => SimpleName.IsMatch(s) && !Number.IsMatch(s) && !Keywords.Contains(s);

    public void TopLevel(HsNode n)
    {
        if (n is HsList l && l.HeadIs("global") && l.Items.Count == 4 && l.Items[1] is HsAtom type && l.Items[2] is HsAtom name && IsName(name.Text) && IsName(type.Text))
        {
            Emit(0, $"global {name.Text}: {type.Text} = {Expr(l.Items[3], 0)}", l.Line, l.EndLine);
            return;
        }
        if (n is HsList s && s.HeadIs("script") && s.Items.Count >= 3 && s.Items[1] is HsAtom kind)
        {
            if (ScriptKinds.Contains(kind.Text) && s.Items[2] is HsAtom sn && IsName(sn.Text))
            {
                Emit(0, $"{kind.Text.ToLowerInvariant()} {sn.Text}:", s.Line, s.HeaderEnd);
                Body(s.Items.Skip(3).ToList(), 1, s.EndLine, null);
                return;
            }
            if ((kind.Text.Equals("static", StringComparison.OrdinalIgnoreCase) || kind.Text.Equals("stub", StringComparison.OrdinalIgnoreCase))
                && s.Items.Count >= 4 && s.Items[2] is HsAtom ret && IsName(ret.Text) && Signature(s.Items[3]) is { } sig)
            {
                string prefix = kind.Text.Equals("stub", StringComparison.OrdinalIgnoreCase) ? "stub def " : "def ";
                string returns = ret.Text.Equals("void", StringComparison.OrdinalIgnoreCase) ? "" : " -> " + ret.Text;
                Emit(0, $"{prefix}{sig}{returns}:", s.Line, s.HeaderEnd);
                Body(s.Items.Skip(4).ToList(), 1, s.EndLine, returns.Length > 0 ? ret.Text : null);
                return;
            }
        }
        Emit(0, Raw(n), n.Line, n.EndLine);
    }

    /// <summary>name or (name (type param) ...) → name(param: type, ...); null when it has another shape.</summary>
    private static string? Signature(HsNode n)
    {
        if (n is HsAtom a) return IsName(a.Text) ? a.Text + "()" : null;
        if (n is not HsList l || l.Items.Count == 0 || l.Items[0] is not HsAtom name || !IsName(name.Text)) return null;
        var ps = new List<string>();
        foreach (var p in l.Items.Skip(1))
        {
            if (p is not HsList { Items: [HsAtom t, HsAtom pn] } || !IsName(t.Text) || !IsName(pn.Text)) return null;
            ps.Add($"{pn.Text}: {t.Text}");
        }
        return $"{name.Text}({string.Join(", ", ps)})";
    }

    /// <summary>Statements of a block; an empty block is "pass". returns: the type a typed static script returns.</summary>
    private void Body(List<HsNode> items, int level, int endLine, string? returns, bool keepBegins = false)
    {
        if (items.Count == 0) Emit(level, "pass", 0, 0);
        for (int k = 0; k < items.Count; k++) Statement(items[k], level, returns != null && k == items.Count - 1, keepBegins);
        FlushBefore(endLine + 1, level);
    }

    private void Branch(HsNode n, int level, int endLine, bool isReturn)
    {
        if (n is HsList l && l.HeadIs("begin")) Body(l.Items.Skip(1).ToList(), level, Math.Max(endLine, l.EndLine), isReturn ? "value" : null);
        else
        {
            Statement(n, level, isReturn);
            FlushBefore(endLine + 1, level);
        }
    }

    private void Statement(HsNode n, int level, bool isReturn, bool keepBegins = false)
    {
        if (n is HsList l && l.Items.Count > 0)
        {
            if (!isReturn && l.HeadIs("if") && l.Items.Count is 3 or 4)
            {
                If(l, level, "if");
                return;
            }
            if (!isReturn && l.HeadIs("cond") && l.Items.Count >= 2 && l.Items.Skip(1).All(c => c is HsList { Items.Count: >= 1 }))
            {
                Cond(l, level);
                return;
            }
            if (!isReturn && l.HeadIs("sleep_until") && l.Items.Count is >= 2 and <= 4 && l.Items[1] is HsList { Items.Count: >= 2 } loop && loop.HeadIs("begin"))
            {
                // (sleep_until (begin statements... condition) every timeout): Bungie's loop.
                string header = "repeat" + (l.Items.Count >= 3 ? " every " + Expr(l.Items[2], 0) : "") + (l.Items.Count == 4 ? " timeout " + Expr(l.Items[3], 0) : "") + ":";
                Emit(level, header, l.Line, loop.Line);
                var steps = loop.Items.Skip(1).ToList();
                for (int k = 0; k < steps.Count - 1; k++) Statement(steps[k], level + 1, false);
                var until = steps[^1];
                Emit(level + 1, "until " + Expr(until, 0), until.Line, until.EndLine);
                FlushBefore(l.EndLine + 1, level + 1);
                return;
            }
            if (!isReturn && keepBegins && l.HeadIs("begin"))
            {
                // One choice of a begin_random: a block of its own.
                Emit(level, "begin:", l.Line, l.Line);
                Body(l.Items.Skip(1).ToList(), level + 1, l.EndLine, null);
                return;
            }
            if (!isReturn && l.HeadIs("begin"))
            {
                // A begin inside a block: its statements (in place; same meaning).
                foreach (var x in l.Items.Skip(1)) Statement(x, level, false);
                return;
            }
            if (!isReturn && l.Head is { } h && BlockForms.Contains(h))
            {
                bool counted = !h.Equals("begin_random", StringComparison.OrdinalIgnoreCase);
                if (!counted || l.Items.Count >= 2)
                {
                    string header = counted ? $"{h.ToLowerInvariant()}({Expr(l.Items[1], 0)}):" : $"{h.ToLowerInvariant()}:";
                    Emit(level, header, l.Line, l.HeaderEnd);
                    Body(l.Items.Skip(counted ? 2 : 1).ToList(), level + 1, l.EndLine, null, keepBegins: true);
                    return;
                }
            }
            if (!isReturn && l.HeadIs("set") && l.Items.Count == 3 && l.Items[1] is HsAtom v && IsName(v.Text))
            {
                Emit(level, $"{v.Text} = {Expr(l.Items[2], 0)}", l.Line, l.EndLine);
                return;
            }
        }
        Emit(level, (isReturn ? "return " : "") + Expr(n, 0), n.Line, n.EndLine);
    }

    private void If(HsList l, int level, string word)
    {
        Emit(level, $"{word} {Expr(l.Items[1], 0)}:", l.Line, l.HeaderEnd);
        Branch(l.Items[2], level + 1, l.Items.Count == 4 ? l.Items[3].Line - 1 : l.EndLine, false);
        if (l.Items.Count == 4)
        {
            var other = l.Items[3];
            if (other is HsList e && e.HeadIs("if") && e.Items.Count is 3 or 4) { If(e, level, "elif"); return; }
            Emit(level, "else:", other.Line, other.Line - 1);
            Branch(other, level + 1, l.EndLine, false);
        }
    }

    /// <summary>(cond (a x) (b y) (TRUE z)) as a statement: if a: / elif b: / else: (the first true test runs).</summary>
    private void Cond(HsList l, int level)
    {
        var clauses = l.Items.Skip(1).Cast<HsList>().ToList();
        for (int k = 0; k < clauses.Count; k++)
        {
            var c = clauses[k];
            bool last = k == clauses.Count - 1;
            bool otherwise = last && k > 0 && c.Items[0] is HsAtom { IsString: false } t && t.Text.Equals("true", StringComparison.OrdinalIgnoreCase);
            int next = last ? l.EndLine : clauses[k + 1].Line - 1;
            if (otherwise) Emit(level, "else:", c.Line, c.Items[0].EndLine);
            else Emit(level, $"{(k == 0 ? "if" : "elif")} {Expr(c.Items[0], 0)}:", c.Line, c.Items[0].EndLine);
            Body(c.Items.Skip(1).ToList(), level + 1, next, null);
        }
    }

    // Precedence: ternary 0, or 1, and 2, not 3, comparison 4, + - 5, * / % 6, atom 7.
    private static readonly Dictionary<string, string> Compare = new(StringComparer.OrdinalIgnoreCase)
    {
        ["="] = "==", ["!="] = "!=", ["<"] = "<", [">"] = ">", ["<="] = "<=", [">="] = ">=",
    };

    public static string Expr(HsNode n, int min)
    {
        var (text, prec) = ExprP(n);
        return prec < min ? "(" + text + ")" : text;
    }

    private static (string, int) ExprP(HsNode n)
    {
        if (n is HsAtom a)
        {
            if (a.IsString) return ("\"" + a.Text + "\"", 7);
            string t = a.Text;
            if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) return ("True", 7);
            if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) return ("False", 7);
            if (t.Equals("none", StringComparison.OrdinalIgnoreCase)) return ("None", 7);
            if (Number.IsMatch(t)) return (t, t.StartsWith('-') ? 6 : 7);
            if (IsName(t)) return (t, 7);
            return ("`" + t + "`", 7);
        }
        var l = (HsList)n;
        string? h = l.Head;
        int count = l.Items.Count - 1;
        if (h == null) return (Raw(n), 7);
        string low = h.ToLowerInvariant();
        if (low is "and" or "or" && count >= 2)
        {
            int p = low == "or" ? 1 : 2;
            return (string.Join($" {low} ", l.Items.Skip(1).Select(x => Expr(x, p + 1))), p);
        }
        if (low == "not" && count == 1) return ("not " + Expr(l.Items[1], 3), 3);
        if (Compare.TryGetValue(low, out string? op) && count == 2) return (Expr(l.Items[1], 5) + " " + op + " " + Expr(l.Items[2], 5), 4);
        if (low is "+" or "*" && count >= 2)
        {
            int p = low == "+" ? 5 : 6;
            return (string.Join($" {low} ", l.Items.Skip(1).Select(x => Expr(x, p + 1))), p);
        }
        if (low is "-" or "/" or "%" && count == 2)
        {
            int p = low == "-" ? 5 : 6;
            return (Expr(l.Items[1], p + 1) + " " + low + " " + Expr(l.Items[2], p + 1), p);
        }
        if (low == "if" && count == 3) return (Expr(l.Items[2], 1) + " if " + Expr(l.Items[1], 1) + " else " + Expr(l.Items[3], 0), 0);
        if (IsName(h) && !Keywords.Contains(h) && low is not ("begin" or "cond" or "set" or "script" or "global") && !BlockForms.Contains(h))
            return (h + "(" + string.Join(", ", l.Items.Skip(1).Select(x => Expr(x, 0))) + ")", 7);
        return (Raw(n), 7);
    }

    /// <summary>A form as raw HaloScript between backticks, on one line.</summary>
    public static string Raw(HsNode n) => "`" + HsWriter.Inline(n) + "`";
}

// ---- Python style → HaloScript ---------------------------------------------------------------------------

internal sealed class HsWriter(List<HsComment> comments) : LineWriter(comments)
{
    protected override string CommentMark => ";";
    protected override string Indent(int level) => new('\t', level);

    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase) { "if", "begin", "begin_random", "begin_random_count", "begin_count", "cond" };

    public void TopLevel(HsNode n)
    {
        if (n is HsList l && l.HeadIs("script"))
        {
            int bodyAt = l.Items.Count > 1 && l.Items[1] is HsAtom k && (k.Text is "static" or "stub") ? 4 : 3;
            Emit(0, "(" + string.Join(" ", l.Items.Take(bodyAt).Select(Inline)), l.Line, l.HeaderEnd);
            foreach (var x in l.Items.Skip(bodyAt)) Statement(x, 1);
            FlushBefore(l.EndLine + 1, 1);
            Emit(0, ")", 0, 0);
            return;
        }
        Emit(0, Inline(n), n.Line, n.EndLine);
    }

    private void Statement(HsNode n, int level)
    {
        if (n is HsList l && l.Head is { } h && Blocks.Contains(h) && IsMultiLine(l))
        {
            int inlineArgs = h.Equals("if", StringComparison.OrdinalIgnoreCase) || h.Equals("begin_random_count", StringComparison.OrdinalIgnoreCase)
                || h.Equals("begin_count", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            Emit(level, "(" + string.Join(" ", l.Items.Take(inlineArgs).Select(Inline)), l.Line, l.HeaderEnd);
            foreach (var x in l.Items.Skip(inlineArgs)) Statement(x, level + 1);
            FlushBefore(l.EndLine + 1, level + 1);
            Emit(level, ")", 0, 0);
            return;
        }
        Emit(level, Inline(n), n.Line, n.EndLine);
    }

    private static bool IsMultiLine(HsList l) => l.HeadIs("begin") || l.HeadIs("if") && l.Items.Skip(2).Any(x => x is HsList) || l.Items.Count > 2;

    public static string Inline(HsNode n)
    {
        if (n is HsAtom a) return a.IsString ? "\"" + a.Text + "\"" : a.Text;
        return "(" + string.Join(" ", ((HsList)n).Items.Select(Inline)) + ")";
    }
}

/// <summary>Reads Kalo Script (Python style) for missions into HaloScript forms.</summary>
internal sealed class PyParser
{
    private enum K { Name, Number, String, Raw, Op, NewLine, Indent, Dedent, End }
    private sealed record Tk(K Kind, string Text, int Line, int Col)
    {
        public bool Is(string s) => Kind is K.Name or K.Op && Text == s;
        public override string ToString() => Kind switch { K.NewLine => "end of line", K.End => "end of file", K.Indent => "indent", K.Dedent => "dedent", _ => Text };
    }

    private sealed class Fail(Tk at, string message) : Exception(message)
    {
        public Tk At { get; } = at;
    }

    private readonly string _src;
    private readonly List<Tk> _t = new();
    private readonly List<HsComment> _comments = new();
    private int _p;
    public List<ScriptError> Errors { get; } = new();

    public PyParser(string source) => _src = string.Join("\n", source.Replace("\r\n", "\n").Split('\n').Select(ExpandIndent));

    /// <summary>Tabs at the start of a line count as 4 spaces.</summary>
    private static string ExpandIndent(string line)
    {
        int k = 0;
        while (k < line.Length && line[k] is ' ' or '\t') k++;
        return line[..k].Replace("\t", "    ") + line[k..];
    }

    private static readonly string[] Ops = { "->", "==", "!=", "<=", ">=", "+=", "-=", "*=", "/=", "<", ">", "=", "+", "-", "*", "/", "%", "(", ")", ",", ":", "[", "]" };

    private void Lex()
    {
        var lines = _src.Split('\n');
        var indents = new Stack<int>();
        indents.Push(0);
        int depth = 0; // open brackets: lines inside them continue the statement
        for (int li = 0; li < lines.Length; li++)
        {
            string text = lines[li];
            int line = li + 1;
            int i = 0;
            bool startOfStatement = depth == 0;
            // Indentation (only at the start of a statement, and only for lines with code).
            if (startOfStatement)
            {
                int col = 0;
                while (col < text.Length && text[col] == ' ') col++;
                string rest = text[col..];
                if (rest.Length == 0 || rest.StartsWith('#'))
                {
                    if (rest.StartsWith('#')) _comments.Add(new HsComment(line, rest[1..].TrimEnd(), true));
                    continue;
                }
                if (col > indents.Peek()) { indents.Push(col); _t.Add(new Tk(K.Indent, "", line, col + 1)); }
                else
                {
                    while (col < indents.Peek()) { indents.Pop(); _t.Add(new Tk(K.Dedent, "", line, col + 1)); }
                    if (col != indents.Peek()) Errors.Add(new ScriptError(line, col + 1, "this line's indentation matches no block above it"));
                }
                i = col;
            }
            bool code = false;
            while (i < text.Length)
            {
                char c = text[i];
                if (c is ' ' or '\t') { i++; continue; }
                if (c == '#')
                {
                    _comments.Add(new HsComment(line, text[(i + 1)..].TrimEnd(), !code));
                    break;
                }
                code = true;
                int col = i + 1;
                if (c == '"')
                {
                    int end = text.IndexOf('"', i + 1);
                    if (end < 0) { Errors.Add(new ScriptError(line, col, "this text has no closing \"")); end = text.Length; }
                    _t.Add(new Tk(K.String, text[(i + 1)..Math.Min(end, text.Length)], line, col));
                    i = Math.Min(end + 1, text.Length);
                    continue;
                }
                if (c == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end < 0) { Errors.Add(new ScriptError(line, col, "this raw HaloScript has no closing `")); end = text.Length; }
                    _t.Add(new Tk(K.Raw, text[(i + 1)..Math.Min(end, text.Length)], line, col));
                    i = Math.Min(end + 1, text.Length);
                    continue;
                }
                if (char.IsDigit(c) || c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
                {
                    int s = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.')) i++;
                    string w = text[s..i];
                    // Names may start with a digit (3kiva_bsp); a name/point reference may hold one '/'.
                    bool isNumber = Regex.IsMatch(w, @"^(\d+\.?\d*|\.\d+)$");
                    if (!isNumber) w = ReadNameTail(text, ref i, w);
                    _t.Add(new Tk(isNumber ? K.Number : K.Name, w, line, col));
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int s = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    string w = ReadNameTail(text, ref i, text[s..i]);
                    _t.Add(new Tk(K.Name, w, line, col));
                    continue;
                }
                string? op = Ops.FirstOrDefault(o => string.CompareOrdinal(text, i, o, 0, o.Length) == 0);
                if (op == null) { Errors.Add(new ScriptError(line, col, $"unexpected character '{c}'")); i++; continue; }
                if (op is "(" or "[") depth++;
                if (op is ")" or "]") depth = Math.Max(0, depth - 1);
                _t.Add(new Tk(K.Op, op, line, col));
                i += op.Length;
            }
            if (code && depth == 0) _t.Add(new Tk(K.NewLine, "", line, text.Length + 1));
        }
        int last = lines.Length;
        if (_t.Count > 0 && _t[^1].Kind != K.NewLine && _t[^1].Kind != K.Dedent) _t.Add(new Tk(K.NewLine, "", last, 1));
        while (indents.Count > 1) { indents.Pop(); _t.Add(new Tk(K.Dedent, "", last, 1)); }
        _t.Add(new Tk(K.End, "", last, 1));
    }

    /// <summary>pts_barn/court01: a point of a point set (no spaces around the /).</summary>
    private static string ReadNameTail(string text, ref int i, string word)
    {
        if (i + 1 < text.Length && text[i] == '/' && (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] == '_'))
        {
            int s = ++i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            return word + "/" + text[s..i];
        }
        return word;
    }

    private Tk Peek => _t[Math.Min(_p, _t.Count - 1)];
    private Tk PeekAt(int k) => _t[Math.Min(_p + k, _t.Count - 1)];
    private Tk Next() => _t[Math.Min(_p++, _t.Count - 1)];
    private Tk Expect(string s, string? why = null)
    {
        var t = Next();
        if (!t.Is(s)) throw new Fail(t, why ?? $"expected '{s}' but found {t}");
        return t;
    }
    private Tk ExpectKind(K kind, string why)
    {
        var t = Next();
        if (t.Kind != kind) throw new Fail(t, why);
        return t;
    }

    private static readonly HashSet<string> ScriptKinds = new(StringComparer.Ordinal) { "startup", "dormant", "continuous", "command_script" };
    private static readonly HashSet<string> BlockForms = new(StringComparer.Ordinal) { "begin", "begin_random", "begin_random_count", "begin_count" };

    public (List<HsNode> Forms, List<HsComment> Comments) Run()
    {
        Lex();
        var forms = new List<HsNode>();
        while (Peek.Kind != K.End)
        {
            int before = _p;
            try
            {
                if (Peek.Kind is K.NewLine or K.Dedent) { Next(); continue; }
                if (Peek.Kind == K.Indent) throw new Fail(Peek, "unexpected indentation (top-level lines start at the left)");
                forms.Add(TopLevel());
            }
            catch (Fail e)
            {
                Errors.Add(new ScriptError(e.At.Line, e.At.Col, e.Message));
                SkipStatement(before);
            }
        }
        foreach (var f in forms) CheckUntil(f);
        return (forms, _comments);
    }

    /// <summary>until only ends a repeat block.</summary>
    private void CheckUntil(HsNode n)
    {
        if (n is not HsList l) return;
        if (l.HeadIs("__until")) { Errors.Add(new ScriptError(l.Line, 1, "until only goes on the last line of a repeat block")); l.Items[0] = A("begin", l.Line); }
        foreach (var x in l.Items) CheckUntil(x);
    }

    /// <summary>After an error: skip to the next line at the same level (its block too).</summary>
    private void SkipStatement(int before)
    {
        if (_p <= before) _p = before + 1;
        int depth = 0;
        while (Peek.Kind != K.End)
        {
            var t = Next();
            if (t.Kind == K.Indent) depth++;
            else if (t.Kind == K.Dedent) { if (--depth <= 0 && depth < 0) return; if (depth == 0 && Peek.Kind != K.Indent) return; }
            else if (t.Kind == K.NewLine && depth == 0 && Peek.Kind != K.Indent) return;
        }
    }

    private HsNode TopLevel()
    {
        var t = Peek;
        if (t.Kind == K.Raw)
        {
            var raw = RawForms(Next());
            ExpectKind(K.NewLine, "expected the end of the line after the raw HaloScript");
            return raw;
        }
        if (t.Is("global"))
        {
            Next();
            var name = Name("a global name");
            Expect(":", "a global needs a type: global name: type = value");
            var type = Name("a type (boolean, short, real, object, ai...)");
            Expect("=", "a global needs a starting value: global name: type = value");
            var value = Expr();
            var end = ExpectKind(K.NewLine, $"unexpected {Peek} after the global's value");
            return L(t.Line, end.Line, A("global", t.Line), A(type.Text, type.Line), At(name), value);
        }
        if (t.Kind == K.Name && ScriptKinds.Contains(t.Text))
        {
            Next();
            var name = Name("a script name");
            Expect(":", $"expected ':' after {t.Text} {name.Text}");
            var script = L(t.Line, t.Line, A("script", t.Line), A(t.Text, t.Line), At(name));
            script.HeaderEnd = name.Line;
            script.EndLine = Block(script.Items, null);
            return script;
        }
        if (t.Is("def") || t.Is("stub") && PeekAt(1).Is("def"))
        {
            bool stub = Next().Is("stub");
            if (stub) Next();
            var name = Name("a script name");
            Expect("(", $"expected ( after def {name.Text}");
            var sig = new List<HsNode> { At(name) };
            while (!Peek.Is(")"))
            {
                var p = Name("a parameter name");
                Expect(":", $"parameter {p.Text} needs a type: {p.Text}: short");
                var pt = Name("a type");
                sig.Add(L(p.Line, p.Line, A(pt.Text, pt.Line), At(p)));
                if (Peek.Is(",")) { Next(); continue; }
                if (!Peek.Is(")")) throw new Fail(Peek, "expected , or ) in the parameters");
            }
            Next();
            string ret = "void";
            if (Peek.Is("->")) { Next(); ret = Name("a return type").Text; }
            var colon = Expect(":", "expected ':' at the end of the def line");
            HsNode nameNode = sig.Count == 1 ? sig[0] : L(name.Line, name.Line, sig.ToArray());
            var script = L(t.Line, t.Line, A("script", t.Line), A(stub ? "stub" : "static", t.Line), A(ret, t.Line), nameNode);
            script.HeaderEnd = colon.Line;
            script.EndLine = Block(script.Items, ret == "void" ? null : ret);
            return script;
        }
        if (t.Kind == K.Name)
            throw new Fail(t, $"'{t.Text}' must be inside a script: start one with startup / dormant / continuous / command_script name: or def name():");
        throw new Fail(t, $"unexpected {t} at the top level");
    }

    private Tk Name(string what)
    {
        var t = Next();
        if (t.Kind != K.Name) throw new Fail(t, $"expected {what} but found {t}");
        return t;
    }

    /// <summary>An indented block of statements, appended to into; returns its last line. returns: the value type of a def.</summary>
    private int Block(List<HsNode> into, string? returns)
    {
        // "if x: statement" on one line.
        if (Peek.Kind != K.NewLine)
        {
            var single = SimpleStatement(out bool isReturn);
            if (isReturn && returns == null) throw new Fail(Peek, "return only goes in a def with a return type (def name() -> boolean:)");
            if (single != null) into.Add(single);
            return ExpectKind(K.NewLine, $"unexpected {Peek}").Line;
        }
        Next();
        if (Peek.Kind != K.Indent) throw new Fail(Peek, "expected an indented block after ':'");
        Next();
        int last = Peek.Line;
        bool returned = false;
        while (Peek.Kind is not (K.Dedent or K.End))
        {
            int before = _p;
            try
            {
                if (Peek.Kind == K.NewLine) { Next(); continue; }
                if (returned) throw new Fail(Peek, "nothing can come after return: it gives the script's value, as its last line");
                var stmt = Statement(returns, out bool isReturn, out int end);
                returned = isReturn;
                if (stmt != null) into.Add(stmt);
                last = Math.Max(last, end);
            }
            catch (Fail e)
            {
                Errors.Add(new ScriptError(e.At.Line, e.At.Col, e.Message));
                SkipStatement(before);
                last = Math.Max(last, _t[Math.Max(0, _p - 1)].Line);
            }
        }
        if (Peek.Kind == K.Dedent) Next();
        return last;
    }

    private HsNode? Statement(string? returns, out bool isReturn, out int end)
    {
        isReturn = false;
        var t = Peek;
        if (t.Is("if")) { var n = If(); end = n.EndLine; return n; }
        if (t.Kind == K.Name && BlockForms.Contains(t.Text) && (PeekAt(1).Is(":") || PeekAt(1).Is("(") && t.Text is "begin_random_count" or "begin_count"))
        {
            Next();
            var form = L(t.Line, t.Line, A(t.Text, t.Line));
            if (Peek.Is("("))
            {
                Next();
                form.Items.Add(Expr());
                Expect(")");
            }
            form.HeaderEnd = Expect(":").Line;
            form.EndLine = Block(form.Items, null);
            end = form.EndLine;
            return form;
        }
        if (t.Is("repeat") && (PeekAt(1).Is(":") || PeekAt(1).Is("every")))
        {
            Next();
            var form = L(t.Line, t.Line, A("sleep_until", t.Line));
            HsNode? every = null, timeout = null;
            if (Peek.Is("every"))
            {
                Next();
                every = Expr();
                if (Peek.Is("timeout")) { Next(); timeout = Expr(); }
            }
            var colon = Expect(":", "expected ':' at the end of the repeat line");
            var steps = new List<HsNode>();
            int last = Block(steps, null);
            if (steps.Count == 0 || steps[^1] is not HsList { Head: "__until" } u)
                throw new Fail(t, "repeat needs an 'until condition' line at the end of its block: the loop stops when it is true");
            steps[^1] = u.Items[1];
            var loop = L(colon.Line, last, A("begin", colon.Line));
            loop.Items.AddRange(steps);
            form.Items.Add(loop);
            if (every != null) form.Items.Add(every);
            if (timeout != null) form.Items.Add(timeout);
            form.HeaderEnd = colon.Line;
            form.EndLine = last;
            end = last;
            return form;
        }
        if (t.Is("until"))
        {
            Next();
            var cond = Expr();
            end = ExpectKind(K.NewLine, $"unexpected {Peek} after the until condition").Line;
            return L(t.Line, end, A("__until", t.Line), cond);
        }
        if (t.Is("elif") || t.Is("else")) throw new Fail(t, $"{t.Text} without an if just above it (same indentation)");
        var stmt = SimpleStatement(out isReturn);
        if (isReturn && returns == null) throw new Fail(t, "return only goes in a def with a return type (def name() -> boolean:)");
        end = ExpectKind(K.NewLine, $"unexpected {Peek}: one statement per line").Line;
        if (stmt != null) stmt.EndLine = Math.Max(stmt.EndLine, end);
        return stmt;
    }

    private HsNode? SimpleStatement(out bool isReturn)
    {
        isReturn = false;
        var t = Peek;
        if (t.Is("pass")) { Next(); return null; }
        if (t.Is("return")) { Next(); isReturn = true; return Expr(); }
        if (t.Kind == K.Name && PeekAt(1).Kind == K.Op && PeekAt(1).Text is "=" or "+=" or "-=" or "*=" or "/=")
        {
            var name = Next();
            var op = Next();
            var value = Expr();
            if (op.Text != "=") value = L(t.Line, value.EndLine, A(op.Text[..1], op.Line), A(name.Text, name.Line), value);
            return L(t.Line, value.EndLine, A("set", t.Line), At(name), value);
        }
        if (t.Is("global") || t.Is("def") || t.Kind == K.Name && ScriptKinds.Contains(t.Text) && PeekAt(2).Is(":"))
            throw new Fail(t, $"'{t.Text}' goes at the top level, not inside a script");
        return Expr();
    }

    private HsList If()
    {
        var t = Next(); // if / elif
        var cond = Expr();
        var colon = Expect(":", "expected ':' at the end of the if line");
        var then = new List<HsNode>();
        int end = Block(then, null);
        var node = L(t.Line, end, A("if", t.Line), cond, Seq(then, colon.Line, end));
        node.HeaderEnd = colon.Line;
        if (Peek.Is("elif"))
        {
            var e = If();
            node.Items.Add(e);
            node.EndLine = e.EndLine;
        }
        else if (Peek.Is("else"))
        {
            var e = Next();
            Expect(":", "expected ':' after else");
            var other = new List<HsNode>();
            int oend = Block(other, null);
            node.Items.Add(Seq(other, e.Line, oend));
            node.EndLine = oend;
        }
        return node;
    }

    /// <summary>Statements as one form: itself when one, (begin ...) otherwise.</summary>
    private static HsNode Seq(List<HsNode> items, int line, int end)
    {
        if (items.Count == 1) return items[0];
        var b = L(line, end, A("begin", line));
        b.Items.AddRange(items);
        return b;
    }

    // ---- expressions ----

    private HsNode Expr()
    {
        var a = Or();
        if (Peek.Is("if"))
        {
            Next();
            var c = Or();
            Expect("else", "a value-if needs else: a if condition else b");
            var b = Expr();
            return L(a.Line, b.EndLine, A("if", a.Line), c, a, b);
        }
        return a;
    }

    private HsNode Chain(Func<HsNode> next, string word)
    {
        var first = next();
        if (!Peek.Is(word)) return first;
        var l = L(first.Line, first.EndLine, A(word, first.Line), first);
        while (Peek.Is(word)) { Next(); var x = next(); l.Items.Add(x); l.EndLine = x.EndLine; }
        return l;
    }

    private HsNode Or() => Chain(And, "or");
    private HsNode And() => Chain(Not, "and");

    private HsNode Not()
    {
        if (Peek.Is("not"))
        {
            var t = Next();
            var x = Not();
            return L(t.Line, x.EndLine, A("not", t.Line), x);
        }
        return Comparison();
    }

    private static readonly Dictionary<string, string> CompareOps = new() { ["=="] = "=", ["!="] = "!=", ["<"] = "<", [">"] = ">", ["<="] = "<=", [">="] = ">=" };

    private HsNode Comparison()
    {
        var a = Sum();
        if (Peek.Kind == K.Op && CompareOps.TryGetValue(Peek.Text, out string? op))
        {
            Next();
            var b = Sum();
            return L(a.Line, b.EndLine, A(op, a.Line), a, b);
        }
        if (Peek.Is("=")) throw new Fail(Peek, "use == to compare (= changes a value, on its own line)");
        return a;
    }

    private HsNode Sum() => Arith(Term, "+", "-");
    private HsNode Term() => Arith(Unary, "*", "/", "%");

    /// <summary>+ and * chains are one form (+ a b c); - / % take two.</summary>
    private HsNode Arith(Func<HsNode> next, string many, params string[] two)
    {
        var a = next();
        while (Peek.Kind == K.Op && (Peek.Text == many || two.Contains(Peek.Text)))
        {
            string op = Next().Text;
            if (op == many)
            {
                var l = L(a.Line, a.EndLine, A(op, a.Line), a);
                l.Items.Add(next());
                while (Peek.Kind == K.Op && Peek.Text == many) { Next(); l.Items.Add(next()); }
                l.EndLine = l.Items[^1].EndLine;
                a = l;
            }
            else
            {
                var b = next();
                a = L(a.Line, b.EndLine, A(op, a.Line), a, b);
            }
        }
        return a;
    }

    private HsNode Unary()
    {
        if (Peek.Is("-") && PeekAt(1).Kind == K.Number)
        {
            var m = Next();
            var n = Next();
            return A("-" + n.Text, m.Line);
        }
        return Atom();
    }

    private HsNode Atom()
    {
        var t = Next();
        switch (t.Kind)
        {
            case K.Number: return At(t);
            case K.String: return new HsAtom { Text = t.Text, IsString = true, Line = t.Line, EndLine = t.Line, HeaderEnd = t.Line, Col = t.Col, Len = t.Text.Length + 2 };
            case K.Raw: return RawForms(t);
            case K.Name:
                if (t.Text == "True") return A("true", t.Line);
                if (t.Text == "False") return A("false", t.Line);
                if (t.Text == "None") return A("none", t.Line);
                if (t.Text is "and" or "or" or "else" or "elif" or "return" or "pass" or "def" or "global")
                    throw new Fail(t, $"unexpected '{t.Text}'");
                if (Peek.Is("("))
                {
                    Next();
                    var call = L(t.Line, t.Line, At(t));
                    call.Col = t.Col;
                    call.Len = t.Text.Length;
                    while (!Peek.Is(")"))
                    {
                        if (Peek.Kind is K.End or K.NewLine) throw new Fail(t, $"the ( after {t.Text} is never closed");
                        call.Items.Add(Expr());
                        if (Peek.Is(",")) { Next(); continue; }
                        if (!Peek.Is(")")) throw new Fail(Peek, $"expected , or ) in the call to {t.Text}, found {Peek}");
                    }
                    call.EndLine = Next().Line;
                    return call;
                }
                return At(t);
            case K.Op when t.Text == "(":
                var inner = Expr();
                Expect(")", "this ( is never closed");
                return inner;
        }
        throw new Fail(t, $"expected a value but found {t}");
    }

    private HsNode RawForms(Tk t)
    {
        var errors = new List<ScriptError>();
        var (forms, _) = HsReader.Read(t.Text, errors);
        if (errors.Count > 0 || forms.Count != 1) throw new Fail(t, "the raw HaloScript between backticks must be one complete form or name");
        void Shift(HsNode n)
        {
            n.Line = t.Line; n.EndLine = t.Line; n.HeaderEnd = t.Line;
            if (n is HsList l) foreach (var x in l.Items) Shift(x);
        }
        Shift(forms[0]);
        return forms[0];
    }

    private static HsAtom A(string text, int line) => HsAtom.Name(text, line);
    private static HsAtom At(Tk t)
    {
        var a = HsAtom.Name(t.Text, t.Line);
        a.Col = t.Col;
        a.Len = t.Text.Length;
        return a;
    }
    private static HsList L(int line, int end, params HsNode[] items) => new() { Line = line, EndLine = end, HeaderEnd = line, Items = items.ToList() };
}
