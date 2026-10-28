using System.Globalization;
using System.Numerics;

namespace Kalo.Core.Geometry;

/// <summary>Triangle mesh in Halo world space (z up, world units).</summary>
public sealed class Mesh
{
    public float[] Positions { get; }
    public int[] Indices { get; }
    public Vector3 Min { get; }
    public Vector3 Max { get; }
    public int TriangleCount => Indices.Length / 3;
    public int VertexCount => Positions.Length / 3;

    public Mesh(float[] positions, int[] indices)
    {
        Positions = positions;
        Indices = indices;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i + 2 < positions.Length; i += 3)
        {
            var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        Min = positions.Length == 0 ? Vector3.Zero : min;
        Max = positions.Length == 0 ? Vector3.Zero : max;
    }

    public Vector3 Center => (Min + Max) * 0.5f;

    // ---- cache format: "KMSH", version, vertex count, index count, floats, ints ----------------

    private const uint Magic = 0x48534D4B; // "KMSH"

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        using (var w = new BinaryWriter(File.Create(tmp)))
        {
            w.Write(Magic);
            w.Write(1);
            w.Write(Positions.Length);
            w.Write(Indices.Length);
            foreach (float f in Positions) w.Write(f);
            foreach (int i in Indices) w.Write(i);
        }
        File.Move(tmp, path, true);
    }

    public static Mesh? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadUInt32() != Magic || r.ReadInt32() != 1) return null;
            var p = new float[r.ReadInt32()];
            var idx = new int[r.ReadInt32()];
            for (int i = 0; i < p.Length; i++) p[i] = r.ReadSingle();
            for (int i = 0; i < idx.Length; i++) idx[i] = r.ReadInt32();
            return new Mesh(p, idx);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Reads Wavefront OBJ as written by HREK's tool.exe (export-structure-mesh-obj / export-render-model-mesh).
/// Those files are y-up; Halo is z-up, so (x, y, z) in the file is (x, -z, y) in the world.
/// </summary>
public static class ObjReader
{
    public static Mesh Read(TextReader reader, bool yUp = true, float scale = 1f)
    {
        var pos = new List<float>(1 << 16);
        var idx = new List<int>(1 << 16);
        var face = new List<int>(8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var span = line.AsSpan().Trim();
            if (span.Length < 2) continue;
            if (span[0] == 'v' && span[1] == ' ')
            {
                var parts = SplitFloats(span[2..]);
                if (parts.Count < 3) continue;
                float x = parts[0], y = parts[1], z = parts[2];
                if (yUp) { pos.Add(x * scale); pos.Add(-z * scale); pos.Add(y * scale); }
                else { pos.Add(x * scale); pos.Add(y * scale); pos.Add(z * scale); }
            }
            else if (span[0] == 'f' && span[1] == ' ')
            {
                face.Clear();
                int vertexCount = pos.Count / 3;
                foreach (var token in span[2..].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    int slash = token.IndexOf('/');
                    string first = slash >= 0 ? token[..slash] : token;
                    if (!int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) continue;
                    face.Add(v < 0 ? vertexCount + v : v - 1);
                }
                for (int i = 1; i + 1 < face.Count; i++)
                {
                    idx.Add(face[0]);
                    idx.Add(face[i]);
                    idx.Add(face[i + 1]);
                }
            }
        }
        // Triangle soup without faces (some exports): every 3 vertices make a triangle.
        if (idx.Count == 0)
            for (int i = 0; i + 2 < pos.Count / 3; i += 3) { idx.Add(i); idx.Add(i + 1); idx.Add(i + 2); }
        return new Mesh(pos.ToArray(), idx.ToArray());
    }

    private static List<float> SplitFloats(ReadOnlySpan<char> s)
    {
        var list = new List<float>(3);
        foreach (var part in s.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) list.Add(f);
        return list;
    }
}
