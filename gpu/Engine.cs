using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LGGpu
{
    // The surface service: one render thread owns the D3D11 device, the DirectComposition device
    // and every surface, and draws them. It sleeps on kernel objects when idle (no polling): work
    // from other threads arrives through a queue and an event, desktop changes through an event
    // from the feed thread, and while a surface animates the thread waits on the swap chain's
    // frame-latency object, so presents are paced to the display.
    //
    // The feed thread has a D3D11 device of its own on the same adapter: Desktop Duplication does
    // its work (on an HDR display the conversion to 8-bit) through the immediate context of the
    // device it was made on, inside AcquireNextFrame, so it cannot share the render thread's. It
    // keeps a "mirror" of the display up to date (a texture both devices open, guarded by a keyed
    // mutex): only what changed is copied, and only surfaces whose part changed are told.
    internal sealed class Engine : IDisposable
    {
        const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        const uint WAIT_TIMEOUT = 258, WAIT_OBJECT_0 = 0;

        public Gfx G;
        public readonly List<Surface> Surfaces = new List<Surface>();
        public Tex Mirror;            // the display, as the render device sees it (live mode)
        public bool Offscreen;
        public string Problem;
        public volatile bool Ready;

        // counters for the report
        public long RenderWakeups, FeedWakeups, FeedFrames, FeedPointerOnly, FeedTouching, Presents, PresentErrors, MutexWaitsFailed;
        public int LastPresentError;
        public double LatencySumMs, LatencyMaxMs, DesktopLatencySumMs, DesktopLatencyMaxMs, PresentCallSumMs;
        public int LatencyCount;
        public long FeedCopyPixels;
        public string FeedWay = "";

        Thread render, feed;
        readonly Queue<Action> work = new Queue<Action>();
        readonly AutoResetEvent workEvent = new AutoResetEvent(false);
        readonly AutoResetEvent frameReady = new AutoResetEvent(false);
        readonly ManualResetEvent dupReady = new ManualResetEvent(false);
        volatile bool stop;
        int atX, atY;

        // what the feed hands over: the parts of the display that changed since the render thread last looked
        readonly object handLock = new object();
        List<RECT> pendingRects = new List<RECT>();
        bool pendingAll;
        long pendingAcquireTicks, pendingPresentQpc;
        long handedAcquireTicks, handedPresentQpc;

        // the feed's own device and the shared mirror
        IntPtr feedDevice, feedContext, mirrorFeed, mirrorFeedHandle, mirrorMutexFeed, mirrorMutexRender;
        CopyRegionFn feedCopyRegion;
        CopyResourceFn feedCopyResource;

        // duplication (made on the render thread on the feed device; used by the feed thread)
        IntPtr duplication;
        AcquireFn acquire;
        RectsFn dirtyRects, moveRects;
        NoArgFn releaseFrame;
        IntPtr rectBuffer;
        int rectBufferSize;
        volatile bool dupLost;
        bool first = true;

        // animation (a surface moving in a circle: for pacing and cost measurements)
        public Surface Animating;
        public double AnimRadius = 24, AnimPeriodMs = 2000;
        long animStart;
        bool efficient;

        public Engine(int atX, int atY, bool offscreen)
        {
            this.atX = atX; this.atY = atY;
            Offscreen = offscreen;
        }

        public void Start()
        {
            render = new Thread(RenderLoop);
            render.IsBackground = true;
            render.Name = "LG render";
            render.Start();
            while (!Ready && Problem == null) Thread.Sleep(5);
        }

        // Runs 'action' on the render thread, now-ish (returns at once).
        public void Post(Action action)
        {
            lock (work) work.Enqueue(action);
            workEvent.Set();
        }

        // Pumps the calling thread's window messages (set by the host): a thread that owns windows
        // must keep doing so while it waits, as the render thread's composition calls on those
        // windows need it.
        public static Action Pump;

        // Runs 'action' on the render thread and waits for it.
        public void Invoke(Action action)
        {
            if (Thread.CurrentThread == render) { action(); return; }
            ManualResetEvent done = new ManualResetEvent(false);
            Exception failure = null;
            Post(delegate
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
                finally { done.Set(); }
            });
            if (Pump != null)
            {
                IntPtr[] h = { done.SafeWaitHandle.DangerousGetHandle() };
                while (!done.WaitOne(0))
                {
                    Pump();
                    Win.MsgWaitForMultipleObjects(1, h, false, 1000, 0x04FF);
                }
            }
            else done.WaitOne();
            if (failure != null) throw new InvalidOperationException("on the render thread: " + failure.Message, failure);
        }

        void RenderLoop()
        {
            try
            {
                G = Gfx.Create(atX, atY);
                if (G == null) { Problem = Gfx.LastProblem; return; }
                if (!Offscreen)
                {
                    G.EnsureDComp();
                    string p = OpenFeedDevice();
                    if (p == null) p = OpenDuplication();
                    if (p != null) { Problem = p; return; }
                    feed = new Thread(FeedLoop);
                    feed.IsBackground = true;
                    feed.Name = "LG feed";
                    feed.Start();
                }
                Win.SetEfficient(true);
                efficient = true;
                Ready = true;
                IntPtr[] handles = new IntPtr[3];
                while (!stop)
                {
                    int n = 0;
                    handles[n++] = workEvent.SafeWaitHandle.DangerousGetHandle();
                    handles[n++] = frameReady.SafeWaitHandle.DangerousGetHandle();
                    uint timeout = 0xFFFFFFFF;
                    bool animating = Animating != null && Animating.Chain != null;
                    if (animating)
                    {
                        if (Animating.Chain.Waitable != IntPtr.Zero) handles[n++] = Animating.Chain.Waitable;
                        else timeout = 8;
                    }
                    uint r = Win.WaitForMultipleObjects((uint)n, handles, false, timeout);
                    RenderWakeups++;
                    if (stop) break;
                    if (r == WAIT_OBJECT_0 + 1) TakeRects();
                    Drain();
                    if (dupLost) ReopenDuplication();
                    if (animating && Animating != null && (r == WAIT_OBJECT_0 + 2 || r == WAIT_TIMEOUT)) AnimateStep();
                    bool any = false;
                    foreach (Surface s in Surfaces) if (s.NeedsBackdrop && Mirror != null) any = true;
                    if (any)
                    {
                        if (Gfx.Lock(mirrorMutexRender, 1000))
                        {
                            try { foreach (Surface s in Surfaces) if (s.NeedsBackdrop) s.RefreshBackdrop(Mirror, G.DisplayBounds.Left, G.DisplayBounds.Top); }
                            finally { Gfx.Unlock(mirrorMutexRender); }
                        }
                        else MutexWaitsFailed++;
                    }
                    foreach (Surface s in Surfaces) if (s.NeedsRender && s.Chain != null) Present(s);
                    SetPace(Animating != null);
                }
            }
            catch (Exception ex)
            {
                Problem = ex.Message;
            }
            finally
            {
                stop = true;
                dupReady.Set();
                if (feed != null) feed.Join(3000);
                foreach (Surface s in Surfaces) s.Dispose();
                Surfaces.Clear();
                CloseDuplication();
                if (Mirror != null) Mirror.Dispose();
                Com.Release(ref mirrorMutexRender);
                Com.Release(ref mirrorMutexFeed);
                Com.Release(ref mirrorFeed);
                Com.Release(ref feedContext);
                Com.Release(ref feedDevice);
                if (G != null) G.Dispose();
                Ready = false;
            }
        }

        void SetPace(bool animating)
        {
            if (animating == !efficient) return;
            efficient = !animating;
            Win.SetEfficient(efficient);
            Win.SetThreadPriority(Win.GetCurrentThread(), animating ? 1 : 0);   // ABOVE_NORMAL while animating
        }

        void Drain()
        {
            while (true)
            {
                Action a;
                lock (work)
                {
                    if (work.Count == 0) return;
                    a = work.Dequeue();
                }
                a();
            }
        }

        void AnimateStep()
        {
            Surface s = Animating;
            if (animStart == 0) animStart = Stopwatch.GetTimestamp();
            double t = (Stopwatch.GetTimestamp() - animStart) * 1000.0 / Stopwatch.Frequency / AnimPeriodMs * 2 * Math.PI;
            float x = (float)(Math.Cos(t) * AnimRadius), y = (float)(Math.Sin(t) * AnimRadius);
            if (Math.Abs(x - s.ShiftX) < 0.01f && Math.Abs(y - s.ShiftY) < 0.01f) return;
            s.ShiftX = x; s.ShiftY = y;
            s.NeedsBackdrop = true;   // (the card looks at another part of the screen now)
        }

        public void StopAnimation()
        {
            Surface s = Animating;
            Animating = null;
            animStart = 0;
            if (s != null) { s.ShiftX = s.ShiftY = 0; s.NeedsBackdrop = true; }
        }

        void Present(Surface s)
        {
            long t0 = Stopwatch.GetTimestamp();
            s.Render(true);
            long t1 = Stopwatch.GetTimestamp();
            int hr = G.Present(s.Chain, 1);
            long t2 = Stopwatch.GetTimestamp();
            Presents++;
            if (hr < 0) { PresentErrors++; LastPresentError = hr; }
            PresentCallSumMs += (t2 - t1) * 1000.0 / Stopwatch.Frequency;
            if (handedAcquireTicks != 0)
            {
                double ms = (t2 - handedAcquireTicks) * 1000.0 / Stopwatch.Frequency;
                LatencySumMs += ms;
                if (ms > LatencyMaxMs) LatencyMaxMs = ms;
                LatencyCount++;
                if (handedPresentQpc != 0)
                {
                    double dms = (t2 - handedPresentQpc) * 1000.0 / Stopwatch.Frequency;
                    DesktopLatencySumMs += dms;
                    if (dms > DesktopLatencyMaxMs) DesktopLatencyMaxMs = dms;
                }
                handedAcquireTicks = 0;
            }
            s.ReadGpuMs(false);
        }

        // ---- the feed ----

        string OpenFeedDevice()
        {
            int level;
            int hr = Dll.D3D11CreateDevice(G.Adapter, 0, IntPtr.Zero, 0x20 | (Gfx.DebugLayer ? 0x2u : 0u), IntPtr.Zero, 0, 7, out feedDevice, out level, out feedContext);
            if (hr < 0) return "feed device: D3D11CreateDevice failed " + Com.Hex(hr);
            feedCopyRegion = Com.Method<CopyRegionFn>(feedContext, 46);
            feedCopyResource = Com.Method<CopyResourceFn>(feedContext, 47);
            mirrorFeed = Gfx.CreateSharedTexture(feedDevice, G.DisplayBounds.Width, G.DisplayBounds.Height, Gfx.FmtBgra8, out mirrorFeedHandle, out mirrorMutexFeed);
            Mirror = G.OpenShared(mirrorFeedHandle, G.DisplayBounds.Width, G.DisplayBounds.Height, Gfx.FmtBgra8, out mirrorMutexRender);
            return null;
        }

        string OpenDuplication()
        {
            IntPtr display = Com.Query(G.Output, IID.Output1);
            if (display == IntPtr.Zero) return "the display cannot be duplicated";
            try
            {
                IntPtr output5 = Com.Query(G.Output, IID.Output5);
                int hr = -1;
                if (output5 != IntPtr.Zero)
                {
                    int[] formats = { Gfx.FmtBgra8 };
                    GCHandle pin = GCHandle.Alloc(formats, GCHandleType.Pinned);
                    try { hr = Com.Method<Duplicate1Fn>(output5, 26)(output5, feedDevice, 0, 1, pin.AddrOfPinnedObject(), out duplication); }
                    finally { pin.Free(); Com.Release(ref output5); }
                    FeedWay = "DuplicateOutput1 (8-bit asked for)";
                }
                if (hr < 0 || duplication == IntPtr.Zero)
                {
                    hr = Com.Method<DuplicateFn>(display, 22)(display, feedDevice, out duplication);
                    if (hr < 0 || duplication == IntPtr.Zero) { duplication = IntPtr.Zero; return "DuplicateOutput failed " + Com.Hex(hr); }
                    FeedWay = "DuplicateOutput";
                }
            }
            finally { Com.Release(ref display); }
            IntPtr dd = Marshal.AllocHGlobal(64);
            try
            {
                Com.Method<DescVoidFn>(duplication, 7)(duplication, dd);
                int format = Marshal.ReadInt32(dd, 16);
                FeedWay += ", format " + format;
                if (format != 87 && format != 88 && format != 91 && format != 93) { Com.Release(ref duplication); return "the display's picture is not 8-bit (format " + format + ")"; }
            }
            finally { Marshal.FreeHGlobal(dd); }
            acquire = Com.Method<AcquireFn>(duplication, 8);
            dirtyRects = Com.Method<RectsFn>(duplication, 9);
            moveRects = Com.Method<RectsFn>(duplication, 10);
            releaseFrame = Com.Method<NoArgFn>(duplication, 14);
            first = true;
            dupLost = false;
            dupReady.Set();
            return null;
        }

        void CloseDuplication()
        {
            dupReady.Reset();
            Com.Release(ref duplication);
            acquire = null;
            if (rectBuffer != IntPtr.Zero) Marshal.FreeHGlobal(rectBuffer);
            rectBuffer = IntPtr.Zero;
            rectBufferSize = 0;
        }

        void ReopenDuplication()
        {
            CloseDuplication();
            Thread.Sleep(100);
            string p = OpenDuplication();
            if (p != null) Problem = p;
            else foreach (Surface s in Surfaces) s.NeedsBackdrop = true;
        }

        // Blocks in AcquireNextFrame; copies what changed into the mirror (on the feed's own
        // device) and tells the render thread which parts.
        void FeedLoop()
        {
            List<RECT> rects = new List<RECT>();
            try
            {
                while (!stop)
                {
                    if (!dupReady.WaitOne(500)) continue;
                    if (stop) break;
                    FrameInfo info;
                    IntPtr resource;
                    int hr = acquire(duplication, 2000, out info, out resource);
                    FeedWakeups++;
                    if (hr == DXGI_ERROR_WAIT_TIMEOUT) continue;
                    if (hr < 0)
                    {
                        dupLost = true;
                        dupReady.Reset();
                        workEvent.Set();
                        continue;
                    }
                    long acquired = Stopwatch.GetTimestamp();
                    try
                    {
                        if (!first && info.LastPresentTime == 0) { FeedPointerOnly++; continue; }   // only the pointer moved
                        FeedFrames++;
                        IntPtr texture = Com.Query(resource, IID.Texture2D);
                        if (texture == IntPtr.Zero) continue;
                        try
                        {
                            bool all = first || info.TotalMetadataBufferSize == 0;
                            rects.Clear();
                            if (!all && !ReadRects(info, rects)) all = true;
                            first = false;
                            RECT whole = new RECT(0, 0, G.DisplayBounds.Width, G.DisplayBounds.Height);
                            if (!Gfx.Lock(mirrorMutexFeed, 1000)) { MutexWaitsFailed++; continue; }
                            try
                            {
                                if (all)
                                {
                                    feedCopyResource(feedContext, mirrorFeed, texture);
                                    FeedCopyPixels += (long)whole.Width * whole.Height;
                                }
                                else
                                {
                                    for (int i = 0; i < rects.Count; i++)
                                    {
                                        RECT r = RECT.Intersect(rects[i], whole);
                                        if (r.IsEmpty) continue;
                                        Box box;
                                        box.Left = (uint)r.Left; box.Top = (uint)r.Top; box.Right = (uint)r.Right; box.Bottom = (uint)r.Bottom; box.Front = 0; box.Back = 1;
                                        feedCopyRegion(feedContext, mirrorFeed, 0, (uint)r.Left, (uint)r.Top, 0, texture, 0, ref box);
                                        FeedCopyPixels += (long)r.Width * r.Height;
                                    }
                                }
                            }
                            finally { Gfx.Unlock(mirrorMutexFeed); }
                            lock (handLock)
                            {
                                if (all) { pendingAll = true; pendingRects.Clear(); }
                                else if (!pendingAll) pendingRects.AddRange(rects);
                                pendingAcquireTicks = acquired;
                                pendingPresentQpc = info.LastPresentTime;
                            }
                            frameReady.Set();
                        }
                        finally { Marshal.Release(texture); }
                    }
                    finally
                    {
                        if (resource != IntPtr.Zero) Marshal.Release(resource);
                        releaseFrame(duplication);
                    }
                }
            }
            catch (Exception ex)
            {
                Problem = "feed: " + ex.Message;
            }
        }

        bool ReadRects(FrameInfo info, List<RECT> into)
        {
            int size = (int)info.TotalMetadataBufferSize;
            if (rectBuffer == IntPtr.Zero || rectBufferSize < size)
            {
                if (rectBuffer != IntPtr.Zero) Marshal.FreeHGlobal(rectBuffer);
                rectBufferSize = Math.Max(size, 4096);
                rectBuffer = Marshal.AllocHGlobal(rectBufferSize);
            }
            uint used;
            if (moveRects(duplication, (uint)rectBufferSize, rectBuffer, out used) < 0) return false;
            int moves = (int)(used / 24);
            uint used2;
            int left = rectBufferSize - (int)used;
            if (dirtyRects(duplication, (uint)left, IntPtr.Add(rectBuffer, (int)used), out used2) < 0) return false;
            int dirty = (int)(used2 / 16);
            for (int i = 0; i < moves; i++)
            {
                int o = i * 24 + 8;   // DXGI_OUTDUPL_MOVE_RECT: POINT source, RECT destination
                into.Add(new RECT(Marshal.ReadInt32(rectBuffer, o), Marshal.ReadInt32(rectBuffer, o + 4), Marshal.ReadInt32(rectBuffer, o + 8), Marshal.ReadInt32(rectBuffer, o + 12)));
            }
            for (int i = 0; i < dirty; i++)
            {
                int o = (int)used + i * 16;
                into.Add(new RECT(Marshal.ReadInt32(rectBuffer, o), Marshal.ReadInt32(rectBuffer, o + 4), Marshal.ReadInt32(rectBuffer, o + 8), Marshal.ReadInt32(rectBuffer, o + 12)));
            }
            return true;
        }

        // On the render thread: which surfaces the changes since last time touch.
        void TakeRects()
        {
            List<RECT> rects;
            bool all;
            lock (handLock)
            {
                rects = pendingRects;
                pendingRects = new List<RECT>();
                all = pendingAll;
                pendingAll = false;
                handedAcquireTicks = pendingAcquireTicks;
                handedPresentQpc = pendingPresentQpc;
            }
            int ox = G.DisplayBounds.Left, oy = G.DisplayBounds.Top;
            bool touched = false;
            foreach (Surface s in Surfaces)
            {
                if (all) { s.NeedsBackdrop = true; touched = true; continue; }
                RECT mine = s.BackdropRect;
                for (int i = 0; i < rects.Count; i++)
                {
                    RECT r = rects[i];
                    RECT screen = new RECT(r.Left + ox, r.Top + oy, r.Right + ox, r.Bottom + oy);
                    if (mine.Intersects(screen)) { s.NeedsBackdrop = true; touched = true; break; }
                }
            }
            if (touched) FeedTouching++;
            else handedAcquireTicks = 0;
        }

        // ---- surfaces (render thread) ----

        public Surface AddSurface(Geometry geo, IntPtr hwnd, int screenX, int screenY)
        {
            Surface s = new Surface(G, geo);
            s.Hwnd = hwnd;
            s.ScreenX = screenX;
            s.ScreenY = screenY;
            if (hwnd != IntPtr.Zero) s.Chain = G.CreateChain(hwnd, geo.W, geo.H, true);
            Surfaces.Add(s);
            return s;
        }

        public string DebugMessages()
        {
            return "render device:\r\n" + Gfx.DebugMessages(G != null ? G.Device : IntPtr.Zero) + "feed device:\r\n" + Gfx.DebugMessages(feedDevice);
        }

        public void RemoveSurface(Surface s)
        {
            Surfaces.Remove(s);
            if (Animating == s) Animating = null;
            s.Dispose();
        }

        public void Dispose()
        {
            stop = true;
            workEvent.Set();
            if (render != null) render.Join(5000);
        }
    }
}
