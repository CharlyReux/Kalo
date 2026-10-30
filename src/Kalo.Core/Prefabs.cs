using System.Text.Json;

namespace Kalo.Core;

/// <summary>One object of a prefab, relative to the prefab's origin (its first object's position, on the ground).</summary>
public sealed class PrefabObject
{
    public string Tag { get; set; } = "";
    /// <summary>World units from the prefab origin (x, y, z).</summary>
    public float[] Offset { get; set; } = new float[3];
    /// <summary>Degrees: yaw, pitch, roll.</summary>
    public float[] Rotation { get; set; } = new float[3];
}

/// <summary>A reusable group of objects, saved with the mod (projects\&lt;mod&gt;\prefabs\&lt;name&gt;.json).</summary>
public sealed class Prefab
{
    public string Name { get; set; } = "";
    public DateTime Created { get; set; }
    public List<PrefabObject> Objects { get; set; } = new();

    public string Summary => Objects.Count == 1
        ? Path.GetFileNameWithoutExtension(Objects[0].Tag)
        : $"{Objects.Count} objects: " + string.Join(", ", Objects.GroupBy(o => Path.GetFileNameWithoutExtension(o.Tag)).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key).Take(4));
}

public sealed class PrefabLibrary(string folder)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Folder { get; } = folder;

    public List<Prefab> List()
    {
        var list = new List<Prefab>();
        if (!Directory.Exists(Folder)) return list;
        foreach (string f in Directory.EnumerateFiles(Folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var p = JsonSerializer.Deserialize<Prefab>(File.ReadAllText(f));
                if (p != null && p.Objects.Count > 0) list.Add(p);
            }
            catch { }
        }
        return list;
    }

    public void Save(Prefab prefab)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FileOf(prefab.Name), JsonSerializer.Serialize(prefab, Json));
    }

    public void Delete(string name)
    {
        string f = FileOf(name);
        if (File.Exists(f)) File.Delete(f);
    }

    private string FileOf(string name) =>
        Path.Combine(Folder, string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json");
}
