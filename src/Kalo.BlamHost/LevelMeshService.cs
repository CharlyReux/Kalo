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
    /// Geometry for Kalo's map view, read straight from the tags (render geometry / per mesh temporary keeps raw
    /// vertices and indices in editing tags):
    ///  - levels: each cluster's mesh plus every instanced geometry ("poop") with its transform;
    ///  - object models: the first permutation of each region.
    /// Positions and texcoords stored normalized inside a compression box are expanded. Each triangle keeps its
    /// material (shader → base map bitmap) so the view can texture it. Written in Kalo's .kmesh format v5.
    /// </summary>
    internal static class LevelMeshService
    {
        private const uint Magic = 0x48534D4B; // "KMSH"

        // ---- output -------------------------------------------------------------------------------

        /// <summary>Collects vertices (position, uv), triangles and their materials.</summary>
        private sealed class MeshSink
        {
            public readonly List<float> Positions = new List<float>(1 << 20);
            public readonly List<float> Uvs = new List<float>(1 << 19);
            public readonly List<int> Indices = new List<int>(1 << 20);
            public readonly List<int> TriangleMaterial = new List<int>(1 << 18);
            public readonly List<string> Shaders = new List<string>();
            private readonly Dictionary<string, int> _shaderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public int Material(string shader)
            {
                if (string.IsNullOrEmpty(shader)) return -1;
                int i;
                if (!_shaderIndex.TryGetValue(shader, out i)) { i = Shaders.Count; Shaders.Add(shader); _shaderIndex[shader] = i; }
                return i;
            }

            /// <summary>Adds a mesh; t = {scale, forward, left, up, position} or null; materials maps the mesh's part materials to sink materials.</summary>
            public void Append(RawMesh m, float[] t, int[] materials)
            {
                int baseVertex = Positions.Count / 3;
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
                    Positions.Add(x); Positions.Add(y); Positions.Add(z);
                    int v = i / 3;
                    Uvs.Add(m.Uvs != null && m.Uvs.Length > v * 2 + 1 ? m.Uvs[v * 2] : 0f);
                    Uvs.Add(m.Uvs != null && m.Uvs.Length > v * 2 + 1 ? m.Uvs[v * 2 + 1] : 0f);
                }
                foreach (int i in m.Indices) Indices.Add(baseVertex + i);
                for (int tri = 0; tri < m.Indices.Length / 3; tri++)
                {
                    int part = m.TriangleMaterial != null && tri < m.TriangleMaterial.Length ? m.TriangleMaterial[tri] : -1;
                    TriangleMaterial.Add(part >= 0 && materials != null && part < materials.Length ? materials[part] : -1);
                }
            }

            /// <summary>Writes .kmesh v5: triangles grouped by material, with each material's shader, base map, uv scale/offset, blend mode, flags, flat color and terrain layers.</summary>
            public void Write(string file, Func<string, BaseMap> baseMapOf)
            {
                int triCount = Indices.Count / 3;
                var order = Enumerable.Range(0, triCount).OrderBy(i => TriangleMaterial[i]).ToArray();
                var sorted = new int[Indices.Count];
                var groups = new List<int[]>();
                for (int k = 0; k < triCount; k++)
                {
                    int tri = order[k];
                    sorted[k * 3] = Indices[tri * 3]; sorted[k * 3 + 1] = Indices[tri * 3 + 1]; sorted[k * 3 + 2] = Indices[tri * 3 + 2];
                    int mat = TriangleMaterial[tri];
                    if (groups.Count == 0 || groups[groups.Count - 1][0] != mat) groups.Add(new[] { mat, k * 3, 0 });
                    groups[groups.Count - 1][2] += 3;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                string tmp = file + ".tmp";
                using (var w = new BinaryWriter(File.Create(tmp)))
                {
                    w.Write(Magic);
                    w.Write(5);
                    w.Write(Positions.Count);
                    w.Write(sorted.Length);
                    foreach (float f in Positions) w.Write(f);
                    foreach (int i in sorted) w.Write(i);
                    w.Write(Uvs.Count);
                    foreach (float f in Uvs) w.Write(f);
                    w.Write(groups.Count);
                    foreach (int[] g in groups) { w.Write(g[0]); w.Write(g[1]); w.Write(g[2]); }
                    w.Write(Shaders.Count);
                    foreach (string s in Shaders)
                    {
                        BaseMap b = baseMapOf(s) ?? new BaseMap();
                        w.Write(s ?? "");
                        w.Write(b.Bitmap ?? "");
                        w.Write(b.ScaleX); w.Write(b.ScaleY); w.Write(b.OffsetX); w.Write(b.OffsetY);
                        w.Write(b.Blend ?? "opaque");
                        w.Write((byte)((b.AlphaTest ? 1 : 0) | (b.SelfIllum ? 2 : 0)));
                        foreach (float c in b.Color) w.Write(c);
                        w.Write((byte)b.Layers.Length);
                        foreach (TexLayer l in b.Layers) WriteLayer(w, l);
                        WriteLayer(w, b.BlendMap);
                    }
                }
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
        }

        private static void WriteLayer(BinaryWriter w, TexLayer l)
        {
            w.Write(l == null ? "" : l.Bitmap ?? "");
            w.Write(l == null ? 1f : l.ScaleX); w.Write(l == null ? 1f : l.ScaleY);
            w.Write(l == null ? 0f : l.OffsetX); w.Write(l == null ? 0f : l.OffsetY);
        }

        private sealed class RawMesh
        {
            public float[] Positions;
            public float[] Uvs;
            public int[] Indices;
            /// <summary>Per triangle: the mesh part's material (index into the tag's materials block), or -1.</summary>
            public int[] TriangleMaterial;
        }

        // ---- shaders → base map -----------------------------------------------------------------------

        /// <summary>A bitmap and its uv transform (uv * scale + offset).</summary>
        internal class TexLayer
        {
            public string Bitmap;
            public float ScaleX = 1, ScaleY = 1, OffsetX, OffsetY;

            public void CopyTilingFrom(TexLayer o) { ScaleX = o.ScaleX; ScaleY = o.ScaleY; OffsetX = o.OffsetX; OffsetY = o.OffsetY; }
        }

        /// <summary>What the view needs from a shader: its color bitmap, that bitmap's uv transform, how it blends.</summary>
        internal sealed class BaseMap : TexLayer
        {
            /// <summary>The render method's blend_mode option: opaque, additive, multiply, alpha_blend, double_multiply, pre_multiplied_alpha.</summary>
            public string Blend = "opaque";
            public bool AlphaTest;
            public bool SelfIllum;
            /// <summary>Terrain: up to 4 materials (base_map_m_0..3) mixed by BlendMap's r, g, b, a channels.</summary>
            public TexLayer[] Layers = new TexLayer[0];
            public TexLayer BlendMap;
            /// <summary>Flat color (rgba) for surfaces without a color map, e.g. water. Alpha 0 = none.</summary>
            public float[] Color = new float[4];
            /// <summary>The option tags the shader uses (they hold each bitmap's default tiling).</summary>
            public readonly List<string> OptionTags = new List<string>();

            /// <summary>Default tiling of a bitmap parameter, from the shader's options (1 if none says).</summary>
            public float DefaultScale(string parameter)
            {
                foreach (string option in OptionTags)
                {
                    float s;
                    if (ScalesOf(option).TryGetValue(parameter, out s) && s > 0.0001f && s < 10000f) return s;
                }
                return 1f;
            }
        }

        private static Dictionary<string, float> ScalesOf(string option)
        {
            Dictionary<string, float> scales;
            if (OptionScales.TryGetValue(option, out scales)) return scales;
            scales = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var tag = new TagFile(TagService.TagPathOf(option)))
                {
                    var ps = tag.SelectField("Block:parameters") as TagFieldBlock;
                    for (int i = 0; ps != null && i < ps.Elements.Count; i++)
                    {
                        string name = TagService.ValueOf(tag.SelectField("Block:parameters[" + i + "]/StringId:parameter name")) ?? "";
                        var scale = tag.SelectField("Block:parameters[" + i + "]/Real:default bitmap scale") as TagFieldElementSingle;
                        if (name.Length > 0 && scale != null && !scales.ContainsKey(name)) scales[name] = scale.Data;
                    }
                }
            }
            catch (Exception) { }
            OptionScales[option] = scales;
            return scales;
        }

        private static readonly Dictionary<string, BaseMap> BaseMaps = new Dictionary<string, BaseMap>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Per render method definition: category name → its option names.</summary>
        private static readonly Dictionary<string, List<KeyValuePair<string, string[]>>> Definitions =
            new Dictionary<string, List<KeyValuePair<string, string[]>>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Per render method definition: each category's option tags (same order), plus its global options tag.</summary>
        private static readonly Dictionary<string, List<string[]>> OptionTags = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> GlobalOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Per option tag: bitmap parameter → its default bitmap scale (the tiling a shader gets unless it animates it).</summary>
        private static readonly Dictionary<string, Dictionary<string, float>> OptionScales = new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);

        private const string RenderMethod = "Struct:render_method[0]";

        /// <summary>
        /// A shader's color map (base_map, or another base_map* / diffuse / color / detail map whose tag exists;
        /// a parent shader's when the shader only overrides other parameters) with its tiling, plus the blend mode
        /// and alpha test chosen in its options (read through the render method definition, so it works for every
        /// shader type). Terrain shaders keep their 4 blended materials; water gets a flat sea color.
        /// </summary>
        private static BaseMap BaseMapOf(string shader)
        {
            if (string.IsNullOrEmpty(shader)) return null;
            BaseMap found;
            if (BaseMaps.TryGetValue(shader, out found)) return found;
            found = new BaseMap();
            try
            {
                using (var tag = new TagFile(TagService.TagPathOf(shader)))
                {
                    ReadOptions(tag, found);
                    string ext = Path.GetExtension(shader).ToLowerInvariant();
                    if (ext == ".shader_water")
                    {
                        found.Blend = "alpha_blend";
                        found.Color = new[] { 0.10f, 0.27f, 0.34f, 0.72f };
                    }
                    if (ext == ".shader_terrain") ReadTerrain(tag, found);
                    string parent = TagService.ValueOf(tag.SelectField(RenderMethod + "/Reference:reference"));
                    if (!FindColorMap(tag, found) && found.Layers.Length == 0 && !string.IsNullOrEmpty(parent)
                        && !string.Equals(parent, shader, StringComparison.OrdinalIgnoreCase))
                    {
                        BaseMaps[shader] = found; // guards against reference loops
                        BaseMap inherited = BaseMapOf(parent);
                        if (inherited != null && inherited.Bitmap != null)
                        {
                            found.Bitmap = inherited.Bitmap;
                            found.CopyTilingFrom(inherited);
                        }
                    }
                }
            }
            catch (Exception) { }
            BaseMaps[shader] = found;
            return found;
        }

        /// <summary>Parameter name → (index, bitmap) for every bitmap parameter whose tag exists.</summary>
        private static Dictionary<string, KeyValuePair<int, string>> BitmapParameters(TagFile tag, out TagFieldBlock block)
        {
            const string ps = RenderMethod + "/Block:parameters";
            block = tag.SelectField(ps) as TagFieldBlock;
            var result = new Dictionary<string, KeyValuePair<int, string>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; block != null && i < block.Elements.Count; i++)
            {
                string name = (TagService.ValueOf(tag.SelectField(ps + "[" + i + "]/StringId:parameter name")) ?? "").ToLowerInvariant();
                string bitmap = TagService.ValueOf(tag.SelectField(ps + "[" + i + "]/Reference:bitmap"));
                if (!string.IsNullOrEmpty(bitmap) && !result.ContainsKey(name) && TagService.TagExists(bitmap))
                    result[name] = new KeyValuePair<int, string>(i, bitmap);
            }
            return result;
        }

        private static void ReadTerrain(TagFile tag, BaseMap map)
        {
            TagFieldBlock block;
            var maps = BitmapParameters(tag, out block);
            KeyValuePair<int, string> blend;
            if (!maps.TryGetValue("blend_map", out blend)) return;
            var layers = new TexLayer[4];
            bool any = false;
            for (int m = 0; m < 4; m++)
            {
                KeyValuePair<int, string> p;
                if (!maps.TryGetValue("base_map_m_" + m, out p)) continue;
                float d = map.DefaultScale("base_map_m_" + m);
                layers[m] = new TexLayer { Bitmap = p.Value, ScaleX = d, ScaleY = d };
                ReadTiling(block.Elements[p.Key], layers[m]);
                any = true;
            }
            if (!any) return;
            map.Layers = layers;
            float bd = map.DefaultScale("blend_map");
            map.BlendMap = new TexLayer { Bitmap = blend.Value, ScaleX = bd, ScaleY = bd };
            ReadTiling(block.Elements[blend.Key], map.BlendMap);
        }

        public static ShaderInfoResult ShaderInfo(TagListParams p) => new ShaderInfoResult
        {
            Shaders = (p.Tags ?? new List<string>()).Select(sh =>
            {
                BaseMap b = BaseMapOf(sh);
                string bitmap = b == null ? null : b.Bitmap ?? (b.Layers.FirstOrDefault(l => l != null) ?? new TexLayer()).Bitmap;
                return new ShaderInfo { Shader = sh, BaseMap = bitmap ?? "" };
            }).ToList()
        };

        /// <summary>Rank of a parameter as the surface color: 0 base_map ... 3 detail map; -1 not a color map.</summary>
        private static int ColorRank(string name)
        {
            if (name == "base_map") return 0;
            if (name.StartsWith("base_map")) return 1;
            if (name == "diffuse_map" || name == "color_map" || name == "albedo_map") return 2;
            if (name.StartsWith("detail_map") && !name.Contains("bump")) return 3;
            return -1;
        }

        private static bool FindColorMap(TagFile tag, BaseMap map)
        {
            TagFieldBlock block;
            var maps = BitmapParameters(tag, out block);
            int pick = -1, rank = int.MaxValue;
            string bitmap = null;
            foreach (var kv in maps)
            {
                int r = ColorRank(kv.Key);
                if (r < 0 || r >= rank) continue;
                rank = r; pick = kv.Value.Key; bitmap = kv.Value.Value;
            }
            if (pick < 0) return false;
            map.Bitmap = bitmap;
            string pickedName = maps.First(kv => kv.Value.Key == pick).Key;
            map.ScaleX = map.ScaleY = map.DefaultScale(pickedName);
            ReadTiling(block.Elements[pick], map);
            return true;
        }

        /// <summary>The options block holds one option index per category of the render method definition.</summary>
        private static void ReadOptions(TagFile tag, BaseMap map)
        {
            string definition = TagService.ValueOf(tag.SelectField(RenderMethod + "/Reference:definition"));
            var categories = CategoriesOf(definition);
            var options = tag.SelectField(RenderMethod + "/Block:options") as TagFieldBlock;
            if (categories == null || options == null) return;
            List<string[]> refs;
            OptionTags.TryGetValue(definition, out refs);
            string global;
            if (GlobalOptions.TryGetValue(definition, out global) && !string.IsNullOrEmpty(global)) map.OptionTags.Add(global);
            for (int c = 0; c < categories.Count && c < options.Elements.Count; c++)
            {
                int index = Int(options.Elements[c].Fields[0]);
                if (refs != null && c < refs.Count && index >= 0 && index < refs[c].Length && !string.IsNullOrEmpty(refs[c][index])) map.OptionTags.Add(refs[c][index]);
                string[] names = categories[c].Value;
                string option = index >= 0 && index < names.Length ? names[index] : (names.Length > 0 ? names[0] : "");
                switch (categories[c].Key)
                {
                    case "blend_mode": map.Blend = string.IsNullOrEmpty(option) ? "opaque" : option; break;
                    case "alpha_test": map.AlphaTest = option != "none" && option != "off" && !string.IsNullOrEmpty(option); break;
                    case "self_illumination": map.SelfIllum = option != "off" && option != "none" && !string.IsNullOrEmpty(option); break;
                }
            }
        }

        private static List<KeyValuePair<string, string[]>> CategoriesOf(string definition)
        {
            if (string.IsNullOrEmpty(definition)) return null;
            List<KeyValuePair<string, string[]>> list;
            if (Definitions.TryGetValue(definition, out list)) return list;
            list = null;
            try
            {
                using (var tag = new TagFile(TagService.TagPathOf(definition)))
                {
                    list = new List<KeyValuePair<string, string[]>>();
                    var refs = new List<string[]>();
                    GlobalOptions[definition] = TagService.ValueOf(tag.SelectField("Reference:global options"));
                    var cats = tag.SelectField("Block:categories") as TagFieldBlock;
                    for (int i = 0; cats != null && i < cats.Elements.Count; i++)
                    {
                        string name = TagService.ValueOf(tag.SelectField("Block:categories[" + i + "]/StringId:category name")) ?? "";
                        var opts = tag.SelectField("Block:categories[" + i + "]/Block:options") as TagFieldBlock;
                        var names = new string[opts == null ? 0 : opts.Elements.Count];
                        var optionRefs = new string[names.Length];
                        for (int j = 0; j < names.Length; j++)
                        {
                            names[j] = TagService.ValueOf(tag.SelectField("Block:categories[" + i + "]/Block:options[" + j + "]/StringId:option name")) ?? "";
                            optionRefs[j] = TagService.ValueOf(tag.SelectField("Block:categories[" + i + "]/Block:options[" + j + "]/Reference:option"));
                        }
                        list.Add(new KeyValuePair<string, string[]>(name, names));
                        refs.Add(optionRefs);
                    }
                    OptionTags[definition] = refs;
                }
            }
            catch (Exception) { list = null; }
            Definitions[definition] = list;
            return list;
        }

        /// <summary>
        /// A bitmap parameter's "animated parameters" hold its uv transform (scale uniform / scale x / scale y /
        /// translation x / translation y), each a function; the value at time 0 is what the level shows.
        /// </summary>
        /// <summary>A few raw animated parameters seen while reading tiling (for diagnostics in the export notes).</summary>
        internal static readonly List<string> TilingNotes = new List<string>();

        private static readonly string[] AnimatedTypes = { "value", "color", "scale uniform", "scale x", "scale y", "translation x", "translation y", "frame index", "alpha" };

        private static void ReadTiling(TagElement parameter, TexLayer map)
        {
            var animated = parameter.Fields.FirstOrDefault(f => f.FieldName == "animated parameters") as TagFieldBlock;
            if (animated == null) return;
            foreach (TagElement e in animated.Elements)
            {
                int type = -1;
                float? value = null;
                string source = null;
                foreach (TagField f in Flatten(e.Fields, 3))
                {
                    if (f.FieldName == "type" && type < 0)
                    {
                        var en = f as TagFieldEnum;
                        if (en != null) type = en.Value;
                        else
                        {
                            int parsed;
                            if (int.TryParse((TagService.ValueOf(f) ?? "").Split(' ')[0], out parsed)) type = parsed;
                        }
                    }
                    var fn = f as TagFieldCustomFunctionEditor;
                    if (fn != null && value == null)
                    {
                        value = FunctionValue(fn, out source);
                    }
                    var data = f as TagFieldData;
                    if (data != null && value == null)
                    {
                        value = FunctionValue(data);
                        source = "data";
                        if (TilingNotes.Count < 6)
                            try
                            {
                                byte[] raw = data.GetData();
                                TilingNotes.Add(type + " = " + (value.HasValue ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null") + " [" +
                                    (raw == null ? "" : BitConverter.ToString(raw, 0, Math.Min(raw.Length, 24))) + "]");
                            }
                            catch (Exception) { }
                    }
                }
                if (TilingNotes.Count < 6 && source != "data")
                    TilingNotes.Add(type + " = " + (value.HasValue ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null") + " (" + (source ?? "no value") + ")");
                if (type < 0 || type >= AnimatedTypes.Length || value == null || float.IsNaN(value.Value) || float.IsInfinity(value.Value)) continue;
                string t = AnimatedTypes[type];
                float v = value.Value;
                if (t.StartsWith("scale"))
                {
                    if (Math.Abs(v) < 1e-4f || Math.Abs(v) > 1e4f) continue;
                    if (t == "scale uniform") { map.ScaleX = v; map.ScaleY = v; }
                    else if (t == "scale x") map.ScaleX = v;
                    else map.ScaleY = v;
                }
                else if (t == "translation x") map.OffsetX = v;
                else if (t == "translation y") map.OffsetY = v;
            }
        }

        /// <summary>
        /// A mapping function's value at rest. The serialized function starts with its type byte and flags, then the
        /// minimum (and maximum) as floats at offsets 4 and 8; a constant or an unranged function outputs the minimum.
        /// </summary>
        /// <summary>
        /// Value of a function editor at rest. Shader animated parameters are "basic" constant functions whose value
        /// ManagedBlam reports as the clamp range minimum (Evaluate gives 0 for them); others are evaluated.
        /// </summary>
        private static float? FunctionValue(TagFieldCustomFunctionEditor fn, out string source)
        {
            source = "function";
            try
            {
                var value = fn.Value;
                float e = value.Evaluate(0f, 1f);
                if (Math.Abs(e) > 1e-4f) { source = "eval"; return e; }
                float clamp = value.ClampRangeMin;
                if (Math.Abs(clamp) > 1e-4f) { source = "constant"; return clamp; }
                float amp = value.GetAmplitudeMin(0);
                if (Math.Abs(amp) > 1e-4f) { source = "amplitude"; return amp; }
            }
            catch (Exception ex)
            {
                if (FunctionNotes.Count < 4) FunctionNotes.Add(ex.GetType().Name + ": " + ex.Message);
            }
            return null;
        }

        internal static readonly List<string> FunctionNotes = new List<string>();

        private static float? FunctionValue(TagFieldData data)
        {
            try
            {
                byte[] b = data.GetData();
                if (b == null || b.Length < 8) return null;
                float v = BitConverter.ToSingle(b, 4);
                if (float.IsNaN(v) || float.IsInfinity(v)) return null;
                return v;
            }
            catch (Exception) { return null; }
        }

        private static IEnumerable<TagField> Flatten(IEnumerable<TagField> fields, int depth)
        {
            foreach (TagField f in fields)
            {
                yield return f;
                var s = f as TagFieldStruct;
                if (s != null && depth > 0)
                    foreach (TagElement e in s.Elements)
                        foreach (TagField g in Flatten(e.Fields, depth - 1)) yield return g;
            }
        }

        private static string[] Materials(TagFile tag, string block)
        {
            var b = tag.SelectField(block) as TagFieldBlock;
            int n = b == null ? 0 : b.Elements.Count;
            var list = new string[n];
            for (int i = 0; i < n; i++) list[i] = TagService.ValueOf(tag.SelectField(block + "[" + i + "]/Reference:render method"));
            return list;
        }

        // ---- levels -------------------------------------------------------------------------------------

        public static ExportLevelMeshResult Export(TagService tags, string scenario, string outputFile, List<int> only = null)
        {
            var sw = Stopwatch.StartNew();
            TagFile scen = tags.GetOrOpen(scenario);
            var bspPaths = new List<string>();
            var block = scen.SelectField("Block:structure bsps") as TagFieldBlock;
            if (block != null)
                for (int i = 0; i < block.Elements.Count; i++)
                {
                    if (only != null && !only.Contains(i)) continue;
                    string p = TagService.ValueOf(scen.SelectField("Block:structure bsps[" + i + "]/Reference:structure bsp"));
                    if (!string.IsNullOrEmpty(p)) bspPaths.Add(p);
                }

            var sink = new MeshSink();
            var result = new ExportLevelMeshResult { File = outputFile };
            var notes = new List<string>();
            foreach (string bspPath in bspPaths)
            {
                try
                {
                    using (var bsp = new TagFile(TagService.TagPathOf(bspPath)))
                        AddBsp(bsp, sink, result, notes);
                }
                catch (Exception ex)
                {
                    notes.Add(Path.GetFileName(bspPath) + ": " + ex.Message);
                }
            }
            sink.Write(outputFile, BaseMapOf);
            result.Vertices = sink.Positions.Count / 3;
            result.Triangles = sink.Indices.Count / 3;
            result.Ms = sw.ElapsedMilliseconds;
            notes.Add(sink.Shaders.Count + " materials");
            if (TilingNotes.Count > 0) notes.Add("tiling samples: " + string.Join(" | ", TilingNotes.Take(6)));
            if (FunctionNotes.Count > 0) notes.Add("functions: " + string.Join(" | ", FunctionNotes));
            result.Notes = string.Join("; ", notes);
            return result;
        }

        private static void AddBsp(TagFile bsp, MeshSink sink, ExportLevelMeshResult result, List<string> notes)
        {
            var temp = bsp.SelectField("Struct:render geometry[0]/Block:per mesh temporary") as TagFieldBlock;
            if (temp == null) { notes.Add("no per mesh temporary"); return; }
            float[][] compression = Compression(bsp);
            int[] materials = Materials(bsp, "Block:materials").Select(sink.Material).ToArray();
            var cache = new Dictionary<int, RawMesh>();
            Func<int, RawMesh> mesh = i =>
            {
                RawMesh m;
                if (!cache.TryGetValue(i, out m))
                {
                    m = i >= 0 && i < temp.Elements.Count ? ReadMesh(bsp, i, temp.Elements[i], notes) : null;
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
                    float error;
                    sink.Append(Best(m, index < compression.Length ? compression[index] : null, null, target, ref clustersDecompressed, out error), null, materials);
                    result.Clusters++;
                }

            // Each instance points at an instance definition, which names the real mesh and compression box.
            const string defs = "Struct:resource interface[0]/Block:raw_resources[0]/Struct:raw_items[0]/Block:instanced geometries definitions";
            var defBlock = bsp.SelectField(defs) as TagFieldBlock;
            int defCount = defBlock == null ? 0 : defBlock.Elements.Count;
            var defMesh = new int[defCount];
            var defComp = new int[defCount];
            for (int d = 0; d < defCount; d++)
            {
                defMesh[d] = Int(bsp.SelectField(defs + "[" + d + "]/ShortInteger:mesh index"));
                defComp[d] = Int(bsp.SelectField(defs + "[" + d + "]/ShortInteger:compression index"));
            }

            var instances = bsp.SelectField("Block:instanced geometry instances") as TagFieldBlock;
            if (instances != null)
                for (int k = 0; k < instances.Elements.Count; k++)
                {
                    string e = "Block:instanced geometry instances[" + k + "]/";
                    int definition = Int(bsp.SelectField(e + "ShortBlockIndex:instance definition"));
                    int index = Int(bsp.SelectField(e + "ShortInteger:mesh_index"));
                    int ci = Int(bsp.SelectField(e + "ShortInteger:compression_index"));
                    if (definition >= 0 && definition < defCount && defMesh[definition] >= 0)
                    {
                        index = defMesh[definition];
                        if (defComp[definition] >= 0) ci = defComp[definition];
                    }
                    RawMesh m = mesh(index);
                    if (m == null) continue;
                    float scale = Float(bsp.SelectField(e + "Real:scale"));
                    if (scale == 0) scale = 1;
                    float[] f = Vec(bsp.SelectField(e + "RealVector3d:forward"));
                    float[] l = Vec(bsp.SelectField(e + "RealVector3d:left"));
                    float[] u = Vec(bsp.SelectField(e + "RealVector3d:up"));
                    float[] p = Vec(bsp.SelectField(e + "RealPoint3d:position"));
                    float[] t = { scale, f[0], f[1], f[2], l[0], l[1], l[2], u[0], u[1], u[2], p[0], p[1], p[2] };
                    float[] tt = { scale, f[0], l[0], u[0], f[1], l[1], u[1], f[2], l[2], u[2], p[0], p[1], p[2] };
                    float[] target =
                    {
                        Float(bsp.SelectField(e + "Real:bounds x0")), Float(bsp.SelectField(e + "Real:bounds x1")),
                        Float(bsp.SelectField(e + "Real:bounds y0")), Float(bsp.SelectField(e + "Real:bounds y1")),
                        Float(bsp.SelectField(e + "Real:bounds z0")), Float(bsp.SelectField(e + "Real:bounds z1")),
                    };
                    float[] comp = ci >= 0 && ci < compression.Length ? compression[ci] : index < compression.Length ? compression[index] : null;
                    // The basis vectors may be rows or columns of the rotation; keep whichever fits the recorded bounds.
                    int dec1 = 0, dec2 = 0;
                    float e1, e2;
                    RawMesh b1 = Best(m, comp, t, target, ref dec1, out e1);
                    RawMesh b2 = Best(m, comp, tt, target, ref dec2, out e2);
                    bool columns = e2 + 1e-4f < e1;
                    if (columns) transposed++;
                    instancesDecompressed += columns ? dec2 : dec1;
                    if (Math.Min(e1, e2) > 0.5f) instancesOff++;
                    sink.Append(columns ? b2 : b1, columns ? tt : t, materials);
                    result.Instances++;
                }
            notes.Add(string.Format("{0}: clusters {1}/{2} decompressed, instances {3}/{4} decompressed, {5} transposed, {6} off their bounds",
                Path.GetFileNameWithoutExtension(bsp.Path.RelativePathWithExtension), clustersDecompressed, clusters == null ? 0 : clusters.Elements.Count,
                instancesDecompressed, instances == null ? 0 : instances.Elements.Count, transposed, instancesOff));
        }

        // ---- object models ----------------------------------------------------------------------------

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
                    var sink = new MeshSink();
                    using (var tag = new TagFile(TagService.TagPathOf(model)))
                        AddModel(tag, sink);
                    sink.Write(info.File, BaseMapOf);
                    info.Triangles = sink.Indices.Count / 3;
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

        private static void AddModel(TagFile tag, MeshSink sink)
        {
            var temp = tag.SelectField("Struct:render geometry[0]/Block:per mesh temporary") as TagFieldBlock;
            var meshes = tag.SelectField("Struct:render geometry[0]/Block:meshes") as TagFieldBlock;
            if (temp == null || meshes == null) return;
            int[] materials = Materials(tag, "Block:materials").Select(sink.Material).ToArray();
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
                RawMesh raw = ReadMesh(tag, m, temp.Elements[m], notes);
                if (raw == null) continue;
                // Model vertices are stored normalized inside the model's compression box.
                float[] comp = compression.Length == 0 ? null : compression[m < compression.Length && compression.Length == meshes.Elements.Count ? m : 0];
                if (comp != null && (((int)comp[6]) & 1) != 0 && LooksNormalized(raw.Positions))
                    raw = Decompressed(raw, comp);
                int vertexType = Int(tag.SelectField("Struct:render geometry[0]/Block:meshes[" + m + "]/CharEnum:vertex type"));
                int rigidNode = Int(tag.SelectField("Struct:render geometry[0]/Block:meshes[" + m + "]/CharInteger:rigid node index"));
                float[] t = vertexType == 1 && rigidNode >= 0 && rigidNode < nodes.Length ? nodes[rigidNode] : null;
                sink.Append(raw, t, materials);
            }
        }

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

        // ---- compression ----------------------------------------------------------------------------------

        /// <summary>
        /// Compression boxes of a render geometry: x0, x1, y0, y1, z0, z1, flags, u0, u1, v0, v1 (the tag's labels are
        /// shifted: position bounds 0 = x0 x1 y0, position bounds 1 = y1 z0 z1, texcoord bounds 0 = u0 u1, 1 = v0 v1).
        /// </summary>
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
                float[] t0 = Vec2(tag.SelectField(e + "RealPoint2d:texcoord bounds 0"));
                float[] t1 = Vec2(tag.SelectField(e + "RealPoint2d:texcoord bounds 1"));
                var flags = tag.SelectField(e + "WordFlags:compression flags") as TagFieldFlags;
                list[i] = new[] { b0[0], b0[1], b0[2], b1[0], b1[1], b1[2], flags == null ? 1f : flags.RawValue, t0[0], t0[1], t1[0], t1[1] };
            }
            return list;
        }

        private static RawMesh Decompressed(RawMesh raw, float[] c)
        {
            var p = new float[raw.Positions.Length];
            for (int i = 0; i + 2 < p.Length; i += 3)
            {
                p[i] = c[0] + raw.Positions[i] * (c[1] - c[0]);
                p[i + 1] = c[2] + raw.Positions[i + 1] * (c[3] - c[2]);
                p[i + 2] = c[4] + raw.Positions[i + 2] * (c[5] - c[4]);
            }
            float[] uv = raw.Uvs;
            bool texCompressed = (((int)c[6]) & 2) != 0 && (c[8] != c[7] || c[10] != c[9]);
            if (uv != null && texCompressed && LooksNormalized(uv))
            {
                uv = new float[raw.Uvs.Length];
                for (int i = 0; i + 1 < uv.Length; i += 2)
                {
                    uv[i] = c[7] + raw.Uvs[i] * (c[8] - c[7]);
                    uv[i + 1] = c[9] + raw.Uvs[i + 1] * (c[10] - c[9]);
                }
            }
            return new RawMesh { Positions = p, Uvs = uv, Indices = raw.Indices, TriangleMaterial = raw.TriangleMaterial };
        }

        private static bool LooksNormalized(float[] raw)
        {
            foreach (float v in raw) if (v < -0.01f || v > 1.01f) return false;
            return raw.Length > 0;
        }

        /// <summary>Raw or decompressed positions, whichever lands closest to the world bounds the tag records.</summary>
        private static RawMesh Best(RawMesh raw, float[] comp, float[] t, float[] target, ref int decompressed, out float error)
        {
            float rawError = BoundsError(raw.Positions, t, target);
            error = rawError;
            if (comp == null || !LooksNormalized(raw.Positions)) return raw;
            RawMesh d = Decompressed(raw, comp);
            float dError = BoundsError(d.Positions, t, target);
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

        // ---- reading meshes -----------------------------------------------------------------------------------

        /// <summary>Vertices, uvs and triangles of one mesh, each triangle tagged with its part's material.</summary>
        private static RawMesh ReadMesh(TagFile tag, int meshIndex, TagElement element, List<string> notes)
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
            float[] pos, uv;
            ReadVertices(verts, out pos, out uv);
            int[] raw = words32 != null && words32.Elements.Count > 0 ? ReadIndices(words32, 4) : words != null ? ReadIndices(words, 2) : new int[0];

            // Parts: ranges of the index buffer drawn with one material.
            string pp = "Struct:render geometry[0]/Block:meshes[" + meshIndex + "]/Block:parts";
            var partsBlock = tag.SelectField(pp) as TagFieldBlock;
            var parts = new List<int[]>();
            for (int i = 0; partsBlock != null && i < partsBlock.Elements.Count; i++)
                parts.Add(new[]
                {
                    Int(tag.SelectField(pp + "[" + i + "]/LongInteger:index start")),
                    Int(tag.SelectField(pp + "[" + i + "]/LongInteger:index count")),
                    Int(tag.SelectField(pp + "[" + i + "]/ShortBlockIndex:render method index")),
                });
            Func<int, int> materialAt = rawIndex =>
            {
                foreach (int[] p in parts) if (rawIndex >= p[0] && rawIndex < p[0] + p[1]) return p[2];
                return parts.Count > 0 ? parts[0][2] : -1;
            };

            bool strips = (flags & 1) != 0 && (flags & 2) == 0;
            int vc = pos.Length / 3;
            var tris = new List<int>(raw.Length);
            var triMaterial = new List<int>(raw.Length / 3);
            Action<int, int, int, int> add = (a, b, c, at) =>
            {
                if (a < 0 || b < 0 || c < 0 || a >= vc || b >= vc || c >= vc || a == b || b == c || a == c) return;
                tris.Add(a); tris.Add(b); tris.Add(c);
                triMaterial.Add(materialAt(at));
            };
            if (strips)
            {
                for (int i = 0; i + 2 < raw.Length; i++)
                {
                    if (raw[i] == 0xFFFF || raw[i + 1] == 0xFFFF || raw[i + 2] == 0xFFFF) continue;
                    if ((i & 1) == 0) add(raw[i], raw[i + 1], raw[i + 2], i);
                    else add(raw[i + 1], raw[i], raw[i + 2], i);
                }
            }
            else
            {
                for (int i = 0; i + 2 < raw.Length; i += 3) add(raw[i], raw[i + 1], raw[i + 2], i);
            }
            return new RawMesh { Positions = pos, Uvs = uv, Indices = tris.ToArray(), TriangleMaterial = triMaterial.ToArray() };
        }

        /// <summary>Position (field 0) and texcoord (field 1) of every raw vertex: one bulk read when the serialized layout checks out, else field by field.</summary>
        private static void ReadVertices(TagFieldBlock verts, out float[] positions, out float[] uvs)
        {
            int n = verts.Elements.Count;
            positions = new float[n * 3];
            uvs = new float[n * 2];
            float[] first = Vec(verts.Elements[0].Fields[0]);
            float[] last = Vec(verts.Elements[n - 1].Fields[0]);
            float[] firstUv = Vec2(verts.Elements[0].Fields[1]);
            float[] lastUv = Vec2(verts.Elements[n - 1].Fields[1]);
            try
            {
                byte[] bytes = verts.SerializeEntireBlock();
                if (bytes != null && bytes.Length >= n * 20)
                {
                    foreach (int header in new[] { 0, bytes.Length - (bytes.Length / n) * n })
                    {
                        int stride = (bytes.Length - header) / n;
                        if (stride < 20 || header + stride * n != bytes.Length) continue;
                        if (!Same(bytes, header, first) || !Same(bytes, header + stride * (n - 1), last)) continue;
                        bool uvAt12 = BitConverter.ToSingle(bytes, header + 12) == firstUv[0] && BitConverter.ToSingle(bytes, header + 16) == firstUv[1]
                            && BitConverter.ToSingle(bytes, header + stride * (n - 1) + 12) == lastUv[0] && BitConverter.ToSingle(bytes, header + stride * (n - 1) + 16) == lastUv[1];
                        for (int i = 0; i < n; i++)
                        {
                            int o = header + i * stride;
                            positions[i * 3] = BitConverter.ToSingle(bytes, o);
                            positions[i * 3 + 1] = BitConverter.ToSingle(bytes, o + 4);
                            positions[i * 3 + 2] = BitConverter.ToSingle(bytes, o + 8);
                            if (uvAt12)
                            {
                                uvs[i * 2] = BitConverter.ToSingle(bytes, o + 12);
                                uvs[i * 2 + 1] = BitConverter.ToSingle(bytes, o + 16);
                            }
                        }
                        if (!uvAt12)
                            for (int i = 0; i < n; i++)
                            {
                                float[] t = Vec2(verts.Elements[i].Fields[1]);
                                uvs[i * 2] = t[0]; uvs[i * 2 + 1] = t[1];
                            }
                        return;
                    }
                }
            }
            catch (Exception) { }
            for (int i = 0; i < n; i++)
            {
                float[] v = Vec(verts.Elements[i].Fields[0]);
                positions[i * 3] = v[0]; positions[i * 3 + 1] = v[1]; positions[i * 3 + 2] = v[2];
                float[] t = Vec2(verts.Elements[i].Fields[1]);
                uvs[i * 2] = t[0]; uvs[i * 2 + 1] = t[1];
            }
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

        private static float[] Vec2(TagField f)
        {
            var a = f as TagFieldElementArraySingle;
            if (a == null) return new float[2];
            float[] d = a.Data.ToArray();
            return d.Length >= 2 ? d : new float[2];
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
            if (b != null) return b.Value;
            var e = f as TagFieldEnum;
            return e != null ? e.Value : -1;
        }
    }
}
