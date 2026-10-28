using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>An open tag in an editor tab.</summary>
public partial class TagEditorViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public string Path { get; }
    public string Title => System.IO.Path.GetFileName(Path);
    public string Breadcrumb => "tags › " + string.Join(" › ", Path.Split('\\'));
    public string Group => System.IO.Path.GetExtension(Path).TrimStart('.');
    public ObservableCollection<FieldViewModel> Fields { get; } = new();

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _showRaw;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _changedOnly;
    [ObservableProperty] private bool _differsFromVanilla;
    [ObservableProperty] private string _status = "Loading…";
    [ObservableProperty] private object? _selectedItem;

    public FieldViewModel? SelectedField => SelectedItem as FieldViewModel;
    public bool ShowLabeled { get => !ShowRaw; set => ShowRaw = !value; }

    public TagEditorViewModel(MainViewModel main, string path)
    {
        _main = main;
        Path = path;
    }

    public void Load(TagDocument doc)
    {
        Fields.Clear();
        foreach (var node in doc.Fields ?? new List<FieldNode>()) Fields.Add(new FieldViewModel(this, node, null));
        // Open the first level of structs so the important fields are visible.
        foreach (var f in Fields.Where(f => f.Kind == ValueKinds.Struct)) f.IsExpanded = true;
        IsLoading = false;
        Status = $"{doc.FieldCount:N0} fields · loaded in {doc.LoadMs} ms";
        ApplyFilter();
    }

    public void Fail(string message)
    {
        IsLoading = false;
        Status = "Could not open: " + message;
    }

    partial void OnSelectedItemChanged(object? value) => OnPropertyChanged(nameof(SelectedField));
    partial void OnShowRawChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLabeled));
        foreach (var f in AllFields()) f.RefreshMode();
        ApplyFilter();
    }
    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnChangedOnlyChanged(bool value) => ApplyFilter();

    public async Task CommitAsync(FieldViewModel field, string value)
    {
        if (_main.Host == null) { field.Reject("The Blam Host is not running."); return; }
        try
        {
            SetFieldResult result = await _main.Host.SetFieldAsync(Path, field.Path, value);
            field.Accept(result.Value ?? value);
            IsDirty = AllFields().Any(f => f.IsDirty);
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
        foreach (var f in Fields) Apply(f, q);
    }

    private bool Apply(FieldViewModel f, string q)
    {
        bool anyChild = false;
        foreach (var c in f.Children) anyChild |= Apply(c, q);

        bool modeOk = ShowRaw || !f.IsInternal;
        bool matches = q.Length == 0
            || f.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.RawName.Contains(q, StringComparison.OrdinalIgnoreCase);
        bool changedOk = !ChangedOnly || f.IsDirty;

        bool visible = modeOk && ((matches && changedOk && !f.IsContainer) || anyChild || (f.IsContainer && q.Length == 0 && !ChangedOnly));
        f.IsVisible = visible;
        if ((q.Length > 0 || ChangedOnly) && anyChild) f.IsExpanded = true;
        return visible;
    }
}
