using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LGEngine
{
    // What the owner of a glass tells GlassPresenter when it draws or puts the picture on screen (filled by the owner just
    // before the call): the contents to shade in, where they sit in their picture, and what kind of glass this is.
    public sealed class GlassPresentInput
    {
        public int[] Content;
        public int ContentW, ContentH;
        public Point ContentFrom;   // (where the card's contents are in the pop-up's picture)
        public bool FastContent, WholeWindow;
    }

    // Drawing the glass and putting it on screen: the map cache (which map the card needs now), the light / dark and even-out
    // adaptation, Render (shading the backdrop and contents into the picture), Present (the picture to the layered window,
    // whole, as a dirty rect, or bent), and the counters of them. Moved out of the app's GlassOverlay as it was; the owner
    // supplies its GlassPeek (the backdrop, and the note of the glass's own changes on screen), its GlassLayer, once a frame
    // a GlassPresentInput, and the hooks for what stays with the app (the glass buttons' light, the taskbar's strip).
    public sealed class GlassPresenter
    {
        // (hooks, set once by the owner)
        public Func<bool> GlowShowing;                              // a glass button's light is on the picture now
        public Action GlowAfterShade;                               // the whole glass was just shaded afresh
        public Func<int, int, Deform, bool> WarpGlowPart;           // (w, h, look) only the glow's part of the bent picture was bent again
        public Action<Deform> WarpDone;                             // the whole picture was just bent as look says
        public Func<IntPtr, int, int, int, Point, Deform, bool> PresentGlow;   // (from, alpha, w, h, at, look) only the glow's box was handed over
        public Func<IntPtr, IntPtr, int, int, int, int, int, bool> PresentTaskbarDirty;   // (layer, from, alpha, w, h, a, b) only the taskbar's strip was handed over
        public Action FirstOnScreen;                                // a picture with some opacity has just gone to Windows

        readonly GlassPeek peek;

        public GlassPresenter(GlassPeek peek)
        {
            this.peek = peek;
            ShownAlpha = -1;
            Shown = Deform.None;
        }

        public GlassLayer Layer { get; set; }
        public Dib Out { get; set; }
        public Dib Anim { get; set; }
        public int[] Px { get; set; }
        public GlassMap Map { get { return map; } }
        public GlassMap Primed { get; set; }
        public bool LayerVisible { get; set; }
        public int ShownAlpha { get; set; }
        public Deform Shown { get; set; }
        public Point ShownAt { get; set; }
        public int ShownW { get; set; }
        public int ShownH { get; set; }
        // (the taskbar's columns drawn again since Windows was last handed them: see Present)
        public int TbDirtyFrom, TbDirtyTo;
        // (the bent picture of a Present in the middle of an animation, and what it is made from)
        public int[] WarpRows, WarpOut;
        public float[] WarpRowScale, WarpColScale;

        // The part of the picture drawn again since Windows was last handed it (see Render and Present): empty for
        // none. Standing still, only that part is handed over (UpdateLayeredWindowIndirect with a dirty rect).
        Rectangle dirtyBox;
        long renderedPx, presentedPx;   // pixels drawn again / handed to Windows as dirty rects (for the log)
        // (for the log line and EngineStats: whole-backdrop shades, incremental shades, contents-only shades; dirty-rect and whole presents; looks taken at once; screen changes the glass made itself)
        int shadesFull, shadesInc, shadesContent, rectPresents, fullPresents;

        public long RenderedPx { get { return renderedPx; } }
        public long PresentedPx { get { return presentedPx; } }
        public int ShadesFull { get { return shadesFull; } }
        public int ShadesInc { get { return shadesInc; } }
        public int ShadesContent { get { return shadesContent; } }
        public int RectPresents { get { return rectPresents; } }
        public int FullPresents { get { return fullPresents; } }

        // The start of a pop-up: the counters of drawing and presenting.
        public void ResetCounters()
        {
            shadesFull = shadesInc = shadesContent = rectPresents = fullPresents = 0;
            renderedPx = presentedPx = 0;
        }

        // The map cache.
        GlassMap spareMap, map;
        int sigW, sigH;          // the card the map's name (GlassMap.SignatureFor) was last worked out for
        float sigScale;
        string sigCache;

        // The glass's own look may have changed: the map's name is worked out again.
        public void ForgetSignature()
        {
            sigCache = null;
        }

        // Sees that the map is the one the card needs (the same name, GlassMap.SignatureFor), making it if not. True if the
        // map changed (the backdrop is forgotten; the owner draws again). keepSpare: the volume and brightness cards differ
        // in size and often alternate, so an indicator keeps the map of the other one rather than work it out again.
        // (look and shape are asked for just when needed, as before.)
        public bool FitMap(int cardW, int cardH, float scale, bool keepSpare, Func<GlassLook> look, Func<GlassShape> shape)
        {
            if (cardW != sigW || cardH != sigH || scale != sigScale || sigCache == null)
            {
                sigCache = GlassMap.SignatureFor(cardW, cardH, scale, look(), shape());
                sigW = cardW;
                sigH = cardH;
                sigScale = scale;
            }
            string signature = sigCache;
            if (map == null || map.Signature != signature)
            {
                GlassMap other = spareMap;
                spareMap = keepSpare ? map : null;
                map = other != null && other.Signature == signature ? other : GlassMap.Make(cardW, cardH, scale, look(), shape());
                peek.Backdrop.Forget();
                return true;
            }
            return false;
        }

        // Apple's glass is dark over a dark background and light over a light one, its text and
        // symbols turning dark on it to suit (see GlassMap.Appearance). It changes over AdaptMs once
        // what is behind it is brighter than LightAbove or darker than DarkBelow on average (the
        // way between keeps it from flickering on the line); a new pop-up starts as it should be.
        // Which of the two is judged from the brightness behind the card's middle (where its contents
        // are), not the padding round it; and over a lopsided background the two lines move further
        // apart (by up to LopsidedSlack), as the glass is much the same either way there.
        const int AdaptMs = 260, LightAbove = 165, DarkBelow = 110, LopsidedSlack = 14;
        // The glass is evened out (see GlassMap.Even) from EvenFrom of spread behind the card, fully
        // from EvenFull, over EvenMs when it changes.
        const int EvenFrom = 24, EvenFull = 70, EvenMs = 420;
        float appearance, appearanceGoal;   // 0 dark, 1 light
        float even, evenGoal;               // 0 even backdrop, 1 lopsided
        int appearanceTick;                 // when it last moved on; 0 until a pop-up has its first picture

        public bool LightGoal { get { return appearanceGoal > 0.5f; } }
        // Still turning light or dark, or being evened out.
        public bool Adapting { get { return appearance != appearanceGoal || even != evenGoal; } }

        // The start of a pop-up: the next Adapt takes its goal at once.
        public void ResetAdapt()
        {
            appearanceTick = 0;
        }

        // Sees whether the glass should be light or dark now (see AdaptMs) and moves it on towards
        // that. True if it has changed, and wants drawing again.
        public bool Adapt(bool glassAdapt)
        {
            float goal = appearanceGoal, evenAim = 0f;
            if (!glassAdapt) goal = 0;
            else
            {
                // (all worked out once for each new picture of the backdrop: see GlassBackdrop.Measure)
                peek.Backdrop.Measure(map);
                float u = (peek.Backdrop.Spread - EvenFrom) / (float)(EvenFull - EvenFrom);
                u = u <= 0 ? 0 : (u >= 1 ? 1 : u * u * (3 - 2 * u));
                evenAim = (float)Math.Round(u * 16) / 16f;   // (in sixteenths: a spread wavering a hair does not keep it retinting)
                int slack = (int)(LopsidedSlack * evenAim), luma = peek.Backdrop.MidLuma;
                if (luma >= LightAbove + slack) goal = 1;
                else if (luma <= DarkBelow - slack) goal = 0;
            }
            appearanceGoal = goal;
            evenGoal = evenAim;
            int now = Environment.TickCount;
            if (appearanceTick == 0)
            {
                appearanceTick = now | 1;
                if (appearance == goal && even == evenGoal) return false;
                appearance = goal;
                even = evenGoal;
                return true;
            }
            int since = unchecked(now - appearanceTick);
            appearanceTick = now | 1;
            if (appearance == goal && even == evenGoal) return false;
            float step = Math.Max(1, since) / (float)AdaptMs, evenStep = Math.Max(1, since) / (float)EvenMs;
            appearance = appearance < goal ? Math.Min(goal, appearance + step) : Math.Max(goal, appearance - step);
            even = even < evenGoal ? Math.Min(evenGoal, even + evenStep) : Math.Max(evenGoal, even - evenStep);
            return true;
        }

        // How light the glass is for drawing (eased in and out, as GlassMap.Appearance is).
        public float LightNow()
        {
            return appearance * appearance * (3 - 2 * appearance);
        }

        public void Render(GlassPresentInput inp)
        {
            Render(inp, 0, int.MaxValue);
        }

        // How deep the shadow should be drawn over what is behind now (see GlassMap.ShadowGain).
        public float ShadowGainFor()
        {
            if (!peek.Backdrop.HasPicture) return map.PrimedGain;
            peek.Backdrop.Measure(map);
            float bright = Math.Max(0f, Math.Min(1f, (peek.Backdrop.MidLuma - 80) / 150f));
            float busy = Math.Max(0f, Math.Min(1f, (peek.Backdrop.Spread - 20) / 70f));
            // (never much deeper than the look's own: over a white page the shadow has to stay a
            // soft lift, not a dark smudge round the glass)
            return Math.Min(1.3f, 0.85f + 0.45f * bright + 0.25f * busy);
        }

        // (rows fromRow to toRow of the picture only, when that is all that has changed)
        public void Render(GlassPresentInput inp, int fromRow, int toRow)
        {
            if (Out == null || Out.Width != map.W || Out.Height != map.H)
            {
                if (Out != null) Out.Dispose();
                Out = new Dib(map.W, map.H);
                Px = new int[map.W * map.H];
                Primed = null;
            }
            // The shadow, as Apple's: deeper over a bright or busy background (so the glass stays
            // apart from it), lighter over a plain dark one.
            float shadowGain = ShadowGainFor();
            if (Math.Abs(shadowGain - map.PrimedGain) > 0.08f) { map.ShadowGain = shadowGain; Primed = null; }
            bool whole = false;
            if (Primed != map || (!inp.FastContent && !inp.WholeWindow && GlowShowing()))
            {
                map.Prime(Px);   // the shadow, which stays as it is from frame to frame
                Primed = map;
                fromRow = 0;        // (priming clears the glass too: all of it is drawn again)
                toRow = int.MaxValue;
                whole = true;
            }
            // Where the card's contents are in the pop-up's picture: on the card itself for a
            // notification, wherever the real one currently is for an indicator.
            Point from = inp.ContentFrom;
            map.Appearance = LightNow();   // (eased in and out)
            float evenNow = even * even * (3 - 2 * even);   // (the same way)
            map.Even = evenNow;
            if (evenNow > 0) { map.AvgR = peek.Backdrop.AvgR; map.AvgG = peek.Backdrop.AvgG; map.AvgB = peek.Backdrop.AvgB; }
            bool all = fromRow <= 0 && toRow >= map.H;
            // (only what changed is drawn again: the part of the backdrop that changed, the contents that changed;
            // the map says which part of the picture that was)
            Rectangle did = map.Shade(peek.Backdrop, inp.Content, inp.ContentW, inp.ContentH, from.X, from.Y, Px, all ? 0 : fromRow, all ? map.H : toRow);
            if (whole) did = new Rectangle(0, 0, map.W, map.H);
            if (did.Width <= 0 || did.Height <= 0) return;
            int kind = whole ? 3 : map.LastKind;
            if (kind >= 3) { shadesFull++; EngineStats.FullShades++; }
            else if (kind == 2) { shadesInc++; EngineStats.IncrementalShades++; }
            else { shadesContent++; EngineStats.ContentShades++; }
            if (all && !inp.FastContent && !inp.WholeWindow) GlowAfterShade();   // (the glass buttons' hover light goes back on the clean glass)
            Out.WriteRect(Px, did);
            // (what Windows has yet to be handed: see Present)
            dirtyBox = dirtyBox.Width <= 0 || dirtyBox.Height <= 0 ? did : Rectangle.Union(dirtyBox, did);
            renderedPx += (long)did.Width * did.Height;
        }

        // The glass has just changed on screen (part of it, or all): the screen feed will report that change, which is not
        // news about what is behind the glass (see SelfChangeMs and NoteOwnChange); began is when Present began.
        void Presented(bool part, long began)
        {
            peek.NoteOwnChange(began);
            if (part) { rectPresents++; EngineStats.RectPresents++; }
            else { fullPresents++; EngineStats.FullPresents++; }
        }

        // Puts the current picture on screen at the given place and opacity, bent as look says.
        public void Present(GlassPresentInput inp, Point at, int alpha, Deform look)
        {
            long began = ScreenFeed.Fine();   // (before anything goes to Windows: see NoteOwnChange)
            IntPtr from = Out.Dc;
            int w = Out.Width, h = Out.Height;
            bool wholeWindow = inp.WholeWindow;

            // The taskbar, standing still with only part of it changed: just that strip.
            if (wholeWindow && look.IsNone && LayerVisible && alpha == ShownAlpha && at == ShownAt && w == ShownW && h == ShownH && Shown.IsNone)
            {
                int a = TbDirtyFrom, b = TbDirtyTo;
                TbDirtyFrom = TbDirtyTo = 0;
                if (b <= a) return;
                if (PresentTaskbarDirty(Layer.Handle, from, alpha, w, h, a, b)) { Presented(true, began); return; }
            }
            TbDirtyFrom = TbDirtyTo = 0;

            // Glass standing still with only part of its picture drawn again (an indicator's slider band as the knob
            // moves, the part of the backdrop that changed, a line of text): Windows is handed just that part.
            if (!wholeWindow && look.IsNone && LayerVisible && alpha == ShownAlpha && at == ShownAt && w == ShownW && h == ShownH && Shown.IsNone
                && dirtyBox.Width > 0 && dirtyBox.Height > 0 && (dirtyBox.Width < w || dirtyBox.Height < h))
            {
                Rectangle part = dirtyBox;
                dirtyBox = Rectangle.Empty;
                if (LayerPresent.Rect(Layer.Handle, from, alpha, w, h, part))
                {
                    presentedPx += (long)part.Width * part.Height;
                    Presented(true, began);
                    return;
                }
            }
            dirtyBox = Rectangle.Empty;

            if (!look.IsNone && Px != null)
            {
                // Mid-animation the glass is drawn bent (see Deform), smoothly: a plain stretch
                // would leave its soft edges and shadow jagged.
                if (Anim == null || Anim.Width != w || Anim.Height != h)
                {
                    if (Anim != null) Anim.Dispose();
                    Anim = new Dib(w, h);
                }
                if (WarpOut == null || WarpOut.Length != w * h) { WarpOut = new int[w * h]; WarpRows = new int[w * h]; }
                if (WarpRowScale == null || WarpRowScale.Length < h) WarpRowScale = new float[h];
                if (WarpColScale == null || WarpColScale.Length < w) WarpColScale = new float[w];
                if (!WarpGlowPart(w, h, look))   // (only a glass button's light changed: just what it reaches is bent again)
                {
                    GlassWarp.Bend(Px, w, h, map != null ? map.CardW : w, map != null ? map.CardH : h, look, WarpRows, WarpOut, WarpRowScale, WarpColScale);
                    Anim.Write(WarpOut);
                    WarpDone(look);
                }
                from = Anim.Dc;
            }
            if (PresentGlow(from, alpha, w, h, at, look)) { Presented(true, began); return; }   // (...and likewise only that box is handed to Windows)

            Gdi.POINT position;
            position.X = at.X;
            position.Y = at.Y;
            Gdi.POINT zero;
            zero.X = 0;
            zero.Y = 0;
            Gdi.SIZE size;
            size.Width = w;
            size.Height = h;
            Gdi.BLENDFUNCTION blend = new Gdi.BLENDFUNCTION();
            blend.SourceConstantAlpha = (byte)Math.Max(0, Math.Min(255, alpha));
            blend.AlphaFormat = Gdi.AC_SRC_ALPHA;

            IntPtr screen = Gdi.GetDC(IntPtr.Zero);
            try
            {
                if (!Gdi.UpdateLayeredWindow(Layer.Handle, screen, ref position, ref size, from, ref zero, 0, ref blend, Gdi.ULW_ALPHA))
                {
                    throw new InvalidOperationException("Windows refused to draw the glass (error " + Marshal.GetLastWin32Error() + ")");
                }
            }
            finally
            {
                Gdi.ReleaseDC(IntPtr.Zero, screen);
            }
            ShownAlpha = alpha;
            Shown = look;
            ShownAt = at;
            ShownW = w;
            ShownH = h;
            Presented(false, began);

            if (alpha > 0) FirstOnScreen();
            if (!LayerVisible && alpha > 0)
            {
                Gdi.ShowWindow(Layer.Handle, Gdi.SW_SHOWNOACTIVATE);
                LayerVisible = true;
                peek.NoteOwnChange(began);   // (showing the window is a change on screen too)
            }
        }

        public void HideLayer()
        {
            if (Layer != null && LayerVisible && !Layer.IsDisposed && Layer.IsHandleCreated) { long began = ScreenFeed.Fine(); Gdi.ShowWindow(Layer.Handle, Gdi.SW_HIDE); peek.NoteOwnChange(began); }   // (when quitting, the window may have gone already)
            LayerVisible = false;
        }

        // Lets go of the bitmaps (the owner lets go of its own buffers too).
        public void FreeBitmaps()
        {
            if (Out != null) Out.Dispose();
            if (Anim != null) Anim.Dispose();
            Out = Anim = null;
            Primed = null;
            Px = null;
            peek.Backdrop.Release();
        }
    }
}
