using System;
using System.Drawing;

namespace LGEngine
{
    // What the owner of a glass tells GlassAnimator just before Animate / ApplyMotion (filled by the owner each time).
    public sealed class GlassAnimateInput
    {
        public bool FastContent, PhaseShown, AnimHover, Sloshing;
        public int AnimStyle, OsdIndicatorStyle, AnimIn, AnimOut, AnimZoom, AnimLiquid, AnimBounce;
        public int CursorX, CursorY;
        public float Scale, Slosh;
        public Rectangle Card;
    }

    // The card's own motion: how far in it is (vis), the fade, slide and spring of arriving and leaving, the hover swell,
    // and the GlassMotion that bends the glass itself. Moved out of the app's GlassOverlay as it was; the owner supplies
    // SlideNow (where the card would slide in from), PowerSaving, MouseDown, and once a frame the inputs of GlassAnimateInput.
    public sealed class GlassAnimator
    {
        // (the animation styles, as the app's Settings numbers them)
        const int StyleNone = 0, StyleSlide = 2, StyleZoom = 3, StyleSlideZoom = 4, StyleDrop = 5, StylePop = 6, StyleMelt = 7, StyleRipple = 8;

        // Which way the card travels as it arrives, worked out by the owner when it starts to arrive.
        public Func<PointF> SlideNow;
        // settings.SavePower && (Power.OnBattery || Power.SaverOn), asked only when the motion is on at all.
        public Func<bool> PowerSaving;
        // The left mouse button is down.
        public Func<bool> MouseDown;

        // How far "in" the card is: 0 hidden, 1 fully shown. It moves toward wantVisible over time
        // and drives the fade, the slide and the little spring in the size.
        float vis;
        bool wantVisible;
        long lastAnimFine;   // (by ScreenFeed.Fine: all motion is timed by that, not by the 15.6 ms ticks of the ordinary clock)
        PointF slide;
        // (for the stretch of a pop-up on the move: where it was last frame, and when)
        PointF lastShift;
        long lastShiftFine;
        // (a notification under the mouse swells very slightly on a soft spring: hoverPos goes 0 to 1)
        bool hoverNow;
        float hoverPos, hoverSpeed;
        const float HoverSwell = 0.012f, HoverSpring = 16f;   // (the size it gains; the spring's speed)

        public float Vis { get { return vis; } }
        public bool WantVisible { get { return wantVisible; } }
        public PointF Slide { get { return slide; } }
        public bool HoverNow { get { return hoverNow; } set { hoverNow = value; } }

        // Hidden and not wanted, at once (no exit played).
        public void Cut()
        {
            vis = 0;
            wantVisible = false;
        }

        // The rest of a fresh start (the glass started over).
        public void Restart()
        {
            lastAnimFine = ScreenFeed.Fine();
            lastShift = PointF.Empty;
            lastShiftFine = 0;
            hoverNow = false;
            pressPos = pressSpeed = 0;
            hoverGX = -1;
            hoverPos = hoverSpeed = 0;
        }

        public void SetVisible(bool visible)
        {
            if (wantVisible == visible) return;
            wantVisible = visible;
            lastAnimFine = ScreenFeed.Fine();
            lastShift = PointF.Empty;
            lastShiftFine = 0;
            if (visible) slide = SlideNow();

        }

        // Which way the card travels as it arrives: in from the nearest side edge, or from the top
        // or bottom when it sits in the middle of the screen. (home: where the window's card is placed; on: the card on screen.)
        public static PointF SlideFor(float distance, Point home, Rectangle card)
        {
            Rectangle on = new Rectangle(home.X + card.Left, home.Y + card.Top, Math.Max(1, card.Width), Math.Max(1, card.Height));
            Rectangle work = System.Windows.Forms.Screen.FromRectangle(on).WorkingArea;
            float cx = on.Left + on.Width / 2f - work.Left, cy = on.Top + on.Height / 2f - work.Top;
            if (cx > work.Width * 0.62f) return new PointF(distance, 0);
            if (cx < work.Width * 0.38f) return new PointF(-distance, 0);
            return new PointF(0, cy < work.Height / 2f ? -distance : distance);
        }

        // Moves vis toward its goal and works out how the card should look right now.
        // Returns true while it is still moving.
        public bool Animate(GlassAnimateInput inp, out float alpha, out Deform look, out PointF shift)
        {
            long nowFine = ScreenFeed.Fine();
            int dt = (int)Math.Max(0, Math.Min(100, nowFine - lastAnimFine));
            lastAnimFine = nowFine;


            // (the indicator may have an appearance of its own; -1 is the same as notifications)
            int style = inp.FastContent && inp.OsdIndicatorStyle >= 0 ? inp.OsdIndicatorStyle : inp.AnimStyle;
            bool drops = style == StyleDrop, pops = style == StylePop, melts = style == StyleMelt, ripples = style == StyleRipple;
            bool slides = style == StyleSlide || style == StyleSlideZoom;
            bool zooms = style == StyleZoom || style == StyleSlideZoom;
            float goal = wantVisible ? 1f : 0f;
            if (vis != goal)
            {
                if (style == StyleNone)
                {
                    vis = goal;
                }
                else
                {
                    float length = wantVisible ? inp.AnimIn : inp.AnimOut;
                    float step = dt / Math.Max(1f, length);
                    vis = wantVisible ? Math.Min(1f, vis + step) : Math.Max(0f, vis - step);
                }
            }

            alpha = vis;
            look = Deform.None;
            shift = PointF.Empty;
            if ((slides || zooms || drops || pops || melts || ripples) && vis < 1f)
            {
                float smaller = inp.AnimZoom / 100f, liquid = inp.AnimLiquid / 100f * 1.6f;   // (60 is the long-standing look)
                if (wantVisible)
                {
                    if (melts)
                    {
                        // Poured in: it starts as a thin sheet of liquid across its width, fills out
                        // downwards and up, runs a little past full like something thick, and settles
                        // with a slow, gooey quiver.
                        float mleft = 1f - vis, mu = vis - 1f;
                        float mfill = 1f + 2.4f * mu * mu * mu + 1.4f * mu * mu;   // (overshoots, then settles)
                        float quiver = (float)Math.Sin(vis * Math.PI * 3.6) * mleft * mleft * 0.08f * liquid;
                        alpha = Math.Min(1f, vis * 3f);
                        look.ZoomY = Math.Max(0.06f, 0.12f + 0.88f * mfill) * (1f + quiver);
                        look.ZoomX = (1f + 0.1f * mleft * mleft) * (1f - quiver * 0.6f);
                        look.LeanY = 0.1f * liquid * mleft * mleft;    // (heavier at the bottom while it pours)
                        look.Swell = 0.06f * liquid * mleft;
                        return vis != goal;
                    }
                    if (ripples)
                    {
                        // A ripple: it comes in nearly full size while a wave rolls across it from side
                        // to side and dies away, the glass rising and falling under it.
                        float rleft = 1f - vis;
                        float wave = (float)Math.Sin(vis * Math.PI * 5.0) * rleft * rleft;
                        alpha = Math.Min(1f, vis * 3.5f);
                        float rzoom = 0.94f + 0.06f * (1f - rleft * rleft);
                        look.ZoomX = rzoom * (1f + 0.025f * wave * liquid);
                        look.ZoomY = rzoom * (1f - 0.04f * wave * liquid);
                        look.LeanX = 0.16f * wave * liquid;
                        look.Swell = 0.05f * (float)Math.Cos(vis * Math.PI * 5.0) * rleft * rleft * liquid;
                        return vis != goal;
                    }
                    if (drops || pops)
                    {
                        // No slide: the glass itself grows in. A drop starts as a small rounded blob
                        // and fills out with a slow squash-and-stretch wobble that settles; a pop
                        // springs up from only a little smaller, overshoots, and gives a quick wobble.
                        float dleft = 1f - vis, du = vis - 1f;
                        float dover = drops ? 1.1f : 2f;
                        float dgrow = 1f + (dover + 1f) * du * du * du + dover * du * du;
                        float dfrom = drops ? 0.4f : 0.82f;
                        float dzoom = dfrom + (1f - dfrom) * dgrow;
                        float dwobble = (float)Math.Sin(vis * Math.PI * (drops ? 4.2 : 6.0)) * dleft * dleft * (drops ? 0.2f : 0.06f) * liquid;
                        alpha = Math.Min(1f, vis * (drops ? 3f : 4.5f));
                        look.ZoomX = dzoom * (1f - dwobble);
                        look.ZoomY = dzoom * (1f + dwobble);
                        look.Swell = (drops ? 0.12f : 0.03f) * liquid * dleft * dleft;
                        return vis != goal;
                    }
                    // Arrive like a drop landing: a quick fade, a spring that overshoots by the
                    // "bounce" amount before settling, and as it grows, a squash and stretch that
                    // wobbles from one to the other a couple of times and dies away.
                    float bounce = inp.AnimBounce / 100f * 3f;
                    float u = vis - 1f, spring = 1f + (bounce + 1f) * u * u * u + bounce * u * u;
                    alpha = Math.Min(1f, vis * 2.4f);
                    float zoom = zooms ? (1f - smaller) + smaller * spring : 1f;
                    float left = 1f - vis, wobble = (float)Math.Sin(vis * Math.PI * 3.2) * left * left * 0.07f * liquid;
                    if (Math.Abs(slide.Y) > Math.Abs(slide.X)) wobble = -wobble;   // (coming up or down: tall while it travels, wide as it lands)
                    look.ZoomX = zoom * (1f + wobble);
                    look.ZoomY = zoom * (1f - wobble);
                    // (and as it lands, its middle keeps quivering a moment after, like jelly)
                    look.Swell = 0.05f * liquid * left * left + 0.035f * liquid * (float)Math.Sin(vis * Math.PI * 5.5) * left;
                    if (slides) shift = new PointF(slide.X * (1f - spring), slide.Y * (1f - spring));
                }
                else
                {
                    if (melts || ripples)
                    {
                        // Leave: melted away, sinking flatter and spreading at the bottom as it goes;
                        // or a last ripple that flattens out as it fades.
                        float lgone = 1f - vis, lease = lgone * lgone;
                        alpha = 1f - lease;
                        if (melts)
                        {
                            look.ZoomY = 1f - 0.7f * lease;
                            look.ZoomX = 1f + 0.06f * lease;
                            look.LeanY = 0.14f * liquid * lease;
                        }
                        else
                        {
                            float lwave = (float)Math.Sin(lgone * Math.PI * 3.0) * (1f - lgone);
                            look.ZoomX = look.ZoomY = 1f - 0.06f * lease;
                            look.LeanX = 0.1f * lwave * liquid;
                        }
                        return vis != goal;
                    }
                    if (drops || pops)
                    {
                        // Leave: a drop is drawn back into a small blob, stretching a little as it
                        // goes; a pop shrinks away quickly, as if pressed.
                        float dgone = 1f - vis, dease = dgone * dgone;
                        alpha = 1f - dease;
                        float dzoom = drops ? 1f - 0.4f * dease : 1f - 0.12f * dease;
                        float dpinch = (drops ? 0.1f : 0.04f) * liquid * dease;
                        look.ZoomX = dzoom * (1f - dpinch);
                        look.ZoomY = dzoom * (1f + dpinch);
                        look.Swell = (drops ? -0.08f : -0.03f) * liquid * dease;
                        return vis != goal;
                    }
                    // Leave: ease away, shrinking a touch, drawn in at the middle like a drop pulled
                    // thin, and drifting back the way it came.
                    float gone = 1f - vis, ease = gone * gone;
                    alpha = 1f - ease;
                    float zoom = zooms ? 1f - 0.7f * smaller * ease : 1f;
                    look.ZoomX = look.ZoomY = zoom;
                    look.Swell = -0.06f * liquid * ease;
                    if (slides) shift = new PointF(slide.X * 0.55f * ease, slide.Y * 0.55f * ease);
                }
            }
            // A notification under the mouse swells a hair on a soft spring and settles back when
            // the mouse leaves. Nothing here runs (or keeps the animation going) while it is at rest.
            bool hoverBusy = false;
            if (!inp.FastContent && inp.AnimHover && (hoverNow || hoverPos != 0 || hoverSpeed != 0))
            {
                float aim = hoverNow && wantVisible ? 1f : 0f;
                for (float time = dt / 1000f; time > 0; time -= 0.005f)
                {
                    float step = Math.Min(0.005f, time);
                    hoverSpeed += (-HoverSpring * HoverSpring * (hoverPos - aim) - 1.5f * HoverSpring * hoverSpeed) * step;
                    hoverPos += hoverSpeed * step;
                }
                if (Math.Abs(hoverPos - aim) < 0.002f && Math.Abs(hoverSpeed) < 0.02f) { hoverPos = aim; hoverSpeed = 0; }
                look.ZoomX *= 1f + HoverSwell * hoverPos;
                look.ZoomY *= 1f + HoverSwell * hoverPos;
                hoverBusy = hoverPos != aim || hoverSpeed != 0;
            }
            return vis != goal || hoverBusy;
        }

        // On the move a drop draws out along the way it is going, fuller at the front.
        public void Stretch(float scale, PointF shift, ref Deform look)
        {
            long nowS = ScreenFeed.Fine();
            int dtS = (int)Math.Max(1, Math.Min(1000, nowS - lastShiftFine));
            float vx = (shift.X - lastShift.X) / dtS, vy = (shift.Y - lastShift.Y) / dtS;   // (pixels a millisecond)
            if (dtS > 100 || lastShiftFine == 0) vx = vy = 0;
            lastShift = shift;
            lastShiftFine = nowS;
            float speed = (float)Math.Sqrt(vx * vx + vy * vy) / Math.Max(1f, scale), stretch = Math.Min(0.16f, speed * 0.22f);
            if (stretch > 0.004f)
            {
                bool across = Math.Abs(vx) >= Math.Abs(vy);
                if (across) { look.ZoomX *= 1f + stretch; look.ZoomY *= 1f - stretch * 0.5f; look.LeanX += (vx > 0 ? 1 : -1) * stretch * 0.7f; }
                else { look.ZoomY *= 1f + stretch; look.ZoomX *= 1f - stretch * 0.5f; look.LeanY += (vy > 0 ? 1 : -1) * stretch * 0.7f; }
            }
        }

        // The glass itself moving (GlassMotion, see GlassMap.Motion), with the AnimLiquid slider (0: never): it
        // condenses as it arrives (its bend and rim light grow from nothing, with a small damped overshoot as it
        // lands) and thins and flattens as it leaves; under the mouse (AnimHover) its lensing swells softly about the
        // pointer and its rim light leans towards it; a click presses it thinner (less bend, a brighter rim) and it
        // springs back with one damped wobble; an indicator's lens ripples when its level steps (with the slosh).
        // Off on battery with SavePower, or with battery saver on. Exactly neutral whenever all of that is at rest.
        // following: the swell and the light are still catching up with the pointer (see FollowMs).
        public GlassMotion MotionNow(GlassAnimateInput inp, out bool following)
        {
            following = false;
            long nowF = ScreenFeed.Fine();
            float dt = Math.Min(0.05f, Math.Max(0f, (nowF - motionFine) / 1000f));
            motionFine = nowF;
            float liquid = inp.AnimLiquid / 100f;
            if (liquid <= 0 || PowerSaving())
            {
                pressPos = pressSpeed = 0;
                hoverGX = -1;
                return GlassMotion.Neutral;
            }
            float bend = 1f, light = 1f;
            if (vis < 1f)
            {
                if (wantVisible)
                {
                    float u = vis - 1f, over = 1.2f * liquid;
                    bend = Math.Max(0f, 1f + (over + 1f) * u * u * u + over * u * u);
                    light = Math.Min(1f, vis * 1.5f);
                }
                else
                {
                    bend = vis * vis;
                    light = vis;
                }
            }
            // pressed: thinner, its rim brighter, springing back with one damped wobble
            float aim = !inp.FastContent && hoverNow && inp.PhaseShown && wantVisible && MouseDown() ? 1f : 0f;
            if (aim != 0 || pressPos != 0 || pressSpeed != 0)
            {
                for (float time = dt; time > 0; time -= 0.005f)
                {
                    float step = Math.Min(0.005f, time);
                    pressSpeed += (-PressSpring * PressSpring * (pressPos - aim) - 2f * PressDamp * PressSpring * pressSpeed) * step;
                    pressPos += pressSpeed * step;
                }
                if (Math.Abs(pressPos - aim) < 0.003f && Math.Abs(pressSpeed) < 0.03f) { pressPos = aim; pressSpeed = 0; }
            }
            bend *= 1f - 0.3f * liquid * pressPos;
            light *= 1f + 0.5f * liquid * pressPos;
            GlassMotion m = GlassMotion.Neutral;
            m.Bend = bend;
            m.Light = light;
            if (!inp.FastContent && inp.AnimHover && hoverPos != 0f)
            {
                // (the point followed eases after the pointer, and moves in steps of two points)
                float px = inp.CursorX - inp.Card.Left, py = inp.CursorY - inp.Card.Top;
                if (hoverGX < 0) { hoverGX = px; hoverGY = py; }
                float ease = 1f - (float)Math.Exp(-dt / 0.12f);
                hoverGX += (px - hoverGX) * ease;
                hoverGY += (py - hoverGY) * ease;
                if (Math.Abs(px - hoverGX) < inp.Scale && Math.Abs(py - hoverGY) < inp.Scale) { hoverGX = px; hoverGY = py; }
                else following = true;
                float q = 2f * inp.Scale, gx = (float)Math.Round(hoverGX / q) * q, gy = (float)Math.Round(hoverGY / q) * q;
                m.Swell = 0.12f * liquid * hoverPos;
                m.SwellX = gx;
                m.SwellY = gy;
                m.SwellRadius = Math.Min(inp.Card.Width, inp.Card.Height) * 0.9f;
                m.Lean = Math.Min(1f, 0.55f * liquid * Math.Max(0f, hoverPos));
                m.LeanX = gx;
                m.LeanY = gy;
            }
            else hoverGX = -1;
            if (inp.FastContent && inp.Sloshing)
            {
                m.Ripple = 0.35f * liquid * Math.Max(-1f, Math.Min(1f, inp.Slosh));
                m.RippleLength = Math.Max(8f, inp.Card.Width * 0.7f);
                m.RipplePhase = (float)((nowF % 60000) * 0.012);
            }
            return m;
        }

        // Whether two motions differ only in the point the swell and the light follow.
        static bool OnlyPlaceMoved(GlassMotion a, GlassMotion b)
        {
            return a.Bend == b.Bend && a.Light == b.Light && a.Swell == b.Swell && a.SwellRadius == b.SwellRadius && a.Lean == b.Lean
                && a.Ripple == b.Ripple && a.RippleLength == b.RippleLength && a.RipplePhase == b.RipplePhase;
        }
        float pressPos, pressSpeed, hoverGX = -1, hoverGY;
        long motionFine;
        int motionShownAt;
        const float PressSpring = 26f, PressDamp = 0.3f;   // (a quick press with one damped wobble back)
        const int FollowMs = 50;                           // (the swell and the light follow the pointer at most 20 times a second)

        // The glass itself moving (see MotionNow) put on the map: only the bezel's baked offsets and light are scaled,
        // and only while it moves; at rest it is the static glass, to the last pixel. Sets moving when it is still on the
        // move; returns true when the map changed (the owner's dirty).
        public bool ApplyMotion(GlassAnimateInput inp, GlassMap map, ref bool moving)
        {
            bool changed = false;
            bool following;
            GlassMotion gm = MotionNow(inp, out following);
            if (!gm.Same(map.Motion))
            {
                int nowM = Environment.TickCount;
                // (when only the point the swell and the light follow has moved: at most every FollowMs)
                if (!following || !OnlyPlaceMoved(gm, map.Motion) || unchecked(nowM - motionShownAt) >= FollowMs)
                {
                    map.Motion = gm;
                    motionShownAt = nowM;
                    changed = true;
                }
                moving = true;
            }
            else if (following) moving = true;
            return changed;
        }
    }
}
