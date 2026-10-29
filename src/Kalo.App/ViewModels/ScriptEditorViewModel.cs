using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// A gametype script (Megalo source, data\multiplayer\megalo\*.txt) with the compiler HREK's MegaloEdit uses:
/// errors with line numbers, budget stats, declared variables.
/// </summary>
public partial class ScriptEditorViewModel : DocumentViewModel
{
    private readonly MainViewModel _main;
    private Encoding _encoding = Encoding.Unicode;
    private string _savedText = "";

    /// <param name="path">Relative to the workspace, e.g. data\multiplayer\megalo\slayer.txt</param>
    public ScriptEditorViewModel(MainViewModel main, string path) : base(path)
    {
        _main = main;
    }

    public override string Badge => "script";
    public override string Breadcrumb => string.Join(" › ", Path.Split('\\'));
    public override string BaselinePath => Path;
    public string FullPath => System.IO.Path.Combine(_main.Paths!.Workspace, Path);

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _compileSummary = "Not compiled yet. Press Compile (F7).";
    [ObservableProperty] private bool _compileOk;
    [ObservableProperty] private string _catalogQuery = "";
    [ObservableProperty] private ScriptMessage? _selectedMessage;

    public ObservableCollection<ScriptMessage> Messages { get; } = new();
    public bool HasMessages => Messages.Count > 0;
    public ObservableCollection<StatRow> Stats { get; } = new();
    public ObservableCollection<MegaloEntry> CatalogResults { get; } = new();
    public ObservableCollection<string> Variables { get; } = new();

    /// <summary>The view moves the caret here (1-based line, offset in the text).</summary>
    public event Action<int, int, int>? RevealRequested;
    /// <summary>The view inserts this text at the caret.</summary>
    public event Action<string>? InsertRequested;

    public async Task LoadAsync()
    {
        byte[] bytes = await File.ReadAllBytesAsync(FullPath);
        _encoding = Detect(bytes);
        string text = _encoding.GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        _savedText = text;
        Text = text;
        IsDirty = false;
        Status = $"{text.Split('\n').Length:N0} lines · {(_encoding is UnicodeEncoding ? "UTF-16" : "UTF-8")}";
        UpdateCatalog();
    }

    private static Encoding Detect(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode;
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode;
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return new UTF8Encoding(true);
        return new UTF8Encoding(false);
    }

    partial void OnTextChanged(string value) => IsDirty = value != _savedText;
    partial void OnCatalogQueryChanged(string value) => UpdateCatalog();

    partial void OnSelectedMessageChanged(ScriptMessage? value)
    {
        if (value != null && value.Line > 0) RevealRequested?.Invoke(value.Line, value.Start, value.Length);
    }

    public void UpdateCatalog()
    {
        CatalogResults.Clear();
        var catalog = _main.MegaloCatalog;
        if (catalog == null) return;
        string[] words = CatalogQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<MegaloEntry> all = (catalog.Actions ?? new()).Select(a => new MegaloEntry { Name = "action " + a.Name, Description = a.Description })
            .Concat((catalog.Conditions ?? new()).Select(c => new MegaloEntry { Name = "condition " + c.Name, Description = c.Description }));
        foreach (var e in all)
        {
            string hay = e.Name + " " + e.Description;
            if (words.All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase))) CatalogResults.Add(e);
            if (CatalogResults.Count >= 200) break;
        }
    }

    [RelayCommand]
    private void Insert(MegaloEntry? entry)
    {
        if (entry?.Name != null) InsertRequested?.Invoke(entry.Name + " ");
    }

    [RelayCommand]
    public Task Compile() => CompileAsync(null);

    private async Task CompileAsync(string? mgloFile)
    {
        if (_main.Host == null || _main.Paths == null) return;
        CompileSummary = "Compiling…";
        try
        {
            string output = System.IO.Path.Combine(_main.Paths.KaloData, "megalo", System.IO.Path.GetFileNameWithoutExtension(Path));
            var r = await _main.Host.MegaloCompileAsync(new MegaloCompileParams
            {
                ObjectLists = _main.Paths.MegaloObjectLists,
                IncludeFolder = _main.Paths.MegaloSource,
                Text = Text,
                OutputFolder = output,
                MgloFile = mgloFile
            });
            Messages.Clear();
            foreach (var m in r.Messages ?? new())
                Messages.Add(new ScriptMessage(m.Line, m.Start, m.Length, m.Severity ?? "error", m.Message ?? "", m.Token));
            Stats.Clear();
            var stats = r.Stats ?? new();
            void Stat(string label, string key, long? max = null)
            {
                if (stats.TryGetValue(key, out long v)) Stats.Add(new StatRow(label, max.HasValue ? $"{v:N0} / {max:N0}" : v.ToString("N0")));
            }
            Stat("Triggers", "TriggersUsed");
            Stat("Conditions", "ConditionsUsed");
            Stat("Actions", "ActionsUsed");
            Stat("Strings", "StringsUsed");
            Stat("String bytes", "StringBytesUsed");
            Stat("Game options", "GameOptionsUsed");
            Stat("Player traits", "PlayerTraitsUsed");
            Stat("Loadouts", "LoadoutsUsed");
            Stat("HUD widgets", "HudWidgetsUsed");
            Stat("Encoded size (bytes)", "EncodedSize");
            Variables.Clear();
            foreach (var kv in r.Variables ?? new())
                if (kv.Value.Count > 0) Variables.Add($"{kv.Key}: {string.Join(", ", kv.Value)}");
            OnPropertyChanged(nameof(HasMessages));
            CompileOk = r.Succeeded;
            int errors = Messages.Count(m => m.IsError);
            CompileSummary = r.Succeeded
                ? $"Compiled in {r.Ms} ms" + (Messages.Count > 0 ? $" with {Messages.Count} warning(s)" : "") + "."
                : $"{errors} error(s). Fix the first one and compile again.";
            _main.ReportScriptProblems(this, Messages);
            if (!r.Succeeded && Messages.Count > 0) SelectedMessage = Messages[0];
        }
        catch (HostException ex)
        {
            CompileOk = false;
            CompileSummary = "The compiler stopped: " + ex.Message;
            _main.AddProblem("error", ex.Message, Path);
            if (ex.EngineHalted) await _main.RestartHostAsync();
        }
    }

    /// <summary>
    /// Compiles, deploys the engine to maps\megalo\kalo_&lt;name&gt;.mglo in the workspace (as MegaloEdit does for a devkit)
    /// and starts the selected map with load_binary_game_engine, so the game plays this script.
    /// </summary>
    [RelayCommand]
    private async Task TestInGame()
    {
        if (_main.Host == null || _main.Paths == null) return;
        string name = "kalo_" + System.IO.Path.GetFileNameWithoutExtension(Path);
        string mglo = System.IO.Path.Combine(_main.Paths.Workspace, "maps", "megalo", name + ".mglo");
        await CompileAsync(mglo);
        if (!CompileOk) return;
        if (!File.Exists(mglo)) { CompileSummary = "The compiler did not produce a game engine to deploy."; return; }
        _main.StartGame(new[] { "load_binary_game_engine " + name },
            $"Testing {Title} in the game: deployed {mglo} and asked the game to load it before the map starts.");
        CompileSummary += " Deployed and launched in the game.";
    }

    [RelayCommand]
    private async Task Save()
    {
        try
        {
            byte[] preamble = _encoding.GetPreamble();
            byte[] body = _encoding.GetBytes(Text);
            await using (var f = File.Create(FullPath))
            {
                await f.WriteAsync(preamble);
                await f.WriteAsync(body);
            }
            _savedText = Text;
            IsDirty = false;
            Status = "Saved";
            _main.OnDocumentSaved(this);
        }
        catch (Exception ex)
        {
            _main.AddProblem("error", "Save failed: " + ex.Message, Path);
        }
    }

    public override Task SaveDocumentAsync() => SaveCommand.ExecuteAsync(null);

    public void Reveal(int line, int start, int length) => RevealRequested?.Invoke(line, start, length);
}

public sealed record ScriptMessage(int Line, int Start, int Length, string Severity, string Message, string? Token)
{
    public bool IsError => Severity.Contains("error", StringComparison.OrdinalIgnoreCase);
    public string Where => Line > 0 ? $"line {Line}" : "";
    public string Display => $"{Message}" + (string.IsNullOrEmpty(Token) ? "" : $"  ('{Token}')");
}

public sealed record StatRow(string Label, string Value);

/// <summary>A script in the Gametypes panel.</summary>
public sealed record ScriptFile(string Name, string RelativePath, string Folder);
