using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;

namespace Kalo.App.ViewModels;

/// <summary>
/// A campaign mission script (HaloScript, a .scenario_hs_source_file tag): its text in an editor, a list of the scripts
/// and globals it declares, and a check of its brackets and strings before saving. The game compiles the scripts when
/// the mission loads (Play); its compile errors show in Output.
/// </summary>
public partial class MissionScriptViewModel : DocumentViewModel
{
    public const string Extension = ".scenario_hs_source_file";
    private readonly MainViewModel _main;
    private string _saved = "";
    private bool _loading;

    public MissionScriptViewModel(MainViewModel main, string path) : base(path) => _main = main;

    public override string Title => System.IO.Path.GetFileName(Path)[..^Extension.Length] + ".hsc";
    public override string Badge => "mission";

    /// <summary>The script file's own name (its "name" field).</summary>
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

    public async Task LoadAsync()
    {
        if (_main.Host == null) { Status = "The Blam Host is not running."; return; }
        try
        {
            var r = await _main.Host.ReadMissionScriptAsync(Path);
            _loading = true;
            ScriptName = r.Name ?? "";
            _saved = r.Text ?? "";
            Text = _saved;
            _loading = false;
            IsDirty = false;
            Check();
            Status = $"{Text.Split('\n').Length:N0} lines";
        }
        catch (HostException ex)
        {
            _loading = false;
            Status = "Could not read the script: " + ex.Message;
            _main.AddProblem("error", Status, Path);
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        if (_main.Host == null) return;
        Check();
        var error = Messages.FirstOrDefault(m => m.Severity == "error");
        if (error != null)
        {
            _main.AddProblem("error", $"{Title} not saved: line {error.Line}: {error.Message}", Path);
            SelectedMessage = error;
            return;
        }
        try
        {
            string text = Text;
            var r = await _main.Host.WriteMissionScriptAsync(Path, text);
            _saved = text;
            IsDirty = false;
            Status = $"Saved in {r.Ms} ms. The game compiles it when the mission loads: press Play; compile errors show in Output.";
            _main.OnDocumentSaved(this);
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

    /// <summary>Brackets and strings (what most often breaks a HaloScript file), plus the declarations list.</summary>
    public void Check()
    {
        Messages.Clear();
        foreach (var m in HaloScriptCheck.Run(Text)) Messages.Add(m);
        Declarations.Clear();
        foreach (var d in HaloScriptCheck.Declarations(Text)) Declarations.Add(d);
        int errors = Messages.Count(m => m.Severity == "error");
        CheckOk = errors == 0;
        Summary = errors == 0
            ? $"{Declarations.Count(d => d.Kind != "global")} scripts, {Declarations.Count(d => d.Kind == "global")} globals · brackets and strings OK"
            : $"{errors} problem{(errors == 1 ? "" : "s")}: {Messages[0].Message} (line {Messages[0].Line})";
        OnPropertyChanged(nameof(HasMessages));
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
