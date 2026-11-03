using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>An open tag in an editor tab.</summary>
public partial class TagEditorViewModel : DocumentViewModel
{
    private readonly MainViewModel _main;
    public MainViewModel Main => _main;

    public string Group => System.IO.Path.GetExtension(Path).TrimStart('.');
    public ObservableCollection<FieldViewModel> Fields { get; } = new();

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _showRaw;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _changedOnly;
    [ObservableProperty] private object? _selectedItem;
    [ObservableProperty] private string _referenceQuery = "";
    public ObservableCollection<string> ReferenceResults { get; } = new();
    /// <summary>Blocks were added to or removed from since the last save.</summary>
    private bool _structureChanged;

    // ---- Use in scripts ------------------------------------------------------------------------------
    /// <summary>The name scripts create this object with (create_object("name", ...)), or null.</summary>
    [ObservableProperty] private string? _scriptName;
    [ObservableProperty] private string? _whyNotRemovable;
    private ScriptObjectEntry? _scriptEntry;
    public bool IsObjectTag => ScriptObjects.IsObjectTag(Path);
    public bool ShowUseInScripts => IsObjectTag && ScriptName == null;
    public bool ShowScriptName => ScriptName != null;
    public string ScriptNameText => $"In scripts as {ScriptName}";
    public bool CanRemoveScriptName => ScriptName != null && WhyNotRemovable == null;
    public string RemoveScriptNameTip => WhyNotRemovable ?? "Scripts can no longer create this object (the object itself stays).";
    public ScriptObjectEntry? ScriptEntry => _scriptEntry;

    partial void OnScriptNameChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowUseInScripts));
        OnPropertyChanged(nameof(ShowScriptName));
        OnPropertyChanged(nameof(ScriptNameText));
        OnPropertyChanged(nameof(CanRemoveScriptName));
    }

    partial void OnWhyNotRemovableChanged(string? value)
    {
        OnPropertyChanged(nameof(CanRemoveScriptName));
        OnPropertyChanged(nameof(RemoveScriptNameTip));
    }

    public async Task RefreshScriptNameAsync()
    {
        if (!IsObjectTag) return;
        var r = await _main.GetScriptObjectsAsync();
        _scriptEntry = r?.Entries.LastOrDefault(e => e.ObjectTag.Equals(Path, StringComparison.OrdinalIgnoreCase));
        WhyNotRemovable = _scriptEntry == null || r == null ? null : _main.WhyNotRemovable(_scriptEntry, r.Entries.Count);
        ScriptName = _scriptEntry?.Name;
    }

    [RelayCommand]
    private async Task RemoveScriptName()
    {
        if (_scriptEntry == null) return;
        if (await _main.RemoveScriptObjectAsync(_scriptEntry) is { } why) _main.AddProblem("error", "Remove from scripts: " + why, Path);
        else Status = "Scripts can no longer create this object";
    }

    public FieldViewModel? SelectedField => SelectedItem as FieldViewModel;
    public bool ShowLabeled
    {
        get => !ShowRaw && !ShowSimple;
        set
        {
            if (value) { ShowSimple = false; ShowRaw = false; }
            OnPropertyChanged(nameof(ShowLabeled));
        }
    }

    // ---- Simple view: the fields most people change, with plain names -----------------------------------
    /// <summary>Weapons, projectiles, damage, vehicles and characters have a Simple view; others show all fields.</summary>
    public SimpleView? Simple => IsScoped ? null : SimpleViews.For(Group);
    public bool HasSimpleView => Simple != null;
    [ObservableProperty] private bool _showSimple;
    public string SimpleIntro => Simple?.Intro ?? "";
    public SimpleField? SimpleFor(FieldViewModel f) => ShowSimple ? Simple?.For(f.Path) : null;

    partial void OnShowSimpleChanged(bool value)
    {
        if (value && ShowRaw) ShowRaw = false;
        OnPropertyChanged(nameof(ShowLabeled));
        foreach (var f in AllFields()) f.RefreshMode();
        if (!value) CollapseToDefault();
        ApplyFilter();
    }

    /// <summary>Back to all fields: the first level of structs open, like when the file was opened.</summary>
    private void CollapseToDefault()
    {
        foreach (var f in AllFields()) f.IsExpanded = false;
        foreach (var f in Fields.Where(f => f.Kind == ValueKinds.Struct)) f.IsExpanded = true;
    }

    public TagEditorViewModel(MainViewModel main, string path) : base(path)
    {
        _main = main;
        Status = "Loading…";
    }

    /// <summary>
    /// A tab showing one element of a big tag's block (e.g. one squad of a campaign scenario) instead of the whole tag.
    /// Edits and saves go to the tag itself.
    /// </summary>
    public TagEditorViewModel(MainViewModel main, string path, string block, int index, string label) : this(main, path)
    {
        ScopeBlock = block;
        ScopeIndex = index;
        ScopeLabel = label;
    }

    public string? ScopeBlock { get; }
    public int ScopeIndex { get; }
    public string? ScopeLabel { get; }
    public bool IsScoped => ScopeBlock != null;
    public override string Title => IsScoped ? $"{System.IO.Path.GetFileNameWithoutExtension(Path)} · {ScopeLabel}" : base.Title;
    public override string Breadcrumb => IsScoped ? base.Breadcrumb + $" › {ScopeBlock}[{ScopeIndex}]" : base.Breadcrumb;

    public override Task SaveDocumentAsync() => SaveCommand.ExecuteAsync(null);

    public override async Task CloseAsync()
    {
        try { if (_main.Host != null) await _main.Host.CloseTagAsync(Path); } catch { }
    }

    public void Load(TagDocument doc)
    {
        Fields.Clear();
        _structureChanged = false;
        IsDirty = false;
        foreach (var node in doc.Fields ?? new List<FieldNode>()) Fields.Add(new FieldViewModel(this, node, null));
        // Open the first level of structs so the important fields are visible.
        foreach (var f in Fields.Where(f => f.Kind == ValueKinds.Struct)) f.IsExpanded = true;
        IsLoading = false;
        if (HasSimpleView && !ShowRaw) _showSimple = true;
        OnPropertyChanged(nameof(ShowSimple));
        OnPropertyChanged(nameof(ShowLabeled));
        foreach (var f in AllFields()) f.RefreshMode();
        Status = $"{doc.FieldCount:N0} fields · loaded in {doc.LoadMs} ms" + (IsScoped ? $" · only {ScopeLabel} ({ScopeBlock}[{ScopeIndex}])" : "");
        ApplyFilter();
    }

    public void Fail(string message)
    {
        IsLoading = false;
        Status = "Could not open: " + message;
    }

    partial void OnSelectedItemChanged(object? value)
    {
        OnPropertyChanged(nameof(SelectedField));
        ReferenceQuery = "";
        UpdateReferenceResults();
    }

    partial void OnReferenceQueryChanged(string value) => UpdateReferenceResults();

    /// <summary>Tags the selected reference field can point to, filtered by the query words.</summary>
    private void UpdateReferenceResults()
    {
        ReferenceResults.Clear();
        if (SelectedField is not { IsReference: true } f) return;
        string[] words = ReferenceQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allowed = f.Allowed.Select(a => "." + a).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string p in _main.TagIndex)
        {
            if (allowed.Count > 0 && !allowed.Contains(System.IO.Path.GetExtension(p))) continue;
            if (words.Length > 0 && !words.All(w => p.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            ReferenceResults.Add(p);
            if (ReferenceResults.Count >= 150) break;
        }
    }

    [RelayCommand]
    private async Task PickReference(string? tagPath)
    {
        if (SelectedField is not { IsReference: true } f || tagPath == null) return;
        await CommitAsync(f, tagPath);
    }

    [RelayCommand]
    private async Task ClearReference()
    {
        if (SelectedField is { IsReference: true } f) await CommitAsync(f, "");
    }

    [RelayCommand]
    private async Task OpenReference()
    {
        if (SelectedField is { HasReferenceTarget: true } f) await _main.OpenTagAsync(f.Value!);
    }

    /// <summary>Adds, inserts, duplicates or removes block elements, then rebuilds that block's rows.</summary>
    public async Task BlockOpAsync(FieldViewModel block, string op, int index)
    {
        if (_main.Host == null) return;
        try
        {
            BlockOpResult r = await _main.Host.BlockOpAsync(Path, block.Path, op, index);
            if (r.Block == null) return;
            var fresh = new FieldViewModel(this, r.Block, block.Parent) { IsExpanded = true };
            var siblings = block.Parent?.Children ?? Fields;
            int at = siblings.IndexOf(block);
            if (at >= 0) siblings[at] = fresh;
            _structureChanged = true;
            IsDirty = true;
            ApplyFilter();
            var target = r.SelectIndex >= 0 && r.SelectIndex < fresh.Children.Count ? fresh.Children[r.SelectIndex] : fresh;
            Reveal(target);
            _main.Log($"{Title}: {block.Label} {op} → {r.Block.Value}");
        }
        catch (HostException ex)
        {
            _main.AddProblem("error", $"{block.Label}: {ex.Message}", Path);
            if (ex.EngineHalted) await _main.RestartHostAsync();
        }
    }
    partial void OnShowRawChanged(bool value)
    {
        if (value && ShowSimple) { _showSimple = false; OnPropertyChanged(nameof(ShowSimple)); CollapseToDefault(); }
        OnPropertyChanged(nameof(ShowLabeled));
        foreach (var f in AllFields()) f.RefreshMode();
        ApplyFilter();
    }
    private int _filterGeneration;

    /// <summary>Filters shortly after typing stops: a big tag has tens of thousands of fields.</summary>
    partial void OnFilterChanged(string value)
    {
        int generation = ++_filterGeneration;
        _ = Task.Delay(250).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (generation == _filterGeneration) ApplyFilter();
        }));
    }
    partial void OnChangedOnlyChanged(bool value) => ApplyFilter();

    public async Task CommitAsync(FieldViewModel field, string value)
    {
        if (_main.Host == null) { field.Reject("The Engine is not running."); return; }
        try
        {
            SetFieldResult result = await _main.Host.SetFieldAsync(Path, field.Path, value);
            field.Accept(result.Value ?? value);
            IsDirty = _structureChanged || AllFields().Any(f => f.IsDirty);
            _main.Log($"{Title}: {field.Label} = {result.Value}");
        }
        catch (HostException ex)
        {
            field.Reject(ex.Message);
            _main.AddProblem("error", $"{field.Label}: {ex.Message}", Path);
            if (ex.EngineHalted) await _main.RestartHostAsync();
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_main.Host == null) return;
        try
        {
            SaveResult r = await _main.Host.SaveTagAsync(Path);
            foreach (var f in Fields) f.MarkSaved();
            _structureChanged = false;
            IsDirty = false;
            Status = $"Saved in {r.Ms} ms";
            _main.OnTagSaved(this);
        }
        catch (HostException ex)
        {
            _main.AddProblem("error", "Save failed: " + ex.Message, Path);
        }
    }

    [RelayCommand]
    private async Task Compare() => await _main.CompareWithVanillaAsync(@"tags\" + Path);

    [RelayCommand]
    private async Task RevertToVanillaAsync() => await _main.RevertTagAsync(this);

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var f in AllFields()) if (f.IsContainer) f.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var f in AllFields()) f.IsExpanded = false;
    }

    public IEnumerable<FieldViewModel> AllFields() => Fields.SelectMany(f => f.SelfAndDescendants());

    public FieldViewModel? FindField(string path) => AllFields().FirstOrDefault(f => f.Path == path);

    public void Reveal(FieldViewModel field)
    {
        for (var p = field.Parent; p != null; p = p.Parent) p.IsExpanded = true;
        SelectedItem = field;
    }

    /// <summary>Labeled view hides engine internals; the filter matches labels and raw names; parents of matches stay visible.</summary>
    private void ApplyFilter()
    {
        string q = Filter.Trim();
        _matches = 0;
        // Opening thousands of rows freezes the view: with too many matches, only the first level shows them.
        _expandMatches = true;
        if (q.Length > 0 || ChangedOnly)
        {
            int n = 0;
            foreach (var f in AllFields())
                if (!f.IsContainer && (ShowRaw || !f.IsInternal) && (!ChangedOnly || f.IsDirty)
                    && (q.Length == 0 || f.Label.Contains(q, StringComparison.OrdinalIgnoreCase) || f.RawName.Contains(q, StringComparison.OrdinalIgnoreCase)))
                    if (++n > MaxExpandedMatches) break;
            _expandMatches = n <= MaxExpandedMatches;
        }
        foreach (var f in Fields) Apply(f, q);
        if (q.Length > 0)
            Status = _expandMatches ? $"{_matches:N0} matching fields" : $"More than {MaxExpandedMatches} fields match \"{q}\": type more to narrow it (blocks with matches are not opened).";
    }

    private const int MaxExpandedMatches = 300;
    private int _matches;
    private bool _expandMatches = true;

    private bool Apply(FieldViewModel f, string q)
    {
        bool anyChild = false;
        foreach (var c in f.Children) anyChild |= Apply(c, q);

        bool modeOk = ShowRaw || !f.IsInternal;
        if (ShowSimple && Simple != null)
        {
            // Simple: only the listed fields (and what holds them), opened.
            bool listed = !f.IsContainer && !f.IsExplanation && Simple.For(f.Path) != null;
            bool hit = listed && (q.Length == 0 || f.DisplayText.Contains(q, StringComparison.OrdinalIgnoreCase) || f.RawName.Contains(q, StringComparison.OrdinalIgnoreCase))
                && (!ChangedOnly || f.IsDirty);
            bool show = hit || anyChild;
            f.IsVisible = show;
            if (anyChild) f.IsExpanded = true;
            if (hit) _matches++;
            return show;
        }
        bool matches = q.Length == 0
            || f.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.RawName.Contains(q, StringComparison.OrdinalIgnoreCase);
        bool changedOk = !ChangedOnly || f.IsDirty;

        bool visible = modeOk && ((matches && changedOk && !f.IsContainer) || anyChild || (f.IsContainer && q.Length == 0 && !ChangedOnly));
        f.IsVisible = visible;
        if (visible && !f.IsContainer && (q.Length > 0 || ChangedOnly)) _matches++;
        if ((q.Length > 0 || ChangedOnly) && anyChild && _expandMatches) f.IsExpanded = true;
        return visible;
    }
}
