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
    /// Live link: launch reach_tag_test on a map (the way Foundation's "Launch Scenario" does, through
    /// bonobo_init.txt) and drive the running game with ManagedBlam's RemoteCommand:
    ///   - run a script command and read the answer
    ///   - place a weapon in the world
    ///   - push an edited weapon tag into the game without saving it (quick preview)
    ///   - push single field changes, the way a slider in Kalo would (individual field preview)
    /// Tags are never saved; the edits only exist in the running game.
    /// </summary>
    internal static class LiveProbe
    {
        private const string WeaponPath = @"objects\weapons\rifle\assault_rifle\assault_rifle";
        private const string MagazineField = "Block:magazines[0]/ShortInteger:rounds loaded maximum";
        private const int PreviewTimeoutMs = 15000;

        public static void Run(Options o, Report report)
        {
            report.Section("Live link to reach_tag_test");
            string ws = o.WorkspaceDir;
            string exe = Path.Combine(ws, "reach_tag_test.exe");
            if (!File.Exists(exe))
            {
                report.Fail("reach_tag_test.exe not found in the shared workspace.");
                return;
            }
            bool mapExists = File.Exists(Path.Combine(ws, "tags", o.Map + ".scenario"));
            report.Check("Map exists in the workspace: " + o.Map, mapExists);
            if (!mapExists) return;

            BlamSession.Start(ws, InitializationType.TagsOnly, report);
            string init = Path.Combine(ws, "bonobo_init.txt");
            Process game = null;
            try
            {
                File.WriteAllText(init, "run_game_scripts 1\r\ngame_start " + o.Map + "\r\n");
                report.Metric("bonobo_init.txt", "`game_start " + o.Map + "`");

                var clock = Stopwatch.StartNew();
                game = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = ws, UseShellExecute = false });
                report.Metric("reach_tag_test started", "pid " + game.Id);
                report.Metric("Max script expression length", Safe(() => RemoteCommand.GetMaximumHsExpressionLength().ToString()));

                string client = Connect(game, o.TimeoutSeconds, clock, report);
                if (client == null) return;

                if (!WaitForScripts(client, game, o.TimeoutSeconds, clock, report)) return;

                PlaceWeapon(client, report);
                QuickPreview(client, report);
                FieldPreview(client, report);

                if (!o.Yes) AskUser(report);
            }
            finally
            {
                try { if (RemoteCommand.IsConnected) RemoteCommand.Disconnect(); } catch (Exception) { }
                BlamSession.Stop(report);
                try { if (File.Exists(init)) File.Delete(init); } catch (Exception) { }
                if (game != null && !game.HasExited) CloseGame(game, o.Yes, report);
            }
        }

        // ---- Connection -------------------------------------------------------------------

        private static string Connect(Process game, int timeoutSeconds, Stopwatch clock, Report report)
        {
            report.Section("Connect");
            var candidates = new List<string> { "localhost", "127.0.0.1", Environment.MachineName, "" };
            var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
            bool firstRound = true;

            while (DateTime.Now < deadline)
            {
                if (game.HasExited)
                {
                    report.Fail("reach_tag_test exited (code " + game.ExitCode + ") before Kalo could connect.");
                    return null;
                }
                foreach (string name in candidates)
                {
                    var sw = Stopwatch.StartNew();
                    bool ok = false;
                    string error = null;
                    try { ok = RemoteCommand.Connect(name) && RemoteCommand.IsConnected; }
                    catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }

                    if (firstRound)
                        report.Metric("Connect(\"" + name + "\")", (ok ? "connected" : error ?? "no") + " (" + sw.ElapsedMilliseconds + " ms)");
                    if (ok)
                    {
                        report.Metric("Connected as", "\"" + name + "\" after " + Seconds(clock));
                        report.Check("RemoteCommand connected to the running game", true);
                        return name;
                    }
                    try { RemoteCommand.Disconnect(); } catch (Exception) { }
                }
                firstRound = false;
                Thread.Sleep(3000);
            }
            report.Check("RemoteCommand connected to the running game (gave up after " + timeoutSeconds + " s)", false);
            return null;
        }

        /// <summary>Scripts only answer once the map is loaded: poll a trivial expression until it does.</summary>
        private static bool WaitForScripts(string client, Process game, int timeoutSeconds, Stopwatch clock, Report report)
        {
            report.Section("Script commands");
            var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
            string last = null;
            while (DateTime.Now < deadline && !game.HasExited)
            {
                try
                {
                    RemoteCommand.Update();
                    var sw = Stopwatch.StartNew();
                    HSCommandResults r = RemoteCommand.SendHsCommand(client, "(+ 1 2)");
                    string text = r == null ? "null" : "status " + r.Status + ", result " + r.Result + ", text \"" + r.StringResult + "\"";
                    if (text != last)
                    {
                        report.Metric("(+ 1 2) at " + Seconds(clock), text + " (" + sw.ElapsedMilliseconds + " ms)");
                        last = text;
                    }
                    if (r != null && (r.StringResult ?? "").Trim().StartsWith("3"))
                    {
                        report.Check("Script command answered correctly", true);
                        bool printed = RemoteCommand.SendHsExpression(client, "(print \"Kalo live link OK\")");
                        report.Metric("print to the game console", printed ? "sent" : "failed");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    string text = ex.GetType().Name + ": " + ex.Message;
                    if (text != last) { report.Metric("Script command error at " + Seconds(clock), text); last = text; }
                }
                Thread.Sleep(3000);
            }
            report.Check("Script command answered correctly", false);
            return false;
        }

        // ---- Live edits -------------------------------------------------------------------

        private static void PlaceWeapon(string client, Report report)
        {
            report.Section("Place an object");
            try
            {
                var sw = Stopwatch.StartNew();
                bool ok = RemoteCommand.SendPlaceTag(client, TagPath.FromPathAndExtension(WeaponPath, "weapon"));
                report.Metric("SendPlaceTag(assault_rifle.weapon)", (ok ? "ok" : "refused") + " (" + sw.ElapsedMilliseconds + " ms)");
                report.Check("Weapon placed in the running game", ok);
            }
            catch (Exception ex)
            {
                report.Fail("SendPlaceTag failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Whole-tag preview: edit in memory, serialize, push. The tag file is never saved.</summary>
        private static void QuickPreview(string client, Report report)
        {
            report.Section("Quick preview (whole tag)");
            try
            {
                using (var tag = new TagFile(TagPath.FromPathAndExtension(WeaponPath, "weapon")))
                {
                    var field = tag.SelectField(MagazineField) as TagFieldElementInteger;
                    if (field == null) { report.Fail("Magazine field not found"); return; }
                    field.Data = 99;
                    byte[] data = tag.SerializeForQuickPreview();
                    var sw = Stopwatch.StartNew();
                    QuickPreviewResults r = RemoteCommand.SendQuickPreviewTag(client, tag.Path, data, PreviewTimeoutMs);
                    report.Metric("Magazine set to 99, sent " + data.Length.ToString("N0") + " bytes", (r == null ? "no result" : r.ResultCode.ToString()) + " (" + sw.ElapsedMilliseconds + " ms)");
                    report.Check("Game accepted the edited tag", r != null && r.ResultCode == QuickPreviewResultCode.Success);
                }
            }
            catch (Exception ex)
            {
                report.Fail("Quick preview failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Single-field preview: what dragging a slider in Kalo's tag editor would do.</summary>
        private static void FieldPreview(string client, Report report)
        {
            report.Section("Field preview (slider)");
            try
            {
                using (var tag = new TagFile(TagPath.FromPathAndExtension(WeaponPath, "weapon")))
                {
                    var sw = Stopwatch.StartNew();
                    QuickPreviewResults prep = RemoteCommand.PrepareForIndividualFieldPreview(client, tag.Path, tag.SerializeForQuickPreview(), PreviewTimeoutMs);
                    report.Metric("Prepare", (prep == null ? "no result" : prep.ResultCode.ToString()) + " (" + sw.ElapsedMilliseconds + " ms)");

                    var field = tag.SelectField(MagazineField) as TagFieldElementInteger;
                    if (field == null) { report.Fail("Magazine field not found"); return; }
                    report.Metric("Field supports fast preview", field.SupportsFastIndividualFieldPreview.ToString());

                    var times = new List<long>();
                    var codes = new List<string>();
                    foreach (long value in new long[] { 46, 47, 48, 49, 50 })
                    {
                        field.Data = value;
                        byte[] bytes = RemoteCommand.GetSerializedIndividualFieldPreviewData(new[] { new IndividualFieldPreviewData(field) });
                        sw.Restart();
                        QuickPreviewResults r = RemoteCommand.SendIndividualFieldPreview(client, tag.Path, 1, bytes, PreviewTimeoutMs);
                        times.Add(sw.ElapsedMilliseconds);
                        codes.Add(r == null ? "null" : r.ResultCode.ToString());
                    }
                    report.Metric("Magazine sent as 46, 47, 48, 49, 50", string.Join(", ", codes));
                    report.Metric("Latency per change", "min " + times.Min() + " ms, max " + times.Max() + " ms");
                    report.Check("Every field change accepted", codes.All(c => c == "Success"));
                }
            }
            catch (Exception ex)
            {
                report.Fail("Field preview failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void AskUser(Report report)
        {
            report.Section("What you saw");
            Console.WriteLine();
            Console.WriteLine("Look at the game window. Kalo asked it to place an assault rifle and then changed its");
            Console.WriteLine("magazine size live (99, then 46 to 50). Pick up the rifle (or any assault rifle) and check the ammo.");
            Console.Write("Did the change show up in the game? [y = yes / n = no / anything else = could not check] ");
            string a = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            string what = a == "y" ? "yes" : a == "n" ? "no" : "could not check";
            Console.Write("Anything else you noticed (optional, Enter to skip): ");
            string note = (Console.ReadLine() ?? "").Trim();
            report.Metric("Live change visible in game (your answer)", what);
            if (note.Length > 0) report.Metric("Your note", note);
        }

        /// <summary>The engine test must not run next to the game, so close it (asking first unless --yes).</summary>
        private static void CloseGame(Process game, bool yes, Report report)
        {
            if (!yes)
            {
                Console.WriteLine();
                Console.Write("Close reach_tag_test now? It has to be closed before the engine test. [Y/n] ");
                string a = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                if (a == "n")
                {
                    report.Metric("reach_tag_test", "left running at your request");
                    return;
                }
            }
            try
            {
                game.CloseMainWindow();
                if (!game.WaitForExit(10000)) game.Kill();
                report.Metric("reach_tag_test", "closed");
            }
            catch (Exception ex)
            {
                report.Metric("reach_tag_test", "could not be closed: " + ex.Message);
            }
        }

        private static string Seconds(Stopwatch sw)
        {
            return (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s";
        }

        private static string Safe(Func<string> get)
        {
            try { return get() ?? ""; }
            catch (Exception ex) { return "<" + ex.GetType().Name + ">"; }
        }
    }
}
