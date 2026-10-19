using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LGEngine
{
    // The glow under the pointer (Apple's glass lights up where it is touched), moved out of the app's GlassOverlay (GlassButtons.cs)
    // as it was. Used by SurfaceRuntime for the pieces an owner gives with GlassSurface.SetButtons, and by the bench.
    //
    // While the pointer is over one of the glass pieces the owner drew, the glass there is lit by a soft light round the pointer,
    // clipped to the piece's shape, with its rim brightened a little; it eases in and out over GlowInS, and while the left button is
    // held the piece is pressed (a little darker, the light drawn in). None of it goes through the contents: after the glass is
    // drawn (Shade) the light is laid on the picture over each lit piece's own small box only, with what was under it kept (Snap)
    // so that it can be put back as the light moves or goes, and just those rows go to the bitmap. The contents are never redrawn
    // for it. Geometry is in card pixels (the card's corner is the picture's (Margin, Margin)).
    internal sealed class GlassGlow
    {
        // One piece and its state.
        sealed class Part
        {
            public float Cx, Cy, Hx, Hy, Rad;
            public int Style, Mark;
            public Rectangle Hit;
            public byte[] Mask;
            public Rectangle MaskBox;

            public float Glow, Press;           // how lit it is and how pressed (0-1), each easing towards what the pointer asks for
            public float LightX, LightY;        // where the light is
            public bool Dirty;                  // its glow changed since it was last put on the picture
            public bool Lit;                    // the picture has this part's glow on it now
            public bool SnapOk;                 // Snap holds what the picture has under the part now, without any glow
            public int[] Snap;                  // (box-sized, SnapBox in the picture's coordinates)
            public Rectangle SnapBox;
        }

        // Test seams (the bench, nothing else): a pointer that is where TestCursor says, a button that is held.
        internal static bool TestOn, TestDown;
        internal static Point TestCursor;

        internal static bool Cursor(out int x, out int y)
        {
            if (TestOn)
            {
                x = TestCursor.X;
                y = TestCursor.Y;
                return true;
            }
            PT p;
            bool ok = GetCursorPos(out p);
            x = p.X;
            y = p.Y;
            return ok;
        }

        internal static bool LeftDown()
        {
            if (TestOn) return TestDown;
            return (GetAsyncKeyState(0x01) & 0x8000) != 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PT { public int X, Y; }

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out PT p);

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int key);

        readonly GlassPresenter presenter;
        readonly GlassAnimator anim;
        readonly float scale;
        readonly bool lens;

        List<Part> parts;              // what the owner drew in the contents now on the glass
        long glowFine;                 // (ScreenFeed.Fine) when the glow was last moved on
        int glowWant;                  // how soon the glass wants its next frame for the glow (0: no wish of its own)
        const float GlowInS = 0.12f, GlowPressS = 0.07f, GlowFollowS = 0.045f;   // (seconds: to light up, to press, and for the light to follow the pointer)
        const float GlowPeak = 0.32f, GlowReachH = 0.8f, GlowRim = 0.15f, PressDark = 0.10f;   // (the light's strength at the pointer, how far it reaches in the piece's heights, the rim's extra, and how much a press darkens)

        public GlassGlow(GlassPresenter presenter, GlassAnimator anim, float scale, bool lens)
        {
            this.presenter = presenter;
            this.anim = anim;
            this.scale = scale;
            this.lens = lens;
            presenter.GlowShowing = Showing;
            presenter.GlowAfterShade = AfterShade;
            presenter.WarpGlowPart = WarpGlowPart;
            presenter.WarpDone = WarpDone;
            presenter.PresentGlow = PresentGlow;
        }

        // How soon the glass wants its next frame for the glow (0: no wish of its own).
        public int Want { get { return glowWant; } }

        public bool Any { get { return parts != null && parts.Count > 0; } }

        // The owner's pieces (a copy is made); carry: the same notification as before with something changed on it, so that what was lit stays lit.
        public void Set(GlassButton[] buttons, bool carry)
        {
            if (buttons == null || buttons.Length == 0)
            {
                parts = null;
                glowWant = 0;
                return;
            }
            List<Part> fresh = new List<Part>();
            for (int i = 0; i < buttons.Length; i++)
            {
                GlassButton b = buttons[i];
                Part p = new Part();
                p.Cx = b.Cx; p.Cy = b.Cy; p.Hx = b.Hx; p.Hy = b.Hy; p.Rad = b.Rad;
                p.Style = b.Style; p.Mark = b.Mark;
                p.Hit = b.Hit;
                p.Mask = b.Mask;
                p.MaskBox = b.MaskBox;
                Rectangle need = Rectangle.FromLTRB((int)Math.Floor(p.Cx - p.Hx) - 1, (int)Math.Floor(p.Cy - p.Hy) - 1, (int)Math.Ceiling(p.Cx + p.Hx) + 1, (int)Math.Ceiling(p.Cy + p.Hy) + 1);
                if (p.Mask == null || p.Mask.Length < p.MaskBox.Width * p.MaskBox.Height)
                {
                    // (nothing of it is label)
                    p.MaskBox = need;
                    p.Mask = new byte[p.MaskBox.Width * p.MaskBox.Height];
                }
                else if (!p.MaskBox.Contains(need))
                {
                    // (a mask cropped to the label, as a pipe client may send: Apply reads it over the whole piece, so it is widened, the rest not label)
                    Rectangle mb = p.MaskBox, wide = Rectangle.Union(mb, need);
                    byte[] m = new byte[wide.Width * wide.Height];
                    for (int y = 0; y < mb.Height; y++) Array.Copy(p.Mask, y * mb.Width, m, (mb.Top - wide.Top + y) * wide.Width + (mb.Left - wide.Left), mb.Width);
                    p.Mask = m;
                    p.MaskBox = wide;
                }
                fresh.Add(p);
            }
            if (carry && parts != null) CarryGlow(parts, fresh);
            parts = fresh;
        }

        // The pieces just drawn start as lit (and pressed, and lit from where) as the same pieces were.
        static void CarryGlow(List<Part> was, List<Part> now)
        {
            for (int i = 0; i < now.Count; i++)
            {
                Part n = now[i];
                for (int j = 0; j < was.Count; j++)
                {
                    Part o = was[j];
                    if (o.Style != n.Style || o.Mark != n.Mark || Math.Abs(o.Cx - n.Cx) > 4 || Math.Abs(o.Cy - n.Cy) > 4 || Math.Abs(o.Hx - n.Hx) > 4 || Math.Abs(o.Hy - n.Hy) > 4) continue;
                    n.Glow = o.Glow;
                    n.Press = o.Press;
                    n.LightX = o.LightX;
                    n.LightY = o.LightY;
                    break;
                }
            }
        }

        static float Approach(float value, float goal, float step)
        {
            return value < goal ? Math.Min(goal, value + step) : Math.Max(goal, value - step);
        }

        // Moves the glow on to now: which piece the pointer is on, how lit and pressed each is, where its
        // light is. Marks the pieces that changed (see Paint) and says how soon the next frame is
        // wanted (Want). Cheap when the pointer is not over a piece and nothing is fading.
        // home: the card's top left on the screen; area: where the pointer counts as on the card (card pixels, may reach past the card).
        public bool Step(Point home, Rectangle area, int animTickMs)
        {
            List<Part> list = parts;
            glowWant = 0;
            if (list == null || list.Count == 0) return false;

            long now = ScreenFeed.Fine();
            float dt = glowFine == 0 ? 0.016f : Math.Max(0.001f, Math.Min(0.04f, (now - glowFine) / 1000f));   // (a frame that comes late does not make the glow jump)
            glowFine = now;

            // the pointer: over the card, and over which piece (only once the glass is fully in)
            int under = -1;
            bool over = false, down = false;
            float curX = 0, curY = 0;
            int cx, cy;
            if (anim.WantVisible && anim.Vis >= 0.95f && Cursor(out cx, out cy)
                && cx - home.X >= area.Left && cx - home.X < area.Right && cy - home.Y >= area.Top && cy - home.Y < area.Bottom)
            {
                over = true;
                curX = cx - home.X + 0.5f;
                curY = cy - home.Y + 0.5f;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Hit.Contains((int)curX, (int)curY)) { under = i; break; }
                }
                if (under >= 0) down = LeftDown();
            }

            bool changed = false, busy = false;
            float lightStep = dt / GlowInS, pressStep = dt / GlowPressS, follow = 1f - (float)Math.Exp(-dt / GlowFollowS);
            for (int i = 0; i < list.Count; i++)
            {
                Part p = list[i];
                float glow0 = p.Glow, press0 = p.Press, lx0 = p.LightX, ly0 = p.LightY;
                bool here = i == under;
                if (here)
                {
                    if (p.Glow <= 0f && p.Press <= 0f) { p.LightX = curX; p.LightY = curY; }   // (lit from where the pointer came on)
                    else
                    {
                        p.LightX += (curX - p.LightX) * follow;
                        p.LightY += (curY - p.LightY) * follow;
                        if (Math.Abs(curX - p.LightX) < 0.25f && Math.Abs(curY - p.LightY) < 0.25f) { p.LightX = curX; p.LightY = curY; }
                    }
                }
                float glowGoal = here ? 1f : 0f, pressGoal = here && down ? 1f : 0f;
                p.Glow = Approach(p.Glow, glowGoal, lightStep);
                p.Press = Approach(p.Press, pressGoal, pressStep);
                bool lit = p.Glow > 0f || p.Press > 0f;
                if (p.Glow != glow0 || p.Press != press0 || (lit && (Math.Abs(p.LightX - lx0) > 0.02f || Math.Abs(p.LightY - ly0) > 0.02f)))
                {
                    p.Dirty = true;
                    changed = true;
                }
                if (p.Glow != glowGoal || p.Press != pressGoal || (here && (p.LightX != curX || p.LightY != curY))) busy = true;
            }
            // Often while it moves; while the pointer is on the card but resting, only often enough to see it move.
            if (busy) glowWant = animTickMs;
            else if (over) glowWant = 50;
            return changed;
        }

        // Puts the glow of the pieces whose glow has changed onto the picture, which is otherwise as
        // the last frame left it, and gives the bitmap just their rows. False if there was nothing to do.
        public bool Paint()
        {
            List<Part> list = parts;
            GlassMap map = presenter.Map;
            if (list == null || presenter.Out == null || presenter.Px == null || map == null || presenter.Primed != map || presenter.Out.Width != map.W || presenter.Out.Height != map.H) return false;
            int shiftX = -map.Margin, shiftY = -map.Margin;
            int top = int.MaxValue, bottom = -1;
            for (int i = 0; i < list.Count; i++)
            {
                Part p = list[i];
                if (!p.Dirty) continue;
                p.Dirty = false;
                Rectangle rows = Put(p, shiftX, shiftY);
                if (rows.IsEmpty) continue;
                if (rows.Top < top) top = rows.Top;
                if (rows.Bottom > bottom) bottom = rows.Bottom;
                glowSinceWarp = Join(glowSinceWarp, rows);
                glowSincePresent = Join(glowSincePresent, rows);
            }
            if (bottom <= top) return false;
            presenter.Out.Write(presenter.Px, top, bottom - top);
            return true;
        }

        // Two boxes as one (either may be empty).
        static Rectangle Join(Rectangle a, Rectangle b)
        {
            if (a.IsEmpty) return b;
            if (b.IsEmpty) return a;
            return Rectangle.Union(a, b);
        }

        // Whether a piece has its light on the picture now (the glass under it is then drawn whole, so that the light is laid afresh on clean glass).
        bool Showing()
        {
            if (parts == null) return false;
            for (int i = 0; i < parts.Count; i++) { Part p = parts[i]; if (p.Lit || p.Glow > 0f || p.Press > 0f) return true; }
            return false;
        }

        // The glass has just been drawn afresh (all of it, clean): the glow goes back on every piece that
        // is lit, before the picture goes to the bitmap.
        void AfterShade()
        {
            // (the whole picture is new: the bent one must be made afresh, and all of it shown)
            warpHeld = false;
            glowSinceWarp = glowSincePresent = glowShow = Rectangle.Empty;
            List<Part> list = parts;
            if (list == null || list.Count == 0) return;
            int shiftX = -presenter.Map.Margin, shiftY = -presenter.Map.Margin;
            for (int i = 0; i < list.Count; i++)
            {
                Part p = list[i];
                p.SnapOk = false;
                p.Lit = false;
                p.Dirty = false;
                if (p.Glow > 0f || p.Press > 0f) Put(p, shiftX, shiftY);
            }
        }

        // One piece's glow on the picture as it is now: what the last glow covered is put back first (from
        // Snap, or kept now if there is none yet). Returns the box that may have changed (picture coordinates).
        Rectangle Put(Part p, int shiftX, int shiftY)
        {
            int w = presenter.Out.Width, h = presenter.Out.Height;
            Rectangle box = Box(p, shiftX, shiftY, w, h);
            if (box.IsEmpty) return box;
            Rectangle changed = box;
            if (p.SnapOk)
            {
                changed = Rectangle.Union(box, p.SnapBox);
                if (p.Lit) Restore(presenter.Px, w, p);
                p.Lit = false;
            }
            if (!p.SnapOk || p.SnapBox != box) Snapshot(presenter.Px, w, p, box);
            if (p.Glow > 0f || p.Press > 0f)
            {
                Apply(presenter.Px, w, p, shiftX, shiftY, scale, Smooth(p.Glow), Smooth(p.Press));
                p.Lit = true;
            }
            return changed;
        }

        // The part's box in the picture (shiftX, shiftY: where the card's own corner is in the picture).
        static Rectangle Box(Part s, int shiftX, int shiftY, int w, int h)
        {
            return Rectangle.Intersect(Rectangle.FromLTRB((int)Math.Floor(s.Cx - s.Hx) - 1 - shiftX, (int)Math.Floor(s.Cy - s.Hy) - 1 - shiftY,
                (int)Math.Ceiling(s.Cx + s.Hx) + 1 - shiftX, (int)Math.Ceiling(s.Cy + s.Hy) + 1 - shiftY), new Rectangle(0, 0, w, h));
        }

        // Keeps what the picture has in the box (no glow on it).
        static void Snapshot(int[] px, int w, Part p, Rectangle box)
        {
            int n = box.Width * box.Height;
            if (p.Snap == null || p.Snap.Length < n) p.Snap = new int[n];
            for (int y = 0; y < box.Height; y++) Array.Copy(px, (box.Top + y) * w + box.Left, p.Snap, y * box.Width, box.Width);
            p.SnapBox = box;
            p.SnapOk = true;
        }

        // Puts back what Snap kept.
        static void Restore(int[] px, int w, Part p)
        {
            Rectangle box = p.SnapBox;
            for (int y = 0; y < box.Height; y++) Array.Copy(p.Snap, y * box.Width, px, (box.Top + y) * w + box.Left, box.Width);
        }

        // Distance from (px, py) to a rounded box hx by hy either side of (cx, cy), corners r round (negative inside).
        static float RoundBox(float px, float py, float cx, float cy, float hx, float hy, float r)
        {
            float qx = Math.Abs(px - cx) - (hx - r), qy = Math.Abs(py - cy) - (hy - r);
            float ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
            return (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - r;
        }

        // How much of a pixel a shape covers, from its distance to the shape's edge.
        static float Cover(float distance)
        {
            return distance <= -0.5f ? 1f : (distance >= 0.5f ? 0f : 0.5f - distance);
        }

        static float Smooth(float t)
        {
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            return t * t * (3 - 2 * t);
        }

        // Lights one piece (its box in the picture is its SnapBox; the picture there is the clean glass).
        // glow, press: 0-1. A soft light centred on the pointer, about 22% of the way to white at its
        // centre and fading out over 0.8 of the piece's height, only inside the piece's shape (soft
        // edged, from its distance field); its rim a little brighter, more so near the light; pressed,
        // the glass a little darker and the light smaller. Whatever is premultiplied here just
        // moves towards its own alpha (white); the label and mark are left as they are.
        static void Apply(int[] px, int w, Part s, int shiftX, int shiftY, float k, float glow, float press)
        {
            Rectangle box = s.SnapBox, mb = s.MaskBox;
            byte[] mask = s.Mask;
            float ox = s.Cx - shiftX, oy = s.Cy - shiftY, lx = s.LightX - shiftX, ly = s.LightY - shiftY;
            float reach = GlowReachH * 2f * s.Hy * (1f - 0.35f * press), reachInv = 1f / reach, reachSq = reach * reach, wideInv = 1f / (reach * 2.4f);
            float peak = GlowPeak * glow * (1f + 0.15f * press), rim = GlowRim * glow, dark = PressDark * press;
            float ringAt = 0.9f * k, ringHalf = 0.6f * k, rimBand = 2.6f * k;
            // (the light, and the rim's share of it, do not reach further than this from it: a long reply box need not be gone over end to end; a press darkens all of it)
            int x0 = box.Left, x1 = box.Right, y0 = box.Top, y1 = box.Bottom;
            if (dark <= 0f)
            {
                float span = reach * 2.4f + 2f;
                x0 = Math.Max(x0, (int)Math.Floor(lx - span));
                x1 = Math.Min(x1, (int)Math.Ceiling(lx + span));
                y0 = Math.Max(y0, (int)Math.Floor(ly - span));
                y1 = Math.Min(y1, (int)Math.Ceiling(ly + span));
            }
            for (int y = y0; y < y1; y++)
            {
                float fy = y + 0.5f, dy = fy - ly;
                int row = y * w, maskRow = (y + shiftY - mb.Top) * mb.Width + (shiftX - mb.Left);
                for (int x = x0; x < x1; x++)
                {
                    float fx = x + 0.5f;
                    float d = RoundBox(fx, fy, ox, oy, s.Hx, s.Hy, s.Rad);
                    if (d >= 0.5f) continue;
                    float cover = d <= -0.5f ? 1f : 0.5f - d;
                    float dx = fx - lx, distSq = dx * dx + dy * dy, amount = 0f;
                    if (distSq < reachSq)
                    {
                        float t = 1f - (float)Math.Sqrt(distSq) * reachInv;
                        amount = peak * t * t * (3f - 2f * t);
                    }
                    if (d > -rimBand && rim > 0f)
                    {
                        float ring = Cover(Math.Abs(d + ringAt) - ringHalf);
                        if (ring > 0f)
                        {
                            float u = 1f - (float)Math.Sqrt(distSq) * wideInv;
                            u = u <= 0f ? 0f : u * u * (3f - 2f * u);
                            amount += rim * ring * (0.35f + 0.65f * u);
                        }
                    }
                    if (amount <= 0f && dark <= 0f) continue;
                    float own = 1f - mask[maskRow + x] / 255f;   // (what is not label)
                    amount *= cover * own;
                    float keep = 1f - dark * cover * own;
                    int p = px[row + x], a = (p >> 24) & 255, r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
                    if (dark > 0f)
                    {
                        r = (int)(r * keep + 0.5f);
                        g = (int)(g * keep + 0.5f);
                        b = (int)(b * keep + 0.5f);
                    }
                    if (amount > 0f)
                    {
                        r += (int)((a - r) * amount + 0.5f);
                        g += (int)((a - g) * amount + 0.5f);
                        b += (int)((a - b) * amount + 0.5f);
                    }
                    px[row + x] = (a << 24) | (r << 16) | (g << 8) | b;
                }
            }
        }

        // ------------------------------------------------------------------------------------------
        // Keeping a lit button cheap on the way to the screen.
        //
        // While the pointer is on a notification it swells a hair (see HoverSwell), which means Present bends
        // the whole picture (Warp) every time it shows it: a good deal more work than the light itself.
        // So when only the glow changed, and the swell has not moved, only the rows and columns of the
        // bent picture that the changed box reaches are bent again (WarpPart, which does what Warp would
        // for them, exactly), and Windows is told that only that box changed.
        Rectangle glowSinceWarp;       // what Paint changed in the picture since the bent picture (warpOut) was last brought up to date
        Rectangle glowSincePresent;    // ...since the last Present
        Rectangle glowShow;            // what of the bent picture the Present now under way has to hand over alone (empty: all of it)
        bool warpHeld;                 // warpOut holds the whole picture bent as warpLook says, apart from glowSinceWarp
        Deform warpLook;
        Dib warpHeldDib;
        int[] warpHeldOut;

        // (in Present) Brings the bent picture up to date with just the glow's changes, if that can be
        // done, and writes those rows of it to the bitmap. False if the whole picture has to be bent.
        bool WarpGlowPart(int w, int h, Deform look)
        {
            Rectangle changed = glowSinceWarp;
            glowShow = Rectangle.Empty;
            if (lens || !warpHeld || changed.IsEmpty || !look.Same(warpLook)) return false;
            if (!ReferenceEquals(warpHeldDib, presenter.Anim) || !ReferenceEquals(warpHeldOut, presenter.WarpOut) || presenter.WarpOut == null || presenter.WarpOut.Length != w * h || presenter.Anim == null || presenter.Anim.Width != w || presenter.Anim.Height != h) return false;
            Rectangle touched = GlassWarp.BendPart(presenter.Px, w, h, presenter.WarpRows, presenter.WarpOut, presenter.WarpRowScale, presenter.WarpColScale, changed);
            if (touched.IsEmpty) return false;
            presenter.Anim.Write(presenter.WarpOut, touched.Top, touched.Height);
            glowSinceWarp = Rectangle.Empty;
            glowShow = touched;
            return true;
        }

        // (in Present) The whole picture has just been bent (and written) as look says.
        void WarpDone(Deform look)
        {
            warpHeld = !lens;
            warpLook = look;
            warpHeldDib = presenter.Anim;
            warpHeldOut = presenter.WarpOut;
            glowSinceWarp = Rectangle.Empty;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GlowUlw
        {
            public int cbSize;
            public IntPtr hdcDst, pptDst, psize, hdcSrc, pptSrc;
            public int crKey;
            public IntPtr pblend;
            public int dwFlags;
            public IntPtr prcDirty;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindowIndirect(IntPtr hwnd, ref GlowUlw info);

        static IntPtr glowScratch;

        // (in Present, just before the whole picture would be handed over) When only a glass button's
        // light changed since the last Present, and nothing about how the picture is shown did, hands
        // Windows just that box. True if that was done (nothing more to do).
        bool PresentGlow(IntPtr from, int alpha, int w, int h, Point at, Deform look)
        {
            Rectangle show = look.IsNone ? glowSincePresent : glowShow;
            glowSincePresent = Rectangle.Empty;
            glowShow = Rectangle.Empty;
            GlassLayer layer = presenter.Layer;
            if (show.IsEmpty || lens || layer == null || !presenter.LayerVisible || alpha != presenter.ShownAlpha || at != presenter.ShownAt || w != presenter.ShownW || h != presenter.ShownH || !presenter.Shown.Same(look)) return false;
            show = Rectangle.Intersect(show, new Rectangle(0, 0, w, h));
            if (show.IsEmpty) return false;
            if (glowScratch == IntPtr.Zero) glowScratch = Marshal.AllocHGlobal(64);
            // glowScratch: POINT src (0..7), SIZE (8..15), BLENDFUNCTION (16..19), RECT dirty (20..35)
            Marshal.WriteInt32(glowScratch, 0, 0);
            Marshal.WriteInt32(glowScratch, 4, 0);
            Marshal.WriteInt32(glowScratch, 8, w);
            Marshal.WriteInt32(glowScratch, 12, h);
            Marshal.WriteInt32(glowScratch, 16, (Math.Max(0, Math.Min(255, alpha)) << 16) | (1 << 24));   // AC_SRC_OVER, 0, alpha, AC_SRC_ALPHA
            Marshal.WriteInt32(glowScratch, 20, show.Left);
            Marshal.WriteInt32(glowScratch, 24, show.Top);
            Marshal.WriteInt32(glowScratch, 28, show.Right);
            Marshal.WriteInt32(glowScratch, 32, show.Bottom);
            GlowUlw info = new GlowUlw();
            info.cbSize = Marshal.SizeOf(typeof(GlowUlw));
            info.hdcSrc = from;
            info.pptSrc = glowScratch;
            info.psize = IntPtr.Add(glowScratch, 8);
            info.pblend = IntPtr.Add(glowScratch, 16);
            info.dwFlags = 2;   // ULW_ALPHA
            info.prcDirty = IntPtr.Add(glowScratch, 20);
            return UpdateLayeredWindowIndirect(layer.Handle, ref info);
        }
    }
}
