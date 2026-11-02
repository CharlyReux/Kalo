using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Bungie.Tags;
using Kalo.Protocol;

namespace Kalo.BlamHost
{
    /// <summary>
    /// The names objects have in Forge's menu (e.g. mp_flag_base.crate is "Flag Stand"). Each multiplayer map's
    /// `map variant palettes` lists its Forge objects under a string id (obj_flag_base, or a variant's display name),
    /// and ui\cui\strings\forge_strings holds the English text for those ids.
    /// </summary>
    internal static class ForgeNameService
    {
        public const string StringsTag = @"ui\cui\strings\forge_strings.multilingual_unicode_string_list";

        public static ForgeNamesResult Run(TagService tags)
        {
            var sw = Stopwatch.StartNew();
            var text = ReadStrings(tags);
            var result = new ForgeNamesResult { Names = new List<ForgeName>() };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string folder = tags.FullPath(@"levels\multi");
            if (!Directory.Exists(folder)) return result;
            // Forge World first: it has the biggest menu, so its names win when maps disagree.
            var scenarios = Directory.EnumerateFiles(folder, "*.scenario", SearchOption.AllDirectories)
                .Select(f => f.Substring(TagService.Root.Length + 1))
                .Where(f => f.IndexOf(@"\_kalo", StringComparison.OrdinalIgnoreCase) < 0)
                .OrderBy(f => f.IndexOf("forge_halo", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (string scenario in scenarios)
            {
                try
                {
                    using (var tag = new TagFile(TagService.TagPathOf(scenario)))
                        Collect(tag, text, seen, result.Names);
                    result.Maps++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("forge names: skipped " + scenario + ": " + ex.Message);
                }
            }
            result.Ms = sw.ElapsedMilliseconds;
            return result;
        }

        private static void Collect(TagFile tag, Dictionary<string, string> text, HashSet<string> seen, List<ForgeName> names)
        {
            const string P = "Block:map variant palettes";
            var palettes = tag.SelectField(P) as TagFieldBlock;
            for (int a = 0; palettes != null && a < palettes.Elements.Count; a++)
            {
                var entries = tag.SelectField(P + "[" + a + "]/Block:entries") as TagFieldBlock;
                for (int b = 0; entries != null && b < entries.Elements.Count; b++)
                {
                    string entry = P + "[" + a + "]/Block:entries[" + b + "]";
                    string entryName = Lookup(text, TagService.ValueOf(tag.SelectField(entry + "/StringId:name")));
                    var variants = tag.SelectField(entry + "/Block:variants") as TagFieldBlock;
                    for (int c = 0; variants != null && c < variants.Elements.Count; c++)
                    {
                        string v = entry + "/Block:variants[" + c + "]";
                        string obj = TagService.ValueOf(tag.SelectField(v + "/Reference:object"));
                        if (string.IsNullOrEmpty(obj) || seen.Contains(obj)) continue;
                        string name = Lookup(text, TagService.ValueOf(tag.SelectField(v + "/StringId:display name"))) ?? entryName;
                        if (string.IsNullOrEmpty(name)) continue;
                        seen.Add(obj);
                        names.Add(new ForgeName { ObjectTag = obj, Name = name });
                    }
                }
            }
        }

        private static string Lookup(Dictionary<string, string> text, string id) =>
            !string.IsNullOrEmpty(id) && text.TryGetValue(id, out var s) && s.Length > 0 ? s : null;

        /// <summary>String id → English text of the Forge strings.</summary>
        private static Dictionary<string, string> ReadStrings(TagService tags)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(tags.FullPath(StringsTag))) return map;
            using (var tag = new TagFile(TagService.TagPathOf(StringsTag)))
            {
                byte[] data = (tag.SelectField("Data:string data utf8") as TagFieldData)?.GetData() ?? new byte[0];
                var refs = tag.SelectField("Block:string references") as TagFieldBlock;
                for (int i = 0; refs != null && i < refs.Elements.Count; i++)
                {
                    string id = TagService.ValueOf(tag.SelectField("Block:string references[" + i + "]/StringId:string id"));
                    string off = TagService.ValueOf(tag.SelectField("Block:string references[" + i + "]/LongInteger:english offset"));
                    if (string.IsNullOrEmpty(id) || !int.TryParse(off, out int at) || at < 0 || at >= data.Length) continue;
                    int end = Array.IndexOf(data, (byte)0, at);
                    if (end < 0) end = data.Length;
                    map[id] = Encoding.UTF8.GetString(data, at, end - at).Trim();
                }
            }
            return map;
        }
    }
}
