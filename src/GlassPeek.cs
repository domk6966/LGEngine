using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LGEngine
{
    // What the owner of a glass tells GlassPeek once a frame (filled by the owner, read by Step).
    // LayerVisible is a function, not a value: Step may take the layer off screen for a look (HideLayer) and then asks again.
    public sealed class GlassPeekInput
    {
        public bool WantLive, Live, Moving, Sloshing, InShots, WholeWindow, PhaseShown, LevelBusy;
        public bool SavePower, SaverOn, OnBattery;
        public int GlassFps, BatteryFpsLive, Interval;
        public double LiveGapMs;
        public Func<bool> LayerVisible;
        public Action HideLayer;
    }

    // Looking at what is behind the glass: the tap on the screen feed, the step out of screenshots for a look, the
    // filter for the glass's own changes on screen, bursts of following live, and the GlassBackdrop they fill. Moved out of
    // the app's GlassOverlay as it was; the owner supplies a wake (word of a new picture reached the UI thread), its
    // GlassLayer, and once a frame the inputs of GlassPeekInput.
    public sealed class GlassPeek
    {
        static class PeekNative
        {
            [DllImport("dwmapi.dll")]
            public static extern int DwmFlush();
        }

        // When to look: the screen feed is kept on as an alarm. Its pictures have the glass in them
        // and are no good to draw from, but the ring of them around the card, outside the glass
        // proper, shows what is behind: as it is, or darkened by the glass's shadow (depending on
        // whether the glass was in that picture or stepping out of it). The moment that ring is
        // neither of those, compared with the last look (a switch from a light window to a dark one,
        // say), a look is taken. Failing that, a look every PeekGapMs in any case (some changes are
        // only under the card).
        const int PeekGapMs = 700, PeekGapSavingMs = 1500, PeekLeastGapMs = 60, AlarmGapMs = 16;
        public const int TaskbarPeekMs = 6000, TaskbarPeekSavingMs = 15000;
        const int TimerSlackMs = 8;      // timers fire a little early or late; "due" allows for it
        // A picture the feed brings within SelfChangeMs of the glass changing on screen itself (a present, stepping in
        // or out of screenshots: see NoteOwnChange) is taken to be that, and is not a reason to look.
        // (and a picture from up to SelfChangeLeadMs before the change began: the feed's clock and ours are two)
        const int SelfChangeMs = 80, SelfChangeLeadMs = 2;
        // After one such picture, the next look may be PeekDrawnMs later (PeekDrawnSavingMs on battery with "save power");
        // two within DrawnBurstMs: the glass follows what is behind it live (see burstUntil).
        const int PeekDrawnMs = 80, PeekDrawnSavingMs = 250, DrawnBurstMs = 250;
        // And when the alarm goes, the glass does not step straight back in after its look: while
        // what is around it keeps changing (a window sliding past, the switch to another desktop)
        // it stays out of screenshots and follows it live, frame by frame, as it does when not in
        // screenshots at all, and is back in once that has been still for BurstQuietMs.
        const int BurstQuietMs = 300;

        readonly Action wake;
        readonly GlassBackdrop backdrop = new GlassBackdrop();
        GlassLayer layer;
        ScreenFeed.Tap tap;
        int feedPosted;                // 1 while word of a new picture is on its way to this thread
        bool feedReady;
        int livePictures;
        long lookTicks;
        int looks;
        int lastLook;
        Point lookedAt;
        bool behindStirred;
        // Something within the card's area was drawn again that the glass did not do itself (the feed says so): a look
        // behind the glass is due as soon as PeekDrawnMs allows, rather than at the next PeekGapMs. (In screenshots only.)
        bool behindDrawn;
        int lastDrawnTick;   // (by Environment.TickCount) when the last such picture came; 0 = none yet
        // Looks taken for such pictures that found nothing new, one after the other: each doubles the least time before the
        // next (see DrawnGap), so that an area that keeps being drawn again with the same picture (a clock, an app that
        // repaints for nothing) does not take the glass in and out of screenshots over and over. Zero again at any change.
        int drawnMisses;
        // Whether anything within the card's area has changed on screen since the last look behind
        // the glass: the feed only brings a picture when Windows says something there was drawn
        // again (see ScreenFeed and Duplicator.Touches). While nothing has, the look taken now and
        // then anyway (PeekGapMs) has nothing to find, and is left out.
        bool behindTouched;
        int burstUntil;      // (by Environment.TickCount) 0 when not following live
        long burstFrom;      // (by ScreenFeed.Fine) pictures of the screen from after this have no glass in them
        // (for the log line and EngineStats: looks taken at once; screen changes the glass made itself)
        int promptPeeks, selfIgnored;
        long ownChangeFrom, ownChangeTo; // (by ScreenFeed.Fine) the span in which the glass itself last changed on screen (presents, showing, hiding, in or out of screenshots)

        public GlassPeek(Action wake)
        {
            this.wake = wake;
        }

        public GlassBackdrop Backdrop { get { return backdrop; } }
        public GlassLayer Layer { get { return layer; } set { layer = value; } }
        public int LastBackdropChange { get; set; }   // (by Environment.TickCount) when a new picture of the backdrop last differed
        public bool TapUnserved { get { return tap != null && tap.Unserved; } }   // (unserved: the supply is busy with another display)
        public int LivePictures { get { return livePictures; } }
        public int Looks { get { return looks; } }
        public long LookTicks { get { return lookTicks; } }
        public int PromptPeeks { get { return promptPeeks; } }
        public int SelfIgnored { get { return selfIgnored; } }

        // The start of a pop-up: no looks pending, no burst. (inShots: whether the glass shows in screenshots; a burst
        // that was on ends by stepping back into them.)
        public void ResetBehind(bool inShots)
        {
            behindStirred = false;
            behindDrawn = false;
            lastDrawnTick = 0;
            EndBurst(inShots);
        }

        // The start of a pop-up: the counters, and what was seen of the screen.
        public void ResetCounters()
        {
            lookTicks = 0;
            looks = 0;
            livePictures = 0;
            promptPeeks = selfIgnored = 0;
            ownChangeFrom = ownChangeTo = ScreenFeed.Fine();
            drawnMisses = 0;
            feedReady = false;
            backdrop.Forget();
        }

        public void CloseTap()
        {
            if (tap != null) ScreenFeed.Shared.Close(tap);
            tap = null;
            feedReady = false;
            // (Word of a picture may have been on its way to a glass window that has since gone, and
            // with it: the next tap must not wait for it.)
            System.Threading.Interlocked.Exchange(ref feedPosted, 0);
        }

        // Called on the feed's thread when it has a new picture of what is behind the card: pass
        // word to this thread, once (pictures arriving faster than they are drawn just replace
        // each other).
        void OnFeedThread()
        {
            if (System.Threading.Interlocked.Exchange(ref feedPosted, 1) != 0) return;
            try
            {
                GlassLayer window = layer;
                if (window != null) window.BeginInvoke(new Action(OnFeedArrived));
                else feedPosted = 0;
            }
            catch (Exception)
            {
                feedPosted = 0;   // the window is going away
            }
        }

        // (the owner's wake returns at once when it has switched off)
        void OnFeedArrived()
        {
            System.Threading.Interlocked.Exchange(ref feedPosted, 0);
            if (tap == null) return;
            feedReady = true;
            wake();
        }

        // A test seam (the bench, nothing else): while TestScreen is set (a picture of the whole screen, 0xRRGGBB, TestW x TestH), Step
        // takes the backdrop from it instead of the screen: once, and again when the card is somewhere else, the map is another or
        // TestVersion has moved. No tap, no screen reads.
        internal static int[] TestScreen;
        internal static Point TestShift;
        internal static int TestW, TestH, TestVersion;
        Point testAt;
        int testDone = -1;
        GlassMap testMap;

        // The part of Frame that looks behind the glass, from the card's position onwards. True if the backdrop changed.
        public bool Step(GlassPeekInput i, GlassMap map, Point origin, int lookTick)
        {
            int[] test = TestScreen;
            if (test != null)
            {
                if (backdrop.HasPicture && origin == testAt && testDone == TestVersion && ReferenceEquals(map, testMap)) return false;
                testAt = origin;
                testDone = TestVersion;
                testMap = map;
                bool changed = backdrop.ReadPicture(map, test, TestW, TestH, origin.X + TestShift.X, origin.Y + TestShift.Y);
                if (changed) LastBackdropChange = lookTick;
                return changed;
            }
            // Never photograph the screen while the glass itself could be in the picture.
            bool safeToLook = layer.Excluded || !i.LayerVisible();
            bool backdropChanged = false;

            // With a live background, pictures of what is behind the card arrive by
            // themselves whenever it changes (see ScreenFeed): say where the card is now.
            bool alarm = i.InShots && i.LayerVisible() && i.PhaseShown && i.GlassFps > 0 && !i.WholeWindow
                && !(i.SavePower && i.SaverOn) && !ScreenFeed.Shared.Unavailable;
            bool bursting = burstUntil != 0;
            if (i.WantLive || alarm)
            {
                if (tap == null) tap = ScreenFeed.Shared.Open(OnFeedThread);
                double burstGap = 1000.0 / Math.Min(i.GlassFps, i.SavePower && i.OnBattery ? i.BatteryFpsLive : 1000);
                ScreenFeed.Shared.Aim(tap, GlassBackdrop.AreaFor(map, origin.X, origin.Y), map.SharpFactor, map.SharpW, map.SharpH,
                    i.WantLive ? i.LiveGapMs : bursting ? burstGap : Math.Max(burstGap, i.SavePower && i.OnBattery ? AlarmGapMs * 2 : AlarmGapMs), i.Moving);
            }
            else
            {
                CloseTap();
                behindStirred = false;
                behindDrawn = false;
                drawnMisses = 0;
                EndBurst(i.InShots);
            }
            if (alarm && feedReady)
            {
                feedReady = false;
                int[] seen = backdrop.Buffer(map);
                long seenAt;
                if (ScreenFeed.Shared.Take(tap, seen, out seenAt))
                {
                    // (a picture only comes when something within the card's area was drawn again; when that was
                    // the glass itself, being presented or stepping in or out of screenshots, it says nothing
                    // about what is behind it)
                    bool ours = seenAt >= ownChangeFrom - SelfChangeLeadMs && seenAt <= ownChangeTo + SelfChangeMs;
                    if (ours && !bursting) { selfIgnored++; EngineStats.SelfChangesIgnored++; }   // (while following live, every picture follows a present: not counted)
                    else behindTouched = true;
                    if (!i.Moving && !i.Sloshing && backdrop.HasPicture && origin == lookedAt)
                    {
                        bool stirred = RingChanged(seen, map);
                        if (bursting && seenAt > burstFrom)
                        {
                            // following live: a picture without the glass in it, to draw from
                            livePictures++;
                            if (backdrop.Accept(map, false))
                            {
                                backdropChanged = true;
                                LastBackdropChange = lookTick;
                                EngineStats.BackdropUpdate(EngineStats.SourceLive);
                            }
                            if (stirred || backdropChanged) burstUntil = (lookTick + BurstQuietMs) | 1;   // (also when only the inside of the card changed)
                            if (backdropChanged) drawnMisses = 0;
                        }
                        else if (!bursting)   // (otherwise it has the glass in it: not to draw from)
                        {
                            if (stirred) behindStirred = true;
                            else if (!ours)
                            {
                                // Something behind or around the glass was drawn again, and not by us: look at
                                // once (the cheap way: only what changed is shaded again). Twice within
                                // DrawnBurstMs: it is moving, follow it live as for a stir.
                                behindDrawn = true;
                                // (not while the last looks for such pictures found nothing: see drawnMisses)
                                if (drawnMisses == 0 && lastDrawnTick != 0 && unchecked(lookTick - lastDrawnTick) < DrawnBurstMs) behindStirred = true;
                                lastDrawnTick = lookTick | 1;
                            }
                        }
                    }
                }
            }

            // Reading the screen from here stalls for a frame or two. It is needed for the
            // first picture and when the card is somewhere it was not before; without a live
            // supply it is also how changes are noticed, every so often, but never
            // mid-animation (the card keeps the picture it has until it has stopped moving).
            bool mayLook = !i.Moving || !backdrop.HasPicture;
            bool due = !i.Live && i.Interval > 0 && unchecked(lookTick - lastLook) >= i.Interval - TimerSlackMs;
            if (i.Live && feedReady && backdrop.HasPicture && origin == lookedAt)
            {
                feedReady = false;
                if (ScreenFeed.Shared.Take(tap, backdrop.Buffer(map)))
                {
                    livePictures++;
                    backdropChanged = backdrop.Accept(map, false);
                    if (backdropChanged) { LastBackdropChange = lookTick; EngineStats.BackdropUpdate(EngineStats.SourceLive); }
                }
            }
            else if (!backdrop.HasPicture || (safeToLook && mayLook && (due || origin != lookedAt)))
            {
                // A picture is needed whatever happens. Where the glass cannot be kept out
                // of it (see GlassLayer.Excluded), take the glass off screen for this look.
                if (!safeToLook) i.HideLayer();
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                backdropChanged = i.Live || i.InShots ? backdrop.ReadExact(map, origin.X, origin.Y) : backdrop.Read(map, origin.X, origin.Y);   // (in screenshots, to match the alarm's pictures: see RingChanged)
                lookTicks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
                looks++;
                lastLook = lookTick;
                lookedAt = origin;
                if (backdropChanged) { LastBackdropChange = Environment.TickCount; EngineStats.BackdropUpdate(EngineStats.SourceRead); }
            }
            // In screenshots: a look behind when the alarm goes, or now and then anyway; out of
            // screenshots for its moment only (the two frames it takes Windows to put the screen
            // together without the glass, then the look), or, after the alarm, until what is
            // around the glass is still again (see BurstQuietMs).
            if (i.InShots && i.LayerVisible() && i.PhaseShown && !i.Moving && !i.LevelBusy && backdrop.HasPicture && origin == lookedAt
                && i.GlassFps > 0 && !(i.SavePower && i.SaverOn))
            {
                int gap = i.WholeWindow ? (i.SavePower && i.OnBattery ? TaskbarPeekSavingMs : TaskbarPeekMs) : (i.SavePower && i.OnBattery ? PeekGapSavingMs : PeekGapMs);   // (behind the taskbar there is nearly always only the wallpaper)
                int since = unchecked(lookTick - lastLook);
                // The look now and then is only worth taking once something within the card's
                // area has been drawn again since the last (see behindTouched), while the feed
                // is there to say so; failing that, a few gaps on in any case.
                bool told = tap != null && !tap.Unserved && !ScreenFeed.Shared.Unavailable;
                bool worth = behindTouched || !told || since >= gap * 6;
                if (bursting)
                {
                    if (unchecked(lookTick - burstUntil) >= 0 || tap == null || tap.Unserved) EndBurst(i.InShots);
                }
                else if ((behindStirred && since >= PeekLeastGapMs) || (behindDrawn && since >= DrawnGap(i.SavePower && i.OnBattery)) || (since >= gap - 30 && worth))   // (the timer and the clock are both a little coarse)
                {
                    bool stirred = behindStirred;
                    bool drawnPeek = behindDrawn && !stirred;
                    if (stirred || behindDrawn) { promptPeeks++; EngineStats.PromptPeeks++; }
                    behindStirred = false;
                    behindDrawn = false;
                    behindTouched = false;
                    long began2 = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (SetCapt(false))
                    {
                        PeekNative.DwmFlush();
                        PeekNative.DwmFlush();
                        long clear = ScreenFeed.Fine();
                        if (backdrop.ReadExact(map, origin.X, origin.Y))
                        {
                            backdropChanged = true;
                            LastBackdropChange = lookTick;
                            EngineStats.BackdropUpdate(EngineStats.SourceAlarm);
                            drawnMisses = 0;
                        }
                        else if (drawnPeek && drawnMisses < 8) drawnMisses++;   // (drawn again, but nothing new: wait longer next time)
                        if (stirred && tap != null && !tap.Unserved)
                        {
                            burstFrom = clear;
                            burstUntil = (Environment.TickCount + BurstQuietMs) | 1;
                        }
                        else SetCapt(true);
                    }
                    lookTicks += System.Diagnostics.Stopwatch.GetTimestamp() - began2;
                    looks++;
                    lastLook = Environment.TickCount;
                }
            }
            else EndBurst(i.InShots);
            return backdropChanged;
        }

        // The end of Frame's wait: tick fast only while something is actually moving; otherwise just often enough for
        // whichever is due sooner, the next look behind the glass (saving: Save power on, on battery).
        public int Want(int want, bool inShots, bool shown, bool moving, bool wholeWindow, bool saving)
        {
            if (burstUntil != 0) want = Math.Min(want, Math.Max(10, unchecked(burstUntil - Environment.TickCount)));   // (the moment it may be back in screenshots)
            else if (behindStirred || behindDrawn) want = Math.Min(want, Math.Max(10, (behindStirred ? PeekLeastGapMs : DrawnGap(saving)) - unchecked(Environment.TickCount - lastLook)));   // (the moment the next look may be taken)
            else if (layer != null && inShots && shown && !moving) want = Math.Min(want, wholeWindow ? TaskbarPeekMs : PeekGapMs);
            return want;
        }

        // Back into screenshots after following live (see BurstQuietMs), if it was.
        public void EndBurst(bool inShots)
        {
            if (burstUntil == 0) return;
            burstUntil = 0;
            lastLook = Environment.TickCount;
            if (layer != null) SetCapt(inShots);
        }

        // Steps in or out of screenshots. That changes what the screen feed sees of the glass, which it will report: noted as
        // our own change (see SelfChangeMs).
        bool SetCapt(bool want)
        {
            bool was = layer.Capturable;
            long began = ScreenFeed.Fine();
            bool ok = layer.SetCapturable(want);
            if (ok && was != want) NoteOwnChange(began);
            return ok;
        }

        // The glass changed on screen itself, from began (taken before the call that did it) to now: pictures the feed brings
        // from just before began (a little slack: the clocks differ) to SelfChangeMs after now are that, and nothing else.
        // Changes close together make one span, so that a picture between them is ours too; a gap longer than twice
        // SelfChangeMs starts a new one.
        public void NoteOwnChange(long began)
        {
            long now = ScreenFeed.Fine();
            if (ownChangeTo == 0 || began - ownChangeTo > 2 * SelfChangeMs) ownChangeFrom = began;
            ownChangeTo = now;
        }

        // The least time between two looks taken at once for a change drawn behind the glass (see behindDrawn): it grows while
        // such looks keep finding nothing new (see drawnMisses), up to PeekGapMs.
        int DrawnGap(bool saving)
        {
            int g = (saving ? PeekDrawnSavingMs : PeekDrawnMs) << Math.Min(drawnMisses, 4);
            return Math.Min(g, PeekGapMs);
        }

        // Whether the ring of the alarm's picture around the card (see PeekGapMs) shows something
        // else behind the glass than the last look did: more than a few of its texels further than a
        // little from that, both as it is and as the shadow darkens it.
        bool RingChanged(int[] seen, GlassMap m)
        {
            int[] was = backdrop.Sharp, keep = m.ShadowKeep();
            if (was == null || was.Length != seen.Length || keep.Length != seen.Length) return false;
            int w = m.SharpW, h = m.SharpH, f = Math.Max(1, m.SharpFactor);
            // the card in texels, widened by two for its smoothed outline and the light on it
            int left = m.Pad / f - 2, top = m.Pad / f - 2, right = (m.Pad + m.CardW + f - 1) / f + 2, bottom = (m.Pad + m.CardH + f - 1) / f + 2;
            left = Math.Max(0, Math.Min(w, left));
            right = Math.Max(left, Math.Min(w, right));
            int count = w * h - Math.Max(0, Math.Min(h, bottom) - Math.Max(0, top)) * (right - left), moved = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                // (a row through the card: only the parts either side of it)
                for (int x = 0, end = y >= top && y < bottom ? left : w; ; x = right, end = w)
                {
                    for (; x < end; x++)
                    {
                        int i = row + x, p = seen[i], q = was[i];
                        if (p == q) continue;
                        int k = keep[i];
                        if (Off((p >> 16) & 255, (q >> 16) & 255, k) || Off((p >> 8) & 255, (q >> 8) & 255, k) || Off(p & 255, q & 255, k)) moved++;
                    }
                    if (end == w) break;
                }
            }
            return moved > Math.Max(3, count / 300);
        }

        // Whether one channel of what is seen is further than a little outside what was there, from
        // as the shadow darkens it (letting keep/256 through) up to as it is.
        static bool Off(int seen, int was, int keep)
        {
            const int Near = 24;
            return seen > was + Near || seen < (was * keep >> 8) - Near;
        }
    }
}
