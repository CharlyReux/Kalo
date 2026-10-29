using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kalo.App.ViewModels;

/// <summary>A file compared with its vanilla HREK version: changed tag fields, or changed script lines.</summary>
public partial class DiffViewModel : DocumentViewModel
{
    private readonly string _file;

    /// <param name="file">Workspace-relative path (tags\... or data\...).</param>
    public DiffViewModel(string file) : base("diff:" + file)
    {
        _file = file;
    }

    public string File => _file;
    public override string Title => System.IO.Path.GetFileName(_file);
    public override string Badge => "diff";
    public override string Breadcrumb => string.Join(" › ", _file.Split('\\')) + "  (compared with vanilla)";
    public override string BaselinePath => _file;
    public ObservableCollection<DiffRow> Rows { get; } = new();

    [ObservableProperty] private string _summary = "Comparing…";
    [ObservableProperty] private bool _isText;

    public override Task SaveDocumentAsync() => Task.CompletedTask;
}

/// <summary>
/// One difference. For tags: field label + path, vanilla and current values. For text: line numbers and the line.
/// Kind: changed / added / removed (tags), or '+', '-', ' ', '…' (text).
/// </summary>
public sealed record DiffRow(string Kind, string Label, string Detail, string Vanilla, string Current)
{
    public bool IsAdded => Kind is "added" or "+";
    public bool IsRemoved => Kind is "removed" or "-";
    public bool IsChanged => Kind == "changed";
    public bool IsFold => Kind == "…";
    public string Marker => Kind switch { "added" or "+" => "+", "removed" or "-" => "−", "changed" => "~", "…" => "⋯", _ => " " };
}
