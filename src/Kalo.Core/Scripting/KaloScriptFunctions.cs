using Kalo.Core.Scripting.Multiplayer;
namespace Kalo.Core.Scripting;

/// <summary>
/// Kalo Script functions, expanded where they are called (Megalo text can't call one block from several places):
/// <code>
/// function award(p, points)
///   p.score += points
/// end
/// ...
/// award(killer, 2)
/// </code>
/// Each call is a copy of the body with the arguments in place of the parameters (also inside "{p}" in texts);
/// temps declared in the body get a name of their own per copy. No return value, no recursion.
/// </summary>
internal sealed partial class Transpiler
{
    private sealed record FunctionDef(string Name, List<string> Params, List<Tok> Body, Tok At);

    private readonly Dictionary<string, FunctionDef> _functions = new(StringComparer.Ordinal);
    private int _expansions;
    private bool _checkingBody;
    private readonly HashSet<string> _recursive = new(StringComparer.Ordinal);

    /// <summary>Takes the function definitions out of the token list (they can be anywhere at the top level).</summary>
    private void ExtractFunctions()
    {
        int k = 0;
        int depth = 0;
        while (k < _t.Count && _t[k].Kind != T.End)
        {
            var t = _t[k];
            if (depth == 0 && t.Is("function") && (k == 0 || _t[k - 1].Line < t.Line))
            {
                int start = k;
                try
                {
                    var def = ReadFunction(ref k);
                    if (_functions.ContainsKey(def.Name)) throw new ParseError(def.At, $"function {def.Name} is defined twice");
                    if (MegaloDocs.Actions.ContainsKey(def.Name) || MegaloDocs.Conditions.ContainsKey(def.Name))
                        throw new ParseError(def.At, $"{def.Name} is already a Megalo action or condition: choose another name");
                    _functions[def.Name] = def;
                }
                catch (ParseError e)
                {
                    _errors.Add(new ScriptError(e.At.Line, e.At.Col, e.Message));
                    k = SkipFunction(start);
                }
                _t.RemoveRange(start, k - start);
                k = start;
                continue;
            }
            if (t.Is("if") || t.Is("do") || (t.Is("inline") && k + 1 < _t.Count && _t[k + 1].Line > t.Line)) depth++;
            else if (t.Is("end")) depth = Math.Max(0, depth - 1);
            k++;
        }
        // Recursion would never end: refuse it where the function is defined.
        foreach (var f in _functions.Values)
            if (Calls(f, f.Name, new HashSet<string>()))
            {
                _recursive.Add(f.Name);
                _errors.Add(new ScriptError(f.At.Line, f.At.Col, $"function {f.Name} calls itself (directly or through another function): Megalo can't do that"));
            }
        // Check each body once (syntax), even when the function isn't called yet.
        foreach (var f in _functions.Values) if (!_recursive.Contains(f.Name)) CheckBody(f);
    }

    /// <summary>function NAME(a, b) ... end → the definition; k ends after its end.</summary>
    private FunctionDef ReadFunction(ref int k)
    {
        var at = _t[k++];
        if (_t[k].Kind != T.Word) throw new ParseError(_t[k], "expected the function's name after function");
        var nameTok = _t[k++];
        if (!_t[k].Is("(")) throw new ParseError(_t[k], $"expected ( after function {nameTok.Text}, e.g. function {nameTok.Text}(p)");
        k++;
        var parameters = new List<string>();
        while (!_t[k].Is(")"))
        {
            if (_t[k].Kind != T.Word) throw new ParseError(_t[k], "expected a parameter name");
            if (parameters.Contains(_t[k].Text)) throw new ParseError(_t[k], $"parameter {_t[k].Text} is listed twice");
            parameters.Add(_t[k++].Text);
            if (_t[k].Is(",")) { k++; continue; }
            if (!_t[k].Is(")")) throw new ParseError(_t[k], "expected , or ) in the parameter list");
        }
        k++;
        int bodyStart = k, depth = 0;
        for (; k < _t.Count && _t[k].Kind != T.End; k++)
        {
            var t = _t[k];
            if (t.Is("function") && k > 0 && _t[k - 1].Line < t.Line) throw new ParseError(t, $"missing 'end' for function {nameTok.Text}");
            if (t.Is("if") || t.Is("do") || (t.Is("inline") && k + 1 < _t.Count && _t[k + 1].Line > t.Line)) depth++;
            else if (t.Is("end"))
            {
                if (depth == 0)
                {
                    var body = _t.GetRange(bodyStart, k - bodyStart);
                    k++;
                    return new FunctionDef(nameTok.Text, parameters, body, nameTok);
                }
                depth--;
            }
        }
        throw new ParseError(at, $"missing 'end' for function {nameTok.Text}");
    }

    private int SkipFunction(int start)
    {
        int k = start + 1;
        for (; k < _t.Count && _t[k].Kind != T.End; k++)
            if (_t[k].Line > _t[start].Line && _t[k].Col == 1 && !_t[k].Is("end") && (_t[k].Is("function") || _t[k].Is("for") || _t[k].Is("do") || _t[k].Is("on") || _t[k].Is("declare")))
                return k;
        return k;
    }

    private bool Calls(FunctionDef f, string target, HashSet<string> seen)
    {
        if (!seen.Add(f.Name)) return false;
        for (int i = 0; i + 1 < f.Body.Count; i++)
        {
            var t = f.Body[i];
            if (t.Kind != T.Word || !f.Body[i + 1].Is("(") || (i > 0 && f.Body[i - 1].Is("."))) continue;
            if (t.Text == target) return true;
            if (_functions.TryGetValue(t.Text, out var g) && Calls(g, target, seen)) return true;
        }
        return false;
    }

    /// <summary>Parses a body on its own (parameters as plain names) to report its syntax errors once.</summary>
    private void CheckBody(FunctionDef f)
    {
        var (savedT, savedP, savedIndent) = (_t, _p, _indent);
        int outCount = _out.Count;
        _t = new List<Tok>(f.Body) { new Tok(T.End, "", f.Body.Count > 0 ? f.Body[^1].Line + 1 : f.At.Line, 1) };
        _p = 0;
        _checkingBody = true;
        try
        {
            Block();
            if (Peek.Kind != T.End) _errors.Add(new ScriptError(Peek.Line, Peek.Col, $"unexpected {Peek} in function {f.Name}"));
        }
        catch (ParseError e)
        {
            _errors.Add(new ScriptError(e.At.Line, e.At.Col, e.Message));
        }
        finally
        {
            _checkingBody = false;
            _out.RemoveRange(outCount, _out.Count - outCount);
            (_t, _p, _indent) = (savedT, savedP, savedIndent);
        }
    }

    /// <summary>
    /// At a statement NAME(args) where NAME is a function: replaces the call with a copy of the body (parameters
    /// replaced by the arguments) and returns true; the caller then parses those statements.
    /// </summary>
    private bool ExpandCall()
    {
        var t = Peek;
        if (t.Kind != T.Word || !_functions.TryGetValue(t.Text, out var f) || !PeekAt(1).Is("(")) return false;
        if (_recursive.Contains(f.Name))
        {
            // Already reported where it is defined: skip the call quietly.
            int skip = _p + 2, d = 0;
            while (_t[skip].Kind != T.End && !(_t[skip].Is(")") && d-- == 0)) { if (_t[skip].Is("(")) d++; skip++; }
            _t.RemoveRange(_p, Math.Min(skip + 1, _t.Count - 1) - _p);
            return true;
        }
        if (++_expansions > 2000) throw new ParseError(t, "too many function calls (each call is a copy of the function)");
        int start = _p;
        int k = _p + 2;
        var args = new List<List<Tok>>();
        var current = new List<Tok>();
        int depth = 0;
        while (true)
        {
            var a = _t[k];
            if (a.Kind == T.End || (a.Line != t.Line && current.Count == 0 && a.Is("end"))) throw new ParseError(t, $"expected ')' to close {f.Name}(");
            if (a.Is("(")) depth++;
            if (a.Is(")") && depth-- == 0) { if (current.Count > 0 || args.Count > 0) args.Add(current); k++; break; }
            if (a.Is(",") && depth == 0) { if (current.Count == 0) throw new ParseError(a, "missing argument"); args.Add(current); current = new List<Tok>(); k++; continue; }
            current.Add(a);
            k++;
        }
        if (args.Count != f.Params.Count)
            throw new ParseError(t, $"{f.Name} takes {f.Params.Count} argument(s) ({string.Join(", ", f.Params)}), not {args.Count}");
        var map = new Dictionary<string, List<Tok>>(StringComparer.Ordinal);
        for (int i = 0; i < args.Count; i++) map[f.Params[i]] = args[i];
        // Temps declared in the body: a name of their own in each copy.
        for (int i = 0; i + 2 < f.Body.Count; i++)
            if (f.Body[i].Is("temp") && f.Body[i + 2].Kind == T.Word && !map.ContainsKey(f.Body[i + 2].Text))
                map[f.Body[i + 2].Text] = new List<Tok> { f.Body[i + 2] with { Text = $"{f.Body[i + 2].Text}_{f.Name}_{_expansions}" } };

        var copy = new List<Tok>();
        for (int i = 0; i < f.Body.Count; i++)
        {
            var b = f.Body[i];
            bool member = i > 0 && f.Body[i - 1].Is(".");
            if (b.Kind == T.Word && !member && map.TryGetValue(b.Text, out var with))
            {
                // Keep the body's line numbers: errors point inside the function.
                copy.AddRange(with.Select(w => w with { Line = b.Line, Col = b.Col }));
                continue;
            }
            if (b.Kind == T.String && b.Text.Contains('{')) b = b with { Text = SubstituteInText(b.Text, map) };
            copy.Add(b);
        }
        _t.RemoveRange(start, k - start);
        _t.InsertRange(start, copy);
        _p = start;
        return true;
    }

    /// <summary>"{p} scored": a parameter inside braces is replaced by its argument (a single name or path).</summary>
    private static string SubstituteInText(string text, Dictionary<string, List<Tok>> map)
    {
        foreach (var (name, toks) in map)
        {
            string value = string.Concat(toks.Select(x => x.Text));
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\{\s*" + System.Text.RegularExpressions.Regex.Escape(name) + @"(?=[\s.}])", "{" + value);
        }
        return text;
    }
}
