using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using LGEngine;

namespace LGBench
{
    // -hostmain: the host process's runner (single instance, idle exit, log ring, DACL) in memory (phase 2b).
    internal static class HostBench
    {
        static int total, ok;

        static void Check(string what, bool pass, string detail)
        {
            total++;
            if (pass) ok++;
            Console.WriteLine("hostmain " + total + ": " + (pass ? "OK   " : "FAIL ") + what + (detail == null || detail.Length == 0 ? "" : " (" + detail + ")"));
        }

        sealed class Running
        {
            public GlassHostRunner R;
            public Thread T;
            public int Code = -99;
            public Stopwatch Since = Stopwatch.StartNew();
            public long EndedAt = -1;

            public Running(GlassHostRunner r)
            {
                R = r;
                T = new Thread(delegate ()
                {
                    Code = R.Run();
                    EndedAt = Since.ElapsedMilliseconds;
                });
                T.IsBackground = true;
                T.Start();
            }

            public bool Join(int ms) { return T.Join(ms); }
        }

        static GlassHostRunner Make(string suffix, int idleMs, string log)
        {
            GlassHostRunner r = new GlassHostRunner();
            r.Suffix = suffix;
            r.IdleMs = idleMs;
            r.LogPath = log;
            return r;
        }

        static NamedPipeClientStream Connect(string suffix, bool readAcl)
        {
            NamedPipeClientStream c = readAcl
                ? new NamedPipeClientStream(".", GlassNames.PipeShort(suffix), PipeAccessRights.ReadPermissions | PipeAccessRights.ReadData | PipeAccessRights.WriteData, PipeOptions.None, System.Security.Principal.TokenImpersonationLevel.None, HandleInheritability.None)
                : new NamedPipeClientStream(".", GlassNames.PipeShort(suffix), PipeDirection.InOut, PipeOptions.None);
            c.Connect(3000);
            c.ReadMode = PipeTransmissionMode.Message;
            return c;
        }

        // Hello -> HelloAck on a connected client.
        static bool Hello(NamedPipeClientStream c)
        {
            PayloadWriter w = new PayloadWriter();
            w.U16(GlassFrame.Version);
            w.U16(GlassFrame.Version);
            w.Str("hostbench");
            byte[] f = GlassFrame.Build(Msg.Hello, 1, w.ToArray());
            c.Write(f, 0, f.Length);
            c.Flush();
            byte[] buf = new byte[GlassFrame.MaxFrame];
            int n = 0;
            Thread t = new Thread(delegate () { try { n = c.Read(buf, 0, buf.Length); } catch (Exception) { n = -1; } });
            t.IsBackground = true;
            t.Start();
            if (!t.Join(3000)) return false;
            int type, len;
            uint seq;
            return n > 0 && GlassFrame.Check(buf, n, out type, out seq, out len) == 0 && type == Msg.HelloAck;
        }

        static int CountLines(string path, string contains)
        {
            if (!File.Exists(path)) return 0;
            int n = 0;
            foreach (string l in File.ReadAllLines(path)) if (l.Contains(contains)) n++;
            return n;
        }

        public static void Run()
        {
            string tag = "hm" + Process.GetCurrentProcess().Id;
            string dir = Path.Combine(Path.GetTempPath(), "lgb-hostmain-" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, "host.log");
            try
            {
                Body(tag, log, dir);
            }
            catch (Exception ex)
            {
                Check("hostmain ran to the end", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch (Exception) { }
            }
            Console.WriteLine("hostmain: " + ok + "/" + total + " checks " + (ok == total ? "OK" : "FAILED"));
        }

        static void Body(string tag, string log, string dir)
        {
            // 1: the runner starts the server; a loopback client connects and gets its HelloAck
            Running a = new Running(Make(tag, 300, log));
            bool up = a.R.Ready.WaitOne(5000);
            bool hi = false;
            NamedPipeClientStream c1 = null;
            try
            {
                if (up) { c1 = Connect(tag, false); hi = Hello(c1); }
            }
            catch (Exception ex) { Console.WriteLine("  connect: " + ex.Message); }
            Check("runner starts the server, a client connects and is acknowledged", up && hi, up ? (hi ? "" : "no HelloAck") : "not ready");

            // 2: a second runner with the same suffix returns "already running" at once, and the first is not disturbed
            GlassHostRunner second = Make(tag, 300, log);
            second.MutexWaitMs = 200;   // (the process waits up to 3 s: see the check at the end)
            Running b = new Running(second);
            bool bDone = b.Join(1500);
            Check("second runner: already running, returns at once", bDone && b.R.AlreadyRunning && b.Code == 0 && a.T.IsAlive, bDone ? "code " + b.Code + " after " + b.EndedAt + " ms" : "still running");

            // 3: the DACL that PipeNative.CreateServer gives a host pipe lists only the current user + SYSTEM (all Allow); the user's connect worked in 1
            bool dacl = false;
            string daclText = "";
            try
            {
                using (NamedPipeServerStream c2 = PipeNative.CreateServer(GlassNames.PipeFull("dacl" + tag), true))
                {
                    PipeSecurity ps = c2.GetAccessControl();
                    AuthorizationRuleCollection rules = ps.GetAccessRules(true, false, typeof(SecurityIdentifier));
                    SecurityIdentifier me, sys = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                    using (WindowsIdentity id = WindowsIdentity.GetCurrent()) me = id.User;
                    bool hasMe = false, hasSys = false, other = false;
                    foreach (AuthorizationRule r in rules)
                    {
                        PipeAccessRule pr = (PipeAccessRule)r;
                        SecurityIdentifier s = (SecurityIdentifier)pr.IdentityReference;
                        if (pr.AccessControlType != AccessControlType.Allow) other = true;
                        else if (s.Equals(me)) hasMe = true;
                        else if (s.Equals(sys)) hasSys = true;
                        else other = true;
                    }
                    dacl = hasMe && hasSys && !other && rules.Count == 2;
                    daclText = rules.Count + " rules";
                }
            }
            catch (Exception ex) { daclText = ex.GetType().Name + ": " + ex.Message + " @" + ex.StackTrace.Split((char)10)[0].Trim(); }
            Check("pipe DACL: current user + SYSTEM only", dacl, daclText);

            // 4: no idle exit while a client is connected (3x the idle time)
            Thread.Sleep(1000);
            Check("no idle exit while a client is connected", a.T.IsAlive && !a.R.IdleExit, "");

            // 5: the idle exit fires after the last client leaves (not at once, not never)
            long left = a.Since.ElapsedMilliseconds;
            c1.Dispose();
            bool ended = a.Join(4000);
            long after = a.EndedAt - left;
            Check("idle exit after the last client leaves", ended && a.Code == 0 && a.R.IdleExit && after >= 250 && after <= 3000, ended ? "code " + a.Code + ", " + after + " ms after" : "still running");

            // 6: no client ever: the idle time runs from the start; the names were released, so the same suffix starts again
            Running d = new Running(Make(tag, 300, log));
            bool dUp = d.R.Ready.WaitOne(5000);
            bool dEnd = d.Join(4000);
            Check("never-connected runner exits by itself, names were free again", dUp && dEnd && d.Code == 0 && d.R.IdleExit && d.EndedAt >= 250, dEnd ? d.EndedAt + " ms" : "still running");

            // 7, 8: the log ring
            string ring = Path.Combine(dir, "ring.log");
            long maxLen = 0;
            for (int i = 0; i < 3000; i++)
            {
                GlassHostRunner.Append(ring, "line " + i + " " + new string('x', 80), GlassHostRunner.LogCap);
                if (i % 50 == 0 || i == 2999) maxLen = Math.Max(maxLen, new FileInfo(ring).Length);
            }
            maxLen = Math.Max(maxLen, new FileInfo(ring).Length);
            Check("log ring stays <= 64 KB", maxLen <= GlassHostRunner.LogCap && maxLen > 8000, maxLen + " bytes");
            string[] lines = File.ReadAllLines(ring);
            bool newest = lines.Length > 50 && lines[lines.Length - 1].Contains("line 2999 ");
            for (int k = 1; k < 40 && newest; k++) newest = lines[lines.Length - k].Contains("line " + (3000 - k) + " ");
            Check("log ring keeps the newest lines, whole", newest, lines.Length + " lines kept");

            // 9: an exception in the runner: one fault line, a nonzero code, no dialog (this thread would hang on one)
            string flog = Path.Combine(dir, "fault.log");
            GlassHostRunner fr = Make(tag, 5000, flog);
            fr.TestFault = delegate { throw new InvalidOperationException("boom"); };
            Running f = new Running(fr);
            bool fDone = f.Join(5000);
            int faultLines = CountLines(flog, "fault");
            Check("runner exception: one log line, nonzero exit", fDone && f.Code != 0 && faultLines == 1 && CountLines(flog, "boom") == 1, fDone ? "code " + f.Code + ", " + faultLines + " fault lines" : "hung");

            // 10: after the fault the names are free again
            Running g = new Running(Make(tag, 150, flog));
            bool gUp = g.R.Ready.WaitOne(5000);
            bool gEnd = g.Join(4000);
            Check("after a fault the next runner starts and ends cleanly", gUp && gEnd && g.Code == 0 && !g.R.AlreadyRunning, "");

            MutexWaitCheck(tag, log);
            IdleWindowCheck(tag, log);
            SessionClockCheck(tag, log);
        }

        // A new instance waits (up to 3 s) for the mutex of a host that is on its way out, then takes over.
        static void MutexWaitCheck(string tag, string log)
        {
            string sfx = tag + "m";
            Running x = new Running(Make(sfx, 400, log));
            bool xUp = x.R.Ready.WaitOne(5000);
            Running y = new Running(Make(sfx, 300, log));
            bool yUp = y.R.Ready.WaitOne(6000);
            bool yEnd = y.Join(4000);
            x.Join(2000);
            Check("a new instance waits for the exiting host's mutex and takes over", xUp && yUp && yEnd && x.R.IdleExit && !y.R.AlreadyRunning && y.Code == 0, "x up " + xUp + ", y up " + yUp + ", y ready after " + y.Since.ElapsedMilliseconds + " ms at most");
        }

        // A client that connects around the moment the host's idle time runs out is served (by that host, or by one it starts).
        static void IdleWindowCheck(string tag, string log)
        {
            string sfx = tag + "w";
            List<Running> all = new List<Running>();
            GlassService.HostStarter = delegate(string path, string s)
            {
                if (s != sfx) return false;
                lock (all) all.Add(new Running(Make(sfx, 300, log)));
                return true;
            };
            int served = 0, tried = 0;
            string bad = "";
            try
            {
                foreach (int off in new int[] { 120, 240, 290, 330, 400, 520 })
                {
                    Running a = new Running(Make(sfx, 300, log));
                    lock (all) all.Add(a);
                    a.R.Ready.WaitOne(5000);
                    Thread.Sleep(off);
                    GlassService svc = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = sfx, HostPath = @"C:\LGBench\no\LGEngineHost.exe" });
                    tried++;
                    if (svc.HostConnected && svc.Transport == "host pipe") served++;
                    else bad += off + " ";
                    svc.Dispose();
                    Running[] list;
                    lock (all) list = all.ToArray();
                    foreach (Running r in list) r.Join(8000);
                    lock (all) all.Clear();
                }
            }
            finally { GlassService.HostStarter = null; }
            Check("a connect during the idle-exit window is served", tried == 6 && served == 6, served + "/" + tried + (bad.Length > 0 ? ", not served at " + bad : ""));
        }

        // A client that joins and leaves between two turns of the idle loop still restarts the idle clock.
        static void SessionClockCheck(string tag, string log)
        {
            string sfx = tag + "s";
            GlassHostRunner r = Make(sfx, 800, log);
            ManualResetEvent hold = new ManualResetEvent(false);
            int armed = 1;
            r.TestFault = delegate { if (Interlocked.Exchange(ref armed, 0) == 1) hold.WaitOne(10000); };   // (the loop is stuck in its first turn)
            Running k = new Running(r);
            bool up = r.Ready.WaitOne(5000);
            bool hi = false;
            try
            {
                using (NamedPipeClientStream c = Connect(sfx, false)) hi = Hello(c);
            }
            catch (Exception ex) { Console.WriteLine("  connect: " + ex.Message); }
            Thread.Sleep(300);
            while (k.Since.ElapsedMilliseconds < 1000) Thread.Sleep(10);   // (past the idle time of the clock that started with the host)
            long releasedAt = k.Since.ElapsedMilliseconds;
            hold.Set();
            Thread.Sleep(300);
            bool aliveAfter = k.T.IsAlive && !r.IdleExit;
            bool ended = k.Join(4000);
            Check("a join and leave between two idle turns restarts the idle clock", up && hi && aliveAfter && ended && r.IdleExit && k.EndedAt - releasedAt >= 600, "alive 300 ms after " + aliveAfter + ", ended " + (ended ? (k.EndedAt - releasedAt) + " ms after the release" : "never"));
        }
    }
}
