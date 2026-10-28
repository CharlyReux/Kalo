using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Kalo.Spike0
{
    /// <summary>
    /// Makes ManagedBlam.dll (and the native DLLs it needs) loadable from the workspace,
    /// the same way HREK's own tools find it (MegaloEdit probes bin\ and bin\tools\bonobo\).
    /// Must run before any code that touches a Bungie.* type.
    /// </summary>
    internal static class BlamLoader
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string path);

        private static string[] _probe;

        public static void Install(string workspaceRoot)
        {
            string bin = Path.Combine(workspaceRoot, "bin");
            _probe = new[] { bin, Path.Combine(bin, "tools", "bonobo"), workspaceRoot };

            // Native dependencies are resolved from the workspace root and bin\.
            Environment.SetEnvironmentVariable("PATH", workspaceRoot + ";" + bin + ";" + Environment.GetEnvironmentVariable("PATH"));
            SetDllDirectory(workspaceRoot);
            Directory.SetCurrentDirectory(workspaceRoot);

            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            foreach (string dir in _probe)
            {
                string candidate = Path.Combine(dir, name + ".dll");
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        }
    }
}
