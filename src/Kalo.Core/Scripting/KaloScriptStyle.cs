using System.Text;
using System.Text.RegularExpressions;

namespace Kalo.Core.Scripting;

/// <summary>
/// Kalo Script's two ways of writing blocks. The Python style (the default) uses indentation and a colon:
/// <code>
/// # 2 points per kill
/// player.streak: number = 0
///
/// for each player:
///     if player_died(current_player, enemy):
///         killer: player = none
///         player_death_get_killing_player(current_player, killer)
///         killer.score += 2
///     elif current_player.streak > 5:
///         current_player.streak = 0
///
/// def award(p, points):
///     p.score += points
/// </code>
/// The classic style (then / do / end, -- comments, temp and declare) still works. The Python style is turned into the
/// classic one line for line (the same line numbers), so everything else (compiler, checks) reads one language.
/// </summary>
public static class KaloScriptStyle
{
    private static readonly Regex ClassicMark = new(@"(^|\s)(then|do)\s*(--.*)?$|^\s*end\s*(--.*)?$|^\s*function\s+\w+\s*\(", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>True for a file written in the classic style (then / do / end).</summary>
    public static bool IsClassic(string source)
    {
        foreach (var (code, _) in CodeLines(source))
            if (ClassicMark.IsMatch(code)) return true;
        return false;
    }

    /// <summary>Lines with their code part (no comment), skipping megalo [[ ]] text.</summary>
    private static IEnumerable<(string Code, int Line)> CodeLines(string source)
    {
        bool raw = false;
        int n = 0;
        foreach (string line in source.Replace("\r\n", "\n").Split('\n'))
        {
            n++;
            if (raw) { if (line.Contains("]]")) raw = false; continue; }
            var (code, _) = Split(line, out bool opensRaw);
            if (opensRaw) raw = !line[(line.IndexOf("[[", StringComparison.Ordinal) + 2)..].Contains("]]");
            yield return (code, n);
        }
    }

    /// <summary>A line's code and its comment (# or --), outside strings. opensRaw: the code has [[.</summary>
    private static (string Code, string? Comment) Split(string line, out bool opensRaw)
    {
        opensRaw = false;
        bool str = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') str = !str;
            if (str) continue;
            if (c == '[' && i + 1 < line.Length && line[i + 1] == '[') { opensRaw = true; return (line, null); }
            if (c == '#') return (line[..i], line[(i + 1)..]);
            if (c == '-' && i + 1 < line.Length && line[i + 1] == '-') return (line[..i], line[(i + 2)..]);
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') return (line[..i], line[(i + 2)..]);
        }
        return (line, null);
    }

    private static readonly Regex Declare = new(@"^(?<net>(local|networked\w*)\s+)?(?<scope>global|player|team|object)\.(?<name>\w+)\s*:\s*(?<type>enum\s*\{[^}]*\}|\w+)\s*(=\s*(?<value>.+))?$", RegexOptions.Compiled);
    private static readonly Regex Temp = new(@"^(?<name>\w+)\s*:\s*(?<type>number|object|player|team)\s*(=\s*(?<value>.+))?$", RegexOptions.Compiled);

    private sealed class Block
    {
        public int HeaderIndent;
        public int BodyIndent = -1;
        public string Kind = "";
        public int HeaderLine;
    }

    /// <summary>Python style → classic style, line for line. Errors: indentation problems.</summary>
    public static (string Classic, List<ScriptError> Errors) ToClassic(string source)
    {
        var errors = new List<ScriptError>();
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var code = new string?[lines.Length];      // rewritten code (null: line kept as it is, e.g. raw Megalo)
        var comment = new string?[lines.Length];
        var indentOf = new int[lines.Length];
        var ends = new int[lines.Length];          // "end"s appended to a line
        var stack = new Stack<Block>();
        int lastCode = -1;
        bool raw = false;
        Block? pending = null;                     // a header waiting for its indented lines
        // Where a closing block's end goes: its last indented comment after the last code, else the last code line.
        int Target(Block b, int before)
        {
            for (int k = before - 1; k > lastCode && k >= 0; k--)
                if (code[k] == "" && comment[k] != null && indentOf[k] >= b.BodyIndent && b.BodyIndent > 0) return k;
            return lastCode;
        }

        // Logical lines: a line goes on while a bracket is open or it ends with and / or / a comma.
        int i = 0;
        while (i < lines.Length)
        {
            if (raw)
            {
                if (lines[i].Contains("]]")) raw = false;
                i++;
                continue;
            }
            var (c0, cm0) = Split(lines[i], out bool opensRaw);
            comment[i] = cm0;
            string first = c0.Trim();
            if (first.Length == 0)
            {
                code[i] = "";
                // A comment inside a block (indented) stays inside it: the block's end may go on its line.
                if (cm0 != null) indentOf[i] = Indent(c0);
                i++;
                continue;
            }
            int indent = Indent(c0);
            int start = i, end = i;
            var parts = new List<string> { first };
            if (!opensRaw)
                while (Continues(string.Join(" ", parts)) && end + 1 < lines.Length)
                {
                    end++;
                    var (cn, cmn) = Split(lines[end], out _);
                    comment[end] = cmn;
                    parts.Add(cn.Trim());
                }

            // The block this line belongs to.
            string text = string.Join(" ", parts);
            bool branch = Regex.IsMatch(first, @"^(elif\b|else\s*:)");
            if (pending != null)
            {
                if (indent > pending.HeaderIndent) pending.BodyIndent = indent;
                else
                {
                    errors.Add(new ScriptError(start + 1, indent + 1, "expected an indented block after the line ending with ':'"));
                    pending.BodyIndent = pending.HeaderIndent + 1;
                }
                pending = null;
            }
            while (stack.Count > 0 && indent < stack.Peek().BodyIndent)
            {
                var top = stack.Peek();
                if (branch && top.Kind == "if" && top.HeaderIndent == indent) break;
                stack.Pop();
                int target = Target(top, i);
                if (target >= 0) ends[target]++;
            }
            int expected = stack.Count == 0 ? 0 : branch && stack.Peek().Kind == "if" && stack.Peek().HeaderIndent == indent ? indent : stack.Peek().BodyIndent;
            if (indent != expected)
                errors.Add(new ScriptError(start + 1, indent + 1, indent > expected
                    ? "unexpected indentation: only the lines of a block (after a line ending with ':') are indented more"
                    : "this line's indentation matches no block above it"));
            for (int k = start; k <= end; k++) indentOf[k] = Indent(lines[k]);

            if (opensRaw)
            {
                code[start] = first;
                raw = !lines[start][(lines[start].IndexOf("[[", StringComparison.Ordinal) + 2)..].Contains("]]");
                lastCode = start;
                i = start + 1;
                continue;
            }

            // Declarations and temps with a type annotation (one-line statements).
            Match m;
            if (start == end && stack.Count == 0 && (m = Declare.Match(first)).Success)
                parts[0] = $"declare {m.Groups["net"].Value}{m.Groups["scope"].Value}.{Regex.Replace(m.Groups["type"].Value, @"\s+", "")} {m.Groups["name"].Value}" + (m.Groups["value"].Success ? " = " + m.Groups["value"].Value.Trim() : "");
            else if (start == end && stack.Count > 0 && (m = Temp.Match(first)).Success)
                parts[0] = TempOf(m);
            else if (start == end && first == "pass") parts[0] = "";

            // Block headers: "if x:" (or "if x: statement" on one line).
            int colon = HeaderColon(text);
            if (colon >= 0 && start == end)
            {
                string head = first[..colon].TrimEnd(), rest = first[(colon + 1)..].Trim();
                var (classic, kind) = Header(head);
                if (rest.Length > 0)
                {
                    if (kind is "elif" or "else") errors.Add(new ScriptError(start + 1, indent + 1, $"write the {head.Split(' ')[0]} branch on its own lines"));
                    var tm = Temp.Match(rest);
                    parts[0] = classic + " " + (tm.Success ? TempOf(tm) : rest) + (kind is "elif" or "else" ? "" : " end");
                }
                else
                {
                    parts[0] = classic;
                    if (kind is "elif" or "else")
                    {
                        if (stack.Count == 0 || stack.Peek().Kind != "if" || stack.Peek().HeaderIndent != indent)
                            errors.Add(new ScriptError(start + 1, indent + 1, $"{head.Split(' ')[0]} without an if just above it (same indentation)"));
                        else pending = stack.Peek();
                    }
                    else
                    {
                        var block = new Block { HeaderIndent = indent, Kind = kind, HeaderLine = start };
                        stack.Push(block);
                        pending = block;
                    }
                }
            }
            else if (colon >= 0)
            {
                // A header over several lines: its words on the first line, its colon on the last.
                var (classic, kind) = Header(first + " x");
                string firstClassic = classic.EndsWith(" x then") ? classic[..^7] : classic.EndsWith(" x do") ? classic[..^5] : classic[..^2];
                parts[0] = firstClassic;
                string last = parts[^1].TrimEnd();
                parts[^1] = last[..^1].TrimEnd() + (kind is "if" or "elif" ? " then" : kind == "code" && Regex.IsMatch(first, @"^(for each|on)\b") ? " do" : "");
                if (kind is "elif" or "else") { if (stack.Count > 0 && stack.Peek().Kind == "if") pending = stack.Peek(); }
                else
                {
                    var block = new Block { HeaderIndent = indent, Kind = kind, HeaderLine = start };
                    stack.Push(block);
                    pending = block;
                }
            }
            for (int k = start; k <= end; k++) code[k] = parts[k - start];
            for (int k = end; k >= start; k--) if (code[k]!.Length > 0) { lastCode = k; break; }
            // An empty block's end goes on its pass line (so the block stays visible as a block).
            if (start == end && first == "pass") lastCode = start;
            i = end + 1;
        }
        if (pending != null) errors.Add(new ScriptError(pending.HeaderLine + 1, 1, "expected an indented block after the line ending with ':'"));
        while (stack.Count > 0) { var top = stack.Pop(); int target = Target(top, lines.Length); if (target >= 0) ends[target]++; }

        var sb = new StringBuilder();
        for (int k = 0; k < lines.Length; k++)
        {
            if (k > 0) sb.Append('\n');
            if (code[k] == null) { sb.Append(lines[k]); continue; }
            string body = code[k]!;
            if (ends[k] > 0) body = (body.Length > 0 ? body + " " : "") + string.Join(" ", Enumerable.Repeat("end", ends[k]));
            string pad = new(' ', indentOf[k]);
            string cm = comment[k] != null ? "--" + comment[k] : "";
            sb.Append(body.Length > 0 ? pad + body + (cm.Length > 0 ? "  " + cm : "") : cm.Length > 0 ? pad + cm : "");
        }
        return (sb.ToString(), errors);
    }

    private static string TempOf(Match m) => $"temp {m.Groups["type"].Value} {m.Groups["name"].Value}" + (m.Groups["value"].Success ? " = " + m.Groups["value"].Value.Trim() : "");

    /// <summary>Classic form of a block header, and its kind (if, elif, else, code, decl).</summary>
    private static (string Classic, string Kind) Header(string head)
    {
        if (head.StartsWith("if ", StringComparison.Ordinal)) return (head + " then", "if");
        if (head.StartsWith("elif ", StringComparison.Ordinal)) return ("elseif " + head[5..] + " then", "elif");
        if (head == "else") return ("else", "else");
        if (head.StartsWith("for each ", StringComparison.Ordinal) || head.StartsWith("on ", StringComparison.Ordinal)) return (head + " do", "code");
        if (head == "every tick" || head == "do") return ("do", "code");
        if (head == "inline") return ("inline", "code");
        if (head.StartsWith("def ", StringComparison.Ordinal)) return ("function " + head[4..].TrimStart(), "code");
        return (head, "decl");
    }

    /// <summary>The colon ending a block header ("if x:" or "if x: y"), or -1. Not a type annotation (name: type).</summary>
    private static int HeaderColon(string text)
    {
        int depth = 0;
        bool str = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') str = !str;
            if (str) continue;
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ':' && depth == 0)
            {
                if (i == text.Length - 1) return i;
                // "if x: y", "for each player: y", "else: y": a one-line block after a keyword.
                string first = text.Split(' ', 2)[0];
                if (first is "if" or "elif" or "else" or "for" or "on" or "def" or "inline" or "do" || text.StartsWith("every tick", StringComparison.Ordinal)) return i;
                return -1;
            }
        }
        return -1;
    }

    private static bool InString(string text, int index)
    {
        bool str = false;
        for (int i = 0; i < index; i++) if (text[i] == '"') str = !str;
        return str;
    }

    /// <summary>The line goes on: an open bracket, or and / or / a comma at its end.</summary>
    private static bool Continues(string code)
    {
        string t = code.TrimEnd();
        if (t.EndsWith(',') || t.EndsWith('(') || Regex.IsMatch(t, @"\b(and|or|not)$")) return true;
        int depth = 0;
        bool str = false;
        foreach (char c in t)
        {
            if (c == '"') str = !str;
            if (str) continue;
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
        }
        return depth > 0;
    }

    private static int Indent(string line)
    {
        int n = 0;
        foreach (char c in line)
        {
            if (c == ' ') n++;
            else if (c == '\t') n += 4;
            else break;
        }
        return n;
    }

    // ---- classic → Python style ----

    private static readonly Regex TempLine = new(@"^temp\s+(?<type>number|object|player|team)\s+(?<name>\w+)\s*(=\s*(?<value>.+))?$", RegexOptions.Compiled);
    private static readonly Regex DeclareLine = new(@"^declare\s+(?<net>(local|networked\w*)\s+)?(?<scope>global|player|team|object)\.(?<type>enum\s*\{[^}]*\}|\w+)\s+(?<name>\w+)\s*(=\s*(?<value>.+))?$", RegexOptions.Compiled);

    /// <summary>Classic style → Python style; null when the blocks don't add up (the file stays as it is).</summary>
    public static string? FromClassic(string classic)
    {
        var lines = classic.Replace("\r\n", "\n").Split('\n').ToList();
        var verbatim = new bool[lines.Count];
        bool raw = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (raw) { verbatim[i] = true; if (lines[i].Contains("]]")) raw = false; continue; }
            Split(lines[i], out bool opens);
            if (opens) raw = !lines[i][(lines[i].IndexOf("[[", StringComparison.Ordinal) + 2)..].Contains("]]");
        }
        var depths = ScriptFormatter.Depths(lines, verbatim);
        if (depths == null) return null;
        var output = new List<string>();
        var passes = new HashSet<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (verbatim[i]) { output.Add(lines[i]); continue; }
            var (code, cm) = Split(lines[i], out _);
            string t = code.Trim();
            string pad = new(' ', depths[i] * 4);
            string comment = cm != null ? "#" + cm : "";
            if (t.Length == 0) { output.Add(comment.Length > 0 ? pad + comment : ""); continue; }
            int? next = NextCode(lines, verbatim, i);
            bool nextIsEnd = next is int n1 && Regex.IsMatch(Split(lines[n1], out _).Code.Trim(), @"^end(\s+end)*$");
            // A line opens a block when the next code is deeper, or is the end of an empty block at its own depth.
            bool opens = next is int n2 && (depths[n2] > depths[i] || nextIsEnd && depths[n2] == depths[i]);
            // "statement end end" (from the Python style): ends that close blocks, as many as the depth drops.
            int closing = Math.Max(0, depths[i] - (next is int n3 ? depths[n3] : 0));
            string? py = Line(t, depths[i] == 0, opens, closing);
            if (py == null) return null;
            if (passes.Remove(i)) output.Add(pad + "    pass");
            if (py.Length == 0) { if (comment.Length > 0) output.Add(pad + "    " + comment); continue; }
            output.Add(pad + py + (comment.Length > 0 ? "  " + comment : ""));
            // A block with nothing in it (only comments, or nothing): pass, where its end (or else) was.
            if (py.EndsWith(':') && next is int n4 && Regex.IsMatch(Split(lines[n4], out _).Code.Trim(), @"^(end(\s+end)*|else|elseif\b.*)$"))
                passes.Add(n4);
        }
        while (output.Count > 0 && output[^1].Length == 0 && lines.Count > 0 && lines[^1].Length > 0) output.RemoveAt(output.Count - 1);
        return string.Join("\n", output);
    }

    private static int? NextCode(List<string> lines, bool[] verbatim, int i)
    {
        for (int k = i + 1; k < lines.Count; k++)
            if (!verbatim[k] && Split(lines[k], out _).Code.Trim().Length > 0) return k;
        return null;
    }

    /// <summary>One classic line in Python style ("" for a line that disappears, like end); null if it can't be written.</summary>
    private static string? Line(string t, bool topLevel, bool opensBlock, int closing)
    {
        if (Regex.IsMatch(t, @"^end(\s+end)*$")) return "";
        // "x end end": a statement and the ends of the blocks it finishes (not a one-line block's own end).
        if (!Regex.IsMatch(t, @"\b(then|do)\b"))
            for (int k = 0; k < closing && Regex.IsMatch(t, @"\s+end$"); k++) t = Regex.Replace(t, @"\s+end$", "");
        Match m;
        if ((m = TempLine.Match(t)).Success) return $"{m.Groups["name"].Value}: {m.Groups["type"].Value}" + (m.Groups["value"].Success ? " = " + m.Groups["value"].Value.Trim() : "");
        if ((m = DeclareLine.Match(t)).Success)
            return $"{m.Groups["net"].Value}{m.Groups["scope"].Value}.{m.Groups["name"].Value}: {Regex.Replace(m.Groups["type"].Value, @"\s+", "")}" + (m.Groups["value"].Success ? " = " + m.Groups["value"].Value.Trim() : "");
        // One-line blocks: if x then y end, for each hill do y end.
        m = Regex.Match(t, @"^(?<head>(if|elseif)\s.+?)\s+then\s+(?<body>.+?)\s+end$");
        if (m.Success && !m.Groups["body"].Value.Contains(" then ")) return Head(m.Groups["head"].Value + " then", topLevel) + " " + Temp2(m.Groups["body"].Value);
        m = Regex.Match(t, @"^(?<head>(for each|on)\s.+?)\s+do\s+(?<body>.+?)\s+end$");
        if (m.Success && !m.Groups["body"].Value.Contains(" do ")) return Head(m.Groups["head"].Value + " do", topLevel) + " " + Temp2(m.Groups["body"].Value);
        if (t.EndsWith(" then", StringComparison.Ordinal) || t.EndsWith(" do", StringComparison.Ordinal) || t is "do" or "else" or "inline" || t.StartsWith("function ", StringComparison.Ordinal))
            return Head(t, topLevel);
        if (Regex.IsMatch(t, @"\b(then|do)\b") && Regex.IsMatch(t, @"^(if|elseif|for each|on)\b")) return null;
        return opensBlock ? t + ":" : t;
    }

    private static string Temp2(string statement)
    {
        var m = TempLine.Match(statement);
        return m.Success ? $"{m.Groups["name"].Value}: {m.Groups["type"].Value}" + (m.Groups["value"].Success ? " = " + m.Groups["value"].Value.Trim() : "") : statement;
    }

    private static string Head(string t, bool topLevel)
    {
        if (t.StartsWith("elseif ", StringComparison.Ordinal)) t = "elif " + t[7..];
        if (t.EndsWith(" then", StringComparison.Ordinal)) return t[..^5] + ":";
        if (t == "do") return topLevel ? "every tick:" : "do:";
        if (t.EndsWith(" do", StringComparison.Ordinal)) return t[..^3] + ":";
        if (t is "else" or "inline") return t + ":";
        if (t.StartsWith("function ", StringComparison.Ordinal)) return "def " + t[9..] + ":";
        return t + ":";
    }

    /// <summary>Error messages that name classic words, said in the Python style.</summary>
    public static string PythonMessage(string message) => message
        .Replace("put it in 'do ... end' or 'for each player do ... end'", "put it under 'every tick:' or 'for each player:'")
        .Replace("expected 'then' after the condition", "expected ':' after the condition")
        .Replace("missing 'end' for this", "this block is not closed: check the indentation of")
        .Replace("'end'", "the end of the block")
        .Replace("expected 'do'", "expected ':'");
}
