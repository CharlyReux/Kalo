using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// The squad of the selected spawn point(s) in the inspector: what spawns. Each cell lists characters and weapons
/// (with chances) and how many spawn; a spawn point can override the character and weapon of its cell. Pickers show
/// the scenario's character and weapon palettes by name.
/// </summary>
public partial class SquadEditorViewModel : ObservableObject
{
    private readonly MapEditorViewModel _map;

    public SquadEditorViewModel(MapEditorViewModel map, SquadDetail d, int? point)
    {
        _map = map;
        Index = d.Index;
        Name = d.Name ?? "";
        Characters = (d.Characters ?? new()).Select(PaletteLabel).ToList();
        Weapons = (d.Weapons ?? new()).Select(PaletteLabel).ToList();
        CharacterOverrides = new[] { "From its cell" }.Concat(Characters).ToList();
        WeaponOverrides = new[] { "From its cell" }.Concat(Weapons).ToList();
        VehicleChoices = new[] { "On foot" }.Concat((d.Vehicles ?? new()).Select(PaletteLabel)).ToList();
        var about = new List<string>();
        if (!string.IsNullOrEmpty(d.Team)) about.Add("team " + d.Team);
        if (d.Groups is { Count: > 0 }) about.Add("in " + string.Join(" › ", d.Groups));
        About = string.Join(" · ", about);
        var starts = new List<string>();
        if (!string.IsNullOrEmpty(d.Objective)) starts.Add("follows the objective " + d.Objective + " (what the AI do)");
        if (!string.IsNullOrEmpty(d.Zone)) starts.Add("uses the zone " + d.Zone + " (where they can go)");
        Starts = starts.Count == 0 ? "" : "Once placed, it " + string.Join(" and ", starts) + ".";
        IsTemplated = d.CellsKind == "templated";
        Template = d.Template ?? "";
        // Checked in reach_tag_test (m10 copy, barn_skirm_init): with its templated cell set to an Elite the game still
        // spawned a Skirmisher; with the spawn point's own character set to an Elite it spawned an Elite.
        TemplateNote = IsTemplated
            ? $"Made from the squad template {d.Template}: the game spawns what the template says, so these cells (a copy of it) are shown for reference only. To change what spawns, set each spawn point's own character and weapon above, or give the squad its own cells (Use its own cells)."
            : string.IsNullOrEmpty(d.Template) ? "" : $"Template {d.Template}.";
        foreach (var c in d.Cells ?? new()) Cells.Add(new SquadCellRow(this, c));
        var p = point is int i ? d.SpawnPoints?.FirstOrDefault(s => s.Index == i) : null;
        if (p != null)
        {
            Point = new SquadPointRow(this, p, d.Cells?.FirstOrDefault(c => c.Index == (p.Cell < 0 ? 0 : p.Cell))?.Name);
        }
        PointsText = $"{d.SpawnPoints?.Count ?? 0} spawn point{((d.SpawnPoints?.Count ?? 0) == 1 ? "" : "s")}";
    }

    public int Index { get; }
    public string Name { get; }
    public string Title => "SQUAD " + Name.ToUpperInvariant();
    public string About { get; }
    /// <summary>Its objective and zone in plain words.</summary>
    public string Starts { get; }
    public bool HasStarts => Starts.Length > 0;
    /// <summary>Which script lines place it (ai_place), set by the map once the script uses are read.</summary>
    [ObservableProperty] private string _placedBy = "";
    public bool HasPlacedBy => PlacedBy.Length > 0;
    partial void OnPlacedByChanged(string value) => OnPropertyChanged(nameof(HasPlacedBy));
    public string PointsText { get; }
    public bool IsTemplated { get; }
    /// <summary>The squad template's name (its file is ai\squad_templates\&lt;name&gt;.squad_template), or "".</summary>
    public string Template { get; }
    public bool HasTemplate => Template.Length > 0;
    public string TemplateButton => "Open template " + Template;
    public string TemplateNote { get; }
    public bool HasTemplateNote => TemplateNote.Length > 0;
    public IReadOnlyList<string> Characters { get; }
    public IReadOnlyList<string> Weapons { get; }
    public IReadOnlyList<string> CharacterOverrides { get; }
    public IReadOnlyList<string> WeaponOverrides { get; }
    /// <summary>"On foot", then the mission's vehicle palette (a vehicle joins it with Use it › Add to map palette).</summary>
    public IReadOnlyList<string> VehicleChoices { get; }
    public ObservableCollection<SquadCellRow> Cells { get; } = new();
    public bool HasCells => Cells.Count > 0;
    public SquadPointRow? Point { get; }
    public bool HasPoint => Point != null;

    [ObservableProperty] private string? _error;

    /// <summary>"objects\characters\elite\ai\elite_officer.character" → "elite officer".</summary>
    public static string PaletteLabel(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return "(empty)";
        return System.IO.Path.GetFileNameWithoutExtension(tag).Replace('_', ' ');
    }

    internal async Task SetAsync(string fieldPath, string value, string what)
    {
        Error = await _map.SetSquadFieldAsync(fieldPath, value, what);
    }
}

public partial class SquadCellRow : ObservableObject
{
    private readonly SquadEditorViewModel _owner;
    private readonly SquadCell _cell;
    private bool _ready;

    public SquadCellRow(SquadEditorViewModel owner, SquadCell cell)
    {
        _owner = owner;
        _cell = cell;
        Name = string.IsNullOrEmpty(cell.Name) ? "cell " + cell.Index : cell.Name;
        _count = cell.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var c in cell.Characters ?? new()) Characters.Add(new SquadChoiceRow(owner, c, owner.Characters, "character"));
        foreach (var w in cell.Weapons ?? new()) Weapons.Add(new SquadChoiceRow(owner, w, owner.Weapons, "weapon"));
        _vehicle = cell.Vehicle + 1;
        _ready = true;
    }

    public string Name { get; }
    /// <summary>False for a templated squad's cells: the game ignores them.</summary>
    public bool CanEdit => !_owner.IsTemplated;
    public ObservableCollection<SquadChoiceRow> Characters { get; } = new();
    public ObservableCollection<SquadChoiceRow> Weapons { get; } = new();
    public bool HasNoCharacter => Characters.Count == 0;
    public bool HasNoWeapon => Weapons.Count == 0;
    /// <summary>Several entries: one is picked at random by chance.</summary>
    public bool ShowChances => Characters.Count > 1 || Weapons.Count > 1;

    [ObservableProperty] private string _count;
    /// <summary>0 on foot, else 1 + the vehicle palette index: the cell's AI spawn in it (the first as driver).</summary>
    [ObservableProperty] private int _vehicle;
    public bool HasVehicleField => _cell.VehiclePath != null;
    public IReadOnlyList<string> VehicleChoices => _owner.VehicleChoices;

    partial void OnVehicleChanged(int value)
    {
        if (!_ready || value < 0 || _cell.VehiclePath == null) return;
        _ = _owner.SetAsync(_cell.VehiclePath, (value - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "squad vehicle");
    }

    partial void OnCountChanged(string value)
    {
        if (!_ready || !int.TryParse(value.Trim(), out int n) || n < 0) return;
        _ = _owner.SetAsync(_cell.CountPath, n.ToString(System.Globalization.CultureInfo.InvariantCulture), "squad count");
    }
}

/// <summary>A character or weapon of a cell: palette pick and chance.</summary>
public partial class SquadChoiceRow : ObservableObject
{
    private readonly SquadEditorViewModel _owner;
    private readonly SquadChoice _choice;
    private readonly string _what;
    private bool _ready;

    public SquadChoiceRow(SquadEditorViewModel owner, SquadChoice choice, IReadOnlyList<string> options, string what)
    {
        _owner = owner;
        _choice = choice;
        _what = what;
        Options = options;
        _selectedIndex = choice.Palette;
        _chance = choice.Chance.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _ready = true;
    }

    public IReadOnlyList<string> Options { get; }
    public bool CanEdit => !_owner.IsTemplated;
    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private string _chance;

    partial void OnSelectedIndexChanged(int value)
    {
        if (!_ready || value < 0) return;
        _ = _owner.SetAsync(_choice.Path, value.ToString(System.Globalization.CultureInfo.InvariantCulture), _what);
    }

    partial void OnChanceChanged(string value)
    {
        if (!_ready || !int.TryParse(value.Trim(), out int n) || n < 0) return;
        _ = _owner.SetAsync(_choice.ChancePath, n.ToString(System.Globalization.CultureInfo.InvariantCulture), _what + " chance");
    }
}

/// <summary>The selected spawn point's own character and weapon (or its cell's).</summary>
public partial class SquadPointRow : ObservableObject
{
    private readonly SquadEditorViewModel _owner;
    private readonly SquadPoint _point;
    private bool _ready;

    public SquadPointRow(SquadEditorViewModel owner, SquadPoint point, string? cellName)
    {
        _owner = owner;
        _point = point;
        Name = point.Name ?? "";
        CellText = "Cell: " + (cellName ?? "the squad's first");
        // Index 0 in the pickers is "From its cell" (-1 in the tag).
        _characterIndex = point.Character + 1;
        _weaponIndex = point.Weapon + 1;
        _ready = true;
    }

    public string Name { get; }
    public string Title => "THIS SPAWN POINT" + (Name.Length > 0 ? " · " + Name : "");
    public string CellText { get; }
    public IReadOnlyList<string> CharacterOptions => _owner.CharacterOverrides;
    public IReadOnlyList<string> WeaponOptions => _owner.WeaponOverrides;
    [ObservableProperty] private int _characterIndex;
    [ObservableProperty] private int _weaponIndex;

    partial void OnCharacterIndexChanged(int value)
    {
        if (!_ready || value < 0) return;
        _ = _owner.SetAsync(_point.CharacterPath, (value - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "spawn point character");
    }

    partial void OnWeaponIndexChanged(int value)
    {
        if (!_ready || value < 0) return;
        _ = _owner.SetAsync(_point.WeaponPath, (value - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "spawn point weapon");
    }
}
