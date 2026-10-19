using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LGEngine;

namespace LGBench
{
    // -proof DIR: picture proof of the friend's C++ app (LiquidGlassControl, native\) glass animations.
    // The chain: (1) the C++ client's wire bytes, ported to C# here and asserted against the golden bytes of
    // tests\lgengine_wire_test.cpp, go over a real pipe to a real in-process GlassHostServer, whose decode is read back with
    // TestProps; (2) the frames come from the engine's SurfaceRuntime run off screen by hand, fed with the values the host decoded;
    // (3) the contents (glyph, track, fill, knob, number, chips) are a C# PORT of native\osd.h and the chips of native\main.cpp.
    // The C++ exe itself is not run. The cursor is never moved: GlassGlow.TestCursor stands in for it.
    internal static class Proof
    {
        static int checks, okChecks;

        static void Check(bool ok, string what)
        {
            checks++;
            if (ok) okChecks++;
            else Program.Say("   proof FAIL: {0}", what);
        }

        static bool Until(int ms, Func<bool> f)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                if (f()) return true;
                Thread.Sleep(2);
            }
            return f();
        }

        // ---------------------------------------------------------------- the wire (port of lg_client.h wire::)

        sealed class WW
        {
            public readonly List<byte> B = new List<byte>();
            public void U8(int n) { B.Add((byte)(n & 255)); }
            public void U16(int n) { U8(n); U8(n >> 8); }
            public void U32(uint n) { for (int i = 0; i < 4; i++) U8((int)((n >> (8 * i)) & 255)); }
            public void I32(int n) { U32(unchecked((uint)n)); }
            public void F32(float f) { U32(BitConverter.ToUInt32(BitConverter.GetBytes(f), 0)); }
            public void Raw(byte[] p) { B.AddRange(p); }
            public void Str(string s) { byte[] b = Encoding.UTF8.GetBytes(s); U16(b.Length); Raw(b); }
            public byte[] Bytes() { return B.ToArray(); }
        }

        sealed class WBtn
        {
            public int L, T, R, Bo;                 // hit, left/top/right/bottom
            public float Cx, Cy, Hx, Hy, Rad;
            public int Style, Mark;
            public int ML, MT, MR, MB;              // mask box
            public byte[] Mask = new byte[0];
            public Rectangle Hit() { return Rectangle.FromLTRB(L, T, R, Bo); }
        }

        sealed class CSpec
        {
            public int W = 420, H = 200, X = 100, Y = 100, Kind;
            public float Scale = 1f;
            public bool Adapt = true, Hover = true, SavePower = true, InShots;
            public int Fps = 15, Motion = 4, InMs = 380, OutMs = 230, Zoom = 10, Liquid = 60, Bounce = 45, Travel = -1, Indicator = -1;
            public int[] Look = new int[] { 35, 30, 80, 60, 28, 12, 100, 50 };
            public string Label = "LiquidGlassControl.Host", Profile = "";
        }

        static class Wire
        {
            public const int Hello = 1, HelloAck = 2, Create = 3, CreateAck = 4, SetMotionEx = 10, SetSlosh = 11, Kick = 12, SetButtons = 13, SetPointerArea = 14, SetBusy = 16, Cut = 17;

            public static byte[] Frame(int type, uint seq, WW p)
            {
                WW w = new WW();
                w.U32(0x3145474c);
                w.U16(1);
                w.U16(type);
                w.U32((uint)p.B.Count);
                w.U32(seq);
                w.B.AddRange(p.B);
                return w.Bytes();
            }

            static void Rect(WW w, int l, int t, int r, int b) { w.I32(l); w.I32(t); w.I32(r - l); w.I32(b - t); }

            public static WW HelloMsg(int minor) { WW w = new WW(); w.U16(1); w.U16(1); w.Str("LiquidGlassControl C++"); w.U16(minor); return w; }

            public static WW CreateMsg(uint cookie, CSpec s)
            {
                WW w = new WW();
                w.U32(cookie); w.U8(s.Kind); w.F32(s.Scale);
                w.U32((uint)s.W); w.U32((uint)s.H); w.U32(unchecked((uint)s.X)); w.U32(unchecked((uint)s.Y));
                w.U8((s.InShots ? 1 : 0) | (s.SavePower ? 2 : 0) | (s.Adapt ? 4 : 0) | (s.Hover ? 8 : 0));
                w.U32((uint)s.Fps); w.U32((uint)s.Motion); w.U32((uint)s.InMs); w.U32((uint)s.OutMs);
                w.I32(s.Zoom); w.I32(s.Liquid); w.I32(s.Bounce);
                for (int i = 0; i < 8; i++) w.I32(s.Look[i]);
                w.Str(s.Label); w.Str(s.Profile);
                return w;
            }

            public static WW SetMotionExMsg(uint id, int zoom, int liquid, int bounce, int travel, int indicator)
            {
                WW w = new WW(); w.U32(id);
                w.I32(zoom); w.I32(liquid); w.I32(bounce); w.I32(travel); w.I32(indicator);
                return w;
            }

            public static WW SetSloshMsg(uint id, int style, int amount) { WW w = new WW(); w.U32(id); w.I32(style); w.I32(amount); return w; }
            public static WW KickMsg(uint id, float push) { WW w = new WW(); w.U32(id); w.F32(push); return w; }

            // 0 = no kick. A pulse (style 2) always pushes the same way.
            public static float KickPush(int style, int amount, int way)
            {
                if (amount <= 0 || style < 0 || style >= 3) return 0f;
                if (style == 2) way = 1;
                return (float)way * 4.4f * (float)amount / 60f;
            }

            public static WW SetButtonsMsg(uint id, List<WBtn> list, bool carry)
            {
                int size = 7;
                foreach (WBtn b in list) size += 64 + b.Mask.Length;
                bool masks = size <= 65536;
                WW w = new WW();
                w.U32(id); w.U8(carry ? 1 : 0); w.U16(list.Count);
                foreach (WBtn b in list)
                {
                    Rect(w, b.L, b.T, b.R, b.Bo);
                    w.F32(b.Cx); w.F32(b.Cy); w.F32(b.Hx); w.F32(b.Hy); w.F32(b.Rad);
                    w.I32(b.Style); w.I32(b.Mark);
                    Rect(w, b.ML, b.MT, b.MR, b.MB);
                    if (masks && b.Mask.Length > 0) { w.U32((uint)b.Mask.Length); w.Raw(b.Mask); }
                    else w.U32(0);
                }
                return w;
            }

            public static WW SetPointerAreaMsg(uint id, int l, int t, int r, int b) { WW w = new WW(); w.U32(id); Rect(w, l, t, r, b); return w; }
            public static WW SetBusyMsg(uint id, bool busy) { WW w = new WW(); w.U32(id); w.U8(busy ? 1 : 0); return w; }
            public static WW CutMsg(uint id) { WW w = new WW(); w.U32(id); return w; }
        }

        static byte[] Cat(params int[][] parts)
        {
            List<byte> r = new List<byte>();
            foreach (int[] p in parts) foreach (int v in p) r.Add((byte)v);
            return r.ToArray();
        }

        static int[] Ascii(string s) { int[] r = new int[s.Length]; for (int i = 0; i < s.Length; i++) r[i] = s[i]; return r; }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // The golden bytes of tests\lgengine_wire_test.cpp, reproduced by this port.
        static void Golden()
        {
            int[] name = Ascii("LiquidGlassControl C++");
            Check(Same(Wire.HelloMsg(1).Bytes(), Cat(new int[] { 1, 0, 1, 0, 0x16, 0 }, name, new int[] { 1, 0 })), "golden: Hello carries clientMinor 1");
            Check(Same(Wire.HelloMsg(0).Bytes(), Cat(new int[] { 1, 0, 1, 0, 0x16, 0 }, name, new int[] { 0, 0 })), "golden: Hello with minor 0");
            Check(Same(Wire.Frame(17, 5, Wire.CutMsg(7)), Cat(new int[] { 0x4c, 0x47, 0x45, 0x31, 1, 0, 17, 0, 4, 0, 0, 0, 5, 0, 0, 0, 7, 0, 0, 0 })), "golden: Cut frame (type 17)");
            Check(Same(Wire.SetMotionExMsg(2, 10, 60, 45, -1, 8).Bytes(), Cat(new int[] { 2, 0, 0, 0, 10, 0, 0, 0, 0x3c, 0, 0, 0, 0x2d, 0, 0, 0, 0xff, 0xff, 0xff, 0xff, 8, 0, 0, 0 })), "golden: SetMotionEx payload");
            Check(Same(Wire.SetSloshMsg(3, 2, 100).Bytes(), Cat(new int[] { 3, 0, 0, 0, 2, 0, 0, 0, 0x64, 0, 0, 0 })), "golden: SetSlosh payload");
            Check(Same(Wire.KickMsg(4, 2.5f).Bytes(), Cat(new int[] { 4, 0, 0, 0, 0, 0, 0x20, 0x40 })), "golden: Kick +2.5");
            Check(Same(Wire.KickMsg(4, -1f).Bytes(), Cat(new int[] { 4, 0, 0, 0, 0, 0, 0x80, 0xbf })), "golden: Kick -1");
            WBtn one = new WBtn();
            one.L = 10; one.T = 20; one.R = 110; one.Bo = 70;
            one.Cx = 1; one.Cy = 2; one.Hx = 3; one.Hy = 4; one.Rad = .5f; one.Style = 1; one.Mark = 2;
            one.ML = 0; one.MT = 0; one.MR = 2; one.MB = 2; one.Mask = new byte[] { 1, 2, 3, 4 };
            List<WBtn> list = new List<WBtn>();
            list.Add(one);
            Check(Same(Wire.SetButtonsMsg(5, list, true).Bytes(), Cat(
                new int[] { 5, 0, 0, 0, 1, 1, 0 },
                new int[] { 0x0a, 0, 0, 0, 0x14, 0, 0, 0, 0x64, 0, 0, 0, 0x32, 0, 0, 0 },
                new int[] { 0, 0, 0x80, 0x3f, 0, 0, 0, 0x40, 0, 0, 0x40, 0x40, 0, 0, 0x80, 0x40, 0, 0, 0, 0x3f },
                new int[] { 1, 0, 0, 0, 2, 0, 0, 0 },
                new int[] { 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0 },
                new int[] { 4, 0, 0, 0, 1, 2, 3, 4 })), "golden: SetButtons with one button and its mask");
            Check(Same(Wire.SetButtonsMsg(5, new List<WBtn>(), false).Bytes(), Cat(new int[] { 5, 0, 0, 0, 0, 0, 0 })), "golden: SetButtons with no buttons");
            Check(Same(Wire.SetPointerAreaMsg(6, 10, 20, 110, 70).Bytes(), Cat(new int[] { 6, 0, 0, 0, 0x0a, 0, 0, 0, 0x14, 0, 0, 0, 0x64, 0, 0, 0, 0x32, 0, 0, 0 })), "golden: SetPointerArea payload");
            Check(Same(Wire.SetBusyMsg(8, true).Bytes(), Cat(new int[] { 8, 0, 0, 0, 1 })), "golden: SetBusy payload");
            CSpec z = new CSpec();
            z.Zoom = 20; z.Liquid = 30; z.Bounce = 40;
            byte[] created = Wire.CreateMsg(1, z).Bytes();
            Check(created[42] == 20 && created[46] == 30 && created[50] == 40, "golden: Create carries zoom, liquid, bounce at payload byte 42");
            Check(Math.Abs(Wire.KickPush(0, 60, -1) + 4.4f) < 1e-5f && Math.Abs(Wire.KickPush(1, 30, 1) - 2.2f) < 1e-5f, "golden: kickPush lean and bounce scale with the amount");
            Check(Math.Abs(Wire.KickPush(2, 60, -1) - 4.4f) < 1e-5f && Wire.KickPush(0, 0, 1) == 0f && Wire.KickPush(3, 60, 1) == 0f, "golden: kickPush pulse forces way 1; none and zero amount are 0");
        }

        // ---------------------------------------------------------------- the raw pipe session

        sealed class Sess : IDisposable
        {
            readonly NamedPipeClientStream p;
            public readonly byte[] Buf = new byte[70000];
            uint seq;

            public Sess(string pipeName)
            {
                p = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                p.Connect(2000);
                p.ReadMode = PipeTransmissionMode.Message;
            }

            public void Send(int type, WW payload)
            {
                byte[] f = Wire.Frame(type, ++seq, payload);
                p.Write(f, 0, f.Length);
            }

            // Reads replies until one of the wanted type arrives (events in between are skipped). Length, or -1.
            public int ReadType(int want, int ms)
            {
                Stopwatch sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms)
                {
                    int n;
                    try
                    {
                        IAsyncResult ar = p.BeginRead(Buf, 0, Buf.Length, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(Math.Max(1, ms - (int)sw.ElapsedMilliseconds))) return -1;
                        n = p.EndRead(ar);
                    }
                    catch (Exception) { return -1; }
                    if (n <= 0) return -1;
                    if (n >= 16 && BitConverter.ToUInt16(Buf, 6) == want) return n;
                }
                return -1;
            }

            public void Dispose() { p.Dispose(); }
        }

        sealed class HostSurf
        {
            public uint Id;
            public GlassSection Sec;
            public int W, H, Version;
            public string Label;
            public Point Place;
        }

        // What the host decoded (TestProps, TryGetLook) and the pixels both halves share.
        sealed class Decoded
        {
            public Dictionary<string, string> Card, Lens;
            public GlassLook CardLook, LensLook;
            public int[] CardPx, LensPx;
            public List<WBtn> Chips;
            public CSpec CardSpec, LensSpec;
        }

        static Dictionary<string, string> Parse(string s)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            if (s == null) return d;
            foreach (string part in s.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) d[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            return d;
        }

        static string Q(Dictionary<string, string> d, string k) { string v; return d != null && d.TryGetValue(k, out v) ? v : "?"; }
        static int QI(Dictionary<string, string> d, string k) { int v; return int.TryParse(Q(d, k), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -999; }

        static void Eq(Dictionary<string, string> d, string who, string k, string want)
        {
            Check(Q(d, k) == want, who + ": host decoded " + k + "=" + Q(d, k) + ", sent " + want);
        }

        static void Publish(HostSurf s, bool shown, bool hover, int[] px, EventWaitHandle wake)
        {
            if (px != null) s.Sec.PublishContents(px, new Rectangle(0, 0, s.W, s.H));
            SurfaceProps p = new SurfaceProps();
            p.Version = ++s.Version;
            p.Place = s.Place;
            p.WantShown = shown;
            p.Hover = hover;
            p.Opacity = 1f;
            p.MotionStyle = 4;
            p.MotionIn = 380;
            p.MotionOut = 230;
            p.CallStamp = Stopwatch.GetTimestamp();
            s.Sec.WriteState(p);
            if (wake != null) wake.Set();
        }

        static HostSurf MakeSurf(Sess ss, CSpec spec, uint cookie, uint seqHint)
        {
            ss.Send(Wire.Create, Wire.CreateMsg(cookie, spec));
            int n = ss.ReadType(Wire.CreateAck, 3000);
            if (n < 0) return null;
            byte[] b = ss.Buf;
            int o = 16;
            uint incoming = BitConverter.ToUInt32(b, o); o += 4;
            uint surface = BitConverter.ToUInt32(b, o); o += 4;
            int len = BitConverter.ToUInt16(b, o); o += 2;
            string name = Encoding.UTF8.GetString(b, o, len); o += len;
            int bytes = BitConverter.ToInt32(b, o); o += 4;
            int w = BitConverter.ToInt32(b, o); o += 4;
            int h = BitConverter.ToInt32(b, o);
            Check(incoming == cookie && surface != 0 && w == spec.W && h == spec.H && bytes == 320 + 8 * w * h, "CreateAck of " + spec.Label + " echoes cookie, size and section bytes");
            HostSurf s = new HostSurf();
            s.Id = surface; s.W = w; s.H = h; s.Label = spec.Label; s.Place = new Point(spec.X, spec.Y);
            s.Sec = GlassSection.Open(name, w, h, bytes);
            return s;
        }

        // ---------------------------------------------------------------- the contents (PORT of native\osd.h and main.cpp)

        static int Lround(double v) { return (int)Math.Round(v, MidpointRounding.AwayFromZero); }
        static float Clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
        static readonly int Ink = unchecked((int)0xFEFEFEFEu);

        sealed class Knob
        {
            const float SliderSpring = 52f, SliderDamping = 1f, LiftSpring = 26f, LiftDamping = .9f, LiftHold = .5f;
            public float At = -1, Speed, Goal, Lift, LiftSpeed, Flow, Hold;
            public void Jump(float v) { At = Goal = Clamp01(v); Speed = Lift = LiftSpeed = Flow = Hold = 0; }
            public void Set(float v)
            {
                v = Clamp01(v);
                if (At < 0) { Jump(v); return; }
                if (Math.Abs(v - Goal) > .0005f) { Hold = LiftHold; if ((v - At) * Speed < 0) Speed = 0; Goal = v; }
            }
            public bool Moving() { return Math.Abs(Goal - At) > .002f || Math.Abs(Speed) > .02f; }
            public bool Active() { return Moving() || Hold > 0 || Lift != 0 || LiftSpeed != 0; }
            public bool Step(float dt)
            {
                if (At < 0) return false;
                dt = Math.Min(.034f, Math.Max(0f, dt));
                float wasAt = At, wasLift = Lift;
                bool going = Moving();
                float liftGoal = going || Hold > 0 ? 1f : 0f;
                Hold = Math.Max(0f, Hold - dt);
                for (float left = dt; left > 0; left -= .005f)
                {
                    float s = Math.Min(.005f, left), before = Goal - At;
                    Speed += (SliderSpring * SliderSpring * (Goal - At) - 2 * SliderDamping * SliderSpring * Speed) * s;
                    At += Speed * s;
                    if (before != 0 && (Goal - At) * before <= 0) { At = Goal; Speed = 0; }
                    LiftSpeed += (LiftSpring * LiftSpring * (liftGoal - Lift) - 2 * LiftDamping * LiftSpring * LiftSpeed) * s;
                    Lift += LiftSpeed * s;
                    Flow += (Speed - Flow) * Math.Min(1f, s * 22f);
                }
                if (!going && Math.Abs(Goal - At) <= .002f && Math.Abs(Speed) <= .02f) { At = Goal; Speed = 0; }
                if (liftGoal == 0 && Math.Abs(Lift) < .004f && Math.Abs(LiftSpeed) < .05f) { Lift = LiftSpeed = 0; Flow = 0; }
                return At != wasAt || Lift != wasLift;
            }
        }

        // The volume source only (the sample sweep and brightness are not used here). The number is the REAL level (the knob's goal),
        // not the gliding knob: it jumps at once.
        sealed class OsdState
        {
            public Knob Knob = new Knob();
            public bool Muted;
            float vol = -1;
            bool volInit;
            public int OnVolume(float level, bool mute)
            {
                level = Clamp01(level);
                if (!volInit) { volInit = true; vol = level; Muted = mute; Knob.Jump(level); return 0; }
                int way = 0;
                if (Math.Abs(level - vol) >= .0005f) way = level > vol ? 1 : -1;
                else if (mute != Muted) way = mute ? -1 : 1;
                vol = level; Muted = mute;
                if (way == 0) return 0;
                Knob.Set(level);
                return way;
            }
            public bool Tick(float dt) { return Knob.Step(dt); }
            public int Percent() { return Knob.At < 0 ? 0 : Lround(Clamp01(Knob.Goal) * 100f); }
            public char Glyph() { return Muted ? (char)0xE74F : (char)0xE767; }
        }

        sealed class Layout
        {
            public int W, H;
            public float K, Cy, BarL, BarR, BarH;
            public Rectangle Strip, GlyphBox, NumBox;
        }

        static Layout MakeLayout(int W, int H, float k)
        {
            Layout L = new Layout();
            L.W = W; L.H = H; L.K = k; L.Cy = H / 2f; L.BarH = 6 * k; L.BarL = 66 * k; L.BarR = W - 66 * k;
            L.Strip = Rectangle.FromLTRB(Math.Max(0, Lround(L.BarL - 14 * k)), 0, Math.Min(W, Lround(L.BarR + 14 * k)), H);
            L.GlyphBox = Rectangle.FromLTRB(Lround(14 * k), 0, Lround(46 * k), H);
            L.NumBox = Rectangle.FromLTRB(W - Lround(52 * k), 0, W - Lround(10 * k), H);
            return L;
        }

        static void Lay(ref float a, ref float c, float la, float lc) { a = la + a * (1 - la); c = lc + c * (1 - la); }

        static float Capsule(float px, float py, float x0, float x1, float y, float r)
        {
            float dy = py - y;
            if (px >= x0 && px <= x1) return Math.Abs(dy) - r;
            float dx = px < x0 ? x0 - px : px - x1;
            return (float)Math.Sqrt(dx * dx + dy * dy) - r;
        }

        static float PillD(float px, float py, float cx, float cy, float hx, float hy)
        {
            float r = Math.Min(hx, hy), qx = Math.Abs(px - cx) - (hx - r), qy = Math.Abs(py - cy) - (hy - r), ox = Math.Max(qx, 0f), oy = Math.Max(qy, 0f);
            return (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0f) - r;
        }

        static float Cover(float d) { return d <= -.5f ? 1f : (d >= .5f ? 0f : .5f - d); }
        static float Smooth(float t) { t = Clamp01(t); return t * t * (3 - 2 * t); }

        // osd::drawSlider: track, fill and knob over the strip of px (premultiplied ARGB). style: 0 lens, 1 pill, 2 round; t: glass light 0..1.
        static void DrawSlider(uint[] px, Layout L, Knob kn, int style, float t)
        {
            float k = L.K, cy = L.Cy, trackR = Math.Max(L.BarH, 4 * k) / 2;
            bool lens = style == 0;
            float knobHy, knobHx, popUp, drawMost;
            if (lens) { knobHy = trackR * 2.6f; knobHx = knobHy * 1.6f; popUp = .24f; drawMost = .2f; }
            else if (style == 1) { knobHy = trackR * 2.3f; knobHx = knobHy * 1.5f; popUp = .14f; drawMost = .1f; }
            else { knobHy = knobHx = trackR * 2.1f; popUp = .3f; drawMost = 0; }
            float x0 = L.BarL + trackR, x1 = L.BarR - trackR, travel0 = L.BarL + knobHx, travel1 = Math.Max(travel0, L.BarR - knobHx);
            float kx = travel0 + (travel1 - travel0) * Math.Max(-.05f, Math.Min(1.05f, kn.At));
            float lift = Clamp01(kn.Lift), clear = lift, grow = 1 + popUp * lift, drawOut = 1 + Math.Min(drawMost, Math.Abs(kn.Flow) * .3f);
            float rest = knobHx * grow, extra = rest * (drawOut - 1), hx = rest + extra, hy = knobHy * grow / (float)Math.Sqrt(drawOut), kc = kx - (kn.Flow >= 0 ? extra : -extra);
            float magnify = 1.25f + .45f * clear, shadowMost = (.24f - .06f * clear) * (1 + .55f * t), shadowDrop = (1.5f + clear) * k, shadowWide = (3 + 1.5f * clear) * k;
            float reachX = hx + 4.5f * k + 1, reachUp = hy + 3 * k + 1, reachDown = hy + 7 * k + 1, outlineA = .14f + .26f * t, rimBoost = 1 + 2 * t, shadeBelow = .09f * t;
            for (int y = L.Strip.Top; y < L.Strip.Bottom; ++y)
            {
                float py = y + .5f;
                for (int x = L.Strip.Left; x < L.Strip.Right; ++x)
                {
                    float pxf = x + .5f, a = 0, c = 0;
                    if (py > cy - trackR - 1 && py < cy + trackR + 1)
                    {
                        float track = Cover(Capsule(pxf, py, x0, x1, cy, trackR)), fill = Cover(Capsule(pxf, py, x0, Math.Min(x1, Math.Max(x0, kx)), cy, trackR));
                        if (track > 0) Lay(ref a, ref c, .3f * track, .3f * track * (1 - t));
                        if (fill > 0) Lay(ref a, ref c, fill, fill * (1 - t));
                    }
                    if (Math.Abs(pxf - kc) < reachX && py > cy - reachUp && py < cy + reachDown)
                    {
                        float knob = PillD(pxf, py, kc, cy, hx, hy), shade = shadowMost * Smooth(1 - (PillD(pxf, py - shadowDrop, kc, cy, hx, hy) + .5f) / shadowWide);
                        if (shade > 0) Lay(ref a, ref c, shade, 0);
                        float body = Cover(knob);
                        if (body > 0)
                        {
                            float bodyA = body, bodyC = body, milk = 1;
                            if (lens)
                            {
                                float lx = kc + (pxf - kc) / magnify, ly = cy + (py - cy) / magnify, lensTrack = Cover(Capsule(lx, ly, x0, x1, cy, trackR)), lensFill = Cover(Capsule(lx, ly, x0, Math.Min(x1, Math.Max(x0, kx)), cy, trackR));
                                float seen = lensFill + .3f * lensTrack * (1 - lensFill);
                                milk = .94f - .6f * clear;
                                bodyA = body * (milk + (1 - milk) * seen);
                                bodyC = body * (milk + (1 - milk) * seen * (1 - t));
                            }
                            Lay(ref a, ref c, bodyA, bodyC);
                            float lower = Math.Max(0f, (py - cy) / hy);
                            if (shadeBelow > 0) Lay(ref a, ref c, shadeBelow * lower * lower * body, 0);
                            if (lens)
                            {
                                float rim = Cover(Math.Abs(knob + .8f * k) - .6f * k) * body, upper = Math.Max(0f, (cy - py) / hy);
                                c = Math.Min(a, c + rim * .15f * (.4f + .6f * upper) * (1 - milk + .3f) * rimBoost);
                            }
                        }
                        float line = outlineA * Cover(Math.Abs(knob) - .5f * k);
                        if (line > 0) Lay(ref a, ref c, line, 0);
                    }
                    if (a < .002f) continue;
                    int at = y * L.W + x;
                    uint under = px[at];
                    int ua = (int)(under >> 24) & 255;
                    float keep = 1 - a;
                    int A = (int)(a * 255 + (float)ua * keep + .5f);
                    if (A <= 0) { px[at] = 0; continue; }
                    if (A > 255) A = 255;
                    A |= 1;   // odd: final colour, left alone by the light-glass flip
                    int v = (int)(c * 255 + .5f);
                    int R = Math.Min(A, v + (int)((float)((under >> 16) & 255) * keep)), G = Math.Min(A, v + (int)((float)((under >> 8) & 255) * keep)), B = Math.Min(A, v + (int)((float)(under & 255) * keep));
                    px[at] = ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | (uint)B;
                }
            }
        }

        static string IconFace()
        {
            foreach (FontFamily f in FontFamily.Families) if (f.Name == "Segoe Fluent Icons") return f.Name;
            return "Segoe MDL2 Assets";
        }

        static Bitmap TextSheet(int w, int h)
        {
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp)) g.Clear(Color.Black);
            return bmp;
        }

        static Graphics TextGraphics(Bitmap bmp)
        {
            Graphics g = Graphics.FromImage(bmp);
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;   // no anti-aliasing, as NONANTIALIASED_QUALITY
            return g;
        }

        static int[] ReadSheet(Bitmap bmp)
        {
            int[] r = new int[bmp.Width * bmp.Height];
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(bd.Scan0, r, 0, r.Length);
            bmp.UnlockBits(bd);
            return r;
        }

        static StringFormat Fmt(StringAlignment h, StringAlignment v, bool wrap)
        {
            StringFormat f = new StringFormat(StringFormat.GenericTypographic);
            f.Alignment = h;
            f.LineAlignment = v;
            f.Trimming = StringTrimming.EllipsisCharacter;
            if (wrap) f.FormatFlags &= ~StringFormatFlags.NoWrap; else f.FormatFlags |= StringFormatFlags.NoWrap;
            return f;
        }

        // osd::inkBase: the glyph and the number as ink (even alpha), the rest clear.
        static uint[] InkBase(Layout L, char glyph, int number)
        {
            uint[] image = new uint[L.W * L.H];
            using (Bitmap bmp = TextSheet(L.W, L.H))
            {
                using (Graphics g = TextGraphics(bmp))
                using (Font icons = new Font(IconFace(), (float)Lround(20 * L.K), FontStyle.Regular, GraphicsUnit.Pixel))
                using (Font digits = new Font("Segoe UI", (float)Lround(18 * L.K), FontStyle.Regular, GraphicsUnit.Pixel))
                using (StringFormat mid = Fmt(StringAlignment.Center, StringAlignment.Center, false))
                using (StringFormat right = Fmt(StringAlignment.Far, StringAlignment.Center, false))
                {
                    g.DrawString(glyph.ToString(), icons, Brushes.White, (RectangleF)L.GlyphBox, mid);
                    g.DrawString(number.ToString(CultureInfo.InvariantCulture), digits, Brushes.White, (RectangleF)L.NumBox, right);
                }
                int[] pix = ReadSheet(bmp);
                for (int i = 0; i < image.Length; i++) if ((pix[i] & 0xffffff) != 0) image[i] = 0xfefefefeu;
            }
            return image;
        }

        static int[] ToInt(uint[] u) { int[] r = new int[u.Length]; Buffer.BlockCopy(u, 0, r, 0, u.Length * 4); return r; }

        // main.cpp osdRender: the ink base is redrawn only when the level (number), mute (glyph), glyph or style changes; the slider every frame.
        sealed class OsdPainter
        {
            readonly Layout L;
            uint[] baseInk;
            int drawnNumber = -1, drawnStyle = -1;
            char drawnGlyph;
            public int InkDraws;
            public OsdPainter(int w, int h, float k) { L = MakeLayout(w, h, k); }

            public int[] Render(OsdState st, int style, float light)
            {
                int number = st.Percent();
                char glyph = st.Glyph();
                if (baseInk == null || number != drawnNumber || glyph != drawnGlyph || style != drawnStyle)
                {
                    baseInk = InkBase(L, glyph, number);
                    drawnNumber = number; drawnGlyph = glyph; drawnStyle = style;
                    InkDraws++;
                }
                uint[] frame = (uint[])baseInk.Clone();
                DrawSlider(frame, L, st.Knob, style, light);
                return ToInt(frame);
            }
        }

        static Rectangle[] ChipBoxes(int W, int H, float k, int[] textW)
        {
            int h = Lround(26 * k), gap = Lround(8 * k), pad = Lround(20 * k), maxW = (W - 2 * Lround(16 * k) - 2 * gap) / 3;
            int[] w = new int[3];
            int total = 2 * gap;
            for (int i = 0; i < 3; i++) { w[i] = Math.Max(h, Math.Min(textW[i] + pad, maxW)); total += w[i]; }
            Rectangle[] boxes = new Rectangle[3];
            int x = (W - total) / 2, top = H - Lround(52 * k);
            for (int i = 0; i < 3; i++) { boxes[i] = Rectangle.FromLTRB(x, top, x + w[i], top + h); x += w[i] + gap; }
            return boxes;
        }

        static WBtn ChipButton(Rectangle box, byte[] mask)
        {
            WBtn b = new WBtn();
            b.L = box.Left; b.T = box.Top; b.R = box.Right; b.Bo = box.Bottom;
            b.ML = box.Left; b.MT = box.Top; b.MR = box.Right; b.MB = box.Bottom;
            b.Hx = box.Width / 2f; b.Hy = box.Height / 2f;
            b.Cx = box.Left + b.Hx; b.Cy = box.Top + b.Hy;
            b.Rad = Math.Min(b.Hx, b.Hy);
            b.Mask = mask;
            return b;
        }

        // osd::tightenMask: crops the mask to its ink bounds; no ink = no mask.
        static void TightenMask(WBtn b)
        {
            int w = b.MR - b.ML, h = b.MB - b.MT, x0 = w, y0 = h, x1 = -1, y1 = -1;
            if (b.Mask.Length != w * h) { b.Mask = new byte[0]; b.ML = b.MT = b.MR = b.MB = 0; return; }
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                    if (b.Mask[y * w + x] != 0) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
            if (x1 < 0) { b.Mask = new byte[0]; b.ML = b.MT = b.MR = b.MB = 0; return; }
            int nw = x1 - x0 + 1, nh = y1 - y0 + 1;
            byte[] cut = new byte[nw * nh];
            for (int y = 0; y < nh; y++) Buffer.BlockCopy(b.Mask, (y + y0) * w + x0, cut, y * nw, nw);
            b.ML += x0; b.MT += y0; b.MR = b.ML + nw; b.MB = b.MT + nh;
            b.Mask = cut;
        }

        // main.cpp content(): the chips' labels and the card text as ink; the chips' label pixels are their glow masks.
        static int[] CardContent(int W, int H, float k, List<WBtn> chips)
        {
            string[] label = new string[] { "Slide + zoom", "Lean", "Lens" };   // kArrival[en][4], kSlosh[en][0], kKnob[en][2]
            int[] px;
            using (Bitmap bmp = TextSheet(W, H))
            {
                using (Graphics g = TextGraphics(bmp))
                using (Font chipFont = new Font("Segoe UI", 13f * k, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Font font = new Font("Segoe UI", 20f * k, FontStyle.Regular, GraphicsUnit.Pixel))
                using (StringFormat mid = Fmt(StringAlignment.Center, StringAlignment.Center, false))
                using (StringFormat top = Fmt(StringAlignment.Center, StringAlignment.Near, true))
                {
                    int[] width = new int[3];
                    for (int i = 0; i < 3; i++) width[i] = (int)Math.Ceiling(g.MeasureString(label[i], chipFont, 1000, StringFormat.GenericTypographic).Width);
                    Rectangle[] boxes = ChipBoxes(W, H, k, width);
                    for (int i = 0; i < 3; i++) g.DrawString(label[i], chipFont, Brushes.White, (RectangleF)boxes[i], mid);
                    int[] sheet = ReadSheet(bmp);
                    for (int i = 0; i < 3; i++)
                    {
                        Rectangle b = boxes[i];
                        byte[] mask = new byte[b.Width * b.Height];
                        for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++)
                                mask[y * b.Width + x] = (sheet[(b.Top + y) * W + b.Left + x] & 0xffffff) != 0 ? (byte)255 : (byte)0;
                        WBtn btn = ChipButton(b, mask);
                        TightenMask(btn);
                        chips.Add(btn);
                    }
                    int bottom = boxes[0].Top - 4;
                    g.DrawString("LGEngine host\nBackground-adaptive liquid glass", font, Brushes.White, new RectangleF(20, H / 3, W - 40, bottom - H / 3), top);
                }
                px = ReadSheet(bmp);
            }
            for (int i = 0; i < px.Length; i++) px[i] = (px[i] & 0xffffff) != 0 ? Ink : 0;
            return px;
        }

        static GlassButton[] ToButtons(List<WBtn> chips)
        {
            GlassButton[] r = new GlassButton[chips.Count];
            for (int i = 0; i < r.Length; i++)
            {
                WBtn c = chips[i];
                GlassButton g = new GlassButton();
                g.Hit = Rectangle.FromLTRB(c.L, c.T, c.R, c.Bo);
                g.Cx = c.Cx; g.Cy = c.Cy; g.Hx = c.Hx; g.Hy = c.Hy; g.Rad = c.Rad;
                g.Style = c.Style; g.Mark = c.Mark;
                // The engine's glow walks the whole piece box through the mask (GlassGlow.Apply), so a mask cropped to the label's ink
                // (what osd::tightenMask sends) runs out of range there and the card goes dark: the frames use the mask padded back to the chip box.
                // (the engine now widens it itself: the label-cropped mask is passed exactly as the client sends it)
                g.Mask = c.Mask.Length > 0 ? c.Mask : null;
                g.MaskBox = Rectangle.FromLTRB(c.ML, c.MT, c.MR, c.MB);
                r[i] = g;
            }
            return r;
        }

        // ---------------------------------------------------------------- part 1: the wire to a real host

        static Decoded HostChain(string suffix)
        {
            Decoded d = new Decoded();
            d.Chips = new List<WBtn>();
            d.CardPx = CardContent(420, 200, 1f, d.Chips);
            OsdState st0 = new OsdState();
            st0.OnVolume(.3f, false);
            d.LensPx = new OsdPainter(300, 64, 1f).Render(st0, 0, 0.9f);

            CSpec card = new CSpec();
            card.Label = "LG proof card"; card.X = -30000; card.Y = 100;
            CSpec lens = new CSpec();
            lens.Label = "LG proof OSD"; lens.Kind = 1; lens.W = 300; lens.H = 64; lens.X = -29940; lens.Y = 308; lens.Hover = false;
            d.CardSpec = card; d.LensSpec = lens;

            GlassHostServer host = new GlassHostServer();
            host.Start(suffix);
            Sess ss = null;
            EventWaitHandle wake = null;
            HostSurf hc = null, hl = null;
            try
            {
                string full = GlassNames.PipeFull(suffix);
                Check(full == host.PipeName, "the pipe name from GlassNames.PipeFull is the host's");
                ss = new Sess(full.Substring(@"\\.\pipe\".Length));
                try { wake = EventWaitHandle.OpenExisting(host.WakeName); } catch (Exception) { wake = null; }

                ss.Send(Wire.Hello, Wire.HelloMsg(1));
                int n = ss.ReadType(Wire.HelloAck, 3000);
                int minor = -1;
                if (n > 0)
                {
                    int len = BitConverter.ToUInt16(ss.Buf, 18);
                    if (n >= 22 + len) minor = BitConverter.ToUInt16(ss.Buf, 20 + len);
                }
                Check(minor == 1, "HelloAck carries host minor 1 (got " + minor + ")");

                hc = MakeSurf(ss, card, 1, 0);
                hl = MakeSurf(ss, lens, 2, 0);
                if (hc == null || hl == null) { Check(false, "both surfaces created"); return d; }
                Publish(hc, true, true, d.CardPx, wake);
                Publish(hl, true, false, d.LensPx, wake);
                Check(Until(3000, delegate { return Q(Parse(host.TestProps(card.Label)), "want") == "1" && Q(Parse(host.TestProps(lens.Label)), "want") == "1"; }), "the host took both shown states (want=1)");

                // the 1.1 messages, as main.cpp apply() / osdRender() / osdShowChange() send them
                ss.Send(Wire.SetMotionEx, Wire.SetMotionExMsg(hc.Id, 10, 60, 45, -1, -1));
                ss.Send(Wire.SetSlosh, Wire.SetSloshMsg(hc.Id, 0, 60));
                ss.Send(Wire.Kick, Wire.KickMsg(hc.Id, Wire.KickPush(0, 60, 1)));
                ss.Send(Wire.SetButtons, Wire.SetButtonsMsg(hc.Id, d.Chips, false));
                ss.Send(Wire.SetPointerArea, Wire.SetPointerAreaMsg(hc.Id, 0, 0, 420, 200));
                ss.Send(Wire.SetMotionEx, Wire.SetMotionExMsg(hl.Id, 10, 60, 45, -1, 8));
                ss.Send(Wire.SetSlosh, Wire.SetSloshMsg(hl.Id, 0, 60));
                ss.Send(Wire.Kick, Wire.KickMsg(hl.Id, Wire.KickPush(0, 60, 1)));
                ss.Send(Wire.SetBusy, Wire.SetBusyMsg(hl.Id, true));
                Until(3000, delegate
                {
                    Dictionary<string, string> c = Parse(host.TestProps(card.Label)), l = Parse(host.TestProps(lens.Label));
                    return QI(c, "kv") >= 1 && QI(c, "bv") >= 1 && Q(c, "hp") == "1" && QI(l, "kv") >= 1 && Q(l, "busy") == "1" && Q(l, "os") == "8";
                });
                d.Card = Parse(host.TestProps(card.Label));
                d.Lens = Parse(host.TestProps(lens.Label));
                Dictionary<string, string> cd = d.Card, ld = d.Lens;

                Eq(cd, "card", "mz", "10"); Eq(cd, "card", "ml", "60"); Eq(cd, "card", "mb", "45"); Eq(cd, "card", "mt", "-1"); Eq(cd, "card", "os", "-1");
                Eq(cd, "card", "ss", "0"); Eq(cd, "card", "sa", "60");
                Check(QI(cd, "kv") >= 1, "card: Kick reached the host (kv=" + Q(cd, "kv") + ")");
                WBtn c0 = d.Chips[0];
                Eq(cd, "card", "b", "3/" + c0.Style + "," + c0.L + "," + c0.Rad.ToString("0.#", CultureInfo.InvariantCulture) + "," + c0.Mask.Length);
                Eq(cd, "card", "bc", "0"); Eq(cd, "card", "hp", "1"); Eq(cd, "card", "pa", "0,0,420,200"); Eq(cd, "card", "w", "420");
                Check(QI(cd, "bv") >= 1, "card: the buttons were taken (bv=" + Q(cd, "bv") + ")");
                Eq(ld, "lens", "mz", "10"); Eq(ld, "lens", "ml", "60"); Eq(ld, "lens", "mb", "45"); Eq(ld, "lens", "mt", "-1"); Eq(ld, "lens", "os", "8");
                Eq(ld, "lens", "ss", "0"); Eq(ld, "lens", "sa", "60"); Eq(ld, "lens", "busy", "1"); Eq(ld, "lens", "w", "300");
                Check(QI(ld, "kv") >= 1, "lens: Kick reached the host (kv=" + Q(ld, "kv") + ")");

                GlassLook want = new GlassLook(card.Look[0], card.Look[1], card.Look[2], card.Look[3], card.Look[4], card.Look[5], card.Look[6], card.Look[7]);
                bool a = host.TryGetLook(card.Label, out d.CardLook), b = host.TryGetLook(lens.Label, out d.LensLook);
                Check(a && b && d.CardLook.Dark == want.Dark && d.CardLook.Blur == want.Blur && d.CardLook.Refraction == want.Refraction && d.CardLook.Rim == want.Rim
                    && d.CardLook.Shadow == want.Shadow && d.CardLook.Round == want.Round && d.CardLook.Intensity == want.Intensity && d.CardLook.Elevation == want.Elevation, "the look the host decoded is the one sent (35,30,80,60,28,12,100,50)");

                // Cut, then the hidden state (the order main.cpp / the client uses: Cut before the visible=0 publish)
                ss.Send(Wire.Cut, Wire.CutMsg(hc.Id));
                ss.Send(Wire.Cut, Wire.CutMsg(hl.Id));
                Check(Until(3000, delegate { return QI(Parse(host.TestProps(card.Label)), "cv") >= 1 && QI(Parse(host.TestProps(lens.Label)), "cv") >= 1; }), "Cut reached the host on both surfaces (cv>=1)");
                Publish(hc, false, true, null, wake);
                Publish(hl, false, false, null, wake);
                Check(Until(3000, delegate { return Q(Parse(host.TestProps(card.Label)), "want") == "0" && Q(Parse(host.TestProps(lens.Label)), "want") == "0"; }), "the hidden states arrived after the Cut (want=0)");
            }
            finally
            {
                if (hc != null && hc.Sec != null) hc.Sec.Dispose();
                if (hl != null && hl.Sec != null) hl.Sec.Dispose();
                if (ss != null) ss.Dispose();
                if (wake != null) wake.Close();
                host.Stop();
            }
            return d;
        }

        // ---------------------------------------------------------------- part 2: frames from SurfaceRuntime, off screen

        sealed class ManualTransport : IGlassTransport
        {
            public SurfaceRuntime Run;
            public string Name { get { return "manual"; } }
            public IGlassChannel Open(GlassSpec spec, IGlassEvents events, SurfaceState state)
            {
                Run = new SurfaceRuntime(spec, events, state, delegate { });
                return Run;
            }
            public void Wake(IGlassChannel channel) { }
            public void Close(IGlassChannel channel)
            {
                SurfaceRuntime r = channel as SurfaceRuntime;
                if (r != null) r.Dispose();
            }
            public bool Post(Action work) { return false; }
            public void Dispose() { }
        }

        sealed class Rig : IDisposable
        {
            readonly GlassService svc;
            readonly ManualTransport mt = new ManualTransport();
            public readonly GlassSurface S;
            public readonly Point Home;     // the surface's place (screen coordinates, shifted off screen by 10000)
            public readonly Size Size;
            readonly Scene scene;

            public Rig(GlassSpec spec, Scene scene)
            {
                this.scene = scene;
                Home = spec.Place;
                Size = spec.Size;
                svc = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.UiThread });
                S = new GlassSurface(svc, mt, spec, null);
            }

            public void Step(int ms)
            {
                ScreenFeed.TestFine += ms;
                GlassPresenter.TestTick = (int)ScreenFeed.TestFine;
                mt.Run.RunFrame();
            }

            public GlassPresenter Pr { get { return mt.Run.Presenter; } }
            public void Steps(int n) { for (int i = 0; i < n; i++) Step(16); }

            // The scene under crop (scene coordinates) with the surface composited over it as the screen would show it.
            public int[] Grab(Rectangle crop)
            {
                int cw = crop.Width, ch = crop.Height;
                float[] acc = new float[cw * ch * 3];
                for (int y = 0; y < ch; y++)
                    for (int x = 0; x < cw; x++)
                    {
                        int sx = crop.X + x, sy = crop.Y + y;
                        int c = (sx >= 0 && sy >= 0 && sx < scene.W && sy < scene.H) ? scene.Px[sy * scene.W + sx] : 0;
                        int i = (y * cw + x) * 3;
                        acc[i] = (c >> 16) & 255; acc[i + 1] = (c >> 8) & 255; acc[i + 2] = c & 255;
                    }
                GlassPresenter p = mt.Run.Presenter;
                if (p.LayerVisible && p.ShownAlpha >= 0 && p.Out != null && p.Px != null)
                {
                    int w = p.Out.Width, h = p.Out.Height;
                    int[] src = p.Shown.IsNone ? p.Px : p.WarpOut;
                    if (src != null && src.Length >= w * h)
                    {
                        float a = p.ShownAlpha / 255f;
                        int ox = p.ShownAt.X + 10000 - crop.X, oy = p.ShownAt.Y + 10000 - crop.Y;
                        for (int y = 0; y < h; y++)
                        {
                            int dy = oy + y;
                            if (dy < 0 || dy >= ch) continue;
                            for (int x = 0; x < w; x++)
                            {
                                int dx = ox + x;
                                if (dx < 0 || dx >= cw) continue;
                                int s = src[y * w + x];
                                int sa = (s >> 24) & 255;
                                if (sa == 0 && (s & 0xffffff) == 0) continue;
                                float keep = 1f - sa / 255f * a;
                                int i = (dy * cw + dx) * 3;
                                acc[i] = ((s >> 16) & 255) * a + acc[i] * keep;
                                acc[i + 1] = ((s >> 8) & 255) * a + acc[i + 1] * keep;
                                acc[i + 2] = (s & 255) * a + acc[i + 2] * keep;
                            }
                        }
                    }
                }
                int[] r = new int[cw * ch];
                for (int i = 0; i < r.Length; i++)
                {
                    int R = Math.Min(255, (int)(acc[i * 3] + .5f)), G = Math.Min(255, (int)(acc[i * 3 + 1] + .5f)), B = Math.Min(255, (int)(acc[i * 3 + 2] + .5f));
                    r[i] = unchecked((int)0xFF000000) | (R << 16) | (G << 8) | B;
                }
                return r;
            }

            public void Dispose()
            {
                S.Dispose();
                svc.Dispose();
            }
        }

        static int testVersion = 7000;

        static void Backdrop(Scene scene)
        {
            GlassPeek.TestScreen = scene.Px;
            GlassPeek.TestW = scene.W;
            GlassPeek.TestH = scene.H;
            GlassPeek.TestShift = new Point(10000, 10000);
            GlassPeek.TestVersion = ++testVersion;
        }

        static Rig NewRig(bool lens, Decoded d, Point sceneAt, Scene scene, string tag)
        {
            CSpec cs = lens ? d.LensSpec : d.CardSpec;
            Dictionary<string, string> pr = lens ? d.Lens : d.Card;
            GlassSpec s = new GlassSpec();
            s.Label = "LG proof frames " + tag;
            s.Kind = lens ? GlassKind.Lens : GlassKind.Card;
            s.Look = lens ? d.LensLook : d.CardLook;
            s.Scale = 1f;
            s.Size = new Size(cs.W, cs.H);
            s.Place = new Point(sceneAt.X - 10000, sceneAt.Y - 10000);
            s.KeepSpare = lens;
            s.Fps = 15;
            s.Hover = false;   // (the hover fade would hide the card under the test pointer; the wire's hover flag is checked on the host side)
            s.AnimStyle = cs.Motion; s.AnimIn = cs.InMs; s.AnimOut = cs.OutMs;
            s.AnimZoom = QI(pr, "mz"); s.AnimLiquid = QI(pr, "ml"); s.AnimBounce = QI(pr, "mb");   // (travel -1 = the spec's own, as the host does)
            return new Rig(s, scene);
        }

        // ---------------------------------------------------------------- the strips

        sealed class Strip
        {
            readonly int cols, rows, cellW, cellH, cap = 15, gap = 4;
            readonly Bitmap bmp;
            readonly Graphics g;
            readonly Font font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Pixel);

            public Strip(int cols, int rows, int cellW, int cropW, int cropH)
            {
                this.cols = cols; this.rows = rows; this.cellW = cellW;
                cellH = Math.Max(1, Lround(cropH * (double)cellW / cropW));
                bmp = new Bitmap(cols * (cellW + gap) + gap, rows * (cellH + cap + gap) + gap, PixelFormat.Format24bppRgb);
                g = Graphics.FromImage(bmp);
                g.Clear(Color.FromArgb(22, 24, 28));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            }

            public void Put(int col, int row, int[] crop, int cw, int ch, string caption)
            {
                GCHandle h = GCHandle.Alloc(crop, GCHandleType.Pinned);
                try
                {
                    using (Bitmap src = new Bitmap(cw, ch, cw * 4, PixelFormat.Format32bppRgb, h.AddrOfPinnedObject()))
                    {
                        int x = gap + col * (cellW + gap), y = gap + row * (cellH + cap + gap);
                        g.DrawImage(src, new Rectangle(x, y, cellW, cellH), 0, 0, cw, ch, GraphicsUnit.Pixel);
                        g.DrawString(caption, font, Brushes.Gainsboro, x, y + cellH + 1);
                    }
                }
                finally { h.Free(); }
            }

            public string Save(string path)
            {
                g.Dispose();
                bmp.Save(path, ImageFormat.Png);
                string info = Path.GetFileName(path) + " " + bmp.Width + "x" + bmp.Height + " " + (new FileInfo(path).Length + 512) / 1024 + " KB";
                bmp.Dispose();
                return info;
            }
        }

        static float LightOf(Scene scene, Rectangle r)
        {
            double sum = 0;
            int n = 0;
            for (int y = r.Top; y < r.Bottom; y += 2)
                for (int x = r.Left; x < r.Right; x += 2)
                {
                    int c = scene.Px[y * scene.W + x];
                    sum += (0.2126 * ((c >> 16) & 255) + 0.7152 * ((c >> 8) & 255) + 0.0722 * (c & 255)) / 255.0;
                    n++;
                }
            return (float)(sum / Math.Max(1, n));
        }

        static string[] ArrivalNames = { "none", "fade", "slide", "zoom", "slide + zoom", "drop", "pop", "melt", "ripple" };

        static void Frames(string dir, Decoded d, List<string> made)
        {
            Scene scene = Scene.Make("busy", null);
            Backdrop(scene);
            Point cardAt = new Point(940, 150), lensAt = new Point(cardAt.X + 60, cardAt.Y + 208);
            Rectangle lensRect = new Rectangle(lensAt.X, lensAt.Y, 300, 64);
            float light = LightOf(scene, lensRect);
            Program.Say("proof: backdrop = busy scene, glass light at the indicator = {0:0.00}", light);
            int pad = 28;
            Rectangle lensCrop = Rectangle.Inflate(lensRect, pad, pad);
            int travel = QI(d.Card, "mt");
            if (travel < 0) travel = 34;
            int zoom = QI(d.Card, "mz"), liquid = QI(d.Card, "ml"), bounce = QI(d.Card, "mb");
            int sloshStyle = QI(d.Card, "ss"), sloshAmount = QI(d.Card, "sa");
            int os = QI(d.Lens, "os");
            Program.Say("proof: frames use the host-decoded values: zoom {0} liquid {1} bounce {2} travel {3} (host: -1 = spec) indicator {4} slosh {5}/{6}", zoom, liquid, bounce, travel, os, sloshStyle, sloshAmount);

            // ---- osd-slosh: lean, bounce, pulse after one kick
            {
                string[] names = { "lean", "bounce", "pulse" };
                int[] at = { 16, 64, 128, 224, 384, 672 };
                Strip strip = new Strip(6, 3, 224, lensCrop.Width, lensCrop.Height);
                for (int row = 0; row < 3; row++)
                    using (Rig rig = NewRig(true, d, lensAt, scene, "slosh " + names[row]))
                    {
                        OsdState st = new OsdState();
                        st.OnVolume(.5f, false);
                        OsdPainter pa = new OsdPainter(300, 64, 1f);
                        rig.S.SetMotion(4, 380, 230, zoom, liquid, bounce, travel, os);
                        rig.S.SetContents(pa.Render(st, 0, light), 300, 64, Rectangle.Empty);
                        rig.S.Show();
                        rig.Steps(45);
                        rig.S.SetSlosh(row, sloshAmount);
                        rig.S.Kick(1);
                        int t = 0;
                        for (int col = 0; col < 6; col++)
                        {
                            while (t < at[col]) { rig.Step(16); t += 16; }
                            strip.Put(col, row, rig.Grab(lensCrop), lensCrop.Width, lensCrop.Height, names[row] + "  t=" + t + " ms");
                        }
                    }
                made.Add(strip.Save(Path.Combine(dir, "osd-slosh.png")));
            }

            // ---- osd-knobs: round, pill, lens, level 30 -> 70
            {
                string[] names = { "round", "pill", "lens" };
                int[] styles = { 2, 1, 0 };
                int[] at = { 32, 96, 192, 352, 560, 720 };
                Strip strip = new Strip(6, 3, 224, lensCrop.Width, lensCrop.Height);
                for (int row = 0; row < 3; row++)
                    using (Rig rig = NewRig(true, d, lensAt, scene, "knob " + names[row]))
                    {
                        OsdState st = new OsdState();
                        st.OnVolume(.3f, false);
                        OsdPainter pa = new OsdPainter(300, 64, 1f);
                        rig.S.SetMotion(4, 380, 230, zoom, liquid, bounce, travel, os);
                        rig.S.SetSlosh(sloshStyle, sloshAmount);
                        rig.S.SetContents(pa.Render(st, styles[row], light), 300, 64, Rectangle.Empty);
                        rig.S.Show();
                        rig.Steps(45);
                        // the volume changes: set the knob, render, kick, busy while it moves (osdShowChange + osdTick)
                        int way = st.OnVolume(.7f, false);
                        rig.S.SetContents(pa.Render(st, styles[row], light), 300, 64, Rectangle.Empty);
                        rig.S.Kick(way);
                        rig.S.SetBusy(true);
                        int t = 0;
                        for (int col = 0; col < 6; col++)
                        {
                            while (t < at[col])
                            {
                                bool changed = st.Tick(0.016f);
                                if (changed) rig.S.SetContents(pa.Render(st, styles[row], light), 300, 64, Rectangle.Empty);
                                rig.Step(16);
                                t += 16;
                            }
                            strip.Put(col, row, rig.Grab(lensCrop), lensCrop.Width, lensCrop.Height,
                                names[row] + " t=" + t + " lvl " + st.Percent() + " knob " + st.Knob.At.ToString("0.00", CultureInfo.InvariantCulture));
                        }
                        Program.Say("proof: knob {0}: ink base drawn {1} time(s) over the whole change", names[row], pa.InkDraws);
                    }
                made.Add(strip.Save(Path.Combine(dir, "osd-knobs.png")));
            }

            // ---- osd-arrival: the lens arriving, indicator styles (8 = ripple)
            {
                int[] styles = { 1, 3, 4, 6, 8 };
                int[] at = { 32, 80, 144, 224, 352 };
                Strip strip = new Strip(5, styles.Length, 260, lensCrop.Width, lensCrop.Height);
                for (int row = 0; row < styles.Length; row++)
                    using (Rig rig = NewRig(true, d, lensAt, scene, "arrival " + styles[row]))
                    {
                        OsdState st = new OsdState();
                        st.OnVolume(.5f, false);
                        OsdPainter pa = new OsdPainter(300, 64, 1f);
                        rig.S.SetMotion(4, 380, 230, zoom, liquid, bounce, travel, styles[row]);
                        rig.S.SetContents(pa.Render(st, 0, light), 300, 64, Rectangle.Empty);
                        rig.S.Show();
                        int t = 0;
                        for (int col = 0; col < 5; col++)
                        {
                            while (t < at[col]) { rig.Step(16); t += 16; }
                            strip.Put(col, row, rig.Grab(lensCrop), lensCrop.Width, lensCrop.Height, "indicator " + styles[row] + " " + ArrivalNames[styles[row]] + "  t=" + t);
                        }
                    }
                made.Add(strip.Save(Path.Combine(dir, "osd-arrival.png")));
            }

            // ---- card-glow: the card with the 3 chips, the (test) pointer at five places
            {
                Rectangle cardRect = new Rectangle(cardAt.X, cardAt.Y, 420, 200);
                Rectangle cardCrop = Rectangle.Inflate(cardRect, 18, 18);
                Strip strip = new Strip(5, 1, 270, cardCrop.Width, cardCrop.Height);
                Scene wall = Scene.Make("wall", null);   // colourful mid-tone gradient: the glow reads on it
                Backdrop(wall);
                using (Rig rig = NewRig(false, d, cardAt, wall, "glow"))
                {
                    GlassButton[] chips = ToButtons(d.Chips);
                    Point home = rig.Home, away = new Point(-30000, -30000);
                    rig.S.SetMotion(4, 380, 230, zoom, liquid, bounce, travel);
                    rig.S.SetSlosh(sloshStyle, sloshAmount);
                    rig.S.SetContents(d.CardPx, 420, 200, Rectangle.Empty);
                    rig.S.SetInteraction(false);   // (hover fades a card out under the pointer: the glow needs it visible)
                    rig.S.SetButtons(chips, false);   // (no pointer area here: it would fade the card out under the test pointer; its wire value is checked on the host)
                    GlassGlow.TestCursor = away;
                    rig.S.Show();
                    rig.Steps(45);
                    rig.S.SetInteraction(false);
                    rig.S.SetButtons(chips, false);
                    rig.Steps(3);
                    strip.Put(0, 0, rig.Grab(cardCrop), cardCrop.Width, cardCrop.Height, "pointer away");
                    GlassGlow.TestCursor = new Point(home.X + 110, home.Y + 70);
                    rig.Steps(16);
                    strip.Put(1, 0, rig.Grab(cardCrop), cardCrop.Width, cardCrop.Height, "on the card text");
                    for (int i = 0; i < 3; i++)
                    {
                        Rectangle h = chips[i].Hit;
                        GlassGlow.TestCursor = new Point(home.X + (h.Left + h.Right) / 2, home.Y + (h.Top + h.Bottom) / 2);
                        rig.Steps(16);
                        if (i == 2) { GlassGlow.TestDown = true; rig.Steps(8); }
                        int[] shot = rig.Grab(cardCrop);
                        int ink = 0;
                        for (int q = 0; q < shot.Length; q += 7) if (shot[q] != scene.Px[0]) ink++;
                        Check(rig.Pr.LayerVisible && ink > 100 && chips[i].Mask != null && chips[i].MaskBox.Width < chips[i].Hit.Width, "frames: the card renders with the label-cropped mask on chip " + (i + 1) + " (visible, not empty)");
                        strip.Put(2 + i, 0, shot, cardCrop.Width, cardCrop.Height, i == 2 ? "chip 3, pressed" : "on chip " + (i + 1));
                    }
                    GlassGlow.TestDown = false;
                    GlassGlow.TestCursor = away;
                }
                made.Add(strip.Save(Path.Combine(dir, "card-glow.png")));
                Backdrop(scene);
                Program.Say("proof frames: {0}/{1} checks OK", okChecks, checks);
            }

            // ---- card-arrival: card arrival styles 0..8
            {
                Rectangle cardRect = new Rectangle(cardAt.X, cardAt.Y, 420, 200);
                Rectangle cardCrop = Rectangle.Inflate(cardRect, 44, 44);
                int[] at = { 32, 80, 144, 224, 352 };
                Strip strip = new Strip(5, 9, 128, cardCrop.Width, cardCrop.Height);
                for (int row = 0; row < 9; row++)
                    using (Rig rig = NewRig(false, d, cardAt, scene, "card arrival " + row))
                    {
                        rig.S.SetMotion(row, 380, 230, zoom, liquid, bounce, travel);
                        rig.S.SetSlosh(sloshStyle, sloshAmount);
                        rig.S.SetContents(d.CardPx, 420, 200, Rectangle.Empty);
                        rig.S.SetButtons(ToButtons(d.Chips), false);
                        GlassGlow.TestCursor = new Point(-30000, -30000);
                        rig.S.Show();
                        int t = 0;
                        for (int col = 0; col < 5; col++)
                        {
                            while (t < at[col]) { rig.Step(16); t += 16; }
                            strip.Put(col, row, rig.Grab(cardCrop), cardCrop.Width, cardCrop.Height, row + " " + ArrivalNames[row] + "  t=" + t);
                        }
                    }
                made.Add(strip.Save(Path.Combine(dir, "card-arrival.png")));
            }
        }

        // ---------------------------------------------------------------- the run

        public static void Run(string dir)
        {
            Program.Say("");
            Program.Say("proof: the C++ app's glass animations as pictures -> {0}", dir);
            Directory.CreateDirectory(dir);
            checks = okChecks = 0;
            ScreenFeed.TestFine = 5000000;
            GlassGlow.TestOn = true;
            GlassGlow.TestDown = false;
            GlassGlow.TestCursor = new Point(-30000, -30000);
            List<string> made = new List<string>();
            try
            {
                Golden();
                Decoded d = HostChain("proof" + Process.GetCurrentProcess().Id);
                Program.Say("proof wire: {0}/{1} checks OK", okChecks, checks);
                if (d.Card != null && d.Lens != null && d.CardLook.Dark > 0)
                {
                    Frames(dir, d, made);
                    WriteReadme(dir);
                }
                else Program.Say("proof: no host values, so no frames");
            }
            catch (Exception ex)
            {
                Program.Say("proof FAILED: {0} {1}", ex.GetType().Name, ex.Message);
                Program.Say("{0}", ex.StackTrace);
            }
            finally
            {
                GlassPeek.TestScreen = null;
                GlassPeek.TestShift = Point.Empty;
                ScreenFeed.TestFine = 0;
                GlassPresenter.TestTick = 0;
                GlassGlow.TestOn = false;
                GlassGlow.TestDown = false;
                GlassGlow.TestCursor = Point.Empty;
            }
            foreach (string m in made) Program.Say("proof png: {0}", m);
        }

        static void WriteReadme(string dir)
        {
            string[] lines = new string[] {
                "Proof pictures of the new glass animations of the C++ app (LiquidGlassControl, integrations\\LGEngine\\native).",
                "Made by LGBench -proof: the chain is exactly this.",
                "1. The C++ client's wire messages (Hello minor 1, Create, SetMotionEx, SetSlosh, Kick, SetButtons, SetPointerArea, SetBusy, Cut)",
                "   were ported from lg_client.h to C#; the port is asserted byte for byte against the golden bytes of tests\\lgengine_wire_test.cpp.",
                "2. Those bytes go over a real named pipe to a real GlassHostServer (in this process, windows off screen); what the host decoded",
                "   is read back (TestProps, TryGetLook) and checked value by value (the line 'proof wire: N/N checks OK').",
                "3. The frames come from the engine's own SurfaceRuntime, run off screen by hand with a fixed clock (16 ms per frame), fed with the",
                "   values the host decoded, over a test backdrop. The test pointer is GlassGlow.TestCursor; the real cursor is never moved.",
                "4. The card and indicator pixels (glyph, track, fill, knob round/pill/lens with their springs, number, the three chips) are a C# PORT",
                "   of native\\osd.h and the chips of main.cpp: the same pixels go into the host's section and into the frames. The number is the real level.",
                "The C++ exe itself was NOT run (Smart App Control rule), so text is GDI+ (not GDI) and the indicator light is estimated from the backdrop.",
                "Files: osd-slosh.png (lean, bounce, pulse after a kick), osd-knobs.png (round, pill, lens, level 30 to 70), osd-arrival.png (indicator",
                "styles 1, 3, 4, 6, 8 = ripple), card-glow.png (pointer away, on the card, on each chip, pressed), card-arrival.png (card styles 0..8)."
            };
            File.WriteAllText(Path.Combine(dir, "README.txt"), string.Join("\r\n", lines) + "\r\n");
        }
    }
}
