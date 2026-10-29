using Kalo.Protocol;

namespace Kalo.Core.Geometry;

/// <summary>RGBA image decoded from a bitmap tag (top row first).</summary>
public sealed record TextureImage(int Width, int Height, byte[] Rgba);

/// <summary>Bitmaps for the textured map view, decoded once by the Blam Host and cached in .kalo\cache\textures.</summary>
public sealed class TextureLibrary
{
    private const uint Magic = 0x5845544B; // "KTEX"
    private readonly KaloPaths _paths;
    private readonly Dictionary<string, TextureImage?> _images = new(StringComparer.OrdinalIgnoreCase);

    public TextureLibrary(KaloPaths paths) => _paths = paths;

    public string Folder => Path.Combine(_paths.KaloData, "cache", "textures-v1");
    public int Count => _images.Count(kv => kv.Value != null);

    /// <summary>Why bitmaps could not be decoded, from the last LoadAsync ("bitmap: reason").</summary>
    public List<string> LastErrors { get; } = new();

    public TextureImage? Get(string? bitmap) => bitmap != null && _images.TryGetValue(bitmap, out var t) ? t : null;

    /// <summary>Loads the given bitmaps, asking the host to decode the ones not cached (or older than their tag).</summary>
    public async Task<int> LoadAsync(HostClient? host, IEnumerable<string> bitmaps, int maxSize = 256)
    {
        LastErrors.Clear();
        var missing = new List<string>();
        foreach (string bitmap in bitmaps.Where(b => !string.IsNullOrEmpty(b)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_images.ContainsKey(bitmap)) continue;
            string file = Path.Combine(Folder, TextureNames.FileName(bitmap));
            string tag = Path.Combine(_paths.WorkspaceTags, bitmap);
            bool fresh = File.Exists(file) && (!File.Exists(tag) || File.GetLastWriteTimeUtc(file) >= File.GetLastWriteTimeUtc(tag));
            var image = fresh ? await Task.Run(() => TryLoad(file)) : null;
            if (image != null) _images[bitmap] = image;
            else missing.Add(bitmap);
        }
        if (missing.Count > 0 && host != null)
        {
            foreach (var chunk in missing.Chunk(64))
            {
                var r = await host.CallAsync<ExportTexturesResult>(Methods.ExportTextures,
                    new ExportTexturesParams { Bitmaps = chunk.ToList(), OutputFolder = Folder, MaxSize = maxSize }, TimeSpan.FromMinutes(10));
                foreach (var t in r.Textures ?? new())
                {
                    _images[t.Bitmap!] = t.Error == null && t.File != null ? await Task.Run(() => TryLoad(t.File)) : null;
                    if (t.Error != null) LastErrors.Add($"{t.Bitmap}: {t.Error}");
                }
            }
        }
        return Count;
    }

    public static TextureImage? TryLoad(string file)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(file));
            if (r.ReadUInt32() != Magic) return null;
            int w = r.ReadInt32(), h = r.ReadInt32();
            if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;
            byte[] rgba = r.ReadBytes(w * h * 4);
            return rgba.Length == w * h * 4 ? new TextureImage(w, h, rgba) : null;
        }
        catch
        {
            return null;
        }
    }
}
