using System;
using System.Runtime.InteropServices;

namespace LGGpu
{
    // One piece of glass on screen: its own textures (sized to its card), its window's swap chain
    // (or an off-screen target), and the passes that draw it. Only the engine's render thread
    // touches it.
    internal sealed class Surface : IDisposable
    {
        public readonly Gfx G;
        public Geometry Geo;
        public IntPtr Hwnd;
        public Gfx.Chain Chain;      // live: the composition swap chain in the window
        public Tex Offscreen;        // off-screen: the target instead
        public int ScreenX, ScreenY; // the card's top-left on screen (its window is Margin + Travel further up and left)
        public float ShiftX, ShiftY; // the card's offset inside its window (animation), pixels
        public float Appearance = -1, Opacity = 1, HoverStrength, HoverX, HoverY, HoverRadius = 60;
        public bool ShadowAuto = true;
        public bool NeedsBackdrop = true, NeedsRender = true;
        public int ContentW, ContentH;
        public int Frames, BackdropRefreshes;
        public double LastGpuMs = -1;

        Tex back, edge, edgeTmp, softA, softB, content, haloA, haloB, stats, bend;
        Tex[] levels = new Tex[0];
        readonly Constants c = new Constants();
        Gfx.GpuTimer timer;
        bool hasContent;

        public Surface(Gfx g, Geometry geo)
        {
            G = g;
            Resize(geo);
            timer = g.CreateTimer();
        }

        public int WindowW { get { return Geo.W; } }
        public int WindowH { get { return Geo.H; } }
        public int WindowX { get { return ScreenX - Geo.Margin - Geo.Travel; } }
        public int WindowY { get { return ScreenY - Geo.Margin - Geo.Travel; } }

        // The part of the screen the glass draws from.
        public RECT BackdropRect
        {
            get
            {
                int x = ScreenX - Geo.Pad + (int)Math.Round(ShiftX), y = ScreenY - Geo.Pad + (int)Math.Round(ShiftY);
                return new RECT(x, y, x + Geo.BackW, y + Geo.BackH);
            }
        }

        // (Re)makes the textures for a geometry (a new size, scale, look or elevation).
        public void Resize(Geometry geo)
        {
            bool sameBack = Geo != null && Geo.BackW == geo.BackW && Geo.BackH == geo.BackH && Geo.SoftScale == geo.SoftScale && Geo.EdgeScale == geo.EdgeScale;
            Geo = geo;
            if (!sameBack)
            {
                FreeBackdrop();
                back = G.CreateTex(geo.BackW, geo.BackH, Gfx.FmtBgra8, true, true);
                int n = 0;
                for (int s = 1; s < geo.SoftScale; s *= 2) n++;
                levels = new Tex[n];
                for (int i = 0, s = 2; i < n; i++, s *= 2) levels[i] = G.CreateTex(geo.BackW / s, geo.BackH / s, Gfx.FmtBgra8, true, true);
                softA = G.CreateTex(geo.SoftW, geo.SoftH, Gfx.FmtBgra8, true, true);
                softB = G.CreateTex(geo.SoftW, geo.SoftH, Gfx.FmtBgra8, true, true);
                edge = G.CreateTex(geo.EdgeW, geo.EdgeH, Gfx.FmtBgra8, true, true);
                edgeTmp = G.CreateTex(geo.EdgeW, geo.EdgeH, Gfx.FmtBgra8, true, true);
                stats = G.CreateTex(1, 1, Gfx.FmtRgba32F, true, true);
                G.Clear(stats, 0.5f, 0.5f, 0.5f, 128000f);
            }
            if (bend != null) bend.Dispose();
            GCHandle pin = GCHandle.Alloc(geo.Bend, GCHandleType.Pinned);
            try { bend = G.CreateTex(Geometry.Steps + 1, 1, Gfx.FmtRgba32F, false, true, pin.AddrOfPinnedObject(), (Geometry.Steps + 1) * 16, 1, 0); }
            finally { pin.Free(); }
            NeedsBackdrop = true;
            NeedsRender = true;
        }

        void FreeBackdrop()
        {
            if (back != null) back.Dispose();
            foreach (Tex t in levels) t.Dispose();
            levels = new Tex[0];
            if (softA != null) softA.Dispose();
            if (softB != null) softB.Dispose();
            if (edge != null) edge.Dispose();
            if (edgeTmp != null) edgeTmp.Dispose();
            if (stats != null) stats.Dispose();
            back = softA = softB = edge = edgeTmp = stats = null;
        }

        // The pop-up's own picture (premultiplied BGRA, w x h; null for none), laid over the card
        // from its top-left corner, and the underlay beneath its ink made from it.
        public void SetContent(int[] px, int w, int h)
        {
            hasContent = px != null && w > 0 && h > 0;
            if (!hasContent) { NeedsRender = true; return; }
            if (content == null || content.W != w || content.H != h)
            {
                if (content != null) content.Dispose();
                if (haloA != null) haloA.Dispose();
                if (haloB != null) haloB.Dispose();
                content = G.CreateTex(w, h, Gfx.FmtBgra8, false, true);
                haloA = G.CreateTex(Math.Max(1, w / 2), Math.Max(1, h / 2), Gfx.FmtR8, true, true);
                haloB = G.CreateTex(haloA.W, haloA.H, Gfx.FmtR8, true, true);
            }
            ContentW = w; ContentH = h;
            G.Upload(content, px, w, h);
            // the underlay: the ink at half size, a box average twice across and twice down
            c.Set(19, 1f / haloA.W, 1f / haloA.H, Geo.HaloRadius, 0);
            G.SetConstants(c);
            G.Pass(G.PSAlphaDown, haloA.Rtv, haloA.W, haloA.H, false, content);
            c.Set(18, 1f / haloA.W, 0, 0, 0); G.SetConstants(c); G.Pass(G.PSBox, haloB.Rtv, haloB.W, haloB.H, false, haloA);
            c.Set(18, 0, 1f / haloA.H, 0, 0); G.SetConstants(c); G.Pass(G.PSBox, haloA.Rtv, haloA.W, haloA.H, false, haloB);
            c.Set(18, 1f / haloA.W, 0, 0, 0); G.SetConstants(c); G.Pass(G.PSBox, haloB.Rtv, haloB.W, haloB.H, false, haloA);
            c.Set(18, 0, 1f / haloA.H, 0, 0); G.SetConstants(c); G.Pass(G.PSBox, haloA.Rtv, haloA.W, haloA.H, false, haloB);
            NeedsRender = true;
        }

        // Takes a fresh picture of what is behind the card from 'mirror' (a texture holding the
        // display, whose top-left is at mirrorX, mirrorY on screen), and frosts it.
        public void RefreshBackdrop(Tex mirror, int mirrorX, int mirrorY)
        {
            RECT want = BackdropRect;
            RECT display = new RECT(mirrorX, mirrorY, mirrorX + mirror.W, mirrorY + mirror.H);
            RECT have = RECT.Intersect(want, display);
            if (have.IsEmpty) return;
            G.Copy(back, have.Left - want.Left, have.Top - want.Top, mirror.Ptr, new RECT(have.Left - mirrorX, have.Top - mirrorY, have.Right - mirrorX, have.Bottom - mirrorY));
            // off the display: the nearest pixels on it, repeated (as the CPU engine reads it)
            int x0 = have.Left - want.Left, x1 = have.Right - want.Left, y0 = have.Top - want.Top, y1 = have.Bottom - want.Top;
            for (int x = x0 - 1; x >= 0; x--) G.Copy(back, x, y0, back.Ptr, new RECT(x0, y0, x0 + 1, y1));
            for (int x = x1; x < back.W; x++) G.Copy(back, x, y0, back.Ptr, new RECT(x1 - 1, y0, x1, y1));
            for (int y = y0 - 1; y >= 0; y--) G.Copy(back, 0, y, back.Ptr, new RECT(0, y0, back.W, y0 + 1));
            for (int y = y1; y < back.H; y++) G.Copy(back, 0, y, back.Ptr, new RECT(0, y1 - 1, back.W, y1));
            Frost();
            BackdropRefreshes++;
            NeedsBackdrop = false;
            NeedsRender = true;
        }

        // The same from a picture already the size of the backdrop (off-screen tests).
        public void SetBackdrop(Tex picture, int x, int y)
        {
            RefreshBackdrop(picture, x, y);
        }

        void Frost()
        {
            // the soft picture: shrunk by two until the soft scale, then a Gaussian each way
            Tex src = back;
            for (int i = 0; i < levels.Length; i++)
            {
                G.Pass(G.PSSample, levels[i].Rtv, levels[i].W, levels[i].H, false, src);
                src = levels[i];
            }
            if (Geo.BlurRadius > 0)
            {
                c.Set(18, 1f / Geo.SoftW, 0, Geo.BlurSigmaTex, Geo.BlurRadius); G.SetConstants(c);
                G.Pass(G.PSBlur, softB.Rtv, softB.W, softB.H, false, src);
                c.Set(18, 0, 1f / Geo.SoftH, Geo.BlurSigmaTex, Geo.BlurRadius); G.SetConstants(c);
                G.Pass(G.PSBlur, softA.Rtv, softA.W, softA.H, false, softB);
            }
            else G.Pass(G.PSSample, softA.Rtv, softA.W, softA.H, false, src);
            // the rim's picture: at the scale's size, smoothed a little
            Tex e = Geo.EdgeScale == 1 ? back : levels[0];
            c.Set(19, 1f / Geo.EdgeW, 1f / Geo.EdgeH, 0, 0); G.SetConstants(c);
            if (Geo.EdgePasses == 0) G.Pass(G.PSSample, edge.Rtv, edge.W, edge.H, false, e);
            else
            {
                Tex from = e, to = edge, other = edgeTmp;
                for (int i = 0; i < Geo.EdgePasses; i++)
                {
                    G.Pass(G.PSTent, to.Rtv, to.W, to.H, false, from);
                    from = to; to = other; other = from;
                }
                if (from != edge) G.Pass(G.PSSample, edge.Rtv, edge.W, edge.H, false, from);
            }
            // what the glass needs to know about it
            float sw = Geo.SoftW * Geo.SoftScale, sh = Geo.SoftH * Geo.SoftScale;
            float ax0 = (Geo.Pad + ShiftX * 0) / sw, ay0 = Geo.Pad / sh, ax1 = (Geo.Pad + Geo.CardW) / sw, ay1 = (Geo.Pad + Geo.CardH) / sh;
            c.Set(20, ax0, ay0, ax1, ay1);
            c.Set(21, ax0 + (ax1 - ax0) * 0.18f, ay0 + (ay1 - ay0) * 0.2f, ax1 - (ax1 - ax0) * 0.18f, ay1 - (ay1 - ay0) * 0.2f);
            G.SetConstants(c);
            G.Pass(G.PSStats, stats.Rtv, 1, 1, false, null, softA);
        }

        // Draws the glass into 'rtv' (w x h), premultiplied; over what is there if 'over'.
        public void Render(IntPtr rtv, int w, int h, bool over, bool time)
        {
            Geo.Fill(c, Appearance, Opacity, ShiftX, ShiftY, ShadowAuto);
            float cx = Geo.Margin + Geo.Travel + ShiftX, cy = Geo.Margin + Geo.Travel + ShiftY;
            if (hasContent) c.Set(15, cx, cy, ContentW, ContentH); else c.Set(15, -100000, -100000, 1, 1);
            c.Set(16, haloA != null ? haloA.W : 1, haloA != null ? haloA.H : 1, hasContent ? Geo.Underlay : 0, Opacity);
            c.Set(17, HoverX, HoverY, HoverStrength, HoverRadius);
            G.SetConstants(c);
            if (time) G.TimerBegin(timer);
            G.Pass(G.PSGlass, rtv, w, h, over, null, softA, edge, bend, content, haloA, stats);
            if (time) G.TimerEnd(timer);
            Frames++;
            NeedsRender = false;
        }

        public void Render(bool time)
        {
            if (Chain != null) Render(Chain.Rtv, Chain.W, Chain.H, false, time);
            else if (Offscreen != null) Render(Offscreen.Rtv, Offscreen.W, Offscreen.H, true, time);
        }

        // For the test rig: what the stats pass measured (average colour 0-1 x3, midLuma, spread).
        public float[] ReadStats()
        {
            Tex staging = G.CreateStaging(1, 1, Gfx.FmtRgba32F);
            try
            {
                float[] f = G.ReadBackFloats(stats, staging);
                float mid = (float)Math.Floor(f[3] / 1000), spread = f[3] - mid * 1000;
                return new float[] { f[0], f[1], f[2], mid, spread };
            }
            finally { staging.Dispose(); }
        }

        public double ReadGpuMs(bool wait)
        {
            double ms = G.TimerRead(timer, wait);
            if (ms >= 0) LastGpuMs = ms;
            return ms;
        }

        public void Dispose()
        {
            FreeBackdrop();
            if (bend != null) bend.Dispose();
            if (content != null) content.Dispose();
            if (haloA != null) haloA.Dispose();
            if (haloB != null) haloB.Dispose();
            if (Chain != null) Chain.Dispose();
            if (Offscreen != null) Offscreen.Dispose();
            if (timer != null) timer.Dispose();
            bend = content = haloA = haloB = null;
            Chain = null; Offscreen = null; timer = null;
        }
    }
}
