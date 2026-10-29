using System.Diagnostics;

namespace Kalo.Core.Geometry;

/// <summary>
/// Level geometry for the map view, cached in .kalo\cache\geometry so a map opens instantly the second time.
/// The cache is rebuilt when one of the map's structure_bsp tags is newer than it.
/// </summary>
public sealed class LevelGeometry
{
    private readonly KaloPaths _paths;
    /// <summary>Optional faster source (e.g. geometry read from the tags by the Blam Host). Null result = not available.</summary>
    public Func<string, IReadOnlyList<string>, Task<Mesh?>>? TagSource { get; set; }

    public LevelGeometry(KaloPaths paths) => _paths = paths;

    public string CacheFile(string scenarioPath) =>
        Path.Combine(_paths.KaloData, "cache", "geometry", scenarioPath.Replace('\\', '_').Replace('/', '_') + ".v3.kmesh"); // v3: decompressed positions, meshes from instance definitions

    public async Task<Mesh?> LoadLevelAsync(string scenarioPath, IReadOnlyList<string> bsps, Action<string>? progress = null)
    {
        string cache = CacheFile(scenarioPath);
        DateTime newestSource = bsps
            .Select(b => Path.Combine(_paths.WorkspaceTags, b))
            .Where(File.Exists)
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        if (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) >= newestSource)
        {
            var cached = await Task.Run(() => Mesh.TryLoad(cache));
            if (cached != null) return cached;
        }

        Mesh? mesh = null;
        if (TagSource != null)
        {
            progress?.Invoke("Reading level geometry from the tags…");
            try { mesh = await TagSource(scenarioPath, bsps); } catch (Exception ex) { progress?.Invoke("Tag geometry failed: " + ex.Message); }
        }
        if (mesh == null || mesh.TriangleCount == 0)
            mesh = await ExportWithToolAsync(scenarioPath, progress);
        if (mesh != null && mesh.TriangleCount > 0) await Task.Run(() => mesh.Save(cache));
        return mesh;
    }

    /// <summary>tool.exe export-structure-mesh-obj writes the level as OBJ on its console; slow but always available.</summary>
    private async Task<Mesh?> ExportWithToolAsync(string scenarioPath, Action<string>? progress)
    {
        string tool = Path.Combine(_paths.Workspace, "tool.exe");
        if (!File.Exists(tool)) return null;
        string scenario = scenarioPath.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase) ? scenarioPath[..^".scenario".Length] : scenarioPath;
        string objFile = CacheFile(scenarioPath) + ".obj";
        Directory.CreateDirectory(Path.GetDirectoryName(objFile)!);

        var psi = new ProcessStartInfo(tool)
        {
            WorkingDirectory = _paths.Workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("export-structure-mesh-obj");
        psi.ArgumentList.Add(scenario);
        using var process = Process.Start(psi)!;
        _ = process.StandardError.ReadToEndAsync();
        long bytes = 0;
        await using (var file = new StreamWriter(objFile))
        {
            var sw = Stopwatch.StartNew();
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null)
            {
                await file.WriteLineAsync(line);
                bytes += line.Length + 2;
                if (sw.ElapsedMilliseconds > 1000)
                {
                    progress?.Invoke($"Exporting level geometry with tool.exe (first time only)… {bytes / 1048576.0:F1} MB");
                    sw.Restart();
                }
            }
        }
        await process.WaitForExitAsync();
        progress?.Invoke("Reading exported geometry…");
        return await Task.Run(() =>
        {
            using var reader = new StreamReader(objFile);
            return ObjReader.Read(reader);
        });
    }
}
