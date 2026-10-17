using System;

namespace LGEngine
{
    // LGEngineHost.exe (winexe: no window, no console). `--probe` exits 0 at once (ship8's Smart App Control test-run); no arguments
    // runs the host until idle; `--suffix <s>` (tests) renames the pipe / event / mutex. The logic is GlassHostRunner's.
    internal static class HostMain
    {
        [STAThread]
        static int Main(string[] args)
        {
            string suffix = "";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--probe") return 0;
                if (args[i] == "--suffix" && i + 1 < args.Length) suffix = args[++i];
            }
            GlassHostRunner.InstallCrashLog(null);
            GlassHostRunner r = new GlassHostRunner();
            r.Suffix = suffix;
            return r.Run();
        }
    }
}
