using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Core.Scripting.Multiplayer;

namespace Kalo.App.ViewModels;

/// <summary>The vanilla check: whether the mod plays in MCC without mods (see <see cref="VanillaCheck"/>), shown in My mod.</summary>
public partial class MainViewModel
{
    [ObservableProperty] private string _vanillaSummary = "";
    [ObservableProperty] private IBrush _vanillaBrush = Brushes.Gray;
    public VanillaReport? Vanilla { get; private set; }

    private static readonly IBrush VanillaGreen = new SolidColorBrush(Color.Parse("#5FB865"));
    private static readonly IBrush VanillaAmber = new SolidColorBrush(Color.Parse("#E5A50A"));
    private static readonly IBrush VanillaRed = new SolidColorBrush(Color.Parse("#F75464"));

    private void UpdateVanilla(IReadOnlyList<WorkspaceChange> changes)
    {
        var templates = ModProjects?.Active?.Manifest.MapTemplates ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Vanilla = VanillaCheck.Check(changes, templates, GametypeTooBig);
        VanillaSummary = Vanilla.Summary;
        VanillaBrush = Vanilla.IsVanilla ? VanillaGreen : Vanilla.CanBeVanilla ? VanillaAmber : VanillaRed;
    }

    /// <summary>A reason when the gametype's last compiled variant (maps\megalo\kalo_&lt;name&gt;.bin) is too big for a game type file.</summary>
    private string? GametypeTooBig(string workspacePath)
    {
        if (Paths == null) return null;
        string bin = System.IO.Path.Combine(Paths.Workspace, "maps", "megalo", "kalo_" + System.IO.Path.GetFileNameWithoutExtension(workspacePath) + ".bin");
        try
        {
            if (!File.Exists(bin)) return null;
            int size = Kalo.Core.Scripting.Multiplayer.GameVariantFile.VariantData(File.ReadAllBytes(bin)).Length;
            int max = Kalo.Core.Scripting.Multiplayer.GameVariantFile.MpvrChunkSize - 12 - 28;
            return size > max ? $"a game type too big for a game type file ({size:N0} bytes of {max:N0})" : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return null; }
    }

    /// <summary>Writes the vanilla check, file by file, to Output.</summary>
    [RelayCommand]
    private async Task ShowVanillaReport()
    {
        await RefreshChangesAsync();
        if (Vanilla == null) return;
        BottomPanel = "activity";
        Log("Vanilla check: " + Vanilla.Summary);
        foreach (var group in Vanilla.Items.GroupBy(i => i.Status).OrderBy(g => g.Key))
        {
            string head = group.Key switch
            {
                VanillaStatus.Vanilla => "plays without mods",
                VanillaStatus.Exportable => "needs mods as it is, can be exported (map › Export as Forge variant)",
                _ => "only plays with mods",
            };
            Log($"Vanilla check: {group.Count()} file(s) {head}:");
            foreach (var item in group) Log($"    {item.Path}: {item.Reason}");
        }
    }
}
