namespace Kalo.Core;

/// <summary>Where things are: the Kalo folder, the shared workspace, HREK, MCC, the Blam Host.</summary>
public sealed class KaloPaths
{
    public const string DefaultHrek = @"D:\SteamLibrary\steamapps\common\HREK";

    public required string Repo { get; init; }
    public required string Workspace { get; init; }
    public required string Hrek { get; init; }

    public string WorkspaceTags => Path.Combine(Workspace, "tags");
    public string HrekTags => Path.Combine(Hrek, "tags");
    public string MegaloSource => Path.Combine(Workspace, "data", "multiplayer", "megalo");
    public string MegaloObjectLists => Path.Combine(MegaloSource, "object_lists");
    /// <summary>Kalo's own scratch and cache folder (repo\.kalo), never inside the workspace.</summary>
    public string KaloData => Path.Combine(Repo, ".kalo");
    public string? Mcc
    {
        get
        {
            string? parent = Path.GetDirectoryName(Hrek.TrimEnd('\\'));
            return parent == null ? null : Path.Combine(parent, "Halo The Master Chief Collection");
        }
    }

    /// <summary>Finds the Kalo folder (the one holding Kalo.sln) from the current directory or the app's own folder.</summary>
    public static KaloPaths Discover(string? workspace = null, string? hrek = null)
    {
        string repo = FindRepo(Directory.GetCurrentDirectory()) ?? FindRepo(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("Could not find the Kalo folder (Kalo.sln) above " + AppContext.BaseDirectory);
        workspace ??= Environment.GetEnvironmentVariable("KALO_WORKSPACE") ?? Path.Combine(repo, ".kalo", "shared");
        hrek ??= Environment.GetEnvironmentVariable("KALO_HREK") ?? DefaultHrek;
        var paths = new KaloPaths
        {
            Repo = repo,
            Workspace = Path.GetFullPath(workspace).TrimEnd('\\'),
            Hrek = Path.GetFullPath(hrek).TrimEnd('\\')
        };
        if (IsUnder(paths.Workspace, paths.Hrek)) throw new InvalidOperationException("The workspace must not be inside HREK.");
        return paths;
    }

    /// <summary>The Blam Host exe: next to the app (blamhost\) when deployed, else the build output in src.</summary>
    public string FindBlamHost()
    {
        // A test build (bin\test\...) uses the test build of the host next to it.
        bool test = AppContext.BaseDirectory.Contains(Path.Combine("bin", "test"), StringComparison.OrdinalIgnoreCase);
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "blamhost", "kalo-blamhost.exe"),
            test ? Path.Combine(Repo, "src", "Kalo.BlamHost", "bin", "test", "Debug", "net48", "kalo-blamhost.exe") : "",
            Path.Combine(Repo, "src", "Kalo.BlamHost", "bin", "Debug", "net48", "kalo-blamhost.exe"),
            Path.Combine(Repo, "src", "Kalo.BlamHost", "bin", "Release", "net48", "kalo-blamhost.exe"),
        };
        return candidates.Where(c => c.Length > 0).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("kalo-blamhost.exe not found. Build the solution first.");
    }

    public static bool IsUnder(string path, string root)
    {
        string p = Path.GetFullPath(path).TrimEnd('\\');
        string r = Path.GetFullPath(root).TrimEnd('\\');
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindRepo(string start)
    {
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Kalo.sln"))) return dir.FullName;
        return null;
    }
}
