using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace LGEngine
{
    // The client side of the host protocol (SERVICE.md sections 3, 6 and design 7): the same IGlassTransport as the in-process ones,
    // but the engine is a GlassHostServer in another process (or, in the loopback test, this one). Nothing a surface calls waits for
    // the host: a call writes the surface's state block / contents in the shared section (a seqlock and a double buffer) and sets
    // the shared HostWake event; the rare messages (Create, Resize, SetLook, Destroy) are queued to a write chain that has no thread.
    //
    // One I/O thread per service reads the pipe (blocking) and, when the pipe breaks, reconnects with backoff 100 ms, 400 ms, 1 s,
    // then every 2 s. A service whose first connect fails stays offline: Open throws "host unavailable" (the surface fails, nothing
    // else) and no thread runs. After a break the surfaces are Lost (they keep their spec, state and contents); when the host is
    // back they are created again and republished, and ServiceRestarted is raised.
    internal sealed class PipeTransport : IGlassTransport
    {
        static readonly int[] Backoff = new int[] { 100, 400, 1000, 2000 };
        const int PendingLimit = 256;

        sealed class StatsWait
        {
            public bool Done, Ok;
            public int Frames;
            public GlassLatencyInfo Info;
        }

        readonly string pipeName, wakeName;
        readonly object gate = new object();   // never held while taking a channel's gate
        readonly Dictionary<uint, PipeChannel> byCookie = new Dictionary<uint, PipeChannel>();
        readonly Dictionary<uint, PipeChannel> bySurface = new Dictionary<uint, PipeChannel>();
        readonly Dictionary<uint, PipeChannel> pending = new Dictionary<uint, PipeChannel>();   // request seq -> the channel an Error reply is for
        readonly Queue<uint> pendingOrder = new Queue<uint>();
        readonly Dictionary<uint, StatsWait> statsWaits = new Dictionary<uint, StatsWait>();
        readonly Thread io;
        readonly ManualResetEvent stopEvt = new ManualResetEvent(false);
        readonly ManualResetEvent firstDone = new ManualResetEvent(false);
        NamedPipeClientStream pipe;
        FrameQueue writer;
        EventWaitHandle hostWake;
        uint nextSeq, nextCookie;
        volatile bool disposed, connected, everConnected;
        bool helloSeen;   // (the I/O thread's)
        volatile int hostMinor = -1;   // the host's protocol minor: -1 until the HelloAck, then its trailing u16 (0 when it had none: a v1 host)
        volatile bool busyEver;   // (sticky: the constructor's wait)
        volatile bool busySeen;   // the host answered the Hello with Error "busy" (every slot taken): retry soon, never offline for good
        bool lostRaised;   // (the I/O thread's) Lost was raised for this connection
        bool restartPending;   // (the I/O thread's) reconnected; Restarted waits for the surfaces
        // The I/O thread owns the handles it uses: whoever disposes releases them only once it has ended (or lets it do that itself).
        readonly object relGate = new object();
        bool ioDone, releaseWanted, released;
        int disposeCalled;
        // the SetLook rate (client side, under the host's limit): a look over it waits for the bucket (the channel's retry timer)
        readonly object bucketGate = new object();
        double tokens = 200;
        long tokenAt = Stopwatch.GetTimestamp();

        public Action Lost, Restarted;   // (set by the service before Start; run on the I/O thread; the service posts them on its context)

        readonly Func<bool> starter;   // start-on-demand: starts the host process (true = started, so the pipe is worth waiting for); null = never

        public PipeTransport(string suffix) : this(suffix, null) { }

        public PipeTransport(string suffix, Func<bool> starter)
        {
            pipeName = GlassNames.PipeShort(suffix);
            wakeName = GlassNames.HostWake(suffix);
            this.starter = starter;
            io = new Thread(IoMain);
            io.IsBackground = true;
            io.Name = "LGEngine client pipe";
        }

        // Starts the I/O thread and waits for the first connect. The service sets Lost and Restarted before calling this, so that no
        // drop can be missed.
        public void Start()
        {
            io.Start();
            if (!firstDone.WaitOne(starter != null ? 4500 : 1500))   // (a host that accepts and never answers: offline; one that says busy gets a few seconds more)
            {
                if (!busyEver || !firstDone.WaitOne(3500)) ShutdownIo();
            }
        }

        public string Name { get { return "host pipe"; } }
        public bool Connected { get { return connected; } }
        public bool EverConnected { get { return everConnected; } }
        internal int HostMinor { get { return hostMinor; } }

        // A client that dies without a word (test): the pipe just closes and the I/O thread ends; nothing is told to the host, the
        // sections are let go. A Dispose afterwards only cleans up.
        internal void Abandon()
        {
            ShutdownIo();
            PipeChannel[] all;
            lock (gate)
            {
                all = new PipeChannel[byCookie.Count];
                byCookie.Values.CopyTo(all, 0);
            }
            foreach (PipeChannel c in all) c.OnLost();
        }

        // Start-on-demand is capped: after 3 failed starts in a row the next start waits 4 s, then 8, 16 ... at most 60 s, plus 0-25%
        // random jitter; a connect resets it. (I/O thread only.)
        int startFails;
        long nextStartAt;
        static readonly Random jitter = new Random();

        // The wait after `fails` failed starts in a row (0 while fewer than 3; never more than 60 s, jitter included).
        internal static int StartDelayMs(int fails, Random rng)
        {
            if (fails < 3) return 0;
            int b = Math.Min(60000, 4000 << Math.Min(fails - 3, 4));
            double j;
            lock (rng) j = rng.NextDouble() * 0.25;
            return Math.Min(60000, (int)(b + b * j));
        }

        void NoteStartFailed()
        {
            startFails = Math.Min(startFails + 1, 1000);
            if (startFails >= 3) nextStartAt = Stopwatch.GetTimestamp() + StartDelayMs(startFails, jitter) * (Stopwatch.Frequency / 1000);
        }

        // Tries the pipe; if nobody listens and a starter is set (and the backoff allows), starts the host and waits up to 2 s for the
        // pipe (less when budgetMs >= 0 is smaller). retry: a host was started but the pipe did not appear.
        NamedPipeClientStream TryConnectStarting(int timeoutMs, int budgetMs, out bool retry)
        {
            retry = false;
            NamedPipeClientStream p = TryConnect(timeoutMs);
            if (p != null) { startFails = 0; return p; }
            if (starter == null || disposed) return null;
            if (startFails >= 3 && Stopwatch.GetTimestamp() < nextStartAt) return null;
            bool started;
            try { started = starter(); }
            catch (Exception) { started = false; }
            if (!started)
            {
                NoteStartFailed();
                return null;
            }
            p = TryConnect(budgetMs < 0 ? 2000 : Math.Min(2000, Math.Max(0, budgetMs)));
            if (p != null) { startFails = 0; return p; }
            NoteStartFailed();
            retry = true;
            return null;
        }

        // ---- the I/O thread ----

        void IoMain()
        {
            Interlocked.Increment(ref GlassPipeStats.threads);
            try
            {
                bool first = true;
                int attempt = 0, busyTries = 0;
                bool busyWait = false;   // (the host was full: a short wait, then try again, even on the first connect)
                bool retryFirst = false;   // (the first connect found no pipe after a start, or the pipe closed before the HelloAck: again until the deadline)
                int firstMs = starter != null ? 4000 : 1300;   // (the first connect's deadline; the constructor gives up a little later)
                Stopwatch life = Stopwatch.StartNew();
                while (!disposed)
                {
                    if (busyWait)
                    {
                        busyWait = false;
                        if (stopEvt.WaitOne(Math.Min(250, 50 << Math.Min(busyTries++, 3)))) break;
                    }
                    else if (retryFirst)
                    {
                        retryFirst = false;
                        if (stopEvt.WaitOne(100)) break;
                    }
                    else if (!first)
                    {
                        int ms = Backoff[Math.Min(attempt, Backoff.Length - 1)];
                        attempt++;
                        if (stopEvt.WaitOne(ms)) break;
                    }
                    bool retry;
                    NamedPipeClientStream p = TryConnectStarting(first ? 100 : 300, first ? (int)(firstMs - life.ElapsedMilliseconds) : -1, out retry);
                    if (p == null)
                    {
                        if (first && !busyEver)
                        {
                            // offline from the start: nothing to retry (a host that said busy is retried), unless a host was started
                            // and its pipe is not up yet
                            if (!retry || life.ElapsedMilliseconds >= firstMs) return;
                            retryFirst = true;
                        }
                        continue;
                    }
                    busySeen = false;
                    bool hello;
                    try { hello = RunConnection(p, first); }
                    catch (Exception)   // (nothing ends this thread but a Dispose: a fault is a lost connection, then the normal backoff)
                    {
                        PipeNative.Shut(p);
                        hello = helloSeen;
                        if (hello && !disposed && !lostRaised)
                        {
                            try { ConnectionLost(); }
                            catch (Exception) { }
                        }
                    }
                    if (hello) { attempt = 0; busyTries = 0; startFails = 0; }
                    if (!hello && busySeen && !disposed)
                    {
                        busyWait = true;   // (the host is full: not offline, not the long backoff)
                        continue;
                    }
                    if (first && !hello && !busyEver)
                    {
                        // the pipe closed before the HelloAck (a host on its way out): connect again, starting one if need be
                        if (disposed || life.ElapsedMilliseconds >= firstMs) return;
                        retryFirst = true;
                        continue;
                    }
                    first = false;
                }
            }
            catch (Exception) { }   // (nothing escapes a background thread)
            finally
            {
                try { firstDone.Set(); }
                catch (Exception) { }
                Interlocked.Decrement(ref GlassPipeStats.threads);
                lock (relGate)
                {
                    ioDone = true;
                    if (releaseWanted) Release();
                }
            }
        }

        // The SetLook bucket: 100 a second, a burst of 200 (the host allows 200 and 400 for everything).
        internal bool TakeToken()
        {
            lock (bucketGate)
            {
                long now = Stopwatch.GetTimestamp();
                tokens = Math.Min(200, tokens + (now - tokenAt) * 100.0 / Stopwatch.Frequency);
                tokenAt = now;
                if (tokens < 1) return false;
                tokens -= 1;
                return true;
            }
        }

        NamedPipeClientStream TryConnect(int timeoutMs) { return ConnectLoop(pipeName, timeoutMs, stopEvt); }

        // Connects within timeoutMs. NamedPipeClientStream.Connect(timeout) of .NET 4 spins on a CPU while the pipe does not exist, so
        // each try is Connect(0) and the wait between tries is a 50 ms sleep (cut short by `stop`). null = no pipe (or not ours).
        internal static NamedPipeClientStream ConnectLoop(string name, int timeoutMs, WaitHandle stop)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                NamedPipeClientStream p = ConnectOnce(name);
                if (p != null) return p;
                long left = timeoutMs - sw.ElapsedMilliseconds;
                if (left <= 0) return null;
                int w = (int)Math.Min(50, left);
                if (stop != null) { if (stop.WaitOne(w)) return null; }
                else Thread.Sleep(w);
            }
        }

        static NamedPipeClientStream ConnectOnce(string pipeName)
        {
            NamedPipeClientStream p = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            try
            {
                p.Connect(0);
                p.ReadMode = PipeTransmissionMode.Message;
                uint pid;
                if (!PipeNative.PeerInMySession(p, true, out pid)) throw new IOException("the pipe's server is not in this session");
                if (!PipeNative.PeerOwnedByMe(p, true)) throw new IOException("the pipe's server is not run by this user");
                return p;
            }
            catch (Exception)
            {
                PipeNative.Shut(p);
                return null;
            }
        }

        // Reads until the pipe breaks. True if the host answered the Hello.
        bool RunConnection(NamedPipeClientStream p, bool first)
        {
            FrameQueue w = new FrameQueue(p, delegate { PipeNative.Shut(p); });
            lock (gate)
            {
                pipe = p;
                writer = w;
            }
            helloSeen = false;
            lostRaised = false;
            hostMinor = -1;
            try
            {
                PayloadWriter hw = new PayloadWriter();
                hw.U16(1);
                hw.U16(1);
                hw.Str("LGEngine client " + Process.GetCurrentProcess().Id);
                hw.U16(GlassFrame.Minor);   // (protocol 1.1: our minor; a v1 host ignores the trailing bytes)
                uint s;
                Send(Msg.Hello, hw.ToArray(), null, out s);
                byte[] buf = new byte[GlassFrame.MaxFrame];
                while (!disposed)
                {
                    int n;
                    try { n = p.Read(buf, 0, buf.Length); }
                    catch (Exception) { break; }
                    if (n <= 0) break;
                    bool complete;
                    try { complete = p.IsMessageComplete; }
                    catch (Exception) { break; }
                    if (!complete) break;   // (the host never sends a frame this large)
                    try { Handle(buf, n, first); }
                    catch (Exception) { }
                }
            }
            finally
            {
                lock (gate)
                {
                    pipe = null;
                    writer = null;
                    connected = false;
                }
                PipeNative.Shut(p);
                if (helloSeen && !disposed) ConnectionLost();
            }
            return helloSeen;
        }

        // After a reconnect: Restarted is raised once every surface has been created again (acked, its state and contents written) or
        // is gone (closed, failed). I/O thread.
        void CheckRestarted()
        {
            if (!restartPending) return;
            PipeChannel[] all;
            lock (gate)
            {
                all = new PipeChannel[byCookie.Count];
                byCookie.Values.CopyTo(all, 0);
            }
            foreach (PipeChannel c in all) if (!c.Settled) return;
            restartPending = false;
            Action a = Restarted;
            if (a != null) { try { a(); } catch (Exception) { } }
        }

        void ConnectionLost()
        {
            restartPending = false;
            hostMinor = -1;   // (unknown again until the next HelloAck)
            PipeChannel[] all;
            StatsWait[] waits;
            EventWaitHandle oldWake;
            lock (gate)
            {
                oldWake = hostWake;   // (the host's event is gone with the host: not held, or its name could not be made again)
                hostWake = null;
                all = new PipeChannel[byCookie.Count];
                byCookie.Values.CopyTo(all, 0);
                bySurface.Clear();
                pending.Clear();
                pendingOrder.Clear();
                waits = new StatsWait[statsWaits.Count];
                statsWaits.Values.CopyTo(waits, 0);
                statsWaits.Clear();
            }
            if (oldWake != null) { try { oldWake.Dispose(); } catch (Exception) { } }
            foreach (PipeChannel c in all) c.OnLost();
            foreach (StatsWait w in waits) lock (w) { w.Done = true; Monitor.PulseAll(w); }
            lostRaised = true;
            Action a = Lost;
            if (a != null) { try { a(); } catch (Exception) { } }
        }

        void Handle(byte[] buf, int n, bool first)
        {
            int type, len;
            uint seq;
            if (GlassFrame.Check(buf, n, out type, out seq, out len) != 0) return;
            PayloadReader r = new PayloadReader(buf, GlassFrame.HeaderSize, len);
            switch (type)
            {
                case Msg.HelloAck:
                    {
                        int chosen = r.U16();
                        if (r.Bad || chosen != GlassFrame.Version) { PipeNative.Shut(pipe); return; }
                        r.Str(256);   // (the host's name; then, from a 1.1 host, its minor)
                        int minor = !r.Bad && r.Remaining >= 2 ? r.U16() : 0;
                        hostMinor = Math.Max(0, minor);
                        EventWaitHandle ev;
                        try { ev = EventWaitHandle.OpenExisting(wakeName, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize); }
                        catch (Exception) { PipeNative.Shut(pipe); return; }
                        EventWaitHandle old;
                        PipeChannel[] all;
                        lock (gate)
                        {
                            old = hostWake;
                            hostWake = ev;
                            connected = true;
                            all = new PipeChannel[byCookie.Count];
                            byCookie.Values.CopyTo(all, 0);
                        }
                        if (old != null) { try { old.Dispose(); } catch (Exception) { } }
                        bool restart = everConnected;
                        everConnected = true;
                        helloSeen = true;
                        try { firstDone.Set(); }
                        catch (Exception) { }
                        if (restart) restartPending = true;   // (raised once every surface is back: see CheckRestarted)
                        foreach (PipeChannel c in all) Establish(c);
                        CheckRestarted();
                        break;
                    }
                case Msg.CreateAck:
                    {
                        uint cookie = r.U32(), id = r.U32();
                        string name = r.Str(256);
                        int bytes = r.I32(), w = r.I32(), h = r.I32();
                        if (r.Bad) return;
                        PipeChannel ch;
                        lock (gate)
                        {
                            byCookie.TryGetValue(cookie, out ch);
                            if (ch != null) bySurface[id] = ch;
                        }
                        if (ch == null || ch.OnAck(id, name, bytes, w, h))
                        {
                            lock (gate) bySurface.Remove(id);
                            SendDestroy(id);
                        }
                        CheckRestarted();
                        break;
                    }
                case Msg.Error:
                    {
                        int code = r.I32();
                        string text = r.Str(1024);
                        if (!helloSeen && !r.Bad && text == "busy") { busySeen = true; busyEver = true; break; }   // (the answer to the Hello when the host has no slot free)
                        PipeChannel ch;
                        StatsWait sw;
                        lock (gate)
                        {
                            pending.TryGetValue(seq, out ch);
                            pending.Remove(seq);
                            if (statsWaits.TryGetValue(seq, out sw)) statsWaits.Remove(seq);
                        }
                        if (sw != null) lock (sw) { sw.Done = true; Monitor.PulseAll(sw); }
                        if (ch != null) ch.RequestFailed(code, "host: " + text);
                        CheckRestarted();
                        break;
                    }
                case Msg.StatsReply:
                    {
                        r.U32();
                        int frames = r.I32();
                        GlassLatencyInfo i = new GlassLatencyInfo();
                        i.QueueP50 = r.F64();
                        i.QueueP99 = r.F64();
                        i.PresentP50 = r.F64();
                        i.PresentP99 = r.F64();
                        i.VblankP50 = r.F64();
                        i.VblankP99 = r.F64();
                        i.QueueSamples = r.I32();
                        i.PresentSamples = r.I32();
                        i.VblankSamples = r.I32();
                        StatsWait sw;
                        lock (gate)
                        {
                            if (statsWaits.TryGetValue(seq, out sw)) statsWaits.Remove(seq);
                        }
                        if (sw != null && !r.Bad) lock (sw) { sw.Frames = frames; sw.Info = i; sw.Ok = true; sw.Done = true; Monitor.PulseAll(sw); }
                        break;
                    }
                case Msg.Shown:
                case Msg.Hidden:
                case Msg.Appearance:
                case Msg.Failed:
                    {
                        uint id = r.U32();
                        PipeChannel ch;
                        lock (gate) bySurface.TryGetValue(id, out ch);
                        if (ch == null) return;
                        if (type == Msg.Shown) ch.Events.Shown();
                        else if (type == Msg.Hidden) ch.Events.Hidden();
                        else if (type == Msg.Appearance) { int light = r.U8(); if (!r.Bad) ch.Events.AppearanceChanged(light != 0); }
                        else
                        {
                            int code = r.I32();
                            string text = r.Str(1024);
                            if (!r.Bad) ch.Events.Failed(code, text);
                        }
                        break;
                    }
                case Msg.LightMoved:   // (protocol 1.1, only from a host that said it speaks it)
                    {
                        uint id = r.U32();
                        float light = r.F32();
                        PipeChannel ch;
                        lock (gate) bySurface.TryGetValue(id, out ch);
                        IGlassLightEvents sink = ch == null ? null : ch.Events as IGlassLightEvents;
                        if (sink == null || r.Bad || float.IsNaN(light) || float.IsInfinity(light)) return;
                        sink.LightMoved(Math.Max(0f, Math.Min(1f, light)));
                        break;
                    }
            }
        }

        // ---- sending ----

        // Queues a frame (false: not connected or the write chain is dead). pendingFor: the channel an Error reply to it concerns.
        public bool Send(int type, byte[] payload, PipeChannel pendingFor, out uint seqOut)
        {
            FrameQueue w;
            uint s;
            lock (gate)
            {
                w = writer;
                s = ++nextSeq;
                if (pendingFor != null && w != null)
                {
                    pending[s] = pendingFor;
                    pendingOrder.Enqueue(s);
                    while (pendingOrder.Count > PendingLimit) pending.Remove(pendingOrder.Dequeue());
                }
            }
            seqOut = s;
            return w != null && w.Send(GlassFrame.Build(type, s, payload));
        }

        void SendDestroy(uint id)
        {
            PayloadWriter w = new PayloadWriter();
            w.U32(id);
            uint s;
            Send(Msg.Destroy, w.ToArray(), null, out s);
        }

        public void SignalHost()
        {
            EventWaitHandle e = hostWake;
            if (e == null) return;
            try { e.Set(); }
            catch (Exception) { }
        }

        void Establish(PipeChannel ch)
        {
            byte[] frame = ch.BuildCreate();
            if (frame == null) return;
            uint s;
            Send(Msg.Create, frame, ch, out s);
        }

        // A diagnostic round trip (the caller waits, up to 2 s; nothing else does).
        public bool AskStats(uint surface, out int frames, out GlassLatencyInfo info)
        {
            frames = 0;
            info = new GlassLatencyInfo();
            if (Thread.CurrentThread == io) return false;   // (only this thread reads the reply: the caller gets the last known figures)
            StatsWait w = new StatsWait();
            PayloadWriter pw = new PayloadWriter();
            pw.U32(surface);
            FrameQueue q;
            uint s;
            lock (gate)
            {
                q = writer;
                s = ++nextSeq;
                if (q != null) statsWaits[s] = w;
            }
            if (q == null || !q.Send(GlassFrame.Build(Msg.Stats, s, pw.ToArray()))) { lock (gate) statsWaits.Remove(s); return false; }
            lock (w)
            {
                Stopwatch sw = Stopwatch.StartNew();
                while (!w.Done && sw.ElapsedMilliseconds < 2000) Monitor.Wait(w, 50);
            }
            lock (gate) statsWaits.Remove(s);
            if (!w.Ok) return false;
            frames = w.Frames;
            info = w.Info;
            return true;
        }

        // ---- IGlassTransport ----

        public IGlassChannel Open(GlassSpec spec, IGlassEvents events, SurfaceState state)
        {
            if (disposed) throw new ObjectDisposedException("PipeTransport");
            if (!everConnected) throw new InvalidOperationException("host unavailable");
            PipeChannel ch;
            lock (gate)
            {
                ch = new PipeChannel(this, spec, events, state, ++nextCookie);
                byCookie[ch.Cookie] = ch;
            }
            if (connected) Establish(ch);   // (else the reconnect creates it)
            return ch;
        }

        public void Wake(IGlassChannel channel)
        {
            PipeChannel c = channel as PipeChannel;
            if (c != null) c.Wake();
        }

        public void Close(IGlassChannel channel)
        {
            PipeChannel c = channel as PipeChannel;
            if (c != null) c.Close();
        }

        // Events are delivered on the I/O thread when the client has no SynchronizationContext.
        public bool Post(Action work) { return false; }

        internal void Forget(PipeChannel ch, uint id)
        {
            lock (gate)
            {
                byCookie.Remove(ch.Cookie);
                if (id != 0) bySurface.Remove(id);
            }
        }

        // Stops the I/O thread; true when it has ended. Called ON that thread (a Lost handler, an event with no context) it only
        // signals: the thread ends on its own when the handler returns.
        bool ShutdownIo()
        {
            disposed = true;
            try { stopEvt.Set(); }
            catch (Exception) { }
            NamedPipeClientStream p;
            lock (gate) p = pipe;
            PipeNative.Shut(p);
            if (Thread.CurrentThread == io) return false;
            return io.Join(3000);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeCalled, 1) != 0) return;
            PipeChannel[] all;
            lock (gate)
            {
                all = new PipeChannel[byCookie.Count];
                byCookie.Values.CopyTo(all, 0);
            }
            foreach (PipeChannel c in all)
            {
                try { c.Close(); }   // (tells the host; nothing is left mapped)
                catch (Exception) { }
            }
            // let the Destroy frames reach the pipe before it closes (they are written asynchronously)
            FrameQueue w;
            lock (gate) w = writer;
            if (w != null && !w.Dead)
            {
                Stopwatch sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 200 && !w.Idle) Thread.Sleep(1);
            }
            ShutdownIo();
            // the handles are released once the I/O thread is done with them: now if it has ended, else when it does end
            lock (relGate)
            {
                releaseWanted = true;
                if (ioDone) Release();
            }
        }

        // (relGate held, the I/O thread ended)
        void Release()
        {
            if (released) return;
            released = true;
            EventWaitHandle e;
            lock (gate)
            {
                e = hostWake;
                hostWake = null;
            }
            if (e != null) { try { e.Dispose(); } catch (Exception) { } }
            try { stopEvt.Close(); }
            catch (Exception) { }
            try { firstDone.Close(); }
            catch (Exception) { }
        }
    }

    // A surface's place on the host, as the client sees it: the section it writes into and the bookkeeping that keeps the host's copy
    // current. Every field below `gate` belongs to it. Lock order: a channel's gate, then the transport's.
    internal sealed class PipeChannel : IGlassChannel, IGlassStats
    {
        readonly PipeTransport owner;
        readonly GlassSpec spec;
        readonly SurfaceState state;
        public readonly IGlassEvents Events;
        public readonly uint Cookie;
        readonly object gate = new object();
        GlassSection section;
        uint surfaceId;
        bool failedOnce;   // Fail was called (the surface is going to stop)
        bool ready, closed, stopped, createSent, destroySent, resizePending, forceAll, retryArmed;
        Timer retryTimer;
        int writtenVersion = -1, lastContentVer = -1, sentLookVer;
        int sentSizeVer = -1, rejectedSizeVer = -1;   // the SizeVer of the Resize in flight / of the last one the host refused (not asked again)
        // Protocol 1.1: what the host holds of the extras (a new host surface starts at these values; a Create resets them: see FreshExtras)
        int sentFlowVer;
        int sMotionZoom = -1, sMotionLiquid = -1, sMotionBounce = -1, sMotionTravel = -1, sOsdStyle = -1;
        int sSloshStyle, sSloshAmount, sKickVer, sButtonsVer, sCutVer, sFps = -1, sAdapt = -1, sSavePower = -1;
        bool sBusy, sHasPointer;
        Rectangle sPointer;
        // the stats cache
        int cacheAt, cachedFrames;
        bool haveCache;
        GlassLatencyInfo cachedInfo;

        public PipeChannel(PipeTransport owner, GlassSpec spec, IGlassEvents events, SurfaceState state, uint cookie)
        {
            this.owner = owner;
            this.spec = spec;
            this.Events = events;
            this.state = state;
            this.Cookie = cookie;
        }

        // The Create frame for the newest props, or null when there is nothing to create (closed, stopped, already sent).
        public byte[] BuildCreate()
        {
            lock (gate)
            {
                if (closed || stopped || createSent) return null;
                createSent = true;
                SurfaceProps p = state.Props;
                sentLookVer = p.LookVer;
                sentFlowVer = p.FlowVer;   // (the Create carries the size as it is: no flow)
                return GlassWire.Create(Cookie, spec, p);
            }
        }

        // The host's protocol minor (-1: not known yet). 0 = a v1 host: the in-process extras are not available.
        public int HostMinor { get { return owner.HostMinor; } }

        // (gate held) A host surface was just created (first time, or again after a host restart): it holds the defaults, so every extra
        // is sent again. A kick and a cut are events, not state: they are not replayed.
        void FreshExtras(SurfaceProps p)
        {
            sMotionZoom = sMotionLiquid = sMotionBounce = sMotionTravel = sOsdStyle = -1;
            sSloshStyle = sSloshAmount = 0;
            sButtonsVer = 0;
            sFps = sAdapt = sSavePower = -1;
            sBusy = false;
            sHasPointer = false;
            sPointer = Rectangle.Empty;
            sKickVer = p.KickVer;
            sCutVer = p.CutVer;
            state.TakeKick();
        }

        // True when this surface needs nothing more from a reconnect: it is created on the host (ready), or it is closed or failed.
        public bool Settled
        {
            get { lock (gate) return ready || closed || stopped || failedOnce; }
        }

        // The host's answer to a Create or a Resize. True = the caller must tell the host to destroy this id (we no longer want it).
        public bool OnAck(uint id, string name, int bytes, int w, int h)
        {
            string fail = null;
            lock (gate)
            {
                if (closed || stopped)
                {
                    bool need = !destroySent;
                    destroySent = true;
                    return need;
                }
                GlassSection s = null;
                try { s = GlassSection.Open(name, w, h, bytes); }
                catch (Exception ex) { fail = ex.Message; }
                if (fail == null)
                {
                    if (!ready) FreshExtras(state.Props);   // (the answer to a Create; an answer to a Resize leaves the host's extras as they are)
                    if (section != null) section.Dispose();
                    section = s;
                    surfaceId = id;
                    ready = true;
                    resizePending = false;
                    writtenVersion = -1;
                    lastContentVer = -1;
                    forceAll = true;
                    Reconcile();
                }
            }
            if (fail != null) Fail(GlassFailure.TransportFailed, "the host's section would not map: " + fail);
            else owner.SignalHost();
            return false;
        }

        public void Fail(int code, string text)
        {
            lock (gate) failedOnce = true;
            try { Events.Failed(code, text); }
            catch (Exception) { }
        }

        // The pipe broke: the surface keeps everything it needs to be created again.
        public void OnLost()
        {
            lock (gate)
            {
                ready = false;
                createSent = false;
                destroySent = false;
                resizePending = false;
                rejectedSizeVer = -1;
                surfaceId = 0;
                haveCache = false;
                if (section != null) { section.Dispose(); section = null; }
            }
        }

        // Any thread, after a call changed the surface's state.
        public void Wake()
        {
            bool wrote;
            lock (gate)
            {
                if (closed || stopped || !ready) return;
                wrote = Reconcile();
            }
            if (wrote) owner.SignalHost();
        }

        // (gate held) Brings the host's copy up to date: a resize first (the contents wait for the new section), the look, the state
        // block, the contents. True if anything was written to the section.
        bool Reconcile()
        {
            bool wrote = false;
            SurfaceProps p = state.Props;
            uint seq;
            if ((p.Size.Width != section.W || p.Size.Height != section.H) && p.SizeVer != rejectedSizeVer)
            {
                if (!resizePending)
                {
                    resizePending = true;
                    sentSizeVer = p.SizeVer;
                    PayloadWriter w = new PayloadWriter();
                    w.U32(surfaceId);
                    w.I32(p.Size.Width);
                    w.I32(p.Size.Height);
                    w.U32(owner.HostMinor >= 1 && p.FlowVer != sentFlowVer ? 1u : 0u);   // (flags: bit0 = flow, protocol 1.1)
                    sentFlowVer = p.FlowVer;
                    owner.Send(Msg.Resize, w.ToArray(), this, out seq);
                }
            }
            else if (!resizePending) sentFlowVer = p.FlowVer;   // (the size is back at the section's: a flow asked for before is dropped, it is not for a later resize)
            if (p.LookVer != sentLookVer)
            {
                if (owner.TakeToken())
                {
                    sentLookVer = p.LookVer;
                    PayloadWriter w = new PayloadWriter();
                    w.U32(surfaceId);
                    GlassWire.WriteLook(w, p.Look);
                    w.Str(p.Profile);
                    owner.Send(Msg.SetLook, w.ToArray(), this, out seq);
                }
                else if (!retryArmed)
                {
                    // over the rate: only the newest look matters; it goes out when the bucket has refilled
                    retryArmed = true;
                    if (retryTimer == null) retryTimer = new Timer(RetryTick, null, 10, Timeout.Infinite);
                    else retryTimer.Change(10, Timeout.Infinite);
                }
            }
            if (owner.HostMinor >= 1) SendCut(p);   // (before the state block, see SendCut)
            if (p.Version != writtenVersion)
            {
                section.WriteState(p);
                writtenVersion = p.Version;
                wrote = true;
            }
            if (owner.HostMinor >= 1) SendExtras(p);
            int cw, ch, ver;
            Rectangle dirty;
            int[] px = state.Contents.Take(out cw, out ch, out ver, out dirty);
            if (cw != section.W || ch != section.H)
            {
                forceAll = true;   // (a resize is in flight: this picture belongs to the new section)
            }
            else if (ver != lastContentVer || forceAll)
            {
                if (forceAll) dirty = new Rectangle(0, 0, section.W, section.H);
                forceAll = false;
                section.PublishContents(px, dirty);
                lastContentVer = ver;
                wrote = true;
            }
            return wrote;
        }

        // (gate held) Protocol 1.1: sends what changed of the extras since the host last heard (a diff against what was sent).
        void SendExtras(SurfaceProps p)
        {
            uint seq;
            if (p.MotionZoom != sMotionZoom || p.MotionLiquid != sMotionLiquid || p.MotionBounce != sMotionBounce || p.MotionTravel != sMotionTravel || p.OsdStyle != sOsdStyle)
            {
                sMotionZoom = p.MotionZoom;
                sMotionLiquid = p.MotionLiquid;
                sMotionBounce = p.MotionBounce;
                sMotionTravel = p.MotionTravel;
                sOsdStyle = p.OsdStyle;
                PayloadWriter w = new PayloadWriter();
                w.U32(surfaceId);
                w.I32(sMotionZoom);
                w.I32(sMotionLiquid);
                w.I32(sMotionBounce);
                w.I32(sMotionTravel);
                w.I32(sOsdStyle);
                owner.Send(Msg.SetMotionEx, w.ToArray(), null, out seq);
            }
            if (p.SloshStyle != sSloshStyle || p.SloshAmount != sSloshAmount)
            {
                sSloshStyle = p.SloshStyle;
                sSloshAmount = p.SloshAmount;
                PayloadWriter w = new PayloadWriter();
                w.U32(surfaceId);
                w.I32(sSloshStyle);
                w.I32(sSloshAmount);
                owner.Send(Msg.SetSlosh, w.ToArray(), null, out seq);
            }
            if (p.KickVer != sKickVer)
            {
                sKickVer = p.KickVer;
                float push = state.TakeKick();
                // (a Kick is limited to |push| <= 8 on the wire: a larger sum goes out as several frames, so it sums up as in process)
                for (int k = 0; k < 64 && push != 0f && !float.IsNaN(push) && !float.IsInfinity(push); k++)
                {
                    float part = Math.Max(-8f, Math.Min(8f, push));
                    PayloadWriter w = new PayloadWriter();
                    w.U32(surfaceId);
                    w.F32(part);
                    owner.Send(Msg.Kick, w.ToArray(), null, out seq);
                    push -= part;
                }
            }
            if (p.ButtonsVer != sButtonsVer)
            {
                sButtonsVer = p.ButtonsVer;
                owner.Send(Msg.SetButtons, GlassWire.Buttons(surfaceId, p.Buttons, p.ButtonsCarry), null, out seq);
            }
            if (p.HasPointer != sHasPointer || p.Pointer != sPointer)
            {
                sHasPointer = p.HasPointer;
                sPointer = p.Pointer;
                Rectangle a = p.HasPointer ? p.Pointer : Rectangle.Empty;
                PayloadWriter w = new PayloadWriter();
                w.U32(surfaceId);
                w.I32(Math.Max(-100000, Math.Min(100000, a.X)));
                w.I32(Math.Max(-100000, Math.Min(100000, a.Y)));
                w.I32(a.Width);
                w.I32(a.Height);
                owner.Send(Msg.SetPointerArea, w.ToArray(), null, out seq);
            }
            if (p.Fps >= 0 && p.Adapt >= 0 && p.SavePower >= 0 && (p.Fps != sFps || p.Adapt != sAdapt || p.SavePower != sSavePower))
            {
                sFps = p.Fps;
                sAdapt = p.Adapt;
                sSavePower = p.SavePower;
                PayloadWriter w = new PayloadWriter();
                w.U32(surfaceId);
                w.I32(sFps);
                w.U8(sAdapt);
                w.U8(sSavePower);
                owner.Send(Msg.SetUpdates, w.ToArray(), null, out seq);
            }
            if (p.Busy != sBusy)
            {
                sBusy = p.Busy;
                PayloadWriter w = new PayloadWriter();
                w.U32(surfaceId);
                w.U8(sBusy ? 1 : 0);
                owner.Send(Msg.SetBusy, w.ToArray(), null, out seq);
            }
        }

        // (gate held) A cut goes out BEFORE the state block that says "hidden" is published, so the host never starts an animated hide.
        void SendCut(SurfaceProps p)
        {
            if (p.CutVer == sCutVer) return;
            sCutVer = p.CutVer;
            uint seq;
            PayloadWriter w = new PayloadWriter();
            w.U32(surfaceId);
            owner.Send(Msg.Cut, w.ToArray(), null, out seq);
        }

        void RetryTick(object state)
        {
            lock (gate) retryArmed = false;
            try { Wake(); }
            catch (Exception) { }
        }

        // The host answered a request with an Error: the resize it may have been for is over. A quota refusal of a resize leaves the
        // surface as it was (shown at its last acked size, contents kept) and is reported as RequestRejected; anything else fails it.
        public void RequestFailed(int wireCode, string text)
        {
            bool rejected;
            lock (gate)
            {
                rejected = wireCode == WireError.Quota && ready && resizePending;
                if (rejected) rejectedSizeVer = sentSizeVer;
                resizePending = false;
            }
            if (rejected)
            {
                try { Events.RequestRejected(GlassFailure.Rejected, text); }
                catch (Exception) { }
                return;
            }
            Fail(wireCode == WireError.BadPayload ? GlassFailure.BadSpec : GlassFailure.TransportFailed, text);
        }

        // The surface failed: no more frames, glass off the screen.
        public void Stop()
        {
            uint id;
            lock (gate)
            {
                if (stopped || closed) return;
                stopped = true;
                id = surfaceId;
                if (id != 0) destroySent = true;
            }
            if (id != 0) Destroy(id);
        }

        public void Close()
        {
            uint id;
            bool tell;
            lock (gate)
            {
                if (closed) return;
                closed = true;
                id = surfaceId;
                tell = id != 0 && !destroySent;
                if (id != 0) destroySent = true;
                ready = false;
                if (section != null) { section.Dispose(); section = null; }
                if (retryTimer != null) { retryTimer.Dispose(); retryTimer = null; }
                retryArmed = true;   // (no timer again)
            }
            owner.Forget(this, id);
            if (tell) Destroy(id);
        }

        void Destroy(uint id)
        {
            PayloadWriter w = new PayloadWriter();
            w.U32(id);
            uint s;
            owner.Send(Msg.Destroy, w.ToArray(), null, out s);
        }

        public void Dispose() { Close(); }

        // ---- IGlassStats ----

        void Refresh()
        {
            uint id;
            lock (gate)
            {
                if (!ready || closed || surfaceId == 0) return;
                if (haveCache && unchecked(Environment.TickCount - cacheAt) < 10) return;
                id = surfaceId;
            }
            int frames;
            GlassLatencyInfo info;
            if (!owner.AskStats(id, out frames, out info)) return;
            lock (gate)
            {
                cachedFrames = frames;
                cachedInfo = info;
                cacheAt = Environment.TickCount;
                haveCache = true;
            }
        }

        public int FramesNow()
        {
            Refresh();
            lock (gate) return cachedFrames;
        }

        public GlassLatencyInfo LatencyNow()
        {
            Refresh();
            lock (gate) return cachedInfo;
        }
    }
}
