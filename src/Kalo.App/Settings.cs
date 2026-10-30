using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Input;

namespace Kalo.App;

/// <summary>One rebindable action of the map view.</summary>
public sealed record KeyAction(string Id, string Name, string Group, bool Hold = false);

/// <summary>
/// Kalo's user settings (%APPDATA%\Kalo\settings.json): for now the map view's keys.
/// Keys are stored as Avalonia key gestures ("Z", "Ctrl+D"). The first run picks ZQSD on an AZERTY keyboard.
/// </summary>
public sealed class AppSettings
{
    private static AppSettings? s_current;
    /// <summary>Loaded on first use (after the presets below are initialized).</summary>
    public static AppSettings Current => s_current ??= Load();

    /// <summary>Raised after the settings are saved, so views can refresh hints.</summary>
    public static event Action? Changed;

    public static readonly IReadOnlyList<KeyAction> Actions = new[]
    {
        new KeyAction("forward", "Fly forward", "Camera", true),
        new KeyAction("back", "Fly back", "Camera", true),
        new KeyAction("left", "Fly left", "Camera", true),
        new KeyAction("right", "Fly right", "Camera", true),
        new KeyAction("up", "Fly up", "Camera", true),
        new KeyAction("down", "Fly down", "Camera", true),
        new KeyAction("frameSelection", "Frame the selection", "Camera"),
        new KeyAction("frameAll", "Frame the play space", "Camera"),
        new KeyAction("shading", "Cycle shading", "View"),
        new KeyAction("wireframe", "Wireframe", "View"),
        new KeyAction("turn", "Turn 15°", "Objects"),
        new KeyAction("turnBack", "Turn -15°", "Objects"),
        new KeyAction("duplicate", "Duplicate", "Objects"),
        new KeyAction("delete", "Delete", "Objects"),
        new KeyAction("copy", "Copy", "Objects"),
        new KeyAction("cut", "Cut", "Objects"),
        new KeyAction("paste", "Paste", "Objects"),
        new KeyAction("selectAll", "Select all", "Objects"),
        new KeyAction("deselect", "Deselect", "Objects"),
        new KeyAction("undo", "Undo", "Edit"),
        new KeyAction("redo", "Redo", "Edit"),
        new KeyAction("redoAlt", "Redo (other key)", "Edit"),
    };

    public static readonly IReadOnlyDictionary<string, string> Qwerty = new Dictionary<string, string>
    {
        ["forward"] = "W", ["back"] = "S", ["left"] = "A", ["right"] = "D", ["up"] = "E", ["down"] = "Q",
        ["frameSelection"] = "F", ["frameAll"] = "Home", ["shading"] = "Z", ["wireframe"] = "Shift+Z",
        ["turn"] = "R", ["turnBack"] = "Shift+R", ["duplicate"] = "Ctrl+D", ["delete"] = "Delete",
        ["copy"] = "Ctrl+C", ["cut"] = "Ctrl+X", ["paste"] = "Ctrl+V", ["selectAll"] = "Ctrl+A", ["deselect"] = "Escape",
        ["undo"] = "Ctrl+Z", ["redo"] = "Ctrl+Y", ["redoAlt"] = "Ctrl+Shift+Z",
    };

    /// <summary>AZERTY: ZQSD to fly, A/E down/up; shading moves to W (where Z sits on QWERTY).</summary>
    public static readonly IReadOnlyDictionary<string, string> Azerty = new Dictionary<string, string>(Qwerty)
    {
        ["forward"] = "Z", ["back"] = "S", ["left"] = "Q", ["right"] = "D", ["up"] = "E", ["down"] = "A",
        ["shading"] = "W", ["wireframe"] = "Shift+W",
    };

    public string Layout { get; set; } = "qwerty";
    public Dictionary<string, string> Keys { get; set; } = new();

    public string GestureText(string action) =>
        Keys.TryGetValue(action, out var g) ? g : (Layout == "azerty" ? Azerty : Qwerty).GetValueOrDefault(action, "");

    /// <summary>The key a hold action (fly) uses; modifiers are ignored for those.</summary>
    public Key HoldKey(string action) => Parse(GestureText(action))?.Key ?? Key.None;

    /// <summary>True when the event is the action's gesture (exact modifiers).</summary>
    public bool Matches(string action, KeyEventArgs e)
    {
        var g = Parse(GestureText(action));
        return g != null && g.Key == e.Key && g.KeyModifiers == (e.KeyModifiers & ~KeyModifiers.Meta);
    }

    public static KeyGesture? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return KeyGesture.Parse(text); } catch { return null; }
    }

    public void ApplyPreset(string layout)
    {
        Layout = layout;
        Keys = new Dictionary<string, string>(layout == "azerty" ? Azerty : Qwerty);
    }

    // ---- storage ----------------------------------------------------------------------------------------

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kalo", "settings.json");

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s != null) return s;
            }
        }
        catch { }
        var fresh = new AppSettings();
        fresh.ApplyPreset(DetectLayout());
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        s_current = this;
        Changed?.Invoke();
    }

    public AppSettings Clone() => new() { Layout = Layout, Keys = new Dictionary<string, string>(Keys) };

    /// <summary>French and Belgian keyboard layouts are AZERTY.</summary>
    public static string DetectLayout()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                int lang = (int)(GetKeyboardLayout(0).ToInt64() & 0xFFFF);
                if (lang is 0x040C or 0x080C or 0x140C or 0x180C) return "azerty"; // fr-FR, fr-BE, fr-LU, fr-MC
            }
        }
        catch { }
        return "qwerty";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint thread);

    /// <summary>The help line under the map view, from the current keys.</summary>
    public string MapHint()
    {
        string K(string a) => GestureText(a);
        return $"Right-drag look · {K("forward")}{K("left")}{K("back")}{K("right")}/{K("down")}{K("up")} fly (Shift faster) · click select · drag move (Shift up/down, Ctrl snap) · " +
               $"{K("turn")} turn · {K("duplicate")} duplicate · {K("delete")} delete · {K("frameSelection")} frame · {K("shading")} shading · double-click a list row to go to it";
    }
}
