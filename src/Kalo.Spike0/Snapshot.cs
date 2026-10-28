using System;
using System.Collections.Generic;
using System.IO;

namespace Kalo.Spike0
{
    /// <summary>
    /// Size + last-write-time of every file under some folders. Used to prove that a run
    /// left the real HREK install untouched.
    /// </summary>
    internal sealed class Snapshot
    {
        private readonly Dictionary<string, KeyValuePair<long, DateTime>> _files =
            new Dictionary<string, KeyValuePair<long, DateTime>>(StringComparer.OrdinalIgnoreCase);

        public int Count { get { return _files.Count; } }

        /// <summary>HREK's root files (not recursive) plus every file under each given folder.</summary>
        public static Snapshot Take(string root, IEnumerable<string> recursiveDirs)
        {
            var s = new Snapshot();
            foreach (string f in Directory.GetFiles(root)) s.Add(f);
            foreach (string dir in recursiveDirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) s.Add(f);
            }
            return s;
        }

        private void Add(string path)
        {
            try
            {
                var info = new FileInfo(path);
                _files[path] = new KeyValuePair<long, DateTime>(info.Length, info.LastWriteTimeUtc);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static List<string> Diff(Snapshot before, Snapshot after)
        {
            var changes = new List<string>();
            foreach (var kv in after._files)
            {
                KeyValuePair<long, DateTime> old;
                if (!before._files.TryGetValue(kv.Key, out old)) changes.Add("created: " + kv.Key);
                else if (old.Key != kv.Value.Key || old.Value != kv.Value.Value) changes.Add("changed: " + kv.Key);
            }
            foreach (var kv in before._files)
            {
                if (!after._files.ContainsKey(kv.Key)) changes.Add("deleted: " + kv.Key);
            }
            changes.Sort(StringComparer.OrdinalIgnoreCase);
            return changes;
        }
    }
}
