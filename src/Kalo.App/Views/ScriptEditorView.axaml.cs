using System.Xml;
using Avalonia.Controls;
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
