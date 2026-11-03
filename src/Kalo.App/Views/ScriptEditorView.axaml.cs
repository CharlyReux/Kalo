using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using Kalo.App.ViewModels;

namespace Kalo.App.Views;

public partial class ScriptEditorView : UserControl
{
    private ScriptEditorViewModel? _vm;
    private bool _syncing;
    private static IHighlightingDefinition? _megalo;

    public ScriptEditorView()
    {
        InitializeComponent();
        _errors = new ErrorMarkers(Editor);
        Editor.SyntaxHighlighting = Megalo();
        MegaloView.SyntaxHighlighting = Megalo();
        Editor.Options.ConvertTabsToSpaces = false;
        Editor.Options.IndentationSize = 4;
        Editor.TextChanged += (_, _) =>
        {
            if (_syncing || _vm == null) return;
            _vm.Text = Editor.Text;
        };
        DataContextChanged += (_, _) => Attach(DataContext as ScriptEditorViewModel);
        Editor.TextArea.TextEntered += (_, e) =>
        {
            if (_completion != null || string.IsNullOrEmpty(e.Text)) return;
            char c = e.Text[^1];
            if (char.IsLetterOrDigit(c) || c == '_' || c == ' ' || c == '(' || c == ',' || c == '.') ShowCompletion(false);
        };
        // A character that can't be part of a suggestion closes the list (without inserting anything), so a list opened
        // after "(" or "," doesn't stay open, empty, and swallow the arrow keys once the call is typed.
        Editor.TextArea.TextEntering += (_, e) =>
        {
            if (_completion != null && !string.IsNullOrEmpty(e.Text) && !(char.IsLetterOrDigit(e.Text[0]) || e.Text[0] == '_'))
                _completion.Close();
        };
        // Up / Down with an empty suggestion list move the caret, as they should.
        Editor.TextArea.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_completion != null && e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown
                && (_completion.CompletionList.ListBox?.ItemCount ?? 0) == 0) _completion.Close();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        // Sounds in the suggestion list: the one reached with the arrow keys plays after a short pause.
        Editor.TextArea.AddHandler(KeyUpEvent, (_, e) =>
        {
            if (_completion != null && e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown
                && _completion.CompletionList.SelectedItem is MegaloCompletion { Item.Kind: "sound" } c) HoverSound(c.Text);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        // The call the caret is in: its arguments, the current one marked.
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCallHint();
        // Hover help: after a short pause over a word, a popup explains it.
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); ShowHover(); };
        Editor.TextArea.AddHandler(PointerMovedEvent, (_, e) =>
        {
            var point = e.GetPosition(Editor);
            // Tiny moves (or the same position re-sent when the popup opens) change nothing.
            if (_hoverPoint is { } last && Math.Abs(last.X - point.X) < 3 && Math.Abs(last.Y - point.Y) < 3) return;
            _hoverPoint = point;
            var word = HoverKey(_hoverPoint.Value);
            if (word != _hoverWord) HideHover();
            _hoverTimer.Stop();
            if (word != null) _hoverTimer.Start();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        Editor.TextArea.AddHandler(PointerExitedEvent, (_, _) => { _hoverTimer.Stop(); HideHover(); }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        Editor.TextArea.AddHandler(PointerPressedEvent, (_, _) => { _hoverTimer.Stop(); HideHover(); }, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        Editor.TextArea.KeyDown += (_, e) =>
        {
            HideHover();
            if (e.Key == Key.F1)
            {
                // F1: the documentation, at the word under the caret.
                var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
                string text = Editor.Document.GetText(line.Offset, line.Length);
                int col = Editor.CaretOffset - line.Offset;
                int a = col, b = col;
                while (a > 0 && (char.IsLetterOrDigit(text[a - 1]) || text[a - 1] == '_')) a--;
                while (b < text.Length && (char.IsLetterOrDigit(text[b]) || text[b] == '_')) b++;
                if (TopLevel.GetTopLevel(this) is Window w && _vm != null) DocsWindow.Open(w, _vm.Main, b > a ? text[a..b] : null);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.I && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                FormatDocument();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                ShowCompletion(true);
                e.Handled = true;
            }
        };
    }

    private CompletionWindow? _completion;
    private readonly DispatcherTimer _hoverTimer;
    private Avalonia.Point? _hoverPoint;
    private string? _hoverWord;
    private readonly ErrorMarkers _errors;

    /// <summary>What the pointer rests on, for the hover popup: a word, or an underlined error.</summary>
    private string? HoverKey(Avalonia.Point point)
    {
        string? word = WordAt(point);
        if (word != null) return word;
        int? offset = OffsetAt(point);
        var marker = offset == null ? null : _errors.At(offset.Value).FirstOrDefault();
        return marker == null ? null : "e:" + marker.StartOffset;
    }

    private int? OffsetAt(Avalonia.Point point)
    {
        var pos = Editor.GetPositionFromPoint(point);
        return pos == null ? null : Editor.Document.GetOffset(pos.Value.Location);
    }

    /// <summary>The errors and warnings underlined in the code (for tests).</summary>
    public IReadOnlyList<ScriptMessage> Underlined => _errors.All.Select(m => m.Message).ToList();
    public IReadOnlyList<(int Line, string Text)> UnderlinedText =>
        _errors.All.Select(m => (Editor.Document.GetLineByOffset(m.StartOffset).LineNumber, Editor.Document.GetText(m.StartOffset, m.Length))).ToList();

    // ---- sound previews ----

    private DispatcherTimer? _soundTimer;
    private string _soundPending = "";
    private string _soundPlayed = "";

    /// <summary>The mouse rests on (or the arrow keys select) a sound: it plays after 350 ms. "" cancels.</summary>
    private void HoverSound(string name)
    {
        _soundPending = name;
        if (_soundTimer == null)
        {
            _soundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _soundTimer.Tick += (_, _) =>
            {
                _soundTimer!.Stop();
                if (_soundPending.Length > 0 && _soundPending != _soundPlayed) PlaySound(_soundPending);
            };
        }
        _soundTimer.Stop();
        if (name.Length > 0) _soundTimer.Start();
    }

    private async void PlaySound(string name)
    {
        if (_vm == null) return;
        _soundPlayed = name;
        string status = await _vm.Main.PlayMegaloSoundAsync(name);
        CallHintText.Text = status;
        CallHintBox.IsVisible = true;
    }

    /// <summary>Ctrl+Shift+I: formats the whole file as one undo step; the caret stays on its line, after the same code.</summary>
    public void FormatDocument()
    {
        if (_vm == null) return;
        var doc = Editor.Document;
        var caret = doc.GetLocation(Editor.CaretOffset);
        var caretLine = doc.GetLineByNumber(caret.Line);
        string beforeCaret = doc.GetText(caretLine.Offset, Math.Max(0, Editor.CaretOffset - caretLine.Offset));
        int codeBefore = beforeCaret.Count(c => !char.IsWhiteSpace(c));
        var result = _vm.Formatted(doc.Text);
        if (result.Changed)
        {
            // Line numbers before the caret may shift when blank lines are removed: count the code lines above it.
            int codeLinesAbove = Enumerable.Range(1, caret.Line - 1).Count(n => doc.GetText(doc.GetLineByNumber(n)).Trim().Length > 0);
            doc.BeginUpdate();
            doc.Replace(0, doc.TextLength, result.Text);
            doc.EndUpdate();
            // Back to the same code line, after the same number of non-space characters.
            int line = 1, seen = 0;
            for (; line <= doc.LineCount; line++)
            {
                bool code = doc.GetText(doc.GetLineByNumber(line)).Trim().Length > 0;
                if (code && seen == codeLinesAbove) break;
                if (code) seen++;
            }
            line = Math.Clamp(line, 1, doc.LineCount);
            var l = doc.GetLineByNumber(line);
            string text = doc.GetText(l);
            int col = text.Length - text.TrimStart().Length; // caret in the indentation: start of the code
            for (int count = 0; codeBefore > 0 && col < text.Length && count < codeBefore; col++)
                if (!char.IsWhiteSpace(text[col])) count++;
            Editor.CaretOffset = l.Offset + Math.Min(col, l.Length);
        }
        _vm.FormatDone(result.Message);
        Editor.Focus();
    }

    private void UpdateCallHint()
    {
        string? hint = null;
        if (_vm != null && Editor.Document.TextLength > 0)
        {
            var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
            hint = _vm.CallHint(Editor.Document.GetText(line.Offset, Editor.CaretOffset - line.Offset));
        }
        CallHintText.Text = hint ?? "";
        CallHintBox.IsVisible = hint != null;
    }

    /// <summary>For tests: the call hint shown for the caret at the end of a line's text up to a marker.</summary>
    public string? CallHintAfter(string textBeforeCaret)
    {
        int offset = Editor.Text.IndexOf(textBeforeCaret, StringComparison.Ordinal);
        if (offset < 0) return null;
        Editor.CaretOffset = offset + textBeforeCaret.Length;
        UpdateCallHint();
        return CallHintBox.IsVisible ? CallHintText.Text : null;
    }

    /// <summary>For tests: types text at the end of a line (as the keyboard would), then presses a key; returns the caret's line after.</summary>
    public (int Line, bool ListOpen) TypeThenPress(int line, string text, Key key)
    {
        Editor.Focus();
        Editor.CaretOffset = Editor.Document.GetLineByNumber(line).EndOffset;
        foreach (char c in text) Editor.TextArea.PerformTextInput(c.ToString());
        var args = new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key, Source = Editor.TextArea };
        Editor.TextArea.RaiseEvent(args);
        return (Editor.TextArea.Caret.Line, _completion != null);
    }

    /// <summary>For tests: the suggestions offered with the caret right after a piece of text.</summary>
    public IReadOnlyList<string> SuggestionsAfter(string textBeforeCaret)
    {
        if (_vm == null) return Array.Empty<string>();
        int offset = Editor.Text.IndexOf(textBeforeCaret, StringComparison.Ordinal);
        if (offset < 0) return Array.Empty<string>();
        int end = offset + textBeforeCaret.Length;
        var line = Editor.Document.GetLineByOffset(end);
        return _vm.Complete(Editor.Document.GetText(line.Offset, end - line.Offset)).Items.Select(i => i.Text).ToList();
    }

    private void ShowDiagnostics()
    {
        if (_vm == null) { _errors.Clear(); return; }
        _errors.Set(_vm.Diagnostics, _vm.DiagnosticsText);
        // The popup of an error that is gone (or moved) closes; others stay open.
        if (_hoverWord?.StartsWith("e:") == true && _errors.All.All(m => "e:" + m.StartOffset != _hoverWord)) HideHover();
    }

    /// <summary>"line:start:word" under a point of the editor, or null.</summary>
    private string? WordAt(Avalonia.Point point)
    {
        var pos = Editor.GetPositionFromPoint(point);
        if (pos == null) return null;
        var line = Editor.Document.GetLineByNumber(pos.Value.Line);
        string text = Editor.Document.GetText(line.Offset, line.Length);
        int col = pos.Value.Column - 1;
        if (col < 0 || col >= text.Length || !(char.IsLetterOrDigit(text[col]) || text[col] == '_')) return null;
        int a = col;
        while (a > 0 && (char.IsLetterOrDigit(text[a - 1]) || text[a - 1] == '_')) a--;
        return $"{pos.Value.Line}:{a}";
    }

    private void ShowHover()
    {
        if (_vm == null || _hoverPoint == null || _completion != null) return;
        var pos = Editor.GetPositionFromPoint(_hoverPoint.Value);
        if (pos == null) return;
        var line = Editor.Document.GetLineByNumber(pos.Value.Line);
        string text = Editor.Document.GetText(line.Offset, line.Length);
        var info = _vm.Hover(text, pos.Value.Column - 1);
        var errors = _errors.At(Editor.Document.GetOffset(pos.Value.Location)).SelectMany(m => m.All).ToList();
        if (info == null && errors.Count == 0) return;
        string? key = HoverKey(_hoverPoint.Value);
        if (HoverPopup.IsOpen && key == _hoverWord) return; // already showing it: don't reopen (flicker)
        _hoverWord = key;
        HoverPopup.Child = HoverCard.Build(info, errors);
        // A sound name in the code: hear it.
        string word = WordText(text, pos.Value.Column - 1);
        if (word.Length > 0 && _vm.Main.IsMegaloSound(word)) { _soundPlayed = ""; PlaySound(word); }
        HoverPopup.IsOpen = false;
        HoverPopup.IsOpen = true;
    }

    private static string WordText(string line, int column)
    {
        if (column < 0 || column > line.Length) return "";
        int a = column, b = column;
        while (a > 0 && (char.IsLetterOrDigit(line[a - 1]) || line[a - 1] == '_')) a--;
        while (b < line.Length && (char.IsLetterOrDigit(line[b]) || line[b] == '_')) b++;
        return line[a..b];
    }

    private void HideHover()
    {
        _hoverWord = null;
        if (HoverPopup.IsOpen) HoverPopup.IsOpen = false;
    }

    /// <summary>For tests: renders the open hover popup to a PNG (popups are separate windows, not in the window's screenshot).</summary>
    public bool SaveHoverShot(string path)
    {
        if (!HoverPopup.IsOpen || HoverPopup.Child is not Control card) return false;
        var size = new Avalonia.PixelSize(Math.Max(1, (int)card.Bounds.Width), Math.Max(1, (int)card.Bounds.Height));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96, 96));
        bitmap.Render(card);
        bitmap.Save(path);
        return true;
    }

    /// <summary>For tests: the hover help at a 1-based line and column.</summary>
    public HoverInfo? HoverAt(int line, int column)
    {
        if (_vm == null || line < 1 || line > Editor.Document.LineCount) return null;
        var l = Editor.Document.GetLineByNumber(line);
        return _vm.Hover(Editor.Document.GetText(l.Offset, l.Length), column - 1);
    }

    /// <summary>For tests: shows the hover popup for a word, as if the mouse rested on it.</summary>
    public bool ShowHoverFor(string word)
    {
        int offset = Editor.Text.IndexOf(word, StringComparison.Ordinal);
        if (offset < 0) return false;
        var loc = Editor.Document.GetLocation(offset + 1);
        var visual = Editor.TextArea.TextView.GetVisualPosition(new AvaloniaEdit.TextViewPosition(loc), AvaloniaEdit.Rendering.VisualYPosition.LineMiddle);
        Editor.ScrollTo(loc.Line, loc.Column);
        _hoverPoint = Editor.TextArea.TextView.TranslatePoint(visual - Editor.TextArea.TextView.ScrollOffset, Editor);
        ShowHover();
        return HoverPopup.IsOpen;
    }

    /// <summary>Opens the suggestion list for the word at the caret (Ctrl+Space, or while typing).</summary>
    private void ShowCompletion(bool explicitRequest)
    {
        if (_vm == null) return;
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        string before = Editor.Document.GetText(line.Offset, Editor.CaretOffset - line.Offset);
        bool inCall = _vm.IsKaloScript && Kalo.Core.Scripting.KaloCompletion.CallAt(before) != null;
        if (!explicitRequest && !inCall && before.EndsWith(' ') && Kalo.Core.Scripting.KaloCompletion.EnumValues(before, _vm.Text) == null && !before.TrimEnd().EndsWith("action") && !before.TrimEnd().EndsWith("condition")) return;
        var (start, items) = _vm.Complete(before, explicitRequest);
        if (items.Count == 0) return;
        _completion = new CompletionWindow(Editor.TextArea) { StartOffset = line.Offset + start, Width = 420 };
        _completion.CompletionList.Background = Palette.Panel;
        _completion.CompletionList.Foreground = Palette.Text;
        foreach (var item in items)
            _completion.CompletionList.CompletionData.Add(item.Kind == "sound" ? new MegaloCompletion(item, HoverSound, PlaySound) : new MegaloCompletion(item));
        _completion.Closed += (_, _) => { _completion = null; HoverSound(""); };
        _completion.Show();

    }

    private void Attach(ScriptEditorViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.RevealRequested -= Reveal;
            _vm.InsertRequested -= Insert;
            _vm.DiagnosticsChanged -= OnDiagnosticsChanged;
            _vm.FormatRequested -= FormatDocument;
        }
        _vm = vm;
        _syncing = true;
        Editor.SyntaxHighlighting = vm?.IsKaloScript == true ? KaloScriptHighlighting() : Megalo();
        // A new document rather than replacing the text: an editor hidden in the other editor group would
        // otherwise keep line-number visuals for deleted lines and crash when it is shown again.
        Editor.Document = new TextDocument(vm?.Text ?? "");
        MegaloView.Document = new TextDocument(vm?.GeneratedMegalo ?? "");
        _syncing = false;
        if (vm != null)
        {
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ScriptEditorViewModel.GeneratedMegalo) && ReferenceEquals(_vm, vm)) MegaloView.Document = new TextDocument(vm.GeneratedMegalo);
                // Text set from outside the editor (revert, tools): show it.
                if (e.PropertyName == nameof(ScriptEditorViewModel.Text) && ReferenceEquals(_vm, vm) && Editor.Text != vm.Text)
                {
                    _syncing = true;
                    Editor.Text = vm.Text;
                    _syncing = false;
                }
            };
            vm.RevealRequested += Reveal;
            vm.InsertRequested += Insert;
            vm.DiagnosticsChanged += OnDiagnosticsChanged;
            vm.FormatRequested += FormatDocument;
        }
        ShowDiagnostics();
    }

    private void OnDiagnosticsChanged() => Dispatcher.UIThread.Post(ShowDiagnostics);

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

    private void Insert(string text)
    {
        Editor.Document.Insert(Editor.CaretOffset, text);
        Editor.Focus();
    }

    private static IHighlightingDefinition? _kalo;

    /// <summary>Kalo Script syntax: -- comments, "strings", if/then/for each..., f(...) calls, megalo [[ ]] blocks.</summary>
    private static IHighlightingDefinition KaloScriptHighlighting()
    {
        if (_kalo != null) return _kalo;
        const string xshd = @"<SyntaxDefinition name='KaloScript' xmlns='http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008'>
  <Color name='Comment' foreground='#6A9955' />
  <Color name='String' foreground='#CE9178' />
  <Color name='Keyword' foreground='#569CD6' fontWeight='bold' />
  <Color name='Section' foreground='#C586C0' fontWeight='bold' />
  <Color name='Call' foreground='#DCDCAA' />
  <Color name='Number' foreground='#B5CEA8' />
  <Color name='Member' foreground='#9CDCFE' />
  <Color name='Raw' foreground='#A9B7C6' />
  <RuleSet>
    <Span color='Comment' begin='--' />
    <Span color='Comment' begin='//' />
    <Span color='Comment' begin='\#' />
    <Span color='String'><Begin>""</Begin><End>""</End></Span>
    <Span color='Raw' multiline='true'><Begin>\[\[</Begin><End>\]\]</End></Span>
    <Keywords color='Section'>
      <Word>declare</Word><Word>enum</Word><Word>function</Word><Word>def</Word><Word>every</Word><Word>tick</Word><Word>alias</Word><Word>megalo</Word><Word>include</Word><Word>on</Word><Word>setting</Word><Word>option</Word>
      <Word>traits</Word><Word>objects</Word><Word>constant</Word><Word>widget</Word><Word>stat</Word><Word>engine</Word><Word>teams</Word><Word>loadout</Word>
      <Word>hidden</Word><Word>locked</Word><Word>value</Word><Word>range</Word>
    </Keywords>
    <Keywords color='Keyword'>
      <Word>if</Word><Word>then</Word><Word>else</Word><Word>elseif</Word><Word>elif</Word><Word>pass</Word><Word>end</Word><Word>for</Word><Word>each</Word><Word>do</Word><Word>inline</Word>
      <Word>and</Word><Word>or</Word><Word>not</Word><Word>temp</Word><Word>with</Word><Word>networked</Word><Word>local</Word>
      <Word>true</Word><Word>false</Word><Word>none</Word>
    </Keywords>
    <Rule color='Call'>[a-z_][a-z0-9_]*(?=\()</Rule>
    <Rule color='Member'>\.[a-z_][a-z0-9_]*</Rule>
    <Rule color='Number'>\b-?[0-9]+(\.[0-9]+)?\b</Rule>
  </RuleSet>
</SyntaxDefinition>";
        using var reader = XmlReader.Create(new StringReader(xshd.Replace('\'', '"')));
        _kalo = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        return _kalo;
    }

    /// <summary>Megalo syntax: ; comments, "strings", numbers, block keywords, the word after action/condition.</summary>
    private static IHighlightingDefinition Megalo()
    {
        if (_megalo != null) return _megalo;
        const string xshd = @"<SyntaxDefinition name='Megalo' xmlns='http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008'>
  <Color name='Comment' foreground='#6A9955' />
  <Color name='String' foreground='#CE9178' />
  <Color name='Keyword' foreground='#569CD6' fontWeight='bold' />
  <Color name='Section' foreground='#C586C0' fontWeight='bold' />
  <Color name='Call' foreground='#DCDCAA' />
  <Color name='Number' foreground='#B5CEA8' />
  <Color name='Member' foreground='#9CDCFE' />
  <RuleSet ignoreCase='true'>
    <Span color='Comment' begin=';' />
    <Span color='String'><Begin>""</Begin><End>""</End></Span>
    <Keywords color='Section'>
      <Word>trigger</Word><Word>include</Word><Word>localized_include</Word><Word>game_options</Word><Word>string_table</Word>
      <Word>constants</Word><Word>loadout</Word><Word>loadout_palette</Word><Word>teams</Word><Word>team</Word><Word>engine_data</Word>
      <Word>player_rating</Word><Word>map_permissions</Word><Word>variables</Word><Word>requisition_palette</Word><Word>hud_widgets</Word>
      <Word>map_object</Word><Word>game_stats</Word>
    </Keywords>
    <Keywords color='Keyword'>
      <Word>action</Word><Word>condition</Word><Word>end</Word><Word>if</Word><Word>not</Word><Word>temporary</Word><Word>override</Word>
      <Word>option</Word><Word>networked</Word><Word>local</Word><Word>true</Word><Word>false</Word>
    </Keywords>
    <Rule color='Call'>(?&lt;=\b(action|condition)\s+)(?!if\b)[a-z_][a-z0-9_]*</Rule>
    <Rule color='Member'>\.[a-z_][a-z0-9_]*</Rule>
    <Rule color='Number'>\b-?[0-9]+(\.[0-9]+)?\b</Rule>
  </RuleSet>
</SyntaxDefinition>";
        using var reader = XmlReader.Create(new StringReader(xshd.Replace('\'', '"')));
        _megalo = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        return _megalo;
    }
}

/// <summary>An autocomplete row: the word, its kind on the right, its signature as the tooltip.</summary>
internal sealed class MegaloCompletion(CompletionItem item, Action<string>? hover = null, Action<string>? play = null) : ICompletionData
{
    public IImage? Image => null;
    public string Text => item.Text;
    public CompletionItem Item => item;
    // Controls with explicit colors: the popup lives outside the window and does not inherit its text color.
    public object Content
    {
        get
        {
            var row = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 10,
                Background = Brushes.Transparent,
                Children =
                {
                    new TextBlock { Text = item.Text, Foreground = Palette.Text, FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace") },
                    new TextBlock { Text = item.Kind, Foreground = Palette.Muted, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                },
            };
            if (play != null)
            {
                // Sounds: a play button, and resting the mouse on the row plays it too.
                var button = new Button
                {
                    Content = "▶", FontSize = 10, Padding = new Avalonia.Thickness(5, 0), MinHeight = 0, Background = Brushes.Transparent,
                    Foreground = Palette.Code, BorderThickness = new Avalonia.Thickness(0), Focusable = false,
                };
                ToolTip.SetTip(button, "Play this sound");
                button.Click += (_, e) => { e.Handled = true; play(item.Text); };
                row.Children.Insert(0, button);
                row.PointerEntered += (_, _) => hover?.Invoke(item.Text);
                row.PointerExited += (_, _) => hover?.Invoke("");
            }
            return row;
        }
    }
    public object? Description => new TextBlock
    {
        Text = string.IsNullOrEmpty(item.Description) ? item.Kind
            : item.Description.StartsWith(item.Text + "(", StringComparison.Ordinal) ? item.Description : item.Text + " " + item.Description,
        Foreground = Palette.Text,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 420,
    };
    public double Priority => item.Kind is "action" or "condition" ? 1 : 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, Text);
}

/// <summary>The hover popup's content: title, call form, what it does, an example. Explicit colors (popups don't inherit).</summary>
internal static class HoverCard
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");

    public static Control Build(HoverInfo? info, IReadOnlyList<ScriptMessage>? errors = null)
    {
        var panel = new StackPanel { Spacing = 5, MaxWidth = 520 };
        int shown = 0;
        foreach (var e in errors ?? Array.Empty<ScriptMessage>())
        {
            // The first message is the mistake; the rest are usually knock-on errors from it.
            if (shown++ == 1) panel.Children.Add(new TextBlock { Text = "Also reported on this line (often caused by the first):", Foreground = Palette.Muted, FontSize = 11 });
            var row = new DockPanel { Opacity = shown == 1 ? 1 : 0.75 };
            var icon = new TextBlock { Text = e.IsError ? "✕" : "!", Foreground = e.IsError ? ErrorMarkers.ErrorBrush : ErrorMarkers.WarningBrush, FontWeight = FontWeight.Bold, Margin = new Avalonia.Thickness(0, 0, 6, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = e.Display, Foreground = Palette.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(row);
        }
        if (info == null) return Card(panel);
        if (panel.Children.Count > 0) panel.Children.Add(new Border { Height = 1, Background = Palette.Border, Margin = new Avalonia.Thickness(0, 2) });
        panel.Children.Add(new TextBlock { Text = info.Title, FontWeight = FontWeight.SemiBold, Foreground = Palette.Text, FontFamily = Mono, FontSize = 12.5 });
        if (!string.IsNullOrEmpty(info.Signature))
            panel.Children.Add(new TextBlock { Text = info.Signature, Foreground = Palette.Code, FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = info.Text, Foreground = Palette.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(info.Example))
            panel.Children.Add(new Border
            {
                Background = Palette.Bg, CornerRadius = new Avalonia.CornerRadius(3), Padding = new Avalonia.Thickness(6, 4),
                Child = new TextBlock { Text = info.Example, Foreground = Palette.Code, FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap },
            });
        if (!string.IsNullOrEmpty(info.Megalo))
            panel.Children.Add(new TextBlock { Text = "Megalo: " + info.Megalo, Foreground = Palette.Muted, FontFamily = Mono, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "F1: documentation", Foreground = Palette.Muted, FontSize = 10.5 });
        return Card(panel);
    }

    private static Control Card(Control panel)
    {
        return new Border
        {
            Background = Palette.Panel,
            BorderBrush = Palette.Border,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(10, 8),
            BoxShadow = Avalonia.Media.BoxShadows.Parse("0 4 12 0 #60000000"),
            Child = panel,
        };
    }
}

/// <summary>Kalo's palette for controls built in code (same values as App.axaml).</summary>
internal static class Palette
{
    public static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#2B2D30"));
    public static readonly IBrush Text = new SolidColorBrush(Color.Parse("#DFE1E5"));
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9DA0A8"));
    public static readonly IBrush Code = new SolidColorBrush(Color.Parse("#9CDCFE"));
    public static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#1E1F22"));
    public static readonly IBrush Border = new SolidColorBrush(Color.Parse("#43454A"));
}
