using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>One object in a gear list: its file, shown by name. "None" has an empty path.</summary>
public sealed record GearChoice(string Path, string Label)
{
    public override string ToString() => Label;
    public static GearChoice From(string path) => new(path, path.Length == 0 ? "None" : System.IO.Path.GetFileNameWithoutExtension(path).Replace('_', ' '));
}

/// <summary>
/// Map tab › Player gear: what the player starts with (weapons, grenades, armor ability), the map's starting profiles.
/// In a multiplayer game a gametype's loadouts replace it.
/// </summary>
public partial class MapEditorViewModel
{
    public List<GearChoice> GearWeapons { get; private set; } = new();
    public List<GearChoice> GearEquipment { get; private set; } = new();
    [ObservableProperty] private GearChoice? _gearPrimary;
    [ObservableProperty] private GearChoice? _gearSecondary;
    [ObservableProperty] private GearChoice? _gearAbility;
    [ObservableProperty] private string _gearFrags = "0";
    [ObservableProperty] private string _gearPlasmas = "0";
    [ObservableProperty] private string _gearStatus = "";

    private static List<GearChoice> GearList(IEnumerable<string> tags) =>
        new[] { "" }.Concat(tags.OrderBy(t => System.IO.Path.GetFileName(t), StringComparer.OrdinalIgnoreCase)).Select(GearChoice.From).ToList();

    private static GearChoice Choose(List<GearChoice> list, string? path)
    {
        var found = list.FirstOrDefault(c => c.Path.Equals(path ?? "", StringComparison.OrdinalIgnoreCase));
        if (found != null) return found;
        found = GearChoice.From(path ?? "");
        list.Insert(1, found);
        return found;
    }

    /// <summary>Reads the map's gear when the panel opens.</summary>
    [RelayCommand]
    public async Task LoadGear()
    {
        if (_main.Host == null) return;
        // Held weapons only: their first person and projectile parts are other kinds of file.
        GearWeapons = GearList(_main.TagIndex.Where(t => t.EndsWith(".weapon", StringComparison.OrdinalIgnoreCase)
            && (t.StartsWith(@"objects\weapons\", StringComparison.OrdinalIgnoreCase) || t.Contains(@"\mods\", StringComparison.OrdinalIgnoreCase))));
        GearEquipment = GearList(_main.TagIndex.Where(t => t.EndsWith(".equipment", StringComparison.OrdinalIgnoreCase)));
        OnPropertyChanged(nameof(GearWeapons));
        OnPropertyChanged(nameof(GearEquipment));
        var gear = await _main.Host.StartingGearAsync(Path);
        GearPrimary = Choose(GearWeapons, gear.Primary);
        GearSecondary = Choose(GearWeapons, gear.Secondary);
        GearAbility = Choose(GearEquipment, gear.Equipment);
        GearFrags = gear.Frags.ToString();
        GearPlasmas = gear.Plasmas.ToString();
        GearStatus = gear.Profiles == 0 ? "This map gives nothing yet: Apply sets it." : "";
    }

    [RelayCommand]
    public async Task ApplyGear()
    {
        if (_main.Host == null) return;
        if (!int.TryParse(GearFrags.Trim(), out int frags) || frags < 0 || !int.TryParse(GearPlasmas.Trim(), out int plasmas) || plasmas < 0)
        {
            GearStatus = "Grenades: a whole number, 0 or more.";
            return;
        }
        using var edit = await EditAsync("player gear");
        try
        {
            await _main.Host.SetStartingGearAsync(new StartingGear { Path = Path, Primary = GearPrimary?.Path ?? "", Secondary = GearSecondary?.Path ?? "", Equipment = GearAbility?.Path ?? "", Frags = frags, Plasmas = plasmas });
            IsDirty = true;
            bool copied = new[] { GearPrimary?.Path, GearSecondary?.Path }.Any(p => p != null && p.Contains(@"\mods\", StringComparison.OrdinalIgnoreCase));
            GearStatus = copied ? "Set, but a weapon copied into your mod did not reach the player when tested: place it in the map for them to pick up." : "Set. Save, then Play: new weapons start full.";
            QueueStatusRefresh();
        }
        catch (HostException ex)
        {
            GearStatus = ex.Message;
        }
    }
}
