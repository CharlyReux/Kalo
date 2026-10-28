using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Bungie;
using Bungie.Tags;

namespace Kalo.Spike0
{
    /// <summary>
    /// The only file that touches ManagedBlam. Answers the Spike 0 questions:
    ///   1. Can our own x64 net48 process start ManagedBlam on a Kalo workspace?
    ///   2. Can we walk every field of a tag, with the labels/units/descriptions the engine provides?
    ///   3. Can we change a value, save it as a new tag in the workspace and read it back?
    ///   4. How fast is it?
    /// </summary>
    internal static class BlamProbe
    {
        private const string WeaponPath = @"objects\weapons\rifle\assault_rifle\assault_rifle";
        private const string TestCopyPath = @"objects\weapons\rifle\assault_rifle\assault_rifle_kalo_spike";
        private const int MaxElementsPerBlock = 64;
        private const int MaxDepth = 16;
        private const int WeaponsToTime = 40;

        private static Report _report;

        public static void Run(string workspaceRoot, string hrekRoot, Report report)
        {
            _report = report;
            report.Section("ManagedBlam");

            var sw = Stopwatch.StartNew();
            var parameters = new ManagedBlamStartupParameters();
            parameters.InitializationLevel = InitializationType.TagsOnly;
            ManagedBlamSystem.Start(workspaceRoot, new ManagedBlamCrashCallback(OnCrash), parameters);
            report.Metric("Start (TagsOnly)", sw.ElapsedMilliseconds + " ms");

            try
            {
                string tagRoot = ManagedBlamSystem.TagRootPath;
                report.Metric("Tag root", tagRoot);
                report.Metric("Engine build", Safe(() => ManagedBlamSystem.BuildString));
                report.Metric("Tag groups known", Safe(() => TagGroupType.GetTagGroups().Length.ToString()));

                // Hard stop before any save if ManagedBlam is not pointed at the workspace.
                if (!Guard.IsUnder(tagRoot, workspaceRoot))
                    throw new SpikeAbort("ManagedBlam's tag root (" + tagRoot + ") is outside the workspace. Stopping before any save.");
                report.Check("Tag root is inside the workspace", true);

                DumpWeapon(report);
                EditSaveReload(tagRoot, hrekRoot, report);
                TimeManyLoads(tagRoot, report);
            }
            finally
            {
                sw.Restart();
                ManagedBlamSystem.Stop();
                report.Metric("Stop", sw.ElapsedMilliseconds + " ms");
            }
        }

        // ---- 2. Walk and dump ---------------------------------------------------------------

        private static void DumpWeapon(Report report)
        {
            report.Section("Field dump: assault_rifle.weapon");
            TagPath path = TagPath.FromPathAndExtension(WeaponPath, "weapon");

            var sw = Stopwatch.StartNew();
            using (var tag = new TagFile(path))
            {
                report.Metric("Load", sw.ElapsedMilliseconds + " ms");

                var rows = new List<string[]>();
                int withUnits = 0, withDescription = 0, withDisplayName = 0, editable = 0;
                sw.Restart();
                Walk(tag.Fields, f =>
                {
                    string display = Safe(() => f.DisplayName);
                    string units = Safe(() => f.Units);
                    string desc = Safe(() => f.Description);
                    if (!string.IsNullOrEmpty(units)) withUnits++;
                    if (!string.IsNullOrEmpty(desc)) withDescription++;
                    if (!string.IsNullOrEmpty(display) && display != f.FieldName) withDisplayName++;
                    if (f is TagFieldElement || f is TagFieldEnum || f is TagFieldFlags || f is TagFieldReference) editable++;
                    rows.Add(new[]
                    {
                        Safe(() => f.FieldPath),
                        Safe(() => f.FieldType.ToString()),
                        Safe(() => f.FieldName),
                        display,
                        units,
                        desc,
                        Safe(() => f.Visible.ToString()),
                        Safe(() => f.ReadOnly.ToString()),
                        ValueOf(f)
                    });
                }, 0);
                long walkMs = sw.ElapsedMilliseconds;

                string file = report.WriteTsv("assault_rifle.fields.tsv",
                    new[] { "path", "type", "name", "display_name", "units", "description", "visible", "read_only", "value" }, rows);
                report.Metric("Fields walked", rows.Count + " in " + walkMs + " ms (blocks capped at " + MaxElementsPerBlock + " elements)");
                report.Metric("Value fields (numbers, enums, flags, references)", editable.ToString());
                report.Metric("Fields with units", withUnits.ToString());
                report.Metric("Fields with a description", withDescription.ToString());
                report.Metric("Fields with a display name different from the raw name", withDisplayName.ToString());
                report.Metric("Dump", Path.GetFileName(file));
                report.Check("Walked the whole tag", rows.Count > 0);
            }
        }

        // ---- 3. Edit, save as a new tag, reload ---------------------------------------------

        private static void EditSaveReload(string tagRoot, string hrekRoot, Report report)
        {
            report.Section("Edit and save");
            TagPath source = TagPath.FromPathAndExtension(WeaponPath, "weapon");
            TagPath copy = TagPath.FromPathAndExtension(TestCopyPath, "weapon");

            string copyFile = Path.Combine(tagRoot, TestCopyPath + ".weapon");
            string hrekCopyFile = Path.Combine(hrekRoot, "tags", TestCopyPath + ".weapon");
            if (File.Exists(copyFile)) File.Delete(copyFile); // left over from a previous run (workspace only)

            string fieldPath;
            long before;
            using (var tag = new TagFile(source))
            {
                TagFieldElementInteger target = FindMagazineSize(tag);
                if (target == null)
                {
                    report.Fail("Could not find the magazine size field (magazines / rounds loaded maximum). See assault_rifle.fields.tsv.");
                    return;
                }

                fieldPath = target.FieldPath;
                before = target.Data;
                report.Metric("Field", "`" + fieldPath + "` (" + target.FieldType + ")");
                report.Metric("Value before", before.ToString());

                // Does addressing a field by its path work? Kalo's field catalog depends on it.
                TagField byPath = null;
                try { byPath = tag.SelectField(fieldPath); } catch (Exception ex) { report.Metric("SelectField error", ex.Message); }
                report.Check("SelectField(path) finds the same field", byPath != null && byPath.FieldPath == fieldPath);

                target.Data = before + 4;
                var sw = Stopwatch.StartNew();
                tag.SaveAs(copy);
                report.Metric("SaveAs", sw.ElapsedMilliseconds + " ms");
            }

            report.Check("Saved tag is in the workspace (" + TestCopyPath + ".weapon)", File.Exists(copyFile));
            report.Check("Nothing was written to HREK's tags folder", !File.Exists(hrekCopyFile));

            using (var reloaded = new TagFile(copy))
            {
                var field = SelectInteger(reloaded, fieldPath);
                long after = field == null ? long.MinValue : field.Data;
                report.Metric("Value after reload", field == null ? "field not found" : after.ToString());
                report.Check("Reloaded value is " + (before + 4), after == before + 4);
            }

            using (var original = new TagFile(source))
            {
                var field = SelectInteger(original, fieldPath);
                report.Check("The workspace's original assault_rifle.weapon is unchanged", field != null && field.Data == before);
            }
        }

        private static TagFieldElementInteger FindMagazineSize(TagFile tag)
        {
            TagFieldElementInteger found = null;
            Walk(tag.Fields, f =>
            {
                if (found != null) return;
                var integer = f as TagFieldElementInteger;
                if (integer == null) return;
                string path = Safe(() => f.FieldPath).ToLowerInvariant();
                string name = (Safe(() => f.FieldName) + " " + Safe(() => f.DisplayName)).ToLowerInvariant();
                if (path.Contains("magazines") && (name.Contains("rounds loaded maximum") || name.Contains("magazine size")))
                    found = integer;
            }, 0);
            return found;
        }

        private static TagFieldElementInteger SelectInteger(TagFile tag, string fieldPath)
        {
            try
            {
                var f = tag.SelectField(fieldPath) as TagFieldElementInteger;
                if (f != null) return f;
            }
            catch (Exception) { }

            TagFieldElementInteger found = null;
            Walk(tag.Fields, f =>
            {
                if (found == null && Safe(() => f.FieldPath) == fieldPath) found = f as TagFieldElementInteger;
            }, 0);
            return found;
        }

        // ---- 4. Speed -----------------------------------------------------------------------

        private static void TimeManyLoads(string tagRoot, Report report)
        {
            report.Section("Load speed");
            string weapons = Path.Combine(tagRoot, "objects", "weapons");
            if (!Directory.Exists(weapons))
            {
                report.Metric("Skipped", "no objects\\weapons folder");
                return;
            }

            List<string> files = Directory.EnumerateFiles(weapons, "*.weapon", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("_kalo_spike.weapon", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(WeaponsToTime)
                .ToList();

            var times = new List<long>();
            int totalFields = 0, failed = 0;
            foreach (string file in files)
            {
                string rel = file.Substring(tagRoot.Length).TrimStart('\\');
                rel = rel.Substring(0, rel.Length - ".weapon".Length);
                var sw = Stopwatch.StartNew();
                try
                {
                    using (var tag = new TagFile(TagPath.FromPathAndExtension(rel, "weapon")))
                    {
                        int n = 0;
                        Walk(tag.Fields, f => n++, 0);
                        totalFields += n;
                    }
                    times.Add(sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    failed++;
                    report.Metric("Failed to load " + rel, ex.Message);
                }
            }

            if (times.Count > 0)
            {
                times.Sort();
                report.Metric("Weapon tags loaded and walked", times.Count + " (" + totalFields.ToString("N0") + " fields)");
                report.Metric("Per tag", "median " + times[times.Count / 2] + " ms, max " + times[times.Count - 1] + " ms");
                report.Metric("Total", times.Sum() + " ms");
            }
            report.Check("All sampled weapon tags loaded", failed == 0 && times.Count > 0);
        }

        // ---- Helpers ------------------------------------------------------------------------

        private static void Walk(TagField[] fields, Action<TagField> visit, int depth)
        {
            if (fields == null || depth > MaxDepth) return;
            foreach (TagField f in fields)
            {
                if (f == null) continue;
                visit(f);

                var structField = f as TagFieldStruct;
                if (structField != null) { WalkElements(structField.Elements, visit, depth); continue; }

                var block = f as TagFieldBlock;
                if (block != null) { WalkElements(block.Elements, visit, depth); continue; }

                var array = f as TagFieldArray;
                if (array != null) WalkElements(array.Elements, visit, depth);
            }
        }

        private static void WalkElements(ITagElementCollection elements, Action<TagField> visit, int depth)
        {
            if (elements == null) return;
            int count = Math.Min(elements.Count, MaxElementsPerBlock);
            for (int i = 0; i < count; i++)
            {
                TagElement element = elements[i];
                if (element != null) Walk(element.Fields, visit, depth + 1);
            }
        }

        private static string ValueOf(TagField f)
        {
            try
            {
                var element = f as TagFieldElement;
                if (element != null) return element.GetStringData();

                var array = f as TagFieldElementArray;
                if (array != null) return string.Join(", ", array.GetStringData() ?? new string[0]);

                var enumField = f as TagFieldEnum;
                if (enumField != null) return enumField.Value.ToString();

                var flags = f as TagFieldFlags;
                if (flags != null) return "0x" + flags.RawValue.ToString("X");

                var reference = f as TagFieldReference;
                if (reference != null)
                {
                    TagPath p = reference.Path;
                    return p == null ? "" : p.RelativePathWithExtension;
                }

                var block = f as TagFieldBlock;
                if (block != null) return "[" + block.Elements.Count + " elements]";
            }
            catch (Exception ex)
            {
                return "<error: " + ex.GetType().Name + ">";
            }
            return "";
        }

        private static string Safe(Func<string> get)
        {
            try { return get() ?? ""; }
            catch (Exception ex) { return "<error: " + ex.GetType().Name + ">"; }
        }

        private static void OnCrash(ManagedBlamCrashInfo info)
        {
            string message = "ManagedBlam " + info.Type + ": " + info.Message + " (" + info.File + ":" + info.Line + ")";
            Console.Error.WriteLine(message);
            if (_report != null)
            {
                _report.Fail(message);
                _report.Save();
            }
        }
    }
}
