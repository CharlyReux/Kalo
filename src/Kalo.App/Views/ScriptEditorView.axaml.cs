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
            if (char.IsLetterOrDigit(c) || c == '_' || c == ' ') ShowCompletion(false);
        };
        // Hover help: after a short pause over a word, a popup explains it.
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); ShowHover(); };
        Editor.TextArea.AddHandler(PointerMovedEvent, (_, e) =>
        {
            _hoverPoint = e.GetPosition(Editor);
            var word = WordAt(_hoverPoint.Value);
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
        if (info == null) return;
        _hoverWord = WordAt(_hoverPoint.Value);
        HoverPopup.Child = HoverCard.Build(info);
        HoverPopup.IsOpen = false;
        HoverPopup.IsOpen = true;
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
        if (!explicitRequest && before.EndsWith(' ') && !before.TrimEnd().EndsWith("action") && !before.TrimEnd().EndsWith("condition")) return;
        var (start, items) = _vm.Complete(before, explicitRequest);
        if (items.Count == 0) return;
        _completion = new CompletionWindow(Editor.TextArea) { StartOffset = line.Offset + start, Width = 420 };
        _completion.CompletionList.Background = Palette.Panel;
        _completion.CompletionList.Foreground = Palette.Text;
        foreach (var item in items) _completion.CompletionList.CompletionData.Add(new MegaloCompletion(item));
        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
    }

    private void Attach(ScriptEditorViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.RevealRequested -= Reveal;
            _vm.InsertRequested -= Insert;
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
    <Span color='String'><Begin>""</Begin><End>""</End></Span>
    <Span color='Raw' multiline='true'><Begin>\[\[</Begin><End>\]\]</End></Span>
    <Keywords color='Section'>
      <Word>declare</Word><Word>alias</Word><Word>megalo</Word><Word>include</Word><Word>on</Word><Word>setting</Word><Word>option</Word>
      <Word>traits</Word><Word>objects</Word><Word>constant</Word><Word>widget</Word><Word>stat</Word><Word>engine</Word><Word>teams</Word><Word>loadout</Word>
      <Word>hidden</Word><Word>locked</Word><Word>value</Word><Word>range</Word>
    </Keywords>
    <Keywords color='Keyword'>
      <Word>if</Word><Word>then</Word><Word>else</Word><Word>elseif</Word><Word>end</Word><Word>for</Word><Word>each</Word><Word>do</Word><Word>inline</Word>
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
internal sealed class MegaloCompletion(CompletionItem item) : ICompletionData
{
    public IImage? Image => null;
    public string Text => item.Text;
    // Controls with explicit colors: the popup lives outside the window and does not inherit its text color.
    public object Content => new StackPanel
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Spacing = 10,
        Children =
        {
            new TextBlock { Text = item.Text, Foreground = Palette.Text, FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace") },
            new TextBlock { Text = item.Kind, Foreground = Palette.Muted, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
        },
    };
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

    public static Control Build(HoverInfo info)
    {
        var panel = new StackPanel { Spacing = 5, MaxWidth = 520 };
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
