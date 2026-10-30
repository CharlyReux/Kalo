using System.Text;

namespace Kalo.Core.Scripting;

/// <summary>
/// Kalo Script declarations: everything Megalo declares outside triggers, written in Kalo Script.
/// <code>
/// option kill_points "Kill Points" "Points per kill" = 1 range -10 10
/// option hill_time "Hill Movement Time" "How often the hill moves" = 30
///   value 0 "Never"
///   value 30
/// end
/// traits hill_traits "In Hill Traits" "While in the hill"
///   damage_resistance = 150
/// end
/// setting score_to_win_round = 50
/// setting base_player_traits
///   initial_grenades = none
/// end
/// constant k_bomb_state_normal = 0
/// objects hill with label "koth_hill"
/// widget hill_widget at bottom_center
/// engine
///   name = koth_title
/// end
/// </code>
/// Each turns back into the exact Megalo tokens (game options gathered in one game_options block,
/// consecutive constants / widgets / stats in one block each).
/// </summary>
internal sealed partial class Transpiler
{
    private const string VarsMarker = "\u0001variables";
    private const string OptionsMarker = "\u0001game_options";
    private readonly List<(string Text, int Line)> _options = new();
    private bool _optionsPlaced;
    private string? _group;
    /// <summary>The group block of the previous top-level item (consecutive constants share one block).</summary>
    private string? _prevGroup;

    /// <summary>Top-level words that start a declaration.</summary>
    public static readonly HashSet<string> DeclarationWords = new(StringComparer.Ordinal)
    {
        "option", "hidden", "locked", "setting", "traits", "constant", "objects", "widget", "stat", "engine", "teams",
        "loadout", "loadout_palette", "map_permissions", "player_rating", "base", "localized_include", "string_table",
        "widgets", "stats", "constants",
    };

    /// <summary>Kalo Script block names → Megalo.</summary>
    private static readonly Dictionary<string, string> GenericBlocks = new(StringComparer.Ordinal)
    {
        ["engine"] = "engine_data", ["teams"] = "teams", ["loadout"] = "loadout", ["loadout_palette"] = "loadout_palette",
        ["map_permissions"] = "map_permissions", ["player_rating"] = "player_rating", ["string_table"] = "string_table",
        ["widgets"] = "hud_widgets", ["stats"] = "game_stats", ["constants"] = "constants",
    };

    /// <summary>Parses a declaration at the top level; false when the next token doesn't start one.</summary>
    private bool Declaration()
    {
        var t = Peek;
        if (t.Kind != T.Word || !DeclarationWords.Contains(t.Text)) return false;
        // "option(...)" etc. would be a call, not a declaration.
        if (PeekAt(1).Is("(")) return false;
        _indent = 0;
        switch (t.Text)
        {
            case "option":
            case "hidden":
            case "locked":
            case "setting":
            case "traits":
                PlaceOptions(t.Line);
                _options.AddRange(GameOption(0));
                _group = null;
                return true;
            case "constant":
            {
                Next();
                string name = Word("a constant name");
                Expect("=", "write constant NAME = value");
                Grouped("constants", $"number {name} {Rest(Peek.Line)}", t.Line);
                return true;
            }
            case "widget":
            {
                Next();
                string name = Word("a widget name");
                if (Peek.Is("at")) Next();
                Grouped("hud_widgets", $"{name} {Rest(t.Line)}".TrimEnd(), t.Line);
                return true;
            }
            case "stat":
            {
                Next();
                string name = Word("a statistic name");
                Expect("=", "write stat NAME = type text sort ...");
                Grouped("game_stats", $"{name} {Rest(Peek.Line)}", t.Line);
                return true;
            }
            case "objects":
                Objects();
                _group = null;
                return true;
            case "base":
            case "localized_include":
                Next();
                _out.Add(($"{t.Text} {Rest(t.Line)}", t.Line));
                _group = null;
                return true;
            default:
                Next();
                GenericBlock(GenericBlocks[t.Text], t, 0);
                _group = null;
                return true;
        }
    }

    private void PlaceOptions(int line)
    {
        if (_optionsPlaced) return;
        _optionsPlaced = true;
        _out.Add((OptionsMarker, line));
    }

    /// <summary>A line inside a block shared by consecutive declarations (constants, hud_widgets, game_stats).</summary>
    private void Grouped(string block, string text, int line)
    {
        if (_prevGroup == block && _out.Count > 0 && _out[^1].Text == "end")
        {
            _out.Insert(_out.Count - 1, ("\t" + text, line));
            _group = block;
            return;
        }
        _out.Add((block, line));
        _out.Add(("\t" + text, line));
        _out.Add(("end", line));
        _group = block;
    }

    /// <summary>The rest of a source line as Megalo tokens (strings quoted again).</summary>
    private string Rest(int line)
    {
        var parts = new List<string>();
        while (Peek.Kind != T.End && Peek.Kind != T.Raw && Peek.Line == line)
        {
            var t = Next();
            if (t.Kind == T.Op && t.Text == "-" && Peek.Kind == T.Number && Peek.Line == line) { parts.Add("-" + Next().Text); continue; }
            if (t.Kind == T.Word && Peek.Is(".") && PeekAt(1).Kind == T.Word && PeekAt(1).Line == line)
            {
                var sb = new StringBuilder(t.Text);
                while (Peek.Is(".") && PeekAt(1).Kind == T.Word) { Next(); sb.Append('.').Append(Next().Text); }
                parts.Add(sb.ToString());
                continue;
            }
            parts.Add(t.Kind == T.String ? "\"" + t.Text + "\"" : t.Text);
        }
        return string.Join(" ", parts);
    }

    /// <summary>Tokens of the current line up to '=' (not consumed), as Megalo text.</summary>
    private List<string> UntilEquals(int line)
    {
        var parts = new List<string>();
        while (Peek.Kind != T.End && Peek.Line == line && !Peek.Is("="))
        {
            var t = Next();
            parts.Add(t.Kind == T.String ? "\"" + t.Text + "\"" : t.Text);
        }
        return parts;
    }

    /// <summary>
    /// One game option entry (and its nested lines), indented by depth inside game_options:
    /// [hidden|locked] option ... | traits ... | setting ...
    /// </summary>
    private List<(string Text, int Line)> GameOption(int depth)
    {
        var lines = new List<(string, int)>();
        string pad = new('\t', depth);
        var t = Next();
        int line = t.Line;
        string prefix = "";
        while (t.Is("hidden") || t.Is("locked"))
        {
            prefix += t.Text == "hidden" ? "hide " : "lock ";
            t = Next();
        }
        switch (t.Text)
        {
            case "option":
            {
                // option NAME TITLE DESC = DEFAULT [range MIN MAX]   |   ... newline (value V [N [D]])* end
                var head = UntilEquals(t.Line);
                Expect("=", "an option needs '= default'");
                if (head.Count == 1)
                {
                    // option NAME = VALUE: sets (or with locked, fixes) an option declared in an include.
                    lines.Add(($"{pad}{prefix}option {head[0]} {Rest(t.Line)}", line));
                    return lines;
                }
                if (head.Count < 3) throw new ParseError(t, "write option NAME \"Title\" \"Description\" = default");
                var def = new List<string>();
                while (Peek.Line == t.Line && !Peek.Is("range") && Peek.Kind != T.End)
                {
                    var d = Next();
                    if (d.Kind == T.Op && d.Text == "-" && Peek.Kind == T.Number) def.Add("-" + Next().Text);
                    else def.Add(TokenText(d));
                }
                if (Peek.Is("range") && Peek.Line == t.Line)
                {
                    Next();
                    string range = Rest(t.Line);
                    lines.Add(($"{pad}{prefix}ranged_option {string.Join(" ", head)} {string.Join(" ", def)} {range} end", line));
                    return lines;
                }
                lines.Add(($"{pad}{prefix}option {string.Join(" ", head)}", line));
                lines.Add(($"{pad}\t{string.Join(" ", def)}", line));
                while (Peek.Is("value"))
                {
                    var v = Next();
                    var tokens = SplitTokens(Rest(v.Line));
                    while (tokens.Count < 3) tokens.Add("\"\"");
                    lines.Add(($"{pad}\t{string.Join(" ", tokens)}", v.Line));
                }
                lines.Add(($"{pad}end", Expect("end", "an option with values ends with 'end'").Line));
                return lines;
            }
            case "traits":
            {
                var head = Rest(t.Line);
                lines.Add(($"{pad}{prefix}player_traits {head}", line));
                lines.AddRange(KeyValues(depth + 1, "traits"));
                lines.Add(($"{pad}end", Expect("end", "traits end with 'end'").Line));
                return lines;
            }
            case "setting":
            {
                // setting option/traits ... (overrides one from an include), setting NAME = VALUE, or setting NAME + lines + end.
                if (Peek.Is("option") || Peek.Is("traits") || Peek.Is("hidden"))
                {
                    var inner = GameOption(depth);
                    inner[0] = (inner[0].Text.Insert(depth, prefix + "override "), inner[0].Line);
                    return inner;
                }
                string name = Word("a setting name");
                if (Peek.Is("=") && Peek.Line == t.Line)
                {
                    Next();
                    lines.Add(($"{pad}{prefix}override {name} {Rest(t.Line)}", line));
                    return lines;
                }
                lines.Add(($"{pad}{prefix}override {name}", line));
                while (!Peek.Is("end") && Peek.Kind != T.End)
                {
                    if (Peek.Is("option") || Peek.Is("traits") || Peek.Is("setting") || Peek.Is("hidden") || Peek.Is("locked"))
                        lines.AddRange(GameOption(depth + 1));
                    else lines.AddRange(KeyValues(depth + 1, "setting", single: true));
                }
                lines.Add(($"{pad}end", Expect("end").Line));
                return lines;
            }
            default:
                throw new ParseError(t, $"expected option, traits or setting after {prefix.Trim()}");
        }
    }

    /// <summary>"key = value..." lines until end (or one line when single).</summary>
    private List<(string Text, int Line)> KeyValues(int depth, string what, bool single = false)
    {
        var lines = new List<(string, int)>();
        string pad = new('\t', depth);
        while (!Peek.Is("end") && Peek.Kind != T.End)
        {
            var key = Peek;
            if (key.Kind != T.Word) throw new ParseError(key, $"expected a name = value line in this {what}");
            Next();
            if (!Peek.Is("=") || Peek.Line != key.Line) throw new ParseError(Peek, $"write {key.Text} = value");
            Next();
            lines.Add(($"{pad}{key.Text} {Rest(key.Line)}", key.Line));
            if (single) break;
        }
        return lines;
    }

    /// <summary>objects NAME with label "x" [min N] | objects NAME + key = value lines + end → map_object.</summary>
    private void Objects()
    {
        var t = Next();
        string name = Word("a name for the objects");
        _out.Add(($"map_object {name}", t.Line));
        if (Peek.Is("with") && Peek.Line == t.Line)
        {
            Next();
            // with label "x" min 1  → label "x" / min 1 (pairs)
            var tokens = SplitTokens(Rest(t.Line));
            for (int i = 0; i + 1 < tokens.Count; i += 2) _out.Add(($"\t{tokens[i]} {tokens[i + 1]}", t.Line));
            if (tokens.Count % 2 == 1) throw new ParseError(t, "write objects NAME with label \"x\" (and optionally min N)");
            _out.Add(("end", t.Line));
            return;
        }
        foreach (var l in KeyValues(1, "objects")) _out.Add(l);
        _out.Add(("end", Expect("end", "objects without 'with' end with 'end'").Line));
    }

    /// <summary>A Megalo block written as Kalo Script: header line, "key = value" lines, nested blocks, end.</summary>
    private void GenericBlock(string megaloKeyword, Tok start, int depth)
    {
        string pad = new('\t', depth);
        string head = Rest(start.Line);
        _out.Add(($"{pad}{megaloKeyword}{(head.Length > 0 ? " " + head : "")}", start.Line));
        while (!Peek.Is("end") && Peek.Kind != T.End)
        {
            var key = Peek;
            if (key.Kind != T.Word) throw new ParseError(key, "expected name = value, a nested block, or end");
            Next();
            if (Peek.Is("=") && Peek.Line == key.Line)
            {
                Next();
                _out.Add(($"{pad}\t{key.Text} {Rest(key.Line)}", key.Line));
            }
            else GenericBlock(key.Text, key, depth + 1);
        }
        _out.Add(($"{pad}end", Expect("end", $"missing 'end' for {megaloKeyword}").Line));
    }

    private static string TokenText(Tok t) => t.Kind == T.String ? "\"" + t.Text + "\"" : t.Text;

    /// <summary>Splits Megalo text into tokens, keeping "quoted strings" whole.</summary>
    internal static List<string> SplitTokens(string text)
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
            else while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '"') i++; // seconds_30"" is two tokens
            list.Add(text[s..i]);
        }
        return list;
    }
}

/// <summary>Megalo declarations → Kalo Script, each block checked by turning it back and comparing the tokens.</summary>
internal static class DeclarationConverter
{
    private sealed record L(int No, List<string> Tokens, string Comment);

    /// <summary>Why the last Convert kept a block as Megalo (for diagnostics).</summary>
    public static string? LastReason { get; private set; }

    /// <summary>Kalo Script for a top-level Megalo declaration block, or null to keep it as Megalo.</summary>
    public static string? Convert(IReadOnlyList<string> rawLines)
    {
        LastReason = null;
        var lines = rawLines.Select((r, i) => { var (code, comment) = Strip(r); return new L(i, Transpiler.SplitTokens(code), comment); }).ToList();
        var code = lines.Where(l => l.Tokens.Count > 0).ToList();
        if (code.Count == 0) return null;
        string kw = code[0].Tokens[0];
        string? kalo;
        try
        {
            kalo = kw switch
            {
                "game_options" => GameOptions(lines),
                "constants" => Rows(lines, "constant", t => t.Count >= 3 && t[0] == "number" ? $"{t[1]} = {Join(t.Skip(2))}" : null),
                "hud_widgets" => Rows(lines, "widget", t => t.Count >= 2 ? $"{t[0]} at {Join(t.Skip(1))}" : t.Count == 1 ? t[0] : null),
                "game_stats" => Rows(lines, "stat", t => t.Count >= 2 ? $"{t[0]} = {Join(t.Skip(1))}" : null),
                "map_object" => MapObject(lines),
                "engine_data" => Generic(lines, "engine"),
                "teams" or "loadout" or "loadout_palette" or "map_permissions" or "player_rating" or "string_table" => Generic(lines, kw),
                "base" or "localized_include" => code.Count == 1 ? string.Join(" ", code[0].Tokens) + Trail(code[0].Comment) : null,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            kalo = null;
            LastReason = "converter: " + ex.Message;
        }
        if (kalo == null) { LastReason ??= "no Kalo Script form for this block"; return null; }
        // Keep it only if it turns back into exactly the same Megalo.
        var back = KaloScript.Transpile(kalo);
        if (!back.Succeeded) { LastReason = "Kalo Script errors: " + string.Join("; ", back.Errors.Take(2)) + "\n" + kalo; return null; }
        var original = lines.SelectMany(l => l.Tokens).ToList();
        var again = back.Megalo.Replace("\r", "").Split('\n').SelectMany(l => Transpiler.SplitTokens(Strip(l).Code)).ToList();
        if (original.SequenceEqual(again)) return kalo;
        int k = 0;
        while (k < original.Count && k < again.Count && original[k] == again[k]) k++;
        LastReason = $"differs at token {k}: {Join(original.Skip(Math.Max(0, k - 4)).Take(10))}  |  {Join(again.Skip(Math.Max(0, k - 4)).Take(10))}";
        return null;
    }

    private static string Join(IEnumerable<string> tokens) => string.Join(" ", tokens);
    private static string Trail(string comment) => comment.Length > 0 ? "  --" + comment : "";

    /// <summary>Comment-only lines become -- lines.</summary>
    private static IEnumerable<string> Comments(L l, string pad) => l.Tokens.Count == 0 && l.Comment.Length > 0 ? new[] { pad + "--" + l.Comment } : Array.Empty<string>();

    /// <summary>A block of one-line rows (constants, widgets, stats): one Kalo declaration per row.</summary>
    private static string? Rows(List<L> lines, string word, Func<List<string>, string?> row)
    {
        var sb = new StringBuilder();
        var code = lines.Where(l => l.Tokens.Count > 0).ToList();
        if (code.Count == 2 && code[0].Tokens.Count == 1 && code[^1].Tokens is ["end"])
            return Generic(lines, word + "s"); // an empty block: widgets / stats / constants ... end
        if (code.Count < 3 || code[0].Tokens.Count != 1 || code[^1].Tokens is not ["end"]) return null;
        foreach (var l in lines)
        {
            if (l.Tokens.Count == 0) { foreach (var c in Comments(l, "")) sb.AppendLine(c); continue; }
            if (l == code[0] || l == code[^1]) { if (l.Comment.Length > 0) sb.AppendLine("--" + l.Comment); continue; }
            string? r = row(l.Tokens);
            if (r == null) return null;
            sb.AppendLine($"{word} {r}{Trail(l.Comment)}");
        }
        return sb.ToString();
    }

    private static string? MapObject(List<L> lines)
    {
        var code = lines.Where(l => l.Tokens.Count > 0).ToList();
        if (code[0].Tokens.Count != 2 || code[^1].Tokens is not ["end"]) return null;
        string name = code[0].Tokens[1];
        var rows = code.Skip(1).Take(code.Count - 2).ToList();
        var sb = new StringBuilder();
        foreach (var l in lines.Where(l => l.Tokens.Count == 0)) foreach (var c in Comments(l, "")) sb.AppendLine(c);
        if (rows.All(r => r.Tokens.Count == 2) && rows.Count is >= 1 and <= 3)
        {
            sb.AppendLine($"objects {name} with {string.Join(" ", rows.SelectMany(r => r.Tokens))}{Trail(code[0].Comment)}");
            return sb.ToString();
        }
        sb.AppendLine($"objects {name}");
        foreach (var r in rows) sb.AppendLine($"  {r.Tokens[0]} = {Join(r.Tokens.Skip(1))}{Trail(r.Comment)}");
        sb.AppendLine("end");
        return sb.ToString();
    }

    /// <summary>header + "key value" rows + nested blocks (a row of one word opens one) + end.</summary>
    private static string? Generic(List<L> lines, string kaloWord)
    {
        var sb = new StringBuilder();
        int depth = 0;
        bool first = true;
        foreach (var l in lines)
        {
            string pad = new(' ', depth * 2);
            if (l.Tokens.Count == 0) { foreach (var c in Comments(l, pad)) sb.AppendLine(c); continue; }
            var t = l.Tokens;
            if (first)
            {
                sb.AppendLine($"{kaloWord}{(t.Count > 1 ? " " + Join(t.Skip(1)) : "")}{Trail(l.Comment)}");
                first = false;
                depth = 1;
                continue;
            }
            if (t is ["end"])
            {
                depth--;
                sb.AppendLine(new string(' ', Math.Max(0, depth) * 2) + "end" + Trail(l.Comment));
                if (depth < 0) return null;
                continue;
            }
            if (t.Contains("end")) return null;
            if (t.Count == 1)
            {
                sb.AppendLine($"{pad}{t[0]}{Trail(l.Comment)}");
                depth++;
                continue;
            }
            sb.AppendLine($"{pad}{t[0]} = {Join(t.Skip(1))}{Trail(l.Comment)}");
        }
        return depth == 0 ? sb.ToString() : null;
    }

    /// <summary>game_options → option / traits / setting declarations (token-based, as Megalo reads it).</summary>
    private static string? GameOptions(List<L> lines)
    {
        // Flatten to tokens with their line, keeping comment-only lines to re-emit.
        var toks = new List<(string Text, int Line)>();
        var comments = new Dictionary<int, string>();
        foreach (var l in lines)
        {
            foreach (var t in l.Tokens) toks.Add((t, l.No));
            if (l.Comment.Length > 0) comments[l.No] = l.Comment;
        }
        if (toks.Count < 2 || toks[0].Text != "game_options" || toks[^1].Text != "end") return null;
        var sb = new StringBuilder();
        int i = 1;
        int lastLine = toks[0].Line;
        void CommentsUpTo(int line, string pad)
        {
            foreach (var kv in comments.Where(kv => kv.Key > lastLine && kv.Key < line).OrderBy(kv => kv.Key)) sb.AppendLine(pad + "--" + kv.Value);
            lastLine = Math.Max(lastLine, line);
        }
        while (i < toks.Count - 1)
        {
            CommentsUpTo(toks[i].Line, "");
            if (!Entry(toks, ref i, sb, "", comments, ref lastLine)) return null;
        }
        return sb.ToString();
    }

    private static readonly HashSet<string> EntryWords = new() { "option", "ranged_option", "player_traits", "override", "hide", "lock" };

    /// <summary>One game_options entry at toks[i]; appends Kalo Script.</summary>
    private static bool Entry(List<(string Text, int Line)> toks, ref int i, StringBuilder sb, string pad, Dictionary<int, string> comments, ref int lastLine)
    {
        string prefix = "";
        while (toks[i].Text is "hide" or "lock")
        {
            prefix += toks[i].Text == "hide" ? "hidden " : "locked ";
            i++;
        }
        string kw = toks[i].Text;
        int startLine = toks[i].Line;
        if (kw == "option")
        {
            // option TITLE VALUE on one line: sets an option declared elsewhere.
            var same = toks.Skip(i + 1).TakeWhile(t => t.Line == startLine).ToList();
            if (same.Count == 2 && !same.Any(t => t.Text == "end"))
            {
                sb.AppendLine($"{pad}{prefix}option {same[0].Text} = {same[1].Text}");
                i += 3;
                return true;
            }
        }
        if (kw == "option" || kw == "ranged_option")
        {
            int end = IndexOf(toks, "end", i);
            if (end < 0) return false;
            var body = toks.Skip(i + 1).Take(end - i - 1).Select(t => t.Text).ToList();
            if (kw == "ranged_option")
            {
                if (body.Count != 6) return false;
                sb.AppendLine($"{pad}{prefix}option {body[0]} {body[1]} {body[2]} = {body[3]} range {body[4]} {body[5]}");
            }
            else
            {
                if (body.Count < 7 || (body.Count - 4) % 3 != 0) return false;
                sb.AppendLine($"{pad}{prefix}option {body[0]} {body[1]} {body[2]} = {body[3]}");
                for (int k = 4; k < body.Count; k += 3)
                {
                    var v = body.Skip(k).Take(3).ToList();
                    while (v.Count > 1 && v[^1] == "\"\"") v.RemoveAt(v.Count - 1);
                    sb.AppendLine($"{pad}  value {Join(v)}");
                }
                sb.AppendLine($"{pad}end");
            }
            lastLine = Math.Max(lastLine, toks[end].Line);
            i = end + 1;
            return true;
        }
        if (kw == "player_traits")
        {
            int end = IndexOf(toks, "end", i);
            if (end < 0 || end - i < 4) return false;
            sb.AppendLine($"{pad}{prefix}traits {toks[i + 1].Text} {toks[i + 2].Text} {toks[i + 3].Text}");
            if (!Rows(toks, i + 4, end, sb, pad + "  ", comments, ref lastLine)) return false;
            sb.AppendLine($"{pad}end");
            i = end + 1;
            return true;
        }
        if (kw == "override")
        {
            string next = i + 1 < toks.Count ? toks[i + 1].Text : "";
            if (next is "option" or "ranged_option" or "player_traits" or "hide")
            {
                // override option ... : the overriding entry, prefixed.
                var inner = new StringBuilder();
                int j = i + 1;
                if (!Entry(toks, ref j, inner, "", comments, ref lastLine)) return false;
                var text = inner.ToString();
                sb.Append(pad + prefix + "setting " + text.Replace("\n", "\n" + pad).TrimEnd(' '));
                if (!text.EndsWith('\n')) sb.AppendLine();
                i = j;
                return true;
            }
            string name = next;
            var sameLine = toks.Skip(i + 2).TakeWhile(t => t.Line == startLine).Select(t => t.Text).ToList();
            if (sameLine.Count > 0 && !sameLine.Contains("end"))
            {
                sb.AppendLine($"{pad}{prefix}setting {name} = {Join(sameLine)}");
                i += 2 + sameLine.Count;
                return true;
            }
            // Block: key value rows and nested entries until its end.
            sb.AppendLine($"{pad}{prefix}setting {name}");
            int k = i + 2;
            while (k < toks.Count && toks[k].Text != "end")
            {
                if (EntryWords.Contains(toks[k].Text))
                {
                    if (!Entry(toks, ref k, sb, pad + "  ", comments, ref lastLine)) return false;
                    continue;
                }
                int line = toks[k].Line;
                var row = toks.Skip(k).TakeWhile(t => t.Line == line && t.Text != "end").Select(t => t.Text).ToList();
                if (row.Count < 2) return false;
                sb.AppendLine($"{pad}  {row[0]} = {Join(row.Skip(1))}");
                k += row.Count;
            }
            if (k >= toks.Count) return false;
            sb.AppendLine($"{pad}end");
            i = k + 1;
            return true;
        }
        return false;
    }

    /// <summary>"key value..." rows between two token indexes (one row per line).</summary>
    private static bool Rows(List<(string Text, int Line)> toks, int from, int to, StringBuilder sb, string pad, Dictionary<int, string> comments, ref int lastLine)
    {
        int k = from;
        while (k < to)
        {
            int line = toks[k].Line, after = lastLine;
            foreach (var kv in comments.Where(kv => kv.Key > after && kv.Key < line).OrderBy(kv => kv.Key)) sb.AppendLine(pad + "--" + kv.Value);
            lastLine = line;
            var row = toks.Skip(k).Take(to - k).TakeWhile(t => t.Line == line).Select(t => t.Text).ToList();
            if (row.Count < 2) return false;
            sb.AppendLine($"{pad}{row[0]} = {Join(row.Skip(1))}");
            k += row.Count;
        }
        return true;
    }

    private static int IndexOf(List<(string Text, int Line)> toks, string what, int from)
    {
        for (int k = from; k < toks.Count; k++) if (toks[k].Text == what) return k;
        return -1;
    }

    internal static (string Code, string Comment) Strip(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inString = !inString;
            else if (line[i] == ';' && !inString) return (line[..i].TrimEnd(), line[(i + 1)..].TrimEnd());
        }
        return (line.TrimEnd(), "");
    }
}
