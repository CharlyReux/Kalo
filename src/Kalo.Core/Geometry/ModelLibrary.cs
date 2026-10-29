using Kalo.Protocol;

namespace Kalo.Core.Geometry;

/// <summary>Object models for the map view, exported once per render_model by the Blam Host and cached in .kalo\cache\models.</summary>
public sealed class ModelLibrary
{
    private readonly KaloPaths _paths;
    private readonly Dictionary<string, Mesh?> _meshes = new(StringComparer.OrdinalIgnoreCase);

    public ModelLibrary(KaloPaths paths) => _paths = paths;

    public string Folder => Path.Combine(_paths.KaloData, "cache", "models-v2"); // v2: decompressed vertex positions

    public Mesh? Get(string? renderModel) => renderModel != null && _meshes.TryGetValue(renderModel, out var m) ? m : null;

    /// <summary>Loads the given models, asking the host to export the ones not cached (or older than their tag).</summary>
    public async Task<int> LoadAsync(HostClient? host, IEnumerable<string> renderModels)
    {
        var missing = new List<string>();
        foreach (string model in renderModels.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_meshes.ContainsKey(model)) continue;
            string file = Path.Combine(Folder, ModelMeshNames.FileName(model));
            string tag = Path.Combine(_paths.WorkspaceTags, model);
            bool fresh = File.Exists(file) && (!File.Exists(tag) || File.GetLastWriteTimeUtc(file) >= File.GetLastWriteTimeUtc(tag));
            var mesh = fresh ? await Task.Run(() => Mesh.TryLoad(file)) : null;
            if (mesh != null) _meshes[model] = mesh;
            else missing.Add(model);
        }
        if (missing.Count > 0 && host != null)
        {
            var r = await host.CallAsync<ExportModelMeshesResult>(Methods.ExportModelMeshes,
                new ExportModelMeshesParams { RenderModels = missing, OutputFolder = Folder }, TimeSpan.FromMinutes(10));
            foreach (var m in r.Models ?? new())
                _meshes[m.RenderModel!] = m.Error == null && m.File != null ? await Task.Run(() => Mesh.TryLoad(m.File)) : null;
        }
        return _meshes.Count(kv => kv.Value != null);
    }
}
