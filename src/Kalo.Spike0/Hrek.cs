using System;
using System.Collections.Generic;
using System.IO;

namespace Kalo.Spike0
{
    internal sealed class Hrek
    {
        public string Root;
        public string BuildTag;

        public static Hrek Locate(string root)
        {
            if (!Directory.Exists(root))
                throw new SpikeAbort("HREK folder not found: " + root + " (pass --hrek <folder>)");

            string[] required = { "project.root", "tool.exe", @"bin\ManagedBlam.dll" };
            foreach (string rel in required)
            {
                if (!File.Exists(Path.Combine(root, rel)))
                    throw new SpikeAbort("This does not look like HREK, missing " + rel + " in " + root);
            }
            if (!Directory.Exists(Path.Combine(root, "tags")))
                throw new SpikeAbort("HREK has no tags folder yet. Run 'HREK (Extract).bat' in " + root + " first.");

            var h = new Hrek { Root = root.TrimEnd('\\', '/'), BuildTag = "unknown" };
            string buildTag = Path.Combine(root, "build_tag.txt");
            if (File.Exists(buildTag))
            {
                string[] lines = File.ReadAllLines(buildTag);
                if (lines.Length > 0) h.BuildTag = lines[0].Trim();
            }
            return h;
        }

        /// <summary>The MCC install that sits next to HREK in the same Steam library, if any.</summary>
        public string GuessMccRoot()
        {
            string parent = Path.GetDirectoryName(Root);
            return parent == null ? null : Path.Combine(parent, "Halo The Master Chief Collection");
        }
    }

    /// <summary>
    /// Refuses any write inside the HREK or MCC installs. Every path Kalo writes to goes through here.
    /// </summary>
    internal static class Guard
    {
        private static readonly List<string> Protected = new List<string>();

        public static void Init(string hrekRoot)
        {
            Protected.Clear();
            Protected.Add(Normalize(hrekRoot));
            string parent = Path.GetDirectoryName(Normalize(hrekRoot));
            if (parent != null) Protected.Add(Path.Combine(parent, "Halo The Master Chief Collection"));
        }

        public static void EnsureWritable(string path, string what)
        {
            foreach (string root in Protected)
            {
                if (IsUnder(path, root))
                    throw new SpikeAbort("Refusing to write " + what + " inside a protected install: " + path);
            }
        }

        public static bool IsUnder(string path, string root)
        {
            string p = Normalize(path);
            string r = Normalize(root);
            return string.Equals(p, r, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/');
        }
    }
}
