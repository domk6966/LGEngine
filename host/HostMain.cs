using System;
using System.Runtime.InteropServices;

namespace LGEngine
{
    // LGEngineHost.exe (winexe: no window, no console). `--probe` exits 0 at once (ship8's Smart App Control test-run); no arguments
    // runs the host until idle; `--suffix <s>` (tests) renames the pipe / event / mutex. The logic is GlassHostRunner's.
    internal static class HostMain
    {
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            string suffix = "";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--probe") return 0;
                if (args[i] == "--suffix" && i + 1 < args.Length) suffix = args[++i];
            }
            // Clients send physical pixels (LiquidGlass.exe and native adapters are per-monitor aware). A DPI-unaware host
            // has Windows scale every position and size by the DPI, so at 200% the glass lands off-screen. Before any window.
            try { if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
            GlassHostRunner.InstallCrashLog(null);
            GlassHostRunner r = new GlassHostRunner();
            r.Suffix = suffix;
            return r.Run();
        }
    }
}
