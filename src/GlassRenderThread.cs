using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LGEngine
{
    // How well the render thread kept to the display (GlassService.Pacing). Frames: self-wanted frames (animation, peek) that
    // ran on their own due time; jitter: how far from the intended start they began (the intended start is vblank-aligned, so
    // this is the wait's precision). BeforeVblankShare: the share of those frames whose picture was handed to Windows before the
    // vblank they aimed at. Wakeups: times the thread has woken since it started (it should not wake while nothing is due).
    public struct GlassPacingInfo
    {
        public int Frames;
        public double MeanJitterMs, P99JitterMs, BeforeVblankShare;
        public long Wakeups;
        public bool HighResolutionTimer;

        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "paced frames {0}, jitter mean {1:0.00} ms p99 {2:0.00} ms, before the targeted vblank {3:0}%, wakeups {4}, {5}",
                Frames, MeanJitterMs, P99JitterMs, BeforeVblankShare * 100, Wakeups, HighResolutionTimer ? "high-resolution timer" : "timeBeginPeriod(1)");
        }
    }

    // A surface's place on the render thread: the handle the transport gives the surface (IGlassChannel) and the thread's schedule
    // entry for it. Run, Due, TargetVblank and Gone belong to the render thread; Wake / Stop / Dispose are for any thread.
    internal sealed class RenderSlot : IGlassChannel
    {
        public SurfaceRuntime Run;
        public int Woken;           // a client call (or the peek) has asked for a frame
        public long Due;            // Stopwatch ticks: the intended start of the next self-wanted frame; 0 = none
        public long TargetVblank;   // the vblank that frame is aiming to finish before
        public bool Gone;
        public readonly GlassRenderThread Owner;

        public RenderSlot(GlassRenderThread owner) { Owner = owner; }

        public void Wake()
        {
            Interlocked.Exchange(ref Woken, 1);
            Owner.Signal();
        }

        public void Stop()
        {
            Owner.Post(delegate
            {
                Due = 0;
                if (Run != null) Run.Stop();
            });
        }

        public void Dispose() { Owner.CloseSlot(this); }
    }

    // The engine's render thread (SERVICE.md sections 4, 5 and 12 step 7): one dedicated STA thread per service that makes and
    // owns every SurfaceRuntime, so every window, GDI object and peek wake lives on it. It sleeps in MsgWaitForMultipleObjectsEx
    // until a client call wakes it (the frame then runs at once), a window message arrives (the layers' feed posts), or a
    // frame the runtime wants by itself is due; with nothing due it waits forever (no wakeups while idle). Self-wanted frames
    // are aligned to the display: each starts so that it should finish about 1.5 ms before a vblank. There is no Application.Run
    // and no DoEvents: the loop pumps messages itself. An exception in a frame is the surface's (it fails); it never ends the thread.
    internal sealed class GlassRenderThread
    {
        const uint Infinite = 0xFFFFFFFF;
        const uint QsAllInput = 0x04FF, InputAvailable = 0x0004;
        const double MarginMs = 1.5;       // a frame should be done this long before the vblank
        const int InvokeTimeoutMs = 10000;
        // A request of at most this many ms is animation (the animator asks for 15 or 25): such a frame may start up to half a refresh
        // period earlier than asked, at the vblank-aligned moment nearest to the ask, because the first aligned moment at or after it
        // would often skip a whole vblank (a 15 ms ask on a 100 Hz display would run at 50 fps). Longer gaps (peek, idle refresh)
        // keep 'never earlier than asked'.
        const int AnimMs = 20;

        readonly Thread thread;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly object qlock = new object();
        readonly Queue<Action> queue = new Queue<Action>();
        readonly List<RenderSlot> slots = new List<RenderSlot>();   // the render thread only
        readonly PaceLog pace = new PaceLog();
        readonly object startLock = new object();   // (Monitor, not an event: nothing to dispose, and a late Pulse after a timed-out wait is harmless)
        bool startedFlag;
        volatile ExtraWait[] extras = new ExtraWait[0];   // (replaced whole under qlock; the loop reads it once per wait)
        volatile bool ended, stopping;
        volatile int threadId;
        long wakeups;
        bool highRes;

        public GlassRenderThread()
        {
            thread = new Thread(Main);
            thread.IsBackground = true;
            thread.Name = "LGEngine render";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            lock (startLock)
            {
                Stopwatch w = Stopwatch.StartNew();
                while (!startedFlag && w.ElapsedMilliseconds < 5000) Monitor.Wait(startLock, 100);
            }
        }

        public bool OnThread { get { return threadId == Thread.CurrentThread.ManagedThreadId; } }
        public bool Ended { get { return ended; } }
        public bool Alive { get { return thread.IsAlive; } }

        public GlassPacingInfo Pacing
        {
            get
            {
                GlassPacingInfo i = pace.Snapshot();
                i.Wakeups = Interlocked.Read(ref wakeups);
                i.HighResolutionTimer = highRes;
                return i;
            }
        }

        // ---- from any thread ----

        public void Signal()
        {
            try { wake.Set(); }
            catch (ObjectDisposedException) { }
        }

        // Queues work for the render thread (it runs after the current frame). False if the thread has ended.
        public bool Post(Action work)
        {
            lock (qlock)
            {
                if (ended) return false;
                queue.Enqueue(work);
            }
            Signal();
            return true;
        }

        // Runs work now if this is the render thread, otherwise queues it.
        public bool PostOrRun(Action work)
        {
            if (OnThread)
            {
                work();
                return true;
            }
            return Post(work);
        }

        // Runs f on the render thread and waits for the answer (not forever: a stuck thread is a failure, not a hang). What f
        // throws is rethrown here as an InvalidOperationException with its text.
        public T Invoke<T>(Func<T> f) where T : class
        {
            if (OnThread) return f();
            object door = new object();   // the monitor both sides use: whichever finishes last needs nothing disposed
            T result = null;
            Exception err = null;
            bool finished = false, abandoned = false;
            bool queued = Post(delegate
            {
                lock (door) { if (abandoned) return; }
                T r = null;
                Exception e = null;
                try { r = f(); }
                catch (Exception ex) { e = ex; }
                bool late;
                lock (door)
                {
                    result = r;
                    err = e;
                    finished = true;
                    late = abandoned;
                    Monitor.PulseAll(door);
                }
                if (late)
                {
                    IDisposable d = r as IDisposable;
                    if (d != null) { try { d.Dispose(); } catch (Exception) { } }
                }
            });
            if (!queued) throw new InvalidOperationException("the render thread has ended");
            lock (door)
            {
                Stopwatch w = Stopwatch.StartNew();
                while (!finished)
                {
                    long left = InvokeTimeoutMs - w.ElapsedMilliseconds;
                    if (left <= 0)
                    {
                        abandoned = true;
                        throw new TimeoutException("the render thread did not answer within " + (InvokeTimeoutMs / 1000) + " s");
                    }
                    Monitor.Wait(door, (int)left);
                }
                if (err != null) throw new InvalidOperationException(err.Message, err);
                return result;
            }
        }

        // An extra handle the thread also waits on (the host's HostWake event): when it is signalled, callback runs on the render
        // thread, before the frames that it may have woken. No timer, no polling: an idle thread still sleeps until something happens.
        // The caller keeps the handle alive until RemoveWait has returned.
        public void AddWait(WaitHandle handle, Action callback)
        {
            lock (qlock)
            {
                ExtraWait[] n = new ExtraWait[extras.Length + 1];
                Array.Copy(extras, n, extras.Length);
                n[extras.Length] = new ExtraWait(handle, callback);
                extras = n;
            }
            Signal();   // (so that the loop picks up the new set)
        }

        public void RemoveWait(WaitHandle handle)
        {
            lock (qlock)
            {
                List<ExtraWait> keep = new List<ExtraWait>();
                foreach (ExtraWait e in extras) if (e.Wait != handle) keep.Add(e);
                extras = keep.ToArray();
            }
            Signal();
            if (!OnThread && !ended) Invoke<object>(delegate { return null; });   // (when this returns the loop has seen the new set)
        }

        sealed class ExtraWait
        {
            public readonly WaitHandle Wait;
            public readonly IntPtr Handle;
            public readonly Action Callback;
            public ExtraWait(WaitHandle wait, Action callback)
            {
                Wait = wait;
                Handle = wait.SafeWaitHandle.DangerousGetHandle();
                Callback = callback;
            }
        }

        public RenderSlot Open(GlassSpec spec, IGlassEvents events, SurfaceState state)
        {
            return Invoke<RenderSlot>(delegate
            {
                RenderSlot slot = new RenderSlot(this);
                slot.Run = new SurfaceRuntime(spec, events, state, slot.Wake);
                slot.Run.Tag = slot;
                slots.Add(slot);
                return slot;
            });
        }

        public void CloseSlot(RenderSlot slot)
        {
            Post(delegate { Retire(slot); });
        }

        // Ask the thread to end and wait for it (2 s); if it will not, it is left running in the background.
        public void Shutdown()
        {
            Post(delegate { stopping = true; });
            if (OnThread) return;   // (a handler disposing the service from the render thread: the loop ends after it returns)
            if (!thread.Join(2000))
                OutputDebugString("LGEngine: the render thread did not end within 2 s; left running as a background thread");
        }

        // ---- the thread ----

        void Main()
        {
            threadId = Thread.CurrentThread.ManagedThreadId;
            Interlocked.Increment(ref GlassPipeStats.threads);   // (for the loopback test's leak check)
            IntPtr timer = IntPtr.Zero;
            bool period = false;
            try
            {
                // A handler that throws inside a window message comes back to the pump below, not to a dialog.
                try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, true); }
                catch (Exception) { }
                try { timer = CreateWaitableTimerEx(IntPtr.Zero, null, 0x2, 0x1F0003); }   // (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION; TIMER_ALL_ACCESS)
                catch (Exception) { timer = IntPtr.Zero; }
                highRes = timer != IntPtr.Zero;
                SignalStarted();
                IntPtr evt = wake.SafeWaitHandle.DangerousGetHandle();
                IntPtr[] handles = new IntPtr[2];
                long armedFor = 0;
                long freq = Stopwatch.Frequency;

                while (!stopping)
                {
                    // Sleep until something is due.
                    long next = NextDue();
                    uint timeout = Infinite;
                    if (next == 0)
                    {
                        if (armedFor != 0) { CancelWaitableTimer(timer); armedFor = 0; }
                        if (period) { timeEndPeriod(1); period = false; }
                    }
                    else
                    {
                        long left = next - Stopwatch.GetTimestamp();
                        if (left <= 0) timeout = 0;
                        else if (highRes)
                        {
                            if (armedFor != next)
                            {
                                long due100ns = -Math.Max(1L, left * 10000000L / freq);
                                if (SetWaitableTimer(timer, ref due100ns, 0, IntPtr.Zero, IntPtr.Zero, false)) armedFor = next;
                                else timeout = (uint)Math.Max(1L, (left * 1000L + freq - 1) / freq);
                            }
                        }
                        else
                        {
                            if (!period) { timeBeginPeriod(1); period = true; }
                            timeout = (uint)Math.Max(1L, (left * 1000L + freq / 2) / freq);
                        }
                    }
                    // The handles: the thread's own event, the timer while one is armed, then any the owner registered (AddWait).
                    ExtraWait[] ex = extras;
                    int first = highRes && armedFor != 0 ? 2 : 1;
                    if (handles.Length != first + ex.Length) handles = new IntPtr[first + ex.Length];
                    handles[0] = evt;
                    if (first == 2) handles[1] = timer;
                    for (int i = 0; i < ex.Length; i++) handles[first + i] = ex[i].Handle;
                    uint r = MsgWaitForMultipleObjectsEx((uint)handles.Length, handles, timeout, QsAllInput, InputAvailable);
                    Interlocked.Increment(ref wakeups);
                    if (first == 2 && r == 1) armedFor = 0;   // (the timer fired; auto-reset)
                    long xi = (long)r - first;
                    if (xi >= 0 && xi < ex.Length)
                    {
                        try { ex[xi].Callback(); } catch (Exception) { }
                    }

                    try { Pump(); } catch (Exception) { }
                    try { Drain(); } catch (Exception) { }
                    if (stopping) break;
                    try { RunDue(highRes ? 0 : freq / 1000); } catch (Exception) { }
                }
            }
            finally
            {
                Finish();
                if (period) timeEndPeriod(1);
                if (timer != IntPtr.Zero) CloseHandle(timer);
                Interlocked.Decrement(ref GlassPipeStats.threads);
                SignalStarted();
            }
        }

        void SignalStarted()
        {
            lock (startLock)
            {
                startedFlag = true;
                Monitor.PulseAll(startLock);
            }
        }

        // The earliest self-wanted frame (ticks), 0 if none.
        long NextDue()
        {
            long best = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                RenderSlot s = slots[i];
                if (s.Gone || s.Due == 0) continue;
                if (best == 0 || s.Due < best) best = s.Due;
            }
            return best;
        }

        void Drain()
        {
            while (true)
            {
                Action a;
                lock (qlock)
                {
                    if (queue.Count == 0) return;
                    a = queue.Dequeue();
                }
                try { a(); } catch (Exception) { }
            }
        }

        // Every slot that was woken, or whose frame is due, runs one frame.
        void RunDue(long tolerance)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                RenderSlot s = slots[i];
                if (s.Gone) continue;
                bool woken = Interlocked.Exchange(ref s.Woken, 0) != 0;
                long now = Stopwatch.GetTimestamp();
                bool due = s.Due != 0 && now + tolerance >= s.Due;
                if (!woken && !due) continue;
                long intended = s.Due, target = s.TargetVblank;
                s.Due = 0;
                long began = Stopwatch.GetTimestamp();
                try { s.Run.RunFrame(); }
                catch (Exception) { s.Run.NextMs = -1; }
                long ended2 = Stopwatch.GetTimestamp();
                if (due && !woken && intended != 0)
                    pace.Add((began - intended) * 1000.0 / Stopwatch.Frequency, target != 0 && ended2 <= target);
                if (s.Gone) continue;
                // An animation frame that ran on its own aligned time counts its successor from that time, not from its end: from the
                // end, its cost (and any lateness) pushed the ask past the half-period window and the frame skipped a vblank.
                int nextMs = s.Run.NextMs;
                long from = due && !woken && intended != 0 && nextMs >= 0 && nextMs <= AnimMs ? intended : ended2;
                s.Due = nextMs < 0 ? 0 : Align(from, ended2, nextMs, s.Run.CostTicks, out s.TargetVblank);
            }
        }

        // When a frame wanted nextMs after `from` should start: the vblank-aligned moment that lets it finish about MarginMs before a
        // vblank given what a frame costs (the lead): the first one not before then, except for animation frames (nextMs <= AnimMs),
        // which take the nearest one, up to half a refresh period early, so that they keep ~60+ fps on 75/100/144 Hz. `from` is the
        // end of the frame that asked (as the app's WinForms timer counted), or for an animation frame that ran on its own aligned
        // time, that time; either way the next frame never starts before `ended` (the asking frame's end): a frame that ran late
        // does not bring on a burst of catch-up frames. Plain `from + nextMs` if DWM will not say.
        static long Align(long from, long ended, int nextMs, double costTicks, out long target)
        {
            target = 0;
            long freq = Stopwatch.Frequency;
            long earliest = from + (long)nextMs * freq / 1000;
            long vb, period;
            if (!GlassVblank.Read(out vb, out period)) return Math.Max(earliest, ended);
            long lead = (long)costTicks + (long)(MarginMs * freq / 1000.0);
            if (lead > period) lead = period;   // (a frame that costs more than a period misses its vblank anyway: aim at the next one, not several ahead)
            long need = earliest + lead;   // the vblank must be at or after this
            long v = need <= vb ? vb - ((vb - need) / period) * period : vb + ((need - vb + period - 1) / period) * period;
            if (nextMs <= AnimMs)
            {
                long lo = v - period;   // the vblank before it
                if (need - lo < v - need) v = lo;
            }
            if (v - lead < ended) v += ((ended - (v - lead)) + period - 1) / period * period;
            target = v;
            return v - lead;
        }

        void Retire(RenderSlot slot)
        {
            if (slot.Gone) return;
            slot.Gone = true;
            slot.Due = 0;
            slots.Remove(slot);
            if (slot.Run != null)
            {
                try { slot.Run.Dispose(); } catch (Exception) { }
            }
        }

        // The end: run what is queued (nothing more is taken), close every surface, let the windows go.
        void Finish()
        {
            Action[] rest;
            lock (qlock)
            {
                ended = true;
                rest = queue.ToArray();
                queue.Clear();
            }
            foreach (Action a in rest)
            {
                try { a(); } catch (Exception) { }
            }
            RenderSlot[] all = slots.ToArray();
            foreach (RenderSlot s in all) Retire(s);
            try { Pump(); } catch (Exception) { }
            try { wake.Dispose(); } catch (Exception) { }
        }

        void Pump()
        {
            MSG m;
            while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1))   // (PM_REMOVE)
            {
                try
                {
                    TranslateMessage(ref m);
                    DispatchMessage(ref m);
                }
                catch (Exception) { }
            }
        }

        // The pacing figures: the first few thousand paced frames.
        sealed class PaceLog
        {
            readonly object gate = new object();
            readonly double[] jitter = new double[8192];
            int n, total, before;

            public void Add(double jitterMs, bool beforeVblank)
            {
                lock (gate)
                {
                    if (n < jitter.Length) jitter[n++] = Math.Abs(jitterMs);
                    total++;
                    if (beforeVblank) before++;
                }
            }

            public GlassPacingInfo Snapshot()
            {
                GlassPacingInfo i = new GlassPacingInfo();
                double[] c;
                lock (gate)
                {
                    c = new double[n];
                    Array.Copy(jitter, c, n);
                    i.Frames = total;
                    i.BeforeVblankShare = total > 0 ? (double)before / total : 0;
                }
                if (c.Length > 0)
                {
                    double sum = 0;
                    foreach (double d in c) sum += d;
                    i.MeanJitterMs = sum / c.Length;
                    Array.Sort(c);
                    i.P99JitterMs = c[Math.Max(1, Math.Min(c.Length, (int)Math.Ceiling(0.99 * c.Length))) - 1];
                }
                return i;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam, lParam;
            public uint time;
            public int ptX, ptY;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint ms, uint mask, uint flags);

        [DllImport("user32.dll")]
        static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr routine, IntPtr arg, bool resume);

        [DllImport("kernel32.dll")]
        static extern bool CancelWaitableTimer(IntPtr timer);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern void OutputDebugString(string text);

        [DllImport("winmm.dll")]
        static extern uint timeBeginPeriod(uint ms);

        [DllImport("winmm.dll")]
        static extern uint timeEndPeriod(uint ms);
    }
}
