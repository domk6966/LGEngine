using System;
using System.Collections.Generic;
#if !LGCLIENT
using System.Windows.Forms;
#endif

namespace LGEngine
{
    // A surface's runtime as the transport sees it.
    internal interface IGlassChannel : IDisposable
    {
        void Stop();   // no more frames, glass off the screen (the surface has failed or is going)
    }

    // A channel whose engine is in another process: its frame count and latency come from the host (a diagnostic round trip on
    // the calling thread, cached for a few ms; the surface's own calls never wait).
    internal interface IGlassStats
    {
        int FramesNow();
        GlassLatencyInfo LatencyNow();
    }

    // How a surface's calls reach the engine that draws it. GlassSurface only ever talks to this. Render thread (the default):
    // the runtimes live on a thread of the engine's own and every call may come from any thread. UI thread: the runtime is a set
    // of objects in this process, run on the caller's UI thread (step 5, kept for comparison). A host transport (phase 2) would
    // carry the same calls over a pipe and shared memory; a GPU backend would be a different runtime behind the same seam.
    internal interface IGlassTransport : IDisposable
    {
        string Name { get; }
        IGlassChannel Open(GlassSpec spec, IGlassEvents events, SurfaceState state);   // may throw (the surface then fails)
        void Wake(IGlassChannel channel);    // any thread: a call has changed the state
        void Close(IGlassChannel channel);   // any thread: the surface is disposed
        bool Post(Action work);              // run work on the engine's thread (now, if already there); false = not possible, the caller runs it
    }

#if !LGCLIENT
    // The default: a GlassRenderThread per service. Wake is lock-free (a flag and an event); Open waits for the thread; Close and
    // Stop are posted to it.
    internal sealed class RenderThreadTransport : IGlassTransport
    {
        readonly GlassRenderThread thread = new GlassRenderThread();
        readonly string name;

        public RenderThreadTransport(string name) { this.name = name; }

        public string Name { get { return name; } }
        public GlassPacingInfo Pacing { get { return thread.Pacing; } }
        public bool Alive { get { return thread.Alive; } }

        public IGlassChannel Open(GlassSpec spec, IGlassEvents events, SurfaceState state)
        {
            return thread.Open(spec, events, state);
        }

        public void Wake(IGlassChannel channel)
        {
            RenderSlot s = channel as RenderSlot;
            if (s != null) s.Wake();
        }

        public void Close(IGlassChannel channel)
        {
            RenderSlot s = channel as RenderSlot;
            if (s != null) thread.CloseSlot(s);
        }

        public bool Post(Action work) { return thread.PostOrRun(work); }

        public void Dispose() { thread.Shutdown(); }
    }

    // The step-5 transport: every frame on the thread that created the surfaces (which must pump messages), and only that thread
    // may call. Kept so the render thread can be compared with it (GlassMode.UiThread).
    internal sealed class InProcessTransport : IGlassTransport
    {
        sealed class Slot
        {
            public SurfaceRuntime Run;
            public int Due;            // TickCount of the next scheduled frame; 0 = none
            public int Pending;        // a frame is already queued by Wake
            public bool Gone;
            public Action Frame;
        }

        readonly List<Slot> slots = new List<Slot>();
        Timer timer;
        bool disposed;

        public string Name { get { return "ui thread"; } }

        public IGlassChannel Open(GlassSpec spec, IGlassEvents events, SurfaceState state)
        {
            Slot slot = new Slot();
            slot.Run = new SurfaceRuntime(spec, events, state, delegate { Wake(slot); });
            slot.Frame = delegate { RunSlot(slot); };
            slot.Run.Tag = slot;
            slots.Add(slot);
            return slot.Run;
        }

        Slot Find(IGlassChannel channel)
        {
            for (int i = 0; i < slots.Count; i++) if (slots[i].Run == channel) return slots[i];
            return null;
        }

        void Wake(Slot slot)
        {
            if (slot.Gone || disposed) return;
            if (System.Threading.Interlocked.Exchange(ref slot.Pending, 1) != 0) return;
            try { slot.Run.Sync.BeginInvoke(slot.Frame); }
            catch (Exception) { slot.Pending = 0; }   // (the window is gone)
        }

        public void Wake(IGlassChannel channel)
        {
            SurfaceRuntime run = channel as SurfaceRuntime;
            Slot s = run == null ? null : run.Tag as Slot;   // (no look at the list: a call from another thread must not read it)
            if (s != null) Wake(s);
        }

        // Events run on the caller's thread (this transport is the UI thread's).
        public bool Post(Action work) { return false; }

        void RunSlot(Slot slot)
        {
            slot.Pending = 0;
            if (slot.Gone || disposed) return;
            slot.Run.RunFrame();
            slot.Due = slot.Run.NextMs < 0 ? 0 : (Environment.TickCount + slot.Run.NextMs) | 1;
            Reschedule();
        }

        void OnTick()
        {
            int now = Environment.TickCount;
            for (int i = 0; i < slots.Count; i++)
            {
                Slot s = slots[i];
                if (s.Gone || s.Due == 0 || unchecked(now - s.Due) < 0) continue;
                s.Due = 0;
                RunSlot(s);
            }
            Reschedule();
        }

        void Reschedule()
        {
            int now = Environment.TickCount, best = int.MaxValue;
            for (int i = 0; i < slots.Count; i++)
            {
                Slot s = slots[i];
                if (s.Gone || s.Due == 0) continue;
                int left = unchecked(s.Due - now);
                if (left < best) best = left;
            }
            if (best == int.MaxValue)
            {
                if (timer != null) timer.Enabled = false;
                return;
            }
            if (timer == null)
            {
                timer = new Timer();
                timer.Tick += delegate { OnTick(); };
            }
            best = Math.Max(1, best);
            if (timer.Interval != best) timer.Interval = best;
            if (!timer.Enabled) timer.Enabled = true;
        }

        public void Close(IGlassChannel channel)
        {
            Slot s = Find(channel);
            if (s == null) return;
            s.Gone = true;
            slots.Remove(s);
            s.Run.Dispose();
            Reschedule();
        }

        public void Dispose()
        {
            if (disposed) return;
            Slot[] all = slots.ToArray();
            foreach (Slot s in all) Close(s.Run);
            disposed = true;
            if (timer != null)
            {
                timer.Enabled = false;
                timer.Dispose();
                timer = null;
            }
        }
    }
#endif
}
