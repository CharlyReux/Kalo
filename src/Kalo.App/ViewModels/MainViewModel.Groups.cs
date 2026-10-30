using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kalo.App.ViewModels;

/// <summary>
/// Split editors: a primary and a secondary editor group (beside, below, or in a detached window).
/// Tabs stays the list of every open document; each one also sits in one group. ActiveDocument is the
/// focused group's document, so the inspector, the shortcuts and Save follow the group you last clicked.
/// </summary>
public partial class MainViewModel
{
    public EditorGroup Primary { get; private set; } = null!;
    public EditorGroup Secondary { get; private set; } = null!;
    public IReadOnlyList<EditorGroup> Groups => new[] { Primary, Secondary };

    [ObservableProperty] private EditorGroup _focusedGroup = null!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSplit), nameof(IsDetached), nameof(SplitText))]
    private SplitLayout _split;

    public bool IsSplit => Split != SplitLayout.None;
    public bool IsDetached => Split == SplitLayout.Window;
    public string SplitText => Split switch
    {
        SplitLayout.Right => "Split: side by side",
        SplitLayout.Down => "Split: top and bottom",
        SplitLayout.Window => "Split: second window",
        _ => "",
    };

    private void InitGroups()
    {
        Primary = new EditorGroup(this, 0);
        Secondary = new EditorGroup(this, 1);
        _focusedGroup = Primary;
        Primary.IsFocused = true;
        Tabs.CollectionChanged += OnTabsChanged;
    }

    /// <summary>The group a document is in, or null.</summary>
    public EditorGroup? EditorGroupOf(DocumentViewModel doc) =>
        Primary.Tabs.Contains(doc) ? Primary : Secondary.Tabs.Contains(doc) ? Secondary : null;

    public EditorGroup Other(EditorGroup g) => ReferenceEquals(g, Primary) ? Secondary : Primary;

    /// <summary>New documents open in the focused group; closed ones leave theirs.</summary>
    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var g in Groups) { g.Tabs.Clear(); g.Current = null; }
            return;
        }
        if (e.OldItems != null)
            foreach (DocumentViewModel doc in e.OldItems)
                EditorGroupOf(doc)?.Remove(doc);
        if (e.NewItems != null)
            foreach (DocumentViewModel doc in e.NewItems)
            {
                if (EditorGroupOf(doc) != null) continue;
                var target = IsSplit ? FocusedGroup : Primary;
                target.Tabs.Add(doc);
            }
    }

    partial void OnFocusedGroupChanged(EditorGroup oldValue, EditorGroup newValue)
    {
        if (oldValue != null) oldValue.IsFocused = false;
        newValue.IsFocused = true;
    }

    partial void OnSplitChanged(SplitLayout value)
    {
        foreach (var g in Groups) g.IsSplit = value != SplitLayout.None;
    }

    /// <summary>Makes a group the focused one (a click or a key in it): the inspector follows its document.</summary>
    public void FocusGroup(EditorGroup group)
    {
        if (!IsSplit && !group.IsPrimary) return;
        if (ReferenceEquals(FocusedGroup, group) && ReferenceEquals(ActiveDocument, group.Current)) return;
        FocusedGroup = group;
        ShowActive(group.Current);
    }

    /// <summary>Points ActiveDocument at a document (or nothing), refreshing the inspector even when it is unchanged.</summary>
    private void ShowActive(DocumentViewModel? doc)
    {
        if (ReferenceEquals(ActiveDocument, doc)) RaiseActiveDocument();
        else ActiveDocument = doc;
    }

    /// <summary>ActiveDocument changed (opened, clicked, closed): show it in its group and focus that group.</summary>
    private void PlaceActive(DocumentViewModel? doc)
    {
        if (doc == null) return;
        var g = EditorGroupOf(doc);
        if (g == null)
        {
            g = IsSplit ? FocusedGroup : Primary;
            g.Tabs.Add(doc);
        }
        g.Current = doc;
        if (!ReferenceEquals(FocusedGroup, g)) FocusedGroup = g;
    }

    /// <summary>Splits the editor area. "right", "down", "window" or "none" (merge back into one group).</summary>
    [RelayCommand]
    private void SetSplit(string? mode)
    {
        var layout = mode?.ToLowerInvariant() switch
        {
            "right" => SplitLayout.Right,
            "down" => SplitLayout.Down,
            "window" => SplitLayout.Window,
            _ => SplitLayout.None,
        };
        if (layout == SplitLayout.None) { Unsplit(); return; }
        bool wasSplit = IsSplit;
        // First split: the active document goes to the new group (when another one stays behind).
        // Moved before the layout changes, so the window sizes the two sides for what they show.
        DocumentViewModel? moved = null;
        if (!wasSplit && Secondary.Tabs.Count == 0 && ActiveDocument is { } doc && Primary.Tabs.Count > 1)
        {
            Primary.Remove(doc);
            Secondary.Tabs.Add(doc);
            Secondary.Current = doc;
            moved = doc;
        }
        Split = layout;
        if (moved != null)
        {
            FocusedGroup = Secondary;
            ShowActive(moved);
        }
        else if (!wasSplit)
            FocusGroup(Secondary.Tabs.Count > 0 ? Secondary : Primary);
    }

    /// <summary>One group again: the secondary group's tabs join the primary one.</summary>
    [RelayCommand]
    public void Unsplit()
    {
        var keep = ActiveDocument;
        foreach (var doc in Secondary.Tabs.ToList())
        {
            Secondary.Remove(doc);
            Primary.Tabs.Add(doc);
        }
        Secondary.Current = null;
        Split = SplitLayout.None;
        FocusedGroup = Primary;
        if (keep != null && Primary.Tabs.Contains(keep)) Primary.Current = keep;
        else if (Primary.Current == null && Primary.Tabs.Count > 0) Primary.Current = Primary.Tabs[^1];
        ShowActive(Primary.Current);
    }

    /// <summary>Moves a tab to the other group (splitting side by side first if needed).</summary>
    [RelayCommand]
    public void MoveToOtherGroup(DocumentViewModel? doc)
    {
        doc ??= ActiveDocument;
        if (doc == null) return;
        if (!IsSplit) Split = SplitLayout.Right;
        var from = EditorGroupOf(doc) ?? Primary;
        MoveTab(doc, Other(from));
    }

    /// <summary>Moves a tab into a group and shows it there. An emptied secondary group closes.</summary>
    public void MoveTab(DocumentViewModel doc, EditorGroup to)
    {
        var from = EditorGroupOf(doc);
        if (ReferenceEquals(from, to)) { to.Current = doc; FocusGroup(to); return; }
        if (!to.IsPrimary && !IsSplit) Split = SplitLayout.Right;
        from?.Remove(doc);
        to.Tabs.Add(doc);
        to.Current = doc;
        FocusedGroup = to;
        ShowActive(doc);
        if (from != null && !from.IsPrimary && from.Tabs.Count == 0) Unsplit();
    }

    /// <summary>Swaps the two groups' tabs (e.g. to put the code on the left).</summary>
    [RelayCommand]
    private void SwapGroups()
    {
        if (!IsSplit) return;
        var a = Primary.Tabs.ToList();
        var b = Secondary.Tabs.ToList();
        var (ca, cb) = (Primary.Current, Secondary.Current);
        bool focusedPrimary = FocusedGroup.IsPrimary;
        Primary.Current = null; Secondary.Current = null;
        Primary.Tabs.Clear(); Secondary.Tabs.Clear();
        foreach (var d in b) Primary.Tabs.Add(d);
        foreach (var d in a) Secondary.Tabs.Add(d);
        Primary.Current = cb; Secondary.Current = ca;
        // Keep the focus on the same document, now on the other side.
        FocusedGroup = focusedPrimary ? Secondary : Primary;
        ShowActive(FocusedGroup.Current);
    }

    /// <summary>The detached window was closed: its tabs come back.</summary>
    public void OnDetachedWindowClosed()
    {
        if (Split == SplitLayout.Window) Unsplit();
    }

    private void RaiseActiveDocument()
    {
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(ActiveTab));
        OnPropertyChanged(nameof(ActiveMap));
        OnPropertyChanged(nameof(ActiveScript));
        OnPropertyChanged(nameof(ActiveDiff));
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(HasTab));
        OnPropertyChanged(nameof(HasMap));
        OnPropertyChanged(nameof(HasScript));
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(InspectorTitle));
        OnPropertyChanged(nameof(NoTab));
    }
}
