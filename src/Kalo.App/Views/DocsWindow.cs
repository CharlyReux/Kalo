using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Kalo.App.ViewModels;
using Kalo.Core.Scripting;

namespace Kalo.App.Views;

/// <summary>
/// Help › Kalo Script documentation: the guide to the language and a reference of every action, condition and value,
/// with search. One window, reused; F1 in a script opens it at the word under the caret.
/// </summary>
public sealed class DocsWindow : Window
{
    private static DocsWindow? s_open;

    private readonly MainViewModel _vm;
    private IReadOnlyList<GuideSection> _sections = Array.Empty<GuideSection>();
    private readonly TextBox _search;
    private readonly ListBox _nav;
    private readonly StackPanel _page;
    private readonly ScrollViewer _scroll;
    private bool _updatingNav;

    /// <summary>The section shown (for tests).</summary>
    public GuideSection? Current { get; private set; }
    public string Query => _search.Text ?? "";

    /// <summary>Opens (or brings back) the documentation, at a topic: a section id, an action / condition / keyword, or a search.</summary>
    public static DocsWindow Open(Window owner, MainViewModel vm, string? topic = null)
    {
        if (s_open == null)
        {
            s_open = new DocsWindow(vm);
            s_open.Closed += (_, _) => s_open = null;
            s_open.Show(owner);
        }
        else s_open.Activate();
        s_open.GoTo(topic);
        return s_open;
    }

    private static IBrush Brush(string key) =>
        Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");

    private DocsWindow(MainViewModel vm)
    {
        _vm = vm;
        Title = "Kalo help";
        Width = 1120;
        Height = 800;
        MinWidth = 640;
        MinHeight = 400;
        Background = Brush("KBg");
        DataContext = vm;

        _search = new TextBox { Watermark = "Search: weapon, squad, timer, score, create_object...", Margin = new Thickness(8), Classes = { "compact" } };
        _search.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Refresh(); };
        _nav = new ListBox { Background = Brushes.Transparent, ItemTemplate = new FuncDataTemplate<NavRow>((row, _) => NavCell(row)) };
        _nav.SelectionChanged += (_, _) =>
        {
            if (_updatingNav) return;
            if (_nav.SelectedItem is NavRow { Section: { } s }) Show(s);
        };
        var left = new DockPanel { Background = Brush("KPanel") };
        DockPanel.SetDock(_search, Dock.Top);
        left.Children.Add(_search);
        left.Children.Add(_nav);

        _page = new StackPanel { Spacing = 10, Margin = new Thickness(28, 20, 28, 40), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        _scroll = new ScrollViewer { Content = _page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("280,1,*") };
        grid.Children.Add(left);
        var line = new Border { Background = Brush("KBorder") };
        Grid.SetColumn(line, 1);
        grid.Children.Add(line);
        Grid.SetColumn(_scroll, 2);
        grid.Children.Add(_scroll);
        Content = grid;

        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F, KeyModifiers.Control), Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { _search.Focus(); _search.SelectAll(); }) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new CommunityToolkit.Mvvm.Input.RelayCommand(Close) });

        _sections = KaloRecipes.All().Concat(KaloScriptGuide.Build(vm.MegaloCatalog)).ToList();
        Refresh();
        // The reference gets the compiler's argument lists once the gametype compiler is up.
        if (vm.MegaloCatalog == null)
            _ = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try { await vm.EnsureMegaloCatalogAsync(); } catch { return; }
                if (vm.MegaloCatalog == null) return;
                string? id = Current?.Id;
                _sections = KaloRecipes.All().Concat(KaloScriptGuide.Build(vm.MegaloCatalog)).ToList();
                Refresh();
                if (id != null && string.IsNullOrWhiteSpace(Query)) GoTo(id);
            });
    }

    private sealed record NavRow(string Text, GuideSection? Section);

    private Control NavCell(NavRow? row) => row == null ? new TextBlock() : row.Section == null
        ? new TextBlock { Text = row.Text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush("KMuted"), Margin = new Thickness(0, 10, 0, 2) }
        : new TextBlock { Text = row.Text, FontSize = 13, Foreground = Brush("KText"), Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };

    private string[] Words => (Query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool Matches(string text, string[] words) => words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Rebuilds the list (filtered by the search) and the page (search results, or the current section).</summary>
    private void Refresh()
    {
        var words = Words;
        var shown = words.Length == 0 ? _sections : _sections.Where(s => Matches(s.SearchText, words)).ToList();
        var rows = new List<NavRow>();
        foreach (var group in shown.GroupBy(s => s.Group))
        {
            rows.Add(new NavRow(group.Key, null));
            rows.AddRange(group.Select(s => new NavRow(s.Title, s)));
        }
        _updatingNav = true;
        _nav.ItemsSource = rows;
        _nav.SelectedItem = rows.FirstOrDefault(r => r.Section != null && r.Section.Id == Current?.Id);
        _updatingNav = false;
        if (words.Length > 0) ShowResults(shown, words);
        else Show(Current ?? _sections.FirstOrDefault());
    }

    /// <summary>Shows a section by id, or the page of an action / condition / keyword, or searches.</summary>
    public void GoTo(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) { if (Current == null) Show(_sections.FirstOrDefault()); return; }
        var byId = _sections.FirstOrDefault(s => s.Id == topic);
        if (byId != null) { _search.Text = ""; Show(byId); return; }
        var withEntry = _sections.FirstOrDefault(s => s.Blocks.Any(b => b.Kind == GuideKind.Entry && b.Title == topic));
        string? guide = topic switch
        {
            "if" or "then" or "else" or "elseif" or "and" or "or" or "not" => "if",
            "for" or "each" or "do" or "on" or "init" or "inline" or "with" or "random" => "triggers",
            "temp" or "declare" or "alias" or "networked" or "local" => "variables",
            "function" => "functions",
            "megalo" or "include" or "setting" or "option" or "traits" or "objects" or "constant" or "widget" or "stat" or "engine"
                or "teams" or "loadout" or "hidden" or "locked" or "value" or "range" => "declarations",
            _ => null,
        };
        if (guide != null) { _search.Text = ""; Show(_sections.First(s => s.Id == guide)); return; }
        if (withEntry != null) { _search.Text = ""; Show(withEntry, topic); return; }
        _search.Text = topic;
    }

    private void Show(GuideSection? section, string? entry = null)
    {
        if (section == null) return;
        Current = section;
        _page.Children.Clear();
        _page.Children.Add(new TextBlock { Text = section.Group.ToUpperInvariant(), FontSize = 11, Foreground = Brush("KMuted"), FontWeight = FontWeight.SemiBold });
        _page.Children.Add(new TextBlock { Text = section.Title, FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = Brush("KText"), Margin = new Thickness(0, -4, 0, 6) });
        Control? target = null;
        foreach (var b in section.Blocks)
        {
            var c = Render(b);
            if (entry != null && b.Kind == GuideKind.Entry && b.Title == entry) { target = c; if (c is Border border) border.BorderBrush = Brush("KAccent"); }
            _page.Children.Add(c);
        }
        _updatingNav = true;
        if (_nav.ItemsSource is IEnumerable<NavRow> rows) _nav.SelectedItem = rows.FirstOrDefault(r => r.Section?.Id == section.Id);
        _updatingNav = false;
        _scroll.Offset = default;
        if (target != null) Dispatcher.UIThread.Post(() => target.BringIntoView(), DispatcherPriority.Background);
    }

    private void ShowResults(IReadOnlyList<GuideSection> sections, string[] words)
    {
        Current = null;
        _page.Children.Clear();
        int count = 0;
        _page.Children.Add(new TextBlock { Text = $"Search: {Query}", FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Brush("KText") });
        foreach (var s in sections)
        {
            var hits = s.Blocks.Where(b => b.Kind != GuideKind.Heading && Matches($"{b.Title} {b.Text} {b.Signature} {b.Example}", words)).ToList();
            if (hits.Count == 0) continue;
            var open = new Button { Content = $"{s.Group} › {s.Title}", Classes = { "tool" }, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) };
            var section = s;
            open.Click += (_, _) => { _search.Text = ""; Show(section); };
            _page.Children.Add(open);
            foreach (var b in hits.Take(30)) { _page.Children.Add(Render(b)); count++; }
        }
        if (count == 0) _page.Children.Add(new TextBlock { Text = "Nothing found. Try another word (an action name, \"timer\", \"score\"...).", Foreground = Brush("KMuted") });
        _scroll.Offset = default;
    }

    private Control Render(GuideBlock b)
    {
        switch (b.Kind)
        {
            case GuideKind.Heading:
                return new TextBlock { Text = b.Text, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brush("KText"), Margin = new Thickness(0, 12, 0, 0) };
            case GuideKind.Text:
                return new SelectableTextBlock { Text = b.Text, TextWrapping = TextWrapping.Wrap, Foreground = Brush("KText"), FontSize = 13.5, LineHeight = 21 };
            case GuideKind.Note:
                return new Border
                {
                    Background = Brush("KPanel"), BorderBrush = Brush("KAccent"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 8),
                    Child = new SelectableTextBlock { Text = b.Text, TextWrapping = TextWrapping.Wrap, Foreground = Brush("KText"), FontSize = 13 },
                };
            case GuideKind.Code:
            case GuideKind.Program:
                return CodeBlock(b.Text);
            default:
                var panel = new StackPanel { Spacing = 4 };
                panel.Children.Add(new SelectableTextBlock { Text = b.Title, FontFamily = Mono, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brush("KBlue") });
                if (!string.IsNullOrEmpty(b.Signature))
                    panel.Children.Add(new SelectableTextBlock { Text = b.Signature, FontFamily = Mono, FontSize = 12.5, Foreground = Brush("KCode"), TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(new SelectableTextBlock { Text = b.Text, TextWrapping = TextWrapping.Wrap, Foreground = Brush("KText"), FontSize = 13 });
                if (!string.IsNullOrEmpty(b.Example)) panel.Children.Add(CodeBlock(b.Example));
                if (!string.IsNullOrEmpty(b.Megalo))
                    panel.Children.Add(new SelectableTextBlock { Text = $"Megalo: {b.Title} {b.Megalo}", FontFamily = Mono, FontSize = 11.5, Foreground = Brush("KFaint"), TextWrapping = TextWrapping.Wrap });
                return new Border
                {
                    Background = Brush("KPanel"), BorderBrush = Brush("KBorder"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(12, 10), Child = panel,
                };
        }
    }

    /// <summary>Code in a dark box, selectable, with a Copy button.</summary>
    private Control CodeBlock(string code)
    {
        var copy = new Button { Content = "Copy", Classes = { "tool" }, FontSize = 11, Padding = new Thickness(6, 2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        copy.Click += async (_, _) =>
        {
            if (Clipboard != null) await Clipboard.SetTextAsync(code);
            copy.Content = "Copied";
        };
        var grid = new Grid();
        grid.Children.Add(new SelectableTextBlock { Text = code, FontFamily = Mono, FontSize = 12.5, Foreground = Brush("KCode"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 50, 0) });
        grid.Children.Add(copy);
        return new Border { Background = Brush("KPanelAlt"), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 8), Child = grid };
    }
}
