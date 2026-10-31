namespace Kalo.Core;

/// <summary>
/// The vanilla state of the shared workspace (.kalo-baseline.tsv: path, size, last-write ticks).
/// A file that differs from its baseline entry is a change made by the current project.
/// </summary>
public sealed class Baseline
{
    public const string FileName = ".kalo-baseline.tsv";
    private readonly KaloPaths _paths;
    private readonly Dictionary<string, (long Size, long Ticks)> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Count;

    private Baseline(KaloPaths paths) => _paths = paths;

    public static Baseline Load(KaloPaths paths)
    {
        var b = new Baseline(paths);
        string file = Path.Combine(paths.Workspace, FileName);
        if (!File.Exists(file)) throw new FileNotFoundException("No baseline in the workspace. Run `kalo-spike1 shared` once.", file);
        foreach (string line in File.ReadLines(file))
        {
            string[] p = line.Split('\t');
            if (p.Length == 3 && long.TryParse(p[1], out long size) && long.TryParse(p[2], out long ticks))
                b._entries[p[0]] = (size, ticks);
        }
        return b;
    }

    /// <summary>Workspace-relative path such as tags\objects\...\x.weapon.</summary>
    public bool IsChanged(string relativePath)
    {
        string full = Path.Combine(_paths.Workspace, relativePath);
        if (!_entries.TryGetValue(relativePath, out var e)) return File.Exists(full);
        var info = new FileInfo(full);
        return !info.Exists || info.Length != e.Size || info.LastWriteTimeUtc.Ticks != e.Ticks;
    }

    public bool IsVanilla(string relativePath) => _entries.ContainsKey(relativePath);

    /// <summary>Every new, changed or deleted file under tags\ and data\.</summary>
    public List<WorkspaceChange> Scan()
    {
        var changes = new List<WorkspaceChange>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in new[] { "tags", "data" })
        {
            var root = new DirectoryInfo(Path.Combine(_paths.Workspace, dir));
            if (!root.Exists) continue;
            foreach (FileInfo f in root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                string rel = f.FullName.Substring(_paths.Workspace.Length + 1);
                seen.Add(rel);
                // Kalo's own throwaway maps (play copies, test maps) are not changes of the mod.
                if (rel.Contains(@"\_kalo_", StringComparison.OrdinalIgnoreCase)) continue;
                if (!_entries.TryGetValue(rel, out var e)) changes.Add(new WorkspaceChange(rel, ChangeKind.Added));
                else if (e.Size != f.Length || e.Ticks != f.LastWriteTimeUtc.Ticks) changes.Add(new WorkspaceChange(rel, ChangeKind.Modified));
            }
        }
        foreach (string rel in _entries.Keys)
            if (!seen.Contains(rel)) changes.Add(new WorkspaceChange(rel, ChangeKind.Deleted));
        changes.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return changes;
    }

    /// <summary>Puts a vanilla file back exactly (content from HREK, timestamp from the baseline), or deletes a file the project added.</summary>
    public void Revert(string relativePath)
    {
        string target = Path.Combine(_paths.Workspace, relativePath);
        if (!KaloPaths.IsUnder(target, _paths.Workspace)) throw new InvalidOperationException("Outside the workspace: " + relativePath);
        if (!_entries.TryGetValue(relativePath, out var e))
        {
            if (File.Exists(target)) File.Delete(target);
            return;
        }
        string source = Path.Combine(_paths.Hrek, relativePath);
        if (!File.Exists(source)) throw new FileNotFoundException("Vanilla file missing in HREK", source);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        File.SetLastWriteTimeUtc(target, new DateTime(e.Ticks, DateTimeKind.Utc));
    }
}

public enum ChangeKind { Added, Modified, Deleted }

public sealed record WorkspaceChange(string Path, ChangeKind Kind);
