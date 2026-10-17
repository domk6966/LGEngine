using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;

namespace LGEngine
{
    // Keeps glass cards supplied with pictures of what is behind them, from a thread of its own.
    //
    // It waits on Windows (through a Duplicator) for the desktop to change, and only then reads the
    // part of the screen each card needs and tells the card. So a card over a still background
    // costs nothing at all, and one over a moving background gets every change the moment it is on
    // screen, without the thread that draws the glass ever having to stop and read the screen.
    //
    // The thread exists only while at least one card is being shown; a little after the last one
    // goes it lets go of the graphics device and ends.
    //
    // When this way of reading the screen is not available (see Unavailable) cards read the screen
    // themselves, the ordinary way.
    public sealed class ScreenFeed
    {
        public static readonly ScreenFeed Shared = new ScreenFeed();

        // One card's supply.
        public sealed class Tap
        {
            internal readonly Action Notify;     // called on the feed's thread when there is a new picture: must only pass word on
            internal Rectangle Area;             // the part of the screen wanted
            internal int Step, W, H;             // shrunk by Step into W x H texels
            internal double MinGapMs;            // no more often than this, on average
            internal double NextDue;             // by the fine clock: when the next one may go
            internal int Aim;                    // goes up whenever what is wanted changes
            internal int[] Latest;               // the newest picture, and which Aim it was read for
            internal int LatestAim;
            internal long LatestFine;            // when the screen looked like that (by Fine)
            internal bool HasNew, Waiting;
            internal volatile bool Unserved;     // it is on another display than the one being read: its card reads the screen itself
            internal bool Hold;                  // read nothing for now (the card is mid-animation); what is missed is handed over after

            // For the log.
            public int Pictures;

            internal Tap(Action notify) { Notify = notify; }
        }

        const int ParkMs = 1500;        // with no card on screen for this long, stop duplicating the display (see Duplicator.Park)
        const int IdleExitMs = 45000;   // and after this long, let go of the graphics device too and end the thread
        const int RetryMs = 4000;       // after a failure, try again this long after (doubling each time it fails again, up to a minute)
        const int MaxRetryMs = 60000;

        readonly object gate = new object();
        readonly List<Tap> taps = new List<Tap>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread thread;
        volatile bool unavailable;
        int unavailableSince;
        volatile int retryMs = RetryMs;
        volatile string problem;

        ScreenFeed() { }

        // True while pictures cannot be had this way. Only for a few seconds at a time: the usual
        // causes (the lock screen, a permission prompt, a display being switched) pass by themselves,
        // so after that cards ask again and it is tried afresh.
        public bool Unavailable { get { return unavailable && unchecked(Environment.TickCount - unavailableSince) < retryMs; } }

        public string Problem { get { return problem; } }

        // notify is called on the feed's own thread each time a new picture is ready to Take.
        public Tap Open(Action notify)
        {
            Tap tap = new Tap(notify);
            lock (gate)
            {
                taps.Add(tap);
                EnsureThread();
            }
            wake.Set();
            return tap;
        }

        // (Called holding the gate.)
        void EnsureThread()
        {
            if (thread != null) return;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "screen feed";
            thread.Start();
        }

        // Says what the card needs: the screen area, shrunk by step to w x h texels, at most once
        // every minGapMs. Pictures already read for something else are not offered.
        // hold: bring nothing for the moment (the card is mid-animation, and its own movement would
        // otherwise count as the screen changing every frame); the latest is handed over afterwards.
        public void Aim(Tap tap, Rectangle area, int step, int w, int h, double minGapMs, bool hold)
        {
            lock (gate)
            {
                tap.MinGapMs = minGapMs;
                tap.Hold = hold;
                if (taps.Contains(tap)) EnsureThread();
                // While its card is moving, the area it had is kept: nothing is read for it then
                // anyway, and changing the area every frame would make the feed start over each time.
                if (hold && tap.Area.Width > 0) return;
                if (tap.Area == area && tap.Step == step && tap.W == w && tap.H == h) return;
                tap.Area = area;
                tap.Step = step;
                tap.W = w;
                tap.H = h;
                tap.Aim++;
                tap.HasNew = false;
                tap.Waiting = false;
            }
            wake.Set();
        }

        // Copies the newest picture into 'into' (w x h texels as last aimed) if there is one that
        // has not been taken yet. Returns false if there is not.
        public bool Take(Tap tap, int[] into)
        {
            long fine;
            return Take(tap, into, out fine);
        }

        // The same, also saying when (by Fine) the screen looked like that.
        public bool Take(Tap tap, int[] into, out long fine)
        {
            lock (gate)
            {
                fine = tap.LatestFine;
                if (!tap.HasNew || tap.LatestAim != tap.Aim || tap.Latest == null || into == null || into.Length < tap.W * tap.H) return false;
                Array.Copy(tap.Latest, into, tap.W * tap.H);
                tap.HasNew = false;
                return true;
            }
        }

        public void Close(Tap tap)
        {
            if (tap == null) return;
            lock (gate) taps.Remove(tap);
            wake.Set();
        }

        void Run()
        {
            Duplicator dup = null;
            int[] scratch = null;
            int idleSince = 0, lost = 0, lostSince = 0;
            bool park = false;
            Rectangle[] parts = new Rectangle[0];
            bool[] touched = new bool[0];
            try
            {
                while (true)
                {
                  try
                  {
                    // What is wanted right now. One display is read at a time: the one the first card with a
                    // place is on. A card on another display is told so, and reads the screen itself.
                    Tap[] current;
                    Rectangle anchor = Rectangle.Empty, union = Rectangle.Empty;
                    lock (gate)
                    {
                        current = taps.ToArray();
                        foreach (Tap t in current)
                        {
                            if (t.Area.Width <= 0 || t.Area.Height <= 0) continue;
                            anchor = t.Area;
                            break;
                        }
                        if (current.Length == 0)
                        {
                            int now0 = Environment.TickCount;
                            if (idleSince == 0) idleSince = now0 | 1;
                            else if (unchecked(now0 - idleSince) > IdleExitMs)
                            {
                                thread = null;   // a later Open starts a new one
                                return;
                            }
                            park = unchecked(now0 - idleSince) > ParkMs;
                        }
                        else
                        {
                            idleSince = 0;
                            park = false;
                        }
                    }
                    if (current.Length == 0 || anchor.IsEmpty)
                    {
                        if (park && dup != null) dup.Park();
                        wake.WaitOne(park ? 10000 : 500);
                        continue;
                    }
                    park = false;

                    int now = Environment.TickCount;
                    if (unavailable)
                    {
                        if (unchecked(now - unavailableSince) < retryMs)
                        {
                            wake.WaitOne(500);
                            continue;
                        }
                        unavailable = false;
                    }

                    if (dup != null && !dup.Resume())
                    {
                        dup.Dispose();   // the display arrangement has changed since it was parked: start over
                        dup = null;
                    }
                    if (dup == null || !dup.Bounds.Contains(new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2)))
                    {
                        if (dup != null) dup.Dispose();
                        dup = Duplicator.Create(anchor);
                        if (dup == null)
                        {
                            GiveUp(Duplicator.LastProblem);
                            continue;
                        }
                        GlassLog.Write("glass: live background from the graphics card for the display at {0} ({1}){2}", dup.Bounds, dup.Way, problem != null ? "; it is back" : "");
                        problem = null;
                        retryMs = RetryMs;
                    }
                    // The area to read: everything wanted on that display.
                    lock (gate)
                    {
                        foreach (Tap t in current)
                        {
                            bool placed = t.Area.Width > 0 && t.Area.Height > 0, here = placed && dup.Bounds.IntersectsWith(t.Area);
                            t.Unserved = placed && !here;
                            if (here) union = union.IsEmpty ? t.Area : Rectangle.Union(union, t.Area);
                        }
                    }
                    if (union.IsEmpty)
                    {
                        wake.WaitOne(250);
                        continue;
                    }
                    if (dup.SetArea(union))
                    {
                        // Nothing is held for the new area until the screen next changes.
                        lock (gate)
                        {
                            foreach (Tap t in current) t.Waiting = false;
                        }
                    }

                    // No card may have a picture before it is due: until the soonest is, nothing is
                    // read at all (Windows gathers up the changes meanwhile), so a card that wants 15
                    // pictures a second costs 15 reads a second however fast the screen changes.
                    long fine = Fine();
                    double soonest = double.MaxValue;
                    foreach (Tap t in current)
                    {
                        if (t.Hold || t.Unserved || t.Area.Width <= 0) continue;
                        soonest = Math.Min(soonest, t.NextDue - fine);
                    }
                    if (soonest > 3 && soonest < 2000)
                    {
                        wake.WaitOne((int)soonest);
                        continue;
                    }

                    // Then wait for the desktop to change, but no longer than until a picture already
                    // read becomes due for a card that was not ready for it.
                    int timeout = 250;
                    foreach (Tap t in current)
                    {
                        if (!t.Waiting) continue;
                        double left = t.Hold ? 40 : t.NextDue - fine;
                        if (left < timeout) timeout = (int)Math.Max(0, left);
                    }
                    if (parts.Length != current.Length)
                    {
                        parts = new Rectangle[current.Length];
                        touched = new bool[current.Length];
                    }
                    lock (gate)
                    {
                        for (int i = 0; i < current.Length; i++) parts[i] = current[i].Unserved ? Rectangle.Empty : current[i].Area;
                    }
                    int got = dup.Wait(timeout, parts, touched);
                    if (got < 0)
                    {
                        // Windows took the duplication away (a display change, the lock screen, a
                        // full-screen switch). Start over; if it keeps happening, stand down for a bit.
                        dup.Dispose();
                        dup = null;
                        now = Environment.TickCount;
                        if (unchecked(now - lostSince) > 5000) { lost = 0; lostSince = now; }
                        if (++lost > 4) GiveUp("Windows keeps taking the screen picture away");
                        else Thread.Sleep(60);
                        continue;
                    }

                    fine = Fine();
                    for (int i = 0; i < current.Length; i++)
                    {
                        Tap t = current[i];
                        Point from;
                        int step, w, h, aim;
                        lock (gate)
                        {
                            // (Only a card this picture can serve is marked as waiting for it: one that is
                            // not would never be handed anything, and would keep this loop from resting.
                            // And only one whose part of the screen changed.)
                            bool served = !t.Unserved && t.Area.Width > 0 && union.Contains(t.Area);
                            if (!served) t.Waiting = false;
                            else if (got == 1 && touched[i] && t.Area == parts[i]) t.Waiting = true;
                            if (!t.Waiting || t.Hold) continue;
                            if (fine < t.NextDue - 3) continue;   // too soon after the last one; it keeps
                            from = t.Area.Location;
                            step = t.Step;
                            w = t.W;
                            h = t.H;
                            aim = t.Aim;
                        }
                        if (scratch == null || scratch.Length < w * h) scratch = new int[Math.Max(1, w * h)];
                        if (w <= 0 || h <= 0 || step <= 0 || !dup.Read(from, scratch, w, h, step))
                        {
                            lock (gate) t.Waiting = false;   // nothing to be had for this one; do not keep coming back for it
                            continue;
                        }
                        bool tell = false;
                        lock (gate)
                        {
                            if (t.Aim == aim)
                            {
                                if (t.Latest == null || t.Latest.Length < w * h) t.Latest = new int[w * h];
                                Array.Copy(scratch, t.Latest, w * h);
                                t.LatestAim = aim;
                                t.LatestFine = dup.FrameFine;
                                t.HasNew = true;
                                t.Waiting = false;
                                // The next is due one gap after this one was due, not after it went,
                                // so that a rate between two the display can give (45 a second on a
                                // 60 a second display) comes out right on average.
                                t.NextDue = Math.Max(t.NextDue + t.MinGapMs, fine - t.MinGapMs);
                                t.Pictures++;
                                tell = true;
                            }
                        }
                        if (tell)
                        {
                            try { t.Notify(); }
                            catch (Exception) { }
                        }
                    }
                  }
                  catch (Exception ex)
                  {
                      // (A graphics device lost or reset, say.) Start over after a pause: cards read
                      // the screen themselves meanwhile. Only having nothing to do ends this thread.
                      GiveUp(ex.Message);
                      if (dup != null) dup.Dispose();
                      dup = null;
                      wake.WaitOne(1000);
                  }
                }
            }
            catch (Exception ex)
            {
                GiveUp(ex.Message);
                lock (gate) thread = null;
            }
            finally
            {
                if (dup != null) dup.Dispose();
            }
        }

        // Milliseconds on a clock fine enough to tell one frame from the next (the ordinary one
        // only moves every 15 ms or so, which would let a frame in four slip by).
        internal static long Fine()
        {
            long frozen = TestFine;
            if (frozen != 0) return frozen;
            return System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency + 1;
        }

        // A test seam (the bench, nothing else): when not 0, the fine clock stands at this value until it is set again.
        internal static long TestFine;

        void GiveUp(string why)
        {
            if (problem != why) GlassLog.Write("glass: live background not available this way just now (" + why + "); reading the screen the ordinary way");
            // Failing again straight after a retry (the lock screen, a permission prompt): wait
            // longer each time rather than build a new graphics device every few seconds.
            if (unavailable && problem == why) retryMs = Math.Min(MaxRetryMs, retryMs * 2);
            problem = why;
            unavailableSince = Environment.TickCount;
            unavailable = true;
        }
    }
}
