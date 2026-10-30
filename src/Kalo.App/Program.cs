using Avalonia;

namespace Kalo.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppArgs.Current = AppArgs.Parse(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

/// <summary>
/// Command-line options, mostly for automated checks through the Kalo runner:
///   --open &lt;tag path&gt;          open a tag on start (relative to tags\, with extension)
///   --select &lt;field path&gt;      select a field in that tag
///   --screenshot &lt;png&gt;         render the window to a PNG once loaded
///   --screenshot-delay &lt;s&gt;     wait before the screenshot (default 3)
///   --exit                     close after the screenshot
///   --self-test &lt;dir&gt;          drive the whole loop (edit, save, play, hot reload, revert) and write shots to dir
///   --tour &lt;dir&gt;               guided checks of every feature; --tour-sections map,script runs only some
///   --self-test-hold &lt;s&gt;       keep the game running this long after the reload (default 30), for a game screenshot
///   --workspace &lt;dir&gt;, --hrek &lt;dir&gt;
/// </summary>
public sealed class AppArgs
{
    public static AppArgs Current { get; set; } = new();

    public string? Open { get; private set; }
    public string? Select { get; private set; }
    public string? Screenshot { get; private set; }
    public double ScreenshotDelay { get; private set; } = 3;
    public bool Exit { get; private set; }
    public string? SelfTest { get; private set; }
    public string? Tour { get; private set; }
    /// <summary>--tour-sections map,script: run only these tour sections.</summary>
    public string[]? TourSections { get; private set; }
    public bool MapPlay { get; private set; }
    public bool MegaloPlay { get; private set; }
    public bool ModPlay { get; private set; }
    public bool BarebonePlay { get; private set; }
    public double SelfTestHold { get; private set; } = 30;
    public string? Workspace { get; private set; }
    public string? Hrek { get; private set; }

    public static AppArgs Parse(string[] args)
    {
        var a = new AppArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string next = i + 1 < args.Length ? args[i + 1] : "";
            switch (args[i])
            {
                case "--open": a.Open = next; i++; break;
                case "--select": a.Select = next; i++; break;
                case "--screenshot": a.Screenshot = next; i++; break;
                case "--screenshot-delay": a.ScreenshotDelay = double.Parse(next, System.Globalization.CultureInfo.InvariantCulture); i++; break;
                case "--exit": a.Exit = true; break;
                case "--self-test": a.SelfTest = next; i++; break;
                case "--tour": a.Tour = next; i++; break;
                case "--tour-sections": a.TourSections = next.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); i++; break;
                case "--map-play-test": a.Tour = next; a.MapPlay = true; i++; break;
                case "--megalo-play-test": a.Tour = next; a.MegaloPlay = true; i++; break;
                case "--mod-play-test": a.Tour = next; a.ModPlay = true; i++; break;
                case "--barebone-play-test": a.Tour = next; a.BarebonePlay = true; i++; break;
                case "--self-test-hold": a.SelfTestHold = double.Parse(next, System.Globalization.CultureInfo.InvariantCulture); i++; break;
                case "--workspace": a.Workspace = next; i++; break;
                case "--hrek": a.Hrek = next; i++; break;
            }
        }
        return a;
    }
}
