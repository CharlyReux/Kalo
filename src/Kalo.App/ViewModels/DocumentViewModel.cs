using CommunityToolkit.Mvvm.ComponentModel;

namespace Kalo.App.ViewModels;

/// <summary>Anything that opens in a tab: a tag, a map, a gametype script.</summary>
public abstract partial class DocumentViewModel : ObservableObject
{
    protected DocumentViewModel(string path) => Path = path;

    /// <summary>Tag path (relative to tags\, with extension) or, for scripts, the file path relative to the workspace.</summary>
    public string Path { get; }
    public virtual string Title => System.IO.Path.GetFileName(Path);
    public virtual string Breadcrumb => "tags › " + string.Join(" › ", Path.Split('\\'));
    /// <summary>Short kind shown before the title in tabs: "", "map", "script".</summary>
    public virtual string Badge => "";
    public bool HasBadge => Badge.Length > 0;
    /// <summary>Path relative to the workspace, as the vanilla baseline knows it.</summary>
    public virtual string BaselinePath => "tags\\" + Path;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _differsFromVanilla;

    public abstract Task SaveDocumentAsync();
    /// <summary>Called when the tab closes.</summary>
    public virtual Task CloseAsync() => Task.CompletedTask;
}
