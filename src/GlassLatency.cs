using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LGEngine
{
    // How long a surface's calls take to reach the screen (SERVICE.md section 7). Three readings per call:
    //   queue:   from the call to the frame that handles it starting (the render thread's queueing delay);
    //   present: from the call to Windows having been handed the picture (what the engine controls);
    //   vblank:  from the call to the next display refresh after that (an estimate of when it is lit; DWM's timing info).
    // Only the newest 256 of each are kept. (Queue is read at every frame that finds a new call; present and vblank only for
    // pictures put on screen by a call.)
    public struct GlassLatencyInfo
    {
        public double QueueP50, QueueP99, PresentP50, PresentP99, VblankP50, VblankP99;
        public int QueueSamples, PresentSamples, VblankSamples;

        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "queue p50 {0:0.0} ms p99 {1:0.0} ms, present p50 {2:0.0} ms p99 {3:0.0} ms, vblank p50 {4:0.0} ms p99 {5:0.0} ms, samples {6}/{7}/{8}",
                QueueP50, QueueP99, PresentP50, PresentP99, VblankP50, VblankP99, QueueSamples, PresentSamples, VblankSamples);
        }
    }

    // The display's refresh from DWM (DwmGetCompositionTimingInfo): the moment of a vblank and the period, in Stopwatch ticks
    // (the QPC clock). Any thread.
    internal static class GlassVblank
    {
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct Ratio { public uint Num, Den; }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct TimingInfo
        {
            public uint cbSize;
            public Ratio rateRefresh;
            public ulong qpcRefreshPeriod;
            public Ratio rateCompose;
            public ulong qpcVBlank;
            public ulong cRefresh;
            public uint cDXRefresh;
            public ulong qpcCompose;
            public ulong cFrame;
            public uint cDXPresent;
            public ulong cRefreshFrame;
            public ulong cFrameSubmitted;
            public uint cDXPresentSubmitted;
            public ulong cFrameConfirmed;
            public uint cDXPresentConfirmed;
            public ulong cRefreshConfirmed;
            public uint cDXRefreshConfirmed;
            public ulong cFramesLate;
            public uint cFramesOutstanding;
            public ulong cFrameDisplayed;
            public ulong qpcFrameDisplayed;
            public ulong cRefreshFrameDisplayed;
            public ulong cFrameComplete;
            public ulong qpcFrameComplete;
            public ulong cFramePending;
            public ulong qpcFramePending;
            public ulong cFramesDisplayed;
            public ulong cFramesComplete;
            public ulong cFramesPending;
            public ulong cFramesAvailable;
            public ulong cFramesDropped;
            public ulong cFramesMissed;
            public ulong cRefreshNextDisplayed;
            public ulong cRefreshNextPresented;
            public ulong cRefreshesDisplayed;
            public ulong cRefreshesPresented;
            public ulong cRefreshStarted;
            public ulong cPixelsReceived;
            public ulong cPixelsDrawn;
            public ulong cBuffersEmpty;
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref TimingInfo info);

        static long brokenAt;   // Stopwatch ticks of the last DWM failure (0 = none); asked again after about a second

        public static int StructSize { get { return Marshal.SizeOf(typeof(TimingInfo)); } }

        // False if DWM would not say (or the period is nonsense).
        public static bool Read(out long vblank, out long period)
        {
            vblank = 0;
            period = 0;
            long bAt = Interlocked.Read(ref brokenAt);
            if (bAt != 0 && Stopwatch.GetTimestamp() - bAt < Stopwatch.Frequency) return false;
            try
            {
                TimingInfo t = new TimingInfo();
                t.cbSize = (uint)Marshal.SizeOf(typeof(TimingInfo));
                if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref t) != 0) { Interlocked.Exchange(ref brokenAt, Stopwatch.GetTimestamp() | 1); return false; }
                period = (long)t.qpcRefreshPeriod;
                vblank = (long)t.qpcVBlank;
                return period > 0 && period <= Stopwatch.Frequency / 10 && vblank > 0;
            }
            catch (Exception)
            {
                Interlocked.Exchange(ref brokenAt, Stopwatch.GetTimestamp() | 1);
                return false;
            }
        }

        // The display's refresh period in ms (16.7 if DWM would not say).
        public static double PeriodMs()
        {
            long vb, period;
            if (!Read(out vb, out period)) return 1000.0 / 60;
            return period * 1000.0 / Stopwatch.Frequency;
        }

        // The first vblank strictly after the moment (ticks), from one reading.
        public static long NextAfter(long at, long vblank, long period)
        {
            long k = at >= vblank ? (at - vblank) / period + 1 : 0;
            return vblank + k * period;
        }
    }

    internal sealed class GlassLatency
    {
        const int Ring = 256;

        readonly object gate = new object();
        readonly double[] queue = new double[Ring], present = new double[Ring], vblank = new double[Ring];
        int queueN, presentN, vblankN;   // samples ever taken (the ring holds the last Ring)
        readonly object snapGate = new object();
        readonly double[] sq = new double[Ring], sa = new double[Ring], sb = new double[Ring];   // Snapshot's scratch (no allocation)

        // callStamp, frameStart: Stopwatch ticks.
        public void RecordQueue(long callStamp, long frameStart)
        {
            double q = Math.Max(0, (frameStart - callStamp) * 1000.0 / Stopwatch.Frequency);
            lock (gate)
            {
                queue[queueN % Ring] = q;
                queueN++;
            }
        }

        // callStamp, presentedAt: Stopwatch ticks.
        public void Record(long callStamp, long presentedAt)
        {
            double toMs = 1000.0 / Stopwatch.Frequency;
            double p = Math.Max(0, (presentedAt - callStamp) * toMs);
            double v = -1;
            long vb, period;
            if (GlassVblank.Read(out vb, out period))
            {
                long next = GlassVblank.NextAfter(presentedAt, vb, period);
                v = Math.Max(0, (next - callStamp) * toMs);
            }
            lock (gate)
            {
                present[presentN % Ring] = p;
                presentN++;
                if (v >= 0)
                {
                    vblank[vblankN % Ring] = v;
                    vblankN++;
                }
            }
        }

        // Puts the summary in EngineStats (the render thread calls it, at most every 250 ms; no allocation).
        public void Publish()
        {
#if !LGCLIENT
            GlassLatencyInfo l = Snapshot();
            EngineStats.LatencyQueueP50 = l.QueueP50;
            EngineStats.LatencyQueueP99 = l.QueueP99;
            EngineStats.LatencyPresentP50 = l.PresentP50;
            EngineStats.LatencyPresentP99 = l.PresentP99;
            EngineStats.LatencyVblankP50 = l.VblankP50;
            EngineStats.LatencyVblankP99 = l.VblankP99;
#endif
        }

        public GlassLatencyInfo Snapshot()
        {
            GlassLatencyInfo r = new GlassLatencyInfo();
            lock (snapGate)
            {
                int qn, an, bn;
                lock (gate)
                {
                    qn = Copy(queue, queueN, sq);
                    an = Copy(present, presentN, sa);
                    bn = Copy(vblank, vblankN, sb);
                }
                r.QueueSamples = qn;
                r.PresentSamples = an;
                r.VblankSamples = bn;
                r.QueueP50 = Percentile(sq, qn, 0.5);
                r.QueueP99 = Percentile(sq, qn, 0.99);
                r.PresentP50 = Percentile(sa, an, 0.5);
                r.PresentP99 = Percentile(sa, an, 0.99);
                r.VblankP50 = Percentile(sb, bn, 0.5);
                r.VblankP99 = Percentile(sb, bn, 0.99);
            }
            return r;
        }

        static int Copy(double[] ring, int n, double[] to)
        {
            int c = Math.Min(n, Ring);
            Array.Copy(ring, to, c);
            return c;
        }
        // Nearest rank on a sorted copy.
        static double Percentile(double[] s, int n, double q)
        {
            if (n == 0) return 0;
            Array.Sort(s, 0, n);
            int rank = (int)Math.Ceiling(q * n);
            return s[Math.Max(1, Math.Min(n, rank)) - 1];
        }
    }
}
