using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace LGEngine
{
    // The logic of the LGEngineHost process (phase 2b design 1), apart from Main so that LGBench -hostmain can test it in memory:
    // single instance (the mutex Local\LGEngineHost.<h>.1), the GlassHostServer, the idle exit (IdleMs after the last client left, or
    // after the start if none ever came), the host.log ring (<= 64 KB) and the one-line crash log. Nothing here ever shows a dialog.
    internal sealed class GlassHostRunner
    {
        public const int ExitOk = 0, ExitFault = 1;
        public const int LogCap = 64 * 1024;

        // ---- settings (the process uses the defaults; tests set them) ----
        public string Suffix = "";          // the pipe / event / mutex suffix (GlassConnectOptions.HostSuffix)
        public int IdleMs = 10000;          // the idle exit
        public int MutexWaitMs = 3000;      // how long a new instance waits for the single-instance mutex (a host that is just exiting)
        public string LogPath;              // null: %LOCALAPPDATA%\LGEngine\host.log
        public Action TestFault;            // test hook: called every turn of the idle loop (may throw)

        // ---- results ----
        public readonly ManualResetEvent Ready = new ManualResetEvent(false);   // the server is up
        public volatile bool AlreadyRunning;                                    // Run returned because another host holds the mutex
        public volatile bool IdleExit;                                          // Run returned because the idle time ran out

        readonly ManualResetEvent stopEvent = new ManualResetEvent(false);

        public void RequestStop() { stopEvent.Set(); }

        public string LogFile { get { return LogPath ?? DefaultLogPath(); } }

        public static string DefaultLogPath()
        {
            string dir = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
            return Path.Combine(Path.Combine(dir, "LGEngine"), "host.log");
        }

        // Runs until idle (or RequestStop). 0 = done (also "already running"); ExitFault = an exception, logged as one line.
        public int Run()
        {
            Mutex mutex = null;
            bool owned = false;
            GlassHostServer server = null;
            try
            {
                bool created;
                mutex = new Mutex(false, GlassNames.HostMutex(Suffix), out created);
                try { owned = mutex.WaitOne(MutexWaitMs); }   // (the holder may be on its way out: it lets go within a moment)
                catch (AbandonedMutexException) { owned = true; }   // (the previous host died holding it)
                if (!owned)
                {
                    AlreadyRunning = true;
                    return ExitOk;
                }
                server = new GlassHostServer();
                server.RaisePriority = true;
                server.Start(Suffix);
                Log("start pid " + System.Diagnostics.Process.GetCurrentProcess().Id);
                Ready.Set();
                Loop(server);
                return ExitOk;
            }
            catch (Exception ex)
            {
                Log("fault " + ex.GetType().Name + ": " + ex.Message);
                return ExitFault;
            }
            finally
            {
                try { if (server != null) server.Stop(); }
                catch (Exception) { }
                try
                {
                    if (mutex != null)
                    {
                        if (owned) mutex.ReleaseMutex();
                        mutex.Close();
                    }
                }
                catch (Exception) { }
            }
        }

        void Loop(GlassHostServer server)
        {
            System.Diagnostics.Stopwatch idle = System.Diagnostics.Stopwatch.StartNew();   // running = no client right now
            int seenEvents = server.SessionEvents;
            WaitHandle[] waits = new WaitHandle[] { server.SessionsChanged, stopEvent };
            while (!stopEvent.WaitOne(0))
            {
                server.SessionsChanged.Reset();   // (before the count is read: a change after this wakes the wait below)
                int wait = 1000;
                int events = server.SessionEvents;   // (every join or leave restarts the clock, even one that came and went between two turns)
                bool changed = events != seenEvents;
                seenEvents = events;
                if (server.ClientCount > 0) idle.Reset();
                else
                {
                    if (!idle.IsRunning || changed) idle.Restart();
                    long left = IdleMs - idle.ElapsedMilliseconds;
                    if (left <= 0)
                    {
                        // first stop accepting, then look at the clients again: one that arrived keeps the host (accepting resumes)
                        if (!server.BeginIdleClose())
                        {
                            idle.Reset();
                            continue;
                        }
                        IdleExit = true;
                        Log("idle exit");
                        return;
                    }
                    wait = (int)Math.Min(1000, left);
                }
                if (TestFault != null) TestFault();
                WaitHandle.WaitAny(waits, wait);
            }
        }

        // ---- host.log: a ring of lines, never more than `cap` bytes; nothing here throws ----

        public void Log(string text) { Append(LogFile, text, LogCap); }

        public static void Append(string path, string text, int cap)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + (text ?? "").Replace('\r', ' ').Replace('\n', ' ');
                if (line.Length > 600) line = line.Substring(0, 600);
                byte[] add = Encoding.UTF8.GetBytes(line + "\r\n");
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using (FileStream f = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    long len = f.Length;
                    if (len + add.Length > cap)
                    {
                        // keep the newest half of the cap, from the start of a line
                        int keep = Math.Max(0, cap / 2 - add.Length);
                        byte[] old = new byte[len];
                        f.Position = 0;
                        int got = 0;
                        while (got < old.Length)
                        {
                            int n = f.Read(old, got, old.Length - got);
                            if (n <= 0) break;
                            got += n;
                        }
                        int from = Math.Max(0, got - keep);
                        while (from < got && from > 0 && old[from - 1] != (byte)'\n') from++;
                        f.Position = 0;
                        f.Write(old, from, got - from);
                        f.SetLength(got - from);
                    }
                    f.Position = f.Length;
                    f.Write(add, 0, add.Length);
                }
            }
            catch (Exception) { }
        }

        // The process-wide last resort: an unhandled exception is one line in host.log, then exit 1 (no Windows error dialog).
        public static void InstallCrashLog(string path)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                Append(path ?? DefaultLogPath(), "crash " + (ex == null ? "unknown" : ex.GetType().Name + ": " + ex.Message), LogCap);
                Environment.Exit(ExitFault);
            };
        }
    }
}
