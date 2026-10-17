using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LGGpu
{
    // LGGpu.exe: the GPU Liquid Glass prototype's test rig.
    //   offscreen --out DIR [--alone] [--edge] [--case NAME] [--scene mixed|white|dark]
    //                                            draws test cards through the shader and saves PNGs
    //   live --x X --y Y [--w W --h H] [--seconds N] [--anim] [--busy] [--light|--dark] [--elev E] [--lens] [--nolayered] [--two]
    //                                            shows a card over the desktop and measures
    //   busy-only --x X --y Y --seconds N         the moving test window alone (its own cost)
    internal static class Program
    {
        static Dictionary<string, string> opt = new Dictionary<string, string>();
        static StringBuilder log = new StringBuilder();
        static string logPath;

        static void Say(string s)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s;
            Console.WriteLine(line);
            log.AppendLine(line);
            if (logPath != null) { try { File.AppendAllText(logPath, line + "\r\n"); } catch (Exception) { } }
        }

        static int Opt(string name, int fallback)
        {
            string v;
            return opt.TryGetValue(name, out v) ? int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        }
        static string Opt(string name, string fallback)
        {
            string v;
            return opt.TryGetValue(name, out v) ? v : fallback;
        }
        static bool Flag(string name) { return opt.ContainsKey(name); }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("modes: offscreen | live | busy-only (see Program.cs)"); return 2; }
            for (int i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) continue;
                string key = args[i].Substring(2);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) opt[key] = args[++i]; else opt[key] = "";
            }
            logPath = Opt("log", null);
            if (logPath != null) { try { File.Delete(logPath); } catch (Exception) { } }
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Say("CRASH at stage '" + Gfx.Stage + "': " + e.ExceptionObject);
            };
            try { if (!Win.SetProcessDpiAwarenessContext(new IntPtr(-4))) Win.SetProcessDPIAware(); }
            catch (Exception) { Win.SetProcessDPIAware(); }
            Engine.Pump = Pump;
            Gfx.DebugLayer = Flag("debuglayer");
            Geometry.DebugMode = Opt("debug", 0);
            int result = 1;
            try
            {
                switch (args[0])
                {
                    case "offscreen": result = Offscreen(); break;
                    case "live": result = Live(); break;
                    case "busy-only": result = BusyOnly(); break;
                    default: Console.WriteLine("unknown mode " + args[0]); break;
                }
            }
            catch (Exception ex)
            {
                Say("FAILED: " + ex);
            }
            return result;
        }

        // ---------------- off-screen ----------------

        static int Offscreen()
        {
            string dir = Opt("out", Path.Combine(Path.GetTempPath(), "lggpu"));
            Directory.CreateDirectory(dir);
            Engine engine = new Engine(10, 10, true);
            engine.Start();
            if (engine.Problem != null) { Say("no engine: " + engine.Problem); return 1; }
            Say("device: " + engine.G.AdapterName + ", feature level 0x" + engine.G.FeatureLevel.ToString("X"));

            int sceneW = 1400, sceneH = 900;
            int[] scenePx = TestArt.Scene(sceneW, sceneH, Opt("scene", "mixed"));
            string only = Opt("case", null);
            // (name, cardW, cardH, k, lens?, elevation, appearance)
            object[][] cases = {
                new object[] { "toast-200", 792, 380, 2f, false, 50f, -1f },
                new object[] { "toast-100", 396, 190, 1f, false, 50f, -1f },
                new object[] { "osd-lens-200", 424, 122, 2f, true, 50f, -1f },
                new object[] { "pill-150", 330, 72, 1.5f, true, 50f, -1f },
                new object[] { "toast-200-light", 792, 380, 2f, false, 50f, 1f },
                new object[] { "toast-200-elev100", 792, 380, 2f, false, 100f, -1f },
                new object[] { "toast-200-elev0", 792, 380, 2f, false, 0f, -1f },
                new object[] { "dock-200", 1100, 140, 2f, false, 70f, -1f },
            };
            Look look = Opt("look", "app") == "default" ? Look.Default : Look.App;
            foreach (object[] cs in cases)
            {
                string name = (string)cs[0];
                if (only != null && name != only) continue;
                int cardW = (int)cs[1], cardH = (int)cs[2];
                float k = (float)cs[3];
                bool lens = (bool)cs[4];
                float elev = (float)cs[5], appearance = (float)cs[6];
                if (Flag("dark")) appearance = 0;
                RunOffscreenCase(engine, dir, name, scenePx, sceneW, sceneH, cardW, cardH, k, lens ? Shape.Lens : Shape.Card, elev, appearance, look);
            }
            engine.Dispose();
            return 0;
        }

        // Where a test card sits in the scene (shared with the CPU reference).
        public static void CardPlace(int sceneW, int sceneH, int cardW, int cardH, bool edge, out int x, out int y)
        {
            x = (sceneW - cardW) / 2 + 40;
            y = (sceneH - cardH) / 2 - 30;
            if (edge) { x = -cardW / 3; y = sceneH - cardH + 20; }
        }

        static void RunOffscreenCase(Engine engine, string dir, string name, int[] scenePx, int sceneW, int sceneH, int cardW, int cardH, float k, Shape shape, float elev, float appearance, Look look)
        {
            int cardX, cardY;
            CardPlace(sceneW, sceneH, cardW, cardH, Flag("edge"), out cardX, out cardY);
            int cw = cardW, ch = cardH;
            int[] contentPx = shape.Name == "lens" ? TestArt.LensContent(cw, ch, k) : TestArt.ToastContent(cw, ch, k);
            double gpuGlass = 0, gpuFrost = 0, wallGlass = 0;
            engine.Invoke(delegate
            {
                Gfx G = engine.G;
                Tex scene = G.CreateTex(sceneW, sceneH, Gfx.FmtBgra8, true, true);
                G.Upload(scene, scenePx, sceneW, sceneH);
                Geometry geo = new Geometry(cardW, cardH, k, look, shape, elev, 0);
                Surface s = new Surface(G, geo);
                s.ScreenX = cardX; s.ScreenY = cardY;
                s.Appearance = appearance;
                s.SetContent(contentPx, cw, ch);
                Gfx.GpuTimer ft = G.CreateTimer();
                for (int i = 0; i < 5; i++)
                {
                    G.TimerBegin(ft);
                    s.RefreshBackdrop(scene, 0, 0);
                    G.TimerEnd(ft);
                    G.Flush();
                    double ms = G.TimerRead(ft, true);
                    if (i > 0 && ms >= 0) gpuFrost += ms / 4;
                }
                Tex rt = G.CreateTex(geo.W, geo.H, Gfx.FmtBgra8, true, true);
                s.Offscreen = rt;
                RECT under = new RECT(s.WindowX, s.WindowY, s.WindowX + geo.W, s.WindowY + geo.H);
                RECT have = RECT.Intersect(under, new RECT(0, 0, sceneW, sceneH));
                int reps = 6;
                for (int i = 0; i < reps; i++)
                {
                    G.Clear(rt, 0.5f, 0.5f, 0.5f, 1);
                    if (!have.IsEmpty) G.Copy(rt, have.Left - under.Left, have.Top - under.Top, scene.Ptr, have);
                    long t0 = Stopwatch.GetTimestamp();
                    s.Render(true);
                    G.Flush();
                    double ms = s.ReadGpuMs(true);
                    long t1 = Stopwatch.GetTimestamp();
                    if (i > 0) { if (ms >= 0) gpuGlass += ms / (reps - 1); wallGlass += (t1 - t0) * 1000.0 / Stopwatch.Frequency / (reps - 1); }
                }
                float[] st = s.ReadStats();
                Say(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}: stats avg ({1:0},{2:0},{3:0}) midLuma {4:0} spread {5:0.0}", name, st[0] * 255, st[1] * 255, st[2] * 255, st[3], st[4]));
                Tex staging = G.CreateStaging(geo.W, geo.H, Gfx.FmtBgra8);
                int[] px = G.ReadBack(rt, staging);
                if (Geometry.DebugMode > 0)
                {
                    int mp = px[(geo.H / 2) * geo.W + geo.W / 2];
                    Say("debug pixel at the middle: R " + ((mp >> 16) & 255) + " G " + ((mp >> 8) & 255) + " B " + (mp & 255) + " (mode " + Geometry.DebugMode + ")");
                }
                if (Gfx.DebugLayer) Say("debug layer:\r\n" + Gfx.DebugMessages(G.Device));
                string path = Path.Combine(dir, name + ".png");
                TestArt.SavePng(px, geo.W, geo.H, path);
                Say("saved " + path);
                if (Flag("alone"))
                {
                    G.Clear(rt, 0, 0, 0, 0);
                    s.Render(true);
                    px = G.ReadBack(rt, staging);
                    TestArt.SavePng(px, geo.W, geo.H, Path.Combine(dir, name + "-alone.png"));
                }
                staging.Dispose();
                s.Dispose();
                scene.Dispose();
                ft.Dispose();
                Say(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0}: card {1}x{2} @{3} {4} elev {5}: window {6}x{7}, backdrop {8}x{9}, soft 1/{10} ({11}x{12}, sigma {13:0.0}px, blur r{14}), edge 1/{15} x{16}; GPU frost {17:0.000} ms, glass {18:0.000} ms (wall {19:0.00} ms incl. sync)",
                    name, cardW, cardH, k, shape.Name, elev, geo.W, geo.H, geo.BackW, geo.BackH, geo.SoftScale, geo.SoftW, geo.SoftH, geo.Sigma, geo.BlurRadius, geo.EdgeScale, geo.EdgePasses, gpuFrost, gpuGlass, wallGlass));
            });
        }

        // ---------------- live ----------------

        static Win.WndProc glassProc = GlassWndProc, busyProc = BusyWndProc;
        static IntPtr busyHwnd, busyBrushA, busyBrushB, busyBrushC;
        static int busyTick;

        static IntPtr GlassWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == Win.WM_NCHITTEST) return new IntPtr(Win.HTTRANSPARENT);
            return Win.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        static IntPtr BusyWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == Win.WM_PAINT || msg == Win.WM_TIMER)
            {
                busyTick++;
                IntPtr dc = Win.GetDC(hwnd);
                RECT all = new RECT(0, 0, 400, 240);
                Win.FillRect(dc, ref all, (busyTick & 1) == 0 ? busyBrushA : busyBrushB);
                int x = (busyTick * 7) % 300;
                RECT box = new RECT(x, 60, x + 100, 180);
                Win.FillRect(dc, ref box, busyBrushC);
                Win.ReleaseDC(hwnd, dc);
                if (msg == Win.WM_PAINT) Win.ValidateRect(hwnd, IntPtr.Zero);
                return IntPtr.Zero;
            }
            if (msg == Win.WM_ERASEBKGND) return new IntPtr(1);
            return Win.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);

        static ushort RegisterClass(string name, Win.WndProc proc)
        {
            WNDCLASSEX wc = new WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc);
            wc.hInstance = Win.GetModuleHandleW(null);
            wc.lpszClassName = name;
            return Win.RegisterClassExW(ref wc);
        }

        static void Pump()
        {
            MSG msg;
            while (Win.PeekMessageW(out msg, IntPtr.Zero, 0, 0, 1))
            {
                Win.TranslateMessage(ref msg);
                Win.DispatchMessageW(ref msg);
            }
        }

        static void PumpFor(int ms)
        {
            long end = Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000;
            while (true)
            {
                Pump();
                long left = (end - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                if (left <= 0) break;
                Win.MsgWaitForMultipleObjects(0, null, false, (uint)left, 0x04FF);   // QS_ALLINPUT
            }
        }

        static IntPtr MakeBusyWindow(int x, int y)
        {
            RegisterClass("LGGpuBusy", busyProc);
            busyBrushA = Win.CreateSolidBrush(0x00C08040);
            busyBrushB = Win.CreateSolidBrush(0x0040A0E0);
            busyBrushC = Win.CreateSolidBrush(0x00FFFFFF);
            IntPtr h = Win.CreateWindowExW(Win.WS_EX_NOACTIVATE | Win.WS_EX_TOOLWINDOW, "LGGpuBusy", "LGGpu busy", Win.WS_POPUP, x, y, 400, 240, IntPtr.Zero, IntPtr.Zero, Win.GetModuleHandleW(null), IntPtr.Zero);
            Win.SetWindowPos(h, IntPtr.Zero, x, y, 400, 240, Win.SWP_NOACTIVATE | Win.SWP_SHOWWINDOW);
            SetTimer(h, new UIntPtr(1), 16, IntPtr.Zero);
            return h;
        }

        // The sum of a part of the screen's colours, sampled (through GDI; excluded windows do not show).
        static long ScreenSum(int x, int y, int w, int h)
        {
            IntPtr screen = Win.GetDC(IntPtr.Zero);
            IntPtr dc = Win.CreateCompatibleDC(screen);
            IntPtr bmp = Win.CreateCompatibleBitmap(screen, w, h);
            IntPtr old = Win.SelectObject(dc, bmp);
            Win.BitBlt(dc, 0, 0, w, h, screen, x, y, 0x00CC0020);
            long sum = 0;
            for (int j = 0; j < h; j += 8)
                for (int i = 0; i < w; i += 8)
                {
                    int p = Win.GetPixel(dc, i, j);
                    sum += (p & 255) + ((p >> 8) & 255) + ((p >> 16) & 255);
                }
            Win.SelectObject(dc, old);
            Win.DeleteObject(bmp);
            Win.DeleteDC(dc);
            Win.ReleaseDC(IntPtr.Zero, screen);
            return sum;
        }

        static int BusyOnly()
        {
            int x = Opt("x", 1200), y = Opt("y", 600), seconds = Opt("seconds", 5);
            busyHwnd = MakeBusyWindow(x, y);
            Process me = Process.GetCurrentProcess();
            TimeSpan cpu0 = me.TotalProcessorTime;
            long t0 = Stopwatch.GetTimestamp();
            for (int s = 0; s < seconds; s++)
            {
                PumpFor(1000);
                me.Refresh();
                TimeSpan cpu = me.TotalProcessorTime;
                double wall = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                Say(string.Format(System.Globalization.CultureInfo.InvariantCulture, "busy alone: {0:0.0}% of a core (process), {1} paints", (cpu - cpu0).TotalMilliseconds / wall * 100, busyTick));
                cpu0 = cpu; t0 = Stopwatch.GetTimestamp(); busyTick = 0;
            }
            Win.DestroyWindow(busyHwnd);
            return 0;
        }

        static IntPtr MakeGlassWindow(int winX, int winY, int w, int h, bool layered, out bool excluded)
        {
            int ex = Win.WS_EX_NOREDIRECTIONBITMAP | Win.WS_EX_TOOLWINDOW | Win.WS_EX_NOACTIVATE | Win.WS_EX_TOPMOST | Win.WS_EX_TRANSPARENT | (layered ? Win.WS_EX_LAYERED : 0);
            IntPtr hwnd = Win.CreateWindowExW(ex, "LGGpuGlass", "LGGpu glass", Win.WS_POPUP, winX, winY, w, h, IntPtr.Zero, IntPtr.Zero, Win.GetModuleHandleW(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed " + Marshal.GetLastWin32Error());
            if (layered && !Win.SetLayeredWindowAttributes(hwnd, 0, 255, 2)) Say("SetLayeredWindowAttributes failed " + Marshal.GetLastWin32Error());
            excluded = Win.SetWindowDisplayAffinity(hwnd, Win.WDA_EXCLUDEFROMCAPTURE);
            return hwnd;
        }

        static int Live()
        {
            int cardW = Opt("w", 792), cardH = Opt("h", 380), seconds = Opt("seconds", 8);
            int x = Opt("x", 2000), y = Opt("y", 400);
            float k = Opt("k", 2);
            bool lens = Flag("lens");
            float elev = Opt("elev", 50);
            Look look = Look.App;
            Shape shape = lens ? Shape.Lens : Shape.Card;
            int travel = Flag("anim") ? 32 : 0;
            Geometry geo = new Geometry(cardW, cardH, k, look, shape, elev, travel);
            int winX = x - geo.Margin - travel, winY = y - geo.Margin - travel;

            // the window: no redirection surface (DirectComposition draws into it), topmost, never
            // activated, out of the taskbar, and click-through
            bool layered = !Flag("nolayered");
            RegisterClass("LGGpuGlass", glassProc);
            bool excluded;
            IntPtr hwnd = MakeGlassWindow(winX, winY, geo.W, geo.H, layered, out excluded);
            Say("window " + geo.W + "x" + geo.H + " at " + winX + "," + winY + (layered ? " (layered+transparent)" : " (transparent, hit-test)") + "; excluded from capture: " + excluded + (excluded ? "" : " (error " + Marshal.GetLastWin32Error() + ")"));

            long before = ScreenSum(x, y, cardW, cardH);

            Engine engine = new Engine(x + cardW / 2, y + cardH / 2, false);
            engine.Start();
            if (engine.Problem != null) { Say("no engine: " + engine.Problem + "\r\n" + Gfx.SharedNote); return 1; }
            if (Gfx.SharedNote.Length > 0) Say(Gfx.SharedNote);
            Say("device: " + engine.G.AdapterName + "; display " + engine.G.DisplayBounds + "; feed: " + engine.FeedWay);
            Surface surface = null, second = null;
            int[] content = lens ? TestArt.LensContent(cardW, cardH, k) : TestArt.ToastContent(cardW, cardH, k);
            long tAdd = Stopwatch.GetTimestamp();
            engine.Invoke(delegate
            {
                surface = engine.AddSurface(geo, hwnd, x, y);
                surface.Appearance = Flag("light") ? 1 : (Flag("dark") ? 0 : -1);
                surface.SetContent(content, cardW, cardH);
            });
            Say(string.Format("surface added in {0:0.0} ms: backdrop {1}x{2}, soft 1/{3}, edge 1/{4}; sigma {5:0.0} px, blur r{6}", (Stopwatch.GetTimestamp() - tAdd) * 1000.0 / Stopwatch.Frequency, geo.BackW, geo.BackH, geo.SoftScale, geo.EdgeScale, geo.Sigma, geo.BlurRadius));
            Win.ShowWindow(hwnd, Win.SW_SHOWNOACTIVATE);
            PumpFor(400);
            Say("after show: presents " + engine.Presents + ", feed frames " + engine.FeedFrames + " (touching " + engine.FeedTouching + "), render wakeups " + engine.RenderWakeups + ", present errors " + engine.PresentErrors + (engine.LastPresentError != 0 ? " " + Com.Hex(engine.LastPresentError) : ""));

            // a second surface (an indicator) sharing the device, the feed and the composition tree
            IntPtr hwnd2 = IntPtr.Zero;
            if (Flag("two"))
            {
                Geometry geo2 = new Geometry(424, 122, k, look, Shape.Lens, elev, 0);
                int x2 = x + (cardW - 424) / 2, y2 = y + cardH + 60;
                bool ex2;
                hwnd2 = MakeGlassWindow(x2 - geo2.Margin, y2 - geo2.Margin, geo2.W, geo2.H, layered, out ex2);
                int[] content2 = TestArt.LensContent(424, 122, k);
                engine.Invoke(delegate
                {
                    second = engine.AddSurface(geo2, hwnd2, x2, y2);
                    second.SetContent(content2, 424, 122);
                });
                Win.ShowWindow(hwnd2, Win.SW_SHOWNOACTIVATE);
                PumpFor(200);
            }

            // checks: is it on screen, is it click-through, is it invisible to capture?
            POINT mid = new POINT();
            mid.X = x + cardW / 2; mid.Y = y + cardH / 2;
            IntPtr under = Win.WindowFromPoint(mid);
            Say("click-through: WindowFromPoint(card middle) = 0x" + under.ToString("X") + (under == hwnd ? " (OUR WINDOW: not click-through)" : " (another window: click-through OK)"));
            long whileExcluded = ScreenSum(x, y, cardW, cardH);
            Win.SetWindowDisplayAffinity(hwnd, 0);
            Win.DwmFlush(); Win.DwmFlush();
            PumpFor(150);
            long shown = ScreenSum(x, y, cardW, cardH);
            Win.SetWindowDisplayAffinity(hwnd, Win.WDA_EXCLUDEFROMCAPTURE);
            Say("visibility: screen sum before " + before + ", with the glass capturable " + shown + (shown != before ? " (DIFFERS: glass is on screen)" : " (SAME: glass not visible?)") + ", excluded " + whileExcluded + (Math.Abs(whileExcluded - before) <= Math.Max(1, before / 50) ? " (= before: capture does not see it)" : " (differs from before)"));
            PumpFor(300);
            Say("before measuring: presents " + engine.Presents + ", feed frames " + engine.FeedFrames + " (touching " + engine.FeedTouching + "), render wakeups " + engine.RenderWakeups);

            if (Flag("busy")) busyHwnd = MakeBusyWindow(x + cardW / 2 - 200, y + cardH / 2 - 120);
            if (Flag("anim")) engine.Post(delegate { engine.Animating = surface; });

            Process me = Process.GetCurrentProcess();
            TimeSpan cpu0 = me.TotalProcessorTime;
            long t0 = Stopwatch.GetTimestamp();
            long rw0 = engine.RenderWakeups, fw0 = engine.FeedWakeups, ff0 = engine.FeedFrames, ft0 = engine.FeedTouching, pr0 = engine.Presents, po0 = engine.FeedPointerOnly, cp0 = engine.FeedCopyPixels;
            double ls0 = engine.LatencySumMs, ds0 = engine.DesktopLatencySumMs, ps0 = engine.PresentCallSumMs;
            int lc0 = engine.LatencyCount;
            engine.LatencyMaxMs = engine.DesktopLatencyMaxMs = 0;
            for (int s = 0; s < seconds; s++)
            {
                PumpFor(1000);
                me.Refresh();
                TimeSpan cpu = me.TotalProcessorTime;
                double wall = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                int lc = engine.LatencyCount - lc0;
                Say(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "sec {0}: CPU {1:0.00}% of a core | wakeups render {2}/s feed {3}/s | feed frames {4} (pointer-only {5}, touching {6}, {7:0.0} Mpx copied) | presents {8} | latency acquire->present avg {9:0.00} max {10:0.00} ms, desktop->present avg {11:0.00} max {12:0.00} ms | present call {13:0.00} ms | GPU glass {14:0.000} ms{15}",
                    s + 1, (cpu - cpu0).TotalMilliseconds / wall * 100, engine.RenderWakeups - rw0, engine.FeedWakeups - fw0, engine.FeedFrames - ff0, engine.FeedPointerOnly - po0, engine.FeedTouching - ft0,
                    (engine.FeedCopyPixels - cp0) / 1e6, engine.Presents - pr0,
                    lc > 0 ? (engine.LatencySumMs - ls0) / lc : 0, engine.LatencyMaxMs, lc > 0 ? (engine.DesktopLatencySumMs - ds0) / lc : 0, engine.DesktopLatencyMaxMs,
                    engine.Presents - pr0 > 0 ? (engine.PresentCallSumMs - ps0) / (engine.Presents - pr0) : 0, surface.LastGpuMs, engine.PresentErrors > 0 ? " PRESENT ERRORS " + engine.PresentErrors + " " + Com.Hex(engine.LastPresentError) : ""));
                cpu0 = cpu; t0 = Stopwatch.GetTimestamp();
                rw0 = engine.RenderWakeups; fw0 = engine.FeedWakeups; ff0 = engine.FeedFrames; ft0 = engine.FeedTouching; pr0 = engine.Presents; po0 = engine.FeedPointerOnly; cp0 = engine.FeedCopyPixels;
                ls0 = engine.LatencySumMs; ds0 = engine.DesktopLatencySumMs; ps0 = engine.PresentCallSumMs; lc0 = engine.LatencyCount;
                engine.LatencyMaxMs = engine.DesktopLatencyMaxMs = 0;
                if (engine.Problem != null) { Say("engine problem: " + engine.Problem); break; }
                if (Flag("anim") && s == seconds * 2 / 3) engine.Post(delegate { engine.StopAnimation(); });
            }
            if (busyHwnd != IntPtr.Zero) Win.DestroyWindow(busyHwnd);
            Win.ShowWindow(hwnd, Win.SW_HIDE);
            if (hwnd2 != IntPtr.Zero) Win.ShowWindow(hwnd2, Win.SW_HIDE);
            if (Gfx.DebugLayer)
            {
                string messages = "";
                try { engine.Invoke(delegate { messages = engine.DebugMessages(); }); }
                catch (Exception ex) { messages = ex.Message; }
                Say("debug layer:\r\n" + messages);
            }
            engine.Dispose();
            Win.DestroyWindow(hwnd);
            if (hwnd2 != IntPtr.Zero) Win.DestroyWindow(hwnd2);
            Say("done; problem: " + (engine.Problem ?? "none"));
            return 0;
        }
    }
}
