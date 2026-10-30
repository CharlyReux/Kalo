using System.Text;
using System.Text.RegularExpressions;

namespace Kalo.Core.Scripting;

/// <summary>
/// Format document (Ctrl+Shift+I): re-indents by block, trims trailing spaces, keeps at most one blank line in a row,
/// and in Kalo Script puts one space after commas and around = == += &lt; ... Only whitespace changes: the result is
/// checked to have the same tokens, and when the blocks don't add up (a missing end) the indentation is left alone.
/// </summary>
public static class ScriptFormatter
{
    public sealed record Result(string Text, bool Changed, string Message);

    public static Result Format(string text, bool kaloScript)
    {
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var verbatim = new bool[lines.Count];
        if (kaloScript) SpaceKaloTokens(lines, verbatim);
        int[]? depth = kaloScript ? KaloDepths(lines, verbatim) : MegaloDepths(lines);
        string unit = kaloScript ? "  " : "\t";

        var output = new List<string>();
        int blank = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (verbatim[i]) { output.Add(line); blank = 0; continue; }
            string body = line.Trim();
            if (body.Length == 0)
            {
                // At most one blank line in a row, none at the start.
                if (++blank == 1 && output.Count > 0) output.Add("");
                continue;
            }
            blank = 0;
            string indent = depth != null ? string.Concat(Enumerable.Repeat(unit, Math.Max(0, depth[i]))) : line[..(line.Length - line.TrimStart().Length)];
            output.Add(indent + body);
        }
        while (output.Count > 0 && output[^1].Length == 0) output.RemoveAt(output.Count - 1);
        string result = string.Join(newline, output) + newline;

        // Safety: only whitespace may differ, and Kalo Script must read as the same tokens.
        if (Squeeze(result) != Squeeze(text) || (kaloScript && !SameTokens(text, result)))
            return new Result(text, false, "Not formatted: the formatter would have changed more than spaces (please report this).");
        bool changed = result != text;
        string note = depth == null ? " (indentation kept: the blocks don't add up, check for a missing or extra end)" : "";
        return new Result(result, changed, (changed ? "Formatted" : "Already formatted") + note + ".");
    }

    private static string Squeeze(string s) => Regex.Replace(s, @"\s+", "");

    private static bool SameTokens(string a, string b)
    {
        var ea = new List<ScriptError>();
        var eb = new List<ScriptError>();
        var ta = Lexer.Run(a.Replace("\r\n", "\n"), ea);
        var tb = Lexer.Run(b.Replace("\r\n", "\n"), eb);
        return ta.Count == tb.Count && ta.Zip(tb).All(p => p.First.Kind == p.Second.Kind && (p.First.Kind == T.Raw ? Squeeze(p.First.Text) == Squeeze(p.Second.Text) : p.First.Text == p.Second.Text));
    }

    // ---- Kalo Script ----

    private static readonly HashSet<string> Spaced = new(StringComparer.Ordinal)
    {
        "=", "==", "!=", "<", ">", "<=", ">=", "+=", "-=", "*=", "/=", "%=", "|=", "&=", "^=", "~=", "<<=", ">>=",
    };

    /// <summary>One space after commas (none before), one around assignments and comparisons. Marks megalo [[ ]] lines.</summary>
    private static void SpaceKaloTokens(List<string> lines, bool[] verbatim)
    {
        var errors = new List<ScriptError>();
        var toks = Lexer.Run(string.Join("\n", lines), errors);
        foreach (var t in toks.Where(t => t.Kind == T.Raw))
        {
            int count = t.Text.Count(c => c == '\n');
            for (int l = t.Line; l <= t.Line + count && l - 1 < lines.Count; l++) if (l > t.Line) verbatim[l - 1] = true;
        }
        // Edit each line from its last token to its first, so earlier columns stay valid.
        foreach (var group in toks.Where(t => t.Kind is T.Punct or T.Op && t.Line - 1 < lines.Count).GroupBy(t => t.Line))
        {
            int li = group.Key - 1;
            if (verbatim[li] || toks.Any(t => t.Kind == T.Raw && t.Line == group.Key)) continue;
            string s = lines[li];
            foreach (var t in group.OrderByDescending(t => t.Col))
            {
                int at = t.Col - 1, end = at + t.Text.Length;
                if (at < 0 || end > s.Length || s.Substring(at, t.Text.Length) != t.Text) continue;
                if (t.Is(","))
                {
                    int after = end;
                    while (after < s.Length && s[after] == ' ') after++;
                    string rest = after < s.Length ? " " : "";
                    int before = at;
                    while (before > 0 && s[before - 1] == ' ') before--;
                    if (before == 0) before = at; // a line starting with a comma: leave its indentation
                    s = s[..before] + "," + rest + s[after..];
                }
                else if (t.Kind == T.Op && Spaced.Contains(t.Text))
                {
                    int after = end;
                    while (after < s.Length && s[after] == ' ') after++;
                    int before = at;
                    while (before > 0 && s[before - 1] == ' ') before--;
                    if (before == 0 || after >= s.Length) continue; // not between two things on this line
                    s = s[..before] + " " + t.Text + " " + s[after..];
                }
            }
            lines[li] = s;
        }
    }

    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "engine", "teams", "loadout", "loadout_palette", "map_permissions", "player_rating", "string_table", "widgets", "stats", "constants",
    };

    /// <summary>The indentation level of every line, or null when the blocks don't add up.</summary>
    private static int[]? KaloDepths(List<string> lines, bool[] verbatim)
    {
        var errors = new List<ScriptError>();
        var toks = Lexer.Run(string.Join("\n", lines), errors).Where(t => t.Kind != T.End).ToList();
        var byLine = toks.GroupBy(t => t.Line).ToDictionary(g => g.Key, g => g.ToList());
        var depth = new int[lines.Count];
        // Frames: "code" (if / do / inline), "decl" (rows until end), "generic" (rows and nested blocks until end).
        var stack = new Stack<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (verbatim[i]) { depth[i] = stack.Count; continue; }
            if (!byLine.TryGetValue(i + 1, out var l) || l.Count == 0) { depth[i] = stack.Count; continue; }
            var first = l[0];
            bool dedent = first.Is("end") || first.Is("else") || first.Is("elseif");
            depth[i] = stack.Count - (dedent ? 1 : 0);
            if (depth[i] < 0) return null;

            string? frame = stack.Count > 0 ? stack.Peek() : null;
            if (frame is "decl" or "generic")
            {
                if (first.Is("end")) stack.Pop();
                else if (frame == "generic" && !l.Any(t => t.Is("=")) && l.Count >= 1) stack.Push("generic");
                continue;
            }

            // Declarations at the top level: blocks unless written on one line.
            int w = 0;
            while (w < l.Count && (l[w].Is("hidden") || l[w].Is("locked"))) w++;
            if (stack.Count == 0 && w < l.Count && l[w].Kind == T.Word && Transpiler.DeclarationWords.Contains(l[w].Text)
                && !(w + 1 < l.Count && l[w + 1].Is("(")))
            {
                string word = l[w].Text;
                int eq = l.FindIndex(t => t.Is("="));
                bool oneLine = word switch
                {
                    // option NAME "Title" "About" = 1 range a b, or option NAME = V; the list form has value rows and end.
                    "option" => l.Any(t => t.Is("range")) || (eq >= 0 && eq - w < 4),
                    "widget" or "stat" or "base" or "localized_include" or "constant" => true,
                    "objects" => l.Any(t => t.Is("with")),
                    _ => eq >= 0,
                };
                if (!oneLine) stack.Push(GenericWords.Contains(word) ? "generic" : "decl");
                continue;
            }

            for (int k = 0; k < l.Count; k++)
            {
                var t = l[k];
                if (t.Kind != T.Word) continue;
                bool call = k + 1 < l.Count && l[k + 1].Is("(");
                bool member = k > 0 && l[k - 1].Is(".");
                if (call || member) continue;
                if (t.Is("if") || t.Is("do") || (t.Is("inline") && k == l.Count - 1)) stack.Push("code");
                else if (t.Is("end"))
                {
                    if (stack.Count == 0) return null;
                    stack.Pop();
                }
            }
        }
        return stack.Count == 0 ? depth : null;
    }

    // ---- Megalo text ----

    private static readonly HashSet<string> MegaloOpeners = new(StringComparer.Ordinal)
    {
        "trigger", "game_options", "variables", "constants", "hud_widgets", "game_stats", "engine_data", "teams", "team", "map_object",
        "map_permissions", "player_rating", "string_table", "loadout", "loadout_palette", "requisition_palette", "begin",
    };

    private static readonly Regex MegaloToken = new(@"""[^""]*""|[^\s""]+");

    private static int[]? MegaloDepths(List<string> lines)
    {
        var depth = new int[lines.Count];
        int d = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var w = MegaloToken.Matches(CodeOf(lines[i])).Select(m => m.Value).ToList();
            if (w.Count == 0) { depth[i] = d; continue; }
            if (w[0] == "end" && w.Count == 1)
            {
                depth[i] = --d;
                if (d < 0) return null;
                continue;
            }
            depth[i] = d;
            if (w[^1] == "end") continue; // a whole block on one line
            string a = w[0];
            bool opens = MegaloOpeners.Contains(a)
                || (a == "action" && w.Count > 1 && w[1] == "for_each")
                || (a is "option" or "ranged_option" && w.Count == 2)
                || (a is "hide" or "lock" && w.Count == 3)
                || (a == "override" && w.Count == 2)
                || a == "player_traits";
            if (opens) d++;
        }
        return d == 0 ? depth : null;
    }

    /// <summary>A Megalo line without its ; comment (outside strings).</summary>
    private static string CodeOf(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inString = !inString;
            else if (!inString && line[i] == ';') return line[..i];
        }
        return line;
    }
}
