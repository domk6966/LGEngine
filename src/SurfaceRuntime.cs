using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LGEngine
{
    // One surface's engine, in this process: the layer window, the peek (what is behind), the presenter and the animator,
    // composed as the app's GlassOverlay composes them (src\Glass.cs Frame), but fed from a SurfaceState instead of from a
    // pop-up window. Everything here (construction, frames, Stop, Dispose) runs on one thread: the render thread that made it
    // (GlassRenderThread), or in the comparison mode the creating UI thread. Everything it reads comes through SurfaceState.
    internal sealed class SurfaceRuntime : IGlassChannel
    {
        // The slosh (a lens's lean, bounce or pulse when its level changes): a spring back to level, moved here from the app's GlassOverlay.
        const float SloshSpring = 15f, SloshKick = 4.4f, SloshMost = 0.16f;   // (radians a second; lean per second a kick gives; the most lean)
        const int MorphMs = 260;

        readonly GlassSpec spec;
        readonly IGlassEvents sink;
        readonly SurfaceState st;
        readonly GlassLayer layer;
        readonly GlassPeek peek;
        readonly GlassPresenter presenter;
        readonly GlassAnimator anim = new GlassAnimator();
        readonly GlassPeekInput peekIn = new GlassPeekInput();
        readonly GlassAnimateInput animIn = new GlassAnimateInput();
        readonly GlassPresentInput presentIn = new GlassPresentInput();
        readonly Func<GlassLook> lookFunc;
        readonly Func<GlassShape> shapeFunc;
        readonly bool lens;
        readonly float scale;
        readonly Action wake;

        GlassLook look;
        GlassShape shape;
        Size size;
        Rectangle card;
        float curX, curY;
        bool placed;
        long lastFine;
        int lastVersion = -1, lastLookVer = -1, lastSizeVer = -1, contentVer = -1;
        long pendingStamp;
        bool dirty = true, up, shownRaised, stopped, disposed;
        bool awaitingFirst;       // a Show is waiting for its first visible picture (its call stamp is kept until then)
        bool rested = true;       // hidden and its bitmaps freed (nothing on screen, nothing held)
        bool didPresent;          // the frame in progress put a picture on screen
        bool baked;               // the frame in progress made a new map (its cost is not an ordinary frame's)
        long frameBegan;
        int lastLight = -1;
        double liveGapMs = 66.7;
        int[] content;
        int contentW, contentH;
        readonly GlassGlow glow;
        readonly IGlassLightEvents lightSink;
        float lastPushedLight = float.NaN;
        int lastButtonsVer, lastKickVer, lastCutVer, lastFlowVer;
        float slosh, sloshSpeed, morphFrom = 1f;
        long sloshFine, morphAt;
        bool Sloshing { get { return slosh != 0 || sloshSpeed != 0; } }
        // the live settings (SetUpdates, SetMotion): the props' where they say, the spec's where not
        int fpsSet;
        bool adaptSet, saveSet;
        int travelSet;

        public int NextMs = -1;   // when the next frame is wanted (ms from now); -1 = none until woken
        public double CostTicks;  // what a frame that put a picture on screen costs (Stopwatch ticks, a moving average): the pacing's lead
        public object Tag;        // the transport's own slot
        public Control Sync { get { return layer; } }
        // (for the bench: what the runtime has drawn, and whether it is on the move)
        internal GlassPresenter Presenter { get { return presenter; } }
        internal float Vis { get { return anim.Vis; } }
        internal bool IsSloshing { get { return Sloshing; } }
        internal bool WasMoving;

        public SurfaceRuntime(GlassSpec spec, IGlassEvents sink, SurfaceState state, Action wake)
        {
            this.spec = spec;
            this.sink = sink;
            this.st = state;
            this.wake = wake;
            lens = spec.Kind == GlassKind.Lens;
            scale = spec.Scale > 0f ? spec.Scale : 1f;
            lookFunc = delegate { return look; };
            shapeFunc = delegate { return shape; };

            layer = new GlassLayer(spec.InShots);
            layer.Text = spec.Label;
            IntPtr unused = layer.Handle;   // (the window's handle is made now, here, so that BeginInvoke has a thread to go to)
            peek = new GlassPeek(OnPeekWake);
            peek.Layer = layer;
            presenter = new GlassPresenter(peek);
            presenter.Layer = layer;
            lightSink = sink as IGlassLightEvents;
            fpsSet = spec.Fps;
            adaptSet = spec.Adapt;
            saveSet = spec.SavePower;
            travelSet = spec.AnimTravel;
            // A glass button's light is the glow (GlassSurface.SetButtons); the taskbar's strip is not a service surface's business.
            glow = new GlassGlow(presenter, anim, scale, lens);
            presenter.PresentTaskbarDirty = delegate(IntPtr l, IntPtr from, int alpha, int w, int h, int a, int b) { return false; };
            presenter.FirstOnScreen = delegate
            {
                SurfaceStats ss = st.Stats;
                if (!ss.HasFirst) { ss.FirstTick = Environment.TickCount; ss.HasFirst = true; }
            };
            anim.SlideNow = SlideNow;
            anim.PowerSaving = delegate { return saveSet && (Pwr.OnBattery || Pwr.SaverOn); };
            anim.MouseDown = GlassGlow.LeftDown;
            peekIn.LayerVisible = delegate { return presenter.LayerVisible; };
            peekIn.HideLayer = presenter.HideLayer;
            ApplyLook(st.Props);
            size = st.Props.Size;
            card = new Rectangle(0, 0, size.Width, size.Height);
        }

        void OnPeekWake()
        {
            if (!stopped && !disposed) wake();
        }

        PointF SlideNow()
        {
            return GlassAnimator.SlideFor(travelSet * scale, new Point((int)curX, (int)curY), card);
        }

        void ApplyLook(SurfaceProps p)
        {
            look = p.Look;
            GlassShape s = lens ? GlassShape.Lens : GlassShape.Card;
            string text = p.Profile == null ? "" : p.Profile.Trim();
            if (text.Length > 0)
            {
                GlassProfile prof = GlassProfile.Preset(text, s) ?? GlassProfile.Parse(text);
                if (prof != null) s = s.WithProfile(prof);
            }
            shape = s;
            presenter.ForgetSignature();
            dirty = true;
        }

        // ---- the frame ----

        public void RunFrame()
        {
            if (stopped || disposed) { NextMs = -1; return; }
            long began = Stopwatch.GetTimestamp();
            frameBegan = began;
            didPresent = false;
            baked = false;
            try
            {
                Frame();
            }
            catch (Exception ex)
            {
                NextMs = -1;
                sink.Failed(GlassFailure.FrameFailed, "frame: " + ex.Message);
            }
            long spent = Stopwatch.GetTimestamp() - began;
            // The pacing's lead: what an ordinary animation frame costs. A frame that baked a new map (the first one after a Show
            // or a resize: 50-150 ms when cold) says nothing about the frames that follow and would push their aim 100+ ms ahead;
            // it is left out. The average rises fast and falls slowly, so that a frame that costs more than usual is soon allowed for.
            if (didPresent && !baked)
            {
                if (CostTicks <= 0) CostTicks = spent;
                else CostTicks = spent > CostTicks ? CostTicks * 0.5 + spent * 0.5 : CostTicks * 0.8 + spent * 0.2;
            }
            EngineStats.Busy(spent);
        }

        void Frame()
        {
            SurfaceProps p = st.Props;
            bool raiseShown = false, raiseHidden = false;

            if (p.Version != lastVersion)
            {
                lastVersion = p.Version;
                if (p.LookVer != lastLookVer) { lastLookVer = p.LookVer; ApplyLook(p); }
                if (p.SizeVer != lastSizeVer)
                {
                    lastSizeVer = p.SizeVer;
                    size = p.Size;
                    card = new Rectangle(0, 0, size.Width, size.Height);
                    dirty = true;
                }
                if (!(awaitingFirst && pendingStamp != 0)) pendingStamp = p.CallStamp;   // (a Show's stamp is not replaced by a later call's while it waits)
                if (p.CallStamp != 0) st.Latency.RecordQueue(p.CallStamp, frameBegan);
            }
            fpsSet = p.Fps >= 0 ? p.Fps : spec.Fps;
            adaptSet = p.Adapt >= 0 ? p.Adapt == 1 : spec.Adapt;
            saveSet = p.SavePower >= 0 ? p.SavePower == 1 : spec.SavePower;
            travelSet = p.MotionTravel >= 0 ? p.MotionTravel : spec.AnimTravel;
            if (p.ButtonsVer != lastButtonsVer)
            {
                lastButtonsVer = p.ButtonsVer;
                glow.Set(p.Buttons, p.ButtonsCarry);
                dirty = true;   // (the glass is shaded afresh, the light laid on it clean)
            }
            TakeContents();

            long fine = ScreenFeed.Fine();
            int dt = placed ? (int)Math.Max(1, Math.Min(100, fine - lastFine)) : 16;
            lastFine = fine;
            bool moving = false;

            // Gone at once (Hide(false)): no exit; the end of the frame does the rest. Always applied, even when a Show has followed
            // before this frame: it drops at once, and the Show below then starts the arrival afresh.
            if (p.CutVer != lastCutVer)
            {
                lastCutVer = p.CutVer;
                anim.SetVisible(false);
                anim.Cut();
                shownRaised = false;
                awaitingFirst = false;
                slosh = sloshSpeed = 0f;
                morphAt = 0;
            }
            // The glass flows to another width (another indicator taking over): only once it is fully in.
            if (p.FlowVer != lastFlowVer)
            {
                lastFlowVer = p.FlowVer;
                if (anim.Vis >= 1f)
                {
                    morphFrom = p.FlowFrom;
                    morphAt = fine;
                }
            }
            // A change of level: a kick to the slosh.
            if (p.KickVer != lastKickVer)
            {
                lastKickVer = p.KickVer;
            }
            float push = st.TakeKick();
            if (push != 0f)
            {
                if (!Sloshing) sloshFine = fine;
                else StepSlosh();
                sloshSpeed += push;
            }

            // Where the card is: it glides to a new place (exponential ease) when asked to and visible, otherwise jumps.
            float tx = p.Place.X, ty = p.Place.Y;
            if (!placed || !p.PlaceAnimate || anim.Vis <= 0)
            {
                curX = tx;
                curY = ty;
                placed = true;
            }
            else
            {
                float k = 1f - (float)Math.Exp(-dt / 70.0);
                curX += (tx - curX) * k;
                curY += (ty - curY) * k;
                if (Math.Abs(tx - curX) < 0.5f && Math.Abs(ty - curY) < 0.5f) { curX = tx; curY = ty; }
                else moving = true;
            }
            Point home = new Point((int)Math.Round(curX), (int)Math.Round(curY));

            // Show / hide.
            if (p.WantShown != anim.WantVisible)
            {
                if (p.WantShown)
                {
                    if (anim.Vis <= 0) BeginShow();
                    if (!up) { up = true; EngineStats.SurfaceUp(); }
                    anim.SetVisible(true);
                }
                else
                {
                    shownRaised = false;   // (a Show before the exit is over raises Shown again)
                    awaitingFirst = false;
                    if (anim.Vis <= 0 || !presenter.LayerVisible || presenter.Out == null)
                    {
                        // nothing on screen to play an exit with: off at once (as GlassOverlay.Dismiss does)
                        anim.SetVisible(false);
                        anim.Cut();
                    }
                    else
                    {
                        if (!spec.PeekOnExit) peek.CloseTap();   // the exit plays with the picture it has (PeekOnExit: it goes on looking behind)
                        anim.SetVisible(false);
                    }
                }
            }
            bool exiting = !anim.WantVisible && anim.Vis > 0;

            // The pointer on the card (a card swells under it; a lens does not), inside the hover region if there is one.
            // (a pointer area set by the owner may reach past the card: a notification's whole window)
            bool hovered = false;
            if (p.Hover && !lens)
            {
                int px, py;
                GlassGlow.Cursor(out px, out py);
                Point c = new Point(px, py);
                Rectangle area = new Rectangle(home, size);
                if (p.HasPointer)
                {
                    area = p.Pointer;
                    area.Offset(home);
                }
                else if (!p.HoverRegion.IsEmpty)
                {
                    Rectangle r = p.HoverRegion;
                    r.Offset(home);
                    area.Intersect(r);
                }
                hovered = area.Width > 0 && area.Height > 0 && area.Contains(c);
                if (hovered)
                {
                    animIn.CursorX = c.X - home.X;
                    animIn.CursorY = c.Y - home.Y;
                }
            }
            anim.HoverNow = hovered;

            // Whether pictures of what is behind come by themselves, and how often the glass looks.
            bool onDesktop = SystemInformation.VirtualScreen.IntersectsWith(new Rectangle(home, size));
            int fps = onDesktop ? fpsSet : 0;
            bool wantLive = layer.Excluded && !spec.InShots && fps > 0 && !ScreenFeed.Shared.Unavailable;
            bool live = wantLive && !peek.TapUnserved;
            int animTick = saveSet && Pwr.OnBattery ? 25 : 15;
            int interval = RefreshInterval(live, fps, animTick);
            if (interval <= 0) live = wantLive = false;

            // Animate.
            animIn.FastContent = lens;
            animIn.PhaseShown = anim.WantVisible;   // (false while the exit plays: as GlassOverlay's phase is not Shown then)
            animIn.AnimHover = p.Hover;
            animIn.Sloshing = Sloshing;
            animIn.AnimStyle = p.MotionStyle;
            animIn.OsdIndicatorStyle = p.OsdStyle;
            animIn.AnimIn = p.MotionIn;
            animIn.AnimOut = p.MotionOut;
            animIn.AnimZoom = p.MotionZoom >= 0 ? p.MotionZoom : spec.AnimZoom;
            animIn.AnimLiquid = p.MotionLiquid >= 0 ? p.MotionLiquid : spec.AnimLiquid;
            animIn.AnimBounce = p.MotionBounce >= 0 ? p.MotionBounce : spec.AnimBounce;
            animIn.Scale = scale;
            animIn.Slosh = slosh;
            animIn.Card = card;
            float alpha;
            Deform bend;
            PointF shift;
            if (anim.Animate(animIn, out alpha, out bend, out shift)) moving = true;
            if (morphAt != 0)
            {
                float t = (ScreenFeed.Fine() - morphAt) / (float)MorphMs;
                if (t >= 1f || !lens) morphAt = 0;
                else
                {
                    // ease out with a small overshoot: it flows to the new width and settles, like liquid
                    float u = t - 1f, e = 1f + 2.2f * u * u * u + 1.2f * u * u;
                    bend.ZoomX *= morphFrom + (1f - morphFrom) * e;
                    bend.ZoomY *= 1f + (morphFrom - 1f) * 0.18f * (float)Math.Sin(t * Math.PI);   // (a touch taller as it narrows, and back)
                    moving = true;
                }
            }
            anim.Stretch(scale, shift, ref bend);
            if (Sloshing)
            {
                // A lens, when its level is changed: leaning the way it went, squashing a little as it does, and back. It never moves
                // sideways: the knob on it must not seem to go anywhere but where the level takes it.
                StepSlosh();
                int sloshStyle = p.SloshStyle;
                if (!anim.WantVisible || sloshStyle >= 3) slosh = sloshSpeed = 0;
                else
                {
                    float amount = Math.Abs(slosh);
                    if (sloshStyle == 1)
                    {
                        // Bounce: up stretches it tall and narrow, down squashes it short and wide
                        // (the same amount of glass), and it settles back to its own shape.
                        bend.ZoomY *= 1f + 0.55f * slosh;
                        bend.ZoomX *= 1f - 0.3f * slosh;
                    }
                    else if (sloshStyle == 2)
                    {
                        // Pulse: swells all round, whichever way the level went, and eases back.
                        float swell = Math.Max(0f, slosh);
                        bend.ZoomX *= 1f + 0.4f * swell;
                        bend.ZoomY *= 1f + 0.4f * swell;
                        bend.Swell += 0.2f * swell;
                    }
                    else
                    {
                        bend.LeanX += slosh;
                        bend.ZoomX *= 1f + 0.12f * amount;
                        bend.ZoomY *= 1f - 0.2f * amount;
                        bend.Swell += 0.15f * amount;
                    }
                    moving = true;
                }
            }
            if (p.UseBend) bend = p.Bend;

            bool presented = false;
            if (card.Width > 0 && card.Height > 0 && (anim.Vis > 0 || anim.WantVisible))
            {
                bool drew = false;
                // (the exit plays with the picture it has: no looking behind, no new shading; as GlassOverlay's Closing)
                if (!exiting || spec.PeekOnExit)
                {
                    if (presenter.FitMap(card.Width, card.Height, scale, spec.KeepSpare, lookFunc, shapeFunc)) { dirty = true; baked = true; }
                    animIn.Sloshing = Sloshing;   // (as GlassOverlay: the animator's inputs again, after the slosh moved)
                    animIn.Slosh = slosh;
                    if (anim.ApplyMotion(animIn, presenter.Map, ref moving)) dirty = true;
                    int lookTick = Environment.TickCount;
                    peekIn.WantLive = wantLive;
                    peekIn.Live = live;
                    peekIn.Moving = moving;
                    peekIn.Sloshing = Sloshing;
                    peekIn.InShots = spec.InShots;
                    peekIn.WholeWindow = false;
                    peekIn.PhaseShown = !(exiting && spec.PeekOnExit);   // (as GlassOverlay's phase while it steps aside)
                    peekIn.LevelBusy = p.Busy;
                    peekIn.SavePower = saveSet;
                    peekIn.SaverOn = Pwr.SaverOn;
                    peekIn.OnBattery = Pwr.OnBattery;
                    peekIn.GlassFps = fps;
                    peekIn.BatteryFpsLive = 20;
                    peekIn.Interval = interval;
                    peekIn.LiveGapMs = liveGapMs;
                    bool backdropChanged = peek.Step(peekIn, presenter.Map, home, lookTick);
                    if (peek.Backdrop.HasPicture && presenter.Adapt(adaptSet)) dirty = true;
                    if (PushLight()) TakeContents();   // (the owner may have drawn its contents again to suit the glass: in this frame)
                    if (glow.Any) glow.Step(home, p.HasPointer ? p.Pointer : new Rectangle(0, 0, size.Width, size.Height), animTick);   // (a glass button's light under the pointer)
                    if (dirty || backdropChanged || presenter.Primed != presenter.Map || presenter.Out == null)
                    {
                        long begin = Stopwatch.GetTimestamp();
                        presenter.Render(PresentIn());
                        long drawn = Stopwatch.GetTimestamp() - begin;
                        EngineStats.FrameDone(drawn);
                        st.Stats.Frames++;
                        st.Stats.FrameTicks += drawn;
                        dirty = false;
                        drew = true;
                    }
                }
                if (!drew && !lens && glow.Any && glow.Paint()) drew = true;   // (a glass button's light moved: only its rows were drawn again)
                if (presenter.Out != null && presenter.Map != null)
                {
                    int a8 = (int)(alpha * p.Opacity * 255f + 0.5f);
                    Point at = new Point(home.X - presenter.Map.Margin + (int)Math.Round(shift.X), home.Y - presenter.Map.Margin + (int)Math.Round(shift.Y));
                    if (drew || a8 != presenter.ShownAlpha || !bend.Same(presenter.Shown) || at != presenter.ShownAt)
                    {
                        presenter.Present(PresentIn(), at, a8, bend);
                        presented = true;
                    }
                }
            }
            if (presented)
            {
                didPresent = true;
                if (anim.Vis > 0)
                {
                    if (pendingStamp != 0)
                    {
                        st.Latency.Record(pendingStamp, Stopwatch.GetTimestamp());
                        st.Frames++;
                        st.OnSample();
                    }
                    awaitingFirst = false;
                }
            }
            // A call that presented nothing is not timed by a later frame's present; only a Show waits on, for its first visible pixel.
            if (!(awaitingFirst && anim.WantVisible)) pendingStamp = 0;

            bool light = presenter.LightGoal;
            bool lightChanged = lastLight != (light ? 1 : 0) && peek.Backdrop.HasPicture;
            if (lightChanged) lastLight = light ? 1 : 0;

            if (anim.Vis <= 0 && !anim.WantVisible)
            {
                // As GlassOverlay does when the exit ends (HideLayer, CloseTap, then Hide: Cut, CloseTap, EndBurst, FreeBitmaps).
                presenter.HideLayer();
                peek.CloseTap();
                if (!rested)
                {
                    rested = true;
                    anim.Cut();
                    peek.CloseTap();
                    peek.EndBurst(spec.InShots);
                    presenter.FreeBitmaps();
                }
                if (up)
                {
                    up = false;
                    EngineStats.SurfaceDown();
                    raiseHidden = true;
                }
                shownRaised = false;
            }
            else if (anim.WantVisible && anim.Vis >= 1f && !shownRaised)
            {
                shownRaised = true;
                raiseShown = true;
            }

            // When the next frame is wanted.
            bool shown = anim.WantVisible && anim.Vis >= 1f;
            int want;
            if (anim.Vis <= 0 && !anim.WantVisible) want = -1;
            else if (moving) want = animTick;
            else if (shown) want = interval > 0 && !live ? interval : 250;
            else want = 30;
            if (want > 0)
            {
                if (shown && p.Hover && !lens) want = Math.Min(want, 100);
                if (presenter.Adapting) want = Math.Min(want, animTick);
                want = peek.Want(want, spec.InShots, shown, moving, false, saveSet && Pwr.OnBattery);
                if (glow.Want > 0 && anim.WantVisible && !lens) want = Math.Min(want, glow.Want);   // (the glass buttons' hover light: followed closely while it moves)
            }
            NextMs = want;
            WasMoving = moving;
            PublishStats();

            // Events last: a handler may dispose the surface.
            if (raiseShown) sink.Shown();
            if (lightChanged) sink.AppearanceChanged(light);
            if (raiseHidden) sink.Hidden();
        }

        void BeginShow()
        {
            peek.ResetBehind(spec.InShots);
            presenter.ResetAdapt();
            presenter.ShownAlpha = -1;
            peek.ResetCounters();
            presenter.ResetCounters();
            anim.Restart();
            peek.LastBackdropChange = Environment.TickCount;
            shownRaised = false;
            awaitingFirst = true;
            rested = false;
            lastLight = -1;
            dirty = true;
            slosh = sloshSpeed = 0f;
            morphAt = 0;
            SurfaceStats ss = st.Stats;   // (a fresh count for this showing)
            ss.Frames = 0;
            ss.FrameTicks = 0;
            ss.HasFirst = false;
            ss.BeganTick = Environment.TickCount;
            ss.Began = true;
            PushLight();
            TakeContents();   // (a handler of BeforeRender may have set contents to suit the light: the first frame of the arrival has them)
        }

        // The counters of the peek and the presenter, for the owner's log line (see SurfaceStats).
        void PublishStats()
        {
            SurfaceStats ss = st.Stats;
            ss.LivePictures = peek.LivePictures;
            ss.Looks = peek.Looks;
            ss.LookTicks = peek.LookTicks;
            ss.PromptPeeks = peek.PromptPeeks;
            ss.SelfIgnored = peek.SelfIgnored;
            ss.ShadesFull = presenter.ShadesFull;
            ss.ShadesInc = presenter.ShadesInc;
            ss.ShadesContent = presenter.ShadesContent;
            ss.RenderedPx = presenter.RenderedPx;
            ss.FullPresents = presenter.FullPresents;
            ss.RectPresents = presenter.RectPresents;
            ss.PresentedPx = presenter.PresentedPx;
            ss.LightGoal = presenter.LightGoal;
        }

        // The newest contents (the swap is done inside Front; this array is ours until the next frame's Front). A picture that
        // is not the card's size is the other half of a resize still in flight: left for the frame that follows it.
        void TakeContents()
        {
            int cw, ch, cver;
            int[] front = st.Contents.Front(out cw, out ch, out cver);
            if (cver != contentVer && cw == size.Width && ch == size.Height)
            {
                contentVer = cver;
                content = front;
                contentW = cw;
                contentH = ch;
                dirty = true;
            }
            if (content == null || contentW != size.Width || contentH != size.Height)
            {
                content = new int[size.Width * size.Height];
                contentW = size.Width;
                contentH = size.Height;
                contentVer = -1;
                dirty = true;
            }
        }

        // How light the glass is now, to the surface (Light, LightMoved, BeforeRender). True if it moved.
        bool PushLight()
        {
            float v = presenter.LightNow();
            if (lightSink == null || v == lastPushedLight) return false;
            lastPushedLight = v;
            lightSink.LightMoved(v);
            return true;
        }

        // Moves the slosh on to now: a spring back to level, damped exactly enough not to pass it.
        void StepSlosh()
        {
            long now = ScreenFeed.Fine();
            float dt = Math.Min(0.05f, Math.Max(0f, (now - sloshFine) / 1000f));
            sloshFine = now;
            for (float left = dt; left > 0; left -= 0.005f)
            {
                float step = Math.Min(0.005f, left);
                sloshSpeed += (-SloshSpring * SloshSpring * slosh - 2 * SloshSpring * sloshSpeed) * step;
                slosh += sloshSpeed * step;
            }
            if (slosh > SloshMost) slosh = SloshMost; else if (slosh < -SloshMost) slosh = -SloshMost;
            if (Math.Abs(slosh) < 0.002f && Math.Abs(sloshSpeed) < 0.02f) slosh = sloshSpeed = 0;
        }

        GlassPresentInput PresentIn()
        {
            presentIn.Content = content;
            presentIn.ContentW = contentW;
            presentIn.ContentH = contentH;
            presentIn.ContentFrom = Point.Empty;
            presentIn.FastContent = lens;
            presentIn.WholeWindow = false;
            return presentIn;
        }

        // The shortest time between two looks behind the glass (see GlassOverlay.RefreshInterval); 0 = frozen.
        int RefreshInterval(bool live, int fps, int animTick)
        {
            if (!layer.Excluded || spec.InShots || fps <= 0) return 0;
            if (saveSet)
            {
                if (Pwr.SaverOn) return 0;
                int most = live ? 20 : 10;
                if (Pwr.OnBattery && fps > most) fps = most;
            }
            liveGapMs = 1000.0 / fps;
            if (live) return Math.Max(1, 1000 / fps);
            int ms = Math.Max(animTick, 1000 / fps);
            if (ms < 500 && unchecked(Environment.TickCount - peek.LastBackdropChange) > 1500) ms = 500;
            return ms;
        }

        // ---- stopping ----

        public void Stop()
        {
            if (stopped) return;
            stopped = true;
            NextMs = -1;
            try { presenter.HideLayer(); } catch (Exception) { }
            try { peek.CloseTap(); } catch (Exception) { }
            if (up)
            {
                up = false;
                EngineStats.SurfaceDown();
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            Stop();
            disposed = true;
            try { peek.EndBurst(spec.InShots); } catch (Exception) { }
            try { presenter.FreeBitmaps(); } catch (Exception) { }
            try
            {
                peek.Layer = null;
                presenter.Layer = null;
                layer.Dispose();
            }
            catch (Exception) { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PT { public int X, Y; }

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out PT p);

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int key);

        // The power state (the app's Power class is in src\ and not the engine's): read now and then, only when asked.
        static class Pwr
        {
            [StructLayout(LayoutKind.Sequential)]
            struct Status { public byte AC, Flag, Percent, Saver; public int Life, Full; }

            [DllImport("kernel32.dll")]
            static extern bool GetSystemPowerStatus(out Status s);

            static int at;
            static bool battery, saver;

            static void Refresh()
            {
                int now = Environment.TickCount;
                if (at != 0 && unchecked(now - at) < 5000) return;
                at = now | 1;
                try
                {
                    Status s;
                    if (GetSystemPowerStatus(out s))
                    {
                        battery = s.AC == 0;
                        saver = s.Saver == 1;
                    }
                }
                catch (Exception) { }
            }

            public static bool OnBattery { get { Refresh(); return battery; } }
            public static bool SaverOn { get { Refresh(); return saver; } }
        }
    }
}
