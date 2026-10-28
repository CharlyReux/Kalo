using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Kalo.Spike0
{
    /// <summary>
    /// Collects results as Markdown (spike-results/spike-report-&lt;command&gt;.md) and echoes them to the console.
    /// Extra data files (field dumps, tool output) go next to it.
    /// </summary>
    internal sealed class Report
    {
        private readonly StringBuilder _md = new StringBuilder();

        public readonly string Dir;
        public readonly string MarkdownPath;
        public int Failures;

        public Report(string dir, string command)
        {
            Dir = dir;
            Directory.CreateDirectory(dir);
            MarkdownPath = Path.Combine(dir, "spike-report-" + command + ".md");
        }

        public void Line(string text)
        {
            _md.AppendLine(text);
            Console.WriteLine(text);
        }

        public void Section(string title)
        {
            Line("");
            Line("## " + title);
            Line("");
        }

        public void Metric(string name, string value)
        {
            Line("- " + name + ": " + value);
        }

        public void Check(string what, bool ok)
        {
            Line("- [" + (ok ? "x" : " ") + "] " + what);
            if (!ok) Failures++;
        }

        public void Fail(string message)
        {
            Failures++;
            Line("");
            Line("**FAILED:** " + message);
        }

        public string WriteTsv(string fileName, string[] header, IEnumerable<string[]> rows)
        {
            string path = Path.Combine(Dir, fileName);
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.WriteLine(string.Join("\t", header));
                foreach (string[] row in rows)
                {
                    var cells = new string[row.Length];
                    for (int i = 0; i < row.Length; i++) cells[i] = Clean(row[i]);
                    w.WriteLine(string.Join("\t", cells));
                }
            }
            return path;
        }

        public string WriteLines(string fileName, IEnumerable<string> lines)
        {
            string path = Path.Combine(Dir, fileName);
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            return path;
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(MarkdownPath, _md.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not save the report: " + ex.Message);
            }
        }

        private static string Clean(string s)
        {
            if (s == null) return "";
            return s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
