using System.IO.Compression;
using System.Text.Json;

namespace Kalo.Core;

/// <summary>
/// Mod projects on top of the one shared workspace. A project is a small folder (Kalo\projects\&lt;name&gt;) holding
/// only the files the mod changes, mirrored from the workspace (files\tags\..., files\data\...), plus
/// kalo-project.json. The workspace shows one project at a time: switching saves the current project's files,
/// puts the workspace back to vanilla, then copies the other project's files in.
/// </summary>
public sealed class ModProjects
{
    public const string ManifestName = "kalo-project.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly KaloPaths _paths;
    private readonly Baseline _baseline;
    /// <summary>Saves (after every save in the app), switches and exports must not overlap.</summary>
    private readonly object _gate = new();

    public ModProjects(KaloPaths paths, Baseline baseline)
    {
        _paths = paths;
        _baseline = baseline;
    }

    public string Root => Path.Combine(_paths.Repo, "projects");
    public string ExportRoot => Path.Combine(_paths.Repo, "exports");
    private string ActiveFile => Path.Combine(_paths.KaloData, "active-project.txt");

    public string? ActiveName
    {
        get => File.Exists(ActiveFile) ? File.ReadAllText(ActiveFile).Trim() is { Length: > 0 } n ? n : null : null;
        private set
        {
            Directory.CreateDirectory(_paths.KaloData);
            File.WriteAllText(ActiveFile, value ?? "");
        }
    }

    public List<ModProject> List()
    {
        var list = new List<ModProject>();
        if (!Directory.Exists(Root)) return list;
        foreach (string dir in Directory.GetDirectories(Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var p = Load(dir);
            if (p != null) list.Add(p);
        }
        return list;
    }

    public ModProject? Find(string name) => List().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static ModProject? Load(string dir)
    {
        string file = Path.Combine(dir, ManifestName);
        if (!File.Exists(file)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(file), Json);
            return m == null ? null : new ModProject(dir, m);
        }
        catch { return null; }
    }

    private static void SaveManifest(ModProject p)
    {
        p.Manifest.Updated = DateTime.Now;
        File.WriteAllText(Path.Combine(p.Folder, ManifestName), JsonSerializer.Serialize(p.Manifest, Json));
    }

    public ModProject? Active => ActiveName is { } n ? Find(n) : null;

    /// <summary>Changes the active project's manifest and saves it.</summary>
    public void UpdateActive(Action<ProjectManifest> change)
    {
        lock (_gate)
        {
            var p = Active;
            if (p == null) return;
            change(p.Manifest);
            SaveManifest(p);
        }
    }

    /// <summary>New project. If no project is active, the workspace's current changes become this project's.</summary>
    public ModProject Create(string name, string? author, string? description)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Give the project a name.");
        string folderName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string dir = Path.Combine(Root, folderName);
        if (Directory.Exists(dir)) throw new InvalidOperationException("A project with that name already exists.");
        Directory.CreateDirectory(Path.Combine(dir, "files"));
        var p = new ModProject(dir, new ProjectManifest { Name = name, Author = author ?? "", Description = description ?? "", Created = DateTime.Now });
        SaveManifest(p);
        if (ActiveName == null)
        {
            ActiveName = p.Name;
            Capture(p);
        }
        return p;
    }

    /// <summary>Copies the workspace's changed files into the project (and forgets files that went back to vanilla).</summary>
    public CaptureResult Capture(ModProject p)
    {
        lock (_gate) return CaptureCore(p);
    }

    private CaptureResult CaptureCore(ModProject p)
    {
        var changes = _baseline.Scan();
        string files = Path.Combine(p.Folder, "files");
        Directory.CreateDirectory(files);
        int copied = 0;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in changes.Where(c => c.Kind != ChangeKind.Deleted))
        {
            keep.Add(c.Path);
            string src = Path.Combine(_paths.Workspace, c.Path);
            string dst = Path.Combine(files, c.Path);
            var si = new FileInfo(src);
            var di = new FileInfo(dst);
            if (di.Exists && di.Length == si.Length && di.LastWriteTimeUtc == si.LastWriteTimeUtc) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, true);
            File.SetLastWriteTimeUtc(dst, si.LastWriteTimeUtc);
            copied++;
        }
        int removed = 0;
        foreach (string f in Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories).ToList())
        {
            string rel = f.Substring(files.Length + 1);
            if (keep.Contains(rel)) continue;
            File.Delete(f);
            removed++;
        }
        p.Manifest.Files = keep.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        p.Manifest.Deleted = changes.Where(c => c.Kind == ChangeKind.Deleted).Select(c => c.Path).ToList();
        SaveManifest(p);
        return new CaptureResult(p.Manifest.Files.Count, copied, removed, p.Manifest.Deleted.Count);
    }

    /// <summary>Saves the active project, resets the workspace to vanilla and loads <paramref name="target"/> (null = plain vanilla).</summary>
    public void Switch(ModProject? target, Action<string>? progress = null)
    {
        lock (_gate) SwitchCore(target, progress);
    }

    private void SwitchCore(ModProject? target, Action<string>? progress)
    {
        var current = ActiveName == null ? null : Find(ActiveName);
        if (current != null)
        {
            progress?.Invoke($"Saving {current.Name}…");
            Capture(current);
        }
        progress?.Invoke("Putting the workspace back to vanilla…");
        foreach (var c in _baseline.Scan()) _baseline.Revert(c.Path);
        if (target != null)
        {
            progress?.Invoke($"Loading {target.Name}…");
            string files = Path.Combine(target.Folder, "files");
            if (Directory.Exists(files))
                foreach (string f in Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(files.Length + 1);
                    string dst = Path.Combine(_paths.Workspace, rel);
                    if (!KaloPaths.IsUnder(dst, _paths.Workspace)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(f, dst, true);
                    File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(f));
                }
            foreach (string rel in target.Manifest.Deleted ?? new())
            {
                string dst = Path.Combine(_paths.Workspace, rel);
                if (KaloPaths.IsUnder(dst, _paths.Workspace) && File.Exists(dst)) File.Delete(dst);
            }
        }
        ActiveName = target?.Name;
    }

    /// <summary>
    /// Imports a mod zip made by Export (or laid out the same way: kalo-project.json + tags\... / data\...) as a new,
    /// inactive project. Only files under tags\ and data\ are taken.
    /// </summary>
    public ModProject Import(string zipPath)
    {
        lock (_gate)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            ProjectManifest manifest = new() { Name = Path.GetFileNameWithoutExtension(zipPath) };
            var entry = zip.GetEntry(ManifestName);
            if (entry != null)
            {
                using var r = new StreamReader(entry.Open());
                manifest = JsonSerializer.Deserialize<ProjectManifest>(r.ReadToEnd(), Json) ?? manifest;
            }
            string baseName = string.IsNullOrWhiteSpace(manifest.Name) ? "Imported mod" : manifest.Name.Trim();
            string name = baseName;
            for (int i = 2; Find(name) != null || Directory.Exists(Path.Combine(Root, Safe(name))); i++) name = $"{baseName} ({i})";
            string dir = Path.Combine(Root, Safe(name));
            string files = Path.Combine(dir, "files");
            Directory.CreateDirectory(files);
            var imported = new List<string>();
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                string rel = e.FullName.Replace('/', '\\');
                if (!(rel.StartsWith(@"tags\", StringComparison.OrdinalIgnoreCase) || rel.StartsWith(@"data\", StringComparison.OrdinalIgnoreCase))) continue;
                string target = Path.GetFullPath(Path.Combine(files, rel));
                if (!KaloPaths.IsUnder(target, files)) continue; // no "..\" tricks
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, true);
                imported.Add(rel);
            }
            manifest.Name = name;
            manifest.Files = imported.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            manifest.Deleted ??= new();
            if (manifest.Created == default) manifest.Created = DateTime.Now;
            var p = new ModProject(dir, manifest);
            SaveManifest(p);
            return p;
        }
    }

    private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Deletes a project folder (only inside Kalo\projects). Does not touch the workspace.</summary>
    public void Delete(ModProject p)
    {
        if (!KaloPaths.IsUnder(p.Folder, Root)) throw new InvalidOperationException("Not a Kalo project folder: " + p.Folder);
        if (p.Name.Equals(ActiveName, StringComparison.OrdinalIgnoreCase)) ActiveName = null;
        Directory.Delete(p.Folder, true);
    }

    /// <summary>Zips the project's files and manifest into Kalo\exports, ready to share or to build into a map.</summary>
    public string Export(ModProject p)
    {
        lock (_gate) return ExportCore(p);
    }

    private string ExportCore(ModProject p)
    {
        if (p.Name.Equals(ActiveName, StringComparison.OrdinalIgnoreCase)) Capture(p);
        Directory.CreateDirectory(ExportRoot);
        string zip = Path.Combine(ExportRoot, $"{Path.GetFileName(p.Folder)}-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        if (File.Exists(zip)) File.Delete(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(Path.Combine(p.Folder, ManifestName), ManifestName);
            string files = Path.Combine(p.Folder, "files");
            if (Directory.Exists(files))
                foreach (string f in Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories))
                    archive.CreateEntryFromFile(f, f.Substring(files.Length + 1).Replace('\\', '/'));
            var readme = archive.CreateEntry("README.txt");
            using var w = new StreamWriter(readme.Open());
            w.WriteLine($"{p.Manifest.Name} — a Halo: Reach mod made with Kalo");
            if (!string.IsNullOrWhiteSpace(p.Manifest.Author)) w.WriteLine($"Author: {p.Manifest.Author}");
            if (!string.IsNullOrWhiteSpace(p.Manifest.Description)) w.WriteLine(p.Manifest.Description);
            w.WriteLine();
            w.WriteLine("Contents: the tags and data files this mod changes, laid out like the HREK folder.");
            w.WriteLine("To use them: copy the tags and data folders over a copy of HREK (or import the zip in Kalo),");
            w.WriteLine("then build the map with tool.exe build-cache-file and load it in MCC with the mod tools.");
            if (p.Manifest.Deleted is { Count: > 0 }) w.WriteLine($"The mod also removes {p.Manifest.Deleted.Count} vanilla file(s): see deleted in {ManifestName}.");
        }
        return zip;
    }
}

public sealed record CaptureResult(int Files, int Copied, int Removed, int Deleted);

public sealed class ModProject(string folder, ProjectManifest manifest)
{
    public string Folder { get; } = folder;
    public ProjectManifest Manifest { get; } = manifest;
    public string Name => Manifest.Name;
}

public sealed class ProjectManifest
{
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
    /// <summary>Workspace-relative paths of the files the mod adds or changes.</summary>
    public List<string> Files { get; set; } = new();
    /// <summary>Vanilla files the mod removes.</summary>
    public List<string> Deleted { get; set; } = new();
    /// <summary>Maps the mod created → the Reach scenario they were copied from (to tell new and changed objects apart).</summary>
    public Dictionary<string, string> MapTemplates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Painted block copies (object tag) → how to rebuild them if their files go missing.</summary>
    public Dictionary<string, PaintedBlock> PaintedBlocks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A painted block: the Forge piece it copies and the shader its materials use.</summary>
public sealed record PaintedBlock(string Source, string Shader);
