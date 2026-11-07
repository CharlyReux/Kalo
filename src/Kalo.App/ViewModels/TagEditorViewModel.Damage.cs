using CommunityToolkit.Mvvm.ComponentModel;
using Kalo.Core;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// Weapons: what one shot does, shown in the weapon's Simple view. Damage is not in the weapon file but two files away
/// (weapon › projectile › damage effect); this box reads them and writes to them, and Save saves them with the weapon.
/// </summary>
public partial class TagEditorViewModel
{
    [ObservableProperty] private ShotInfo? _shot;
    public bool ShowShot => ShowSimple && Shot != null;
    partial void OnShotChanged(ShotInfo? value) => OnPropertyChanged(nameof(ShowShot));

    /// <summary>Other tags this tab edited (the bullet and its damage file), saved with it.</summary>
    private readonly HashSet<string> _linkedDirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _linkedOpen = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The tab was closed: a load still running closes what it opens and stops.</summary>
    private bool _linkedClosed;

    private async Task LoadShotAsync(TagDocument doc)
    {
        Shot = null;
        if (Group != "weapon" || IsScoped || _main.Host == null) return;
        try
        {
            string? projectile = Find(doc.Fields, "barrels/projectile")?.Value;
            if (string.IsNullOrWhiteSpace(projectile)) return;
            var p = await _main.Host.OpenTagAsync(projectile);
            if (await ClosedMeanwhileAsync(projectile)) return;
            _linkedOpen.Add(projectile);
            var speed = Find(p.Fields, "initial velocity");
            var finalSpeed = Find(p.Fields, "final velocity");
            // Rockets and launchers hurt mostly by their explosion; bullets only on impact.
            string? damagePath = Find(p.Fields, "detonation damage")?.Value;
            bool explosive = !string.IsNullOrWhiteSpace(damagePath);
            if (!explosive) damagePath = Find(p.Fields, "impact damage")?.Value;
            FieldNode? low = null, high = null, radius = null;
            if (!string.IsNullOrWhiteSpace(damagePath))
            {
                var d = await _main.Host.OpenTagAsync(damagePath);
                if (await ClosedMeanwhileAsync(damagePath)) return;
                _linkedOpen.Add(damagePath);
                low = Find(d.Fields, "damage lower bound");
                high = Find(d.Fields, "damage upper bound");
                radius = Find(d.Fields, "radius");
                if (radius != null && Bounds(radius.Value).Max > 0) explosive = true;
            }
            Shot = new ShotInfo(this, projectile, damagePath, explosive, speed, finalSpeed, low, high, radius);
        }
        catch (HostException ex)
        {
            _main.Log($"{Title}: could not read the bullet's damage ({ex.Message}).");
        }
    }

    /// <summary>A field of another tag (the bullet or its damage file), checked by the engine like any field.</summary>
    internal async Task<string?> SetLinkedAsync(string tag, string fieldPath, string value)
    {
        if (_main.Host == null) return null;
        try
        {
            var r = await _main.Host.SetFieldAsync(tag, fieldPath, value);
            _linkedDirty.Add(tag);
            IsDirty = true;
            _main.Log($"{Title}: {System.IO.Path.GetFileName(tag)} › {fieldPath[(fieldPath.LastIndexOf(':') + 1)..]} = {r.Value}");
            return r.Value ?? value;
        }
        catch (HostException ex)
        {
            _main.AddProblem("error", $"{System.IO.Path.GetFileName(tag)}: {ex.Message}", tag);
            return null;
        }
    }

    private async Task SaveLinkedAsync()
    {
        if (_main.Host == null) return;
        foreach (string tag in _linkedDirty.ToList())
        {
            try
            {
                await _main.Host.SaveTagAsync(tag);
                _linkedDirty.Remove(tag);
                _main.Log($"Saved {tag} (edited from {Title})." + (_main.IsGameRunning ? " The running game should reload it." : ""));
                // A tab showing that file reads it again.
                var other = _main.Tabs.OfType<TagEditorViewModel>().FirstOrDefault(t => t != this && t.Path.Equals(tag, StringComparison.OrdinalIgnoreCase));
                if (other != null && !other.IsDirty) await _main.ReloadTabAsync(other);
            }
            catch (HostException ex)
            {
                _main.AddProblem("error", $"Save {System.IO.Path.GetFileName(tag)} failed: {ex.Message}", tag);
            }
        }
    }

    /// <summary>True (and the tag closed again) when the tab was closed while the tag was opening.</summary>
    private async Task<bool> ClosedMeanwhileAsync(string tag)
    {
        if (!_linkedClosed) return false;
        if (!_main.Tabs.OfType<TagEditorViewModel>().Any(t => t.Path.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            try { await _main.Host!.CloseTagAsync(tag); } catch { }
        return true;
    }

    private async Task CloseLinkedAsync()
    {
        _linkedClosed = true;
        if (_main.Host == null) return;
        foreach (string tag in _linkedOpen.ToList())
        {
            if (_main.Tabs.OfType<TagEditorViewModel>().Any(t => t != this && t.Path.Equals(tag, StringComparison.OrdinalIgnoreCase))) continue;
            try { await _main.Host.CloseTagAsync(tag); } catch { }
        }
        _linkedOpen.Clear();
        _linkedDirty.Clear();
    }

    /// <summary>The first field whose path ends with <paramref name="match"/> (names only, as in Simple views).</summary>
    internal static FieldNode? Find(IEnumerable<FieldNode>? nodes, string match)
    {
        if (nodes == null) return null;
        foreach (var n in nodes)
        {
            if (n.Kind is not (ValueKinds.Struct or ValueKinds.Block or ValueKinds.Element or ValueKinds.Explanation or ValueKinds.Array) &&
                ("/" + SimpleView.Normalize(n.Path)).EndsWith("/" + match, StringComparison.OrdinalIgnoreCase)) return n;
            if (Find(n.Children, match) is { } hit) return hit;
        }
        return null;
    }

    internal static (double Min, double Max) Bounds(string? value)
    {
        var parts = (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        double Parse(string s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        return parts.Length switch { 0 => (0, 0), 1 => (Parse(parts[0]), Parse(parts[0])), _ => (Parse(parts[0]), Parse(parts[1])) };
    }
}

/// <summary>One shot of a weapon in plain numbers: damage per hit, bullet speed, blast radius. Edits go to the bullet and damage files.</summary>
public partial class ShotInfo : ObservableObject
{
    private readonly TagEditorViewModel _tab;
    private readonly FieldNode? _speedNode, _finalSpeedNode, _lowNode, _highNode, _radiusNode;
    public string Projectile { get; }
    public string? DamageFile { get; }
    public bool Explosive { get; }

    [ObservableProperty] private string _damage = "";
    [ObservableProperty] private string _speed = "";
    [ObservableProperty] private string _radius = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _error;
    private bool _loading = true;

    public bool HasDamage => _lowNode != null;
    public bool HasSpeed => _speedNode != null;
    public bool HasRadius => _radiusNode != null && Explosive;
    public string DamageLabel => Explosive ? "Explosion damage" : "Damage per hit";
    public string Files => $"Bullet: {System.IO.Path.GetFileNameWithoutExtension(Projectile)}" +
                           (DamageFile != null ? $" · damage: {System.IO.Path.GetFileNameWithoutExtension(DamageFile)}" : "");

    public ShotInfo(TagEditorViewModel tab, string projectile, string? damageFile, bool explosive,
                    FieldNode? speed, FieldNode? finalSpeed, FieldNode? low, FieldNode? high, FieldNode? radius)
    {
        _tab = tab; Projectile = projectile; DamageFile = damageFile; Explosive = explosive;
        _speedNode = speed; _finalSpeedNode = finalSpeed; _lowNode = low; _highNode = high; _radiusNode = radius;
        var (hMin, hMax) = TagEditorViewModel.Bounds(high?.Value);
        double lowV = TagEditorViewModel.Bounds(low?.Value).Min;
        Damage = lowV == hMin && hMin == hMax ? Fmt(hMax) : $"{Fmt(lowV)} to {Fmt(hMax)}";
        Speed = speed?.Value ?? "";
        var (rMin, rMax) = TagEditorViewModel.Bounds(radius?.Value);
        Radius = rMin == rMax ? Fmt(rMax) : $"{Fmt(rMin)}, {Fmt(rMax)}";
        _loading = false;
        UpdateSummary();
    }

    private static string Fmt(double d) => d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    private static bool TryNumber(string s, out double d) =>
        double.TryParse(s.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d);

    /// <summary>Spartan: about 70 shield and 45 health points (Reach's own numbers, before damage modifiers).</summary>
    private void UpdateSummary()
    {
        var parts = new List<string>();
        if (TryNumber(Damage.Split(' ')[^1], out double dmg) && dmg > 0 && !Explosive)
        {
            int shields = (int)Math.Ceiling(70 / dmg), kill = (int)Math.Ceiling(115 / dmg);
            parts.Add($"About {shields} hit{(shields == 1 ? "" : "s")} to break a Spartan's shields, {kill} to kill (body shots; headshots finish sooner)");
        }
        else if (Explosive && TryNumber(Damage.Split(' ')[^1], out double boom) && boom > 0)
            parts.Add(boom >= 115 ? "Kills a Spartan at the center of the blast" : $"A Spartan survives the center of the blast ({Fmt(boom)} of 115 shields + health)");
        if (TryNumber(Speed, out double v) && v > 0 && !Explosive)
            parts.Add(v >= 1000 ? "the bullet hits almost instantly" : $"the bullet takes {Fmt(Math.Round(30 / v, 2))} s to fly 30 units (about 90 m)");
        Summary = string.Join("; ", parts) + (parts.Count > 0 ? "." : "");
    }

    async partial void OnDamageChanged(string value)
    {
        if (_loading || _lowNode == null || DamageFile == null) return;
        Error = null;
        // "20" sets every hit to 20; "15 to 20" (or "15, 20") keeps a range.
        var nums = value.Replace(" to ", ",").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (nums.Length is < 1 or > 2 || !TryNumber(nums[0], out double min) || !TryNumber(nums[^1], out double max) || min < 0 || max < min)
        { Error = "Type a number (e.g. 20) or a range (15 to 20)."; return; }
        await _tab.SetLinkedAsync(DamageFile, _lowNode.Path, Fmt(min));
        if (_highNode != null) await _tab.SetLinkedAsync(DamageFile, _highNode.Path, $"{Fmt(max)}, {Fmt(max)}");
        UpdateSummary();
    }

    async partial void OnSpeedChanged(string value)
    {
        if (_loading || _speedNode == null) return;
        Error = null;
        if (!TryNumber(value, out double v) || v <= 0) { Error = "Speed: a number above 0 (world units per second)."; return; }
        // The bullet keeps its own slowdown: final speed moves with the initial one.
        double oldV = TryNumber(_speedNode.Value, out var o) ? o : v;
        await _tab.SetLinkedAsync(Projectile, _speedNode.Path, Fmt(v));
        if (_finalSpeedNode != null && TryNumber(_finalSpeedNode.Value, out double f) && oldV > 0)
            await _tab.SetLinkedAsync(Projectile, _finalSpeedNode.Path, Fmt(Math.Round(f * v / oldV, 3)));
        _speedNode.Value = Fmt(v);
        UpdateSummary();
    }

    async partial void OnRadiusChanged(string value)
    {
        if (_loading || _radiusNode == null || DamageFile == null) return;
        Error = null;
        var nums = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (nums.Length is < 1 or > 2 || !TryNumber(nums[0], out double inner) || !TryNumber(nums[^1], out double outer) || inner < 0 || outer < inner)
        { Error = "Radius: one number (e.g. 3) or inner, outer (1, 3)."; return; }
        if (nums.Length == 1) inner = Math.Round(outer / 3, 3); // full damage in the inner third, fading to the edge
        await _tab.SetLinkedAsync(DamageFile, _radiusNode.Path, $"{Fmt(inner)}, {Fmt(outer)}");
        UpdateSummary();
    }
}

/// <summary>New weapon from this one, and the Use it buttons (place it in a map, add it to a map's palette).</summary>
public partial class TagEditorViewModel
{
    public bool IsWeapon => Group == "weapon" && !IsScoped;
    /// <summary>Weapons, vehicles, scenery, crates...: files a map can place.</summary>
    public bool IsPlaceable => !IsScoped && MapEditorViewModel.CategoryFor(Path) != null;
    [ObservableProperty] private string _useStatus = "";

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task PlaceInMap()
    {
        if (IsDirty) { UseStatus = "Save first: maps use the file as it is on disk."; return; }
        string? map = _main.LastMap?.Title;
        UseStatus = await _main.PlaceInLastMapAsync(Path) is { } why ? why : $"Placed in {map} in front of its camera. Save the map to keep it.";
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task AddToMapPalette()
    {
        if (IsDirty) { UseStatus = "Save first: maps use the file as it is on disk."; return; }
        string? map = _main.LastMap?.Title;
        UseStatus = await _main.AddToLastMapPaletteAsync(Path) is { } why ? why
            : $"Added to {map}'s palette: its squads can carry it (Squad section › Weapon) and it is listed for placing. Save the map to keep it.";
    }
}
