namespace Kalo.Core;

/// <summary>
/// Which gamepad drives which split-screen player in reach_tag_test. The test build gives player 1 the keyboard and
/// mouse and the first gamepad, so Kalo puts its own xinput1_4.dll (tools\xinput, forwards to the system XInput)
/// next to the game, which reports each physical gamepad at the player slot written in kalo_input.txt. In game a pad
/// changes player with Back + D-pad right/left, and F8 moves gamepad 1 to the next player; editing the file while
/// the game runs reassigns the pads too.
/// </summary>
public static class PlaytestInput
{
    public const int Pads = 4;
    /// <summary>Setting values: a player index 0-3, Off, or Auto.</summary>
    public const int Off = -1, Auto = -2;

    public const string DllName = "xinput1_4.dll", ConfigName = "kalo_input.txt", LogName = "kalo_input.log";

    /// <summary>
    /// The player (0-based, or -1) each physical gamepad drives. Auto: alone, gamepad 1 is player 1 (with the keyboard);
    /// in split-screen, gamepads 1, 2, 3 go to players 2, 3, 4 so the keyboard keeps player 1 to itself.
    /// </summary>
    public static int[] Resolve(IReadOnlyList<int>? settings, int players)
    {
        var map = new int[Pads];
        for (int pad = 0; pad < Pads; pad++)
        {
            int s = settings != null && pad < settings.Count ? settings[pad] : Auto;
            map[pad] = s == Auto
                ? (players <= 1 ? (pad == 0 ? 0 : Off) : (pad + 1 < players ? pad + 1 : Off))
                : s >= players ? Off : s;
        }
        return map;
    }

    /// <summary>Copies the routing DLL next to the game when it is missing or different. Returns false (with why) if it can't.</summary>
    public static bool Deploy(string workspace, string dllSource, out string message)
    {
        message = "";
        string target = Path.Combine(workspace, DllName);
        try
        {
            if (!File.Exists(dllSource)) { message = "gamepad routing DLL not found: " + dllSource; return false; }
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(dllSource).Length
                && File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(dllSource))) return true;
            File.Copy(dllSource, target, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            message = "could not install the gamepad routing DLL: " + ex.Message;
            return false;
        }
    }

    public static void WriteConfig(string workspace, int players, int[] map)
    {
        var lines = new List<string>
        {
            "# Written by Kalo before each playtest: physical gamepad -> controller slot (0 = player 1, -1 = ignored).",
            "# In game: Back + D-pad right/left moves a gamepad to the next/previous player; F8 moves gamepad 1.",
            $"players={Math.Clamp(players, 1, Pads)}",
        };
        for (int pad = 0; pad < Pads; pad++) lines.Add($"pad{pad}={map[pad]}");
        File.WriteAllLines(Path.Combine(workspace, ConfigName), lines);
    }

    /// <summary>"Keyboard and mouse: player 1 · gamepad 1: player 2"</summary>
    public static string Describe(int players, int[] map)
    {
        var parts = new List<string> { "keyboard and mouse: player 1" };
        for (int pad = 0; pad < Pads; pad++)
            if (map[pad] >= 0) parts.Add($"gamepad {pad + 1}: player {map[pad] + 1}");
        return string.Join(" · ", parts);
    }
}
