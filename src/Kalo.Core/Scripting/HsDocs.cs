using System.Reflection;
using System.Text.RegularExpressions;

namespace Kalo.Core.Scripting;

/// <summary>One form of a HaloScript function: what it returns, its parameter types, its help text.</summary>
public sealed record HsFunction(string Name, string Returns, IReadOnlyList<HsParam> Params, string Help)
{
    public int Min => Params.Count(p => !p.Optional && !p.Repeated) + Params.Count(p => p.Repeated && !p.Optional);
    public int Max => Params.Any(p => p.Repeated) ? int.MaxValue : Params.Count;

    /// <summary>ai_place(ai, short) → void, in Kalo Script form.</summary>
    public string Signature => $"{Name}({string.Join(", ", Params.Select(p => p.Display))})" + (Returns is "void" or "passthrough" ? "" : " → " + Returns);
}

public sealed record HsParam(string Type, bool Optional, bool Repeated)
{
    public string Display => (Optional ? "[" : "") + Type + (Repeated ? "..." : "") + (Optional ? "]" : "");
}

/// <summary>
/// Every HaloScript function and engine global, read from the game's own list (hs_doc.txt, written by its
/// script_doc command; Kalo ships a copy): for autocomplete, hover help and checks in mission scripts.
/// </summary>
public static class HsDocs
{
    private static IReadOnlyDictionary<string, IReadOnlyList<HsFunction>>? _functions;
    private static IReadOnlyDictionary<string, string>? _globals;

    public static IReadOnlyDictionary<string, IReadOnlyList<HsFunction>> Functions { get { Load(); return _functions!; } }
    public static IReadOnlyDictionary<string, string> Globals { get { Load(); return _globals!; } }

    public static IReadOnlyList<HsFunction>? Find(string name) => Functions.TryGetValue(name, out var f) ? f : null;

    private static readonly Regex Entry = new(@"^\(<(?<ret>[^>]+)>\s+(?<name>\S+)(?<args>.*)\)\s*$", RegexOptions.Compiled);
    private static readonly Regex Arg = new(@"\[[^\]]*\]|<[^>]+>(\(s\))?|\([^)]*\)", RegexOptions.Compiled);

    private static void Load()
    {
        if (_functions != null) return;
        string text = "";
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Kalo.HsDoc"))
            if (s != null) text = new StreamReader(s).ReadToEnd();
        Parse(text, out var functions, out var globals);
        _functions = functions;
        _globals = globals;
    }

    public static void Parse(string text, out IReadOnlyDictionary<string, IReadOnlyList<HsFunction>> functions, out IReadOnlyDictionary<string, string> globals)
    {
        var fs = new Dictionary<string, List<HsFunction>>(StringComparer.OrdinalIgnoreCase);
        var gs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        bool inGlobals = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith("; AVAILABLE EXTERNAL GLOBALS", StringComparison.Ordinal)) { inGlobals = true; continue; }
            var m = Entry.Match(line);
            if (!m.Success) continue;
            string name = m.Groups["name"].Value, ret = m.Groups["ret"].Value;
            if (inGlobals) { gs[name] = ret; continue; }
            var ps = new List<HsParam>();
            foreach (Match a in Arg.Matches(m.Groups["args"].Value))
            {
                string v = a.Value;
                bool optional = v.StartsWith('[');
                string inner = optional ? v.Trim('[', ']').Trim() : v;
                // [<script>], <number(s)>, [(<boolean2> <result2>) [...]]
                if (inner.StartsWith("...") || inner.Length == 0) continue;
                bool repeated = inner.Contains("(s)") || inner.Contains("...");
                var t = Regex.Match(inner, @"<([^>]+)>");
                string type = t.Success ? t.Groups[1].Value : inner.Trim('(', ')');
                ps.Add(new HsParam(type, optional, repeated));
            }
            var help = new List<string>();
            for (int k = i + 1; k < lines.Length && lines[k].TrimStart().StartsWith(';'); k++) help.Add(lines[k].Trim().TrimStart(';').Trim());
            if (!fs.TryGetValue(name, out var list)) fs[name] = list = new();
            list.Add(new HsFunction(name, ret, ps, string.Join(" ", help)));
        }
        // The list says sleep_until takes 2 values; it also takes a timeout (Bungie's scripts use 3).
        foreach (var n in new[] { "sleep_until", "sleep_until_game_ticks" })
            if (fs.TryGetValue(n, out var su))
                for (int k = 0; k < su.Count; k++)
                    if (su[k].Params.Count == 2) su[k] = su[k] with { Params = su[k].Params.Append(new HsParam("short", true, false)).ToList() };
        functions = fs.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<HsFunction>)kv.Value, StringComparer.OrdinalIgnoreCase);
        globals = gs;
    }
}
