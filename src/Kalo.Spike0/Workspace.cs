using System;
using System.Diagnostics;
using System.IO;

namespace Kalo.Spike0
{
    internal enum LinkMode
    {
        /// <summary>NTFS junction to the HREK folder (programs and read-only data).</summary>
        Junction,
        /// <summary>Full copy: the project's own, editable content.</summary>
        Copy,
        /// <summary>Fresh empty folder (logs and reports the tools write).</summary>
        Empty
    }

    /// <summary>
    /// A Kalo workspace is an HREK-shaped folder the tools can treat as their root:
    ///   - tags/ data/ maps/ prefs/ user/  are copied (editable, per project)
    ///   - reports/                        starts empty (tools write logs here)
    ///   - every other folder (bin, fmod, bat, data_xx...) is an NTFS junction to HREK
    ///   - root files (tool.exe, sapien.exe, project.xml...) are copied, except HREK.7z
    /// </summary>
    internal static class Workspace
    {
        public const string MarkerFile = ".kalo-workspace";
        private static readonly string[] CopyDirs = { "tags", "data", "maps", "prefs", "user" };
        private static readonly string[] EmptyDirs = { "reports" };
        private static readonly string[] SkipRootFiles = { "HREK.7z" };
        private const long MaxRootFileBytes = 1L << 30; // skip anything over 1 GB at the root (archives)

        public static LinkMode Mode(string folderName)
        {
            foreach (string d in CopyDirs) if (string.Equals(d, folderName, StringComparison.OrdinalIgnoreCase)) return LinkMode.Copy;
            foreach (string d in EmptyDirs) if (string.Equals(d, folderName, StringComparison.OrdinalIgnoreCase)) return LinkMode.Empty;
            return LinkMode.Junction;
        }

        public static void Require(string ws)
        {
            if (!File.Exists(Path.Combine(ws, MarkerFile)))
                throw new SpikeAbort("No Kalo workspace at " + ws + ". Run `kalo-spike workspace` first.");
        }

        public static void Create(Hrek hrek, string ws, bool yes, Report report)
        {
            report.Section("Workspace");
            Guard.EnsureWritable(ws, "the workspace");

            if (File.Exists(Path.Combine(ws, MarkerFile)))
            {
                report.Metric("Status", "already exists, reusing it");
                return;
            }
            if (Directory.Exists(ws) && Directory.GetFileSystemEntries(ws).Length > 0)
                throw new SpikeAbort("The workspace folder exists, is not empty and is not a finished Kalo workspace " +
                    "(maybe an interrupted copy): " + ws + ". Delete that folder and run again.");

            // 1. Estimate what will be copied and check free space.
            long bytes = 0, files = 0;
            foreach (string name in CopyDirs)
            {
                long n;
                bytes += Env.DirSize(Path.Combine(hrek.Root, name), out n);
                files += n;
            }
            foreach (string f in Directory.GetFiles(hrek.Root))
            {
                if (ShouldCopyRootFile(f)) { bytes += new FileInfo(f).Length; files++; }
            }

            long free = new DriveInfo(Path.GetPathRoot(ws)).AvailableFreeSpace;
            report.Metric("To copy", Env.Gb(bytes) + " in " + files.ToString("N0") + " files");
            report.Metric("Free space", Env.Gb(free));
            if (bytes + (2L << 30) > free)
                throw new SpikeAbort("Not enough free space for the workspace (need " + Env.Gb(bytes) + " + 2 GB margin).");

            if (!yes)
            {
                Console.WriteLine();
                Console.Write("Copy " + Env.Gb(bytes) + " into " + ws + "? Nothing in HREK is modified. [y/N] ");
                string answer = Console.ReadLine();
                if (answer == null || answer.Trim().ToLowerInvariant() != "y")
                    throw new SpikeAbort("Workspace creation cancelled.");
            }

            // 2. Build it.
            Directory.CreateDirectory(ws);
            var total = Stopwatch.StartNew();

            foreach (string f in Directory.GetFiles(hrek.Root))
            {
                if (ShouldCopyRootFile(f)) File.Copy(f, Path.Combine(ws, Path.GetFileName(f)));
            }

            foreach (string dir in Directory.GetDirectories(hrek.Root))
            {
                string name = Path.GetFileName(dir);
                string target = Path.Combine(ws, name);
                var sw = Stopwatch.StartNew();
                switch (Mode(name))
                {
                    case LinkMode.Copy:
                        Robocopy(dir, target);
                        report.Metric("Copied " + name + "/", Seconds(sw));
                        break;
                    case LinkMode.Empty:
                        Directory.CreateDirectory(target);
                        report.Metric("Created empty " + name + "/", "ok");
                        break;
                    default:
                        Junction(target, dir);
                        report.Metric("Linked " + name + "/", "junction to HREK");
                        break;
                }
            }

            File.WriteAllText(Path.Combine(ws, MarkerFile),
                "Kalo workspace\r\nsource=" + hrek.Root + "\r\nbuild=" + hrek.BuildTag + "\r\ncreated=" + DateTime.Now.ToString("s") + "\r\n");
            report.Metric("Total time", Seconds(total));
            report.Check("Workspace created", true);
        }

        private static bool ShouldCopyRootFile(string path)
        {
            string name = Path.GetFileName(path);
            foreach (string skip in SkipRootFiles)
                if (string.Equals(skip, name, StringComparison.OrdinalIgnoreCase)) return false;
            return new FileInfo(path).Length <= MaxRootFileBytes;
        }

        private static void Robocopy(string source, string target)
        {
            // /E all subfolders, /MT:16 parallel copy, quiet per-file output, keep the summary.
            int code = RunProcess("robocopy", Quote(source) + " " + Quote(target) + " /E /MT:16 /R:1 /W:1 /NFL /NDL /NP /NJH");
            if (code >= 8) throw new SpikeAbort("robocopy failed copying " + source + " (exit code " + code + ")");
        }

        private static void Junction(string link, string target)
        {
            int code = RunProcess("cmd.exe", "/c mklink /J " + Quote(link) + " " + Quote(target));
            if (code != 0 || !Directory.Exists(link))
                throw new SpikeAbort("Could not create junction " + link + " -> " + target + " (exit code " + code + ")");
        }

        private static int RunProcess(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false };
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        private static string Quote(string path)
        {
            return "\"" + path.TrimEnd('\\') + "\"";
        }

        private static string Seconds(Stopwatch sw)
        {
            return (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s";
        }
    }
}
