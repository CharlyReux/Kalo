using System;
using System.IO;
using Microsoft.Win32;

namespace Kalo.Spike0
{
    /// <summary>Read-only checks. Never writes anything except the report.</summary>
    internal static class Env
    {
        public static void Run(Hrek hrek, Options opts, Report report)
        {
            report.Section("Environment");
            report.Metric("Windows", Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? ", 64-bit" : ", 32-bit"));
            report.Metric("This process", Environment.Is64BitProcess ? "64-bit" : "32-bit (ManagedBlam needs 64-bit)");
            report.Metric(".NET Framework", DotNetFrameworkRelease());
            report.Check("Running as a 64-bit process", Environment.Is64BitProcess);

            report.Section("HREK");
            report.Metric("Folder", hrek.Root);
            report.Metric("Build", hrek.BuildTag);
            foreach (string name in new[] { "tags", "data" })
            {
                long files;
                long bytes = DirSize(Path.Combine(hrek.Root, name), out files);
                report.Metric(name + "/", Gb(bytes) + " in " + files.ToString("N0") + " files");
            }

            report.Section("MCC");
            string mcc = hrek.GuessMccRoot();
            bool mccFound = mcc != null && File.Exists(Path.Combine(mcc, "haloreach", "haloreach.dll"));
            report.Metric("Folder", mccFound ? mcc : "not found next to HREK");
            if (mccFound)
            {
                string bt = Path.Combine(mcc, "build_tag.txt");
                if (File.Exists(bt)) report.Metric("Build", File.ReadAllText(bt).Trim().Replace("\r", "").Replace("\n", " / "));
            }

            report.Section("Disk");
            string driveRoot = Path.GetPathRoot(opts.WorkspaceDir);
            try
            {
                var drive = new DriveInfo(driveRoot);
                report.Metric("Free space on " + driveRoot + " (workspace drive)", Gb(drive.AvailableFreeSpace));
            }
            catch (Exception ex)
            {
                report.Metric("Free space on " + driveRoot, "unknown (" + ex.Message + ")");
            }
        }

        public static long DirSize(string dir, out long fileCount)
        {
            fileCount = 0;
            if (!Directory.Exists(dir)) return 0;
            long total = 0;
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(f).Length;
                    fileCount++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return total;
        }

        public static string Gb(long bytes)
        {
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00") + " GB";
        }

        private static string DotNetFrameworkRelease()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    object release = key == null ? null : key.GetValue("Release");
                    if (release == null) return "not found";
                    int r = Convert.ToInt32(release);
                    string name = r >= 533320 ? "4.8.1" : r >= 528040 ? "4.8" : "older than 4.8";
                    return name + " (release " + r + ")";
                }
            }
            catch (Exception ex)
            {
                return "unknown (" + ex.Message + ")";
            }
        }
    }
}
