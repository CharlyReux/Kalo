using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core.Scripting;

namespace Kalo.App.ViewModels;

/// <summary>A choice in the inspector's "When a player enters" list.</summary>
public sealed record ZoneActionChoice(ZoneAction Action, string Label, string ParamLabel, string Default)
{
    public bool HasParam => ParamLabel.Length > 0;
    public override string ToString() => Label;
}

/// <summary>
/// Map inspector › Zone › When a player enters: writes the gametype code for a zone (message, kill zone, weapon,
/// points, speed boost, teleport) into the gametype picked for Play, giving the object a Megalo label if it has none.
/// </summary>
public partial class MapEditorViewModel
{
    public IReadOnlyList<ZoneActionChoice> ZoneActionChoices { get; } =
        ZoneTriggers.Choices.Select(c => new ZoneActionChoice(c.Action, c.Label, c.ParamLabel, c.Default)).ToList();

    [ObservableProperty] private ZoneActionChoice? _zoneAction;
    [ObservableProperty] private string _zoneParam = "";
    [ObservableProperty] private string _zoneTriggerStatus = "";

    public bool ZoneActionHasParam => ZoneAction?.HasParam == true;
    public string ZoneParamLabel => ZoneAction?.ParamLabel ?? "";

    partial void OnZoneActionChanged(ZoneActionChoice? value)
    {
        ZoneParam = value?.Default ?? "";
        OnPropertyChanged(nameof(ZoneActionHasParam));
        OnPropertyChanged(nameof(ZoneParamLabel));
        ZoneTriggerStatus = value?.Action == Kalo.Core.Scripting.ZoneAction.Teleport
            ? "Give the exit object (any object, e.g. a teleporter receiver or a flag stand) this Megalo label in its Properties."
            : "";
    }

    [RelayCommand]
    private async Task AddZoneTrigger()
    {
        var item = Selected;
        if (item == null || !item.HasZone) { ZoneTriggerStatus = "Select an object and give it a zone (Sphere, Cylinder or Box) first."; return; }
        if (ZoneAction == null) { ZoneTriggerStatus = "Pick what happens first."; return; }
        // Read the choice now: opening the gametype switches tabs, and the inspector's list then lets go of it.
        var choice = ZoneAction;
        string param = ZoneParam;

        // 1. The object needs a Megalo label: gametypes find map objects by it.
        var labelRow = Properties.FirstOrDefault(r => r.Path.EndsWith("String:megalo label", StringComparison.OrdinalIgnoreCase));
        string label = labelRow?.Value.Trim() ?? "";
        if (label.Length == 0)
        {
            if (labelRow == null) { ZoneTriggerStatus = "This object has no Megalo label field (only multiplayer objects do)."; return; }
            label = ZoneTriggers.LabelFor(item.PlacedName is { Length: > 0 } n ? n : "zone_" + item.Index);
            if (await SetPropertyAsync(labelRow, label) is { } why) { ZoneTriggerStatus = "Could not set its Megalo label: " + why; return; }
            labelRow.Value = label;
        }

        // 2. The gametype: the one picked for Play when it is a Kalo Script of the mod, else a new one made from Slayer.
        var script = await TargetGametypeAsync();
        if (script == null) return;

        // 3. The code, replacing what Kalo wrote earlier for this zone.
        script.Text = ZoneTriggers.AddTo(script.Text, label, choice.Action, param);
        script.IsDirty = true;
        await script.CompileCommand.ExecuteAsync(null);
        ZoneTriggerStatus = $"Added to {script.Title} (zone label \"{label}\")" + (script.CompileOk ? ". Save the map and the gametype, then Play." : ": it does not compile yet, see the gametype tab.") +
                            (choice.Action == Kalo.Core.Scripting.ZoneAction.Teleport ? $" The exit is the object labelled \"{ZoneTriggers.LabelFor(param)}\"." : "");
        _main.Log($"{Title}: zone {label} › {choice.Label} written into {script.Path}.");
        _main.ActiveDocument = this;
        // Back on the map: show the choice again (which resets the status line, so it is written after).
        string done = ZoneTriggerStatus;
        ZoneAction = choice;
        ZoneParam = param;
        ZoneTriggerStatus = done;
    }

    private async Task<ScriptEditorViewModel?> TargetGametypeAsync()
    {
        string? path = _main.SelectedGametype?.Path;
        if (path != null && path.EndsWith(KaloScript.Extension, StringComparison.OrdinalIgnoreCase) && path.Contains(@"\mods\", StringComparison.OrdinalIgnoreCase))
            return await _main.OpenScriptAsync(path);
        if (_main.ModProjects?.ActiveName == null) { ZoneTriggerStatus = "Create a mod first: the zone code goes into one of its gametypes."; return null; }
        _main.OpenNewGametypeFormCommand.Execute(null);
        _main.IsNewGametypeOpen = false;
        var slayer = _main.GametypeTemplates.FirstOrDefault(t => t.Label == "Slayer") ?? _main.GametypeTemplates.FirstOrDefault();
        string name = System.IO.Path.GetFileNameWithoutExtension(Path) + " rules";
        for (int n = 2; _main.Scripts.Any(s => s.Name.Equals(MainViewModel.Slug(name), StringComparison.OrdinalIgnoreCase)); n++) name = System.IO.Path.GetFileNameWithoutExtension(Path) + $" rules {n}";
        var created = await _main.CreateGametypeAsync(name, slayer, true);
        if (created != null) _main.Log($"Made the gametype {created.Title} for the zones of {Title} (Play picks it).");
        return created;
    }
}
