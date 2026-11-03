using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using Kalo.App.ViewModels;

namespace Kalo.App.Views;

/// <summary>
/// The text editor of a campaign mission script: Kalo Script or HaloScript colors, autocomplete (Ctrl+Space or while
/// typing), hover help, underlined problems, and Ctrl+click on a name to show it in the map.
/// </summary>
public partial class MissionScriptView : UserControl
{
    private MissionScriptViewModel? _vm;
    private bool _syncing;
    private static IHighlightingDefinition? _hs;
    private static IHighlightingDefinition? _ks;
    private readonly ErrorMarkers _errors;
    private CompletionWindow? _completion;
    private readonly DispatcherTimer _hoverTimer;
    private Point? _hoverPoint;
    private string? _hoverWord;

    public MissionScriptView()
    {
        InitializeComponent();
        _errors = new ErrorMarkers(Editor);
        Editor.SyntaxHighlighting = KaloScript();
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.TextChanged += (_, _) =>
        {
            if (_syncing || _vm == null) return;
            _vm.Text = Editor.Text;
        };
        DataContextChanged += (_, _) => Attach(DataContext as MissionScriptViewModel);

        Editor.TextArea.TextEntered += (_, e) =>
        {
            if (_completion != null || string.IsNullOrEmpty(e.Text) || _vm?.IsKaloScript != true) return;
            char c = e.Text[^1];
            if (char.IsLetterOrDigit(c) || c == '_') ShowCompletion(false);
        };
        Editor.TextArea.TextEntering += (_, e) =>
        {
            if (_completion != null && !string.IsNullOrEmpty(e.Text) && !(char.IsLetterOrDigit(e.Text[0]) || e.Text[0] == '_'))
                _completion.Close();
        };
        Editor.TextArea.KeyDown += (_, e) =>
        {
            HideHover();
            if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                ShowCompletion(true);
                e.Handled = true;
            }
        };

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); ShowHover(); };
        Editor.TextArea.AddHandler(PointerMovedEvent, (_, e) =>
        {
            var point = e.GetPosition(Editor);
            if (_hoverPoint is { } last && Math.Abs(last.X - point.X) < 3 && Math.Abs(last.Y - point.Y) < 3) return;
            _hoverPoint = point;
            var key = WordKey(point);
            if (key != _hoverWord) HideHover();
            _hoverTimer.Stop();
            if (key != null) _hoverTimer.Start();
            // Ctrl over a name: a hand, like a link.
            Editor.TextArea.Cursor = e.KeyModifiers.HasFlag(KeyModifiers.Control) && key != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        Editor.TextArea.AddHandler(PointerExitedEvent, (_, _) => { _hoverTimer.Stop(); HideHover(); }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        Editor.TextArea.AddHandler(PointerPressedEvent, async (_, e) =>
        {
            _hoverTimer.Stop();
            HideHover();
            // Ctrl+click: the name in the map (or the script it names).
            if (_vm != null && e.KeyModifiers.HasFlag(KeyModifiers.Control) && WordAt(e.GetPosition(Editor)) is { } word)
            {
                e.Handled = true;
                if (!await _vm.GoToNameAsync(word)) _vm.Status = $"{word}: not a name of the map or a script of this mission.";
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
    }

    private void Attach(MissionScriptViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.RevealRequested -= Reveal;
            _vm.PropertyChanged -= OnVmChanged;
            _vm.DiagnosticsChanged -= OnDiagnostics;
        }
        _vm = vm;
        _syncing = true;
        Editor.SyntaxHighlighting = vm?.IsKaloScript == false ? HaloScript() : KaloScript();
        // A new document rather than replacing the text (see ScriptEditorView: hidden editors keep stale line visuals).
        Editor.Document = new TextDocument(vm?.Text ?? "");
        _errors.Clear();
        _syncing = false;
        if (vm != null)
        {
            vm.RevealRequested += Reveal;
            vm.PropertyChanged += OnVmChanged;
            vm.DiagnosticsChanged += OnDiagnostics;
            OnDiagnostics();
        }
    }

    private void OnDiagnostics() => Dispatcher.UIThread.Post(() =>
    {
        if (_vm == null) { _errors.Clear(); return; }
        _errors.Set(_vm.Diagnostics, _vm.DiagnosticsText);
    });

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (e.PropertyName == nameof(MissionScriptViewModel.IsKaloScript)) Editor.SyntaxHighlighting = _vm.IsKaloScript ? KaloScript() : HaloScript();
        // Text loaded or set from outside the editor: show it.
        if (e.PropertyName == nameof(MissionScriptViewModel.Text) && Editor.Text != _vm.Text)
        {
            _syncing = true;
            Editor.Document = new TextDocument(_vm.Text);
            _errors.Clear();
            _syncing = false;
        }
    }

    private void Reveal(int line, int start, int length)
    {
        if (line < 1 || line > Editor.Document.LineCount) return;
        var docLine = Editor.Document.GetLineByNumber(line);
        int offset = start >= docLine.Offset && start <= docLine.EndOffset ? start : docLine.Offset;
        int len = Math.Clamp(length, 0, docLine.EndOffset - offset);
        Editor.Select(offset, len > 0 ? len : docLine.Length);
        Editor.ScrollToLine(line);
        Editor.TextArea.Caret.Offset = offset;
        Editor.Focus();
    }

    // ---- autocomplete and hover ----

    private void ShowCompletion(bool explicitRequest)
    {
        if (_vm == null) return;
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        string before = Editor.Document.GetText(line.Offset, Editor.CaretOffset - line.Offset);
        var (start, items) = _vm.Complete(before);
        if (items.Count == 0 || (!explicitRequest && before.Length - start < 2)) return;
        _completion = new CompletionWindow(Editor.TextArea) { StartOffset = line.Offset + start, Width = 460 };
        _completion.CompletionList.Background = Palette.Panel;
        _completion.CompletionList.Foreground = Palette.Text;
        foreach (var item in items) _completion.CompletionList.CompletionData.Add(new MegaloCompletion(item));
        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
    }

    /// <summary>The word under a point (names may hold one '/', like pts_barn/court01).</summary>
    private string? WordAt(Point point)
    {
        var pos = Editor.GetPositionFromPoint(point);
        if (pos == null) return null;
        var line = Editor.Document.GetLineByNumber(pos.Value.Line);
        string text = Editor.Document.GetText(line.Offset, line.Length);
        int col = pos.Value.Column - 1;
        if (col < 0 || col >= text.Length || !(char.IsLetterOrDigit(text[col]) || text[col] is '_' or '/')) return null;
        var (_, word) = Kalo.Core.Scripting.MissionAssist.WordAt(text, col);
        return word.Length > 0 ? word : null;
    }

    private string? WordKey(Point point)
    {
        var pos = Editor.GetPositionFromPoint(point);
        if (pos == null) return null;
        var word = WordAt(point);
        if (word != null) return $"{pos.Value.Line}:{word}";
        int offset = Editor.Document.GetOffset(pos.Value.Location);
        var marker = _errors.At(offset).FirstOrDefault();
        return marker == null ? null : "e:" + marker.StartOffset;
    }

    private void ShowHover()
    {
        if (_vm == null || _hoverPoint == null || _completion != null) return;
        var pos = Editor.GetPositionFromPoint(_hoverPoint.Value);
        if (pos == null) return;
        var line = Editor.Document.GetLineByNumber(pos.Value.Line);
        string text = Editor.Document.GetText(line.Offset, line.Length);
        var info = _vm.IsKaloScript ? _vm.Hover(text, pos.Value.Column - 1) : null;
        var errors = _errors.At(Editor.Document.GetOffset(pos.Value.Location)).SelectMany(m => m.All).ToList();
        if (info == null && errors.Count == 0) return;
        string? key = WordKey(_hoverPoint.Value);
        if (HoverPopup.IsOpen && key == _hoverWord) return;
        _hoverWord = key;
        HoverPopup.Child = HoverCard.Build(info, errors);
        HoverPopup.IsOpen = false;
        HoverPopup.IsOpen = true;
    }

    private void HideHover()
    {
        _hoverWord = null;
        if (HoverPopup.IsOpen) HoverPopup.IsOpen = false;
    }

    /// <summary>For tests: the hover help for a word of the text (as if the mouse rested on it).</summary>
    public HoverInfo? HoverFor(string word)
    {
        if (_vm == null) return null;
        int offset = Editor.Text.IndexOf(word, StringComparison.Ordinal);
        if (offset < 0) return null;
        var loc = Editor.Document.GetLocation(offset + 1);
        var l = Editor.Document.GetLineByNumber(loc.Line);
        return _vm.Hover(Editor.Document.GetText(l.Offset, l.Length), loc.Column - 1);
    }

    /// <summary>For tests: the underlined problems.</summary>
    public int UnderlineCount => _errors.Count;

    // ---- colors ----

    /// <summary>Kalo Script for missions: # comments, "strings", `raw HaloScript`, keywords, calls, numbers.</summary>
    private static IHighlightingDefinition KaloScript()
    {
        if (_ks != null) return _ks;
        const string xshd = @"<SyntaxDefinition name='KaloMission' xmlns='http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008'>
  <Color name='Comment' foreground='#6A9955' />
  <Color name='String' foreground='#CE9178' />
  <Color name='Raw' foreground='#A9B7C6' />
  <Color name='Declare' foreground='#C586C0' fontWeight='bold' />
  <Color name='Keyword' foreground='#569CD6' fontWeight='bold' />
  <Color name='Type' foreground='#4EC9B0' />
  <Color name='Number' foreground='#B5CEA8' />
  <Color name='Call' foreground='#DCDCAA' />
  <RuleSet>
    <Span color='Comment' begin='\#' />
    <Span color='String' begin='""' end='""' />
    <Span color='Raw' begin='`' end='`' />
    <Keywords color='Declare'>
      <Word>startup</Word><Word>dormant</Word><Word>continuous</Word><Word>command_script</Word><Word>def</Word><Word>stub</Word><Word>global</Word>
    </Keywords>
    <Keywords color='Keyword'>
      <Word>if</Word><Word>elif</Word><Word>else</Word><Word>repeat</Word><Word>every</Word><Word>timeout</Word><Word>until</Word><Word>return</Word><Word>pass</Word>
      <Word>and</Word><Word>or</Word><Word>not</Word><Word>True</Word><Word>False</Word><Word>None</Word><Word>begin_random</Word><Word>begin_random_count</Word><Word>begin</Word>
    </Keywords>
    <Keywords color='Type'>
      <Word>void</Word><Word>boolean</Word><Word>short</Word><Word>long</Word><Word>real</Word><Word>string</Word><Word>object</Word><Word>unit</Word><Word>ai</Word>
      <Word>trigger_volume</Word><Word>cutscene_flag</Word><Word>object_list</Word><Word>vehicle</Word><Word>team</Word><Word>sound</Word><Word>effect</Word>
    </Keywords>
    <Rule color='Call'>[A-Za-z_][A-Za-z0-9_]*(?=\()</Rule>
    <Rule color='Number'>\b-?[0-9]+(\.[0-9]+)?\b</Rule>
  </RuleSet>
</SyntaxDefinition>";
        using var reader = XmlReader.Create(new StringReader(xshd));
        _ks = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        return _ks;
    }

    /// <summary>HaloScript: ; comments (and ;* block *;), "strings", script / global declarations, control forms, numbers.</summary>
    private static IHighlightingDefinition HaloScript()
    {
        if (_hs != null) return _hs;
        const string xshd = @"<SyntaxDefinition name='HaloScript' xmlns='http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008'>
  <Color name='Comment' foreground='#6A9955' />
  <Color name='String' foreground='#CE9178' />
  <Color name='Declare' foreground='#C586C0' fontWeight='bold' />
  <Color name='Keyword' foreground='#569CD6' fontWeight='bold' />
  <Color name='Type' foreground='#4EC9B0' />
  <Color name='Number' foreground='#B5CEA8' />
  <Color name='Call' foreground='#DCDCAA' />
  <RuleSet ignoreCase='true'>
    <Span color='Comment' multiline='true' begin=';\*' end='\*;' />
    <Span color='Comment' begin=';' />
    <Span color='String' begin='""' end='""' />
    <Keywords color='Declare'>
      <Word>script</Word><Word>global</Word><Word>startup</Word><Word>dormant</Word><Word>continuous</Word><Word>static</Word><Word>stub</Word><Word>command_script</Word>
    </Keywords>
    <Keywords color='Keyword'>
      <Word>if</Word><Word>begin</Word><Word>begin_random</Word><Word>cond</Word><Word>set</Word><Word>and</Word><Word>or</Word><Word>not</Word>
      <Word>sleep</Word><Word>sleep_until</Word><Word>sleep_forever</Word><Word>wake</Word><Word>thread</Word><Word>inspect</Word>
      <Word>true</Word><Word>false</Word><Word>none</Word>
    </Keywords>
    <Keywords color='Type'>
      <Word>void</Word><Word>boolean</Word><Word>short</Word><Word>long</Word><Word>real</Word><Word>string</Word><Word>object</Word><Word>unit</Word>
      <Word>ai</Word><Word>trigger_volume</Word><Word>cutscene_flag</Word><Word>cutscene_camera_point</Word><Word>object_list</Word><Word>vehicle</Word>
      <Word>zone_set</Word><Word>point_reference</Word><Word>sound</Word><Word>effect</Word><Word>object_name</Word><Word>team</Word>
    </Keywords>
    <Rule color='Call'>(?&lt;=\()\s*[A-Za-z_][A-Za-z0-9_!?]*</Rule>
    <Rule color='Number'>\b-?[0-9]+(\.[0-9]+)?\b</Rule>
  </RuleSet>
</SyntaxDefinition>";
        using var reader = XmlReader.Create(new StringReader(xshd));
        _hs = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        return _hs;
    }
}
