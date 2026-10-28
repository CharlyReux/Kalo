using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Kalo.Protocol;
using Kalo.Spike0;

namespace Kalo.BlamHost
{
    /// <summary>
    /// kalo-blamhost --workspace &lt;dir&gt; --pipe &lt;name&gt; [--hrek &lt;dir&gt;]
    ///
    /// Connects to the named pipe the Kalo app created, starts ManagedBlam (tags only) on the workspace,
    /// then answers one JSON request per line until the pipe closes or "shutdown" arrives.
    /// Logs go to stderr, which the app shows in its Output panel.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            string workspace = Arg(args, "--workspace");
            string pipe = Arg(args, "--pipe");
            string hrek = Arg(args, "--hrek");
            if (workspace == null || pipe == null)
            {
                Console.Error.WriteLine("usage: kalo-blamhost --workspace <dir> --pipe <name> [--hrek <dir>]");
                return 2;
            }
            workspace = Path.GetFullPath(workspace).TrimEnd('\\');
            if (hrek != null && IsUnder(workspace, Path.GetFullPath(hrek)))
            {
                Console.Error.WriteLine("Refusing to run: the workspace is inside HREK.");
                return 2;
            }
            if (!File.Exists(Path.Combine(workspace, "project.root")))
            {
                Console.Error.WriteLine("Not an HREK-shaped workspace (no project.root): " + workspace);
                return 2;
            }

            using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut))
            {
                try { client.Connect(15000); }
                catch (TimeoutException)
                {
                    Console.Error.WriteLine("Could not connect to pipe " + pipe);
                    return 3;
                }
                var reader = new StreamReader(client, new UTF8Encoding(false));
                var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                return Serve(workspace, reader, writer);
            }
        }

        // Kept separate so BlamLoader is installed before the JIT touches any ManagedBlam type.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Serve(string workspace, StreamReader reader, StreamWriter writer)
        {
            BlamLoader.Install(workspace);
            return Server.Run(workspace, reader, writer);
        }

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        internal static bool IsUnder(string path, string root)
        {
            string p = Path.GetFullPath(path).TrimEnd('\\');
            string r = Path.GetFullPath(root).TrimEnd('\\');
            return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
        }
    }
}
