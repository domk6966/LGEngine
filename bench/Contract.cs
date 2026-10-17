using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LGEngine;

namespace LGBench
{
    // -contract: the public surface API (GlassService / GlassSurface) used only through public types, off screen. Run for the
    // render-thread transport (the calls come from the bench thread without pumping, and from pool threads) and, for the
    // latency comparison and the same checks, the UI-thread transport (pumping, as step 5 did).
    static class Contract
    {
        const string Title = "LGBench contract";

        static int checks, bad;

        static void Check(bool ok, string what)
        {
            checks++;
            if (ok) return;
            bad++;
            Program.Say("contract: FAILED " + what);
        }

        // A counter several threads may touch.
        sealed class Count
        {
            int n;
            public void Inc() { lock (this) n++; }
            public int Get() { lock (this) return n; }
        }

        // Pumps messages (never Application.Run) until done() or the time is up.
        internal static bool Pump(int ms, Func<bool> done)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                if (done != null && done()) return true;
                Thread.Sleep(1);
            }
            Application.DoEvents();
            return done != null && done();
        }

        static int[] Picture(int w, int h, int stripe)
        {
            int[] px = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool ink = y >= stripe && y < stripe + 14 && x > 20 && x < w - 20 && ((x / 6) % 3 != 0);
                    px[y * w + x] = ink ? unchecked((int)0xB0B0B0B0) : 0;
                }
            return px;
        }

        internal static GlassSpec Spec(string label)
        {
            GlassSpec s = new GlassSpec();
            s.Label = label;
            s.Kind = GlassKind.Card;
            s.Size = new Size(360, 120);
            s.Place = new Point(-10000, -10000);
            return s;
        }

        // One invalid case on a fresh surface: exactly one Failed, nothing thrown, later calls silent.
        static void Invalid(GlassService svc, string name, GlassSpec spec, Action<GlassSurface> act)
        {
            Count failed = new Count();
            int code = 0;
            try
            {
                GlassSurface s = svc.Create(spec);
                s.Failed += delegate(object o, GlassFailedEventArgs e) { failed.Inc(); code = e.Code; };
                if (act != null) act(s);
                Pump(50, null);
                // a second bad call after the failure must not raise again, nor throw
                s.SetContents(null, 1, 1, Rectangle.Empty);
                s.SetOpacity(float.NaN);
                s.Show();
                Pump(50, null);
                Check(failed.Get() == 1, name + ": Failed raised " + failed.Get() + " times (code " + code + ")");
                Check(s.HasFailed, name + ": HasFailed");
                s.Dispose();
            }
            catch (Exception ex)
            {
                Check(false, name + ": threw " + ex.GetType().Name + " " + ex.Message);
            }
        }

        // ---- the windows of this process ----

        delegate bool EnumProc(IntPtr hwnd, IntPtr lp);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumProc proc, IntPtr lp);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

        // Top-level windows of this process whose title starts with "LGBench contract".
        static int TestWindows()
        {
            int n = 0;
            uint me = (uint)Process.GetCurrentProcess().Id;
            EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != me) return true;
                StringBuilder sb = new StringBuilder(128);
                GetWindowText(h, sb, 128);
                if (sb.ToString().StartsWith(Title, StringComparison.Ordinal)) n++;
                return true;
            }, IntPtr.Zero);
            return n;
        }

        static void LatencyLine(string mode, GlassSurface surf)
        {
            GlassLatencyInfo l = surf.Latency;
            string retries = mode.StartsWith("loopback", StringComparison.Ordinal) ? ", seqlock retries " + GlassPipeStats.SeqlockRetries : "";
            Program.Say("contract [{0}]: queue {1:0.0}/{2:0.0} ms, present {3:0.0}/{4:0.0} ms, vblank {5:0.0}/{6:0.0} ms (p50/p99), frames {7}, samples {8}/{9}/{10}{11}",
                mode, l.QueueP50, l.QueueP99, l.PresentP50, l.PresentP99, l.VblankP50, l.VblankP99, surf.Frames, l.QueueSamples, l.PresentSamples, l.VblankSamples, retries);
            Check(l.PresentSamples > 0, mode + ": latency samples were taken");
            Check(l.QueueSamples > 0, mode + ": queue samples were taken");
            Check(l.PresentP99 < 1000, mode + ": present p99 under 1 s");
            if (mode == "host process")
            {
                // SERVICE.md 4: a client call is on screen within one vblank + ~1 ms (informational: it depends on the display)
                double period = GlassVblank.PeriodMs();
                bool ok = l.VblankSamples > 0 && l.VblankP50 <= period + 1.0;
                Program.Say("contract [host process]: vblank {0:0.0} ms (p50) against the budget <vblank+1ms> = {1:0.0} ms: {2}", l.VblankP50, period + 1.0, ok ? "OK" : "OVER");
            }
        }

        static string runHostPath;   // RunMode over the pipe: the HostPath to give (null = none)

        internal static void Run(bool loopback, bool hostProcess)
        {
            Program.Say("");
            Program.Say("contract: surface API (the test windows are off the desktop)");
            Check(GlassLatencyStruct(), "DWM timing struct is 292 bytes");
            try
            {
                if (hostProcess)
                {
                    RunHostProcess();
                }
                else if (loopback)
                {
                    RunLoopback();
                }
                else
                {
                    RunMode(GlassMode.Auto, "render thread", "render thread (host unavailable)", null);
                    RunMode(GlassMode.UiThread, "ui thread", "ui thread", null);
                    ThreadChecks(null);
                    OfflineChecks();
                }
            }
            catch (Exception ex)
            {
                Check(false, "threw " + ex.GetType().Name + " " + ex.Message);
            }
            Program.Say("contract: {0}/{1} checks OK", checks - bad, checks);
        }

        static bool GlassLatencyStruct()
        {
            return GlassService.TimingStructSize == 292;
        }

        // suffix: non-null = Host mode over the pipe (a GlassHostServer in this process listens on that suffix).
        static void RunMode(GlassMode mode, string name, string transport, string suffix)
        {
            bool render = mode != GlassMode.UiThread;
            bool piped = suffix != null;
            GlassConnectOptions opt = new GlassConnectOptions { Mode = mode };
            if (piped) { opt.HostSuffix = suffix; opt.HostPath = runHostPath; }
            else opt.HostSuffix = "auto" + Process.GetCurrentProcess().Id;   // (Auto looks for a host first: never the real one)
            GlassService svc = GlassService.Connect(opt);
            try
            {
                Check(svc.Transport == transport, name + ": transport name " + svc.Transport);
                GlassMode expectUsed = piped ? GlassMode.Host : (render ? GlassMode.InProcess : GlassMode.UiThread);
                Check(svc.ModeUsed == expectUsed, name + ": ModeUsed " + svc.ModeUsed);
                Check(svc.EngineThreadAlive == (render && !piped), name + ": engine thread alive " + svc.EngineThreadAlive);
                if (mode != GlassMode.UiThread) RangeChecks(svc, name);

                // 1. A valid surface: show, a burst of calls, hide.
                GlassSpec spec = Spec(Title + " test (off screen)");
                GlassSurface surf = svc.Create(spec);
                Count shown = new Count(), hidden = new Count(), failed = new Count();
                surf.Shown += delegate { shown.Inc(); };
                surf.Hidden += delegate { hidden.Inc(); };
                surf.Failed += delegate(object o, GlassFailedEventArgs e) { failed.Inc(); Program.Say("contract: unexpected Failed {0} {1}", e.Code, e.Text); };
                int w = spec.Size.Width, h = spec.Size.Height;
                surf.SetContents(Picture(w, h, 40), w, h, Rectangle.Empty);
                surf.Show();
                Check(Pump(2000, delegate { return shown.Get() > 0; }), name + ": Shown within 2 s");
                Check(Pump(2000, delegate { return surf.Frames > 0; }), name + ": a picture reached the screen path");

                // (a) the burst: on the render thread the bench thread does not pump at all, the engine does the work
                for (int i = 0; i < 200; i++)
                {
                    int stripe = 20 + (i * 3) % 60;
                    surf.SetPlace(new Point(-10000 + (i % 7), -10000 + (i % 5)), i % 3 == 0);
                    surf.SetOpacity(i % 2 == 0 ? 0.6f : 1.0f);
                    surf.SetContents(Picture(w, h, stripe), w, h, new Rectangle(0, Math.Max(0, stripe - 4), w, 24));
                    if (render) Thread.Sleep(4);
                    else Pump(4, null);
                }
                Pump(300, null);
                LatencyLine(name, surf);

                surf.Hide();
                Check(Pump(3000, delegate { return hidden.Get() > 0; }), name + ": Hidden after Hide");
                Check(failed.Get() == 0, name + ": no failure on the valid surface");

                // Show again; then Hide, and Show before the exit is over: Shown comes again (the Hide reset the shown flag).
                int s1 = shown.Get();
                surf.Show();
                Check(Pump(2000, delegate { return shown.Get() > s1; }), name + ": Shown after Show again");
                surf.Hide();
                if (render) Thread.Sleep(60);
                else Pump(60, null);
                surf.Show();
                Check(Pump(2000, delegate { return shown.Get() > s1 + 1; }), name + ": Shown again when a Show interrupts the exit");
                surf.Dispose();
                surf.SetOpacity(0.5f);     // after Dispose: ignored, no exception
                surf.Show();
                Check(true, name + ": calls after Dispose are ignored");

                // 2. Invalid input: each one failure, none escapes.
                Invalid(svc, name + ": null contents", Spec(Title + " invalid 1"), delegate(GlassSurface s) { s.SetContents(null, w, h, Rectangle.Empty); });
                Invalid(svc, name + ": oversized contents", Spec(Title + " invalid 2"), delegate(GlassSurface s) { s.SetContents(new int[361 * 120], 361, 120, Rectangle.Empty); });
                Invalid(svc, name + ": dirty out of range", Spec(Title + " invalid 3"), delegate(GlassSurface s) { s.SetContents(new int[w * h], w, h, new Rectangle(0, 0, w + 5, h)); });
                Invalid(svc, name + ": NaN opacity", Spec(Title + " invalid 4"), delegate(GlassSurface s) { s.SetOpacity(float.NaN); });
                Invalid(svc, name + ": garbage profile in SetLook", Spec(Title + " invalid 5"), delegate(GlassSurface s) { s.SetLook(spec.Look, "not a profile !!"); });
                GlassSpec garbage = Spec(Title + " invalid 6");
                garbage.Profile = "garbage ~~ text";
                Invalid(svc, name + ": garbage profile in the spec", garbage, null);
                GlassSpec empty = Spec(Title + " invalid 7");
                empty.Size = new Size(0, 0);
                Invalid(svc, name + ": empty size in the spec", empty, null);
                try { svc.Create(null); Check(false, "null spec"); } catch (ArgumentNullException) { Check(true, "null spec"); }
                try { svc.Create(new GlassSpec()); Check(false, "empty label"); } catch (ArgumentException) { Check(true, "empty label"); }

                SizeChecks(svc, name);
                SlideChecks(svc, name);
                if (render) PoolChecks(svc, name);
            }
            finally
            {
                svc.Dispose();
            }
            Check(!svc.EngineThreadAlive, name + ": engine thread ended after Dispose");
        }

        // Two threads alternating SetSize and SetContents: the surface never fails and ends at the last size (a picture that lost
        // the race to a SetSize is dropped, not a failure).
        static void SizeChecks(GlassService svc, string name)
        {
            Size[] cycle = new Size[] { new Size(360, 120), new Size(300, 100), new Size(400, 140) };
            GlassSurface s = svc.Create(Spec(Title + " size race"));
            Count failed = new Count(), fin = new Count();
            s.Failed += delegate(object o, GlassFailedEventArgs e) { failed.Inc(); Program.Say("contract: size race Failed {0} {1}", e.Code, e.Text); };
            for (int k = 1; k < cycle.Length; k++) s.SetSize(cycle[k]);
            s.SetSize(cycle[0]);
            Size last = cycle[(200 - 1) % cycle.Length];
            ThreadStart sizer = delegate
            {
                for (int i = 0; i < 200; i++) { s.SetSize(cycle[i % cycle.Length]); Thread.Sleep(0); }
                fin.Inc();
            };
            ThreadStart putter = delegate
            {
                for (int i = 0; i < 200; i++)
                {
                    Size z = cycle[(i * 2 + 1) % cycle.Length];
                    s.SetContents(Picture(z.Width, z.Height, 20), z.Width, z.Height, Rectangle.Empty);
                    Thread.Sleep(0);
                }
                fin.Inc();
            };
            Thread t1 = new Thread(sizer), t2 = new Thread(putter);
            t1.Start();
            t2.Start();
            Check(Pump(20000, delegate { return fin.Get() == 2; }), name + ": size race threads finished");
            t1.Join();
            t2.Join();
            s.SetContents(Picture(last.Width, last.Height, 20), last.Width, last.Height, Rectangle.Empty);   // the last size is current
            s.Show();
            Pump(300, null);
            Check(!s.HasFailed && failed.Get() == 0, name + ": SetSize/SetContents race never fails the surface");
            s.Dispose();
        }

        // A Show whose slide is 1 px still reaches the screen path and is timed.
        static void SlideChecks(GlassService svc, string name)
        {
            GlassSpec spec = Spec(Title + " 1 px slide");
            spec.AnimStyle = 2;
            spec.AnimIn = 60;   spec.Scale = 0.03f;   // (34 px of travel x 0.03 = 1 px)
            GlassSurface s = svc.Create(spec);
            s.SetContents(Picture(spec.Size.Width, spec.Size.Height, 40), spec.Size.Width, spec.Size.Height, Rectangle.Empty);
            s.SetMotion(2, 60, 60);
            s.Show();
            Check(Pump(2000, delegate { return s.Frames > 0; }), name + ": a 1 px slide Show reached the screen path");
            Check(s.Latency.PresentSamples > 0, name + ": the 1 px slide Show was timed");
            s.Dispose();
        }
        static void WaitFor4(Count go)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (go.Get() < 4 && sw.ElapsedMilliseconds < 3000) Thread.Sleep(0);
        }

        // (b) the same burst from 4 pool threads at once, with a Show racing the setters (a lost update would lose the Shown); and
        // (c) Dispose, and a call back into the surface, from inside the handlers.
        static void PoolChecks(GlassService svc, string mode)
        {
            GlassSpec spec = Spec(Title + " pool test (off screen)");
            int w = spec.Size.Width, h = spec.Size.Height;
            GlassSurface surf = svc.Create(spec);
            Count failed = new Count(), shown = new Count();
            surf.Failed += delegate { failed.Inc(); };
            surf.Shown += delegate { shown.Inc(); };
            surf.SetContents(Picture(w, h, 40), w, h, Rectangle.Empty);
            surf.Show();
            Check(Pump(2000, delegate { return shown.Get() > 0; }), "pool: Shown within 2 s");
            Count done = new Count();
            for (int t = 0; t < 4; t++)
            {
                int me = t;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    for (int i = 0; i < 50; i++)
                    {
                        int stripe = 20 + ((i + me * 13) * 3) % 60;
                        surf.SetPlace(new Point(-10000 + (i % 7), -10000 + (me % 5)), i % 3 == 0);
                        surf.SetOpacity((i + me) % 2 == 0 ? 0.6f : 1.0f);
                        surf.SetContents(Picture(w, h, stripe), w, h, new Rectangle(0, Math.Max(0, stripe - 4), w, 24));
                        surf.SetInteraction(i % 2 == 0, new Rectangle(0, 0, 100, 50));
                        Thread.Sleep(4);
                    }
                    done.Inc();
                });
            }
            Check(Pump(20000, delegate { return done.Get() == 4; }), "pool: 4 threads finished their bursts");
            Pump(300, null);
            LatencyLine(mode + ", 4 threads", surf);
            Check(failed.Get() == 0, "pool: no failure from concurrent calls");
            surf.Dispose();

            // a Show racing setters on other threads is never lost
            int lost = 0;
            for (int round = 0; round < 10; round++)
            {
                GlassSurface s = svc.Create(Spec(Title + " race " + round));
                Count sh = new Count();
                s.Shown += delegate { sh.Inc(); };
                s.SetContents(Picture(w, h, 40), w, h, Rectangle.Empty);
                Count go = new Count(), fin = new Count();
                for (int t = 0; t < 3; t++)
                {
                    int me = t;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        go.Inc();
                        WaitFor4(go);
                        for (int i = 0; i < 40; i++)
                        {
                            s.SetPlace(new Point(-10000 + i, -10000 + me), false);
                            s.SetOpacity(0.5f + (i % 2) * 0.5f);
                            s.SetInteraction(true);
                        }
                        fin.Inc();
                    });
                }
                go.Inc();
                WaitFor4(go);
                s.Show();
                Pump(10000, delegate { return fin.Get() == 3; });
                if (!Pump(2000, delegate { return sh.Get() > 0; })) lost++;
                s.Dispose();
            }
            Check(lost == 0, "race: Show lost to concurrent setters in " + lost + " of 10 rounds");

            // (c) Dispose from inside Failed; a Set call from inside Shown
            GlassSurface f = svc.Create(Spec(Title + " dispose in Failed"));
            Count fh = new Count();
            f.Failed += delegate(object o, GlassFailedEventArgs e) { f.Dispose(); fh.Inc(); };
            f.SetOpacity(float.NaN);
            Check(Pump(2000, delegate { return fh.Get() > 0; }), "Failed handler ran");
            Check(f.IsDisposed, "Dispose inside a Failed handler took effect");
            f.Show();
            Check(true, "calls after a handler's Dispose are ignored");

            GlassSurface g = svc.Create(Spec(Title + " set in Shown"));
            Count gs = new Count();
            g.Shown += delegate { g.SetOpacity(0.8f); g.SetPlace(new Point(-9990, -10000), false); gs.Inc(); };
            g.SetContents(Picture(w, h, 40), w, h, Rectangle.Empty);
            g.Show();
            Check(Pump(2000, delegate { return gs.Get() > 0; }), "Shown handler ran and called back");
            Pump(200, null);
            Check(!g.HasFailed, "no failure after calls inside Shown");
            g.Dispose();

        }

        // Host mode with no host running: the service is offline, never throws, every surface it makes is Failed "host unavailable".
        static void OfflineChecks()
        {
            GlassService host = null;
            try
            {
                host = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = "off" + Process.GetCurrentProcess().Id });
                Check(host.ModeUsed == GlassMode.Host && host.Transport == "host pipe", "Host mode with no host: " + host.ModeUsed + " / " + host.Transport);
                Check(!host.HostConnected, "Host mode with no host: not connected");
                GlassSurface s = host.Create(Spec(Title + " offline"));
                Count failed = new Count();
                string text = "";
                s.Failed += delegate(object o, GlassFailedEventArgs e) { text = e.Text; failed.Inc(); };
                Check(Pump(1000, delegate { return failed.Get() > 0; }), "offline: the surface failed");
                Check(failed.Get() == 1 && text.IndexOf("host unavailable", StringComparison.Ordinal) >= 0, "offline: Failed once with 'host unavailable' (" + failed.Get() + ": " + text + ")");
                s.SetContents(null, 1, 1, Rectangle.Empty);
                s.Show();
                s.Dispose();
                Check(true, "offline: calls on the failed surface are ignored");
            }
            catch (Exception ex)
            {
                Check(false, "offline: threw " + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                if (host != null) host.Dispose();
            }
        }

        // (d) GlassService.Dispose with 3 live surfaces: every window gone, the thread ended.
        // ---- loopback: a GlassHostServer in this process, real pipes, sections and events ----

        static NamedPipeClientStream Raw(GlassHostServer host)
        {
            NamedPipeClientStream p = new NamedPipeClientStream(".", host.PipeName.Substring(@"\\.\pipe\".Length), PipeDirection.InOut, PipeOptions.Asynchronous);
            p.Connect(2000);
            p.ReadMode = PipeTransmissionMode.Message;
            return p;
        }

        static void RawSend(NamedPipeClientStream p, byte[] frame)
        {
            try { p.Write(frame, 0, frame.Length); }
            catch (Exception) { }
        }

        // One reply: its length; 0 = the pipe ended; -1 = nothing within ms (the pipe is then abandoned by the caller).
        static int RawRead(NamedPipeClientStream p, byte[] buf, int ms)
        {
            try
            {
                IAsyncResult ar = p.BeginRead(buf, 0, buf.Length, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ms)) return -1;
                return p.EndRead(ar);
            }
            catch (Exception) { return 0; }
        }

        static byte[] Hello(int min, int max)
        {
            return new byte[] { (byte)min, 0, (byte)max, 0, 0, 0 };
        }

        // Sends a frame and checks the reply is an Error with the given code (code 0: any Error).
        static bool ExpectError(NamedPipeClientStream p, byte[] frame, int code, byte[] buf)
        {
            RawSend(p, frame);
            int n = RawRead(p, buf, 2000);
            if (n <= 0 || GlassProtocolTest.ReplyType(buf, n) != GlassProtocolTest.TypeError) return false;
            return code == 0 || BitConverter.ToInt32(buf, 16) == code;
        }

        static void MalformedChecks(GlassHostServer host)
        {
            byte[] buf = new byte[70000];
            int n;
            // no Hello first: an Error, then the session ends
            using (NamedPipeClientStream p = Raw(host))
            {
                Check(ExpectError(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeCreate, 1, GlassProtocolTest.CreatePayload(1, Title + " raw", 100, 50, "")), 10, buf), "malformed: a Create before Hello gets Error NoHello");
                Check(RawRead(p, buf, 2000) == 0, "malformed: the session ends after it");
            }
            // a Hello for a protocol version the host does not speak
            using (NamedPipeClientStream p = Raw(host))
            {
                Check(ExpectError(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeHello, 1, Hello(2, 2)), 3, buf), "malformed: Hello for version 2 gets Error BadVersion");
                Check(RawRead(p, buf, 2000) == 0, "malformed: the session ends after it");
            }
            // a frame larger than any frame: Error, then the session ends
            using (NamedPipeClientStream p = Raw(host))
            {
                RawSend(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeHello, 1, GlassProtocolTest.HelloPayload("LGBench raw")));
                n = RawRead(p, buf, 2000);
                Check(n > 0 && GlassProtocolTest.ReplyType(buf, n) == GlassProtocolTest.TypeHelloAck, "malformed: Hello is answered");
                byte[] big = new byte[70000];
                for (int i = 0; i < big.Length; i++) big[i] = 0x41;
                RawSend(p, big);
                n = RawRead(p, buf, 2000);
                Check(n > 0 && GlassProtocolTest.ReplyType(buf, n) == GlassProtocolTest.TypeError, "malformed: a 70000-byte message gets an Error");
            }
            // the session survives bad frames and bad requests
            using (NamedPipeClientStream p = Raw(host))
            {
                RawSend(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeHello, 1, GlassProtocolTest.HelloPayload("LGBench raw")));
                n = RawRead(p, buf, 2000);
                Check(n > 0 && GlassProtocolTest.ReplyType(buf, n) == GlassProtocolTest.TypeHelloAck, "malformed: Hello is answered");
                byte[] good = GlassProtocolTest.Frame(GlassProtocolTest.TypeCreate, 5, GlassProtocolTest.CreatePayload(7, Title + " raw", 200, 80, ""));
                byte[] shortLen = new byte[good.Length - 1];
                Array.Copy(good, shortLen, shortLen.Length);
                byte[] badMagic = (byte[])good.Clone();
                badMagic[0] ^= 0xFF;
                Check(ExpectError(p, new byte[] { 1, 2, 3, 4, 5 }, 1, buf), "malformed: a 5-byte message gets Error BadFrame");
                Check(ExpectError(p, badMagic, 1, buf), "malformed: bad magic gets Error BadFrame");
                Check(ExpectError(p, shortLen, 2, buf), "malformed: a length that does not match gets Error BadLength");
                Check(ExpectError(p, GlassProtocolTest.Frame(77, 6, null), 4, buf), "malformed: an unknown type gets Error Unsupported");
                Check(ExpectError(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeCreate, 7, new byte[10]), 5, buf), "malformed: a short Create gets Error BadPayload");
                Check(ExpectError(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeCreate, 8, GlassProtocolTest.CreatePayload(7, Title + " raw", 5000, 80, "")), 5, buf), "malformed: a 5000-wide Create gets Error BadPayload");
                Check(ExpectError(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeCreate, 9, GlassProtocolTest.CreatePayload(7, Title + " raw", 200, 80, "garbage ~~ text")), 5, buf), "malformed: a Create with a garbage profile gets Error BadPayload");
                byte[] ghost = BitConverter.GetBytes(424242u);
                Check(ExpectError(p, GlassProtocolTest.Frame(5, 10, ghost), 8, buf), "malformed: Destroy of an unknown surface gets Error NoSurface");
                RawSend(p, good);
                n = RawRead(p, buf, 2000);
                Check(n > 0 && GlassProtocolTest.ReplyType(buf, n) == GlassProtocolTest.TypeCreateAck, "malformed: after all that, a good Create is acknowledged");
                // a burst far over the rate limit is slowed, not failed: every one is answered, none with an Error, the session lives
                int sawError = 0, answered = 0;
                byte[] stats = GlassProtocolTest.Frame(8, 11, BitConverter.GetBytes(BitConverter.ToUInt32(buf, 20)));   // (the surface id from the CreateAck)
                Stopwatch burst = Stopwatch.StartNew();
                for (int i = 0; i < 600; i++) RawSend(p, stats);
                while (answered < 600 && (n = RawRead(p, buf, 2000)) > 0)
                {
                    int t = GlassProtocolTest.ReplyType(buf, n);
                    if (t == GlassProtocolTest.TypeError) sawError++;
                    else if (t == GlassProtocolTest.TypeStatsReply) answered++;
                }
                Check(sawError == 0 && answered == 600, "malformed: a burst of 600 messages is throttled, not failed (" + answered + " answered, " + sawError + " errors, " + burst.ElapsedMilliseconds + " ms)");
            }
        }

        static bool SameLook(GlassLook a, GlassLook b)
        {
            return a.Dark == b.Dark && a.Blur == b.Blur && a.Refraction == b.Refraction && a.Rim == b.Rim && a.Shadow == b.Shadow && a.Round == b.Round && a.Intensity == b.Intensity && a.Elevation == b.Elevation;
        }

        // One raw client with a Hello done; null if the host did not answer.
        static NamedPipeClientStream RawHello(GlassHostServer host, byte[] buf)
        {
            NamedPipeClientStream p = Raw(host);
            RawSend(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeHello, 1, GlassProtocolTest.HelloPayload("LGBench raw")));
            int n = RawRead(p, buf, 2000);
            if (n > 0 && GlassProtocolTest.ReplyType(buf, n) == GlassProtocolTest.TypeHelloAck) return p;
            p.Dispose();
            return null;
        }

        // A connection that sends nothing is dropped within 3 s (the Hello timeout is 2 s).
        static void HelloTimeoutCheck(GlassHostServer host)
        {
            byte[] buf = new byte[70000];
            using (NamedPipeClientStream p = Raw(host))
            {
                Stopwatch sw = Stopwatch.StartNew();
                int n = RawRead(p, buf, 3000);
                Check(n == 0 && sw.ElapsedMilliseconds < 3000, "hello timeout: a connection that never says Hello is dropped within 3 s (read " + n + " after " + sw.ElapsedMilliseconds + " ms)");
            }
        }

        // 17 clients: the 17th waits (all 16 pipe instances are taken); when one leaves it connects within 1 s.
        static void CapCheck(GlassHostServer host)
        {
            Pump(3000, delegate { return host.ClientCount == 0; });
            byte[] buf = new byte[70000];
            List<NamedPipeClientStream> ps = new List<NamedPipeClientStream>();
            NamedPipeClientStream extra = null;
            try
            {
                for (int i = 0; i < 16; i++)
                {
                    NamedPipeClientStream p = RawHello(host, buf);
                    if (p != null) ps.Add(p);
                }
                Check(ps.Count == 16, "cap: 16 clients connected and were answered (" + ps.Count + ")");
                Count joined = new Count();
                string pipe = host.PipeName.Substring(@"\\.\pipe\".Length);
                Thread t = new Thread(delegate()
                {
                    try
                    {
                        NamedPipeClientStream q = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                        q.Connect(5000);
                        q.ReadMode = PipeTransmissionMode.Message;
                        extra = q;
                        joined.Inc();
                    }
                    catch (Exception) { }
                });
                t.IsBackground = true;
                t.Start();
                Thread.Sleep(400);
                Check(joined.Get() == 0, "cap: the 17th client waits while 16 are connected");
                Stopwatch sw = Stopwatch.StartNew();
                ps[0].Dispose();
                ps.RemoveAt(0);
                while (joined.Get() == 0 && sw.ElapsedMilliseconds < 3000) Thread.Sleep(5);
                long took = sw.ElapsedMilliseconds;
                Check(joined.Get() == 1 && took < 1000, "cap: the 17th connects within 1 s of one leaving (" + took + " ms)");
                if (extra != null)
                {
                    RawSend(extra, GlassProtocolTest.Frame(GlassProtocolTest.TypeHello, 1, GlassProtocolTest.HelloPayload("LGBench raw 17")));
                    int n = RawRead(extra, buf, 2000);
                    Check(n > 0 && GlassProtocolTest.ReplyType(buf, n) == GlassProtocolTest.TypeHelloAck, "cap: the 17th is served");
                }
            }
            finally
            {
                foreach (NamedPipeClientStream p in ps) p.Dispose();
                if (extra != null) extra.Dispose();
            }
            Pump(3000, delegate { return host.ClientCount == 0; });
        }

        // A burst of 2000 SetLook in about a second from a real client, then 700 raw ones: the surface stays shown, the host ends on
        // the last look sent, nobody reconnects.
        static void BurstCheck(GlassHostServer host, string suffix)
        {
            GlassService svc = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = suffix });
            try
            {
                Count shown = new Count(), hidden = new Count(), lost = new Count(), back = new Count(), failed = new Count();
                svc.Lost += delegate { lost.Inc(); };
                svc.ServiceRestarted += delegate { back.Inc(); };
                string label = Title + " burst " + Process.GetCurrentProcess().Id;
                GlassSpec spec = Spec(label);
                GlassSurface s = svc.Create(spec);
                s.Shown += delegate { shown.Inc(); };
                s.Hidden += delegate { hidden.Inc(); };
                s.Failed += delegate(object o, GlassFailedEventArgs e) { failed.Inc(); Program.Say("contract: burst Failed {0} {1}", e.Code, e.Text); };
                s.SetContents(Picture(spec.Size.Width, spec.Size.Height, 40), spec.Size.Width, spec.Size.Height, Rectangle.Empty);
                s.Show();
                Check(Pump(3000, delegate { return shown.Get() > 0; }), "burst: Shown before the burst");
                Stopwatch sw = Stopwatch.StartNew();
                for (int i = 0; i < 2000; i++)
                {
                    s.SetLook(GlassProtocolTest.LookFor(i), "");
                    while (sw.ElapsedTicks * 1000.0 / Stopwatch.Frequency < i * 0.5) Thread.Sleep(0);
                }
                long ms = sw.ElapsedMilliseconds;
                GlassLook last = GlassProtocolTest.LookFor(1999);
                bool same = Pump(3000, delegate { GlassLook g; return host.TryGetLook(label, out g) && SameLook(g, last); });
                Check(same, "burst: after 2000 SetLook in " + ms + " ms the host holds the last look");
                Check(shown.Get() == 1 && hidden.Get() == 0 && !s.HasFailed && failed.Get() == 0, "burst: the surface stays shown (" + shown.Get() + " Shown, " + hidden.Get() + " Hidden, failed " + failed.Get() + ")");
                Check(lost.Get() == 0 && back.Get() == 0 && svc.HostConnected, "burst: no reconnect");
                s.Dispose();
            }
            finally { svc.Dispose(); }

            // the same, raw: 700 SetLook frames at once are more than the host's bucket holds; the newest still arrives
            byte[] buf = new byte[70000];
            using (NamedPipeClientStream p = RawHello(host, buf))
            {
                Check(p != null, "burst: raw client said Hello");
                if (p == null) return;
                string label = Title + " raw burst " + Process.GetCurrentProcess().Id;
                RawSend(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeCreate, 2, GlassProtocolTest.CreatePayload(1, label, 200, 80, "")));
                int n = RawRead(p, buf, 2000);
                uint id = GlassProtocolTest.AckSurface(buf, n);
                Check(id != 0, "burst: raw Create acknowledged");
                for (int i = 0; i < 700; i++) RawSend(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeSetLook, (uint)(10 + i), GlassProtocolTest.SetLookPayload(id, 1300 + i)));
                GlassLook want = GlassProtocolTest.LookFor(1999);
                bool ok = Pump(4000, delegate { GlassLook g; return host.TryGetLook(label, out g) && SameLook(g, want); });
                Check(ok, "burst: 700 raw SetLook over the rate: the host ends on the last one");
                int errs = 0;
                while ((n = RawRead(p, buf, 300)) > 0) if (GlassProtocolTest.ReplyType(buf, n) == GlassProtocolTest.TypeError) errs++;
                Check(errs == 0, "burst: no Error for the throttled looks (" + errs + ")");
            }
        }

        // A resize over the quota is answered with an Error (and the surface keeps its section); a normal resize and contents work after.
        static void ResizeQuotaCheck(GlassHostServer host)
        {
            byte[] buf = new byte[70000];
            using (NamedPipeClientStream p = RawHello(host, buf))
            {
                Check(p != null, "resize quota: raw client said Hello");
                if (p == null) return;
                RawSend(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeCreate, 2, GlassProtocolTest.CreatePayload(1, Title + " raw quota", 200, 80, "")));
                int n = RawRead(p, buf, 2000);
                uint id = GlassProtocolTest.AckSurface(buf, n);
                Check(id != 0, "resize quota: Create acknowledged");
                // 4096 x 4096 counts 335 MB (section, mirror, contents) against 256 MB
                Check(ExpectError(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeResize, 3, GlassProtocolTest.ResizePayload(id, 4096, 4096)), 6, buf), "resize quota: a resize over the quota gets Error Quota");
                RawSend(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeResize, 4, GlassProtocolTest.ResizePayload(id, 300, 100)));
                n = RawRead(p, buf, 2000);
                Check(n > 0 && GlassProtocolTest.ReplyType(buf, n) == GlassProtocolTest.TypeCreateAck && GlassProtocolTest.AckSurface(buf, n) == id, "resize quota: a normal resize afterwards is acknowledged");
                if (n <= 0) return;
                Check(GlassProtocolTest.ShowFromAck(buf, n, host.WakeName), "resize quota: the new section maps and takes contents");
                bool shown = false;
                for (int i = 0; i < 6 && !shown; i++)
                {
                    n = RawRead(p, buf, 3000);
                    if (n <= 0) break;
                    shown = GlassProtocolTest.ReplyType(buf, n) == GlassProtocolTest.TypeShown;
                }
                Check(shown, "resize quota: contents work after (Shown arrives)");
            }
        }

        // g) A real client's SetSize over the quota: RequestRejected once, no Failed, the surface stays shown, and a normal SetSize +
        // SetContents work afterwards.
        static void QuotaRejectCheck(string suffix)
        {
            GlassService svc = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = suffix });
            try
            {
                Count shown = new Count(), hidden = new Count(), failed = new Count(), rejected = new Count();
                GlassSpec spec = Spec(Title + " reject " + Process.GetCurrentProcess().Id);
                GlassSurface s = svc.Create(spec);
                s.Shown += delegate { shown.Inc(); };
                s.Hidden += delegate { hidden.Inc(); };
                s.Failed += delegate(object o, GlassFailedEventArgs e) { failed.Inc(); Program.Say("contract: reject Failed {0} {1}", e.Code, e.Text); };
                s.RequestRejected += delegate(object o, GlassFailedEventArgs e) { rejected.Inc(); };
                s.SetContents(Picture(spec.Size.Width, spec.Size.Height, 40), spec.Size.Width, spec.Size.Height, Rectangle.Empty);
                s.Show();
                Check(Pump(3000, delegate { return shown.Get() > 0; }), "reject: Shown before the oversize SetSize");
                s.SetSize(new Size(4096, 3600));   // (about 295 MB against the 256 MB quota)
                Check(Pump(4000, delegate { return rejected.Get() > 0; }), "reject: RequestRejected raised for a SetSize over the quota");
                Pump(400, null);
                Check(rejected.Get() == 1, "reject: RequestRejected raised once (" + rejected.Get() + ")");
                Check(failed.Get() == 0 && !s.HasFailed, "reject: Failed not raised (" + failed.Get() + ")");
                Check(hidden.Get() == 0 && shown.Get() == 1 && svc.HostConnected, "reject: the surface stays Shown (" + shown.Get() + " Shown, " + hidden.Get() + " Hidden)");
                s.SetSize(new Size(300, 100));
                s.SetContents(Picture(300, 100, 40), 300, 100, Rectangle.Empty);
                int f0 = s.Frames;
                Check(Pump(4000, delegate { return s.Frames > f0; }), "reject: a normal SetSize + SetContents afterwards reach the screen path");
                Pump(300, null);
                Check(rejected.Get() == 1 && failed.Get() == 0 && !s.HasFailed && hidden.Get() == 0, "reject: nothing more was raised after them (rejected " + rejected.Get() + ", failed " + failed.Get() + ")");
                s.Dispose();
            }
            finally { svc.Dispose(); }
        }

        // h) 200 SetLook frames naming an unknown surface in about 100 ms: the session stays connected, the Errors are throttled, and the
        // next valid call works.
        static void BadIdFloodCheck(GlassHostServer host)
        {
            byte[] buf = new byte[70000];
            using (NamedPipeClientStream p = RawHello(host, buf))
            {
                Check(p != null, "bad-id flood: raw client said Hello");
                if (p == null) return;
                string label = Title + " raw flood " + Process.GetCurrentProcess().Id;
                RawSend(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeCreate, 2, GlassProtocolTest.CreatePayload(1, label, 200, 80, "")));
                int n = RawRead(p, buf, 2000);
                uint id = GlassProtocolTest.AckSurface(buf, n);
                Check(id != 0, "bad-id flood: raw Create acknowledged");
                Stopwatch sw = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++) RawSend(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeSetLook, (uint)(10 + i), GlassProtocolTest.SetLookPayload(id + 5000, 1300 + i)));
                long ms = sw.ElapsedMilliseconds;
                GlassLook want = GlassProtocolTest.LookFor(1999);
                RawSend(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeSetLook, 400, GlassProtocolTest.SetLookPayload(id, 1999)));
                bool ok = Pump(4000, delegate { GlassLook g; return host.TryGetLook(label, out g) && SameLook(g, want); });
                Check(ok, "bad-id flood: the session stays connected and the next valid SetLook works (200 bad ones in " + ms + " ms)");
                int errs = 0;
                while ((n = RawRead(p, buf, 300)) > 0) if (GlassProtocolTest.ReplyType(buf, n) == GlassProtocolTest.TypeError) errs++;
                Check(n != 0 && errs <= 12, "bad-id flood: the Errors were throttled (" + errs + " of 200, pipe " + (n == 0 ? "ended" : "open") + ")");
            }
        }

        // i) A client over the cap gets Error "busy" and the close (not a silent drop), and a real client treats that as retryable: it
        // connects within 1 s after a slot frees. (The host's cap is lowered to 2, so that the pipe still has an instance to meet.)
        static void BusyCheck()
        {
            string suffix = "lbb" + Process.GetCurrentProcess().Id;
            GlassHostServer h = new GlassHostServer();
            h.ClientCap = 2;
            h.Start(suffix);
            byte[] buf = new byte[70000];
            NamedPipeClientStream p1 = null, p2 = null, p3 = null;
            GlassService svc = null;
            try
            {
                p1 = RawHello(h, buf);
                p2 = RawHello(h, buf);
                Check(p1 != null && p2 != null, "busy: two clients fill the cap");
                p3 = Raw(h);
                RawSend(p3, GlassProtocolTest.Frame(GlassProtocolTest.TypeHello, 1, GlassProtocolTest.HelloPayload("LGBench raw 3")));
                int n = RawRead(p3, buf, 2000);
                bool busy = n > 16 && GlassProtocolTest.ReplyType(buf, n) == GlassProtocolTest.TypeError && Encoding.ASCII.GetString(buf, 0, n).Contains("busy");
                Check(busy, "busy: the client over the cap gets Error busy (read " + n + ")");
                Check(RawRead(p3, buf, 1000) == 0, "busy: and then the close");
                Count made = new Count();
                Thread t = new Thread(delegate()
                {
                    svc = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = suffix });
                    made.Inc();
                });
                t.IsBackground = true;
                t.Start();
                Thread.Sleep(400);
                Check(made.Get() == 0, "busy: a real client waits while the host is full");
                Stopwatch sw = Stopwatch.StartNew();
                p1.Dispose();
                p1 = null;
                bool up = Pump(3000, delegate { return made.Get() > 0 && svc != null && svc.HostConnected; });
                long took = sw.ElapsedMilliseconds;
                Check(up && took < 1000, "busy: the real client connects within 1 s of a slot freeing (" + took + " ms)");
            }
            finally
            {
                if (svc != null) svc.Dispose();
                if (p1 != null) p1.Dispose();
                if (p2 != null) p2.Dispose();
                if (p3 != null) p3.Dispose();
                h.Stop();
            }
        }

        // Dispose the service from inside its own Lost handler and from inside a surface event (the thread has no
        // SynchronizationContext, so both run on the I/O thread); a Lost handler that outlasts a Dispose from another thread.
        // None may throw, the threads end, and a new service connects fine afterwards.
        static void DisposeInHandlerChecks()
        {
            int th0 = GlassPipeStats.LiveThreads;
            string suffix = "lbd" + Process.GetCurrentProcess().Id;
            GlassHostServer h = new GlassHostServer();
            h.Start(suffix);
            Count lostRan = new Count(), shownRan = new Count(), slowStarted = new Count(), slowEnded = new Count();
            Exception caught = null;
            GlassService svcA = null, svcB = null, svcC = null;
            Thread t = new Thread(delegate()
            {
                svcA = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = suffix });
                svcA.Lost += delegate
                {
                    try { svcA.Dispose(); lostRan.Inc(); }
                    catch (Exception ex) { caught = ex; }
                };
                svcB = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = suffix });
                GlassSurface sb = svcB.Create(Spec(Title + " dispose in Shown"));
                sb.Shown += delegate
                {
                    try { svcB.Dispose(); shownRan.Inc(); }
                    catch (Exception ex) { caught = ex; }
                };
                sb.SetContents(Picture(360, 120, 40), 360, 120, Rectangle.Empty);
                sb.Show();
                svcC = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = suffix });
                svcC.Lost += delegate { slowStarted.Inc(); Thread.Sleep(4000); slowEnded.Inc(); };
            });
            t.Start();
            t.Join();
            Check(Pump(5000, delegate { return shownRan.Get() > 0; }), "dispose in handlers: the service was disposed from inside a surface event");
            Check(svcA != null && svcA.HostConnected && svcC != null && svcC.HostConnected, "dispose in handlers: the other services are connected");
            h.Stop();
            Check(Pump(5000, delegate { return lostRan.Get() > 0 && slowStarted.Get() > 0; }), "dispose in handlers: Lost ran on both services");
            // svcC's Lost handler is asleep on its I/O thread: Dispose from here cannot join it (3 s), and must not break it
            Stopwatch sw = Stopwatch.StartNew();
            try { svcC.Dispose(); Check(true, "dispose in handlers: Dispose while a Lost handler blocks does not throw (" + sw.ElapsedMilliseconds + " ms)"); }
            catch (Exception ex) { Check(false, "dispose in handlers: Dispose while a Lost handler blocks threw " + ex.GetType().Name); }
            Check(Pump(6000, delegate { return slowEnded.Get() > 0; }), "dispose in handlers: the blocked handler returned");
            Check(Pump(4000, delegate { return GlassPipeStats.LiveThreads == th0; }), "dispose in handlers: all threads ended (" + (GlassPipeStats.LiveThreads - th0) + " left)");
            Check(caught == null, "dispose in handlers: nothing was thrown inside the handlers" + (caught == null ? "" : " (" + caught.GetType().Name + ")"));
            // the process is alive, and a new service on a new host connects fine
            h.Start(suffix);
            GlassService again = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = suffix });
            try
            {
                Count shown = new Count();
                GlassSurface s = again.Create(Spec(Title + " after dispose in handlers"));
                s.Shown += delegate { shown.Inc(); };
                s.SetContents(Picture(360, 120, 40), 360, 120, Rectangle.Empty);
                s.Show();
                Check(again.HostConnected && Pump(3000, delegate { return shown.Get() > 0; }), "dispose in handlers: a new service connects and shows a surface");
            }
            finally
            {
                again.Dispose();
                h.Stop();
            }
        }

        // The values the host rejects are an ArgumentException on the client, in every mode.
        static void RangeChecks(GlassService svc, string name)
        {
            GlassSpec ok = Spec(Title + " range");
            GlassSurface s = svc.Create(ok);
            try { s.SetLook(ok.Look, new string('a', 2401)); Check(false, name + ": a 2401-character profile in SetLook"); }
            catch (ArgumentException) { Check(true, name + ": a 2401-character profile in SetLook is an ArgumentException"); }
            Check(!s.HasFailed, name + ": the surface did not fail on it");
            // (the Elevation setter clamps, so a value outside 0..100 can only get in through the stored field: what a raw struct copy would do)
            System.Reflection.FieldInfo ef = typeof(GlassLook).GetField("elevationPlus50", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object box = ok.Look;
            ef.SetValue(box, -51);
            GlassLook lo = (GlassLook)box;
            try { s.SetLook(lo, ""); Check(false, name + ": elevation -1"); }
            catch (ArgumentException) { Check(true, name + ": elevation -1 in SetLook is an ArgumentException"); }
            ef.SetValue(box, 51);
            lo = (GlassLook)box;
            try { s.SetLook(lo, ""); Check(false, name + ": elevation 101"); }
            catch (ArgumentException) { Check(true, name + ": elevation 101 in SetLook is an ArgumentException"); }
            try { s.SetPlace(new Point(-200000, 0), false); Check(false, name + ": place -200000"); }
            catch (ArgumentException) { Check(true, name + ": SetPlace(-200000, 0) is an ArgumentException"); }
            Check(!s.HasFailed, name + ": the surface did not fail on them");
            s.Dispose();
            GlassSpec bad = Spec(Title + " range scale");
            bad.Scale = 0.005f;
            try { svc.Create(bad); Check(false, name + ": scale 0.005"); }
            catch (ArgumentException) { Check(true, name + ": scale 0.005 is an ArgumentException"); }
            bad = Spec(Title + " range scale 9");
            bad.Scale = 9f;
            try { svc.Create(bad); Check(false, name + ": scale 9"); }
            catch (ArgumentException) { Check(true, name + ": scale 9 is an ArgumentException"); }
            bad = Spec(Title + " " + new string('x', 300));
            try { svc.Create(bad); Check(false, name + ": a 300-byte label"); }
            catch (ArgumentException) { Check(true, name + ": a label over 256 bytes is an ArgumentException"); }
            bad = Spec(Title + " range profile");
            bad.Profile = new string('a', 2401);
            try { svc.Create(bad); Check(false, name + ": a 2401-character profile in the spec"); }
            catch (ArgumentException) { Check(true, name + ": a 2401-character profile in the spec is an ArgumentException"); }
            bad = Spec(Title + " range place");
            bad.Place = new Point(0, 100001);
            try { svc.Create(bad); Check(false, name + ": place y 100001 in the spec"); }
            catch (ArgumentException) { Check(true, name + ": a place outside +-100000 in the spec is an ArgumentException"); }
        }

        // A client that dies without a word (its pipe handle just closes): its surface's window and section go.
        static void DeathCheck(GlassHostServer host)
        {
            byte[] buf = new byte[70000];
            int w0 = TestWindows(), sec0 = GlassPipeStats.LiveSections;
            NamedPipeClientStream p = Raw(host);
            RawSend(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeHello, 1, GlassProtocolTest.HelloPayload("LGBench raw")));
            RawRead(p, buf, 2000);
            RawSend(p, GlassProtocolTest.Frame(GlassProtocolTest.TypeCreate, 2, GlassProtocolTest.CreatePayload(1, Title + " raw death", 200, 80, "")));
            int n = RawRead(p, buf, 2000);
            Check(n > 0 && GlassProtocolTest.ReplyType(buf, n) == GlassProtocolTest.TypeCreateAck, "death: the raw client's Create is acknowledged");
            Check(Pump(2000, delegate { return TestWindows() == w0 + 1; }), "death: its window exists (" + (TestWindows() - w0) + ")");
            Check(GlassPipeStats.LiveSections == sec0 + 1, "death: its section exists (" + (GlassPipeStats.LiveSections - sec0) + ")");
            p.Dispose();
            Check(Pump(3000, delegate { return TestWindows() == w0 && GlassPipeStats.LiveSections == sec0; }), "death: its window and section went when it died (" + (TestWindows() - w0) + " windows, " + (GlassPipeStats.LiveSections - sec0) + " sections left)");
        }

        // The host stops and starts again: the surface is Lost, then created again from what the client kept.
        static void RestartCheck(GlassHostServer host, string suffix)
        {
            GlassService svc = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = suffix });
            try
            {
                Count shown = new Count(), lost = new Count(), back = new Count(), failed = new Count();
                svc.Lost += delegate { lost.Inc(); };
                svc.ServiceRestarted += delegate { back.Inc(); };
                GlassSpec spec = Spec(Title + " restart");
                GlassSurface s = svc.Create(spec);
                s.Shown += delegate { shown.Inc(); };
                s.Failed += delegate(object o, GlassFailedEventArgs e) { failed.Inc(); Program.Say("contract: restart Failed {0} {1}", e.Code, e.Text); };
                s.SetContents(Picture(spec.Size.Width, spec.Size.Height, 40), spec.Size.Width, spec.Size.Height, Rectangle.Empty);
                s.Show();
                Check(Pump(3000, delegate { return shown.Get() > 0; }), "restart: Shown before the host stops");
                host.Stop();
                Check(Pump(5000, delegate { return lost.Get() > 0; }), "restart: Lost raised when the host stops");
                Check(!svc.HostConnected && !s.HasFailed, "restart: the service waits, the surface has not failed");
                s.SetOpacity(0.7f);
                s.SetContents(Picture(spec.Size.Width, spec.Size.Height, 60), spec.Size.Width, spec.Size.Height, Rectangle.Empty);
                host.Start(suffix);
                Check(Pump(10000, delegate { return back.Get() > 0; }), "restart: ServiceRestarted raised after the host starts again");
                int s1 = shown.Get();
                Check(Pump(5000, delegate { return shown.Get() > s1; }), "restart: the surface is shown again by the new host");
                Check(svc.HostConnected && !s.HasFailed && failed.Get() == 0, "restart: connected, no failure");
                s.Dispose();
            }
            finally { svc.Dispose(); }
        }

        // Start-on-demand through the HostStarter seam (a GlassHostServer in this process stands in for the host process): no host ->
        // the seam starts one -> Shown; the server stops -> Lost -> the seam starts one again -> ServiceRestarted -> the surface is
        // back with its last look.
        static void StartOnDemandChecks()
        {
            string suffix = "lbs" + Process.GetCurrentProcess().Id;
            GlassHostServer[] srv = new GlassHostServer[1];
            Count calls = new Count();
            GlassService.HostStarter = delegate(string path, string sfx)
            {
                if (sfx != suffix) return false;
                calls.Inc();
                GlassHostServer h = new GlassHostServer();
                h.Start(sfx);
                srv[0] = h;
                return true;
            };
            GlassService svc = null;
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                svc = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = suffix, HostPath = @"C:\LGBench\no\LGEngineHost.exe" });
                Check(calls.Get() == 1 && svc.HostConnected && svc.Transport == "host pipe", "start on demand: no host, the starter ran once and the service is connected (" + calls.Get() + " starts, " + sw.ElapsedMilliseconds + " ms)");
                Count shown = new Count(), lost = new Count(), back = new Count(), failed = new Count();
                svc.Lost += delegate { lost.Inc(); };
                svc.ServiceRestarted += delegate { back.Inc(); };
                GlassSpec spec = Spec(Title + " start on demand");
                GlassSurface s = svc.Create(spec);
                s.Shown += delegate { shown.Inc(); };
                s.Failed += delegate(object o, GlassFailedEventArgs e) { failed.Inc(); Program.Say("contract: start on demand Failed {0} {1}", e.Code, e.Text); };
                GlassLook want = GlassProtocolTest.LookFor(1500);
                s.SetLook(want, "");
                s.SetContents(Picture(spec.Size.Width, spec.Size.Height, 40), spec.Size.Width, spec.Size.Height, Rectangle.Empty);
                s.Show();
                Check(Pump(3000, delegate { return shown.Get() > 0; }), "start on demand: Shown by the started host");
                GlassHostServer first = srv[0];
                first.Stop();
                Check(Pump(5000, delegate { return lost.Get() > 0; }), "start on demand: Lost raised when the started host stops");
                int s1 = shown.Get();
                Check(Pump(10000, delegate { return back.Get() > 0; }), "start on demand: ServiceRestarted after the reconnect started a host again");
                Check(calls.Get() == 2 && srv[0] != first, "start on demand: the starter ran again for the reconnect (" + calls.Get() + " starts)");
                Check(Pump(5000, delegate { return shown.Get() > s1; }), "start on demand: the surface is shown again");
                GlassLook got;
                Check(srv[0].TryGetLook(spec.Label, out got) && SameLook(got, want), "start on demand: the new host has the surface's last look");
                Check(svc.HostConnected && !s.HasFailed && failed.Get() == 0, "start on demand: connected, no failure");
                s.Dispose();
            }
            finally
            {
                GlassService.HostStarter = null;
                if (svc != null) svc.Dispose();
                if (srv[0] != null) srv[0].Stop();
            }
        }

        // No host and no exe at HostPath: Auto falls back to the in-process render thread, Host mode goes offline.
        static void MissingHostChecks()
        {
            GlassService.HostStarter = null;
            string suffix = "lbm" + Process.GetCurrentProcess().Id;
            string bad = @"C:\LGBench\no\LGEngineHost.exe";
            GlassService a = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Auto, HostSuffix = suffix, HostPath = bad });
            try
            {
                Check(a.ModeUsed == GlassMode.InProcess && a.Transport == "render thread (host unavailable)" && a.EngineThreadAlive, "missing host: Auto falls back to the render thread (" + a.ModeUsed + ", " + a.Transport + ")");
                Count shown = new Count();
                GlassSurface s = a.Create(Spec(Title + " auto fallback"));
                s.Shown += delegate { shown.Inc(); };
                s.SetContents(Picture(360, 120, 40), 360, 120, Rectangle.Empty);
                s.Show();
                Check(Pump(3000, delegate { return shown.Get() > 0; }), "missing host: the fallback surface is shown");
                s.Dispose();
            }
            finally { a.Dispose(); }
            GlassService h = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = suffix, HostPath = bad });
            try
            {
                Check(h.ModeUsed == GlassMode.Host && h.Transport == "host pipe" && !h.HostConnected, "missing host: Host mode is offline (" + h.ModeUsed + ", " + h.Transport + ")");
                Count failed = new Count();
                string text = "";
                GlassSurface s = h.Create(Spec(Title + " host missing"));
                s.Failed += delegate(object o, GlassFailedEventArgs e) { text = e.Text; failed.Inc(); };
                Check(Pump(1000, delegate { return failed.Get() > 0; }) && text.IndexOf("host unavailable", StringComparison.Ordinal) >= 0, "missing host: Host mode surface Failed 'host unavailable' (" + text + ")");
                s.Dispose();
            }
            finally { h.Dispose(); }
        }

        // A client that dies without Dispose (its pipe just closes): the host retires its surfaces within 2 s.
        static void ClientDeathCheck()
        {
            string suffix = "lbk" + Process.GetCurrentProcess().Id;
            GlassHostServer h = new GlassHostServer();
            h.Start(suffix);
            GlassService svc = null;
            try
            {
                int w0 = TestWindows(), sec0 = GlassPipeStats.LiveSections;
                svc = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = suffix });
                Count shown = new Count();
                GlassSurface s = svc.Create(Spec(Title + " client death"));
                s.Shown += delegate { shown.Inc(); };
                s.SetContents(Picture(360, 120, 40), 360, 120, Rectangle.Empty);
                s.Show();
                Check(Pump(3000, delegate { return shown.Get() > 0; }) && TestWindows() == w0 + 1, "client death: the surface is shown (" + (TestWindows() - w0) + " windows)");
                svc.AbandonForTest();
                Stopwatch sw = Stopwatch.StartNew();
                bool gone = Pump(2000, delegate { return TestWindows() == w0; });
                Check(gone, "client death: its surface is retired within 2 s (" + sw.ElapsedMilliseconds + " ms, " + (TestWindows() - w0) + " windows left)");
            }
            finally
            {
                if (svc != null) svc.Dispose();
                h.Stop();
            }
        }

        // ---- -contract -host: the INSTALLED LGEngineHost.exe, started on demand ----

        // The installed host: env LGENGINE_HOST, else LGEngineHost.exe beside a running LiquidGlass process's exe, else beside this exe.
        static string FindInstalledHost()
        {
            string env = Environment.GetEnvironmentVariable("LGENGINE_HOST");
            if (!string.IsNullOrEmpty(env)) return File.Exists(env) ? env : null;
            try
            {
                foreach (Process p in Process.GetProcessesByName("LiquidGlass"))
                {
                    string dir = null;
                    try { dir = Path.GetDirectoryName(p.MainModule.FileName); }
                    catch (Exception) { }   // (another user's process, or access denied)
                    finally { p.Dispose(); }
                    if (string.IsNullOrEmpty(dir)) continue;
                    string cand = Path.Combine(dir, "LGEngineHost.exe");
                    if (File.Exists(cand)) return cand;
                }
            }
            catch (Exception) { }
            try
            {
                string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LGEngineHost.exe");
                if (File.Exists(beside)) return beside;
            }
            catch (Exception) { }
            return null;
        }

        static Process NewHostProcess(HashSet<int> before, int ms)
        {
            Stopwatch sw = Stopwatch.StartNew();
            do
            {
                foreach (Process p in Process.GetProcessesByName("LGEngineHost"))
                {
                    if (!before.Contains(p.Id)) return p;
                    p.Dispose();
                }
                Thread.Sleep(50);
            } while (sw.ElapsedMilliseconds < ms);
            return null;
        }

        static void RunHostProcess()
        {
            string InstalledHost = FindInstalledHost();
            if (InstalledHost == null)
            {
                Program.Say("contract [host process]: skipped (no installed host)");
                return;
            }
            HashSet<int> before = new HashSet<int>();
            foreach (Process p in Process.GetProcessesByName("LGEngineHost")) { before.Add(p.Id); p.Dispose(); }
            string suffix = "hp" + Process.GetCurrentProcess().Id;
            runHostPath = InstalledHost;
            Process started = null, again = null;
            try
            {
                RunMode(GlassMode.Host, "host process", "host pipe", suffix);
                started = NewHostProcess(before, 3000);
                Check(started != null, "host process: the client started LGEngineHost.exe on demand");

                // kill test: the host dies under a live surface -> Lost -> started again -> ServiceRestarted -> the surface is back
                GlassService svc = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = suffix, HostPath = InstalledHost });
                try
                {
                    Count shown = new Count(), lost = new Count(), back = new Count(), failed = new Count();
                    svc.Lost += delegate { lost.Inc(); };
                    svc.ServiceRestarted += delegate { back.Inc(); };
                    GlassSpec spec = Spec(Title + " host kill");
                    GlassSurface s = svc.Create(spec);
                    s.Shown += delegate { shown.Inc(); };
                    s.Failed += delegate(object o, GlassFailedEventArgs e) { failed.Inc(); Program.Say("contract: host kill Failed {0} {1}", e.Code, e.Text); };
                    s.SetOpacity(0.7f);
                    s.SetContents(Picture(spec.Size.Width, spec.Size.Height, 40), spec.Size.Width, spec.Size.Height, Rectangle.Empty);
                    s.Show();
                    Check(Pump(5000, delegate { return shown.Get() > 0; }), "host kill: Shown before the kill");
                    Process victim = started != null && !started.HasExited ? started : NewHostProcess(before, 1000);
                    Check(victim != null, "host kill: the host process is found");
                    if (victim != null)
                    {
                        try { victim.Kill(); victim.WaitForExit(3000); }
                        catch (Exception) { }
                    }
                    int s1 = shown.Get();
                    Check(Pump(5000, delegate { return lost.Get() > 0; }), "host kill: Lost raised");
                    Check(Pump(10000, delegate { return back.Get() > 0; }), "host kill: ServiceRestarted after the host was started again");
                    Check(Pump(5000, delegate { return shown.Get() > s1; }), "host kill: the surface is shown again");
                    Check(svc.HostConnected && !s.HasFailed && failed.Get() == 0, "host kill: connected, no failure");
                    again = NewHostProcess(before, 1000);
                    if (again != null && victim != null && again.Id == victim.Id) { again.Dispose(); again = null; }
                    if (again == null)
                    {
                        foreach (Process p in Process.GetProcessesByName("LGEngineHost"))
                        {
                            if (!before.Contains(p.Id) && (victim == null || p.Id != victim.Id)) { again = p; break; }
                            p.Dispose();
                        }
                    }
                    Check(again != null, "host kill: a new host process runs");
                    s.Dispose();
                }
                finally { svc.Dispose(); }
                if (again != null)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    bool exited = Pump(12000, delegate { return again.HasExited; });
                    Check(exited, "host process: the host exits within 12 s of the last client leaving (" + sw.ElapsedMilliseconds + " ms)");
                }
            }
            finally
            {
                runHostPath = null;
                try { if (again != null && !again.HasExited) again.Kill(); }   // (only a host this test started)
                catch (Exception) { }
            }
        }

        static void RunLoopback()
        {
            int w0 = TestWindows(), sec0 = GlassPipeStats.LiveSections, ses0 = GlassPipeStats.LiveSessions, th0 = GlassPipeStats.LiveThreads;
            string suffix = "lb" + Process.GetCurrentProcess().Id;
            GlassHostServer host = new GlassHostServer();
            host.Start(suffix);
            try
            {
                GlassHostServer second = new GlassHostServer();
                try { second.Start(suffix); Check(false, "a second host on the same names was accepted"); second.Stop(); }
                catch (Exception) { Check(true, "a second host on the same names is refused"); }
                RunMode(GlassMode.Host, "loopback pipe", "host pipe", suffix);
                ThreadChecks(suffix);
                MalformedChecks(host);
                HelloTimeoutCheck(host);
                CapCheck(host);
                BusyCheck();
                BurstCheck(host, suffix);
                BadIdFloodCheck(host);
                ResizeQuotaCheck(host);
                QuotaRejectCheck(suffix);
                DeathCheck(host);
                RestartCheck(host, suffix);
            }
            finally
            {
                host.Stop();
            }
            DisposeInHandlerChecks();
            StartOnDemandChecks();
            MissingHostChecks();
            ClientDeathCheck();
            WaitCpuCheck();
            StartBackoffCheck();
            ContextChecks();
            RelativePathCheck();
            Check(Pump(4000, delegate { return TestWindows() == w0 && GlassPipeStats.LiveSections == sec0 && GlassPipeStats.LiveSessions == ses0 && GlassPipeStats.LiveThreads == th0; }),
                "loopback leaves nothing behind: windows " + (TestWindows() - w0) + ", sections " + (GlassPipeStats.LiveSections - sec0) + ", sessions " + (GlassPipeStats.LiveSessions - ses0) + ", threads " + (GlassPipeStats.LiveThreads - th0) + " over the start");
            bool pipeGone = false, wakeGone = false;
            try
            {
                using (NamedPipeClientStream p = new NamedPipeClientStream(".", host.PipeName.Substring(@"\\.\pipe\".Length), PipeDirection.InOut)) p.Connect(300);
            }
            catch (Exception) { pipeGone = true; }
            try { EventWaitHandle.OpenExisting(host.WakeName).Dispose(); }
            catch (Exception) { wakeGone = true; }
            Check(pipeGone, "the pipe is gone after Stop");
            Check(wakeGone, "the wake event is gone after Stop");
        }

        // ---- the phase 2b fixes ----

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        static extern bool GetThreadTimes(IntPtr thread, out long create, out long exit, out long kernel, out long user);

        // This thread's CPU time (kernel + user) in ms.
        static double ThreadCpuMs()
        {
            long c, e, k, u;
            GetThreadTimes(GetCurrentThread(), out c, out e, out k, out u);
            return (k + u) / 10000.0;
        }

        // A 2 s wait for a pipe that does not exist costs (almost) no CPU: no busy-spin in NamedPipeClientStream.Connect(timeout).
        static void WaitCpuCheck()
        {
            string name = GlassNames.PipeShort("lbw" + Process.GetCurrentProcess().Id);
            double c0 = ThreadCpuMs();
            Stopwatch sw = Stopwatch.StartNew();
            NamedPipeClientStream p = PipeTransport.ConnectLoop(name, 2000, null);
            double cpu = ThreadCpuMs() - c0;
            if (p != null) p.Dispose();
            Check(p == null && sw.ElapsedMilliseconds >= 1900 && cpu < 100, "wait for a missing pipe: 2 s cost " + cpu.ToString("0") + " ms of CPU (" + sw.ElapsedMilliseconds + " ms waited)");
        }

        // A host that cannot be started: the starter is asked at most 4 times in the first 10 s, and the delay between starts grows.
        static void StartBackoffCheck()
        {
            string suffix = "lbb" + Process.GetCurrentProcess().Id;
            GlassHostServer h = new GlassHostServer();
            h.Start(suffix);
            Count calls = new Count();
            GlassService.HostStarter = delegate(string path, string sfx)
            {
                if (sfx == suffix) calls.Inc();
                return false;
            };
            GlassService svc = null;
            try
            {
                svc = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = suffix, HostPath = @"C:\LGBench\no\LGEngineHost.exe" });
                Check(svc.HostConnected && calls.Get() == 0, "start backoff: connected to the running host without a start");
                h.Stop();
                Pump(10000, null);
                int n = calls.Get();
                Check(n >= 3 && n <= 4, "start backoff: " + n + " starts in the first 10 s of a lost host (3 or 4)");
                Random rng = new Random(7);
                int d2 = PipeTransport.StartDelayMs(2, rng), d3 = PipeTransport.StartDelayMs(3, rng), d4 = PipeTransport.StartDelayMs(4, rng), d5 = PipeTransport.StartDelayMs(5, rng), d9 = PipeTransport.StartDelayMs(9, rng);
                Check(d2 == 0 && d3 >= 4000 && d3 <= 5000 && d4 > d3 && d5 > d4 && d9 >= 60000 && d9 <= 75000, "start backoff grows: " + d3 + ", " + d4 + ", " + d5 + " ms ... " + d9 + " ms at most 60 s + 25%");
            }
            finally
            {
                GlassService.HostStarter = null;
                if (svc != null) svc.Dispose();
                h.Stop();
            }
        }

        // A SynchronizationContext that runs a Post at once, on the posting thread, and says so.
        sealed class TestContext : SynchronizationContext
        {
            [ThreadStatic]
            static bool inPost;
            public static bool InPost { get { return inPost; } }
            public int Posts;
            public override void Post(SendOrPostCallback d, object state)
            {
                Interlocked.Increment(ref Posts);
                inPost = true;
                try { d(state); }
                finally { inPost = false; }
            }
        }

        // Lost and ServiceRestarted are raised on the context that was current at Connect; ServiceRestarted only once the surface is back.
        static void ContextChecks()
        {
            string suffix = "lbx" + Process.GetCurrentProcess().Id;
            SynchronizationContext prev = SynchronizationContext.Current;
            TestContext ctx = new TestContext();
            SynchronizationContext.SetSynchronizationContext(ctx);
            GlassHostServer h = new GlassHostServer();
            h.Start(suffix);
            GlassService svc = null;
            try
            {
                svc = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = suffix });
                Count lost = new Count(), back = new Count(), shown = new Count();
                bool lostIn = false, backIn = false, backHadSurface = false;
                GlassSpec spec = Spec(Title + " context");
                GlassLook want = GlassProtocolTest.LookFor(1500);
                svc.Lost += delegate { lostIn = TestContext.InPost; lost.Inc(); };
                svc.ServiceRestarted += delegate
                {
                    backIn = TestContext.InPost;
                    GlassLook lk;
                    backHadSurface = h.TryGetLook(spec.Label, out lk) && SameLook(lk, want);
                    back.Inc();
                };
                GlassSurface s = svc.Create(spec);
                s.Shown += delegate { shown.Inc(); };
                s.SetLook(want, "");
                s.SetContents(Picture(spec.Size.Width, spec.Size.Height, 40), spec.Size.Width, spec.Size.Height, Rectangle.Empty);
                s.Show();
                Check(Pump(3000, delegate { return shown.Get() > 0; }), "context: Shown before the host stops");
                h.Stop();
                Check(Pump(5000, delegate { return lost.Get() > 0; }), "context: Lost raised");
                Check(lostIn, "context: Lost was posted to the context current at Connect");
                h.Start(suffix);
                Check(Pump(10000, delegate { return back.Get() > 0; }), "context: ServiceRestarted raised");
                Check(backIn, "context: ServiceRestarted was posted to the context current at Connect");
                Check(backHadSurface, "context: ServiceRestarted came after the surface was created again with its last look");
                s.Dispose();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prev);
                if (svc != null) svc.Dispose();
                h.Stop();
            }
        }

        // A HostPath that is not a full path is refused at Connect.
        static void RelativePathCheck()
        {
            bool threw = false;
            try { GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = "lbr" + Process.GetCurrentProcess().Id, HostPath = @"LGEngineHost.exe" }).Dispose(); }
            catch (ArgumentException ex) { threw = ex.Message.StartsWith("HostPath must be a full path", StringComparison.Ordinal); }
            Check(threw, "a relative HostPath throws ArgumentException at Connect");
        }
        // suffix: non-null = over the pipe (the windows are the host's, and go a moment after the Destroy frames arrive).
        static void ThreadChecks(string suffix)
        {
            bool piped = suffix != null;
            GlassConnectOptions opt = new GlassConnectOptions();
            if (piped) { opt.Mode = GlassMode.Host; opt.HostSuffix = suffix; }
            else opt.HostSuffix = "auto" + Process.GetCurrentProcess().Id;
            GlassService svc = GlassService.Connect(opt);
            try
            {
            int before0 = TestWindows();
            GlassSurface[] s = new GlassSurface[3];
            for (int i = 0; i < 3; i++)
            {
                GlassSpec spec = Spec(Title + " live " + i);
                s[i] = svc.Create(spec);
                s[i].SetContents(Picture(spec.Size.Width, spec.Size.Height, 40), spec.Size.Width, spec.Size.Height, Rectangle.Empty);
                if (i < 2) s[i].Show();
            }
            if (piped) Pump(3000, delegate { return TestWindows() - before0 == 3; });
            Pump(500, null);
            int before = TestWindows();
            Check(before - before0 == 3, "3 live surfaces have 3 windows (" + (before - before0) + ")");
            Check(piped || svc.EngineThreadAlive, "the engine thread is alive");
            svc.Dispose();
            if (piped) Pump(3000, delegate { return TestWindows() == before0; });
            int after = TestWindows();
            Check(after == before0, "all windows gone after GlassService.Dispose (" + before + " -> " + after + ", started at " + before0 + ")");
            Check(!svc.EngineThreadAlive, "the engine thread ended");
            svc.Dispose();   // twice is fine
            s[0].SetOpacity(0.2f);
            Check(true, "service disposed twice, calls after it ignored");
            try { svc.Create(Spec(Title + " late")); Check(false, "Create after Dispose should throw"); }
            catch (ObjectDisposedException) { Check(true, "Create after Dispose throws"); }
            }
            finally { svc.Dispose(); }
        }
    }
}
