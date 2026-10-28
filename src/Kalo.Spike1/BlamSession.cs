using System;
using System.Diagnostics;
using Bungie;
using Kalo.Spike0;

namespace Kalo.Spike1
{
    /// <summary>Starts and stops ManagedBlam at a given level, reporting crashes and timings.</summary>
    internal static class BlamSession
    {
        private static Report _report;

        public static void Start(string workspace, InitializationType level, Report report)
        {
            _report = report;
            var sw = Stopwatch.StartNew();
            var parameters = new ManagedBlamStartupParameters();
            parameters.InitializationLevel = level;
            ManagedBlamSystem.Start(workspace, new ManagedBlamCrashCallback(OnCrash), parameters);
            report.Metric("ManagedBlam start (" + level + ")", sw.ElapsedMilliseconds + " ms");
            report.Metric("Initialized to " + level, ManagedBlamSystem.IsInitializedToLevel(level).ToString());

            string tagRoot = ManagedBlamSystem.TagRootPath;
            if (!Guard.IsUnder(tagRoot, workspace))
                throw new SpikeAbort("ManagedBlam's tag root (" + tagRoot + ") is outside the shared workspace. Stopping.");
        }

        public static void Stop(Report report)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                ManagedBlamSystem.Stop();
                report.Metric("ManagedBlam stop", sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex)
            {
                report.Metric("ManagedBlam stop failed", ex.Message);
            }
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
