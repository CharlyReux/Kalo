using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>One row of the tag editor: a field, a block element or a section.</summary>
public partial class FieldViewModel : ObservableObject
{
    private static readonly HashSet<string> EditableKinds = new()
    {
        ValueKinds.Integer, ValueKinds.Real, ValueKinds.Text, ValueKinds.Enum, ValueKinds.Flags,
        ValueKinds.Reference, ValueKinds.Bounds, ValueKinds.BlockIndex
    };

    private readonly TagEditorViewModel _owner;
    private bool _suppress;
    private string? _committed;

    public FieldNode Node { get; }
    public FieldViewModel? Parent { get; }
    public ObservableCollection<FieldViewModel> Children { get; } = new();

    public string Path => Node.Path ?? "";
    public string RawName => Node.Name ?? "";
    public string Type => Node.Type ?? "";
    public string Kind => Node.Kind ?? "";
    public string Label { get; }
    public string Units { get; }
    public string Range { get; }
    public string? Description => string.IsNullOrWhiteSpace(Node.Description) ? null : Node.Description;
    public IReadOnlyList<string> Options => Node.Options ?? (IReadOnlyList<string>)Array.Empty<string>();

    public bool IsBlock => Kind == ValueKinds.Block;
    public bool IsBlockElement => Kind == ValueKinds.Element && Parent?.Kind == ValueKinds.Block;
    public bool IsReference => Kind == ValueKinds.Reference;
    public bool HasReferenceTarget => IsReference && !string.IsNullOrWhiteSpace(Value);
    public IReadOnlyList<string> Allowed => Node.Allowed ?? (IReadOnlyList<string>)Array.Empty<string>();
    public string AllowedText => Allowed.Count == 0 ? "any tag" : string.Join(", ", Allowed.Select(a => "." + a));
    /// <summary>Index of this element in its block ("[3]" → 3), or -1.</summary>
    public int ElementIndex => Kind == ValueKinds.Element && RawName.StartsWith('[') && int.TryParse(RawName.Trim('[', ']'), out int i) ? i : -1;
    public bool IsTruncated => Node.Truncated;
    public string TruncatedText => Node.Truncated ? $"Showing the first {Children.Count} of {Node.ElementCount} elements." : "";

    [RelayCommand] private Task AddElement() => _owner.BlockOpAsync(this, BlockOps.Add, 0);
    [RelayCommand] private Task ClearElements() => _owner.BlockOpAsync(this, BlockOps.Clear, 0);
    [RelayCommand] private Task InsertBefore() => Parent == null ? Task.CompletedTask : _owner.BlockOpAsync(Parent, BlockOps.Insert, ElementIndex);
    [RelayCommand] private Task DuplicateElement() => Parent == null ? Task.CompletedTask : _owner.BlockOpAsync(Parent, BlockOps.Duplicate, ElementIndex);
    [RelayCommand] private Task DeleteElement() => Parent == null ? Task.CompletedTask : _owner.BlockOpAsync(Parent, BlockOps.Remove, ElementIndex);

    public bool IsContainer => Kind is ValueKinds.Block or ValueKinds.Struct or ValueKinds.Array or ValueKinds.Element;
    public bool IsExplanation => Kind == ValueKinds.Explanation;
    public bool IsEditable => Node.Editable && EditableKinds.Contains(Kind);
    public bool IsEnum => IsEditable && Kind == ValueKinds.Enum && Options.Count > 0;
    public bool IsTextEditor => IsEditable && !IsEnum;
    public bool IsReadOnlyValue => !IsEditable && !IsExplanation;
    /// <summary>
    /// Engine-computed or hidden fields, not shown in the labeled view. A struct, block or element marked hidden still
    /// shows when something inside it is shown (e.g. a squad's "templated" struct holds its cells: characters, weapons).
    /// </summary>
    public bool IsInternal { get; }
    public string SavedValue { get; private set; }
    /// <summary>Label in the labeled view, engine name in the raw view.</summary>
    public string DisplayText => _owner.ShowRaw && Kind != ValueKinds.Element && RawName.Length > 0 ? RawName : Label;
    /// <summary>Range in the labeled view, engine type in the raw view.</summary>
    public string Extra => _owner.ShowRaw ? Type : Range;

    public void RefreshMode()
    {
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(Extra));
    }

    [ObservableProperty] private string? _value;
    [ObservableProperty] private int _selectedOption = -1;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string? _error;

    public FieldViewModel(TagEditorViewModel owner, FieldNode node, FieldViewModel? parent)
    {
        _owner = owner;
        Node = node;
        Parent = parent;
        Label = Kind == ValueKinds.Element
            ? (string.IsNullOrWhiteSpace(node.DisplayName) ? node.Name ?? "" : $"{node.Name}  {node.DisplayName}")
            : FieldLabels.For(node.Name, node.DisplayName);
        (Units, Range) = FieldLabels.SplitUnits(node.Units);
        SavedValue = node.Value ?? "";
        _committed = node.Value;
        _suppress = true;
        Value = node.Value;
        if (IsEnum && int.TryParse(node.Value, out int index)) SelectedOption = index;
        _suppress = false;
        if (node.Children != null)
            foreach (var child in node.Children) Children.Add(new FieldViewModel(owner, child, this));
        IsInternal = RawName.StartsWith("runtime", StringComparison.OrdinalIgnoreCase) || Kind == ValueKinds.Other
            || !Node.Visible && (!IsContainer || Children.All(c => c.IsInternal));
    }

    partial void OnValueChanged(string? value)
    {
        OnPropertyChanged(nameof(HasReferenceTarget));
        if (_suppress || !IsTextEditor || value == _committed) return;
        _ = _owner.CommitAsync(this, value ?? "");
    }

    partial void OnSelectedOptionChanged(int value)
    {
        if (_suppress || !IsEnum || value < 0) return;
        string text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (text == _committed) return;
        _ = _owner.CommitAsync(this, text);
    }

    /// <summary>The host accepted a value (possibly normalized): show it and track whether it differs from the saved file.</summary>
    public void Accept(string value)
    {
        _suppress = true;
        _committed = value;
        Value = value;
        if (IsEnum && int.TryParse(value, out int index)) SelectedOption = index;
        _suppress = false;
        Error = null;
        IsDirty = value != SavedValue;
    }

    public void Reject(string message)
    {
        _suppress = true;
        Value = _committed;
        if (IsEnum && int.TryParse(_committed, out int index)) SelectedOption = index;
        _suppress = false;
        Error = message;
    }

    public void MarkSaved()
    {
        SavedValue = _committed ?? "";
        IsDirty = false;
        foreach (var c in Children) c.MarkSaved();
    }

    public IEnumerable<FieldViewModel> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants()) yield return d;
    }
}
