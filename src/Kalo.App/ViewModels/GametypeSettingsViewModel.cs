using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core.Scripting;

namespace Kalo.App.ViewModels;

/// <summary>
/// The Settings panel of a Kalo Script gametype, like ReachVariantTool's: base settings, lobby options, player traits,
/// map objects and constants as forms. Every change rewrites only that declaration's lines in the script.
/// </summary>
public partial class GametypeSettingsViewModel : ObservableObject
{
    private readonly ScriptEditorViewModel _script;
    private bool _loading;

    public GametypeSettingsViewModel(ScriptEditorViewModel script)
    {
        _script = script;
        Loadouts = new LoadoutsViewModel(script);
    }

    /// <summary>Settings › Loadouts.</summary>
    public LoadoutsViewModel Loadouts { get; }

    public ObservableCollection<SettingRow> Settings { get; } = new();
    public ObservableCollection<OptionCard> Options { get; } = new();
    public ObservableCollection<TraitsCard> Traits { get; } = new();
    public ObservableCollection<ObjectsRow> Objects { get; } = new();
    public ObservableCollection<ConstantRow> Constants { get; } = new();
    [ObservableProperty] private string _status = "";

    public bool Loading => _loading;

    /// <summary>Reads the declarations from the script's current text.</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            var items = GametypeSettings.Parse(_script.Text);
            Settings.Clear();
            foreach (var k in GametypeSettings.Settings)
            {
                var it = items.FirstOrDefault(i => i.Kind == DeclKind.Setting && i.Name == k.Name);
                Settings.Add(new SettingRow(this, k.Name, k.Label, k.Help, k.Values, it));
            }
            // loadout_palette lines belong to the Loadouts section.
            foreach (var it in items.Where(i => i.Kind == DeclKind.Setting && i.Name != "loadout_palette" && GametypeSettings.Settings.All(k => k.Name != i.Name)))
                Settings.Add(new SettingRow(this, it.Name, Pretty(it.Name), "", Array.Empty<string>(), it));

            Traits.Clear();
            var baseTraits = items.FirstOrDefault(i => i.Kind == DeclKind.SettingBlock && i.Name == "base_player_traits");
            Traits.Add(new TraitsCard(this, baseTraits ?? new DeclItem { Kind = DeclKind.SettingBlock, Name = "base_player_traits" }, isBase: true, exists: baseTraits != null));
            foreach (var it in items.Where(i => i.Kind == DeclKind.Traits)) Traits.Add(new TraitsCard(this, it, false, true));

            Options.Clear();
            foreach (var it in items.Where(i => i.Kind == DeclKind.Option)) Options.Add(new OptionCard(this, it));
            Objects.Clear();
            foreach (var it in items.Where(i => i.Kind == DeclKind.Objects)) Objects.Add(new ObjectsRow(this, it));
            Constants.Clear();
            foreach (var it in items.Where(i => i.Kind == DeclKind.Constant)) Constants.Add(new ConstantRow(this, it));
            Loadouts.Reload();
            Status = $"{items.Count(i => i.Kind != DeclKind.Other)} declarations. Changes are written into the script.";
        }
        finally
        {
            _loading = false;
        }
    }

    public static string Pretty(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..].Replace('_', ' ');

    /// <summary>
    /// Writes one declaration: replaces the one found by kind and original name, adds it when new, removes it when
    /// replacement is null. Returns the item as now parsed (with its lines) or null.
    /// </summary>
    internal DeclItem? Write(DeclKind kind, string? originalName, DeclItem? replacement)
    {
        if (_loading) return null;
        var items = GametypeSettings.Parse(_script.Text);
        var old = originalName == null ? null : items.FirstOrDefault(i => SameKind(i.Kind, kind) && i.Name == originalName);
        string text;
        try
        {
            if (old != null) text = GametypeSettings.Replace(_script.Text, old, replacement == null ? null : GametypeSettings.RenderFor(_script.Text, replacement));
            else if (replacement != null) text = GametypeSettings.Insert(_script.Text, items, kind, GametypeSettings.RenderFor(_script.Text, replacement));
            else return null;
        }
        catch (Exception ex)
        {
            Status = "Could not write it: " + ex.Message;
            return null;
        }
        _script.Text = text;
        var check = KaloScript.Transpile(text);
        Status = check.Succeeded ? "Saved into the script (unsaved file: Ctrl+S)." : $"The script has an error now: line {check.Errors[0].Line}: {check.Errors[0].Message}";
        return replacement == null ? null : GametypeSettings.Parse(text).FirstOrDefault(i => SameKind(i.Kind, kind) && i.Name == replacement.Name);
    }

    private static bool SameKind(DeclKind a, DeclKind b) => a == b || (a is DeclKind.Setting or DeclKind.SettingBlock && b is DeclKind.Setting or DeclKind.SettingBlock);

    private string FreeName(string stem, IEnumerable<string> taken)
    {
        var set = taken.ToHashSet();
        if (!set.Contains(stem)) return stem;
        for (int n = 2; ; n++) if (!set.Contains($"{stem}_{n}")) return $"{stem}_{n}";
    }

    [RelayCommand]
    private void AddOption()
    {
        var d = new DeclItem { Kind = DeclKind.Option, Name = FreeName("my_option", Options.Select(o => o.Name)), Title = "\"My Option\"", Description = "\"\"", Value = "1", IsRanged = true, Min = "0", Max = "10" };
        if (Write(DeclKind.Option, null, d) is { } added) Options.Add(new OptionCard(this, added));
    }

    [RelayCommand]
    private void AddTraits()
    {
        var d = new DeclItem { Kind = DeclKind.Traits, Name = FreeName("my_traits", Traits.Select(o => o.Name)), Title = "\"My Traits\"", Description = "\"\"" };
        d.Rows.Add(("speed", "100"));
        if (Write(DeclKind.Traits, null, d) is { } added) Traits.Add(new TraitsCard(this, added, false, true));
    }

    [RelayCommand]
    private void AddObjects()
    {
        var d = new DeclItem { Kind = DeclKind.Objects, Name = FreeName("my_objects", Objects.Select(o => o.Name)) };
        d.Rows.Add(("label", "\"my_label\""));
        if (Write(DeclKind.Objects, null, d) is { } added) Objects.Add(new ObjectsRow(this, added));
    }

    [RelayCommand]
    private void AddConstant()
    {
        var d = new DeclItem { Kind = DeclKind.Constant, Name = FreeName("k_my_constant", Constants.Select(o => o.Name)), Value = "0" };
        if (Write(DeclKind.Constant, null, d) is { } added) Constants.Add(new ConstantRow(this, added));
    }

    internal void Removed(object card)
    {
        switch (card)
        {
            case OptionCard o: Options.Remove(o); break;
            case TraitsCard t: Traits.Remove(t); break;
            case ObjectsRow r: Objects.Remove(r); break;
            case ConstantRow c: Constants.Remove(c); break;
        }
    }
}

/// <summary>A base setting: empty = the default (no line in the script).</summary>
public partial class SettingRow : ObservableObject
{
    private readonly GametypeSettingsViewModel _owner;
    private bool _exists;

    public SettingRow(GametypeSettingsViewModel owner, string name, string label, string help, string[] values, DeclItem? item)
    {
        _owner = owner;
        Name = name;
        Label = label;
        Help = help;
        Suggestions = values;
        _exists = item != null;
        _value = item?.Value ?? "";
        _isLocked = item?.Prefix.Contains("locked") == true;
    }

    public string Name { get; }
    public string Label { get; }
    public string Help { get; }
    public string[] Suggestions { get; }
    public bool HasSuggestions => Suggestions.Length > 0;
    [ObservableProperty] private string _value;
    [ObservableProperty] private bool _isLocked;

    partial void OnValueChanged(string value) => Commit();
    partial void OnIsLockedChanged(bool value) => Commit();

    [RelayCommand]
    private void Pick(string? value)
    {
        if (value != null) Value = value;
    }

    private void Commit()
    {
        if (_owner.Loading) return;
        string v = Value.Trim();
        var d = v.Length == 0 ? null : new DeclItem { Kind = DeclKind.Setting, Name = Name, Value = v, Prefix = IsLocked ? "locked " : "" };
        _owner.Write(DeclKind.Setting, _exists ? Name : null, d);
        _exists = d != null;
    }
}

/// <summary>A lobby option: a number range, or a list of values.</summary>
public partial class OptionCard : ObservableObject
{
    private readonly GametypeSettingsViewModel _owner;
    private string _original;
    private bool _loading = true;

    public OptionCard(GametypeSettingsViewModel owner, DeclItem item)
    {
        _owner = owner;
        _original = item.Name;
        _name = item.Name;
        _title = GametypeSettings.Unquote(item.Title);
        _description = GametypeSettings.Unquote(item.Description);
        _default = item.Value;
        _isRanged = item.IsRanged;
        _min = item.Min;
        _max = item.Max;
        _isHidden = item.Prefix.Contains("hidden");
        foreach (var (v, l) in item.Values) Values.Add(new ValueRow(this, v, GametypeSettings.Unquote(l)));
        _loading = false;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _title;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _default;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsList))] private bool _isRanged;
    [ObservableProperty] private string _min;
    [ObservableProperty] private string _max;
    [ObservableProperty] private bool _isHidden;
    public bool IsList => !IsRanged;
    public ObservableCollection<ValueRow> Values { get; } = new();

    partial void OnNameChanged(string value) => Commit();
    partial void OnTitleChanged(string value) => Commit();
    partial void OnDescriptionChanged(string value) => Commit();
    partial void OnDefaultChanged(string value) => Commit();
    partial void OnIsRangedChanged(bool value)
    {
        if (!value && Values.Count == 0 && !_loading)
        {
            _loading = true;
            foreach (var v in new[] { Min, Default, Max }.Distinct().Where(x => x.Length > 0)) Values.Add(new ValueRow(this, v, ""));
            _loading = false;
        }
        Commit();
    }
    partial void OnMinChanged(string value) => Commit();
    partial void OnMaxChanged(string value) => Commit();
    partial void OnIsHiddenChanged(bool value) => Commit();

    [RelayCommand]
    private void AddValue()
    {
        int next = Values.Select(v => int.TryParse(v.Value, out int n) ? n : 0).DefaultIfEmpty(-1).Max() + 1;
        Values.Add(new ValueRow(this, next.ToString(), ""));
        Commit();
    }

    [RelayCommand]
    private void RemoveValue(ValueRow? row)
    {
        if (row == null || Values.Count <= 1) return;
        Values.Remove(row);
        Commit();
    }

    [RelayCommand]
    private void Remove()
    {
        _owner.Write(DeclKind.Option, _original, null);
        _owner.Removed(this);
    }

    internal void Commit()
    {
        if (_loading || _owner.Loading || string.IsNullOrWhiteSpace(Name)) return;
        var d = new DeclItem
        {
            Kind = DeclKind.Option, Name = Name.Trim(), Title = GametypeSettings.Quote(Title), Description = GametypeSettings.Quote(Description),
            Value = Default.Trim().Length > 0 ? Default.Trim() : "0", IsRanged = IsRanged, Min = Min.Trim().Length > 0 ? Min.Trim() : "0",
            Max = Max.Trim().Length > 0 ? Max.Trim() : "10", Prefix = IsHidden ? "hidden " : "",
        };
        foreach (var v in Values) d.Values.Add((v.Value.Trim().Length > 0 ? v.Value.Trim() : "0", v.Label.Length > 0 ? GametypeSettings.Quote(v.Label) : ""));
        if (!IsRanged && d.Values.Count == 0) d.Values.Add((d.Value, ""));
        if (_owner.Write(DeclKind.Option, _original, d) != null) _original = d.Name;
    }
}

public partial class ValueRow : ObservableObject
{
    private readonly OptionCard _card;
    public ValueRow(OptionCard card, string value, string label)
    {
        _card = card;
        _value = value;
        _label = label;
    }
    public OptionCard Card => _card;
    [ObservableProperty] private string _value;
    [ObservableProperty] private string _label;
    partial void OnValueChanged(string value) => _card.Commit();
    partial void OnLabelChanged(string value) => _card.Commit();
}

/// <summary>Player traits, or the base player traits (setting base_player_traits).</summary>
public partial class TraitsCard : ObservableObject
{
    private readonly GametypeSettingsViewModel _owner;
    private string _original;
    private bool _exists;
    private bool _loading = true;

    public TraitsCard(GametypeSettingsViewModel owner, DeclItem item, bool isBase, bool exists)
    {
        _owner = owner;
        IsBase = isBase;
        _exists = exists;
        _original = item.Name;
        _name = item.Name;
        _title = GametypeSettings.Unquote(item.Title);
        _description = GametypeSettings.Unquote(item.Description);
        foreach (var (k, v) in item.Rows) Rows.Add(new TraitRow(this, k, v));
        _loading = false;
    }

    public bool IsBase { get; }
    public bool IsNamed => !IsBase;
    public string Heading => IsBase ? "Base player traits (everyone, at spawn)" : "Traits";
    public string RemoveLabel => IsBase ? "Clear" : "Remove";
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _title;
    [ObservableProperty] private string _description;
    public ObservableCollection<TraitRow> Rows { get; } = new();
    /// <summary>Trait keys not used yet, to add.</summary>
    public IEnumerable<string> AvailableKeys => GametypeSettings.TraitKeys.Where(k => Rows.All(r => r.Key != k.Name)).Select(k => k.Label);
    [ObservableProperty] private string? _newKey;

    partial void OnNameChanged(string value) => Commit();
    partial void OnTitleChanged(string value) => Commit();
    partial void OnDescriptionChanged(string value) => Commit();

    partial void OnNewKeyChanged(string? label)
    {
        if (label == null || _loading) return;
        var value = GametypeSettings.TraitKeys.FirstOrDefault(k => k.Label == label);
        if (value == null) return;
        Rows.Add(new TraitRow(this, value.Name, value.Values.FirstOrDefault() ?? "0"));
        Commit();
        _loading = true;
        NewKey = null;
        _loading = false;
        OnPropertyChanged(nameof(AvailableKeys));
    }

    [RelayCommand]
    private void RemoveRow(TraitRow? row)
    {
        if (row == null) return;
        Rows.Remove(row);
        Commit();
        OnPropertyChanged(nameof(AvailableKeys));
    }

    [RelayCommand]
    private void Remove()
    {
        if (IsBase) { Rows.Clear(); Commit(); return; }
        _owner.Write(DeclKind.Traits, _original, null);
        _owner.Removed(this);
    }

    internal void Commit()
    {
        if (_loading || _owner.Loading) return;
        DeclItem? d;
        if (IsBase)
        {
            d = Rows.Count == 0 ? null : new DeclItem { Kind = DeclKind.SettingBlock, Name = "base_player_traits" };
            foreach (var r in Rows) d?.Rows.Add((r.Key, r.Value.Trim().Length > 0 ? r.Value.Trim() : "0"));
            _owner.Write(DeclKind.SettingBlock, _exists ? "base_player_traits" : null, d);
            _exists = d != null;
            return;
        }
        if (string.IsNullOrWhiteSpace(Name)) return;
        d = new DeclItem { Kind = DeclKind.Traits, Name = Name.Trim(), Title = GametypeSettings.Quote(Title), Description = GametypeSettings.Quote(Description) };
        foreach (var r in Rows) d.Rows.Add((r.Key, r.Value.Trim().Length > 0 ? r.Value.Trim() : "0"));
        if (_owner.Write(DeclKind.Traits, _original, d) != null) _original = d.Name;
    }
}

public partial class TraitRow : ObservableObject
{
    private readonly TraitsCard _card;
    public TraitRow(TraitsCard card, string key, string value)
    {
        _card = card;
        Key = key;
        _value = value;
        var known = GametypeSettings.TraitKeys.FirstOrDefault(k => k.Name == key);
        Label = known?.Label ?? GametypeSettingsViewModel.Pretty(key);
        Help = known?.Help ?? "";
        Suggestions = known?.Values ?? Array.Empty<string>();
    }
    public TraitsCard Card => _card;
    public string Key { get; }
    public string Label { get; }
    public string Help { get; }
    public string[] Suggestions { get; }
    public bool HasSuggestions => Suggestions.Length > 0;
    [ObservableProperty] private string _value;
    partial void OnValueChanged(string value) => _card.Commit();

    [RelayCommand]
    private void Pick(string? value)
    {
        if (value != null) Value = value;
    }
}

/// <summary>objects NAME with label/type "x" [min N].</summary>
public partial class ObjectsRow : ObservableObject
{
    private readonly GametypeSettingsViewModel _owner;
    private string _original;
    private readonly List<(string Key, string Value)> _extra = new();

    public ObjectsRow(GametypeSettingsViewModel owner, DeclItem item)
    {
        _owner = owner;
        _original = item.Name;
        _name = item.Name;
        var pick = item.Rows.FirstOrDefault(r => r.Key is "label" or "type");
        _by = pick.Key ?? "label";
        _value = GametypeSettings.Unquote(pick.Value ?? "");
        _min = item.Rows.FirstOrDefault(r => r.Key == "min").Value ?? "";
        _extra.AddRange(item.Rows.Where(r => r.Key is not ("label" or "type" or "min")));
    }

    public static string[] ByChoices { get; } = { "label", "type" };
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _by;
    [ObservableProperty] private string _value;
    [ObservableProperty] private string _min;
    partial void OnNameChanged(string value) => Commit();
    partial void OnByChanged(string value) => Commit();
    partial void OnValueChanged(string value) => Commit();
    partial void OnMinChanged(string value) => Commit();

    [RelayCommand]
    private void Remove()
    {
        _owner.Write(DeclKind.Objects, _original, null);
        _owner.Removed(this);
    }

    private void Commit()
    {
        if (_owner.Loading || string.IsNullOrWhiteSpace(Name)) return;
        var d = new DeclItem { Kind = DeclKind.Objects, Name = Name.Trim() };
        d.Rows.Add((By, "\"" + Value.Trim().Trim('"') + "\""));
        if (Min.Trim().Length > 0) d.Rows.Add(("min", Min.Trim()));
        d.Rows.AddRange(_extra);
        if (_owner.Write(DeclKind.Objects, _original, d) != null) _original = d.Name;
    }
}

public partial class ConstantRow : ObservableObject
{
    private readonly GametypeSettingsViewModel _owner;
    private string _original;

    public ConstantRow(GametypeSettingsViewModel owner, DeclItem item)
    {
        _owner = owner;
        _original = item.Name;
        _name = item.Name;
        _value = item.Value;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _value;
    partial void OnNameChanged(string value) => Commit();
    partial void OnValueChanged(string value) => Commit();

    [RelayCommand]
    private void Remove()
    {
        _owner.Write(DeclKind.Constant, _original, null);
        _owner.Removed(this);
    }

    private void Commit()
    {
        if (_owner.Loading || string.IsNullOrWhiteSpace(Name)) return;
        var d = new DeclItem { Kind = DeclKind.Constant, Name = Name.Trim(), Value = Value.Trim().Length > 0 ? Value.Trim() : "0" };
        if (_owner.Write(DeclKind.Constant, _original, d) != null) _original = d.Name;
    }
}
