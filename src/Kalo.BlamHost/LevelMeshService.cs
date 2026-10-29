using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Bungie.Tags;
using Kalo.Protocol;

namespace Kalo.BlamHost
{
    /// <summary>
    /// Level geometry for Kalo's map view, read straight from the structure_bsp tags: each cluster's mesh plus
    /// every instanced geometry ("poop") placed with its transform, from render geometry/per mesh temporary
    /// (raw vertices and indices kept in editing tags). Written as one mesh in Kalo's .kmesh format.
    /// </summary>
    internal static class LevelMeshService
    {
        private const uint Magic = 0x48534D4B; // "KMSH"

        public static ExportLevelMeshResult Export(TagService tags, string scenario, string outputFile)
        {
            var sw = Stopwatch.StartNew();
            TagFile scen = tags.GetOrOpen(scenario);
            var bspPaths = new List<string>();
            var block = scen.SelectField("Block:structure bsps") as TagFieldBlock;
            if (block != null)
                for (int i = 0; i < block.Elements.Count; i++)
                {
                    string p = TagService.ValueOf(scen.SelectField("Block:structure bsps[" + i + "]/Reference:structure bsp"));
                    if (!string.IsNullOrEmpty(p)) bspPaths.Add(p);
                }

            var positions = new List<float>(1 << 20);
            var indices = new List<int>(1 << 20);
            var result = new ExportLevelMeshResult { File = outputFile };
            var notes = new List<string>();
            foreach (string bspPath in bspPaths)
            {
                try
                {
                    using (var bsp = new TagFile(TagService.TagPathOf(bspPath)))
                        AddBsp(bsp, positions, indices, result, notes);
                }
                catch (Exception ex)
                {
                    notes.Add(Path.GetFileName(bspPath) + ": " + ex.Message);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
            string tmp = outputFile + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                w.Write(Magic);
                w.Write(1);
                w.Write(positions.Count);
                w.Write(indices.Count);
                foreach (float f in positions) w.Write(f);
                foreach (int i in indices) w.Write(i);
            }
            if (File.Exists(outputFile)) File.Delete(outputFile);
            File.Move(tmp, outputFile);
            result.Vertices = positions.Count / 3;
            result.Triangles = indices.Count / 3;
            result.Ms = sw.ElapsedMilliseconds;
            result.Notes = string.Join("; ", notes);
            return result;
        }

        /// <summary>Each render model as one mesh in model space: the first permutation of every region, rigid parts moved by their node.</summary>
        public static ExportModelMeshesResult ExportModels(List<string> renderModels, string folder)
        {
            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(folder);
            var result = new ExportModelMeshesResult { Models = new List<ModelMeshInfo>() };
            foreach (string model in (renderModels ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var info = new ModelMeshInfo { RenderModel = model, File = Path.Combine(folder, ModelMeshNames.FileName(model)) };
                try
                {
                    var positions = new List<float>();
                    var indices = new List<int>();
                    using (var tag = new TagFile(TagService.TagPathOf(model)))
                        AddModel(tag, positions, indices);
                    Write(info.File, positions, indices);
                    info.Triangles = indices.Count / 3;
                }
                catch (Exception ex)
                {
                    info.Error = ex.Message;
                }
                result.Models.Add(info);
            }
            result.Ms = sw.ElapsedMilliseconds;
            return result;
        }

        private static void AddModel(TagFile tag, List<float> positions, List<int> indices)
        {
            var temp = tag.SelectField("Struct:render geometry[0]/Block:per mesh temporary") as TagFieldBlock;
            var meshes = tag.SelectField("Struct:render geometry[0]/Block:meshes") as TagFieldBlock;
            if (temp == null || meshes == null) return;
            float[][] nodes = NodeTransforms(tag);
            var wanted = new SortedSet<int>();
            var regions = tag.SelectField("Block:regions") as TagFieldBlock;
            if (regions != null)
                for (int r = 0; r < regions.Elements.Count; r++)
                {
                    string perm = "Block:regions[" + r + "]/Block:permutations[0]/";
                    var perms = tag.SelectField("Block:regions[" + r + "]/Block:permutations") as TagFieldBlock;
                    if (perms == null || perms.Elements.Count == 0) continue;
                    int first = Int(tag.SelectField(perm + "ShortInteger:mesh index"));
                    int count = Math.Max(1, Int(tag.SelectField(perm + "ShortInteger:mesh count")));
                    for (int m = first; m >= 0 && m < first + count; m++) wanted.Add(m);
                }
            var notes = new List<string>();
            float[][] compression = Compression(tag);
            foreach (int m in wanted)
            {
                if (m >= temp.Elements.Count) continue;
                RawMesh raw = ReadMesh(temp.Elements[m], notes);
                if (raw == null) continue;
                // Model vertices are stored normalized inside the model's compression box.
                float[] comp = compression.Length == 0 ? null : compression[m < compression.Length && compression.Length == meshes.Elements.Count ? m : 0];
                if (comp != null && (((int)comp[6]) & 1) != 0 && LooksNormalized(raw.Positions))
                    raw = new RawMesh { Positions = Decompress(raw.Positions, comp), Indices = raw.Indices };
                int vertexType = Int(tag.SelectField("Struct:render geometry[0]/Block:meshes[" + m + "]/CharEnum:vertex type"));
                int rigidNode = Int(tag.SelectField("Struct:render geometry[0]/Block:meshes[" + m + "]/CharInteger:rigid node index"));
                float[] t = vertexType == 1 && rigidNode >= 0 && rigidNode < nodes.Length ? nodes[rigidNode] : null;
                Append(raw, positions, indices, t);
            }
        }

        /// <summary>Model-space transform of each node from its default pose: {scale, forward, left, up, position}.</summary>
        private static float[][] NodeTransforms(TagFile tag)
        {
            var block = tag.SelectField("Block:nodes") as TagFieldBlock;
            int n = block == null ? 0 : block.Elements.Count;
            var world = new float[n][];
            var parent = new int[n];
            var local = new float[n][];
            for (int i = 0; i < n; i++)
            {
                string e = "Block:nodes[" + i + "]/";
                parent[i] = Int(tag.SelectField(e + "ShortBlockIndex:parent node"));
                float[] tr = Vec(tag.SelectField(e + "RealPoint3d:default translation"));
                float[] q = Quat(tag.SelectField(e + "RealQuaternion:default rotation"));
                local[i] = FromQuat(q, tr);
            }
            for (int i = 0; i < n; i++) world[i] = Resolve(i, parent, local, world, 0);
            return world;
        }

        private static float[] Resolve(int i, int[] parent, float[][] local, float[][] world, int depth)
        {
            if (world[i] != null) return world[i];
            int p = parent[i];
            float[] w = p < 0 || p >= local.Length || p == i || depth > 64 ? local[i] : Compose(Resolve(p, parent, local, world, depth + 1), local[i]);
            world[i] = w;
            return w;
        }

        /// <summary>{1, forward(3), left(3), up(3), position(3)} from a quaternion (i, j, k, w) and a translation.</summary>
        private static float[] FromQuat(float[] q, float[] t)
        {
            float x = q[0], y = q[1], z = q[2], w = q[3];
            float len = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
            if (len < 1e-6f) { x = 0; y = 0; z = 0; w = 1; } else { x /= len; y /= len; z /= len; w /= len; }
            return new[]
            {
                1f,
                1 - 2 * (y * y + z * z), 2 * (x * y + w * z), 2 * (x * z - w * y),
                2 * (x * y - w * z), 1 - 2 * (x * x + z * z), 2 * (y * z + w * x),
                2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y),
                t[0], t[1], t[2]
            };
        }

        /// <summary>parent ∘ child: child axes and position expressed in the parent's space.</summary>
        private static float[] Compose(float[] p, float[] c)
        {
            Func<float, float, float, float[]> apply = (x, y, z) => new[]
            {
                x * p[1] + y * p[4] + z * p[7],
                x * p[2] + y * p[5] + z * p[8],
                x * p[3] + y * p[6] + z * p[9]
            };
            float[] f = apply(c[1], c[2], c[3]);
            float[] l = apply(c[4], c[5], c[6]);
            float[] u = apply(c[7], c[8], c[9]);
            float[] pos = apply(c[10], c[11], c[12]);
            return new[] { 1f, f[0], f[1], f[2], l[0], l[1], l[2], u[0], u[1], u[2], pos[0] + p[10], pos[1] + p[11], pos[2] + p[12] };
        }

        private static float[] Quat(TagField f)
        {
            var a = f as TagFieldElementArraySingle;
            if (a == null) return new[] { 0f, 0f, 0f, 1f };
            float[] d = a.Data.ToArray();
            return d.Length >= 4 ? d : new[] { 0f, 0f, 0f, 1f };
        }

        private static void Write(string file, List<float> positions, List<int> indices)
        {
            string tmp = file + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                w.Write(Magic);
                w.Write(1);
                w.Write(positions.Count);
                w.Write(indices.Count);
                foreach (float f in positions) w.Write(f);
                foreach (int i in indices) w.Write(i);
            }
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        private sealed class RawMesh
        {
            public float[] Positions;
            public int[] Indices;
        }

        private static void AddBsp(TagFile bsp, List<float> positions, List<int> indices, ExportLevelMeshResult result, List<string> notes)
        {
            var temp = bsp.SelectField("Struct:render geometry[0]/Block:per mesh temporary") as TagFieldBlock;
            if (temp == null) { notes.Add("no per mesh temporary"); return; }
            float[][] compression = Compression(bsp);
            var cache = new Dictionary<int, RawMesh>();
            Func<int, RawMesh> mesh = i =>
            {
                RawMesh m;
                if (!cache.TryGetValue(i, out m))
                {
                    m = i >= 0 && i < temp.Elements.Count ? ReadMesh(temp.Elements[i], notes) : null;
                    cache[i] = m;
                }
                return m;
            };
            int clustersDecompressed = 0, instancesDecompressed = 0, instancesOff = 0, transposed = 0;

            // Vertex positions may be stored normalized (0..1) inside a compression box. The tags also give each
            // cluster and instance its world bounds, so each mesh is placed the way that matches those bounds.
            var clusters = bsp.SelectField("Block:clusters") as TagFieldBlock;
            if (clusters != null)
                for (int c = 0; c < clusters.Elements.Count; c++)
                {
                    string e = "Block:clusters[" + c + "]/";
                    int index = Int(bsp.SelectField(e + "ShortInteger:mesh index"));
                    RawMesh m = mesh(index);
                    if (m == null) continue;
                    float[] bx = Vec2(bsp.SelectField(e + "RealBounds:bounds x"));
                    float[] by = Vec2(bsp.SelectField(e + "RealBounds:bounds y"));
                    float[] bz = Vec2(bsp.SelectField(e + "RealBounds:bounds z"));
                    float[] target = { bx[0], bx[1], by[0], by[1], bz[0], bz[1] };
                    float[] best = Best(m.Positions, index < compression.Length ? compression[index] : null, null, target, ref clustersDecompressed);
                    Append(new RawMesh { Positions = best, Indices = m.Indices }, positions, indices, null);
                    result.Clusters++;
                }

            var instances = bsp.SelectField("Block:instanced geometry instances") as TagFieldBlock;
            if (instances != null)
                for (int k = 0; k < instances.Elements.Count; k++)
                {
                    string e = "Block:instanced geometry instances[" + k + "]/";
                    int index = Int(bsp.SelectField(e + "ShortInteger:mesh_index"));
                    RawMesh m = mesh(index);
                    if (m == null) continue;
                    int ci = Int(bsp.SelectField(e + "ShortInteger:compression_index"));
                    float scale = Float(bsp.SelectField(e + "Real:scale"));
                    if (scale == 0) scale = 1;
                    float[] f = Vec(bsp.SelectField(e + "RealVector3d:forward"));
                    float[] l = Vec(bsp.SelectField(e + "RealVector3d:left"));
                    float[] u = Vec(bsp.SelectField(e + "RealVector3d:up"));
                    float[] p = Vec(bsp.SelectField(e + "RealPoint3d:position"));
                    float[] t = { scale, f[0], f[1], f[2], l[0], l[1], l[2], u[0], u[1], u[2], p[0], p[1], p[2] };
                    float[] target =
                    {
                        Float(bsp.SelectField(e + "Real:bounds x0")), Float(bsp.SelectField(e + "Real:bounds x1")),
                        Float(bsp.SelectField(e + "Real:bounds y0")), Float(bsp.SelectField(e + "Real:bounds y1")),
                        Float(bsp.SelectField(e + "Real:bounds z0")), Float(bsp.SelectField(e + "Real:bounds z1")),
                    };
                    float[] comp = ci >= 0 && ci < compression.Length ? compression[ci] : index < compression.Length ? compression[index] : null;
                    // The basis vectors may be rows or columns of the rotation; keep whichever fits the recorded bounds.
                    float[] tt = { scale, f[0], l[0], u[0], f[1], l[1], u[1], f[2], l[2], u[2], p[0], p[1], p[2] };
                    int dec1 = 0, dec2 = 0;
                    float e1, e2;
                    float[] b1 = Best(m.Positions, comp, t, target, ref dec1, out e1);
                    float[] b2 = Best(m.Positions, comp, tt, target, ref dec2, out e2);
                    bool columns = e2 + 1e-4f < e1;
                    if (columns) transposed++;
                    instancesDecompressed += columns ? dec2 : dec1;
                    if (Math.Min(e1, e2) > 0.5f) instancesOff++;
                    Append(new RawMesh { Positions = columns ? b2 : b1, Indices = m.Indices }, positions, indices, columns ? tt : t);
                    result.Instances++;
                }
            notes.Add(string.Format("clusters {0}/{1} decompressed; instances {2}/{3} decompressed, {5} with transposed axes, {4} still off their bounds",
                clustersDecompressed, result.Clusters, instancesDecompressed, result.Instances, instancesOff, transposed));
        }

        /// <summary>Compression boxes of a render geometry: x0, x1, y0, y1, z0, z1, flags (fields are mislabeled in the tag: bounds 0 = x0 x1 y0, bounds 1 = y1 z0 z1).</summary>
        private static float[][] Compression(TagFile tag)
        {
            var block = tag.SelectField("Struct:render geometry[0]/Block:compression info") as TagFieldBlock;
            int n = block == null ? 0 : block.Elements.Count;
            var list = new float[n][];
            for (int i = 0; i < n; i++)
            {
                string e = "Struct:render geometry[0]/Block:compression info[" + i + "]/";
                float[] b0 = Vec(tag.SelectField(e + "RealPoint3d:position bounds 0"));
                float[] b1 = Vec(tag.SelectField(e + "RealPoint3d:position bounds 1"));
                var flags = tag.SelectField(e + "WordFlags:compression flags") as TagFieldFlags;
                list[i] = new[] { b0[0], b0[1], b0[2], b1[0], b1[1], b1[2], flags == null ? 1f : flags.RawValue };
            }
            return list;
        }

        private static float[] Decompress(float[] raw, float[] c)
        {
            var r = new float[raw.Length];
            for (int i = 0; i + 2 < raw.Length; i += 3)
            {
                r[i] = c[0] + raw[i] * (c[1] - c[0]);
                r[i + 1] = c[2] + raw[i + 1] * (c[3] - c[2]);
                r[i + 2] = c[4] + raw[i + 2] * (c[5] - c[4]);
            }
            return r;
        }

        private static bool LooksNormalized(float[] raw)
        {
            foreach (float v in raw) if (v < -0.01f || v > 1.01f) return false;
            return raw.Length > 0;
        }

        private static float[] Best(float[] raw, float[] comp, float[] t, float[] target, ref int decompressed)
        {
            float error;
            return Best(raw, comp, t, target, ref decompressed, out error);
        }

        /// <summary>Raw or decompressed positions, whichever lands closest to the world bounds the tag records.</summary>
        private static float[] Best(float[] raw, float[] comp, float[] t, float[] target, ref int decompressed, out float error)
        {
            float rawError = BoundsError(raw, t, target);
            error = rawError;
            if (comp == null || !LooksNormalized(raw)) return raw;
            float[] d = Decompress(raw, comp);
            float dError = BoundsError(d, t, target);
            if (dError < rawError) { decompressed++; error = dError; return d; }
            return raw;
        }

        private static float BoundsError(float[] pts, float[] t, float[] target)
        {
            if (target == null || pts.Length < 3) return 0;
            float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
            for (int i = 0; i + 2 < pts.Length; i += 3)
            {
                float x = pts[i], y = pts[i + 1], z = pts[i + 2];
                if (t != null)
                {
                    float s = t[0];
                    float wx = t[10] + s * (x * t[1] + y * t[4] + z * t[7]);
                    float wy = t[11] + s * (x * t[2] + y * t[5] + z * t[8]);
                    float wz = t[12] + s * (x * t[3] + y * t[6] + z * t[9]);
                    x = wx; y = wy; z = wz;
                }
                if (x < x0) x0 = x; if (x > x1) x1 = x;
                if (y < y0) y0 = y; if (y > y1) y1 = y;
                if (z < z0) z0 = z; if (z > z1) z1 = z;
            }
            return Math.Abs(x0 - target[0]) + Math.Abs(x1 - target[1]) + Math.Abs(y0 - target[2]) + Math.Abs(y1 - target[3]) + Math.Abs(z0 - target[4]) + Math.Abs(z1 - target[5]);
        }

        private static float[] Vec2(TagField f)
        {
            var a = f as TagFieldElementArraySingle;
            if (a == null) return new float[2];
            float[] d = a.Data.ToArray();
            return d.Length >= 2 ? d : new float[2];
        }

        private static void Append(RawMesh m, List<float> positions, List<int> indices, float[] t)
        {
            int baseVertex = positions.Count / 3;
            for (int i = 0; i + 2 < m.Positions.Length; i += 3)
            {
                float x = m.Positions[i], y = m.Positions[i + 1], z = m.Positions[i + 2];
                if (t != null)
                {
                    float s = t[0];
                    float wx = t[10] + s * (x * t[1] + y * t[4] + z * t[7]);
                    float wy = t[11] + s * (x * t[2] + y * t[5] + z * t[8]);
                    float wz = t[12] + s * (x * t[3] + y * t[6] + z * t[9]);
                    x = wx; y = wy; z = wz;
                }
                positions.Add(x); positions.Add(y); positions.Add(z);
            }
            foreach (int i in m.Indices) indices.Add(baseVertex + i);
        }

        private static RawMesh ReadMesh(TagElement element, List<string> notes)
        {
            TagFieldBlock verts = null, words = null, words32 = null;
            int flags = 0;
            foreach (TagField f in element.Fields)
            {
                if (f.FieldName == "raw vertices") verts = f as TagFieldBlock;
                else if (f.FieldName == "raw indices") words = f as TagFieldBlock;
                else if (f.FieldName == "raw indices32") words32 = f as TagFieldBlock;
                else if (f.FieldName == "flags" && f is TagFieldFlags) flags = (int)((TagFieldFlags)f).RawValue;
            }
            if (verts == null || verts.Elements.Count == 0) return null;
            float[] pos = ReadPositions(verts);
            int[] raw = words32 != null && words32.Elements.Count > 0 ? ReadIndices(words32, 4) : words != null ? ReadIndices(words, 2) : new int[0];
            bool strips = (flags & 1) != 0 && (flags & 2) == 0;
            int[] tris = strips ? Strip(raw) : raw.Take(raw.Length - raw.Length % 3).ToArray();
            int vc = pos.Length / 3;
            var valid = new List<int>(tris.Length);
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vc || b >= vc || c >= vc || a == b || b == c || a == c) continue;
                valid.Add(a); valid.Add(b); valid.Add(c);
            }
            return new RawMesh { Positions = pos, Indices = valid.ToArray() };
        }

        private static int[] Strip(int[] s)
        {
            var list = new List<int>(s.Length * 3);
            for (int i = 0; i + 2 < s.Length; i++)
            {
                if (s[i] == 0xFFFF || s[i + 1] == 0xFFFF || s[i + 2] == 0xFFFF) continue;
                if ((i & 1) == 0) { list.Add(s[i]); list.Add(s[i + 1]); list.Add(s[i + 2]); }
                else { list.Add(s[i + 1]); list.Add(s[i]); list.Add(s[i + 2]); }
            }
            return list.ToArray();
        }

        /// <summary>Positions of every raw vertex: one bulk read when the serialized layout checks out, else field by field.</summary>
        private static float[] ReadPositions(TagFieldBlock verts)
        {
            int n = verts.Elements.Count;
            var result = new float[n * 3];
            float[] first = Vec(verts.Elements[0].Fields[0]);
            float[] last = Vec(verts.Elements[n - 1].Fields[0]);
            try
            {
                byte[] bytes = verts.SerializeEntireBlock();
                if (bytes != null && bytes.Length >= n * 12)
                {
                    foreach (int header in new[] { 0, bytes.Length - (bytes.Length / n) * n })
                    {
                        int stride = (bytes.Length - header) / n;
                        if (stride < 12 || header + stride * n != bytes.Length) continue;
                        if (!Same(bytes, header, first) || !Same(bytes, header + stride * (n - 1), last)) continue;
                        for (int i = 0; i < n; i++)
                        {
                            int o = header + i * stride;
                            result[i * 3] = BitConverter.ToSingle(bytes, o);
                            result[i * 3 + 1] = BitConverter.ToSingle(bytes, o + 4);
                            result[i * 3 + 2] = BitConverter.ToSingle(bytes, o + 8);
                        }
                        return result;
                    }
                }
            }
            catch (Exception) { }
            for (int i = 0; i < n; i++)
            {
                float[] v = Vec(verts.Elements[i].Fields[0]);
                result[i * 3] = v[0]; result[i * 3 + 1] = v[1]; result[i * 3 + 2] = v[2];
            }
            return result;
        }

        private static bool Same(byte[] b, int o, float[] v) =>
            o + 12 <= b.Length && BitConverter.ToSingle(b, o) == v[0] && BitConverter.ToSingle(b, o + 4) == v[1] && BitConverter.ToSingle(b, o + 8) == v[2];

        private static int[] ReadIndices(TagFieldBlock block, int size)
        {
            int n = block.Elements.Count;
            var result = new int[n];
            if (n == 0) return result;
            int first = Int(block.Elements[0].Fields[0]);
            int last = Int(block.Elements[n - 1].Fields[0]);
            try
            {
                byte[] bytes = block.SerializeEntireBlock();
                if (bytes != null && bytes.Length >= n * size)
                {
                    int header = bytes.Length - n * size;
                    Func<int, int> at = i => size == 2 ? BitConverter.ToUInt16(bytes, header + i * 2) : BitConverter.ToInt32(bytes, header + i * 4);
                    if ((at(0) & 0xFFFF) == (first & 0xFFFF) && (at(n - 1) & 0xFFFF) == (last & 0xFFFF))
                    {
                        for (int i = 0; i < n; i++) result[i] = at(i);
                        return result;
                    }
                }
            }
            catch (Exception) { }
            for (int i = 0; i < n; i++) result[i] = Int(block.Elements[i].Fields[0]) & (size == 2 ? 0xFFFF : -1);
            return result;
        }

        private static float[] Vec(TagField f)
        {
            var a = f as TagFieldElementArraySingle;
            if (a != null) { float[] d = a.Data.ToArray(); return d.Length >= 3 ? d : new[] { d.ElementAtOrDefault(0), d.ElementAtOrDefault(1), 0f }; }
            return new float[3];
        }

        private static float Float(TagField f)
        {
            var s = f as TagFieldElementSingle;
            return s != null ? s.Data : 0f;
        }

        private static int Int(TagField f)
        {
            var i = f as TagFieldElementInteger;
            if (i != null) return (int)i.Data;
            var b = f as TagFieldBlockIndex;
            return b != null ? b.Value : -1;
        }
    }
}
