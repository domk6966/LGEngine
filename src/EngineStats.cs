using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LGEngine
{
    // Cheap, lock-free counters of what the engine is doing, for a status page, a tray tooltip or the log. The glass
    // code (src\Glass.cs) feeds them from its own (UI) thread; anyone may read them from any thread with Read().
    //
    //   EngineStats.Snapshot s = EngineStats.Read();   // at most about once a second is plenty
    //   s.ActiveSurfaces, s.FramesPerSecond, s.AvgFrameMs, s.MaxFrameMs, s.BackdropUpdatesPerSecond, s.BackdropSource,
    //   s.Efficiency, s.EngineBusyMsPerSecond, s.ThreadCpuMsPerSecond; s.ToString() is one log-style line.
    //
    // The rates cover the last whole second the engine did anything in (they read 0 once it has been idle for two). The
    // plain counters (Frames, FullShades, ...) only ever grow. Writers use no locks: the hot-path calls cost a handful of
    // additions and one clock read. The totals are plain long fields written by one thread at a time; a reader on another
    // thread may see a value a moment old, which is fine for statistics.
    public static class EngineStats
    {
        // Where the last backdrop update came from.
        public const int SourceNone = 0, SourceLive = 1, SourceAlarm = 2, SourceRead = 3;
        public static readonly string[] SourceNames = { "none", "live feed", "alarm look", "screen read" };

        // Efficiency mode: 0 normal, 1 on battery with "save power" (fewer frames, longer gaps), 2 battery saver on (the
        // glass keeps what it has).
        public const int EffNormal = 0, EffBattery = 1, EffSaver = 2;
        public static readonly string[] EfficiencyNames = { "normal", "battery", "saver" };
        public static volatile int Efficiency;

        // Totals since the program started.
        public static long Frames;              // glass frames drawn (any surface)
        public static long FullShades;          // whole backdrop shaded again (or the glass primed afresh)
        public static long IncrementalShades;   // only the part of the backdrop that changed shaded again
        public static long ContentShades;       // only contents (rows, text, slider) drawn again
        public static long RectPresents;        // handed to Windows as a dirty rectangle only
        public static long FullPresents;        // handed to Windows whole
        public static long BackdropUpdates;     // new pictures of what is behind the glass taken in
        public static long PromptPeeks;         // looks behind the glass taken at once because something there changed
        public static long SelfChangesIgnored;  // screen changes the glass caused itself, and so did not look at

        static int surfaces;
        // How many glass surfaces are up (notifications, indicators, taskbar, search box ...) right now.
        public static int ActiveSurfaces { get { return Thread.VolatileRead(ref surfaces); } }
        public static void SurfaceUp() { Interlocked.Increment(ref surfaces); }
        public static void SurfaceDown() { if (Interlocked.Decrement(ref surfaces) < 0) Interlocked.Exchange(ref surfaces, 0); }

        // ---- the window (about a second) being filled by the hot path ----
        static long winStart;                  // (Stopwatch ticks)
        static int winFrames, winBackdrop, winSource;
        static long winFrameTicks, winMaxTicks, winBusyTicks;
        static long winThreadCpu;              // (100 ns units) of the roller's thread at the window's start
        static int winThread;
        static volatile Snapshot last = new Snapshot();

        public sealed class Snapshot
        {
            public int ActiveSurfaces;
            public int FramesPerSecond;
            public double AvgFrameMs, MaxFrameMs;
            public int BackdropUpdatesPerSecond;
            public int BackdropSource;
            public int Efficiency;
            public double EngineBusyMsPerSecond;   // time spent inside the engine's frame/tick code on its thread (wall clock)
            public double ThreadCpuMsPerSecond;    // CPU time its thread used (everything it ran, so this is the ceiling)
            public long AtTick;                    // Environment.TickCount when the window closed

            public string BackdropSourceName { get { return SourceNames[Math.Max(0, Math.Min(SourceNames.Length - 1, BackdropSource))]; } }
            public string EfficiencyName { get { return EfficiencyNames[Math.Max(0, Math.Min(EfficiencyNames.Length - 1, Efficiency))]; } }

            public override string ToString()
            {
                return string.Format("engine: {0} surfaces; {1} fps, {2:0.0} ms avg / {3:0.0} max; backdrop {4}/s ({5}); {6} mode; busy {7:0} ms/s, thread cpu {8:0} ms/s",
                    ActiveSurfaces, FramesPerSecond, AvgFrameMs, MaxFrameMs, BackdropUpdatesPerSecond, BackdropSourceName, EfficiencyName, EngineBusyMsPerSecond, ThreadCpuMsPerSecond);
            }
        }

        // The numbers for the last second. Cheap; may be called from any thread.
        public static Snapshot Read()
        {
            Snapshot s = last;
            if (unchecked(Environment.TickCount - (int)s.AtTick) > 2000)
            {
                // (idle for a while: nothing has rolled the window on)
                Snapshot idle = new Snapshot();
                idle.BackdropSource = s.BackdropSource;
                idle.AtTick = s.AtTick;
                s = idle;
            }
            Snapshot r = new Snapshot();
            r.ActiveSurfaces = ActiveSurfaces;
            r.FramesPerSecond = s.FramesPerSecond;
            r.AvgFrameMs = s.AvgFrameMs;
            r.MaxFrameMs = s.MaxFrameMs;
            r.BackdropUpdatesPerSecond = s.BackdropUpdatesPerSecond;
            r.BackdropSource = s.BackdropSource;
            r.Efficiency = Efficiency;
            r.EngineBusyMsPerSecond = s.EngineBusyMsPerSecond;
            r.ThreadCpuMsPerSecond = s.ThreadCpuMsPerSecond;
            r.AtTick = s.AtTick;
            return r;
        }

        // ---- hot path (engine thread) ----

        // One glass frame drawn, ticks (Stopwatch) long.
        public static void FrameDone(long ticks)
        {
            Frames++;
            winFrames++;
            winFrameTicks += ticks;
            if (ticks > winMaxTicks) winMaxTicks = ticks;
            Roll();
        }

        // Time spent in one pass of the engine's timer code (frame or not), for the busy figure.
        public static void Busy(long ticks)
        {
            winBusyTicks += ticks;
            Roll();
        }

        // A new picture of what is behind a glass came in, from source (a Source... constant).
        public static void BackdropUpdate(int source)
        {
            BackdropUpdates++;
            winBackdrop++;
            winSource = source;
        }

        static void Roll()
        {
            long now = Stopwatch.GetTimestamp();
            if (winStart == 0) { winStart = now; winThread = Thread.CurrentThread.ManagedThreadId; winThreadCpu = ThreadCpu(); return; }
            long span = now - winStart;
            if (span < Stopwatch.Frequency) return;
            double toMs = 1000.0 / Stopwatch.Frequency, perSecond = (double)Stopwatch.Frequency / span;
            Snapshot s = new Snapshot();
            s.FramesPerSecond = (int)Math.Round(winFrames * perSecond);
            s.AvgFrameMs = winFrames > 0 ? winFrameTicks * toMs / winFrames : 0;
            s.MaxFrameMs = winMaxTicks * toMs;
            s.BackdropUpdatesPerSecond = (int)Math.Round(winBackdrop * perSecond);
            s.BackdropSource = winBackdrop > 0 ? winSource : last.BackdropSource;
            s.EngineBusyMsPerSecond = winBusyTicks * toMs * perSecond;
            int thread = Thread.CurrentThread.ManagedThreadId;
            long cpu = ThreadCpu();
            s.ThreadCpuMsPerSecond = thread == winThread && cpu > 0 && winThreadCpu > 0 ? (cpu - winThreadCpu) / 10000.0 * perSecond : 0;
            s.AtTick = Environment.TickCount;
            last = s;
            winStart = now;
            winFrames = winBackdrop = 0;
            winFrameTicks = winMaxTicks = winBusyTicks = 0;
            winThread = thread;
            winThreadCpu = cpu;
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")]
        static extern bool GetThreadTimes(IntPtr thread, out long created, out long exited, out long kernel, out long user);

        static long ThreadCpu()
        {
            long c, e, k, u;
            return GetThreadTimes(GetCurrentThread(), out c, out e, out k, out u) ? k + u : 0;
        }
    }
}
