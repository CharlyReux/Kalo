using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kalo.App.ViewModels;

/// <summary>A folder or tag file in the workspace's tags tree. Folders load their children when first expanded.</summary>
public partial class ExplorerNode : ObservableObject
{
    private static readonly Dictionary<string, string> GroupColors = new(StringComparer.OrdinalIgnoreCase)
    {
        [".weapon"] = "#E5A50A",
        [".vehicle"] = "#E5A50A",
        [".biped"] = "#E5A50A",
        [".equipment"] = "#E5A50A",
        [".projectile"] = "#E5A50A",
        [".scenario"] = "#5FB865",
        [".scenario_structure_bsp"] = "#5FB865",
        [".shader"] = "#6E9EF7",
        [".bitmap"] = "#6E9EF7",
        [".render_model"] = "#C77DFF",
        [".model"] = "#C77DFF",
        [".damage_effect"] = "#F75464",
        [".effect"] = "#F75464",
    };

    private bool _loaded;
    private readonly string _tagsRoot = "";
    private readonly Func<string, bool>? _changedCheck;

    public string Name { get; }
    public string FullPath { get; }
    /// <summary>Path relative to the tags folder (with extension) for files; null for folders.</summary>
    public string? TagPath { get; }
    public bool IsFolder { get; }
    public bool IsPlaceholder { get; }
    public IBrush Dot { get; }
    public ObservableCollection<ExplorerNode> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isChanged;

    private ExplorerNode(string name)
    {
        Name = name;
        FullPath = "";
        IsPlaceholder = true;
        Dot = Brushes.Transparent;
    }

    public ExplorerNode(string fullPath, string tagsRoot, bool isFolder, Func<string, bool>? isChanged)
    {
        FullPath = fullPath;
        _tagsRoot = tagsRoot;
        Name = System.IO.Path.GetFileName(fullPath);
        IsFolder = isFolder;
        _changedCheck = isChanged;
        if (isFolder)
        {
            Dot = Brushes.Transparent;
            Children.Add(new ExplorerNode("…"));
        }
        else
        {
            TagPath = fullPath.Substring(tagsRoot.Length).TrimStart('\\');
            Dot = SolidColorBrush.Parse(GroupColors.TryGetValue(System.IO.Path.GetExtension(fullPath), out var c) ? c : "#6F737A");
            IsChanged = isChanged?.Invoke("tags\\" + TagPath) ?? false;
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _loaded || !IsFolder) return;
        _loaded = true;
        Children.Clear();
        string tagsRoot = _tagsRoot;
        try
        {
            foreach (string d in Directory.GetDirectories(FullPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                Children.Add(new ExplorerNode(d, tagsRoot, true, _changedCheck));
            foreach (string f in Directory.GetFiles(FullPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                Children.Add(new ExplorerNode(f, tagsRoot, false, _changedCheck));
        }
        catch (Exception ex)
        {
            Children.Add(new ExplorerNode("error: " + ex.Message));
        }
    }

    public void RefreshChanged()
    {
        if (!IsFolder && TagPath != null) IsChanged = _changedCheck?.Invoke("tags\\" + TagPath) ?? false;
        foreach (var c in Children) c.RefreshChanged();
    }
}

public sealed record SearchResult(string Name, string TagPath, string Folder);

public sealed record ProblemItem(string Severity, string Message, string Source, string Time)
{
    public bool IsError => Severity == "error";
}

public partial class ChangeItem : ObservableObject
{
    public required string Path { get; init; }
    public required string Kind { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    /// <summary>Tag path relative to tags\ when the change is a tag, else null.</summary>
    public string? TagPath => Path.StartsWith("tags\\", StringComparison.OrdinalIgnoreCase) ? Path.Substring(5) : null;
}
