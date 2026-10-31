using System.Text.RegularExpressions;

namespace Kalo.Core.Scripting;

/// <summary>
/// Autocomplete that knows where the caret is in Kalo Script: inside a call's parentheses it offers what that
/// argument takes (a player, an object type, a sound...), not every action and keyword.
/// </summary>
public static class KaloCompletion
{
    /// <summary>The call the caret is in: its name, which argument (0-based), the arguments already written, and the text of the current one.</summary>
    public sealed record CallSite(string Name, int Index, IReadOnlyList<string> Before, string Typed);

    /// <summary>One suggestion: the text to insert, what it is, and a short note.</summary>
    public sealed record Suggestion(string Text, string Kind, string? Note);

    /// <summary>What an argument expects, from the compiler's argument list.</summary>
    public sealed record Slot(string Placeholder, string? Keyword, bool Optional, string Kind, string Description);

    /// <summary>Finds the innermost unclosed call on the line before the caret (strings and -- comments skipped).</summary>
    public static CallSite? CallAt(string lineBeforeCaret)
    {
        var stack = new Stack<(string Name, int OpenAt, List<int> Commas)>();
        bool inString = false;
        string s = lineBeforeCaret;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '-' && i + 1 < s.Length && s[i + 1] == '-') return null; // in a comment
            if (c == '(')
            {
                int e = i;
                while (e > 0 && s[e - 1] == ' ') e--;
                int b = e;
                while (b > 0 && (char.IsLetterOrDigit(s[b - 1]) || s[b - 1] == '_')) b--;
                stack.Push((s[b..e], i, new List<int>()));
            }
            else if (c == ')') { if (stack.Count > 0) stack.Pop(); }
            else if (c == ',' && stack.Count > 0) stack.Peek().Commas.Add(i);
        }
        if (inString || stack.Count == 0) return null;
        var (name, open, commas) = stack.Peek();
        if (name.Length == 0) return null;
        var bounds = new List<int> { open };
        bounds.AddRange(commas);
        var before = new List<string>();
        for (int k = 0; k + 1 < bounds.Count; k++) before.Add(s[(bounds[k] + 1)..bounds[k + 1]].Trim());
        string typed = s[(bounds[^1] + 1)..].TrimStart();
        return new CallSite(name, commas.Count, before, typed);
    }

    /// <summary>The compiler's argument list split into Kalo Script arguments ("at &lt;place_at_object&gt;" is one).</summary>
    public static List<Slot> Slots(string? megaloSignature)
    {
        var slots = new List<Slot>();
        foreach (string arg in MegaloDocs.KaloArguments(megaloSignature))
        {
            string a = arg.Trim();
            bool optional = a.StartsWith('[') && a.EndsWith(']');
            if (optional) a = a[1..^1].Trim();
            string? keyword = null;
            var m = Regex.Match(a, @"^([a-z_]+)\s+(.*)$");
            if (m.Success && !a.StartsWith('<') && !a.StartsWith('{')) { keyword = m.Groups[1].Value; a = m.Groups[2].Value; }
            else if (Regex.IsMatch(a, @"^[a-z_]+$")) { keyword = a; a = ""; } // a flag such as [never_garbage]
            string kind = KindOf(a);
            slots.Add(new Slot(a, keyword, optional, kind, arg.Trim()));
        }
        return slots;
    }

    /// <summary>
    /// The slots the current argument can be: the next one, plus the ones after it while those are optional
    /// (play_sound(everyone, [immediate], sound): after "everyone," both "immediate" and a sound fit).
    /// </summary>
    public static List<Slot> Candidates(List<Slot> slots, CallSite call)
    {
        int s = 0;
        foreach (string done in call.Before)
        {
            // Skip optional slots the written argument doesn't use.
            while (s < slots.Count && slots[s].Optional && slots[s].Keyword != null && !StartsWithWord(done, slots[s].Keyword!)) s++;
            if (s < slots.Count) s++;
        }
        var result = new List<Slot>();
        for (int i = s; i < slots.Count; i++)
        {
            result.Add(slots[i]);
            if (!slots[i].Optional) break;
        }
        // Optional keyword arguments of create_object & co. can come in any order once the required ones are written.
        if (result.Count > 0 && result.All(r => r.Optional))
            foreach (var later in slots.Skip(s).Where(x => x.Optional && !result.Contains(x) && !call.Before.Any(b => x.Keyword != null && StartsWithWord(b, x.Keyword))))
                result.Add(later);
        return result;
    }

    private static bool StartsWithWord(string text, string word) =>
        text.StartsWith(word, StringComparison.Ordinal) && (text.Length == word.Length || text[word.Length] == ' ');

    /// <summary>What kind of value a placeholder such as "&lt;player&gt;" or "&lt;object_type&gt;" takes.</summary>
    public static string KindOf(string placeholder)
    {
        string p = placeholder.Trim();
        if (p.StartsWith('{')) return "choice";
        p = Regex.Replace(p, @"\(.*?\)", "").Trim().Trim('<', '>').Trim();
        p = Regex.Replace(p, @"\s*\(.*$", "");
        bool output = p.EndsWith("_out") || p.EndsWith(" out") || p.Contains("_out ");
        string b = p.Replace("_out", "").Replace(" out", "").Trim();
        string kind = b switch
        {
            "player" or "dead_player" or "killing_player" or "victim_player" or "assisting_player" or "player_reference" or "player variable name" => "player",
            "object" or "object_a" or "object_b" or "place_at_object" or "parent_object" or "child_object" or "boundary_object" or "test_object"
                or "ignore object" or "weapon object" or "object_reference" or "unit" or "biped" or "vehicle" or "weapon" or "equipment" or "target" => "object",
            "team" or "team_a" or "team_b" => "team",
            "team_or_player" or "team_or_player_target" or "cause_team_or_player" or "effect_team_or_player" => "target",
            "timer" => "timer",
            "timer_name" => "timer_member",
            "object_type" => "objectTypes",
            "button" => "buttons",
            "sound" => "sounds",
            "incident_name" => "incidents",
            "loadout" => "loadouts",
            "req_palette_name" => "loadoutPalettes",
            "icon name" => "hudWidgetIcons",
            "player_traits_name" => "traits",
            "hud_widget_name" => "widgets",
            "player_death_type" => "death",
            "boolean" or "literal_boolean" or "should hide" or "boolean active" => "boolean",
            "dynamic_string" => "string",
            "filter name" or "filter_name" or "label" => "label",
            _ when b.StartsWith("number") || b.StartsWith("value") || b is "var_a" or "var_b" or "x" or "y" or "z" or "radius" or "rate"
                || b.StartsWith("offset") || b.EndsWith("_seconds") || b.StartsWith("milliseconds") || b.StartsWith("distance") || b.StartsWith("vitality")
                || b.StartsWith("orientation") || b.StartsWith("penalty") || b.StartsWith("custom_value") || b.StartsWith("splitscreen")
                || b.StartsWith("interpolat") || b.StartsWith("animation") || b.StartsWith("acceleration") || b.StartsWith("deceleration") => "number",
            _ => "any",
        };
        return output && kind is "player" or "object" or "team" or "number" or "timer" ? kind + "_out" : kind;
    }

    /// <summary>Plain-language name of a kind (shown in the suggestion list).</summary>
    public static string Describe(string kind) => kind switch
    {
        "player" => "a player",
        "player_out" => "a player variable to store the result in",
        "object" => "an object (a player means their body)",
        "object_out" => "an object variable to store the result in",
        "team" => "a team",
        "team_out" => "a team variable to store the result in",
        "target" => "who: a team, or player <name>",
        "number" => "a number (a value, an option, a variable)",
        "number_out" => "a number variable to store the result in",
        "timer" => "a timer variable",
        "timer_member" => "the name of a timer of that object",
        "objectTypes" => "an object type",
        "buttons" => "a button (no quotes)",
        "sounds" => "a sound",
        "incidents" => "an incident (medal, announcer event)",
        "loadouts" => "a loadout",
        "loadoutPalettes" => "a loadout palette",
        "hudWidgetIcons" => "a HUD icon",
        "traits" => "player traits declared with traits",
        "widgets" => "a HUD widget declared with widget",
        "death" => "how the player died",
        "boolean" => "true or false",
        "string" => "a text in quotes",
        "label" => "a Megalo label (set on map objects in Forge or the map editor)",
        "choice" => "one of these words",
        _ => "a value",
    };

    /// <summary>The names a script declares, by kind: number, player, object, team, timer (globals and temps), members, traits, widgets, labels.</summary>
    public sealed class ScriptNames
    {
        public Dictionary<string, SortedSet<string>> ByKind { get; } = new();
        /// <summary>Per-player / team / object variables: (scope, type, name).</summary>
        public List<(string Scope, string Type, string Name)> Members { get; } = new();
        /// <summary>enum variables → their value names.</summary>
        public Dictionary<string, List<string>> Enums { get; } = new(StringComparer.Ordinal);

        public void Add(string kind, string name)
        {
            if (!ByKind.TryGetValue(kind, out var set)) ByKind[kind] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(name);
        }

        public IEnumerable<string> Of(string kind) => ByKind.TryGetValue(kind, out var set) ? set : Enumerable.Empty<string>();
    }

    private static readonly Regex Declare = new(@"^\s*declare\s+(?:(?:networked\w*|local)\s+)*(global|player|team|object)\.(number|player|object|team|timer)\s+(\w+)", RegexOptions.Multiline);
    private static readonly Regex DeclareEnum = new(@"^\s*declare\s+(?:(?:networked\w*|local)\s+)*(global|player|team|object)\.enum\s*\{([^}]*)\}\s*(\w+)", RegexOptions.Multiline);
    private static readonly Regex Temp = new(@"\btemp\s+(number|player|object|team|timer)\s+(\w+)");
    private static readonly Regex Named = new(@"^\s*(?:hidden\s+|locked\s+)*(option|constant|traits|widget|objects|alias)\s+(\w+)", RegexOptions.Multiline);
    private static readonly Regex Label = new(@"\blabel\s+""([^""]+)""");

    public static ScriptNames Scan(string script)
    {
        var n = new ScriptNames();
        foreach (Match m in Declare.Matches(script))
        {
            string scope = m.Groups[1].Value, type = m.Groups[2].Value, name = m.Groups[3].Value;
            if (scope == "global") n.Add(type, name);
            else n.Members.Add((scope, type, name));
        }
        foreach (Match m in DeclareEnum.Matches(script))
        {
            string scope = m.Groups[1].Value, name = m.Groups[3].Value;
            n.Enums[name] = m.Groups[2].Value.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
            if (scope == "global") n.Add("number", name);
            else n.Members.Add((scope, "number", name));
        }
        foreach (Match m in Temp.Matches(script)) n.Add(m.Groups[1].Value, m.Groups[2].Value);
        foreach (Match m in Named.Matches(script))
        {
            string word = m.Groups[1].Value, name = m.Groups[2].Value;
            switch (word)
            {
                case "option" or "constant": n.Add("fixed_number", name); break; // read only
                case "traits": n.Add("traits", name); break;
                case "widget": n.Add("widgets", name); break;
                case "alias": n.Add("any", name); break;
            }
        }
        foreach (Match m in Label.Matches(script)) n.Add("label", "\"" + m.Groups[1].Value + "\"");
        return n;
    }

    /// <summary>
    /// After "x." (x a player, team or object): its variables and Megalo's own members. Null when x is unknown.
    /// </summary>
    public static List<Suggestion>? Members(string baseExpr, string script)
    {
        var names = Scan(script);
        string? scope = baseExpr switch
        {
            "current_player" => "player",
            "current_team" => "team",
            "current_object" => "object",
            _ => names.ByKind.FirstOrDefault(k => k.Key is "player" or "team" or "object" && k.Value.Contains(baseExpr)).Key,
        };
        if (scope == null && baseExpr.EndsWith(".team", StringComparison.Ordinal)) scope = "team";
        if (scope == null) return null;
        var list = names.Members.Where(m => m.Scope == scope)
            .Select(m => new Suggestion(m.Name, m.Type, names.Enums.TryGetValue(m.Name, out var e) ? "enum: " + string.Join(", ", e) : $"{scope}.{m.Type} declared in this script"))
            .ToList();
        if (scope == "player") { list.Add(new Suggestion("score", "number", "the player's score")); list.Add(new Suggestion("team", "team", "the player's team")); }
        if (scope == "team") list.Add(new Suggestion("score", "number", "the team's score"));
        if (scope == "object") list.Add(new Suggestion("team", "team", "the object's team"));
        return list;
    }

    /// <summary>After "x == " or "x = " where x is an enum variable: its value names.</summary>
    public static List<Suggestion>? EnumValues(string lineBeforeCaret, string script)
    {
        var m = Regex.Match(lineBeforeCaret, @"([\w.]+)\s*(==|!=|=)\s*\w*$");
        if (!m.Success) return null;
        string var = m.Groups[1].Value[(m.Groups[1].Value.LastIndexOf('.') + 1)..];
        return Scan(script).Enums.TryGetValue(var, out var values)
            ? values.Select(v => new Suggestion(v, "enum value", $"{var} = {v} (stored as {values.IndexOf(v)})")).ToList()
            : null;
    }

    /// <summary>
    /// Suggestions for the argument at the caret. <paramref name="lists"/> are the compiler's value lists (sounds,
    /// objectTypes...). Null when the caret is not in a known call.
    /// </summary>
    public static (CallSite Call, List<Slot> Slots, List<Suggestion> Items)? Suggest(string lineBeforeCaret, string script,
        Func<string, string?> signatureOf, IReadOnlyDictionary<string, List<string>>? lists)
    {
        var call = CallAt(lineBeforeCaret);
        if (call == null) return null;
        string? signature = signatureOf(call.Name);
        if (signature == null) return null;
        var slots = Slots(signature);
        var candidates = Candidates(slots, call);
        var names = Scan(script);
        var items = new List<Suggestion>();
        string typed = call.Typed;
        // "set ", "at ", "offset ": the argument's keyword is written, so only that argument's values fit.
        var chosen = candidates.FirstOrDefault(c => c.Keyword != null && c.Placeholder.Length > 0 && typed.StartsWith(c.Keyword + " ", StringComparison.Ordinal));
        if (chosen != null) candidates = new List<Slot> { chosen };
        foreach (var slot in candidates)
        {
            // "at current_player": once the keyword is written, the value follows it.
            bool keywordWritten = slot.Keyword != null && StartsWithWord(typed, slot.Keyword);
            string lead = slot.Keyword != null && !keywordWritten ? slot.Keyword + " " : "";
            if (slot.Keyword != null && slot.Placeholder.Length == 0)
            {
                if (!keywordWritten) items.Add(new Suggestion(slot.Keyword, "option", "optional: " + slot.Description));
                continue;
            }
            string kind = slot.Kind;
            string note = Describe(kind) + "  ·  " + slot.Description;
            int placeholders = Regex.Matches(slot.Placeholder, "<").Count;
            if (slot.Keyword != null && !keywordWritten && (kind == "any" || placeholders > 1))
            {
                // offset <x> <y> <z>, variant <name>: the keyword, with zeros for numbers.
                string filled = kind == "number" || placeholders > 1 ? " " + string.Join(" ", Enumerable.Repeat("0", placeholders)) : " ";
                items.Add(new Suggestion((slot.Keyword + filled).TrimEnd(), "option", (slot.Optional ? "optional: " : "") + slot.Description));
                continue;
            }
            int before = items.Count;
            foreach (string v in Values(kind, slot.Placeholder, typed, names, lists))
                items.Add(new Suggestion(lead + v, kind is "choice" or "boolean" or "death" ? "value" : KindLabel(kind), note));
            if (items.Count == before && slot.Keyword != null && !keywordWritten)
                items.Add(new Suggestion(slot.Keyword + (kind is "label" or "string" ? " \"\"" : ""), "option", (slot.Optional ? "optional: " : "") + slot.Description));
        }
        // Several words already typed ("player k"): suggestions continue them, so only the last word is replaced.
        int space = typed.LastIndexOf(' ');
        if (space >= 0)
        {
            string head = typed[..(space + 1)];
            items = items.Where(i => i.Text.StartsWith(head, StringComparison.Ordinal)).Select(i => i with { Text = i.Text[head.Length..] }).ToList();
        }
        return (call, candidates, items.Where(i => i.Text.Length > 0).GroupBy(i => i.Text).Select(g => g.First()).ToList());
    }

    private static string KindLabel(string kind) => kind.Replace("_out", "").Replace("_member", "") switch
    {
        "objectTypes" => "object type",
        "buttons" => "button",
        "sounds" => "sound",
        "incidents" => "incident",
        "loadouts" => "loadout",
        "loadoutPalettes" => "palette",
        "hudWidgetIcons" => "icon",
        "widgets" => "widget",
        var k => k,
    };

    private static IEnumerable<string> Values(string kind, string placeholder, string typed, ScriptNames names, IReadOnlyDictionary<string, List<string>>? lists)
    {
        // After "x." only members make sense.
        int dot = typed.LastIndexOf('.');
        if (dot >= 0)
        {
            string type = kind.Replace("_out", "");
            foreach (var (_, t, name) in names.Members)
                if (type is "any" or "label" or "string" || t == type || (type == "object" && t == "player") || (type == "timer_member" && t == "timer")) yield return name;
            if (type is "number" or "any") yield return "score";
            if (type is "team" or "any" or "target") yield return "team";
            yield break;
        }
        List<string>? List(string key) => lists != null && lists.TryGetValue(key, out var l) ? l : null;
        switch (kind)
        {
            case "choice":
                foreach (string v in placeholder.Trim('{', '}').Split('|'))
                {
                    string w = v.Trim();
                    // "player <p> <boolean>" (navpoint_set_visible, boundary_set_visible...): who, then 1 = yes / 0 = no.
                    if (w.StartsWith("player ")) { foreach (string p in Players(names)) yield return w.Contains("<boolean>") ? $"player, {p}, 1" : "player " + p; }
                    else if (w.Length > 0 && !w.StartsWith('<')) yield return w;
                }
                yield break;
            case "boolean": yield return "true"; yield return "false"; yield break;
            case "death": foreach (string d in new[] { "enemy", "suicide", "betrayal", "any" }) yield return d; yield break;
            case "player": foreach (string p in Players(names)) yield return p; yield break;
            case "player_out": foreach (string p in names.Of("player")) yield return p; yield break;
            case "object":
                yield return "current_object";
                foreach (string o in names.Of("object")) yield return o;
                foreach (string p in Players(names)) yield return p;
                yield return "none";
                yield break;
            case "object_out": foreach (string o in names.Of("object")) yield return o; yield break;
            case "team":
                yield return "current_team";
                foreach (string t in names.Of("team")) yield return t;
                yield return "current_player.team";
                yield return "neutral";
                yield break;
            case "team_out": foreach (string t in names.Of("team")) yield return t; yield break;
            case "target":
                foreach (string p in Players(names)) yield return "player " + p;
                yield return "current_team";
                foreach (string t in names.Of("team")) yield return t;
                yield return "everyone";
                yield return "no_one";
                yield break;
            case "number":
                foreach (string v in names.Of("number")) yield return v;
                foreach (string v in names.Of("fixed_number")) yield return v;
                foreach (var (scope, t, name) in names.Members) if (t == "number") yield return $"current_{scope}.{name}";
                yield return "current_player.score";
                yield break;
            case "number_out":
                foreach (string v in names.Of("number")) yield return v;
                foreach (var (scope, t, name) in names.Members) if (t == "number") yield return $"current_{scope}.{name}";
                yield break;
            case "timer" or "timer_out":
                foreach (string v in names.Of("timer")) yield return v;
                foreach (var (scope, t, name) in names.Members) if (t == "timer") yield return $"current_{scope}.{name}";
                yield break;
            case "timer_member":
                foreach (var (_, t, name) in names.Members) if (t == "timer") yield return name;
                yield break;
            case "traits" or "widgets" or "label":
                foreach (string v in names.Of(kind)) yield return v;
                yield break;
            case "string":
                yield return "\"\"";
                yield break;
            case "buttons":
                foreach (string v in MegaloDocs.Buttons) yield return v;
                yield break;
            case "objectTypes" or "sounds" or "incidents" or "loadouts" or "loadoutPalettes" or "hudWidgetIcons":
                foreach (string v in List(kind) ?? new List<string>()) yield return v;
                yield break;
            default:
                foreach (var set in names.ByKind.Where(k => k.Key is "number" or "player" or "object" or "team" or "timer" or "any")) foreach (string v in set.Value) yield return v;
                yield return "current_player";
                yield return "current_object";
                yield break;
        }
    }

    private static IEnumerable<string> Players(ScriptNames names)
    {
        yield return "current_player";
        foreach (string p in names.Of("player")) yield return p;
    }
}
