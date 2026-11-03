using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;

namespace Kalo.App.ViewModels;

/// <summary>
/// A campaign mission script (a .scenario_hs_source_file tag). Shown in Kalo Script (Python style) by default, or as
/// Bungie's HaloScript; saved as HaloScript, which the game compiles when the mission loads. Help while typing:
/// autocomplete and hover for every game function and every name in the map, checks (unknown functions, wrong number of
/// values, names not in the map), Ctrl+click on a name to see it in the map.
/// </summary>
public partial class MissionScriptViewModel : DocumentViewModel
{
    public const string Extension = ".scenario_hs_source_file";
    private readonly MainViewModel _main;
    private string _saved = "";
    private string _savedHaloScript = "";
    private bool _loading;

    public MissionScriptViewModel(MainViewModel main, string path, string? scenario = null) : base(path)
    {
        _main = main;
        ScenarioPath = scenario;
    }

    public MainViewModel Main => _main;
    public override string Title => System.IO.Path.GetFileName(Path)[..^Extension.Length] + (IsKaloScript ? ".ks" : ".hsc");
    public override string Badge => "mission";

    /// <summary>The scenario these scripts belong to (for the names of its squads, volumes, objects...).</summary>
    public string? ScenarioPath { get; set; }

    /// <summary>Shown in Kalo Script (Python style) rather than HaloScript.</summary>
    [ObservableProperty] private bool _isKaloScript = true;
    public string ViewLabel => IsKaloScript ? "Show HaloScript" : "Show Kalo Script";
    public string LanguageName => IsKaloScript ? "Kalo Script" : "HaloScript";
    partial void OnIsKaloScriptChanged(bool value)
    {
        OnPropertyChanged(nameof(ViewLabel));
        OnPropertyChanged(nameof(LanguageName));
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>The script file's own name (its "name" field): the game names it in its errors ([m10 line 12]).</summary>
    [ObservableProperty] private string _scriptName = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _checkOk;
    public ObservableCollection<ScriptMessage> Messages { get; } = new();
    public bool HasMessages => Messages.Count > 0;
    [ObservableProperty] private ScriptMessage? _selectedMessage;
    public ObservableCollection<HsDeclaration> Declarations { get; } = new();
    [ObservableProperty] private HsDeclaration? _selectedDeclaration;
    /// <summary>Kept in a map's own folder (levels\mods\...): editing it changes only that map.</summary>
    public bool IsShared => !Path.StartsWith(@"levels\mods\", StringComparison.OrdinalIgnoreCase);
    public string SharedWarning => "This script is shared (" + System.IO.Path.GetDirectoryName(Path) + "): changing it changes every mission that uses it. Copy a campaign mission into your mod (New map › Campaign) to get its own scripts.";

    /// <summary>The map's names by kind (squad, trigger volume...), and the scripts and globals of the mission's other files.</summary>
    public IReadOnlyDictionary<string, List<string>>? Names { get; private set; }
    public IReadOnlyList<Kalo.Core.Scripting.MissionDecl>? Others { get; private set; }

    /// <summary>For the saved HaloScript: its line i+1 came from Kalo Script line LineMap[i] (to place the game's errors).</summary>
    public IReadOnlyList<int>? LineMap { get; private set; }

    /// <summary>The checked messages, for the editor's underlines (and the text they were checked on).</summary>
    public IReadOnlyList<ScriptMessage> Diagnostics { get; private set; } = Array.Empty<ScriptMessage>();
    public string DiagnosticsText { get; private set; } = "";
    public event Action? DiagnosticsChanged;

    /// <summary>Asks the view to select a range: line, start offset, length.</summary>
    public event Action<int, int, int>? RevealRequested;

    partial void OnTextChanged(string value)
    {
        if (_loading) return;
        IsDirty = value != _saved;
        _checkTimer?.Dispose();
        _checkTimer = new Timer(_ => Avalonia.Threading.Dispatcher.UIThread.Post(Check), null, 400, Timeout.Infinite);
    }
    private Timer? _checkTimer;

    partial void OnSelectedMessageChanged(ScriptMessage? value)
    {
        if (value != null) RevealRequested?.Invoke(value.Line, value.Start, value.Length);
    }

    partial void OnSelectedDeclarationChanged(HsDeclaration? value)
    {
        if (value != null) RevealRequested?.Invoke(value.Line, value.Start, value.Name.Length);
    }

    /// <summary>Shows a line (1-based), e.g. from "Used by scripts".</summary>
    public void RevealLine(int line, string? word = null)
    {
        var lines = Text.Split('\n');
        if (line < 1 || line > lines.Length) return;
        int offset = lines.Take(line - 1).Sum(l => l.Length + 1);
        int at = word != null ? lines[line - 1].IndexOf(word, StringComparison.OrdinalIgnoreCase) : -1;
        RevealRequested?.Invoke(line, at >= 0 ? offset + at : offset, at >= 0 ? word!.Length : 0);
    }

    public async Task LoadAsync()
    {
        if (_main.Host == null) { Status = "The Engine is not running."; return; }
        try
        {
            var r = await _main.Host.ReadMissionScriptAsync(Path);
            _loading = true;
            ScriptName = r.Name ?? "";
            _savedHaloScript = r.Text ?? "";
            _saved = IsKaloScript ? Kalo.Core.Scripting.MissionScript.ToPython(_savedHaloScript) : _savedHaloScript;
            Text = _saved;
            _loading = false;
            IsDirty = false;
            if (IsKaloScript) LineMap = Kalo.Core.Scripting.MissionScript.ToHaloScript(Text).LineMap;
            Check();
            Status = $"{Text.Split('\n').Length:N0} lines";
            _ = LoadContextAsync();
        }
        catch (HostException ex)
        {
            _loading = false;
            Status = "Could not read the script: " + ex.Message;
            _main.AddProblem("error", Status, Path);
        }
    }

    /// <summary>The map's names and the mission's other scripts, for help and checks (in the background).</summary>
    public async Task LoadContextAsync()
    {
        ScenarioPath ??= _main.ScenarioOfMissionScript(Path);
        if (_main.Host == null || ScenarioPath == null) return;
        try
        {
            var r = await _main.Host.ScenarioNamesAsync(ScenarioPath);
            var names = new Dictionary<string, List<string>>(r.Names ?? new());
            var others = new List<Kalo.Core.Scripting.MissionDecl>();
            foreach (var script in r.Scripts ?? new())
            {
                if (script.Equals(Path, StringComparison.OrdinalIgnoreCase)) continue;
                others.AddRange(await _main.MissionDeclarationsAsync(script));
            }
            names["script"] = others.Where(d => d.Kind != "global").Select(d => d.Name).ToList();
            Names = names;
            Others = others;
            Check();
        }
        catch (HostException ex)
        {
            _main.Log($"{Title}: map names unavailable ({ex.Message}).");
        }
    }

    /// <summary>Switches between Kalo Script and HaloScript (the text is converted; nothing is saved).</summary>
    [RelayCommand]
    private void SwitchView()
    {
        if (IsKaloScript)
        {
            var r = Kalo.Core.Scripting.MissionScript.ToHaloScript(Text);
            if (!r.Ok)
            {
                Check();
                Status = $"Fix the errors first (line {r.Errors[0].Line}: {r.Errors[0].Message}).";
                return;
            }
            _loading = true;
            Text = IsDirty ? r.HaloScript : _savedHaloScript;
            _saved = _savedHaloScript;
            IsKaloScript = false;
            _loading = false;
        }
        else
        {
            _loading = true;
            string py = Kalo.Core.Scripting.MissionScript.ToPython(Text);
            _saved = Kalo.Core.Scripting.MissionScript.ToPython(_savedHaloScript);
            Text = IsDirty ? py : _saved;
            IsKaloScript = true;
            _loading = false;
        }
        IsDirty = Text != _saved;
        Check();
    }

    [RelayCommand]
    private async Task Save()
    {
        if (_main.Host == null) return;
        Check();
        var error = Messages.FirstOrDefault(m => m.Severity == "error" && !m.Message.StartsWith("Game: ", StringComparison.Ordinal));
        if (error != null)
        {
            _main.AddProblem("error", $"{Title} not saved: line {error.Line}: {error.Message}", Path);
            SelectedMessage = error;
            return;
        }
        try
        {
            string text = Text;
            string haloScript = text;
            if (IsKaloScript)
            {
                var r = Kalo.Core.Scripting.MissionScript.ToHaloScript(text);
                haloScript = r.HaloScript;
                LineMap = r.LineMap;
            }
            var w = await _main.Host.WriteMissionScriptAsync(Path, haloScript);
            _saved = text;
            _savedHaloScript = haloScript;
            IsDirty = false;
            Status = $"Saved in {w.Ms} ms. The game compiles it when the mission loads: press Play; its errors show in Problems.";
            _gameErrors.Clear();
            _main.OnDocumentSaved(this);
            _main.ForgetMissionDeclarations(Path);
        }
        catch (HostException ex)
        {
            _main.AddProblem("error", "Save failed: " + ex.Message, Path);
        }
    }

    public override Task SaveDocumentAsync() => SaveCommand.ExecuteAsync(null);

    public override async Task CloseAsync()
    {
        try { if (_main.Host != null) await _main.Host.CloseTagAsync(Path); } catch { }
    }

    [RelayCommand]
    private void CheckNow() => Check();

    /// <summary>Kalo Script: syntax, functions, values, names; HaloScript: brackets and strings. And the declarations list.</summary>
    public void Check()
    {
        Messages.Clear();
        Declarations.Clear();
        if (IsKaloScript)
        {
            foreach (var m in Kalo.Core.Scripting.MissionAssist.Check(Text, Names, Others).Take(200))
                Messages.Add(new ScriptMessage(m.Line, -1, m.Length, m.IsError ? "error" : "warning", m.Text, null, m.Column));
            var starts = LineStarts(Text);
            foreach (var d in Kalo.Core.Scripting.MissionAssist.Declarations(Text))
                Declarations.Add(new HsDeclaration(d.Kind, d.Name, d.Line, starts[d.Line - 1] + d.Column - 1));
        }
        else
        {
            foreach (var m in HaloScriptCheck.Run(Text)) Messages.Add(m);
            foreach (var d in HaloScriptCheck.Declarations(Text)) Declarations.Add(d);
        }
        foreach (var g in _gameErrors) Messages.Add(g);
        int errors = Messages.Count(m => m.Severity == "error"), warnings = Messages.Count - errors;
        CheckOk = errors == 0;
        Summary = errors == 0
            ? $"{Declarations.Count(d => d.Kind != "global")} scripts, {Declarations.Count(d => d.Kind == "global")} globals · " +
              (IsKaloScript ? (warnings == 0 ? "no problems found" : $"{warnings} warning{(warnings == 1 ? "" : "s")}") : "brackets and strings OK")
            : $"{errors} problem{(errors == 1 ? "" : "s")}: {Messages.First(m => m.IsError).Message} (line {Messages.First(m => m.IsError).Line})";
        OnPropertyChanged(nameof(HasMessages));
        Diagnostics = Messages.ToList();
        DiagnosticsText = Text;
        DiagnosticsChanged?.Invoke();
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }

    // ---- help while typing ----

    public (int Start, List<CompletionItem> Items) Complete(string lineBefore)
    {
        if (!IsKaloScript) return (lineBefore.Length, new());
        var (start, items) = Kalo.Core.Scripting.MissionAssist.Complete(lineBefore, Text, Names, Others);
        return (start, items.Select(i => new CompletionItem(i.Text, i.Kind, i.Signature != null ? i.Signature + (i.About is { Length: > 0 } ? "\n\n" + i.About : "") : i.About)).ToList());
    }

    public HoverInfo? Hover(string line, int column)
    {
        var h = Kalo.Core.Scripting.MissionAssist.Hover(line, column, Text, Names, Others);
        if (h == null) return null;
        return new HoverInfo(h.Kind == "function" ? h.Text : $"{h.Text}  ({h.Kind})", h.Signature, null, h.About ?? "", null);
    }

    /// <summary>Ctrl+click: a name of the map (squad, volume, object, flag...) shown in the map view.</summary>
    public async Task<bool> GoToNameAsync(string word)
    {
        if (ScenarioPath == null || Names == null) return false;
        foreach (var (kind, list) in Names)
            if (kind != "script" && list.Contains(word, StringComparer.OrdinalIgnoreCase))
                return await _main.ShowNameInMapAsync(ScenarioPath, word, kind);
        var decl = Kalo.Core.Scripting.MissionAssist.Declarations(Text).FirstOrDefault(d => d.Name.Equals(word, StringComparison.OrdinalIgnoreCase));
        if (decl != null) { RevealLine(decl.Line, decl.Name); return true; }
        var other = Others?.FirstOrDefault(d => d.Name.Equals(word, StringComparison.OrdinalIgnoreCase) && d.File != null);
        if (other?.File != null)
        {
            var doc = await _main.OpenMissionScriptAsync(other.File, ScenarioPath);
            doc.RevealLine(doc.FindDeclarationLine(other.Name), other.Name);
            return true;
        }
        return false;
    }

    /// <summary>The line declaring a script or global in this file's current view.</summary>
    public int FindDeclarationLine(string name)
    {
        var d = IsKaloScript ? Kalo.Core.Scripting.MissionAssist.Declarations(Text).FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            : Kalo.Core.Scripting.MissionAssist.HaloScriptDeclarations(Text).FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return d?.Line ?? 1;
    }

    /// <summary>A line of the game's HaloScript (from a compile error) in this view's numbering.</summary>
    public int ViewLine(int haloScriptLine)
    {
        if (!IsKaloScript || LineMap == null || haloScriptLine < 1 || haloScriptLine > LineMap.Count) return haloScriptLine;
        // Closing brackets have no line of their own in Kalo Script: the nearest line above.
        for (int k = haloScriptLine - 1; k >= 0; k--) if (LineMap[k] > 0) return LineMap[k];
        return 1;
    }

    private readonly List<ScriptMessage> _gameErrors = new();

    /// <summary>An error the game reported when compiling this script (kept until the next save).</summary>
    public void AddGameError(int line, string message)
    {
        _gameErrors.Add(new ScriptMessage(line, -1, 0, "error", "Game: " + message, null, 0));
        Check();
    }
}

/// <summary>A script or global declared in a mission script, for the "Go to" list.</summary>
public sealed record HsDeclaration(string Kind, string Name, int Line, int Start)
{
    public string Display => Kind == "global" ? $"global {Name}" : $"{Name}  ({Kind})";
    public override string ToString() => Display;
}

/// <summary>Light checks of HaloScript text: unbalanced brackets, unterminated strings; and its declarations.</summary>
public static class HaloScriptCheck
{
    public static List<ScriptMessage> Run(string text)
    {
        var list = new List<ScriptMessage>();
        var open = new Stack<(int Line, int Offset)>();
        int line = 1;
        int lineStart = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n') { line++; lineStart = i + 1; continue; }
            if (c == ';')
            {
                // A ;* ... *; block comment, or a comment to the end of the line.
                if (i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*;", i + 2, StringComparison.Ordinal);
                    int stop = end < 0 ? text.Length : end + 2;
                    for (int k = i; k < stop; k++) if (text[k] == '\n') { line++; lineStart = k + 1; }
                    i = stop - 1;
                    continue;
                }
                while (i < text.Length && text[i] != '\n') i++;
                i--;
                continue;
            }
            if (c == '"')
            {
                int start = i, startLine = line;
                i++;
                while (i < text.Length && text[i] != '"' && text[i] != '\n') i++;
                if (i >= text.Length || text[i] == '\n')
                {
                    list.Add(new ScriptMessage(startLine, start, 1, "error", "this string has no closing \" on its line", null, start - lineStart + 1));
                    if (i < text.Length) { line++; lineStart = i + 1; }
                }
                continue;
            }
            if (c == '(') open.Push((line, i));
            else if (c == ')')
            {
                if (open.Count == 0) list.Add(new ScriptMessage(line, i, 1, "error", "this ) closes nothing (one ( too few above it)", null, i - lineStart + 1));
                else open.Pop();
            }
        }
        foreach (var (l, offset) in open.Reverse().Take(5))
            list.Add(new ScriptMessage(l, offset, 1, "error", "this ( is never closed (one ) missing)", null, 0));
        return list.OrderBy(m => m.Line).Take(50).ToList();
    }

    private static readonly Regex Declaration = new(
        @"\(\s*(script\s+(?<kind>startup|dormant|continuous|static|stub|command_script)\s+(?:(?<type>[a-z_]+)\s+)?(?<name>[A-Za-z0-9_]+)|(?<kind>global)\s+[a-z_]+\s+(?<name>[A-Za-z0-9_]+))",
        RegexOptions.Compiled);

    public static List<HsDeclaration> Declarations(string text)
    {
        var list = new List<HsDeclaration>();
        // Line starts, to turn offsets into line numbers.
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        foreach (Match m in Declaration.Matches(text))
        {
            // Skip matches inside comments (a ; earlier on the same line).
            int lineIndex = starts.BinarySearch(m.Index);
            if (lineIndex < 0) lineIndex = ~lineIndex - 1;
            int ls = starts[lineIndex];
            if (text.IndexOf(';', ls, m.Index - ls) >= 0) continue;
            var name = m.Groups["name"];
            string kind = m.Groups["kind"].Value;
            // "(script static void name": the optional return type was taken as the name when there is none.
            list.Add(new HsDeclaration(kind, name.Value, lineIndex + 1, name.Index));
        }
        return list;
    }
}
