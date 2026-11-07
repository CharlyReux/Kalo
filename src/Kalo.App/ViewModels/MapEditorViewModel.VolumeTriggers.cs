using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core.Scripting;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>A choice in a trigger volume's "When the player enters" list.</summary>
public sealed record VolumeActionChoice(VolumeAction Action, string Label, string Param)
{
    public override string ToString() => Label;
}

/// <summary>
/// Campaign missions: map inspector › trigger volume › When the player enters. Writes the mission script code (spawn a
/// squad, checkpoint, teleport to a flag, kill zone, win) into the mission's own script, naming the volume if needed.
/// The multiplayer counterpart is the Zone panel (MapEditorViewModel.ZoneTriggers).
/// </summary>
public partial class MapEditorViewModel
{
    public IReadOnlyList<VolumeActionChoice> VolumeActionChoices { get; } =
        VolumeTriggers.Choices.Select(c => new VolumeActionChoice(c.Action, c.Label, c.Param)).ToList();

    [ObservableProperty] private VolumeActionChoice? _volumeAction;
    [ObservableProperty] private string? _volumeParam;
    [ObservableProperty] private string _volumeTriggerStatus = "";
    public List<string> VolumeParamChoices { get; private set; } = new();
    public bool VolumeActionHasParam => VolumeAction?.Param.Length > 0;
    public string VolumeParamLabel => VolumeAction?.Param switch { "squad" => "Squad", "flag" => "Flag", _ => "" };

    partial void OnVolumeActionChanged(VolumeActionChoice? value)
    {
        VolumeParamChoices = value?.Param switch
        {
            "squad" => _squads.Select(s => s.Name).Where(n => !string.IsNullOrEmpty(n)).OrderBy(n => n).ToList(),
            "flag" => Items.Where(i => i.Category == CampaignCategories.CutsceneFlags && !string.IsNullOrEmpty(i.PlacedName)).Select(i => i.PlacedName!).OrderBy(n => n).ToList(),
            _ => new List<string>(),
        };
        OnPropertyChanged(nameof(VolumeParamChoices));
        OnPropertyChanged(nameof(VolumeActionHasParam));
        OnPropertyChanged(nameof(VolumeParamLabel));
        VolumeParam = VolumeParamChoices.FirstOrDefault();
        VolumeTriggerStatus = value?.Param switch
        {
            "squad" when VolumeParamChoices.Count == 0 => "This mission has no squad yet: Add › Squad in the Campaign bar first.",
            "flag" when VolumeParamChoices.Count == 0 => "This mission has no named flag: Add › Flag where the player should arrive, and name it.",
            _ => "",
        };
    }

    [RelayCommand]
    private async Task AddVolumeTrigger()
    {
        var item = Selected;
        if (item is not { IsVolume: true }) { VolumeTriggerStatus = "Select a trigger volume first (Volumes in the Campaign bar)."; return; }
        // Read the choice now: opening the script switches tabs, and the inspector's lists then let go of it.
        var choice = VolumeAction;
        string param = VolumeParam ?? "";
        if (choice == null) { VolumeTriggerStatus = "Pick what happens first."; return; }
        if (choice.Param.Length > 0 && param.Length == 0) { VolumeTriggerStatus = $"Pick the {VolumeParamLabel.ToLowerInvariant()} first."; return; }

        // Scripts find volumes by name.
        string volume = item.PlacedName ?? "";
        if (volume.Length == 0)
        {
            volume = "tv_kalo_" + item.Index;
            await RenameAsync(item, volume);
            if (string.IsNullOrEmpty(item.PlacedName)) { VolumeTriggerStatus = "Give the volume a name first (inspector Name, or F2)."; return; }
        }
        // Special volumes (zone set switches, "zone_set:...") are read by the engine by their name: scripts can't use them.
        if (!System.Text.RegularExpressions.Regex.IsMatch(volume, @"^[A-Za-z_][A-Za-z0-9_]*$"))
        { VolumeTriggerStatus = $"Scripts can't use the name \"{volume}\" (only letters, digits and _). Add a volume of your own (Add › Volume) or rename this one if it isn't a zone set switch."; return; }

        var own = MissionScripts.FirstOrDefault(f => f.IsOwn);
        if (own == null) { VolumeTriggerStatus = "This mission has no script of its own: copy it into your mod (New map › Campaign mission) to get one."; return; }
        var doc = await _main.OpenMissionScriptAsync(own.Path, Path);
        if (!doc.IsKaloScript) doc.IsKaloScript = true;
        doc.Text = VolumeTriggers.AddTo(doc.Text, volume, choice.Action, param);
        doc.IsDirty = true;
        doc.Check();
        string done = $"Added to {doc.Title} (volume {volume})." + (IsDirty ? " Save the map and the script, then Play." : " Save the script, then Play.");
        _main.Log($"{Title}: volume {volume} › {choice.Label} written into {own.Path}.");
        _main.ActiveDocument = this;
        VolumeAction = choice;
        VolumeParam = param;
        VolumeTriggerStatus = done;
    }
}
