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
    /// Level pieces: the instanced geometry of a level's bsps (buildings, rocks, pipes, crates... each a named instance,
    /// e.g. "shed_metal_walls_barn_int_04__(m10_030_spadehorn_barn)"). A piece is hidden by giving it a scale of 0.001
    /// (verified in reach_tag_test, job 0366: the pieces disappear). The bsp is first copied next to the map, so the
    /// original level and other maps keep it. The original bsp's path is kept beside the copy (".kalo_original.txt"),
    /// and showing a piece again restores its scale from there.
    /// </summary>
    internal sealed partial class ScenarioService
    {
        internal const float HiddenScale = 0.001f;

        private sealed class Piece
        {
            public int Index;
            public string Name;
            public float[] Box; // x0 x1 y0 y1 z0 z1
            public float Scale;
        }

        private readonly Dictionary<string, KeyValuePair<DateTime, List<Piece>>> _pieces =
            new Dictionary<string, KeyValuePair<DateTime, List<Piece>>>(StringComparer.OrdinalIgnoreCase);

        private string BspOf(TagFile scenario, int index) =>
            TagService.ValueOf(scenario.SelectField("Block:structure bsps[" + index + "]/Reference:structure bsp"));

        private List<Piece> PiecesOf(string bsp)
        {
            string file = _tags.FullPath(bsp);
            if (!File.Exists(file)) return new List<Piece>();
            DateTime stamp = File.GetLastWriteTimeUtc(file);
            KeyValuePair<DateTime, List<Piece>> cached;
            bsp = TagService.Normalize(bsp);
            if (_pieces.TryGetValue(bsp, out cached) && cached.Key == stamp) return cached.Value;
            var list = new List<Piece>();
            using (var t = new TagFile(TagService.TagPathOf(bsp)))
                ReadPieces(t, list);
            _pieces[bsp] = new KeyValuePair<DateTime, List<Piece>>(stamp, list);
            return list;
        }

        private static void ReadPieces(TagFile t, List<Piece> list)
        {
            var block = t.SelectField("Block:instanced geometry instances") as TagFieldBlock;
            if (block == null) return;
            for (int k = 0; k < block.Elements.Count; k++)
            {
                var piece = new Piece { Index = k, Box = new float[6], Scale = 1 };
                foreach (TagField f in block.Elements[k].Fields)
                {
                    string n = f.FieldName;
                    var real = f as TagFieldElementSingle;
                    if (n == "name") piece.Name = TagService.ValueOf(f);
                    else if (real == null) continue;
                    else if (n == "scale") piece.Scale = real.Data;
                    else if (n.StartsWith("bounds ", StringComparison.Ordinal) && n.Length == 9)
                    {
                        int axis = n[7] - 'x', end = n[8] - '0';
                        if (axis >= 0 && axis < 3 && (end == 0 || end == 1)) piece.Box[axis * 2 + end] = real.Data;
                    }
                }
                list.Add(piece);
            }
        }

        /// <summary>"kiva_garagedoor_14__(m10_030_spadehorn_barn)" → piece "kiva_garagedoor_14", group "m10_030_spadehorn_barn".</summary>
        private static void SplitName(string name, out string piece, out string group)
        {
            name = name ?? "";
            piece = name; group = "";
            int open = name.IndexOf("__(", StringComparison.Ordinal);
            if (open < 0) return;
            piece = name.Substring(0, open);
            string inside = name.Substring(open + 3).TrimEnd(')');
            int colon = inside.LastIndexOf(':');
            group = colon >= 0 ? inside.Substring(colon + 1) : inside;
        }

        private static float DistanceTo(float[] b, float x, float y, float z)
        {
            float dx = Math.Max(0, Math.Max(b[0] - x, x - b[1]));
            float dy = Math.Max(0, Math.Max(b[2] - y, y - b[3]));
            float dz = Math.Max(0, Math.Max(b[4] - z, z - b[5]));
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public LevelPiecesResult LevelPieces(LevelPiecesParams p)
        {
            var sw = Stopwatch.StartNew();
            TagFile scen = _tags.GetOrOpen(p.Path);
            var result = new LevelPiecesResult { Pieces = new List<LevelPiece>() };
            var bsps = scen.SelectField("Block:structure bsps") as TagFieldBlock;
            float x = p.Point != null && p.Point.Length > 2 ? p.Point[0] : 0, y = p.Point != null && p.Point.Length > 2 ? p.Point[1] : 0, z = p.Point != null && p.Point.Length > 2 ? p.Point[2] : 0;
            var found = new List<KeyValuePair<float, LevelPiece>>();
            for (int i = 0; bsps != null && i < bsps.Elements.Count; i++)
            {
                if (p.Bsps != null && p.Bsps.Count > 0 && !p.Bsps.Contains(i)) continue;
                string bsp = BspOf(scen, i);
                if (string.IsNullOrEmpty(bsp)) continue;
                List<Piece> pieces;
                try { pieces = PiecesOf(bsp); }
                catch (Exception ex) { Server.Log(bsp + ": " + ex.Message); continue; }
                var groupCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var piece in pieces)
                {
                    string n, g;
                    SplitName(piece.Name, out n, out g);
                    int c; groupCount.TryGetValue(g, out c); groupCount[g] = c + 1;
                }
                foreach (var piece in pieces)
                {
                    bool hidden = piece.Scale <= HiddenScale * 2;
                    float d;
                    if (p.HiddenOnly) { if (!hidden) continue; d = 0; }
                    else
                    {
                        // A hidden piece's box is tiny but still where the piece was: it can be found again to show it.
                        d = DistanceTo(piece.Box, x, y, z);
                        if (d > p.Radius) continue;
                    }
                    string name, group;
                    SplitName(piece.Name, out name, out group);
                    float[] b = piece.Box;
                    float size = Math.Max(b[1] - b[0], Math.Max(b[3] - b[2], b[5] - b[4]));
                    found.Add(new KeyValuePair<float, LevelPiece>(d + size * 0.01f, new LevelPiece
                    {
                        Bsp = i, Index = piece.Index, Name = name, Group = group, GroupCount = groupCount[group],
                        Hidden = hidden, Distance = d, Size = size, Center = new[] { (b[0] + b[1]) / 2, (b[2] + b[3]) / 2, (b[4] + b[5]) / 2 }
                    }));
                }
            }
            result.Total = found.Count;
            result.Pieces = found.OrderBy(f => f.Key).Take(Math.Max(1, p.Max)).Select(f => f.Value).ToList();
            result.Ms = sw.ElapsedMilliseconds;
            return result;
        }

        /// <summary>
        /// Hides or shows pieces of one bsp: by index, or a whole group ("m10_030_spadehorn_barn": a building and what is
        /// in it). The first time, the bsp is copied next to the map and the map points at the copy (unsaved: the map's
        /// Save keeps it). The copy is saved at once.
        /// </summary>
        public HideLevelPiecesResult HideLevelPieces(HideLevelPiecesParams p)
        {
            var sw = Stopwatch.StartNew();
            TagFile scen = _tags.GetOrOpen(p.Path);
            string bsp = BspOf(scen, p.Bsp);
            if (string.IsNullOrEmpty(bsp)) throw new InvalidOperationException("The map has no level piece " + p.Bsp + ".");
            string dir = Path.GetDirectoryName(TagService.Normalize(p.Path)) ?? "";
            string copy = Path.Combine(dir, Path.GetFileName(bsp));
            var result = new HideLevelPiecesResult { Bsp = copy };
            string originalNote = _tags.FullPath(copy) + ".kalo_original.txt";
            if (!bsp.Equals(copy, StringComparison.OrdinalIgnoreCase))
            {
                if (!p.Hide) return result; // nothing hidden in the original
                if (_tags.IsOpen(copy)) _tags.Close(copy);
                File.Copy(_tags.FullPath(bsp), _tags.FullPath(copy), true);
                File.WriteAllText(originalNote, bsp);
                _tags.SetField(p.Path, "Block:structure bsps[" + p.Bsp + "]/Reference:structure bsp", copy, true);
                result.Copied = true;
                Server.Log(p.Path + ": bsp " + p.Bsp + " is now its own copy " + copy + " (from " + bsp + ")");
            }

            List<Piece> originals = null;
            if (!p.Hide && File.Exists(originalNote))
                try { originals = PiecesOf(File.ReadAllText(originalNote).Trim()); } catch (Exception ex) { Server.Log("original bsp: " + ex.Message); }

            bool wasOpen = _tags.IsOpen(copy);
            TagFile tag = _tags.GetOrOpen(copy);
            try
            {
                var block = (TagFieldBlock)tag.SelectField("Block:instanced geometry instances");
                var wanted = new HashSet<int>(p.Indices ?? new List<int>());
                for (int k = 0; k < block.Elements.Count; k++)
                {
                    TagFieldElementSingle scale = null;
                    string name = null;
                    foreach (TagField f in block.Elements[k].Fields)
                    {
                        if (f.FieldName == "scale") scale = f as TagFieldElementSingle;
                        else if (f.FieldName == "name") name = TagService.ValueOf(f);
                    }
                    if (scale == null) continue;
                    bool match = wanted.Contains(k);
                    if (!match && !string.IsNullOrEmpty(p.Group))
                    {
                        string n, g;
                        SplitName(name, out n, out g);
                        match = g.Equals(p.Group, StringComparison.OrdinalIgnoreCase);
                    }
                    if (!match && p.AllHidden && !p.Hide) match = scale.Data <= HiddenScale * 2;
                    if (!match) continue;
                    if (p.Hide)
                    {
                        if (scale.Data <= HiddenScale * 2) continue;
                        scale.Data = HiddenScale;
                    }
                    else
                    {
                        if (scale.Data > HiddenScale * 2) continue;
                        scale.Data = originals != null && k < originals.Count && originals[k].Scale > HiddenScale * 2 ? originals[k].Scale : 1f;
                    }
                    result.Changed++;
                }
                if (result.Changed > 0)
                {
                    _tags.Save(copy);
                    // The list as saved, so the next query doesn't read the bsp file again.
                    var list = new List<Piece>();
                    ReadPieces(tag, list);
                    _pieces[TagService.Normalize(copy)] = new KeyValuePair<DateTime, List<Piece>>(File.GetLastWriteTimeUtc(_tags.FullPath(copy)), list);
                }
            }
            finally
            {
                if (!wasOpen) _tags.Close(copy);
            }
            result.Ms = sw.ElapsedMilliseconds;
            return result;
        }
    }
}
