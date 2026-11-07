using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core.Scripting;

namespace Kalo.App.ViewModels;

/// <summary>A choice in the Spartans / Elites lists: a palette name (null = not set) and what it means.</summary>
public sealed record LoadoutPaletteChoice(string? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Settings › Loadouts: which loadouts Spartans and Elites pick from at spawn, and the gametype's own loadouts.
/// Every change rewrites only the loadout lines of the script (GametypeLoadouts).
/// </summary>
public partial class LoadoutsViewModel : ObservableObject
{
    private readonly ScriptEditorViewModel _script;
    private bool _loading;
    private bool _hasInclude;
    private string? _includeVia;

    public LoadoutsViewModel(ScriptEditorViewModel script) => _script = script;

    public IReadOnlyList<LoadoutPaletteChoice> SpartanChoices { get; } = GametypeLoadouts.SpartanPalettes.Select(p => new LoadoutPaletteChoice(p.Value, p.Label)).ToList();
    public IReadOnlyList<LoadoutPaletteChoice> EliteChoices { get; } = GametypeLoadouts.ElitePalettes.Select(p => new LoadoutPaletteChoice(p.Value, p.Label)).ToList();
    public static IReadOnlyList<string> Names => GametypeLoadouts.Names;
    public static IReadOnlyList<string> Weapons => GametypeLoadouts.Weapons;
    public static IReadOnlyList<string> Equipment => GametypeLoadouts.Equipment;
    public static IReadOnlyList<string> Grenades => GametypeLoadouts.Grenades;

    [ObservableProperty] private LoadoutPaletteChoice? _spartan;
    [ObservableProperty] private LoadoutPaletteChoice? _elite;
    [ObservableProperty] private string? _problem;
    public ObservableCollection<LoadoutRow> Custom { get; } = new();

    public bool ShowCustom => Spartan?.Value == GametypeLoadouts.MyPalette || Elite?.Value == GametypeLoadouts.MyPalette;
    public bool HasProblem => Problem != null;
    public string Summary =>
        Spartan?.Value == null && Elite?.Value == null
            ? "Not set: players get the game's usual loadouts. Starting weapons without a menu: pick None, then set them in Player traits (Primary weapon...)."
            : _hasInclude ? $"Bungie's loadout list is included ({_includeVia}): its palettes are available." : "";

    partial void OnSpartanChanged(LoadoutPaletteChoice? value) { OnPropertyChanged(nameof(ShowCustom)); OnPropertyChanged(nameof(Summary)); Commit(); }
    partial void OnEliteChanged(LoadoutPaletteChoice? value) { OnPropertyChanged(nameof(ShowCustom)); OnPropertyChanged(nameof(Summary)); Commit(); }
    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    public void Reload()
    {
        _loading = true;
        try
        {
            var s = GametypeLoadouts.Parse(_script.Text, _script.Main.Paths?.MegaloSource);
            _hasInclude = s.HasInclude;
            _includeVia = s.IncludeVia;
            Spartan = SpartanChoices.FirstOrDefault(c => c.Value == s.Spartan) ?? (s.Spartan == null ? SpartanChoices[0] : new LoadoutPaletteChoice(s.Spartan, s.Spartan));
            Elite = EliteChoices.FirstOrDefault(c => c.Value == s.Elite) ?? (s.Elite == null ? EliteChoices[0] : new LoadoutPaletteChoice(s.Elite, s.Elite));
            Custom.Clear();
            foreach (var d in s.Custom) Custom.Add(new LoadoutRow(this, d));
            Problem = null;
            OnPropertyChanged(nameof(ShowCustom));
            OnPropertyChanged(nameof(Summary));
        }
        finally { _loading = false; }
    }

    [RelayCommand]
    private void AddLoadout()
    {
        int n = 1;
        while (Custom.Any(c => c.Id == $"kalo_loadout_{n}")) n++;
        _loading = true;
        Custom.Add(new LoadoutRow(this, GametypeLoadouts.NewLoadout(n)));
        // Adding a loadout means using them: pick My loadouts for Spartans if nothing of ours is chosen yet.
        if (!ShowCustom) Spartan = SpartanChoices.First(c => c.Value == GametypeLoadouts.MyPalette);
        _loading = false;
        Commit();
    }

    internal void Remove(LoadoutRow row)
    {
        Custom.Remove(row);
        Commit();
    }

    /// <summary>Writes the loadout lines into the script, unless the choices can't work (then says why, script unchanged).</summary>
    internal void Commit()
    {
        if (_loading) return;
        var s = new LoadoutsState { Spartan = Spartan?.Value, Elite = Elite?.Value, HasInclude = _hasInclude, IncludeVia = _includeVia };
        foreach (var r in Custom) s.Custom.Add(r.ToDef());
        Problem = GametypeLoadouts.Problem(s);
        if (Problem != null) return;
        string text = GametypeLoadouts.Apply(_script.Text, s);
        if (text == _script.Text) return;
        _script.Text = text;
        // Bungie's list may have just been included.
        if (GametypeLoadouts.BungieChosen(s) && !_hasInclude) { _hasInclude = true; _includeVia = GametypeLoadouts.Include; OnPropertyChanged(nameof(Summary)); }
        var check = KaloScript.Transpile(text);
        if (!check.Succeeded) Problem = $"The script has an error now: line {check.Errors[0].Line}: {check.Errors[0].Message}";
    }
}

/// <summary>One of the gametype's own loadouts.</summary>
public partial class LoadoutRow : ObservableObject
{
    private readonly LoadoutsViewModel _owner;
    public string Id { get; }

    public LoadoutRow(LoadoutsViewModel owner, LoadoutDef d)
    {
        _owner = owner;
        Id = d.Id;
        _name = d.Name; _primary = d.Primary; _secondary = d.Secondary; _equipment = d.Equipment; _grenades = d.Grenades;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _primary;
    [ObservableProperty] private string _secondary;
    [ObservableProperty] private string _equipment;
    [ObservableProperty] private string _grenades;

    /// <summary>"loadout_name_ranger" → "Ranger", as the menu shows it.</summary>
    public string Heading => GametypeSettingsViewModel.Pretty(Name.Replace("loadout_name_", ""));

    partial void OnNameChanged(string value) { OnPropertyChanged(nameof(Heading)); _owner.Commit(); }
    partial void OnPrimaryChanged(string value) => _owner.Commit();
    partial void OnSecondaryChanged(string value) => _owner.Commit();
    partial void OnEquipmentChanged(string value) => _owner.Commit();
    partial void OnGrenadesChanged(string value) => _owner.Commit();

    [RelayCommand]
    private void Remove() => _owner.Remove(this);

    public LoadoutDef ToDef() => new(Id, Name, Primary, Secondary, Equipment, Grenades);
}
