using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Kalo.Spike0;

namespace Kalo.Spike1
{
    /// <summary>
    /// The shared workspace: one HREK-shaped folder used by every project.
    /// A baseline index (size + last write time of every file under tags/ and data/) records the
    /// vanilla state, so Kalo can tell which files a project changed. Those files are the project's
    /// overlay; swapping overlays in and out is how projects share one 40 GB workspace.
    /// </summary>
    internal static class SharedWorkspace
    {
        public const string SharedMarker = ".kalo-shared";
        public const string WorkspaceMarker = ".kalo-workspace"; // written by Spike 0
        public const string BaselineFile = ".kalo-baseline.tsv";
        private const string Spike0TestTag = @"tags\objects\weapons\rifle\assault_rifle\assault_rifle_kalo_spike.weapon";
        private static readonly string[] TrackedDirs = { "tags", "data" };

        public static void Require(string ws)
        {
            if (!File.Exists(Path.Combine(ws, SharedMarker)) || !File.Exists(Path.Combine(ws, BaselineFile)))
                throw new SpikeAbort("No shared workspace at " + ws + ". Run `kalo-spike1 shared` first.");
        }

        public static void Create(Options o, Report report)
        {
            report.Section("Shared workspace");
            string ws = o.WorkspaceDir;

            if (File.Exists(Path.Combine(ws, SharedMarker)))
            {
                report.Metric("Status", "already exists");
            }
            else if (File.Exists(Path.Combine(ws, WorkspaceMarker)))
            {
                report.Metric("Status", "adopting an existing Kalo workspace at this path");
                File.WriteAllText(Path.Combine(ws, SharedMarker), "Kalo shared workspace\r\ncreated=" + DateTime.Now.ToString("s") + "\r\n");
            }
            else
            {
                if (Directory.Exists(ws) && Directory.GetFileSystemEntries(ws).Length > 0)
                    throw new SpikeAbort("The shared workspace folder exists, is not empty and is not a Kalo workspace: " + ws);

                string spike = o.SpikeWorkspaceDir;
                if (!File.Exists(Path.Combine(spike, WorkspaceMarker)))
                    throw new SpikeAbort("No Spike 0 workspace to convert at " + spike + ". Create one with: " +
                        "dotnet run --project src\\Kalo.Spike0 -- workspace --workspace \"" + ws + "\" and run this again.");

                Guard.EnsureWritable(spike, "the Spike 0 workspace");
                string testTag = Path.Combine(spike, Spike0TestTag);
                if (File.Exists(testTag))
                {
                    File.Delete(testTag);
                    report.Metric("Removed Spike 0 test tag", Spike0TestTag);
                }

                // Same drive: a rename, not a copy.
                var sw = Stopwatch.StartNew();
                Directory.CreateDirectory(Path.GetDirectoryName(ws));
                if (Directory.Exists(ws)) Directory.Delete(ws); // empty folder
                Directory.Move(spike, ws);
                report.Metric("Moved Spike 0 workspace", sw.ElapsedMilliseconds + " ms");
                File.WriteAllText(Path.Combine(ws, SharedMarker), "Kalo shared workspace\r\ncreated=" + DateTime.Now.ToString("s") + "\r\n");
            }

            string baseline = Path.Combine(ws, BaselineFile);
            if (File.Exists(baseline))
            {
                report.Metric("Baseline", "already recorded");
            }
            else
            {
                var sw = Stopwatch.StartNew();
                int count = 0;
                using (var w = new StreamWriter(baseline, false, new UTF8Encoding(false)))
                {
                    foreach (KeyValuePair<string, FileStamp> kv in Scan(ws))
                    {
                        w.WriteLine(kv.Key + "\t" + kv.Value.Size + "\t" + kv.Value.Ticks);
                        count++;
                    }
                }
                report.Metric("Baseline index", count.ToString("N0") + " files in " + sw.ElapsedMilliseconds + " ms");
            }

            report.Check("Shared workspace ready", File.Exists(Path.Combine(ws, SharedMarker)) && File.Exists(baseline));
        }

        /// <summary>Lists files under tags/ and data/ that differ from the baseline.</summary>
        public static void ListChanges(string ws, Report report)
        {
            report.Section("Workspace changes since baseline");
            var sw = Stopwatch.StartNew();

            var baseline = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadLines(Path.Combine(ws, BaselineFile)))
            {
                string[] p = line.Split('\t');
                if (p.Length == 3) baseline[p[0]] = new FileStamp(long.Parse(p[1]), long.Parse(p[2]));
            }
            long loadMs = sw.ElapsedMilliseconds;

            var changes = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, FileStamp> kv in Scan(ws))
            {
                seen.Add(kv.Key);
                FileStamp old;
                if (!baseline.TryGetValue(kv.Key, out old)) changes.Add("new      " + kv.Key);
                else if (old.Size != kv.Value.Size || old.Ticks != kv.Value.Ticks) changes.Add("changed  " + kv.Key);
            }
            foreach (string key in baseline.Keys)
            {
                if (!seen.Contains(key)) changes.Add("deleted  " + key);
            }
            changes.Sort(StringComparer.OrdinalIgnoreCase);

            report.Metric("Scan time", sw.ElapsedMilliseconds + " ms (baseline load " + loadMs + " ms, " + baseline.Count.ToString("N0") + " files)");
            report.Metric("Files that differ", changes.Count.ToString());
            int shown = 0;
            foreach (string c in changes)
            {
                if (shown++ >= 25) { report.Line("    - ... (" + (changes.Count - 25) + " more in workspace-changes.txt)"); break; }
                report.Line("    - " + c);
            }
            report.WriteLines("workspace-changes.txt", changes);
        }

        private static IEnumerable<KeyValuePair<string, FileStamp>> Scan(string ws)
        {
            foreach (string dir in TrackedDirs)
            {
                var root = new DirectoryInfo(Path.Combine(ws, dir));
                if (!root.Exists) continue;
                foreach (FileInfo f in root.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    string rel = f.FullName.Substring(ws.Length).TrimStart('\\');
                    yield return new KeyValuePair<string, FileStamp>(rel, new FileStamp(f.Length, f.LastWriteTimeUtc.Ticks));
                }
            }
        }

        internal struct FileStamp
        {
            public readonly long Size;
            public readonly long Ticks;
            public FileStamp(long size, long ticks) { Size = size; Ticks = ticks; }
        }
    }
}
