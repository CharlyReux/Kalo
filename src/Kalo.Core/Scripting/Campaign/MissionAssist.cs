using System.Text.RegularExpressions;

namespace Kalo.Core.Scripting.Campaign;

/// <summary>A script or global a mission declares (in Kalo Script or HaloScript text).</summary>
public sealed record MissionDecl(string Kind, string Name, int Line, int Column, int Params = -1, string? Returns = null, string? File = null)
{
    public string Display => Kind == "global" ? $"global {Name}" : $"{Name}  ({Kind})";
    public override string ToString() => Display;
}

/// <summary>A problem found in a mission script: 1-based line and column.</summary>
public sealed record MissionMessage(int Line, int Column, int Length, bool IsError, string Text);

/// <summary>An autocomplete suggestion or a hover help for mission scripts.</summary>
public sealed record MissionHelp(string Text, string Kind, string? Signature, string? About);

/// <summary>
/// Help for Kalo Script mission files: declarations, checks (unknown functions, wrong number of values, names that are
/// not in the map), autocomplete and hover help. Names: what the map holds (from the host's scenarioNames), by kind.
/// Others: scripts and globals declared in the mission's other script files (shared ones too).
/// </summary>
public static class MissionAssist
{
    private static readonly Regex ScriptLine = new(@"^(?<kind>startup|dormant|continuous|command_script)\s+(?<name>[^\s:]+)\s*:", RegexOptions.Compiled);
    private static readonly Regex DefLine = new(@"^(?<stub>stub\s+)?def\s+(?<name>[^\s(]+)\s*\((?<params>[^)]*)\)\s*(->\s*(?<ret>\w+))?\s*:", RegexOptions.Compiled);
    private static readonly Regex GlobalLine = new(@"^global\s+(?<name>[^\s:]+)\s*:\s*(?<type>\w+)", RegexOptions.Compiled);

    public static readonly string[] Keywords =
    {
        "startup", "dormant", "continuous", "command_script", "def", "stub", "global", "if", "elif", "else", "repeat", "every",
        "timeout", "until", "return", "pass", "and", "or", "not", "True", "False", "None", "begin_random", "begin_random_count",
    };

    /// <summary>Scripts and globals declared in Kalo Script mission text.</summary>
    public static List<MissionDecl> Declarations(string python, string? file = null)
    {
        var list = new List<MissionDecl>();
        var lines = python.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i];
            Match m;
            if ((m = ScriptLine.Match(l)).Success) list.Add(new MissionDecl(m.Groups["kind"].Value, m.Groups["name"].Value, i + 1, m.Groups["name"].Index + 1, 0, null, file));
            else if ((m = DefLine.Match(l)).Success)
            {
                string ps = m.Groups["params"].Value.Trim();
                list.Add(new MissionDecl(m.Groups["stub"].Success ? "stub" : "static", m.Groups["name"].Value, i + 1, m.Groups["name"].Index + 1,
                    ps.Length == 0 ? 0 : ps.Split(',').Length, m.Groups["ret"].Success ? m.Groups["ret"].Value : "void", file));
            }
            else if ((m = GlobalLine.Match(l)).Success) list.Add(new MissionDecl("global", m.Groups["name"].Value, i + 1, m.Groups["name"].Index + 1, -1, m.Groups["type"].Value, file));
        }
        return list;
    }

    private static readonly Regex HsDeclaration = new(
        @"\(\s*(script\s+(?<kind>startup|dormant|continuous|static|stub|command_script)\s+(?:(?<type>[a-z_]+)\s+)?\(?\s*(?<name>[^\s()]+)|(?<kind>global)\s+(?<type>[a-z_]+)\s+(?<name>[^\s()]+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Scripts and globals declared in HaloScript text (another file of the mission, e.g. a shared global_*.hsc).</summary>
    public static List<MissionDecl> HaloScriptDeclarations(string haloScript, string? file = null)
    {
        var list = new List<MissionDecl>();
        var errors = new List<ScriptError>();
        var (forms, _) = HsReader.Read(haloScript, errors);
        foreach (var f in forms.OfType<HsList>())
        {
            if (f.HeadIs("global") && f.Items.Count >= 3 && f.Items[2] is HsAtom g)
                list.Add(new MissionDecl("global", g.Text, f.Line, 0, -1, (f.Items[1] as HsAtom)?.Text, file));
            else if (f.HeadIs("script") && f.Items.Count >= 3 && f.Items[1] is HsAtom kind)
            {
                bool typed = kind.Text is "static" or "stub";
                var nameNode = typed && f.Items.Count > 3 ? f.Items[3] : f.Items[2];
                string? name = nameNode is HsAtom a ? a.Text : nameNode is HsList { Items.Count: > 0 } nl && nl.Items[0] is HsAtom na ? na.Text : null;
                int ps = nameNode is HsList pl ? pl.Items.Count - 1 : 0;
                if (name != null) list.Add(new MissionDecl(kind.Text, name, f.Line, 0, ps, typed ? (f.Items[2] as HsAtom)?.Text : null, file));
            }
        }
        return list;
    }

    // Parameter types whose values are names of things in the map (or scripts).
    private static readonly Dictionary<string, string[]> NameTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ai"] = new[] { "squad", "squad group", "objective", "task" },
        ["trigger_volume"] = new[] { "trigger volume" },
        ["cutscene_flag"] = new[] { "cutscene flag" },
        ["cutscene_camera_point"] = new[] { "camera point" },
        ["point_reference"] = new[] { "point", "point set" },
        ["object_name"] = new[] { "object" },
        ["device_group"] = new[] { "device group" },
        ["zone_set"] = new[] { "zone set" },
        ["script"] = new[] { "script" },
    };

    private static readonly HashSet<string> Special = new(StringComparer.OrdinalIgnoreCase)
    {
        "set", "if", "cond", "begin", "begin_random", "begin_random_count", "begin_count", "and", "or", "not", "=", "!=", "<", ">", "<=", ">=",
        "+", "-", "*", "/", "%", "script", "global",
    };

    /// <summary>Problems in a Kalo Script mission file. names / others may be null (then names and scripts aren't checked).</summary>
    public static List<MissionMessage> Check(string python, IReadOnlyDictionary<string, List<string>>? names, IReadOnlyList<MissionDecl>? others)
    {
        var messages = new List<MissionMessage>();
        var (forms, errors) = MissionScript.Parse(python);
        foreach (var e in errors) messages.Add(new MissionMessage(e.Line, Math.Max(1, e.Column), 1, true, e.Message));
        var decls = Declarations(python).Concat(others ?? Array.Empty<MissionDecl>()).ToList();
        var scripts = decls.Where(d => d.Kind != "global").GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var globals = new HashSet<string>(decls.Where(d => d.Kind == "global").Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        var byName = NameIndex(names);
        foreach (var f in forms.OfType<HsList>())
        {
            var locals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // def name(p: type...): its parameters are names inside it.
            if (f.HeadIs("script") && f.Items.Count > 3 && f.Items[3] is HsList sig)
                foreach (var p in sig.Items.Skip(1).OfType<HsList>()) if (p.Items.Count == 2 && p.Items[1] is HsAtom pn) locals.Add(pn.Text);
            int bodyAt = f.HeadIs("script") ? (f.Items.Count > 1 && f.Items[1] is HsAtom k && k.Text is "static" or "stub" ? 4 : 3) : 1;
            foreach (var x in f.Items.Skip(bodyAt)) Walk(x, messages, scripts, globals, locals, byName, names != null, others != null);
        }
        return messages.OrderBy(m => m.Line).ThenBy(m => m.Column).ToList();
    }

    private static Dictionary<string, string> NameIndex(IReadOnlyDictionary<string, List<string>>? names)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (names == null) return index;
        foreach (var (kind, list) in names)
            foreach (var n in list) index.TryAdd(n, kind);
        return index;
    }

    private static void Walk(HsNode n, List<MissionMessage> messages, Dictionary<string, MissionDecl> scripts, HashSet<string> globals,
        HashSet<string> locals, Dictionary<string, string> names, bool checkNames, bool checkScripts)
    {
        if (n is not HsList l || l.Items.Count == 0) return;
        foreach (var x in l.Items.Skip(1)) Walk(x, messages, scripts, globals, locals, names, checkNames, checkScripts);
        if (l.Items[0] is not HsAtom { IsString: false } head || head.Col == 0) return;
        string name = head.Text;
        if (Special.Contains(name)) return;
        int count = l.Items.Count - 1;
        var forms = HsDocs.Find(name);
        if (forms != null)
        {
            var fit = forms.Where(f => count >= f.Min && count <= f.Max).ToList();
            if (fit.Count == 0)
            {
                string want = string.Join(" or ", forms.Select(f => f.Signature));
                messages.Add(new MissionMessage(head.Line, head.Col, head.Len, true, $"{name} takes other values: {want}"));
                return;
            }
            if (!checkNames) return;
            var form = fit[0];
            for (int i = 0; i < count && i < form.Params.Count; i++)
            {
                if (l.Items[i + 1] is not HsAtom { IsString: false } arg || arg.Col == 0) continue;
                if (!NameTypes.TryGetValue(form.Params[i].Type, out var kinds)) continue;
                string v = arg.Text;
                if (Known(v, scripts, globals, locals, names)) continue;
                // squad/spawn point and squad/cell (squad_name/starting_location): the squad is what must exist.
                int slash = v.IndexOf('/');
                if (slash > 0 && names.ContainsKey(v[..slash])) continue;
                if (kinds[0] == "script")
                {
                    if (checkScripts) messages.Add(new MissionMessage(arg.Line, arg.Col, arg.Len, false, $"no script named {v} in this mission"));
                    continue;
                }
                messages.Add(new MissionMessage(arg.Line, arg.Col, arg.Len, false, $"no {string.Join(" or ", kinds)} named {v} in this map"));
            }
            return;
        }
        if (scripts.TryGetValue(name, out var decl))
        {
            if (decl.Params >= 0 && decl.Params != count && decl.Kind is "static" or "stub")
                messages.Add(new MissionMessage(head.Line, head.Col, head.Len, true, $"{name} takes {decl.Params} value{(decl.Params == 1 ? "" : "s")}, not {count}"));
            return;
        }
        if (globals.Contains(name) || locals.Contains(name)) return;
        if (checkScripts) messages.Add(new MissionMessage(head.Line, head.Col, head.Len, false, $"no game function or script named {name}"));
    }

    private static bool Known(string v, Dictionary<string, MissionDecl> scripts, HashSet<string> globals, HashSet<string> locals, Dictionary<string, string> names) =>
        names.ContainsKey(v) || scripts.ContainsKey(v) || globals.Contains(v) || locals.Contains(v) || HsDocs.Globals.ContainsKey(v)
        || HsDocs.Functions.ContainsKey(v) || v.Equals("none", StringComparison.OrdinalIgnoreCase) || char.IsDigit(v[0]) || v.StartsWith('-');

    // ---- autocomplete and hover ----

    /// <summary>Suggestions for the word before the caret: functions, map names, scripts and globals, keywords.</summary>
    public static (int Start, List<MissionHelp> Items) Complete(string lineBefore, string python, IReadOnlyDictionary<string, List<string>>? names, IReadOnlyList<MissionDecl>? others)
    {
        int start = lineBefore.Length;
        while (start > 0 && (char.IsLetterOrDigit(lineBefore[start - 1]) || lineBefore[start - 1] is '_' or '/')) start--;
        string prefix = lineBefore[start..];
        var items = new List<MissionHelp>();
        if (prefix.Length == 0) return (start, items);
        bool Match(string s) => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || prefix.Length >= 3 && s.Contains(prefix, StringComparison.OrdinalIgnoreCase);
        foreach (var k in Keywords) if (Match(k)) items.Add(new MissionHelp(k, "keyword", null, null));
        foreach (var d in Declarations(python).Concat(others ?? Array.Empty<MissionDecl>()).GroupBy(d => d.Name).Select(g => g.First()))
            if (Match(d.Name)) items.Add(new MissionHelp(d.Name, d.Kind == "global" ? "global" : "script", null, DeclAbout(d)));
        if (names != null)
            foreach (var (kind, list) in names)
                foreach (var n in list) if (Match(n)) items.Add(new MissionHelp(n, kind, null, $"{kind} in this map"));
        foreach (var (name, forms) in HsDocs.Functions)
            if (Match(name)) items.Add(new MissionHelp(name, "function", forms[0].Signature, forms[0].Help));
        var sorted = items.GroupBy(i => i.Text, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(i => i.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(i => i.Kind == "function" ? 1 : 0).ThenBy(i => i.Text.Length).ThenBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
            .Take(250).ToList();
        return (start, sorted);
    }

    /// <summary>The word at a column of a line (names may hold one '/').</summary>
    public static (int Start, string Word) WordAt(string line, int column)
    {
        if (column < 0 || column > line.Length) return (column, "");
        int a = column, b = column;
        static bool In(char c) => char.IsLetterOrDigit(c) || c is '_' or '/';
        while (a > 0 && In(line[a - 1])) a--;
        while (b < line.Length && In(line[b])) b++;
        return (a, line[a..b].Trim('/'));
    }

    /// <summary>Help for the word at a column: a game function, a name in the map, a script or global, a keyword.</summary>
    public static MissionHelp? Hover(string line, int column, string python, IReadOnlyDictionary<string, List<string>>? names, IReadOnlyList<MissionDecl>? others)
    {
        var (_, word) = WordAt(line, column);
        if (word.Length == 0) return null;
        var forms = HsDocs.Find(word);
        if (forms != null)
            return new MissionHelp(word, "function", string.Join("\n", forms.Select(f => f.Signature).Distinct()), string.Join(" ", forms.Select(f => f.Help).Where(h => h.Length > 0).Distinct()));
        var decl = Declarations(python).Concat(others ?? Array.Empty<MissionDecl>()).FirstOrDefault(d => d.Name.Equals(word, StringComparison.OrdinalIgnoreCase));
        if (decl != null) return new MissionHelp(word, decl.Kind == "global" ? "global" : "script", null, DeclAbout(decl));
        if (names != null)
            foreach (var (kind, list) in names)
                if (list.Contains(word, StringComparer.OrdinalIgnoreCase))
                    return new MissionHelp(word, kind, null, $"A {kind} of this map. Ctrl+click to show it in the map view.");
        if (HsDocs.Globals.TryGetValue(word, out string? type)) return new MissionHelp(word, "engine global", null, $"An engine setting ({type}).");
        return KeywordHelp(word);
    }

    private static string DeclAbout(MissionDecl d) => d.Kind switch
    {
        "global" => $"A global ({d.Returns}) declared " + Where(d),
        "startup" => "A startup script: runs once when the mission starts. Declared " + Where(d),
        "dormant" => "A dormant script: runs once when woken (wake(name)). Declared " + Where(d),
        "continuous" => "A continuous script: runs again and again (every tick). Declared " + Where(d),
        "command_script" => "A command script: AI run it (cs_run_command_script(ai, name)). Declared " + Where(d),
        _ => $"A function script (def), {d.Params} value{(d.Params == 1 ? "" : "s")}" + (d.Returns is null or "void" ? "" : $", gives a {d.Returns}") + ". Declared " + Where(d),
    };

    private static string Where(MissionDecl d) => d.File != null ? $"in {Path.GetFileName(d.File)}, line {d.Line}." : $"on line {d.Line}.";

    private static MissionHelp? KeywordHelp(string word) => word switch
    {
        "startup" => new(word, "keyword", "startup name:", "A script that runs once when the mission starts."),
        "dormant" => new(word, "keyword", "dormant name:", "A script that waits until another one wakes it: wake(name)."),
        "continuous" => new(word, "keyword", "continuous name:", "A script that runs its lines again and again, every tick."),
        "command_script" => new(word, "keyword", "command_script name:", "Orders for AI: cs_run_command_script(squad, name) makes them run it."),
        "def" => new(word, "keyword", "def name(value: type) -> type:", "A script used like a function (HaloScript's static script). With -> type, its last line is return value."),
        "repeat" => new(word, "keyword", "repeat every 30:\n    ...\n    until condition", "Runs the lines inside again and again (every 30 ticks), until the until line is true."),
        "until" => new(word, "keyword", "until condition", "The last line of a repeat block: the loop stops when the condition is true."),
        "global" => new(word, "keyword", "global name: type = value", "A variable every script of the mission can read and change."),
        "if" or "elif" or "else" => new(word, "keyword", "if condition:\n    ...\nelif other:\n    ...\nelse:\n    ...", "Runs the lines below when the condition is true."),
        "return" => new(word, "keyword", "return value", "The value a def -> type script gives (its last line)."),
        "begin_random" => new(word, "keyword", "begin_random:", "Runs the lines inside in a random order."),
        "begin_random_count" => new(word, "keyword", "begin_random_count(1):", "Runs that many of the lines inside, picked at random."),
        "True" or "False" => new(word, "keyword", null, "A boolean value."),
        "None" => new(word, "keyword", null, "No object, no AI."),
        _ => null,
    };
}
