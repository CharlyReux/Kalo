using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Bungie.Tags;
using Kalo.Protocol;

namespace Kalo.BlamHost
{
    /// <summary>Open tags, their field trees, edits and saves. Paths are relative to the tags folder, with extension.</summary>
    internal sealed class TagService
    {
        private const int MaxElementsPerBlock = 64;
        private const int MaxDepth = 14;
        private const int MaxNodes = 30000;
        private static readonly HashSet<string> Hidden = new HashSet<string> { "Pad", "UselessPad", "Skip", "Terminator", "Custom" };

        private readonly string _tagRoot;
        private readonly Dictionary<string, TagFile> _open = new Dictionary<string, TagFile>(StringComparer.OrdinalIgnoreCase);

        public TagService(string tagRoot)
        {
            _tagRoot = tagRoot.TrimEnd('\\');
        }

        public TagDocument Open(string path, int maxElements = MaxElementsPerBlock)
        {
            path = Normalize(path);
            Close(path);
            var sw = Stopwatch.StartNew();
            var tag = new TagFile(ToTagPath(path));
            _open[path] = tag;
            long ms = sw.ElapsedMilliseconds;
            int count = 0;
            return new TagDocument
            {
                Path = path,
                Group = Path.GetExtension(path).TrimStart('.'),
                Fields = Build(tag.Fields, 0, ref count, maxElements),
                FieldCount = count,
                LoadMs = ms
            };
        }

        public SetFieldResult SetField(string path, string fieldPath, string value)
        {
            TagFile tag = Get(path);
            TagField field = tag.SelectField(fieldPath);
            if (field == null) throw new ArgumentException("No field " + fieldPath);
            if (field.ReadOnly) throw new InvalidOperationException("Field is read-only: " + fieldPath);
            value = value ?? "";
            CultureInfo inv = CultureInfo.InvariantCulture;

            if (field is TagFieldElementInteger)
                ((TagFieldElementInteger)field).Data = long.Parse(value.Trim(), NumberStyles.Integer, inv);
            else if (field is TagFieldElementSingle)
                ((TagFieldElementSingle)field).Data = float.Parse(value.Trim(), NumberStyles.Float, inv);
            else if (field is TagFieldElementArray)
            {
                string[] parts = value.Split(',').Select(p => p.Trim()).ToArray();
                if (!((TagFieldElementArray)field).SetStringData(parts)) throw new ArgumentException("Value rejected: " + value);
            }
            else if (field is TagFieldElement)
            {
                if (!((TagFieldElement)field).SetStringData(value)) throw new ArgumentException("Value rejected: " + value);
            }
            else if (field is TagFieldEnum)
            {
                var e = (TagFieldEnum)field;
                int index;
                if (int.TryParse(value, NumberStyles.Integer, inv, out index)) e.Value = index;
                else e.SetValue(value);
            }
            else if (field is TagFieldFlags)
            {
                string v = value.Trim();
                uint raw = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? uint.Parse(v.Substring(2), NumberStyles.HexNumber, inv)
                    : uint.Parse(v, NumberStyles.Integer, inv);
                ((TagFieldFlags)field).RawValue = raw;
            }
            else if (field is TagFieldBlockIndex)
                ((TagFieldBlockIndex)field).Value = int.Parse(value.Trim(), NumberStyles.Integer, inv);
            else if (field is TagFieldReference)
            {
                var r = (TagFieldReference)field;
                string v = value.Trim();
                r.Path = v.Length == 0 ? null : ToTagPath(v);
            }
            else
                throw new InvalidOperationException("This kind of field cannot be edited yet: " + field.FieldType);

            return new SetFieldResult { Value = ValueOf(tag.SelectField(fieldPath)) };
        }

        public BlockOpResult BlockOp(string path, string fieldPath, string op, int index)
        {
            TagFile tag = Get(path);
            var block = tag.SelectField(fieldPath) as TagFieldBlock;
            if (block == null) throw new ArgumentException("Not a block: " + fieldPath);
            if (block.ReadOnly) throw new InvalidOperationException("This block is read-only.");
            int count = block.Elements.Count;
            int select = -1;
            switch (op)
            {
                case BlockOps.Add:
                    if (count >= block.MaximumElementCount) throw new InvalidOperationException("This block is full (" + block.MaximumElementCount + " elements at most).");
                    block.AddElement();
                    select = count;
                    break;
                case BlockOps.Insert:
                    if (count >= block.MaximumElementCount) throw new InvalidOperationException("This block is full (" + block.MaximumElementCount + " elements at most).");
                    CheckIndex(index, count + 1);
                    block.InsertElement(index);
                    select = index;
                    break;
                case BlockOps.Duplicate:
                    if (count >= block.MaximumElementCount) throw new InvalidOperationException("This block is full (" + block.MaximumElementCount + " elements at most).");
                    CheckIndex(index, count);
                    block.DuplicateElement(index);
                    select = index + 1;
                    break;
                case BlockOps.Remove:
                    CheckIndex(index, count);
                    block.RemoveElement(index);
                    select = Math.Min(index, count - 2);
                    break;
                case BlockOps.Clear:
                    block.RemoveAllElements();
                    break;
                default:
                    throw new ArgumentException("Unknown block operation: " + op);
            }
            int nodes = 0;
            var rebuilt = tag.SelectField(fieldPath);
            List<FieldNode> list = Build(new[] { rebuilt }, 0, ref nodes);
            return new BlockOpResult { Block = list.FirstOrDefault(), SelectIndex = select };
        }

        /// <summary>
        /// Field-by-field comparison of a tag with its vanilla version: the current state (unsaved edits included
        /// when the tag is open) against a temporary copy of HREK's file placed under tags\_kalo_vanilla.
        /// </summary>
        public DiffTagResult Diff(string path, string vanillaFile)
        {
            path = Normalize(path);
            var sw = Stopwatch.StartNew();
            if (!File.Exists(vanillaFile)) throw new FileNotFoundException("No vanilla file", vanillaFile);
            string tempRel = "_kalo_vanilla\\" + path;
            string temp = Path.Combine(_tagRoot, tempRel);
            Directory.CreateDirectory(Path.GetDirectoryName(temp));
            File.Copy(vanillaFile, temp, true);
            TagFile current = null, vanilla = null;
            bool ownCurrent = false;
            try
            {
                if (!_open.TryGetValue(path, out current))
                {
                    current = new TagFile(ToTagPath(path));
                    ownCurrent = true;
                }
                vanilla = new TagFile(ToTagPath(tempRel));
                var a = new Dictionary<string, KeyValuePair<string, string>>();
                var b = new Dictionary<string, KeyValuePair<string, string>>();
                int budget = 400000;
                Flatten(vanilla.Fields, a, 0, ref budget);
                Flatten(current.Fields, b, 0, ref budget);
                var result = new DiffTagResult { Entries = new List<DiffEntry>(), FieldsCompared = b.Count, Truncated = budget <= 0 };
                foreach (var kv in b)
                {
                    KeyValuePair<string, string> old;
                    if (!a.TryGetValue(kv.Key, out old))
                        result.Entries.Add(new DiffEntry { Path = kv.Key, Name = kv.Value.Key, Current = kv.Value.Value, Kind = DiffKinds.Added });
                    else if (old.Value != kv.Value.Value)
                        result.Entries.Add(new DiffEntry { Path = kv.Key, Name = kv.Value.Key, Vanilla = old.Value, Current = kv.Value.Value, Kind = DiffKinds.Changed });
                }
                foreach (var kv in a)
                    if (!b.ContainsKey(kv.Key))
                        result.Entries.Add(new DiffEntry { Path = kv.Key, Name = kv.Value.Key, Vanilla = kv.Value.Value, Kind = DiffKinds.Removed });
                if (result.Entries.Count > 2000) { result.Entries = result.Entries.Take(2000).ToList(); result.Truncated = true; }
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }
            finally
            {
                try { if (vanilla != null) vanilla.Dispose(); } catch (Exception) { }
                try { if (ownCurrent && current != null) current.Dispose(); } catch (Exception) { }
                try { File.Delete(temp); } catch (Exception) { }
                try { Directory.Delete(Path.Combine(_tagRoot, "_kalo_vanilla"), true); } catch (Exception) { }
            }
        }

        /// <summary>Every leaf value (and every block's element count) by field path, with its display name.</summary>
        private static void Flatten(TagField[] fields, Dictionary<string, KeyValuePair<string, string>> into, int depth, ref int budget)
        {
            if (fields == null || depth > 24) return;
            foreach (TagField f in fields)
            {
                if (f == null || budget-- <= 0) return;
                string type = Safe(() => f.FieldType.ToString());
                if (Hidden.Contains(type) || f is TagFieldExplanation) continue;
                string path = Safe(() => f.FieldPath) ?? "";
                string name = Safe(() => f.DisplayName) ?? Safe(() => f.FieldName) ?? "";
                if (f is TagFieldStruct)
                {
                    ITagElementCollection els = ((TagFieldStruct)f).Elements;
                    if (els != null && els.Count > 0) Flatten(els[0].Fields, into, depth + 1, ref budget);
                }
                else if (f is TagFieldBlock || f is TagFieldArray)
                {
                    ITagElementCollection els = f is TagFieldBlock ? ((TagFieldBlock)f).Elements : ((TagFieldArray)f).Elements;
                    int n = els == null ? 0 : els.Count;
                    into[path] = new KeyValuePair<string, string>(name, n + (n == 1 ? " element" : " elements"));
                    for (int i = 0; i < n && budget > 0; i++) Flatten(els[i].Fields, into, depth + 1, ref budget);
                }
                else
                {
                    into[path] = new KeyValuePair<string, string>(name, ValueOf(f) ?? "");
                }
            }
        }

        private static void CheckIndex(int index, int count)
        {
            if (index < 0 || index >= count) throw new ArgumentOutOfRangeException("index", "Element " + index + " does not exist.");
        }

        public SaveResult Save(string path)
        {
            path = Normalize(path);
            var sw = Stopwatch.StartNew();
            Get(path).Save();
            return new SaveResult { File = Path.Combine(_tagRoot, path), Ms = sw.ElapsedMilliseconds };
        }

        public SaveResult SaveAs(string path, string newPath)
        {
            path = Normalize(path);
            newPath = Normalize(newPath);
            TagFile tag = Get(path);
            var sw = Stopwatch.StartNew();
            tag.SaveAs(ToTagPath(newPath));
            _open.Remove(path);
            _open[newPath] = tag;
            return new SaveResult { File = Path.Combine(_tagRoot, newPath), Ms = sw.ElapsedMilliseconds };
        }

        public void Close(string path)
        {
            path = Normalize(path);
            TagFile tag;
            if (_open.TryGetValue(path, out tag))
            {
                _open.Remove(path);
                try { tag.Dispose(); } catch (Exception) { }
            }
        }

        public void CloseAll()
        {
            foreach (string p in _open.Keys.ToList()) Close(p);
        }

        // ---- helpers ----------------------------------------------------------------------------

        /// <summary>The open tag, opening it if needed (shared with the tag editor, so both see the same edits).</summary>
        internal TagFile GetOrOpen(string path)
        {
            path = Normalize(path);
            TagFile tag;
            if (!_open.TryGetValue(path, out tag))
            {
                tag = new TagFile(ToTagPath(path));
                _open[path] = tag;
            }
            return tag;
        }

        internal static TagPath TagPathOf(string path) => ToTagPath(Normalize(path));

        private TagFile Get(string path)
        {
            TagFile tag;
            if (!_open.TryGetValue(Normalize(path), out tag)) throw new InvalidOperationException("Tag is not open: " + path);
            return tag;
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Empty tag path");
            string p = path.Replace('/', '\\').Trim().TrimStart('\\');
            if (p.Contains("..") || Path.IsPathRooted(p)) throw new ArgumentException("Tag paths are relative to the tags folder: " + path);
            return p;
        }

        private static TagPath ToTagPath(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) throw new ArgumentException("Tag path needs its extension: " + path);
            return TagPath.FromPathAndExtension(path.Substring(0, path.Length - ext.Length), ext.TrimStart('.'));
        }

        private static List<FieldNode> Build(TagField[] fields, int depth, ref int count, int maxElements = MaxElementsPerBlock)
        {
            var list = new List<FieldNode>();
            if (fields == null || depth > MaxDepth) return list;
            foreach (TagField f in fields)
            {
                if (f == null || count >= MaxNodes) break;
                string type = Safe(() => f.FieldType.ToString());
                if (Hidden.Contains(type)) continue;
                count++;

                var node = new FieldNode
                {
                    Path = Safe(() => f.FieldPath),
                    Name = Safe(() => f.FieldName),
                    DisplayName = Safe(() => f.DisplayName),
                    Type = type,
                    Units = Safe(() => f.Units),
                    Description = Safe(() => f.Description),
                    Visible = SafeBool(() => f.Visible, true),
                    Editable = !SafeBool(() => f.ReadOnly, false)
                };

                if (f is TagFieldStruct)
                {
                    node.Kind = ValueKinds.Struct;
                    node.Editable = false;
                    ITagElementCollection els = ((TagFieldStruct)f).Elements;
                    node.Children = els != null && els.Count > 0 ? Build(els[0].Fields, depth + 1, ref count, maxElements) : new List<FieldNode>();
                }
                else if (f is TagFieldBlock || f is TagFieldArray)
                {
                    bool isBlock = f is TagFieldBlock;
                    node.Kind = isBlock ? ValueKinds.Block : ValueKinds.Array;
                    node.Editable = false;
                    ITagElementCollection els = isBlock ? ((TagFieldBlock)f).Elements : ((TagFieldArray)f).Elements;
                    int n = els == null ? 0 : els.Count;
                    node.ElementCount = n;
                    node.Value = n + (n == 1 ? " element" : " elements");
                    node.Children = new List<FieldNode>();
                    for (int i = 0; i < Math.Min(n, maxElements) && count < MaxNodes; i++)
                    {
                        TagElement el = els[i];
                        count++;
                        node.Children.Add(new FieldNode
                        {
                            Path = Safe(() => el.FieldPath),
                            Name = "[" + i + "]",
                            DisplayName = Safe(() => el.ElementHeaderText),
                            Type = "Element",
                            Kind = ValueKinds.Element,
                            Visible = true,
                            Children = Build(el.Fields, depth + 1, ref count, maxElements)
                        });
                    }
                    node.Truncated = n > node.Children.Count;
                }
                else
                {
                    node.Kind = KindOf(f);
                    node.Value = ValueOf(f);
                    if (node.Kind == ValueKinds.Explanation || node.Kind == ValueKinds.Other) node.Editable = false;
                    if (f is TagFieldEnum)
                        node.Options = SafeList(() => ((TagFieldEnum)f).Items.Select(i => string.IsNullOrEmpty(i.DisplayName) ? i.EnumName : i.DisplayName).ToList());
                    else if (f is TagFieldFlags)
                        node.Options = SafeList(() => ((TagFieldFlags)f).Items.Select(i => string.IsNullOrEmpty(i.DisplayName) ? i.FlagName : i.DisplayName).ToList());
                    else if (f is TagFieldReference)
                        node.Allowed = SafeList(() => ((TagFieldReference)f).Definition.GetAllowedGroupTypes().Select(g => g.Extension).Where(e => !string.IsNullOrEmpty(e)).Distinct().ToList());
                }
                list.Add(node);
            }
            return list;
        }

        private static string KindOf(TagField f)
        {
            if (f is TagFieldElementInteger) return ValueKinds.Integer;
            if (f is TagFieldElementSingle) return ValueKinds.Real;
            if (f is TagFieldElementArray) return ValueKinds.Bounds;
            if (f is TagFieldElement) return ValueKinds.Text;
            if (f is TagFieldEnum) return ValueKinds.Enum;
            if (f is TagFieldFlags) return ValueKinds.Flags;
            if (f is TagFieldBlockIndex) return ValueKinds.BlockIndex;
            if (f is TagFieldReference) return ValueKinds.Reference;
            if (f is TagFieldExplanation) return ValueKinds.Explanation;
            return ValueKinds.Other;
        }

        internal static string ValueOf(TagField f)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            try
            {
                if (f is TagFieldElementInteger) return ((TagFieldElementInteger)f).Data.ToString(inv);
                if (f is TagFieldElementSingle) return ((TagFieldElementSingle)f).Data.ToString("R", inv);
                if (f is TagFieldElementArraySingle) return string.Join(", ", ((TagFieldElementArraySingle)f).Data.Select(v => v.ToString("R", inv)));
                if (f is TagFieldElementArray) return string.Join(", ", ((TagFieldElementArray)f).GetStringData() ?? new string[0]);
                if (f is TagFieldElement) return ((TagFieldElement)f).GetStringData();
                if (f is TagFieldEnum) return ((TagFieldEnum)f).Value.ToString(inv);
                if (f is TagFieldFlags) return "0x" + ((TagFieldFlags)f).RawValue.ToString("X", inv);
                if (f is TagFieldBlockIndex) return ((TagFieldBlockIndex)f).Value.ToString(inv);
                if (f is TagFieldReference)
                {
                    TagPath p = ((TagFieldReference)f).Path;
                    return p == null ? "" : p.RelativePathWithExtension;
                }
                if (f is TagFieldExplanation) return ((TagFieldExplanation)f).Explanation;
            }
            catch (Exception ex)
            {
                return "<" + ex.GetType().Name + ">";
            }
            return null;
        }

        private static string Safe(Func<string> get)
        {
            try { return get(); } catch (Exception) { return null; }
        }

        private static bool SafeBool(Func<bool> get, bool fallback)
        {
            try { return get(); } catch (Exception) { return fallback; }
        }

        private static List<string> SafeList(Func<List<string>> get)
        {
            try { return get(); } catch (Exception) { return null; }
        }
    }
}
