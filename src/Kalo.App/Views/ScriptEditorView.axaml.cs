using System.Xml;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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
        Editor.TextArea.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                ShowCompletion(true);
                e.Handled = true;
            }
        };
    }

    private CompletionWindow? _completion;

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
        Editor.Text = vm?.Text ?? "";
        _syncing = false;
        if (vm != null)
        {
            vm.PropertyChanged += (_, e) =>
            {
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
    public object Content => item.Text + "   · " + item.Kind;
    public object? Description => string.IsNullOrEmpty(item.Description) ? item.Kind : item.Text + " " + item.Description;
    public double Priority => item.Kind is "action" or "condition" ? 1 : 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, Text);
}
