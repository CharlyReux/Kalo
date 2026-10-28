using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Kalo.Spike1
{
    /// <summary>
    /// Reads the engine's own log files (workspace\reports\reports_NN\*.txt) while the game runs.
    /// The game writes them with NUL padding and keeps them open, so they are read shared and cleaned.
    /// </summary>
    internal sealed class GameLog
    {
        private readonly string _reports;
        private readonly DateTime _since;
        private readonly Dictionary<string, long> _offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public GameLog(string workspace, DateTime sinceUtc)
        {
            _reports = Path.Combine(workspace, "reports");
            _since = sinceUtc.AddSeconds(-5);
        }

        /// <summary>The newest reports_NN folder written since the game started, or null.</summary>
        public string CurrentFolder()
        {
            if (!Directory.Exists(_reports)) return null;
            return Directory.GetDirectories(_reports, "reports_*")
                .Select(d => new DirectoryInfo(d))
                .Where(d => d.GetFiles("*.txt").Any(f => f.LastWriteTimeUtc >= _since))
                .OrderByDescending(d => d.GetFiles("*.txt").Max(f => f.LastWriteTimeUtc))
                .Select(d => d.FullName)
                .FirstOrDefault();
        }

        public string ReadAll(string fileName)
        {
            string dir = CurrentFolder();
            if (dir == null) return "";
            string path = Path.Combine(dir, fileName);
            if (!File.Exists(path)) return "";
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Encoding.UTF8))
                {
                    return r.ReadToEnd().Replace("\0", "");
                }
            }
            catch (IOException) { return ""; }
        }

        /// <summary>Marks the current end of every log file; NewLines then returns only what comes after.</summary>
        public void Mark()
        {
            string dir = CurrentFolder();
            if (dir == null) return;
            foreach (string f in Directory.GetFiles(dir, "*.txt"))
                _offsets[Path.GetFileName(f)] = new FileInfo(f).Length;
        }

        public List<string> NewLines()
        {
            var lines = new List<string>();
            string dir = CurrentFolder();
            if (dir == null) return lines;
            foreach (string f in Directory.GetFiles(dir, "*.txt"))
            {
                string name = Path.GetFileName(f);
                long start;
                if (!_offsets.TryGetValue(name, out start)) start = 0;
                try
                {
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (fs.Length <= start) continue;
                        fs.Seek(start, SeekOrigin.Begin);
                        using (var r = new StreamReader(fs, Encoding.UTF8))
                        {
                            foreach (string l in r.ReadToEnd().Replace("\0", "").Split('\n'))
                            {
                                string t = l.Trim();
                                if (t.Length > 0) lines.Add(name + ": " + t);
                            }
                        }
                    }
                }
                catch (IOException) { }
            }
            return lines;
        }
    }

    /// <summary>Captures one window (the game), never the whole desktop, even when other windows cover it.</summary>
    internal static class ScreenCapture
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

        public static string Save(Process process, string path)
        {
            try
            {
                SetProcessDPIAware();
                if (process == null || process.HasExited) return "no window (process not running)";
                process.Refresh();
                IntPtr hwnd = process.MainWindowHandle;
                RECT r;
                if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out r)) return "no window";
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return "window has no size (minimized?)";
                using (var bmp = new Bitmap(w, h))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        PrintWindow(hwnd, hdc, 2); // PW_RENDERFULLCONTENT
                        g.ReleaseHdc(hdc);
                    }
                    bmp.Save(path, ImageFormat.Png);
                }
                return Path.GetFileName(path);
            }
            catch (Exception ex)
            {
                return "screenshot failed: " + ex.Message;
            }
        }
    }

    /// <summary>Which TCP/UDP ports a process listens on (parsed from netstat -ano).</summary>
    internal static class Ports
    {
        public static List<string> ListeningBy(int pid)
        {
            var result = new List<string>();
            try
            {
                var psi = new ProcessStartInfo("netstat", "-ano") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    foreach (string raw in output.Split('\n'))
                    {
                        string[] parts = raw.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 4) continue;
                        string proto = parts[0];
                        string owner = parts[parts.Length - 1];
                        if (owner != pid.ToString()) continue;
                        if (proto == "TCP" && parts.Length >= 5 && parts[3] == "LISTENING") result.Add("TCP " + parts[1]);
                        else if (proto == "UDP") result.Add("UDP " + parts[1]);
                    }
                }
            }
            catch (Exception ex)
            {
                result.Add("netstat failed: " + ex.Message);
            }
            return result;
        }

        /// <summary>Port number from "0.0.0.0:1234" or "[::]:1234".</summary>
        public static int PortOf(string endpoint)
        {
            int i = endpoint.LastIndexOf(':');
            int port;
            return i >= 0 && int.TryParse(endpoint.Substring(i + 1), out port) ? port : -1;
        }
    }

    internal static class Wait
    {
        /// <summary>Polls until the condition is true, the process exits, or the timeout passes.</summary>
        public static bool Until(Func<bool> condition, Process process, int timeoutSeconds)
        {
            DateTime end = DateTime.Now.AddSeconds(timeoutSeconds);
            while (DateTime.Now < end)
            {
                if (process != null && process.HasExited) return false;
                if (condition()) return true;
                Thread.Sleep(2000);
            }
            return false;
        }
    }
}
