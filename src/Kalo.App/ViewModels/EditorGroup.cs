using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kalo.App.ViewModels;

/// <summary>How the editor area is split.</summary>
public enum SplitLayout
{
    /// <summary>One editor group.</summary>
    None,
    /// <summary>Two groups side by side.</summary>
    Right,
    /// <summary>Two groups, one above the other.</summary>
    Down,
    /// <summary>The second group lives in its own window (e.g. code on another screen).</summary>
    Window,
}

/// <summary>
/// One editor group: its own tabs and the document it shows. The main window has a primary group and,
/// when split, a secondary one (beside, below, or in a detached window). Every open document is in
/// exactly one group; MainViewModel.Tabs stays the list of all of them.
/// </summary>
public partial class EditorGroup : ObservableObject
{
    public EditorGroup(MainViewModel main, int id)
    {
        Main = main;
        Id = id;
    }

    public MainViewModel Main { get; }
    public int Id { get; }
    public bool IsPrimary => Id == 0;
    public ObservableCollection<DocumentViewModel> Tabs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTag), nameof(CurrentMap), nameof(CurrentScript), nameof(CurrentDiff),
        nameof(HasTag), nameof(HasMap), nameof(HasScript), nameof(HasDiff), nameof(ShowStart), nameof(ShowEmpty))]
    private DocumentViewModel? _current;

    /// <summary>The group the keyboard and the inspector follow.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFocusMark))]
    private bool _isFocused;

    /// <summary>True while the editor area is split.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFocusMark))]
    private bool _isSplit;

    /// <summary>The accent line over the focused group's tabs (only when split).</summary>
    public bool ShowFocusMark => IsFocused && IsSplit;

    public TagEditorViewModel? CurrentTag => Current as TagEditorViewModel;
    public MapEditorViewModel? CurrentMap => Current as MapEditorViewModel;
    public ScriptEditorViewModel? CurrentScript => Current as ScriptEditorViewModel;
    public DiffViewModel? CurrentDiff => Current as DiffViewModel;
    public bool HasTag => CurrentTag != null;
    public bool HasMap => CurrentMap != null;
    public bool HasScript => CurrentScript != null;
    public bool HasDiff => CurrentDiff != null;
    /// <summary>The start page shows in the primary group when it has nothing open.</summary>
    public bool ShowStart => IsPrimary && Current == null;
    /// <summary>The secondary group with nothing open shows a hint.</summary>
    public bool ShowEmpty => !IsPrimary && Current == null;

    partial void OnCurrentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        // IsActive = the tab is the one shown in its group (highlighted in the tab strip).
        if (oldValue != null && !ReferenceEquals(oldValue, newValue)) oldValue.IsActive = false;
        if (newValue != null) newValue.IsActive = true;
    }

    /// <summary>Removes a tab; if it was shown, shows its left neighbour.</summary>
    public void Remove(DocumentViewModel doc)
    {
        int i = Tabs.IndexOf(doc);
        if (i < 0) return;
        Tabs.RemoveAt(i);
        if (ReferenceEquals(Current, doc)) Current = Tabs.Count == 0 ? null : Tabs[Math.Clamp(i - 1, 0, Tabs.Count - 1)];
    }
}
