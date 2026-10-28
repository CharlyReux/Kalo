using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Threading;
using Bungie;
using Bungie.Game;
using Bungie.GameEditor;
using Kalo.Spike0;

namespace Kalo.Spike1
{
    /// <summary>
    /// Can the real engine run inside Kalo's own process and give us its picture?
    ///   1. Start ManagedBlam at the GameEditor level (the level HREK's own "World Editor" used)
    ///   2. GameEditor.ChangeMap(map), logging the engine's loading messages
    ///   3. Run frames with RunGameFrame + ViewPort.Render, timing them
    ///   4. Read ViewPort.GetRenderSurface() and save what it holds as PNGs
    /// A watchdog ends the process with a report if the engine hangs.
    /// </summary>
    internal static class EngineProbe
    {
        private const int FramesToRun = 900;

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public static void Run(Options o, Report report)
        {
            report.Section("Engine inside Kalo's process");
            bool mapExists = File.Exists(Path.Combine(o.WorkspaceDir, "tags", o.Map + ".scenario"));
            report.Check("Map exists in the workspace: " + o.Map, mapExists);
            if (!mapExists) return;

            Directory.CreateDirectory(o.ResultsDir);
            var watchdog = new Watchdog(o.TimeoutSeconds + 120, report);
            var progress = new List<string>();
            var clock = Stopwatch.StartNew();

            try
            {
                watchdog.Phase("starting ManagedBlam at GameEditor level");
                try
                {
                    BlamSession.Start(o.WorkspaceDir, InitializationType.GameEditor, report);
                }
                catch (Exception ex)
                {
                    report.Fail("ManagedBlam would not start at GameEditor level: " + ex.GetType().Name + ": " + ex.Message);
                    return;
                }

                try
                {
                    Editor editor = Get(() => ManagedBlamSystem.GameEditor, "GameEditor", report);
                    Get(() => ManagedBlamSystem.Game, "Game instance", report);
                    ViewPort viewport = Get(() => ManagedBlamSystem.GameViewPort, "ViewPort", report);

                    if (editor != null)
                    {
                        editor.PregameProgressReceived += (s, e) =>
                        {
                            string line = (clock.ElapsedMilliseconds / 1000.0).ToString("0.0").PadLeft(6) + " s  " + e.Message;
                            lock (progress) progress.Add(line);
                            Console.WriteLine("  engine: " + e.Message);
                        };

                        watchdog.Phase("ChangeMap(" + o.Map + ")");
                        var sw = Stopwatch.StartNew();
                        try
                        {
                            editor.ChangeMap(o.Map);
                            report.Metric("ChangeMap(\"" + o.Map + "\")", "returned after " + sw.ElapsedMilliseconds + " ms");
                        }
                        catch (Exception ex)
                        {
                            report.Metric("ChangeMap failed", ex.GetType().Name + ": " + ex.Message);
                        }
                    }

                    RunFrames(o, viewport, watchdog, report);

                    // The viewport may only exist once a map is loaded.
                    if (viewport == null) viewport = Get(() => ManagedBlamSystem.GameViewPort, "ViewPort after loading", report);
                    report.Section("Render surface");
                    if (viewport != null)
                    {
                        watchdog.Phase("capturing the render surface");
                        IntPtr surface = viewport.GetRenderSurface();
                        bool saved = SurfaceProbe.Capture(surface, Path.Combine(o.ResultsDir, "engine-frame.png"), report);
                        report.Check("Captured a frame rendered by the engine", saved);
                    }
                    else
                    {
                        report.Check("Captured a frame rendered by the engine (no ViewPort object)", false);
                    }
                }
                finally
                {
                    watchdog.Phase("stopping ManagedBlam");
                    BlamSession.Stop(report);
                }
            }
            catch (Exception ex)
            {
                report.Fail("Engine probe crashed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                watchdog.Disarm();
                lock (progress)
                {
                    report.Section("Engine loading messages");
                    report.Metric("Messages", progress.Count.ToString());
                    foreach (string line in progress.Take(40)) report.Line("    " + line);
                    if (progress.Count > 0) report.WriteLines("engine-progress.log", progress);
                }
            }
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private static void RunFrames(Options o, ViewPort viewport, Watchdog watchdog, Report report)
        {
            report.Section("Frames");
            watchdog.Phase("running frames");
            var times = new List<double>();
            var deadline = DateTime.Now.AddSeconds(o.TimeoutSeconds);
            string error = null;
            int frames = 0;
            while (frames < FramesToRun && DateTime.Now < deadline)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    ManagedBlamSystem.RunGameFrame();
                    if (viewport != null) viewport.Render();
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                    break;
                }
                times.Add(sw.Elapsed.TotalMilliseconds);
                frames++;
                watchdog.Beat();

                if (frames == 120 && viewport != null)
                {
                    try { SurfaceProbe.Capture(viewport.GetRenderSurface(), Path.Combine(o.ResultsDir, "engine-frame-120.png"), report); }
                    catch (Exception ex) { report.Metric("Early capture failed", ex.Message); }
                }
            }

            report.Metric("Frames run", frames.ToString());
            if (error != null) report.Metric("Stopped by", error);
            if (times.Count > 0)
            {
                var steady = times.Skip(Math.Min(60, times.Count / 2)).OrderBy(t => t).ToList();
                if (steady.Count == 0) steady = times.OrderBy(t => t).ToList();
                double median = steady[steady.Count / 2];
                report.Metric("First frame", times[0].ToString("0") + " ms");
                report.Metric("Slowest frame", times.Max().ToString("0") + " ms");
                report.Metric("Median frame after warm-up", median.ToString("0.0") + " ms (about " + (median > 0 ? (1000 / median).ToString("0") : "?") + " fps)");
            }
            report.Check("Engine ran frames inside Kalo's process", frames >= 60 && error == null);
        }

        private static T Get<T>(Func<T> get, string name, Report report) where T : class
        {
            try
            {
                T value = get();
                report.Metric(name, value == null ? "null" : "available");
                return value;
            }
            catch (Exception ex)
            {
                report.Metric(name, "error: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>Ends the process with a saved report if the engine stops making progress.</summary>
        private sealed class Watchdog
        {
            private readonly Report _report;
            private readonly int _limitSeconds;
            private readonly Thread _thread;
            private volatile string _phase = "starting";
            private long _lastBeat = Environment.TickCount;
            private volatile bool _disarmed;

            public Watchdog(int limitSeconds, Report report)
            {
                _report = report;
                _limitSeconds = limitSeconds;
                _thread = new Thread(Loop) { IsBackground = true, Name = "kalo-watchdog" };
                _thread.Start();
            }

            public void Phase(string phase) { _phase = phase; Beat(); }
            public void Beat() { Interlocked.Exchange(ref _lastBeat, Environment.TickCount); }
            public void Disarm() { _disarmed = true; }

            private void Loop()
            {
                while (!_disarmed)
                {
                    Thread.Sleep(1000);
                    long idle = (Environment.TickCount - Interlocked.Read(ref _lastBeat)) / 1000;
                    if (!_disarmed && idle > _limitSeconds)
                    {
                        _report.Fail("The engine made no progress for " + idle + " s during: " + _phase + ". Kalo stopped the probe.");
                        _report.Save();
                        Environment.Exit(3);
                    }
                }
            }
        }
    }
}
