using System.Xml;
using Avalonia.Controls;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using Kalo.App.ViewModels;

namespace Kalo.App.Views;

/// <summary>The text editor of a campaign mission script, with HaloScript colors.</summary>
public partial class MissionScriptView : UserControl
{
    private MissionScriptViewModel? _vm;
    private bool _syncing;
    private static IHighlightingDefinition? _hs;

    public MissionScriptView()
    {
        InitializeComponent();
        Editor.SyntaxHighlighting = HaloScript();
        Editor.Options.ConvertTabsToSpaces = false;
        Editor.Options.IndentationSize = 4;
        Editor.TextChanged += (_, _) =>
        {
            if (_syncing || _vm == null) return;
            _vm.Text = Editor.Text;
        };
        DataContextChanged += (_, _) => Attach(DataContext as MissionScriptViewModel);
    }

    private void Attach(MissionScriptViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.RevealRequested -= Reveal;
            _vm.PropertyChanged -= OnVmChanged;
        }
        _vm = vm;
        _syncing = true;
        // A new document rather than replacing the text (see ScriptEditorView: hidden editors keep stale line visuals).
        Editor.Document = new TextDocument(vm?.Text ?? "");
        _syncing = false;
        if (vm != null)
        {
            vm.RevealRequested += Reveal;
            vm.PropertyChanged += OnVmChanged;
        }
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Text loaded or set from outside the editor: show it.
        if (e.PropertyName == nameof(MissionScriptViewModel.Text) && _vm != null && Editor.Text != _vm.Text)
        {
            _syncing = true;
            Editor.Document = new TextDocument(_vm.Text);
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
