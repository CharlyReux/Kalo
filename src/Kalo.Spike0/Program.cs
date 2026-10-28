using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace Kalo.Spike0
{
    /// <summary>
    /// kalo-spike: Spike 0 for Kalo.
    ///
    ///   env        read-only checks: HREK, MCC, .NET, folder sizes, free disk space
    ///   workspace  build a Kalo workspace (copies tags/ and data/, links the HREK programs)
    ///   tags       start ManagedBlam on the workspace, dump, edit and save a weapon tag
    ///   tool       run the workspace's tool.exe and capture its output
    ///   all        everything above, in order
    ///
    /// Nothing is ever written inside the HREK or MCC folders; the program refuses to.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Options opts = Options.Parse(args);
            if (opts == null)
            {
                Options.PrintUsage();
                return 2;
            }

            Hrek hrek;
            try
            {
                hrek = Hrek.Locate(opts.HrekRoot);
                Guard.Init(hrek.Root);
                Guard.EnsureWritable(opts.ResultsDir, "the results folder");
                Guard.EnsureWritable(opts.WorkspaceDir, "the workspace");
            }
            catch (SpikeAbort ex)
            {
                Console.Error.WriteLine("kalo-spike: " + ex.Message);
                return 1;
            }

            var report = new Report(opts.ResultsDir, opts.Command);
            report.Line("# Kalo Spike 0 report: " + opts.Command);
            report.Line("");
            report.Line("- Started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.Line("- Command: `kalo-spike " + string.Join(" ", args) + "`");
            report.Line("- HREK: `" + hrek.Root + "` (build " + hrek.BuildTag + ")");
            report.Line("- Workspace: `" + opts.WorkspaceDir + "`");

            int code = 0;
            try
            {
                switch (opts.Command)
                {
                    case "env":
                        Env.Run(hrek, opts, report);
                        break;
                    case "workspace":
                        Workspace.Create(hrek, opts.WorkspaceDir, opts.Yes, report);
                        break;
                    case "tags":
                        Workspace.Require(opts.WorkspaceDir);
                        WithHrekSnapshot(hrek, report, () => RunTags(opts.WorkspaceDir, hrek.Root, report));
                        break;
                    case "tool":
                        Workspace.Require(opts.WorkspaceDir);
                        WithHrekSnapshot(hrek, report, () => ToolProbe.Run(opts.WorkspaceDir, report));
                        break;
                    case "all":
                        Env.Run(hrek, opts, report);
                        Workspace.Create(hrek, opts.WorkspaceDir, opts.Yes, report);
                        WithHrekSnapshot(hrek, report, () =>
                        {
                            RunTags(opts.WorkspaceDir, hrek.Root, report);
                            ToolProbe.Run(opts.WorkspaceDir, report);
                        });
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

            if (report.Failures > 0) code = 1;
            return code;
        }

        /// <summary>
        /// Takes a snapshot of the HREK folders the workspace links to (and HREK's root files),
        /// runs the action, then reports any file in HREK that was created, changed or deleted.
        /// </summary>
        private static void WithHrekSnapshot(Hrek hrek, Report report, Action action)
        {
            var watched = new List<string>();
            foreach (string dir in Directory.GetDirectories(hrek.Root))
            {
                string name = Path.GetFileName(dir);
                if (Workspace.Mode(name) == LinkMode.Junction) watched.Add(dir);
            }

            Snapshot before = Snapshot.Take(hrek.Root, watched);
            try
            {
                action();
            }
            finally
            {
                Snapshot after = Snapshot.Take(hrek.Root, watched);
                List<string> changes = Snapshot.Diff(before, after);
                report.Section("HREK safety check");
                report.Metric("Files watched in HREK", before.Count.ToString());
                report.Check("No file in the HREK folder was created, changed or deleted", changes.Count == 0);
                foreach (string c in changes) report.Line("    - " + c);
            }
        }

        // Kept separate and not inlined so that BlamLoader.Install runs before the JIT
        // touches any ManagedBlam type (BlamProbe is only compiled when first called).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunTags(string workspace, string hrekRoot, Report report)
        {
            BlamLoader.Install(workspace);
            BlamProbe.Run(workspace, hrekRoot, report);
        }
    }

    internal sealed class Options
    {
        public const string DefaultHrek = @"D:\SteamLibrary\steamapps\common\HREK";
        private static readonly string[] Commands = { "env", "workspace", "tags", "tool", "all" };

        public string Command;
        public string HrekRoot;
        public string WorkspaceDir;
        public string ResultsDir;
        public bool Yes;

        public static Options Parse(string[] args)
        {
            if (args.Length == 0) return null;
            var o = new Options { Command = args[0].ToLowerInvariant() };
            if (Array.IndexOf(Commands, o.Command) < 0) return null;

            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--yes" || a == "-y") o.Yes = true;
                else if (a == "--hrek" && i + 1 < args.Length) o.HrekRoot = args[++i];
                else if (a == "--workspace" && i + 1 < args.Length) o.WorkspaceDir = args[++i];
                else if (a == "--results" && i + 1 < args.Length) o.ResultsDir = args[++i];
                else
                {
                    Console.Error.WriteLine("Unknown argument: " + a);
                    return null;
                }
            }

            string cwd = Directory.GetCurrentDirectory();
            if (o.HrekRoot == null) o.HrekRoot = Environment.GetEnvironmentVariable("KALO_HREK") ?? DefaultHrek;
            if (o.WorkspaceDir == null) o.WorkspaceDir = Path.Combine(cwd, ".kalo", "workspaces", "spike");
            if (o.ResultsDir == null) o.ResultsDir = Path.Combine(cwd, "spike-results");

            // Absolute paths now: the tags probe changes the current directory later.
            o.HrekRoot = Path.GetFullPath(o.HrekRoot);
            o.WorkspaceDir = Path.GetFullPath(o.WorkspaceDir);
            o.ResultsDir = Path.GetFullPath(o.ResultsDir);
            return o;
        }

        public static void PrintUsage()
        {
            Console.WriteLine("kalo-spike <command> [options]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  env         Read-only checks: HREK, MCC, .NET, folder sizes, free space");
            Console.WriteLine("  workspace   Create the Kalo workspace (copies tags/ and data/, links the rest)");
            Console.WriteLine("  tags        ManagedBlam test: load, dump, edit and save a weapon tag in the workspace");
            Console.WriteLine("  tool        Job Runner test: run the workspace's tool.exe and capture its output");
            Console.WriteLine("  all         All of the above, in order");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --hrek <dir>        HREK folder (default: KALO_HREK or " + DefaultHrek + ")");
            Console.WriteLine("  --workspace <dir>   Workspace folder (default: .kalo\\workspaces\\spike)");
            Console.WriteLine("  --results <dir>     Where reports go (default: spike-results)");
            Console.WriteLine("  --yes, -y           Do not ask before copying files");
        }
    }
}
