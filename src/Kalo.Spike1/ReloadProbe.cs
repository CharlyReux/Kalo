using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Bungie;
using Bungie.Game.Tools;
using Bungie.Tags;
using Kalo.Spike0;

namespace Kalo.Spike1
{
    /// <summary>
    /// Does a running reach_tag_test pick up a tag that changes on disk?
    ///   1. Launch the game on a map and wait for "END initialize for new round" in its log
    ///   2. List the ports the game listens on, and try RemoteCommand on each
    ///   3. Save assault_rifle.weapon in the shared workspace with a different magazine size
    ///   4. Watch the game's logs for 45 s and take screenshots
    ///   5. Restore the original tag file (content and timestamp) and close the game
    /// No user input needed: runs unattended through the Kalo runner.
    /// </summary>
    internal static class ReloadProbe
    {
        private const string WeaponPath = @"objects\weapons\rifle\assault_rifle\assault_rifle";
        private const string MagazineField = "Block:magazines[0]/ShortInteger:rounds loaded maximum";
        private const int WatchSeconds = 45;

        public static void Run(Options o, Report report)
        {
            report.Section("Tag hot reload in reach_tag_test");
            string ws = o.WorkspaceDir;
            string exe = Path.Combine(ws, "reach_tag_test.exe");
            bool mapExists = File.Exists(Path.Combine(ws, "tags", o.Map + ".scenario"));
            report.Check("Map exists in the workspace: " + o.Map, mapExists);
            if (!mapExists || !File.Exists(exe)) return;
            Directory.CreateDirectory(o.ResultsDir);

            string tagFile = Path.Combine(ws, "tags", WeaponPath + ".weapon");
            string backupDir = Path.Combine(ws, ".kalo-backup");
            string backup = Path.Combine(backupDir, "assault_rifle.weapon");
            Directory.CreateDirectory(backupDir);
            File.Copy(tagFile, backup, true);
            DateTime originalTime = File.GetLastWriteTimeUtc(tagFile);
            long originalSize = new FileInfo(tagFile).Length;

            string init = Path.Combine(ws, "bonobo_init.txt");
            Process game = null;
            try
            {
                File.WriteAllText(init, "run_game_scripts 1\r\ngame_start " + o.Map + "\r\n");
                DateTime startUtc = DateTime.UtcNow;
                var clock = Stopwatch.StartNew();
                game = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = ws, UseShellExecute = false });
                report.Metric("reach_tag_test started", "pid " + game.Id);

                var log = new GameLog(ws, startUtc);
                bool loaded = Wait.Until(() => log.ReadAll("networking_debug.txt").Contains("END initialize for new round"), game, o.TimeoutSeconds);
                report.Metric("Map loaded", loaded ? "after " + Seconds(clock) : (game.HasExited ? "no, the game exited (code " + game.ExitCode + ")" : "no, gave up after " + o.TimeoutSeconds + " s"));
                report.Metric("Log folder", log.CurrentFolder() ?? "not found");
                if (!loaded)
                {
                    report.Metric("Screenshot", ScreenCapture.Save(game, Path.Combine(o.ResultsDir, "reload-0-not-loaded.png")));
                    report.Check("Game loaded the map", false);
                    return;
                }
                Thread.Sleep(5000);
                report.Metric("Screenshot after load", ScreenCapture.Save(game, Path.Combine(o.ResultsDir, "reload-1-loaded.png")));

                report.Section("Ports the game listens on");
                List<string> ports = Ports.ListeningBy(game.Id);
                report.Metric("Count", ports.Count.ToString());
                foreach (string p in ports) report.Line("    - " + p);

                BlamSession.Start(ws, InitializationType.TagsOnly, report);
                try
                {
                    TryRemoteCommand(ports, report);
                    EditAndWatch(o, log, tagFile, game, report);
                }
                finally
                {
                    BlamSession.Stop(report);
                }
            }
            finally
            {
                report.Section("Clean up");
                try
                {
                    File.Copy(backup, tagFile, true);
                    File.SetLastWriteTimeUtc(tagFile, originalTime);
                    var info = new FileInfo(tagFile);
                    report.Check("Original assault_rifle.weapon restored (content and timestamp)", info.Length == originalSize && info.LastWriteTimeUtc == originalTime);
                }
                catch (Exception ex)
                {
                    report.Fail("Could not restore assault_rifle.weapon: " + ex.Message + ". The original is in " + backup);
                }
                try { if (File.Exists(init)) File.Delete(init); } catch (Exception) { }
                if (game != null && !game.HasExited)
                {
                    try
                    {
                        game.CloseMainWindow();
                        if (!game.WaitForExit(10000)) game.Kill();
                        report.Metric("reach_tag_test", "closed");
                    }
                    catch (Exception ex) { report.Metric("reach_tag_test", "could not be closed: " + ex.Message); }
                }
            }
        }

        private static void TryRemoteCommand(List<string> ports, Report report)
        {
            report.Section("RemoteCommand on the game's ports");
            var names = new List<string> { "127.0.0.1", "localhost" };
            foreach (string p in ports.Where(x => x.StartsWith("TCP ")))
            {
                int port = Ports.PortOf(p);
                if (port <= 0) continue;
                names.Add("127.0.0.1:" + port);
                names.Add("localhost:" + port);
            }
            foreach (string name in names.Distinct())
            {
                var sw = Stopwatch.StartNew();
                bool ok = false;
                string error = null;
                try { ok = RemoteCommand.Connect(name) && RemoteCommand.IsConnected; }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                report.Metric("Connect(\"" + name + "\")", (ok ? "CONNECTED" : error ?? "no") + " (" + sw.ElapsedMilliseconds + " ms)");
                if (ok)
                {
                    try
                    {
                        HSCommandResults r = RemoteCommand.SendHsCommand(name, "(+ 1 2)");
                        report.Metric("(+ 1 2)", r == null ? "null" : "status " + r.Status + ", text \"" + r.StringResult + "\"");
                    }
                    catch (Exception ex) { report.Metric("(+ 1 2) failed", ex.Message); }
                }
                try { RemoteCommand.Disconnect(); } catch (Exception) { }
            }
        }

        private static void EditAndWatch(Options o, GameLog log, string tagFile, Process game, Report report)
        {
            report.Section("Change the tag on disk");
            DateTime before = File.GetLastWriteTimeUtc(tagFile);
            log.Mark();
            using (var tag = new TagFile(TagPath.FromPathAndExtension(WeaponPath, "weapon")))
            {
                var field = tag.SelectField(MagazineField) as TagFieldElementInteger;
                if (field == null) { report.Fail("Magazine field not found"); return; }
                report.Metric("Magazine before", field.Data.ToString());
                field.Data = 77;
                tag.Save();
            }
            report.Check("assault_rifle.weapon saved with magazine 77 (file timestamp changed)", File.GetLastWriteTimeUtc(tagFile) != before);

            Thread.Sleep(WatchSeconds * 1000);
            List<string> lines = log.NewLines();
            report.Metric("New log lines in " + WatchSeconds + " s", lines.Count.ToString());
            var interesting = lines.Where(l =>
                l.IndexOf("assault_rifle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                l.IndexOf("reload", StringComparison.OrdinalIgnoreCase) >= 0 ||
                l.IndexOf("quick_preview", StringComparison.OrdinalIgnoreCase) >= 0 ||
                l.IndexOf("changed", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            foreach (string l in interesting.Take(40)) report.Line("    " + l);
            report.WriteLines("reload-new-log-lines.txt", lines);
            report.Check("The game logged a reaction to the changed tag", interesting.Count > 0);
            report.Metric("Screenshot after the change", ScreenCapture.Save(game, Path.Combine(o.ResultsDir, "reload-2-after-save.png")));
        }

        private static string Seconds(Stopwatch sw)
        {
            return (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s";
        }
    }
}
