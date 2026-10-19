using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using LGEngine;

namespace LGBench
{
    // The engine's off-screen benchmark: standard scenes, every kind of glass, every scale; what a
    // frame costs on each path, and pictures to judge the look by. Shows nothing on screen.
    static class Program
    {
        // -profile NAME|TEXT: every glass is made with this GlassProfile (a preset's name, or its string form; empty = today's analytic shoulder)
        internal static string ProfileArg = "";

        internal static GlassShape ShapeFor(bool lens)
        {
            GlassShape s = lens ? GlassShape.Lens : GlassShape.Card;
            if (string.IsNullOrEmpty(ProfileArg)) return s;
            GlassProfile p = GlassProfile.Preset(ProfileArg, s) ?? GlassProfile.Parse(ProfileArg);
            return s.WithProfile(p);
        }

        // One kind of glass: its size in points (at 100%), its look and its shape.
        sealed class Kind
        {
            public string Name;
            public int PtW, PtH;
            public GlassLook Look;
            public bool Lens;
            public Kind(string name, int w, int h, GlassLook look, bool lens) { Name = name; PtW = w; PtH = h; Look = look; Lens = lens; }
        }

        static readonly Kind[] AllKinds =
        {
            new Kind("card", 396, 152, new GlassLook(46, 52, 80, 60, 40, 24, 100), false),
            new Kind("osd", 170, 46, new GlassLook(46, 52, 80, 60, 28, 24, 100), true),
            new Kind("dock", 400, 56, new GlassLook(46, 52, 80, 60, 28, 100, 100), false),
            new Kind("pill", 160, 40, new GlassLook(46, 52, 80, 60, 28, 100, 100), true),
        };
        static readonly string[] AllScenes = { "busy", "wall", "grid", "dark", "lop", "light" };
        static readonly float[] AllScales = { 1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f };

        static string imgDir, dumpDir, cmpDir;
        static int reps = 12;
        static bool verbose;
        static readonly StringBuilder report = new StringBuilder();

        internal static void Say(string format, params object[] args)
        {
            string line = args.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, args);
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            List<string> scenes = new List<string>(AllScenes), kinds = new List<string>();
            foreach (Kind k in AllKinds) kinds.Add(k.Name);
            List<float> scales = new List<float>(AllScales);
            string wallpaper = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";
            bool energy = false, check = false, timing = true, quick = false, zoom = false, elevation = false, service = false, contract = false, loopback = false, host = false, probe = false, motion = false, surface = false, hostmain = false; string proofDir = null;
            string ab = "", reportPath = null, sheetPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : "";
                switch (a.ToLowerInvariant())
                {
                    case "-help": case "-h": case "/?": Help(); return 0;
                    case "-scenes": scenes = new List<string>(next.Split(',')); i++; break;
                    case "-kinds": kinds = new List<string>(next.Split(',')); i++; break;
                    case "-scales": scales = new List<float>(); foreach (string s in next.Split(',')) scales.Add(float.Parse(s, CultureInfo.InvariantCulture)); i++; break;
                    case "-reps": reps = int.Parse(next); i++; break;
                    case "-img": imgDir = next; i++; break;
                    case "-dump": dumpDir = next; i++; break;
                    case "-cmp": cmpDir = next; i++; break;
                    case "-zoom": zoom = true; break;
                    case "-ab": ab = next; i++; break;
                    case "-profile": ProfileArg = next; i++; break;
                    case "-energy": energy = true; break;
                    case "-check": check = true; break;
                    case "-notiming": timing = false; break;
                    case "-quick": quick = true; break;
                    case "-elevation": elevation = true; break;
                    case "-sheet": sheetPath = next; i++; break;
                    case "-motion": motion = true; break;
                    case "-service": service = true; break;
                    case "-contract": contract = true; break;
                    case "-loopback": loopback = true; break;
                    case "-host": host = true; break;
                    case "-surface": surface = true; break;
                    case "-proof": proofDir = next; i++; break;
                    case "-hostmain": hostmain = true; break;
                    case "-probe": probe = true; break;
                    case "-wallpaper": wallpaper = next; i++; break;
                    case "-report": reportPath = next; i++; break;
                    case "-v": verbose = true; break;
                    default: Console.Error.WriteLine("unknown option " + a + " (-help)"); return 2;
                }
            }
            if (quick)
            {
                scenes = new List<string>(new string[] { "busy", "wall" });
                scales = new List<float>(new float[] { 2f });
            }
            if (service || contract || surface || hostmain || proofDir != null)
            {
                // (the service checks stand alone: not the default timing run, and no scenes unless another mode needs them)
                timing = false;
                if (!check && !energy && !motion && !probe && !elevation && sheetPath == null && imgDir == null) scenes = new List<string>();
            }
            if (imgDir != null) Directory.CreateDirectory(imgDir);
            if (dumpDir != null) Directory.CreateDirectory(dumpDir);
            Apply(ab);
            Say("LGBench  {0:yyyy-MM-dd HH:mm}  {1}  cores {2}  {3}", DateTime.Now, Environment.Is64BitProcess ? "x64" : "x86", Environment.ProcessorCount, ab.Length > 0 ? "A/B " + ab : "defaults");
            Say("engine options: {0}", GlassOptions.Describe());

            Dictionary<string, Scene> made = new Dictionary<string, Scene>();
            foreach (string s in scenes) made[s] = Scene.Make(s, wallpaper);

            if (timing)
            {
                Say("");
                Say("{0,-6} {1,-5} {2,-5} {3,9} {4,8} {5,8} {6,8} {7,8} {8,8} {9,8} {10,8}", "kind", "scale", "scene", "size", "map ms", "accept", "full", "bd-chg", "bd-10%", "cont", "alloc");
                Array.Clear(GlassMap.PhaseTicks, 0, GlassMap.PhaseTicks.Length);
                foreach (string kn in kinds)
                {
                Kind kind = Find(kn);
                    foreach (float k in scales)
                        foreach (string sn in scenes)
                            Time(kind, k, made[sn], zoom);
                }
                if (verbose)
                {
                    StringBuilder ph = new StringBuilder("phases (ms, all frames of the timing runs):");
                    for (int p = 0; p < GlassMap.PhaseTicks.Length; p++) ph.AppendFormat(" {0} {1:0.0}", GlassMap.PhaseNames[p], GlassMap.PhaseTicks[p] * 1000.0 / Stopwatch.Frequency);
                    Say(ph.ToString());
                }
            }
            if (check)
            {
                Say("");
                foreach (string kn in kinds)
                {
                    Kind kind = Find(kn);
                    foreach (float k in scales) Check(kind, k, made[scenes[0]]);
                }
            }
            if (energy)
            {
                Say("");
                Say("Energy proxy: CPU ms a second on the engine for a realistic second of each pattern (lower is better).");
                foreach (string kn in kinds)
                {
                    Kind kind = Find(kn);
                    foreach (float k in scales) Energy(kind, k, made[scenes[0]]);
                }
            }
            if (sheetPath != null) Sheet(kinds, scales[0], scenes, made, sheetPath);
            if (motion) { Say(""); if (Environment.GetEnvironmentVariable("LGMOTIONREV") == "1") MotionOrder = new int[] { 3, 2, 1, 0 }; string onlyEv = Environment.GetEnvironmentVariable("LGMOTIONEV"); if (!string.IsNullOrEmpty(onlyEv)) MotionOrder = new int[] { int.Parse(onlyEv) }; foreach (string kn in kinds) foreach (float k in scales) MotionBench(Find(kn), k, made[scenes[0]]); }
            if (elevation && imgDir != null)
            {
                foreach (string kn in kinds) ElevationStrip(Find(kn), scales[scales.Count - 1], made[scenes[0]]);
            }
            if (probe) { Say(""); foreach (string kn in kinds) { Kind kind = Find(kn); foreach (float k in scales) Probe(kind, k, made[scenes[0]]); } }
            if (service) ServiceBench();
            if (contract) Contract.Run(loopback, host);
            if (surface) SurfaceBench.Run();
            if (proofDir != null) Proof.Run(proofDir);
            if (hostmain) HostBench.Run();
            if (reportPath != null) File.WriteAllText(reportPath, report.ToString());
            return 0;
        }

        static void Help()
        {
            Console.WriteLine("LGBench: the Liquid Glass engine's off-screen benchmark (shows nothing).");
            Console.WriteLine("  -quick                card/osd/dock/pill at 200% over the busy and wallpaper scenes");
            Console.WriteLine("  -kinds card,osd,dock,pill   -scales 1,1.25,1.5,1.75,2,2.5   -scenes busy,wall,grid,dark,lop,light");
            Console.WriteLine("  -reps N               repetitions per measurement (median reported)");
            Console.WriteLine("  -img DIR [-zoom]      write pictures of each glass over its scene (and 4x zooms of the corner)");
            Console.WriteLine("  -dump DIR | -cmp DIR  keep the raw pixels of each glass, or compare with pixels kept before");
            Console.WriteLine("  -profile NAME|TEXT    make every glass with a GlassProfile: a preset (Tighter rim, Thin lens, Pillow, Flat pane) or its string form");
            Console.WriteLine("  -ab Name=value;...    try engine options or tunables (GlassOptions.*, GlassMap.*, Lens.*)");
            Console.WriteLine("  -check                pixel-identity checks of the fast paths against a full frame");
            Console.WriteLine("  -energy               CPU ms per second for realistic update patterns");
            Console.WriteLine("  -elevation            with -img: a strip of each kind at elevation 0, 25, 50, 75, 100");
            Console.WriteLine("  -sheet FILE           one png: every kind (rows) over every scene (columns) at the first scale");
            Console.WriteLine("  -motion               the glass moving (arrival, 2 s of hover, a press, a level ripple): CPU ms per event");
            Console.WriteLine("  -service              the surface service's thread: wakeups, pacing jitter, idle cost");
            Console.WriteLine("  -contract             the public surface API off screen: show, burst of calls, latency, invalid input");
            Console.WriteLine("  -loopback             with -contract: the same over a GlassHostServer in this process (real pipe, sections, events)");
            Console.WriteLine("  -host                 with -contract: the same against the INSTALLED LGEngineHost.exe (env LGENGINE_HOST, else beside a running LiquidGlass.exe, else beside this exe), started on demand (then a kill test); skipped if none");
            Console.WriteLine("  -surface              the app's glass through the surface API vs the direct path, pixel for pixel (step 6)");
            Console.WriteLine("  -proof DIR            picture proof of the C++ app's glass animations (wire to host, frames, PNGs into DIR)");
            Console.WriteLine("  -hostmain             the host process's runner in memory: single instance, idle exit, log ring (phase 2b)");
            Console.WriteLine("  -report FILE       also write the output to a file");
        }

        static Kind Find(string name)
        {
            foreach (Kind k in AllKinds) if (k.Name == name) return k;
            throw new ArgumentException("no such kind: " + name);
        }

        // "Name=value;Name=value": GlassOptions fields, GlassMap's static tunables, or Lens.<field>.
        static void Apply(string ab)
        {
            if (string.IsNullOrEmpty(ab)) return;
            foreach (string pair in ab.Split(';'))
            {
                string[] kv = pair.Split('=');
                if (kv.Length != 2) continue;
                string key = kv[0].Trim(), val = kv[1].Trim();
                if (key.StartsWith("Lens."))
                {
                    FieldInfo lf = typeof(GlassShape).GetField(key.Substring(5));
                    if (lf == null) throw new ArgumentException("no such lens number: " + key);
                    lf.SetValue(GlassShape.Lens, Convert.ChangeType(val, lf.FieldType, CultureInfo.InvariantCulture));
                    continue;
                }
                FieldInfo f = typeof(GlassOptions).GetField(key, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null) f = typeof(GlassMap).GetField(key, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null) throw new ArgumentException("no such option or number: " + key);
                object v = f.FieldType == typeof(bool) ? (object)(val == "1" || val.ToLowerInvariant() == "true") : Convert.ChangeType(val, f.FieldType, CultureInfo.InvariantCulture);
                f.SetValue(null, v);
            }
        }

        // ------------------------------------------------------------------------------------------
        // One glass set up over a scene, the way the app does it.
        sealed class Rig
        {
            public Kind Kind;
            public float K;
            public Scene Scene;
            public int CardW, CardH, CardX, CardY;
            public GlassMap Map;
            public GlassBackdrop Backdrop = new GlassBackdrop();
            public int[] Content, Out;
            public Rectangle Area;

            public Rig(Kind kind, float k, Scene scene)
            {
                Kind = kind;
                K = k;
                Scene = scene;
                CardW = (int)Math.Round(kind.PtW * k);
                CardH = (int)Math.Round(kind.PtH * k);
                // where it sits: a notification top right, an indicator bottom middle, the dock and pill at the bottom
                CardX = kind.Name == "card" ? scene.W - CardW - (int)(16 * k) : (scene.W - CardW) / 2;
                CardY = kind.Name == "card" ? (int)(16 * k) : scene.H - CardH - (int)(60 * k);
                Map = new GlassMap(CardW, CardH, k, kind.Look, Program.ShapeFor(kind.Lens));
                Area = GlassBackdrop.AreaFor(Map, CardX, CardY);
                Out = new int[Map.W * Map.H];
                Content = MakeContent(0);
            }

            public int[] MakeContent(int frame)
            {
                switch (Kind.Name)
                {
                    case "card": return Contents.Card(CardW, CardH, K, frame);
                    case "osd": return Contents.Osd(CardW, CardH, K, 0.3f + 0.05f * (frame % 10));
                    case "dock": return Contents.Dock(CardW, CardH, K, frame % 5);
                    default: return Contents.Pill(CardW, CardH, K, frame % 2 == 1);
                }
            }

            // A new picture of the scene behind the card, handed in as the feed would. Returns whether
            // the engine took it as a change.
            public bool Feed(Scene from)
            {
                int[] into = Backdrop.Buffer(Map);
                from.ReadArea(Area, Map.SharpFactor, into, Map.SharpW, Map.SharpH);
                return Backdrop.Accept(Map, false);
            }

            // What the app works out for each new picture (GlassOverlay.Adapt and ShadowGainFor).
            public void Adapt()
            {
                Backdrop.Measure(Map);
                float bright = Math.Max(0f, Math.Min(1f, (Backdrop.MidLuma - 80) / 150f));
                float busy = Math.Max(0f, Math.Min(1f, (Backdrop.Spread - 20) / 70f));
                Map.ShadowGain = Math.Min(1.3f, 0.85f + 0.45f * bright + 0.25f * busy);
                float u = (Backdrop.Spread - 24) / (float)(70 - 24);
                u = u <= 0 ? 0 : (u >= 1 ? 1 : u * u * (3 - 2 * u));
                float evenAim = (float)Math.Round(u * 16) / 16f;
                int slack = (int)(14 * evenAim);
                if (Backdrop.MidLuma >= 165 + slack) Map.Appearance = 1;
                else if (Backdrop.MidLuma <= 110 - slack) Map.Appearance = 0;
                Map.Even = evenAim;
                Map.AvgR = Backdrop.AvgR; Map.AvgG = Backdrop.AvgG; Map.AvgB = Backdrop.AvgB;
            }

            // A whole frame drawn from the backdrop as it is now.
            public void Frame(int[] content)
            {
                Map.Prime(Out);
                Map.Shade(Backdrop, content, CardW, CardH, 0, 0, Out);
            }

            public Rectangle FrameDirty(int[] content)
            {
                return Map.Shade(Backdrop, content, CardW, CardH, 0, 0, Out);
            }

            // The glass laid over the scene, cropped to the card and a little round it.
            public int[] Composite(int pad, out int cw, out int ch)
            {
                int[] screen = (int[])Scene.Px.Clone();
                int sw = Scene.W, sh = Scene.H, ox = CardX - Map.Margin, oy = CardY - Map.Margin;
                for (int y = 0; y < Map.H; y++)
                {
                    int sy = oy + y;
                    if (sy < 0 || sy >= sh) continue;
                    for (int x = 0; x < Map.W; x++)
                    {
                        int sx = ox + x;
                        if (sx < 0 || sx >= sw) continue;
                        int p = Out[y * Map.W + x], a = (p >> 24) & 255;
                        if (a == 0) continue;
                        int d = screen[sy * sw + sx], keep = 255 - a;
                        int r = ((p >> 16) & 255) + ((d >> 16) & 255) * keep / 255, g = ((p >> 8) & 255) + ((d >> 8) & 255) * keep / 255, b = (p & 255) + (d & 255) * keep / 255;
                        screen[sy * sw + sx] = (Math.Min(255, r) << 16) | (Math.Min(255, g) << 8) | Math.Min(255, b);
                    }
                }
                Rectangle crop = Rectangle.Intersect(new Rectangle(CardX - pad, CardY - pad, CardW + 2 * pad, CardH + 2 * pad), new Rectangle(0, 0, sw, sh));
                int[] cut = new int[crop.Width * crop.Height];
                for (int y = 0; y < crop.Height; y++) Array.Copy(screen, (crop.Top + y) * sw + crop.Left, cut, y * crop.Width, crop.Width);
                cw = crop.Width;
                ch = crop.Height;
                return cut;
            }
        }

        static double Median(List<double> v)
        {
            v.Sort();
            return v.Count == 0 ? 0 : v[v.Count / 2];
        }

        static double Ms(Stopwatch t) { return t.ElapsedTicks * 1000.0 / Stopwatch.Frequency; }

        static void Time(Kind kind, float k, Scene scene, bool zoom)
        {
            Stopwatch t = Stopwatch.StartNew();
            Rig rig = new Rig(kind, k, scene);
            double mapMs = Ms(t);
            // the second picture: the whole area different (a window switched behind), and one with a tenth of it changed
            Rectangle area = rig.Area;
            Scene whole = scene.Changed(new Rectangle(area.Left - 10, area.Top - 10, area.Width + 20, area.Height + 20), 1);
            Scene tenth = scene.Changed(new Rectangle(area.Left + area.Width * 45 / 100, area.Top + area.Height * 40 / 100, area.Width * 10 / 100, area.Height * 20 / 100), 2);

            rig.Feed(scene);
            rig.Adapt();
            rig.Frame(rig.Content);

            // accept: a new, wholly different picture taken in (shrunk and softened)
            List<double> accept = new List<double>(), full = new List<double>(), change = new List<double>(), small = new List<double>(), cont = new List<double>();
            Scene[] pair = { scene, whole };
            for (int i = 0; i < reps; i++)
            {
                Scene s = pair[i & 1];
                int[] into = rig.Backdrop.Buffer(rig.Map);
                s.ReadArea(area, rig.Map.SharpFactor, into, rig.Map.SharpW, rig.Map.SharpH);
                t.Restart();
                rig.Backdrop.Accept(rig.Map, false);
                accept.Add(Ms(t));
            }
            // full: everything drawn again from scratch (a new map's first frame; the look changing)
            for (int i = 0; i < reps; i++)
            {
                t.Restart();
                rig.Map.Prime(rig.Out);
                rig.Map.Shade(rig.Backdrop.Soft, rig.Backdrop.Edge, rig.Content, rig.CardW, rig.CardH, 0, 0, rig.Out);
                full.Add(Ms(t));
            }
            // backdrop change: a new picture (the whole area different) in, pixels out, as the live path does it
            for (int i = 0; i < reps; i++)
            {
                Scene s = pair[i & 1];
                int[] into = rig.Backdrop.Buffer(rig.Map);
                s.ReadArea(area, rig.Map.SharpFactor, into, rig.Map.SharpW, rig.Map.SharpH);
                t.Restart();
                rig.Backdrop.Accept(rig.Map, false);
                rig.FrameDirty(rig.Content);
                change.Add(Ms(t));
            }
            // the same with a tenth of the area changed
            Scene[] pair2 = { scene, tenth };
            for (int i = 0; i < reps; i++)
            {
                Scene s = pair2[i & 1];
                int[] into = rig.Backdrop.Buffer(rig.Map);
                s.ReadArea(area, rig.Map.SharpFactor, into, rig.Map.SharpW, rig.Map.SharpH);
                t.Restart();
                rig.Backdrop.Accept(rig.Map, false);
                rig.FrameDirty(rig.Content);
                small.Add(Ms(t));
            }
            // contents only: the backdrop as it is, the contents changed (a slider step, a button lit)
            int[] c0 = rig.MakeContent(0), c1 = rig.MakeContent(1);
            rig.FrameDirty(c0);
            long allocBefore = AppDomain.MonitoringIsEnabled ? AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize : 0;
            for (int i = 0; i < reps; i++)
            {
                int[] c = (i & 1) == 0 ? c1 : c0;
                t.Restart();
                rig.FrameDirty(c);
                cont.Add(Ms(t));
            }
            long allocAfter = AppDomain.MonitoringIsEnabled ? AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize : 0;
            double allocKb = (allocAfter - allocBefore) / 1024.0 / Math.Max(1, reps);
            Say("{0,-6} {1,-5} {2,-5} {3,9} {4,8:0.0} {5,8:0.00} {6,8:0.00} {7,8:0.00} {8,8:0.00} {9,8:0.000} {10,7:0.0}k", kind.Name, k, scene.Name, rig.Map.W + "x" + rig.Map.H,
                mapMs, Median(accept), Median(full), Median(change), Median(small), Median(cont), allocKb);
            if (imgDir != null || dumpDir != null || cmpDir != null)
            {
                rig.Feed(scene);
                rig.Adapt();
                rig.Frame(rig.Content);
                string raw = string.Format(CultureInfo.InvariantCulture, "{0}_{1}_{2}.raw", kind.Name, k, scene.Name);
                if (dumpDir != null)
                {
                    byte[] bytes = new byte[rig.Out.Length * 4];
                    Buffer.BlockCopy(rig.Out, 0, bytes, 0, bytes.Length);
                    File.WriteAllBytes(Path.Combine(dumpDir, raw), bytes);
                }
                if (cmpDir != null)
                {
                    string path = Path.Combine(cmpDir, raw);
                    if (!File.Exists(path)) Say("   compare: no {0} to compare with", raw);
                    else
                    {
                        byte[] bytes = File.ReadAllBytes(path);
                        int[] was = new int[bytes.Length / 4];
                        Buffer.BlockCopy(bytes, 0, was, 0, bytes.Length);
                        if (was.Length != rig.Out.Length) Say("   compare: {0} is a different size", raw);
                        else
                        {
                            int differ = 0, most = 0;
                            long sum = 0;
                            for (int i = 0; i < was.Length; i++)
                            {
                                int a = was[i], b = rig.Out[i];
                                if (a == b) continue;
                                differ++;
                                int d = Math.Max(Math.Max(Math.Abs(((a >> 24) & 255) - ((b >> 24) & 255)), Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255))), Math.Max(Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)), Math.Abs((a & 255) - (b & 255))));
                                if (d > most) most = d;
                                sum += d;
                            }
                            Say("   compare with {0}: {1} of {2} pixels differ, the most by {3}, on average by {4:0.00}", raw, differ, was.Length, most, differ > 0 ? sum / (double)differ : 0);
                        }
                    }
                }
            }
            if (imgDir != null)
            {
                int cw, ch;
                int[] pic = rig.Composite((int)(24 * k), out cw, out ch);
                string name = string.Format(CultureInfo.InvariantCulture, "{0}_{1}_{2}", kind.Name, k, scene.Name);
                Scene.Save(pic, cw, ch, Path.Combine(imgDir, name + ".png"));
                if (zoom) Scene.Save(Zoom(pic, cw, ch, 0, 0, Math.Min(cw, (int)(110 * k)), Math.Min(ch, (int)(70 * k)), 4), Math.Min(cw, (int)(110 * k)) * 4, Math.Min(ch, (int)(70 * k)) * 4, Path.Combine(imgDir, name + "_zoom.png"));
            }
        }

        static int[] Zoom(int[] px, int w, int h, int x0, int y0, int zw, int zh, int z)
        {
            int[] big = new int[zw * z * zh * z];
            for (int y = 0; y < zh * z; y++)
                for (int x = 0; x < zw * z; x++)
                    big[y * zw * z + x] = px[(y0 + y / z) * w + x0 + x / z];
            return big;
        }

        // ------------------------------------------------------------------------------------------
        // The fast paths must give exactly the pixels of a whole frame.
        static void Check(Kind kind, float k, Scene scene)
        {
            Rig rig = new Rig(kind, k, scene);
            Rectangle area = rig.Area;
            Scene tenth = scene.Changed(new Rectangle(area.Left + area.Width * 45 / 100, area.Top + area.Height * 40 / 100, area.Width * 10 / 100, area.Height * 20 / 100), 2);
            rig.Feed(scene);
            rig.Adapt();
            int[] c0 = rig.MakeContent(0), c1 = rig.MakeContent(1);

            // 1. contents-only path against a whole frame with those contents
            rig.Frame(c0);
            rig.FrameDirty(c1);                     // (the contents-only path)
            int[] fast = (int[])rig.Out.Clone();
            rig.Frame(c1);                          // (a whole frame)
            int d1 = Differ(fast, rig.Out);

            // 2. an incremental backdrop change against a whole frame from the new backdrop
            rig.Feed(tenth);
            Rectangle dirty = rig.FrameDirty(c1);   // (only what the change reaches)
            int[] inc = (int[])rig.Out.Clone();
            rig.Frame(c1);
            int d2 = Differ(inc, rig.Out);
            int outsideDirty = 0;
            // 3. a backdrop change back again, then the rows-only path (an indicator's slider band)
            rig.Feed(scene);
            rig.FrameDirty(c1);
            int top = rig.Map.H / 2 - 10, bottom = rig.Map.H / 2 + 10;
            rig.Map.Shade(rig.Backdrop.Soft, rig.Backdrop.Edge, rig.Backdrop.Version, c0, rig.CardW, rig.CardH, 0, 0, rig.Out, top, bottom);
            int[] rows = (int[])rig.Out.Clone();
            rig.Frame(c1);
            for (int y = top; y < bottom; y++) Array.Copy(rows, y * rig.Map.W, rig.Out, y * rig.Map.W, rig.Map.W);   // (those rows should be c0's, the rest c1's)
            rig.Map.Prime(fast);
            rig.Map.Shade(rig.Backdrop.Soft, rig.Backdrop.Edge, c0, rig.CardW, rig.CardH, 0, 0, fast);
            int d3 = 0;
            for (int y = top; y < bottom; y++) for (int x = 0; x < rig.Map.W; x++) if (rows[y * rig.Map.W + x] != fast[y * rig.Map.W + x]) d3++;
            // 4. the glass moving (GlassMotion) and back to neutral, with a contents change and a backdrop change on the
            //    way: the settled frame must be the static glass to the last pixel
            rig.Frame(c1);
            int[] still = (int[])rig.Out.Clone();
            GlassMotion m = GlassMotion.Neutral;
            m.Bend = 0.4f; m.Light = 0.5f;
            rig.Map.Motion = m; rig.FrameDirty(c1);
            m.Bend = 1.12f; m.Light = 1.3f; m.Swell = 0.25f; m.SwellX = rig.CardW * 0.3f; m.SwellY = rig.CardH * 0.4f; m.SwellRadius = rig.CardH * 0.6f;
            m.Lean = 0.6f; m.LeanX = rig.CardW * 0.2f; m.LeanY = 0;
            rig.Map.Motion = m; rig.FrameDirty(c0);
            rig.Feed(tenth); rig.FrameDirty(c0);
            m.Ripple = 0.2f; m.RippleLength = rig.CardW / 3f; m.RipplePhase = 1;
            rig.Map.Motion = m; rig.FrameDirty(c1);
            // (a moving frame reached that way must be what a fresh map draws for that motion from scratch)
            GlassMap fresh = new GlassMap(rig.CardW, rig.CardH, k, kind.Look, Program.ShapeFor(kind.Lens));
            fresh.Appearance = rig.Map.Appearance; fresh.Even = rig.Map.Even; fresh.AvgR = rig.Map.AvgR; fresh.AvgG = rig.Map.AvgG; fresh.AvgB = rig.Map.AvgB;
            fresh.ShadowGain = rig.Map.ShadowGain; fresh.Motion = m;
            int[] freshOut = new int[fresh.W * fresh.H];
            fresh.Prime(freshOut);
            fresh.Shade(rig.Backdrop, c1, rig.CardW, rig.CardH, 0, 0, freshOut);
            int d5 = Differ(freshOut, rig.Out);
            if (d5 > 0 && verbose)
            {
                int bx0 = int.MaxValue, bx1 = -1, by0 = int.MaxValue, by1 = -1, most = 0, shown = 0;
                StringBuilder where = new StringBuilder();
                for (int i = 0; i < freshOut.Length; i++)
                {
                    int a = freshOut[i], b = rig.Out[i];
                    if (a == b) continue;
                    int x = i % fresh.W, y = i / fresh.W;
                    bx0 = Math.Min(bx0, x); bx1 = Math.Max(bx1, x); by0 = Math.Min(by0, y); by1 = Math.Max(by1, y);
                    int d = Math.Max(Math.Max(Math.Abs(((a >> 24) & 255) - ((b >> 24) & 255)), Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255))), Math.Max(Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)), Math.Abs((a & 255) - (b & 255))));
                    most = Math.Max(most, d);
                    if (shown++ < 6) where.AppendFormat(" ({0},{1}) {2:X8}/{3:X8}", x, y, a, b);
                }
                Say("   moving frame differs within x {0}-{1}, y {2}-{3} (glass {4}x{5}, margin {6}), most by {7}:{8}", bx0, bx1, by0, by1, fresh.W, fresh.H, fresh.Margin, most, where);
            }
            rig.Feed(scene); rig.FrameDirty(c1);
            rig.Map.Motion = GlassMotion.Neutral;
            rig.FrameDirty(c1);
            int d4 = Differ(still, rig.Out);
            Say("check {0,-5} @{1,-4}: contents-only {2} px differ; incremental (dirty {3}x{4}) {5} px differ{6}; rows-only {7} px differ; moving {10} px, settled {8} px differ  {9}",
                kind.Name, k, d1, dirty.Width, dirty.Height, d2, outsideDirty > 0 ? " (" + outsideDirty + " outside the dirty rect)" : "", d3, d4, d1 + d2 + d3 + d4 + d5 == 0 ? "OK" : "MISMATCH", d5);
            if (d1 > 0 && imgDir != null)
            {
                // where: the differing pixels in white over the glass
                rig.Frame(c1);
                int[] mask = new int[rig.Out.Length];
                int bx0 = rig.Map.W, bx1 = -1, by0 = rig.Map.H, by1 = -1, most = 0;
                for (int i = 0; i < mask.Length; i++)
                {
                    bool differ = fast[i] != rig.Out[i];
                    mask[i] = differ ? 0xFFFFFF : ((rig.Out[i] >> 1) & 0x7F7F7F);
                    if (!differ) continue;
                    int x = i % rig.Map.W, y = i / rig.Map.W;
                    if (x < bx0) bx0 = x; if (x > bx1) bx1 = x; if (y < by0) by0 = y; if (y > by1) by1 = y;
                    int a = fast[i], b = rig.Out[i];
                    int d = Math.Max(Math.Max(Math.Abs(((a >> 24) & 255) - ((b >> 24) & 255)), Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255))), Math.Max(Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)), Math.Abs((a & 255) - (b & 255))));
                    if (d > most) most = d;
                }
                Say("   differing pixels within x {0}-{1}, y {2}-{3} (card at {4},{5} size {6}x{7}), the most by {8}", bx0, bx1, by0, by1, rig.Map.Margin, rig.Map.Margin, rig.CardW, rig.CardH, most);
                if (verbose)
                {
                    int shown = 0;
                    for (int i = 0; i < mask.Length && shown < 12; i++)
                    {
                        if (fast[i] == rig.Out[i]) continue;
                        int x = i % rig.Map.W, y = i / rig.Map.W, cx = x - rig.Map.Margin, cy = y - rig.Map.Margin;
                        int c = cx >= 0 && cy >= 0 && cx < rig.CardW && cy < rig.CardH ? c1[cy * rig.CardW + cx] : 0;
                        Say("     ({0},{1}) fast {2:X8} full {3:X8} content {4:X8}", x, y, fast[i], rig.Out[i], c);
                        shown++;
                    }
                }
                Scene.Save(mask, rig.Map.W, rig.Map.H, Path.Combine(imgDir, string.Format(CultureInfo.InvariantCulture, "mismatch_{0}_{1}.png", kind.Name, k)));
            }
        }

        // Each frame of a pattern on its own, to see what a frame costs in sequence.
        static void Probe(Kind kind, float k, Scene scene)
        {
            Rig rig = new Rig(kind, k, scene);
            Rectangle area = rig.Area;
            Scene whole = scene.Changed(new Rectangle(area.Left - 10, area.Top - 10, area.Width + 20, area.Height + 20), 7);
            Scene video = scene.Changed(new Rectangle(area.Left + area.Width / 2 - 240, area.Top + area.Height / 2 - 135, 480, 270), 6);
            int[] base0 = Picture(rig, scene), wholePx = Picture(rig, whole), videoPx = Picture(rig, video);
            int[] c0 = rig.MakeContent(0);
            rig.Feed(scene);
            rig.Adapt();
            rig.Frame(c0);
            Stopwatch t = new Stopwatch();
            StringBuilder line = new StringBuilder();
            int[][] seq = { wholePx, base0, wholePx, base0, wholePx, base0, videoPx, base0, videoPx, base0, videoPx, base0 };
            for (int i = 0; i < seq.Length; i++)
            {
                int[] into = rig.Backdrop.Buffer(rig.Map);
                Array.Copy(seq[i], into, seq[i].Length);
                t.Restart();
                bool took = rig.Backdrop.Accept(rig.Map, false);
                double a = Ms(t);
                Rectangle d = took ? rig.FrameDirty(c0) : Rectangle.Empty;
                double b = Ms(t);
                line.AppendFormat("  [{0} {1:0.00}+{2:0.00} {3}x{4}{5}]", i < 6 ? "whole" : "video", a, b - a, d.Width, d.Height, rig.Backdrop.Partial ? " part" : "");
            }
            Say("probe {0} @{1}:{2}", kind.Name, k, line);
        }

        static int Differ(int[] a, int[] b)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++;
            return n;
        }

        // ------------------------------------------------------------------------------------------
        // CPU ms a second for realistic patterns of change behind and on the glass. Only the engine's
        // own work (what the live feed hands in is made beforehand).
        static void Energy(Kind kind, float k, Scene scene)
        {
            Rig rig = new Rig(kind, k, scene);
            Rectangle area = rig.Area;
            // what changes: a caret (a few pixels), a clock digit (small box), a line of text, a video
            // (a 480x270 box), the whole area (a window switched)
            Scene caret = scene.Changed(new Rectangle(area.Left + area.Width / 2, area.Top + area.Height / 2, 2, 18), 3);
            Scene clock = scene.Changed(new Rectangle(area.Left + area.Width * 3 / 4, area.Top + area.Height / 3, 30, 20), 4);
            Scene text = scene.Changed(new Rectangle(area.Left + area.Width / 5, area.Top + area.Height / 2, area.Width * 3 / 5, 24), 5);
            Scene video = scene.Changed(new Rectangle(area.Left + area.Width / 2 - 240, area.Top + area.Height / 2 - 135, 480, 270), 6);
            Scene whole = scene.Changed(new Rectangle(area.Left - 10, area.Top - 10, area.Width + 20, area.Height + 20), 7);
            // pictures as the feed would bring them
            int[] base0 = Picture(rig, scene), caretPx = Picture(rig, caret), clockPx = Picture(rig, clock), textPx = Picture(rig, text), videoPx = Picture(rig, video), wholePx = Picture(rig, whole);
            int[] c0 = rig.MakeContent(0), c1 = rig.MakeContent(1);
            rig.Feed(scene);
            rig.Adapt();
            rig.Frame(c0);
            Stopwatch t = new Stopwatch();
            string[] names = { "idle", "caret 2/s", "clock 1/s", "typing 8/s", "video 30/s", "switch 2/s", "slider 60/s" };
            int[] perSecond = { 0, 2, 1, 8, 30, 2, 60 };
            int[][] pics = { null, caretPx, clockPx, textPx, videoPx, wholePx, null };
            StringBuilder line = new StringBuilder();
            line.AppendFormat("{0,-5} @{1,-4}", kind.Name, k);
            for (int p = 0; p < names.Length; p++)
            {
                double ms = 0;
                int n = perSecond[p];
                StringBuilder each = verbose ? new StringBuilder() : null;
                for (int i = 0; i < n; i++)
                {
                    int[] pic = (i & 1) == 0 ? pics[p] : base0;
                    double before = Ms(t);
                    if (pic != null)
                    {
                        int[] into = rig.Backdrop.Buffer(rig.Map);
                        Array.Copy(pic, into, pic.Length);
                        t.Start();
                        if (rig.Backdrop.Accept(rig.Map, false)) rig.FrameDirty(c0);
                        t.Stop();
                    }
                    else
                    {
                        t.Start();
                        rig.FrameDirty((i & 1) == 0 ? c1 : c0);
                        t.Stop();
                    }
                    if (each != null) each.AppendFormat(" {0:0.0}", Ms(t) - before);
                }
                if (each != null) Say("   {0}:{1}", names[p], each);
                ms = Ms(t);
                t.Reset();
                line.AppendFormat("  {0} {1:0.0}", names[p], ms);
            }
            Say(line.ToString());
        }

        static int[] Picture(Rig rig, Scene s)
        {
            int[] px = new int[rig.Map.SharpW * rig.Map.SharpH];
            s.ReadArea(rig.Area, rig.Map.SharpFactor, px, rig.Map.SharpW, rig.Map.SharpH);
            return px;
        }

        // ------------------------------------------------------------------------------------------
        // One picture of every kind (rows) over every scene (columns) at one scale, each glass cropped
        // with a margin round it, for judging the look side by side. light: also each over the scene
        // with Appearance forced light (a second row per kind) when the scene alone would not.
        static void Sheet(List<string> kinds, float k, List<string> scenes, Dictionary<string, Scene> made, string path)
        {
            List<int[]> cells = new List<int[]>();
            List<int> cw = new List<int>(), ch = new List<int>();
            int cols = scenes.Count, rowsN = kinds.Count;
            Stopwatch t = new Stopwatch();
            double mapMs = 0;
            foreach (string kn in kinds)
                foreach (string sn in scenes)
                {
                    t.Restart();
                    Rig rig = new Rig(Find(kn), k, made[sn]);
                    mapMs += Ms(t);
                    rig.Feed(made[sn]);
                    rig.Adapt();
                    string sm = Environment.GetEnvironmentVariable("LGSHEETMOTION");   // ("bend,light,swell,lean,ripple": a frame of the glass moving)
                    if (!string.IsNullOrEmpty(sm))
                    {
                        string[] p = sm.Split(',');
                        GlassMotion m = GlassMotion.Neutral;
                        m.Bend = float.Parse(p[0], CultureInfo.InvariantCulture); m.Light = float.Parse(p[1], CultureInfo.InvariantCulture);
                        m.Swell = float.Parse(p[2], CultureInfo.InvariantCulture); m.SwellX = rig.CardW * 0.2f; m.SwellY = rig.CardH * 0.4f; m.SwellRadius = Math.Min(rig.CardW, rig.CardH) * 0.9f;
                        m.Lean = float.Parse(p[3], CultureInfo.InvariantCulture); m.LeanX = rig.CardW * 0.2f; m.LeanY = rig.CardH * 0.4f;
                        m.Ripple = float.Parse(p[4], CultureInfo.InvariantCulture); m.RippleLength = rig.CardW * 0.7f; m.RipplePhase = 0.5f;
                        rig.Map.Motion = m;
                    }
                    rig.Frame(rig.Content);
                    int w, h;
                    cells.Add(rig.Composite((int)(16 * k), out w, out h));
                    cw.Add(w); ch.Add(h);
                }
            int[] colW = new int[cols], rowH = new int[rowsN];
            for (int r = 0; r < rowsN; r++)
                for (int c = 0; c < cols; c++) { colW[c] = Math.Max(colW[c], cw[r * cols + c]); rowH[r] = Math.Max(rowH[r], ch[r * cols + c]); }
            int gap = 4, W = gap, H = gap;
            foreach (int v in colW) W += v + gap;
            foreach (int v in rowH) H += v + gap;
            int[] sheet = new int[W * H];
            int y0 = gap;
            for (int r = 0; r < rowsN; r++)
            {
                int x0 = gap;
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    for (int y = 0; y < ch[i]; y++) Array.Copy(cells[i], y * cw[i], sheet, (y0 + y) * W + x0, cw[i]);
                    x0 += colW[c] + gap;
                }
                y0 += rowH[r] + gap;
            }
            Scene.Save(sheet, W, H, path);
            Say("sheet written: {0} ({1}x{2}), {3} maps built in {4:0} ms", path, W, H, cells.Count, mapMs);
        }

        // ------------------------------------------------------------------------------------------
        // The glass moving (GlassMotion), event by event at 60 frames a second, each ending on the settling frame:
        // what the engine spends (Shade only; the app's warp and present are as before), against a whole backdrop
        // change on the same glass.
        static int[] MotionOrder = { 0, 1, 2, 3 };
        static void MotionBench(Kind kind, float k, Scene scene)
        {
            Rig rig = new Rig(kind, k, scene);
            Rectangle area = rig.Area;
            Scene whole = scene.Changed(new Rectangle(area.Left - 10, area.Top - 10, area.Width + 20, area.Height + 20), 1);
            rig.Feed(scene);
            rig.Adapt();
            rig.Frame(rig.Content);
            Stopwatch t = new Stopwatch();
            // a whole backdrop change, for comparison (median of 9)
            List<double> change = new List<double>();
            Scene[] pair = { whole, scene };
            for (int i = 0; i < 10; i++)
            {
                int[] into = rig.Backdrop.Buffer(rig.Map);
                pair[i & 1].ReadArea(area, rig.Map.SharpFactor, into, rig.Map.SharpW, rig.Map.SharpH);
                t.Restart();
                rig.Backdrop.Accept(rig.Map, false);
                rig.FrameDirty(rig.Content);
                if (i > 0) change.Add(Ms(t));
            }
            change.Sort();
            double bd = change[0];   // (the least disturbed: other work on the machine only ever adds)
            float W = rig.CardW, H = rig.CardH;
            StringBuilder line = new StringBuilder();
            line.AppendFormat("motion {0,-5} @{1,-4} bd-chg {2:0.00} ms (best of 9) |", kind.Name, k, bd);
            for (int evi = 0; evi < MotionOrder.Length; evi++)
            {
                int ev = MotionOrder[evi];
                List<GlassMotion> frames = new List<GlassMotion>();
                string name;
                if (ev == 0)
                {
                    // arrival: condensing, with a small damped overshoot as it lands
                    name = "arrival";
                    for (int f = 1; f <= 20; f++)
                    {
                        float u = f / 20f - 1, s = 1 + 2.2f * u * u * u + 1.2f * u * u;
                        GlassMotion m = GlassMotion.Neutral; m.Bend = s; m.Light = Math.Min(1.15f, s * 1.05f);
                        frames.Add(m);
                    }
                }
                else if (ev == 1)
                {
                    // hover: 2 s of a pointer moving across, the lensing swelling under it and the light leaning to it
                    name = "hover2s";
                    for (int f = 0; f < 120; f++)
                    {
                        float on = Math.Min(1f, f / 10f), x = W * (0.1f + 0.8f * f / 119f), y = H * (0.5f + 0.2f * (float)Math.Sin(f * 0.1));
                        GlassMotion m = GlassMotion.Neutral; m.Swell = 0.15f * on; m.SwellX = x; m.SwellY = y; m.SwellRadius = Math.Min(W, H) * 0.8f;
                        m.Lean = 0.5f * on; m.LeanX = x; m.LeanY = y;
                        frames.Add(m);
                    }
                    for (int f = 1; f <= 15; f++)
                    {
                        float on = 1 - f / 15f;
                        GlassMotion m = GlassMotion.Neutral; m.Swell = 0.15f * on; m.SwellX = W * 0.9f; m.SwellY = H * 0.5f; m.SwellRadius = Math.Min(W, H) * 0.8f;
                        m.Lean = 0.5f * on; m.LeanX = W * 0.9f; m.LeanY = H * 0.5f;
                        if (on <= 0) m = GlassMotion.Neutral;
                        frames.Add(m);
                    }
                }
                else if (ev == 2)
                {
                    // press: compressed (thinner, brighter rim), springing back with one damped wobble
                    name = "press";
                    for (int f = 1; f <= 24; f++)
                    {
                        float tt = f / 60f, p = (float)(Math.Exp(-tt / 0.12) * Math.Cos(2 * Math.PI * tt / 0.32));
                        GlassMotion m = GlassMotion.Neutral; m.Bend = 1 - 0.3f * p; m.Light = 1 + 0.4f * p;
                        frames.Add(m);
                    }
                }
                else
                {
                    // a level step on an indicator: a ripple running across and dying away
                    name = "ripple";
                    for (int f = 1; f <= 30; f++)
                    {
                        GlassMotion m = GlassMotion.Neutral; m.Ripple = 0.25f * (float)Math.Exp(-f / 10.0); m.RippleLength = W * 0.6f; m.RipplePhase = f * 0.6f;
                        frames.Add(m);
                    }
                }
                frames.Add(GlassMotion.Neutral);   // (the settling frame: the static glass)
                double sum = double.MaxValue, most = 0, viewMs = 0, composeMs = 0;
                for (int pass = 0; pass < 3; pass++)   // (three times, keeping the least disturbed)
                {
                    double passSum = 0, passMost = 0;
                    long v0 = GlassMap.PhaseTicks[1], c0 = GlassMap.PhaseTicks[3];
                    foreach (GlassMotion m in frames)
                    {
                        rig.Map.Motion = m;
                        t.Restart();
                        rig.FrameDirty(rig.Content);
                        double ms = Ms(t);
                        passSum += ms;
                        if (ms > passMost) passMost = ms;
                    }
                    if (passSum < sum) { sum = passSum; most = passMost; viewMs = (GlassMap.PhaseTicks[1] - v0) * 1000.0 / Stopwatch.Frequency; composeMs = (GlassMap.PhaseTicks[3] - c0) * 1000.0 / Stopwatch.Frequency; }
                }
                line.AppendFormat("  {0} {1} fr {2:0.0} ms ({3:0.00}/fr, max {4:0.00}; view {5:0.00}, draw {6:0.00} /fr)", name, frames.Count, sum, sum / frames.Count, most, viewMs / frames.Count, composeMs / frames.Count);
            }
            Say(line.ToString());
        }

        // ------------------------------------------------------------------------------------------
        // The look at each elevation, side by side.
        static void ElevationStrip(Kind kind, float k, Scene scene)
        {
            int[] levels = { 0, 25, 50, 75, 100 };
            List<int[]> pics = new List<int[]>();
            int cw = 0, ch = 0;
            foreach (int e in levels)
            {
                Kind at = new Kind(kind.Name, kind.PtW, kind.PtH, kind.Look, kind.Lens);
                at.Look.Elevation = e;
                Rig rig = new Rig(at, k, scene);
                rig.Feed(scene);
                rig.Adapt();
                rig.Frame(rig.Content);
                pics.Add(rig.Composite((int)(40 * k), out cw, out ch));
            }
            int gap = 8, w = cw * pics.Count + gap * (pics.Count - 1);
            int[] strip = new int[w * ch];
            for (int i = 0; i < pics.Count; i++)
                for (int y = 0; y < ch; y++) Array.Copy(pics[i], y * cw, strip, y * w + i * (cw + gap), cw);
            Scene.Save(strip, w, ch, Path.Combine(imgDir, string.Format(CultureInfo.InvariantCulture, "elevation_{0}_{1}.png", kind.Name, k)));
            Say("elevation strip written for {0} @{1}: 0, 25, 50, 75, 100", kind.Name, k);
        }

        // ------------------------------------------------------------------------------------------
        // The surface service's render thread, off screen: how precisely it paces frames, how many
        // times a second it wakes when idle, and what it costs.
        static void ServiceBench()
        {
            Say("");
            Say(GlassService.SelfTest());
            GlassService svc = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.InProcess });
            try
            {
                GlassSpec spec = Contract.Spec("LGBench service test (off screen)");
                spec.AnimIn = 1200;
                GlassSurface s = svc.Create(spec);
                int hidden = 0;
                s.Hidden += delegate { System.Threading.Interlocked.Increment(ref hidden); };
                s.SetContents(new int[spec.Size.Width * spec.Size.Height], spec.Size.Width, spec.Size.Height, Rectangle.Empty);
                // 2 s of animation: the card arrives (1.2 s) and from 1.0 s on glides back and forth (the first glide goes away from
                // where it is: a glide to its own place would leave a gap with nothing to animate after the arrival)
                Stopwatch sw = Stopwatch.StartNew();
                int k = 0;
                s.Show();
                while (sw.ElapsedMilliseconds < 2000)
                {
                    if (sw.ElapsedMilliseconds > 1000 + k * 300)
                    {
                        s.SetPlace(new Point(-10000 + ((k + 1) % 2) * 700, -10000), true);
                        k++;
                    }
                    Contract.Pump(5, null);
                }
                GlassPacingInfo busy = svc.Pacing;
                Say("service [{0}]: animating 2.0 s: paced frames {1}, presented by a call {2}; pacing jitter |start - intended| mean {3:0.00} ms p99 {4:0.00} ms; presented before the targeted vblank {5:0}%",
                    svc.Transport, busy.Frames, s.Frames, busy.MeanJitterMs, busy.P99JitterMs, busy.BeforeVblankShare * 100);
                // idle: hidden, nothing due
                s.Hide();
                Contract.Pump(4000, delegate { return System.Threading.Thread.VolatileRead(ref hidden) > 0; });
                Contract.Pump(300, null);
                long w0 = svc.Pacing.Wakeups;
                TimeSpan cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
                Stopwatch idle = Stopwatch.StartNew();
                System.Threading.Thread.Sleep(2000);
                double secs = idle.Elapsed.TotalSeconds;
                long wakes = svc.Pacing.Wakeups - w0;
                double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
                Say("service [{0}]: idle {1:0.0} s: render-thread wakeups {2} ({3:0.0}/s), process cpu {4:0} ms; {5}",
                    svc.Transport, secs, wakes, wakes / secs, cpuMs, svc.Pacing);
            }
            finally
            {
                svc.Dispose();
            }
        }
    }
}
