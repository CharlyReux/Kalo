using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Kalo.Spike0;

namespace Kalo.Spike1
{
    /// <summary>
    /// kalo-spike1: can Kalo edit maps live?
    ///
    ///   shared   Spike 0 workspace -> shared workspace (instant move) + baseline index of tags/ and data/
    ///   changes  files in the shared workspace that differ from the baseline (what a project overlay would hold)
    ///   forge    load MCC Forge map variants (.mvar), round-trip them, export them to XML
    ///   live     launch reach_tag_test on a map, connect with RemoteCommand, run a script command,
    ///            place a weapon, push tag edits into the running game
    ///   engine   start the engine inside this process (GameEditor), load a map, run frames,
    ///            grab the viewport's render surface and save it as a PNG
    ///   all      shared, then forge, live and engine, each in its own child process
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Options o = Options.Parse(args);
            if (o == null)
            {
                Options.PrintUsage();
                return 2;
            }

            Hrek hrek;
            try
            {
                hrek = Hrek.Locate(o.HrekRoot);
                Guard.Init(hrek.Root);
                Guard.EnsureWritable(o.ResultsDir, "the results folder");
                Guard.EnsureWritable(o.WorkspaceDir, "the shared workspace");
            }
            catch (SpikeAbort ex)
            {
                Console.Error.WriteLine("kalo-spike1: " + ex.Message);
                return 1;
            }

            if (o.Command == "all") return RunAll(o, hrek);

            var report = new Report(o.ResultsDir, "s1-" + o.Command);
            report.Line("# Kalo Spike 1 report: " + o.Command);
            report.Line("");
            report.Line("- Started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.Line("- Command: `kalo-spike1 " + string.Join(" ", args) + "`");
            report.Line("- HREK: `" + hrek.Root + "` (build " + hrek.BuildTag + ")");
            report.Line("- Shared workspace: `" + o.WorkspaceDir + "`");

            try
            {
                switch (o.Command)
                {
                    case "shared":
                        SharedWorkspace.Create(o, report);
                        break;
                    case "changes":
                        SharedWorkspace.Require(o.WorkspaceDir);
                        SharedWorkspace.ListChanges(o.WorkspaceDir, report);
                        break;
                    case "forge":
                        SharedWorkspace.Require(o.WorkspaceDir);
                        WithHrekSafety(hrek, o.WorkspaceDir, report, () => RunForge(o, hrek, report));
                        break;
                    case "live":
                        SharedWorkspace.Require(o.WorkspaceDir);
                        WithHrekSafety(hrek, o.WorkspaceDir, report, () => RunLive(o, report));
                        break;
                    case "engine":
                        SharedWorkspace.Require(o.WorkspaceDir);
                        WithHrekSafety(hrek, o.WorkspaceDir, report, () => RunEngine(o, report));
                        break;
                    case "reload":
                        SharedWorkspace.Require(o.WorkspaceDir);
                        WithHrekSafety(hrek, o.WorkspaceDir, report, () => RunReload(o, report));
                        break;
                }
            }
            catch (SpikeAbort ex)
            {
                report.Fail(ex.Message);
            }
            catch (Exception ex)
            {
                report.Fail("Unexpected error: " + ex);
            }
            finally
            {
                report.Section("Result");
                report.Line(report.Failures == 0 ? "All checks passed." : report.Failures + " check(s) failed.");
                report.Save();
                Console.WriteLine();
                Console.WriteLine("Report written to " + report.MarkdownPath);
            }
            return report.Failures == 0 ? 0 : 1;
        }

        /// <summary>Runs every probe in its own process so a crash in one cannot hide the others.</summary>
        private static int RunAll(Options o, Hrek hrek)
        {
            var steps = new[] { "shared", "forge", "live", "engine" };
            var results = new List<string>();
            string exe = Assembly.GetEntryAssembly().Location;

            foreach (string step in steps)
            {
                Console.WriteLine();
                Console.WriteLine("=============== kalo-spike1 " + step + " ===============");
                var args = new List<string> { step, "--hrek", Quote(o.HrekRoot), "--workspace", Quote(o.WorkspaceDir), "--results", Quote(o.ResultsDir), "--map", Quote(o.Map), "--timeout", o.TimeoutSeconds.ToString() };
                if (o.Yes) args.Add("--yes");

                var sw = Stopwatch.StartNew();
                var psi = new ProcessStartInfo(exe, string.Join(" ", args)) { UseShellExecute = false };
                int code;
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit();
                    code = p.ExitCode;
                }
                string verdict = code == 0 ? "passed" : code == 1 ? "some checks failed" : "crashed or stopped (exit code " + code + ")";
                results.Add("- " + step + ": " + verdict + ", " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s, see spike-report-s1-" + step + ".md");

                if (step == "shared" && code != 0)
                {
                    results.Add("- stopped: the shared workspace is needed by every other step");
                    break;
                }
            }

            var summary = new Report(o.ResultsDir, "s1-all");
            summary.Line("# Kalo Spike 1 summary");
            summary.Line("");
            summary.Line("- Finished: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            summary.Line("- Map: `" + o.Map + "`");
            summary.Line("");
            foreach (string r in results) summary.Line(r);
            summary.Save();
            Console.WriteLine();
            Console.WriteLine("Summary written to " + summary.MarkdownPath);
            return 0;
        }

        private static string Quote(string s)
        {
            return "\"" + s.TrimEnd('\\') + "\"";
        }

        /// <summary>
        /// Snapshots every HREK folder the workspace links to (junctions) plus HREK's root files,
        /// runs the probe, and fails the report if anything in HREK changed.
        /// </summary>
        private static void WithHrekSafety(Hrek hrek, string workspace, Report report, Action action)
        {
            var watched = new List<string>();
            foreach (string dir in Directory.GetDirectories(workspace))
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    watched.Add(Path.Combine(hrek.Root, Path.GetFileName(dir)));
            }

            Snapshot before = Snapshot.Take(hrek.Root, watched);
            try
            {
                action();
            }
            finally
            {
                List<string> changes = Snapshot.Diff(before, Snapshot.Take(hrek.Root, watched));
                report.Section("HREK safety check");
                report.Metric("Files watched in HREK", before.Count.ToString());
                report.Check("No file in the HREK folder was created, changed or deleted", changes.Count == 0);
                foreach (string c in changes) report.Line("    - " + c);

                // What the probe wrote into the shared workspace: the files a project overlay would capture.
                try { SharedWorkspace.ListChanges(workspace, report); }
                catch (Exception ex) { report.Metric("Workspace change scan failed", ex.Message); }
            }
        }

        // The three ManagedBlam probes are kept out of line so BlamLoader.Install runs
        // before the JIT resolves any Bungie.* type.

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunForge(Options o, Hrek hrek, Report report)
        {
            BlamLoader.Install(o.WorkspaceDir);
            ForgeProbe.Run(o, hrek, report);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunLive(Options o, Report report)
        {
            BlamLoader.Install(o.WorkspaceDir);
            LiveProbe.Run(o, report);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunReload(Options o, Report report)
        {
            BlamLoader.Install(o.WorkspaceDir);
            ReloadProbe.Run(o, report);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunEngine(Options o, Report report)
        {
            BlamLoader.Install(o.WorkspaceDir);
            EngineProbe.Run(o, report);
        }
    }

    internal sealed class Options
    {
        public const string DefaultHrek = @"D:\SteamLibrary\steamapps\common\HREK";
        public const string DefaultMap = @"levels\multi\20_sword_slayer\20_sword_slayer";
        private static readonly string[] Commands = { "shared", "changes", "forge", "live", "engine", "reload", "all" };

        public string Command;
        public string HrekRoot;
        public string WorkspaceDir;
        public string SpikeWorkspaceDir;
        public string ResultsDir;
        public string Map = DefaultMap;
        public int TimeoutSeconds = 300;
        public bool Yes;

        public static Options Parse(string[] args)
        {
            if (args.Length == 0) return null;
            var o = new Options { Command = args[0].ToLowerInvariant() };
            if (Array.IndexOf(Commands, o.Command) < 0) return null;

            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                bool hasValue = i + 1 < args.Length;
                if (a == "--yes" || a == "-y") o.Yes = true;
                else if (a == "--hrek" && hasValue) o.HrekRoot = args[++i];
                else if (a == "--workspace" && hasValue) o.WorkspaceDir = args[++i];
                else if (a == "--results" && hasValue) o.ResultsDir = args[++i];
                else if (a == "--map" && hasValue) o.Map = args[++i];
                else if (a == "--timeout" && hasValue) o.TimeoutSeconds = int.Parse(args[++i]);
                else
                {
                    Console.Error.WriteLine("Unknown argument: " + a);
                    return null;
                }
            }

            string cwd = Directory.GetCurrentDirectory();
            if (o.HrekRoot == null) o.HrekRoot = Environment.GetEnvironmentVariable("KALO_HREK") ?? DefaultHrek;
            if (o.WorkspaceDir == null) o.WorkspaceDir = Path.Combine(cwd, ".kalo", "shared");
            if (o.ResultsDir == null) o.ResultsDir = Path.Combine(cwd, "spike-results");
            o.SpikeWorkspaceDir = Path.Combine(cwd, ".kalo", "workspaces", "spike");

            o.HrekRoot = Path.GetFullPath(o.HrekRoot);
            o.WorkspaceDir = Path.GetFullPath(o.WorkspaceDir);
            o.ResultsDir = Path.GetFullPath(o.ResultsDir);
            o.Map = o.Map.Replace('/', '\\');
            if (o.Map.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)) o.Map = o.Map.Substring(0, o.Map.Length - ".scenario".Length);
            return o;
        }

        public static void PrintUsage()
        {
            Console.WriteLine("kalo-spike1 <command> [options]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  shared    Turn the Spike 0 workspace into the shared workspace (no copy) + baseline index");
            Console.WriteLine("  changes   List shared-workspace files that differ from the baseline");
            Console.WriteLine("  forge     Load, round-trip and export MCC Forge map variants");
            Console.WriteLine("  live      Launch reach_tag_test and drive it live (script command, place, tag preview)");
            Console.WriteLine("  engine    Run the engine inside this process and capture a rendered frame");
            Console.WriteLine("  reload    Does a running reach_tag_test reload a tag saved on disk? (unattended)");
            Console.WriteLine("  all       shared, then forge, live and engine in separate processes");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --hrek <dir>        HREK folder (default: KALO_HREK or " + DefaultHrek + ")");
            Console.WriteLine("  --workspace <dir>   Shared workspace (default: .kalo\\shared)");
            Console.WriteLine("  --results <dir>     Where reports go (default: spike-results)");
            Console.WriteLine("  --map <scenario>    Scenario tag path (default: " + DefaultMap + ")");
            Console.WriteLine("  --timeout <s>       Max wait for the game or the engine to load (default 300)");
            Console.WriteLine("  --yes, -y           No questions (skips the in-game confirmation)");
        }
    }
}
