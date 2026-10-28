using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Kalo.Spike0
{
    /// <summary>
    /// Job Runner probe: runs the workspace's tool.exe the way Kalo's Job Runner will
    /// (no window, output streamed line by line with timestamps, timeout, exit code).
    /// With no arguments tool.exe only prints its list of commands, so nothing is built or changed.
    /// </summary>
    internal static class ToolProbe
    {
        private const int TimeoutMs = 60000;

        public static void Run(string workspaceRoot, Report report)
        {
            report.Section("tool.exe");
            string exe = Path.Combine(workspaceRoot, "tool.exe");
            if (!File.Exists(exe))
            {
                report.Fail("tool.exe not found in the workspace: " + exe);
                return;
            }

            var lines = new List<string>();
            long firstOutputMs = -1;
            var sw = Stopwatch.StartNew();

            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = workspaceRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };

            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => Add(lines, "out", e.Data, sw, ref firstOutputMs);
                p.ErrorDataReceived += (s, e) => Add(lines, "err", e.Data, sw, ref firstOutputMs);

                p.Start();
                p.StandardInput.Close(); // never wait for keyboard input
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                bool exited = p.WaitForExit(TimeoutMs);
                if (!exited)
                {
                    try { p.Kill(); } catch (InvalidOperationException) { }
                    report.Fail("tool.exe did not exit within " + TimeoutMs / 1000 + " s and was stopped.");
                }
                else
                {
                    p.WaitForExit(); // flush the async readers
                }

                report.Metric("Command", "tool.exe (no arguments)");
                report.Metric("Exit code", exited ? p.ExitCode.ToString() : "killed");
                report.Metric("Run time", sw.ElapsedMilliseconds + " ms");
                report.Metric("Time to first output", firstOutputMs < 0 ? "no output" : firstOutputMs + " ms");
                lock (lines) report.Metric("Output lines", lines.Count.ToString());

                string log;
                lock (lines) log = report.WriteLines("tool-usage.log", lines);
                report.Metric("Full output", Path.GetFileName(log));
                report.Check("tool.exe ran and its output was captured", exited && lines.Count > 0);
            }
        }

        private static void Add(List<string> lines, string stream, string text, Stopwatch sw, ref long firstOutputMs)
        {
            if (text == null) return;
            lock (lines)
            {
                if (firstOutputMs < 0) firstOutputMs = sw.ElapsedMilliseconds;
                lines.Add(sw.ElapsedMilliseconds.ToString().PadLeft(7) + " " + stream + " " + text);
            }
        }
    }
}
