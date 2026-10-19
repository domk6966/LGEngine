using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace LGEngine
{
    // The engine as a service, host side (SERVICE.md sections 3, 6, 7 and design 1-5): a named pipe for the rare messages, a section
    // per surface for the frequent ones, one auto-reset event (HostWake) that the clients set after writing a section. Everything
    // that touches a window or a section runs on the host's one GlassRenderThread: it waits on HostWake too (AddWait), so an idle
    // host sleeps with no timer; a wake pulls every surface's state block and contents out of its section into the SurfaceState
    // that the ordinary SurfaceRuntime already reads. The host trusts nothing that a client wrote: every frame is length- and
    // range-checked, every number in a section is clamped or ignored, a rectangle is clipped to the card the HOST sized.
    //
    // One blocking reader thread per client (at most 16); replies and events go out through an asynchronous write chain, so a slow
    // client can stall only itself (its queue over 1024 frames ends its session). Quotas per client: 64 surfaces, 256 MB of contents,
    // 200 control messages a second (a bucket with a burst of 400).
    public sealed class GlassHostServer : IDisposable
    {
        const int MaxClients = 16, MaxSurfaces = 64, RateBurst = 400, RatePerSecond = 200, MaxErrors = 40;
        const long MaxBytesPerClient = 256L * 1024 * 1024;

        sealed class HostSurface
        {
            public uint Id, Cookie;
            public Session Owner;
            public GlassSpec Spec;
            public SurfaceState State = new SurfaceState();
            public SurfaceProps Cur;
            public GlassSection Section;
            public int[] Mirror;
            public int LastP, LastClientVer = -1, HostVer, Generation;
            public long Bytes;
            public RenderSlot Slot;
            public volatile bool Closing;
            public bool Listed;   // (in `all`; the render thread's)
            public HostEvents Events;
        }

        // IGlassEvents for one host surface: the runtime's events become frames to the client. The light goes only to a client that
        // said it speaks protocol 1.1, and only when it moved by 0.02 (or reached exactly 0 or 1): the runtime reports every change.
        sealed class HostEvents : IGlassEvents, IGlassLightEvents
        {
            readonly HostSurface s;
            float lastLight = float.NaN;   // (the render thread's: the last value sent; none yet, so that a re-created surface says where it is)
            public HostEvents(HostSurface s) { this.s = s; }

            public void LightMoved(float light)
            {
                if (s.Closing || s.Owner.Minor < 1 || float.IsNaN(light) || float.IsInfinity(light)) return;
                bool edge = (light == 0f || light == 1f) && light != lastLight;
                if (!edge && !float.IsNaN(lastLight) && Math.Abs(light - lastLight) < 0.02f) return;
                lastLight = light;
                PayloadWriter w = new PayloadWriter();
                w.U32(s.Id);
                w.F32(light);
                Out(Msg.LightMoved, w);
            }

            void Out(int type, PayloadWriter w)
            {
                if (s.Closing) return;
                s.Owner.Send(type, 0, w.ToArray());
            }

            public void Shown() { PayloadWriter w = new PayloadWriter(); w.U32(s.Id); Out(Msg.Shown, w); }
            public void Hidden() { PayloadWriter w = new PayloadWriter(); w.U32(s.Id); Out(Msg.Hidden, w); }
            public void AppearanceChanged(bool light) { PayloadWriter w = new PayloadWriter(); w.U32(s.Id); w.U8(light ? 1 : 0); Out(Msg.Appearance, w); }
            public void Failed(int code, string text) { PayloadWriter w = new PayloadWriter(); w.U32(s.Id); w.I32(code); w.Str(text); Out(Msg.Failed, w); }
            public void RequestRejected(int code, string text) { }   // (the host's own surfaces never reject a request)
        }

        sealed class Session
        {
            public readonly GlassHostServer Host;
            public readonly NamedPipeServerStream Pipe;
            public readonly FrameQueue Writer;
            public readonly Dictionary<uint, HostSurface> Surfaces = new Dictionary<uint, HostSurface>();   // under lock(Surfaces)
            public long Bytes;                                                                             // under lock(Surfaces)
            public Thread Reader;
            public volatile bool Dead;
            readonly object rateGate = new object();
            double tokens = RateBurst;
            long tokenAt = System.Diagnostics.Stopwatch.GetTimestamp();
            volatile bool hello;
            public volatile int Minor;   // the session's protocol minor: min(the client's, ours); 0 for a Hello that carried none
            int errors;                                                   // protocol violations (Interlocked); halved every 10 s
            long errorsAt = System.Diagnostics.Stopwatch.GetTimestamp();   // (Interlocked)
            // Over the rate, a SetLook / Resize waits here (only the newest per surface) until the bucket has refilled.
            sealed class DeferredLook { public GlassLook Look; public string Profile; }
            sealed class DeferredResize { public uint Seq; public int W, H; public bool Flow; }
            readonly object errGate = new object();
            readonly Dictionary<ulong, long> errSentAt = new Dictionary<ulong, long>();   // (surface id, message class) -> when the last throttled Error went out (errGate)
            readonly object deferGate = new object();
            readonly Dictionary<uint, DeferredLook> deferLook = new Dictionary<uint, DeferredLook>();
            readonly Dictionary<uint, DeferredResize> deferResize = new Dictionary<uint, DeferredResize>();
            Timer flushTimer;
            bool flushArmed;

            public Session(GlassHostServer host, NamedPipeServerStream pipe)
            {
                Host = host;
                Pipe = pipe;
                Writer = new FrameQueue(pipe, Kill);
            }

            public HostSurface[] Snapshot()
            {
                lock (Surfaces)
                {
                    HostSurface[] a = new HostSurface[Surfaces.Count];
                    Surfaces.Values.CopyTo(a, 0);
                    return a;
                }
            }

            // False when the host has no render thread any more (it is stopping: its Stop retires everything itself).
            bool PostHost(Action work)
            {
                GlassRenderThread t = Host.thread;
                return t != null && t.Post(work);
            }

            public void Kill()
            {
                Dead = true;
                PipeNative.Shut(Pipe);
            }

            public bool Send(int type, uint seq, byte[] payload)
            {
                return Writer.Send(GlassFrame.Build(type, seq, payload));
            }

            void SendError(uint seq, int code, string text)
            {
                PayloadWriter w = new PayloadWriter();
                w.I32(code);
                w.Str(text);
                Send(Msg.Error, seq, w.ToArray());
            }

            // An Error for a request that can be repeated at will (SetLook, an over-rate Resize): at most one reply per surface id per
            // 100 ms (the rest are dropped, so a flood never fills the write queue). count: it also counts as a protocol violation.
            void ErrorLimited(uint id, uint seq, int code, string text, bool count)
            {
                if (count)
                {
                    Decay();
                    Interlocked.Increment(ref errors);
                }
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                // the class is the message the Error is about (the text up to its colon: "setlook", "resize", "kick" ...), so that a
                // throttled Error of an extra can never swallow the Error of a Resize or a SetLook
                int colon = text == null ? -1 : text.IndexOf(':');
                string cls = text == null ? "" : (colon < 0 ? text : text.Substring(0, colon));
                ulong key = ((ulong)(uint)cls.GetHashCode() << 32) | id;
                lock (errGate)
                {
                    long at;
                    if (errSentAt.TryGetValue(key, out at) && now - at < System.Diagnostics.Stopwatch.Frequency / 10) return;
                    if (errSentAt.Count >= 256) errSentAt.Clear();
                    errSentAt[key] = now;
                }
                SendError(seq, code, text);
            }

            public void Error(uint seq, int code, string text)
            {
                SendError(seq, code, text);
                // only a client that breaks the protocol counts (a surface that is gone, a quota, a host fault are not its doing)
                if (code == WireError.BadFrame || code == WireError.BadLength || code == WireError.BadVersion || code == WireError.Unsupported || code == WireError.BadPayload || code == WireError.NoHello)
                {
                    Decay();
                    Interlocked.Increment(ref errors);
                }
            }

            // The violations fade: halved every 10 s.
            void Decay()
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                long at = Interlocked.Read(ref errorsAt);
                long steps = (now - at) / (10 * System.Diagnostics.Stopwatch.Frequency);
                if (steps <= 0) return;
                if (Interlocked.CompareExchange(ref errorsAt, at + steps * 10 * System.Diagnostics.Stopwatch.Frequency, at) != at) return;
                int shift = (int)Math.Min(31, steps);
                int e, h;
                do
                {
                    e = Thread.VolatileRead(ref errors);
                    h = e >> shift;
                } while (Interlocked.CompareExchange(ref errors, h, e) != e);
            }

            bool Allow()
            {
                lock (rateGate)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    tokens = Math.Min(RateBurst, tokens + (now - tokenAt) * (double)RatePerSecond / System.Diagnostics.Stopwatch.Frequency);
                    tokenAt = now;
                    if (tokens < 1) return false;
                    tokens -= 1;
                    return true;
                }
            }

            // Delays the reader until a token is free (the client's own writes back up behind it); false = the session ended meanwhile.
            bool WaitToken()
            {
                while (!Dead && !Host.stopping)
                {
                    Thread.Sleep(2);
                    if (Allow()) return true;
                }
                return false;
            }

            public void Run()
            {
                Interlocked.Increment(ref GlassPipeStats.threads);
                // a connection that has not sent a valid Hello within 2 s is closed
                Timer helloTimer = new Timer(delegate { if (!hello) Kill(); }, null, 2000, Timeout.Infinite);
                try
                {
                    byte[] buf = new byte[GlassFrame.MaxFrame];
                    while (!Dead && !Host.stopping)
                    {
                        int n;
                        try { n = Pipe.Read(buf, 0, buf.Length); }
                        catch (Exception) { break; }
                        if (n <= 0) break;
                        bool complete;
                        try { complete = Pipe.IsMessageComplete; }
                        catch (Exception) { break; }
                        if (!complete)
                        {
                            // a message larger than any frame: the stream cannot be trusted; say so and end the session
                            Error(0, WireError.BadLength, "frame is larger than " + GlassFrame.MaxFrame + " bytes");
                            break;
                        }
                        try
                        {
                            if (!Handle(buf, n)) break;
                        }
                        catch (Exception ex)
                        {
                            Error(0, WireError.Internal, "internal error: " + ex.Message);
                        }
                        Decay();
                        if (Thread.VolatileRead(ref errors) > MaxErrors) break;
                    }
                }
                finally
                {
                    try { helloTimer.Dispose(); }
                    catch (Exception) { }
                    try { Cleanup(); }
                    catch (Exception) { }   // (nothing escapes a background thread)
                    Interlocked.Decrement(ref GlassPipeStats.threads);
                }
            }

            // False = end the session.
            bool Handle(byte[] buf, int n)
            {
                int type, len;
                uint seq;
                int bad = GlassFrame.Check(buf, n, out type, out seq, out len);
                if (bad != 0)
                {
                    Error(seq, bad, bad == WireError.BadVersion ? "unsupported protocol version" : (bad == WireError.BadLength ? "frame length does not match" : "not a frame"));
                    return true;
                }
                PayloadReader r = new PayloadReader(buf, GlassFrame.HeaderSize, len);
                if (!hello)
                {
                    if (type != Msg.Hello)
                    {
                        Error(seq, WireError.NoHello, "the first message must be Hello");
                        return false;
                    }
                    int min = r.U16(), max = r.U16();
                    string name = r.Str(256);
                    if (r.Bad) { Error(seq, WireError.BadPayload, "hello: bad payload"); return false; }
                    if (min > GlassFrame.Version || max < GlassFrame.Version) { Error(seq, WireError.BadVersion, "no common protocol version (host speaks " + GlassFrame.Version + ")"); return false; }
                    bool carried = r.Remaining >= 2;   // (protocol 1.1: an optional trailing u16, the client's minor)
                    int clientMinor = carried ? r.U16() : 0;
                    Minor = Math.Min(clientMinor, GlassFrame.Minor);
                    hello = true;
                    PayloadWriter w = new PayloadWriter();
                    w.U16(GlassFrame.Version);
                    w.Str("LGEngine host");
                    if (carried) w.U16(GlassFrame.Minor);   // (a client that sent none gets the v1 ack, byte for byte)
                    Send(Msg.HelloAck, seq, w.ToArray());
                    return true;
                }
                // Over the rate the client is slowed, never failed: a SetLook / Resize waits for the bucket (the newest per surface is
                // kept), any other message holds the reader until a token is free.
                bool allowed = Allow();
                if (!allowed && type != Msg.SetLook && type != Msg.Resize)
                {
                    if (!WaitToken()) return false;
                }
                switch (type)
                {
                    case Msg.Create: OnCreate(seq, r); break;
                    case Msg.Resize: OnResize(seq, r, allowed); break;
                    case Msg.Destroy: OnDestroy(seq, r); break;
                    case Msg.SetLook: OnSetLook(seq, r, allowed); break;
                    case Msg.Stats: OnStats(seq, r); break;
                    case Msg.SetMotionEx:
                    case Msg.SetSlosh:
                    case Msg.Kick:
                    case Msg.SetButtons:
                    case Msg.SetPointerArea:
                    case Msg.SetUpdates:
                    case Msg.SetBusy:
                    case Msg.Cut:
                        if (Minor >= 1) OnExtra(type, seq, r, allowed);   // (protocol 1.1 only; to a minor-0 session they are unknown types, as before)
                        else Error(seq, WireError.Unsupported, "message type " + type + " is not supported");
                        break;
                    default: Error(seq, WireError.Unsupported, "message type " + type + " is not supported"); break;
                }
                return true;
            }

            HostSurface Find(uint id)
            {
                HostSurface s;
                lock (Surfaces) Surfaces.TryGetValue(id, out s);
                return s;
            }

            void OnCreate(uint seq, PayloadReader r)
            {
                uint cookie;
                GlassSpec spec;
                SurfaceProps props;
                if (Host.stopping) { Error(seq, WireError.Internal, "the host is shutting down"); return; }   // (a reader that outlived Stop's join)
                string bad = GlassWire.ReadCreate(r, out cookie, out spec, out props);
                if (bad != null) { Error(seq, WireError.BadPayload, bad); return; }
                long bytes = QuotaBytes(spec.Size.Width, spec.Size.Height);
                HostSurface s = new HostSurface();
                s.Owner = this;
                s.Cookie = cookie;
                s.Spec = spec;
                s.Cur = props;
                s.Bytes = bytes;
                lock (Surfaces)
                {
                    if (Surfaces.Count >= MaxSurfaces) { Error(seq, WireError.Quota, "more than " + MaxSurfaces + " surfaces"); return; }
                    if (Bytes + bytes > MaxBytesPerClient) { Error(seq, WireError.Quota, "more than " + (MaxBytesPerClient >> 20) + " MB of contents"); return; }
                    s.Id = Host.NextId();
                    Surfaces[s.Id] = s;
                    Bytes += bytes;
                }
                if (!PostHost(delegate { Host.CreateOnThread(s, seq); }))
                {
                    Drop(s);
                    Error(seq, WireError.Internal, "the host is shutting down");
                }
            }

            void Drop(HostSurface s)
            {
                lock (Surfaces)
                {
                    if (Surfaces.Remove(s.Id)) Bytes -= s.Bytes;
                }
            }

            // The quota is charged on the render thread, when the resize really happens (SetBytes); here it only answers early.
            void OnResize(uint seq, PayloadReader r, bool allowed)
            {
                uint id = r.U32();
                int w = r.I32(), h = r.I32();
                if (r.Bad) { Error(seq, WireError.BadPayload, "resize: bad payload"); return; }
                bool flow = Minor >= 1 && r.Remaining >= 4 && (r.U32() & 1u) != 0;   // (the old reserved u32: bit0 = flow, for protocol 1.1 sessions)
                // (validated before it is coalesced; over the rate an Error is throttled per surface and counts as a violation)
                HostSurface s = Find(id);
                if (s == null) { ResizeError(allowed, id, seq, WireError.NoSurface, "resize: no such surface"); return; }
                if (w < 1 || h < 1 || w > 4096 || h > 4096) { ResizeError(allowed, id, seq, WireError.BadPayload, "resize: size " + w + "x" + h + " is not 1..4096"); return; }
                long bytes = QuotaBytes(w, h);
                lock (Surfaces)
                {
                    if (Bytes - s.Bytes + bytes > MaxBytesPerClient) { ResizeError(allowed, id, seq, WireError.Quota, "more than " + (MaxBytesPerClient >> 20) + " MB of contents"); return; }
                }
                lock (deferGate)
                {
                    if (allowed)
                    {
                        deferResize.Remove(id);   // (an older one that was waiting is superseded)
                        PostResize(s, w, h, seq, flow);
                    }
                    else
                    {
                        DeferredResize d = new DeferredResize();
                        d.Seq = seq;
                        d.W = w;
                        d.H = h;
                        d.Flow = flow;
                        deferResize[id] = d;
                        ArmFlush();
                    }
                }
            }

            // A bad Resize: answered at once while the client is within the rate (it has one in flight, and waits for this reply); over the
            // rate it is a flood: one reply per surface per 100 ms, counted as a violation.
            void ResizeError(bool allowed, uint id, uint seq, int code, string text)
            {
                if (allowed) Error(seq, code, text);
                else ErrorLimited(id, seq, code, text, true);
            }

            void PostResize(HostSurface s, int w, int h, uint seq, bool flow)
            {
                if (!PostHost(delegate { Host.ResizeOnThread(s, w, h, seq, flow); })) Error(seq, WireError.Internal, "the host is shutting down");
            }

            public const int BytesOk = 0, BytesGone = 1, BytesOver = 2;

            // Sets what a surface counts for (its section, its mirror and the contents' two buffers). prev: what it counted before.
            public int SetBytes(HostSurface s, long bytes, bool check, out long prev)
            {
                lock (Surfaces)
                {
                    prev = s.Bytes;
                    if (!Surfaces.ContainsKey(s.Id)) return BytesGone;
                    if (check && Bytes - s.Bytes + bytes > MaxBytesPerClient) return BytesOver;
                    Bytes += bytes - s.Bytes;
                    s.Bytes = bytes;
                    return BytesOk;
                }
            }

            // (deferGate held) The timer that lets the deferred messages out as the bucket refills.
            void ArmFlush()
            {
                if (flushArmed || Dead) return;
                flushArmed = true;
                if (flushTimer == null) flushTimer = new Timer(Flush, null, 5, Timeout.Infinite);
                else flushTimer.Change(5, Timeout.Infinite);
            }

            void Flush(object state)
            {
                try
                {
                    lock (deferGate)
                    {
                        flushArmed = false;
                        if (Dead) return;
                        uint[] ids = new uint[deferLook.Count];
                        deferLook.Keys.CopyTo(ids, 0);
                        foreach (uint id in ids)
                        {
                            if (!Allow()) break;
                            DeferredLook d = deferLook[id];
                            deferLook.Remove(id);
                            HostSurface s = Find(id);
                            if (s != null) PostLook(s, d.Look, d.Profile, 0);
                        }
                        ids = new uint[deferResize.Count];
                        deferResize.Keys.CopyTo(ids, 0);
                        foreach (uint id in ids)
                        {
                            if (!Allow()) break;
                            DeferredResize d = deferResize[id];
                            deferResize.Remove(id);
                            HostSurface s = Find(id);
                            if (s != null) PostResize(s, d.W, d.H, d.Seq, d.Flow);
                            else ErrorLimited(id, d.Seq, WireError.NoSurface, "resize: no such surface", false);
                        }
                        if (deferLook.Count > 0 || deferResize.Count > 0) ArmFlush();
                    }
                }
                catch (Exception) { }   // (a timer thread)
            }

            void OnDestroy(uint seq, PayloadReader r)
            {
                uint id = r.U32();
                if (r.Bad) { Error(seq, WireError.BadPayload, "destroy: bad payload"); return; }
                HostSurface s = Find(id);
                if (s == null) { Error(seq, WireError.NoSurface, "destroy: no such surface"); return; }
                Retire(s);
            }

            public void Retire(HostSurface s)
            {
                s.Closing = true;
                Drop(s);
                lock (deferGate)
                {
                    deferLook.Remove(s.Id);
                    deferResize.Remove(s.Id);
                }
                PostHost(delegate { Host.RetireOnThread(s); });   // (no thread: the host's Stop has retired every surface itself)
            }

            void PostLook(HostSurface s, GlassLook look, string profile, uint seq)
            {
                if (!PostHost(delegate { Host.LookOnThread(s, look, profile); }) && seq != 0) Error(seq, WireError.Internal, "the host is shutting down");
            }

            void OnSetLook(uint seq, PayloadReader r, bool allowed)
            {
                uint id = r.U32();
                GlassLook look;
                bool ok = GlassWire.ReadLook(r, out look);
                string profile = r.Str(GlassWire.MaxProfileBytes);
                // (validated before it is coalesced; the Errors are throttled per surface, and over the rate they count as violations)
                if (r.Bad) { ErrorLimited(0, seq, WireError.BadPayload, "setlook: bad payload", !allowed); return; }
                if (!ok) { ErrorLimited(id, seq, WireError.BadPayload, "setlook: a slider is out of range", !allowed); return; }
                if (!GlassWire.ProfileValid(profile)) { ErrorLimited(id, seq, WireError.BadPayload, "setlook: profile is not a GlassProfile string or a preset's name", !allowed); return; }
                HostSurface s = Find(id);
                if (s == null) { ErrorLimited(id, seq, WireError.NoSurface, "setlook: no such surface", !allowed); return; }
                lock (deferGate)
                {
                    if (allowed)
                    {
                        deferLook.Remove(id);   // (an older one that was waiting is superseded)
                        PostLook(s, look, profile, seq);
                    }
                    else
                    {
                        DeferredLook d = new DeferredLook();
                        d.Look = look;
                        d.Profile = profile;
                        deferLook[id] = d;
                        ArmFlush();
                    }
                }
            }

            // Protocol 1.1 (session minor >= 1): the extras of the in-process surface. Checked like SetLook (the Errors are throttled per
            // surface); nothing is sent back on success. Over the rate Handle has already waited for a token: they are never deferred.
            void OnExtra(int type, uint seq, PayloadReader r, bool allowed)
            {
                uint id = r.U32();
                string what = type == Msg.SetMotionEx ? "setmotionex" : type == Msg.SetSlosh ? "setslosh" : type == Msg.Kick ? "kick" : type == Msg.SetButtons ? "setbuttons"
                    : type == Msg.SetPointerArea ? "setpointerarea" : type == Msg.SetUpdates ? "setupdates" : type == Msg.SetBusy ? "setbusy" : "cut";
                bool ok = true;
                string badText = null;
                Action<HostSurface> work = null;
                switch (type)
                {
                    case Msg.SetMotionEx:
                        {
                            int zoom = r.I32(), liquid = r.I32(), bounce = r.I32(), travel = r.I32(), ind = r.I32();
                            ok = In(zoom, -1, 50) && In(liquid, -1, 100) && In(bounce, -1, 100) && In(travel, -1, 160) && In(ind, -1, 8);
                            work = delegate(HostSurface s)
                            {
                                Host.ExtraOnThread(s, delegate(SurfaceProps n) { n.MotionZoom = zoom; n.MotionLiquid = liquid; n.MotionBounce = bounce; n.MotionTravel = travel; n.OsdStyle = ind; });
                            };
                        }
                        break;
                    case Msg.SetSlosh:
                        {
                            int style = r.I32(), amount = r.I32();
                            ok = In(style, 0, 3) && In(amount, 0, 100);
                            work = delegate(HostSurface s) { Host.ExtraOnThread(s, delegate(SurfaceProps n) { n.SloshStyle = style; n.SloshAmount = amount; }); };
                        }
                        break;
                    case Msg.Kick:
                        {
                            float push = r.F32();
                            ok = Finite(push) && Math.Abs(push) <= 8f;
                            work = delegate(HostSurface s) { Host.KickOnThread(s, push); };
                        }
                        break;
                    case Msg.SetButtons:
                        {
                            bool carry;
                            GlassButton[] buttons;
                            badText = GlassWire.ReadButtons(r, out carry, out buttons);
                            ok = badText == null;
                            work = delegate(HostSurface s) { Host.ExtraOnThread(s, delegate(SurfaceProps n) { n.Buttons = buttons == null || buttons.Length == 0 ? null : buttons; n.ButtonsVer++; n.ButtonsCarry = carry; }); };
                        }
                        break;
                    case Msg.SetPointerArea:
                        {
                            int x = r.I32(), y = r.I32(), w = r.I32(), h = r.I32();
                            ok = In(x, -100000, 100000) && In(y, -100000, 100000);
                            bool has = w > 0 && h > 0;
                            Rectangle area = has ? new Rectangle(x, y, Math.Min(w, 200000), Math.Min(h, 200000)) : Rectangle.Empty;
                            work = delegate(HostSurface s) { Host.ExtraOnThread(s, delegate(SurfaceProps n) { n.HasPointer = has; n.Pointer = area; }); };
                        }
                        break;
                    case Msg.SetUpdates:
                        {
                            int fps = r.I32(), adapt = r.U8(), save = r.U8();
                            ok = In(fps, 0, 60) && In(adapt, 0, 1) && In(save, 0, 1);
                            work = delegate(HostSurface s) { Host.ExtraOnThread(s, delegate(SurfaceProps n) { n.Fps = fps; n.Adapt = adapt; n.SavePower = save; }); };
                        }
                        break;
                    case Msg.SetBusy:
                        {
                            int busy = r.U8();
                            ok = In(busy, 0, 1);
                            work = delegate(HostSurface s) { Host.ExtraOnThread(s, delegate(SurfaceProps n) { n.Busy = busy == 1; }); };
                        }
                        break;
                    default:   // Cut
                        work = delegate(HostSurface s) { Host.CutOnThread(s); };
                        break;
                }
                if (r.Bad || (!ok && badText == "bad payload")) { ErrorLimited(0, seq, WireError.BadPayload, what + ": bad payload", !allowed); return; }
                if (!ok) { ErrorLimited(id, seq, WireError.BadPayload, what + ": " + (badText ?? "a value is out of range"), !allowed); return; }
                HostSurface target = Find(id);
                if (target == null) { ErrorLimited(id, seq, WireError.NoSurface, what + ": no such surface", !allowed); return; }
                PostHost(delegate { work(target); });   // (a host that is stopping retires the surface itself)
            }

            void OnStats(uint seq, PayloadReader r)
            {
                uint id = r.U32();
                if (r.Bad) { Error(seq, WireError.BadPayload, "stats: bad payload"); return; }
                HostSurface s = Find(id);
                if (s == null) { Error(seq, WireError.NoSurface, "stats: no such surface"); return; }
                s.State.Latency.Publish();
                GlassLatencyInfo i = s.State.Latency.Snapshot();
                PayloadWriter w = new PayloadWriter();
                w.U32(id);
                w.I32(Thread.VolatileRead(ref s.State.Frames));
                w.F64(i.QueueP50);
                w.F64(i.QueueP99);
                w.F64(i.PresentP50);
                w.F64(i.PresentP99);
                w.F64(i.VblankP50);
                w.F64(i.VblankP99);
                w.I32(i.QueueSamples);
                w.I32(i.PresentSamples);
                w.I32(i.VblankSamples);
                Send(Msg.StatsReply, seq, w.ToArray());
            }

            void Cleanup()
            {
                Dead = true;
                try
                {
                    lock (deferGate)
                    {
                        deferLook.Clear();
                        deferResize.Clear();
                        flushArmed = true;   // (no timer again)
                        if (flushTimer != null) { flushTimer.Dispose(); flushTimer = null; }
                    }
                    foreach (HostSurface s in Snapshot()) Retire(s);
                }
                finally
                {
                    // (the slot is freed before the pipe closes: a client that connects the moment the instance is free is not counted out)
                    Host.EndSession(this);
                    PipeNative.Shut(Pipe);
                    Host.slot.Set();   // (the accept loop may now make the instance again)
                }
            }
        }

        readonly object gate = new object();
        readonly List<Session> sessions = new List<Session>();
        readonly List<HostSurface> all = new List<HostSurface>();   // the render thread's
        volatile GlassRenderThread thread;
        readonly AutoResetEvent slot = new AutoResetEvent(false);   // set when a client leaves (the accept loop may then make an instance)
        EventWaitHandle hostWake;
        Thread acceptor;
        NamedPipeServerStream waiting;   // the instance the acceptor waits on (under gate)
        string suffix = "", pipeName = "", wakeName = "";
        volatile bool stopping, running;
        int nextId;
        internal int ClientCap = MaxClients;   // (for tests: fewer clients than the pipe's 16 instances, to meet the busy reply)
        int busyReplies;   // busy replies being written (bounded)
        internal readonly ManualResetEvent SessionsChanged = new ManualResetEvent(false);   // pulsed (Set) whenever a client joins or leaves; the host runner's idle clock waits on it
        internal bool RaisePriority;   // the host process: the render thread runs at AboveNormal (set before Start)
        int sessionEvents;   // counts every join and leave (the host runner's idle clock restarts when it changes)
        bool idleClosing;    // (under gate) the idle exit has stopped the accepting; a client that is still let in is refused
        internal int SessionEvents { get { return Thread.VolatileRead(ref sessionEvents); } }

        // The idle exit's first step: stop accepting new pipe connections, then look at the client count again. False = a client is
        // there (or just arrived): accepting goes on and the host stays. True = no client, no new one can get in: the host may exit.
        internal bool BeginIdleClose()
        {
            lock (gate)
            {
                if (!running || stopping) return true;
                if (sessions.Count > 0) return false;
                idleClosing = true;
                PipeNative.Shut(waiting);
                waiting = null;
                return true;
            }
        }

        public string PipeName { get { return pipeName; } }
        public string WakeName { get { return wakeName; } }
        public bool Running { get { return running; } }
        public int ClientCount { get { lock (gate) return sessions.Count; } }

        uint NextId() { return unchecked((uint)Interlocked.Increment(ref nextId)); }

        // What a surface of this size counts against the client's quota: the section (two buffers), the host's mirror and the contents'
        // two buffers.
        static long QuotaBytes(int w, int h) { return GlassSection.SizeFor(w, h) + 12L * w * h; }

        // For tests: the look the host holds for the surface with this label.
        public bool TryGetLook(string label, out GlassLook look)
        {
            look = new GlassLook();
            Session[] ss;
            lock (gate) ss = sessions.ToArray();
            foreach (Session se in ss)
                foreach (HostSurface hs in se.Snapshot())
                {
                    if (hs.Spec.Label != label) continue;
                    SurfaceProps p = hs.State.Props;
                    if (p == null) continue;
                    look = p.Look;
                    return true;
                }
            return false;
        }

        HostSurface FindByLabel(string label)
        {
            Session[] ss;
            lock (gate) ss = sessions.ToArray();
            foreach (Session se in ss)
                foreach (HostSurface hs in se.Snapshot())
                    if (hs.Spec.Label == label) return hs;
            return null;
        }

        // For tests: the protocol 1.1 extras the host holds for the surface with this label, as "key=value;" text (null: no such surface).
        public string TestProps(string label)
        {
            HostSurface hs = FindByLabel(label);
            SurfaceProps p = hs == null ? null : hs.State.Props;
            if (p == null) return null;
            System.Globalization.CultureInfo ic = System.Globalization.CultureInfo.InvariantCulture;
            string b = "";
            if (p.Buttons != null)
            {
                GlassButton g = p.Buttons.Length > 0 ? p.Buttons[0] : null;
                b = p.Buttons.Length + "/" + (g == null ? "-" : g.Style + "," + g.Hit.X + "," + g.Rad.ToString("0.#", ic) + "," + (g.Mask == null ? 0 : g.Mask.Length));
            }
            return "mz=" + p.MotionZoom + ";ml=" + p.MotionLiquid + ";mb=" + p.MotionBounce + ";mt=" + p.MotionTravel + ";os=" + p.OsdStyle
                + ";ss=" + p.SloshStyle + ";sa=" + p.SloshAmount + ";kv=" + p.KickVer + ";bv=" + p.ButtonsVer + ";bc=" + (p.ButtonsCarry ? 1 : 0) + ";b=" + b
                + ";hp=" + (p.HasPointer ? 1 : 0) + ";pa=" + p.Pointer.X + "," + p.Pointer.Y + "," + p.Pointer.Width + "," + p.Pointer.Height
                + ";fps=" + p.Fps + ";ad=" + p.Adapt + ";sp=" + p.SavePower + ";busy=" + (p.Busy ? 1 : 0) + ";cv=" + p.CutVer + ";want=" + (p.WantShown ? 1 : 0)
                + ";fv=" + p.FlowVer + ";ff=" + p.FlowFrom.ToString("0.###", ic) + ";w=" + p.Size.Width + ";";
        }

        // For tests: the surface with this label reports a light value to its client, as the runtime does (through the same throttle).
        public bool TestLight(string label, float light)
        {
            HostSurface hs = FindByLabel(label);
            GlassRenderThread t = thread;
            if (hs == null || hs.Events == null || t == null) return false;
            HostEvents ev = hs.Events;
            return t.Post(delegate { ev.LightMoved(light); });
        }

        // Opens the pipe and the wake event and starts the render thread. Throws if another host owns the names.
        public void Start(string nameSuffix)
        {
            lock (gate)
            {
                if (running) throw new InvalidOperationException("the host is already running");
                suffix = nameSuffix ?? "";
                pipeName = GlassNames.PipeFull(suffix);
                wakeName = GlassNames.HostWake(suffix);
                stopping = false;
                bool created;
                // (a client that has not noticed yet that the previous host is gone still holds the old event: a moment to let go)
                System.Diagnostics.Stopwatch ew = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    hostWake = new EventWaitHandle(false, EventResetMode.AutoReset, wakeName, out created, GlassSecurity.Event());
                    if (created || ew.ElapsedMilliseconds > 500) break;
                    hostWake.Dispose();
                    Thread.Sleep(25);
                }
                NamedPipeServerStream first = null;
                try
                {
                    if (!created) throw new IOException("another host owns the wake event " + wakeName);
                    // (a name stays taken for a moment while the previous owner's clients close their handles: a few tries)
                    System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
                    while (true)
                    {
                        try { first = PipeNative.CreateServer(pipeName, true); break; }
                        catch (IOException ex)
                        {
                            object code = ex.Data["win32"];
                            if (code is int && (int)code == 5 && w.ElapsedMilliseconds < 2000) { Thread.Sleep(25); continue; }
                            throw;
                        }
                    }
                    thread = new GlassRenderThread();
                    thread.AddWait(hostWake, Pump);
                    if (RaisePriority) thread.Post(delegate { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; });
                    waiting = first;
                    acceptor = new Thread(AcceptMain);
                    acceptor.IsBackground = true;
                    acceptor.Name = "LGEngine host accept";
                    running = true;
                    acceptor.Start();
                }
                catch (Exception)
                {
                    PipeNative.Shut(first);
                    if (thread != null)
                    {
                        thread.RemoveWait(hostWake);
                        thread.Shutdown();
                        thread = null;
                    }
                    hostWake.Dispose();
                    hostWake = null;
                    running = false;
                    throw;
                }
            }
        }

        // Ends every session, retires every surface (their windows go), joins the threads, removes the names.
        public void Stop()
        {
            Thread acc;
            Session[] ss;
            lock (gate)
            {
                if (!running) return;
                running = false;
                stopping = true;
                acc = acceptor;
                PipeNative.Shut(waiting);
                waiting = null;
                ss = sessions.ToArray();
            }
            slot.Set();
            if (acc != null) acc.Join(3000);
            foreach (Session s in ss) s.Kill();
            foreach (Session s in ss)
            {
                Thread t = s.Reader;
                if (t != null && t != Thread.CurrentThread) t.Join(3000);
            }
            GlassRenderThread th = thread;
            if (th != null)
            {
                th.Post(delegate
                {
                    foreach (HostSurface hs in all.ToArray()) RetireOnThread(hs);
                });
                th.RemoveWait(hostWake);
                th.Shutdown();
            }
            lock (gate)
            {
                thread = null;
                acceptor = null;
                if (hostWake != null) { hostWake.Dispose(); hostWake = null; }
                sessions.Clear();
            }
        }

        public void Dispose() { Stop(); }

        // ---- the accept loop ----

        void AcceptMain()
        {
            Interlocked.Increment(ref GlassPipeStats.threads);
            try
            {
                int backoff = 50;
                while (!stopping)
                {
                    NamedPipeServerStream cur;
                    lock (gate) cur = waiting;
                    if (cur == null)
                    {
                        if (idleClosing) { slot.WaitOne(50); continue; }   // (the idle exit is deciding: no new instance)
                        // No instance to wait on (the 16-instance cap, or a transient error made the last one fail): try again, backing
                        // off 50 ms .. 1 s; a client leaving (slot) lets the next try come at once.
                        try
                        {
                            NamedPipeServerStream made = PipeNative.CreateServer(pipeName, false);
                            lock (gate)
                            {
                                if (stopping) { PipeNative.Shut(made); break; }
                                if (idleClosing) { PipeNative.Shut(made); continue; }
                                waiting = made;
                            }
                            backoff = 50;
                        }
                        catch (Exception)
                        {
                            slot.WaitOne(backoff);
                            backoff = Math.Min(1000, backoff * 2);
                        }
                        continue;
                    }
                    try { cur.WaitForConnection(); }
                    catch (Exception)
                    {
                        // not a client: drop this instance, the top of the loop makes another
                        PipeNative.Shut(cur);
                        lock (gate) { if (waiting == cur) waiting = null; }
                        Thread.Sleep(20);
                        continue;
                    }
                    // the next instance first, so that the name never has none for a client to find
                    NamedPipeServerStream next = null;
                    try { next = PipeNative.CreateServer(pipeName, false); }
                    catch (Exception) { }
                    lock (gate)
                    {
                        if (stopping || idleClosing) { PipeNative.Shut(next); next = null; }
                        waiting = next;
                    }
                    Admit(cur);
                }
            }
            finally
            {
                Interlocked.Decrement(ref GlassPipeStats.threads);
            }
        }

        void Admit(NamedPipeServerStream pipe)
        {
            uint pid;
            bool ok = !stopping && PipeNative.PeerInMySession(pipe, false, out pid);
            if (ok)
            {
                try { pipe.ReadMode = PipeTransmissionMode.Message; }
                catch (Exception) { ok = false; }
            }
            Session s = null;
            if (ok)
            {
                lock (gate)
                {
                    if (!stopping && !idleClosing && sessions.Count < ClientCap)
                    {
                        s = new Session(this, pipe);
                        sessions.Add(s);
                        Interlocked.Increment(ref GlassPipeStats.sessions);
                        Interlocked.Increment(ref sessionEvents);
                    }
                }
                if (s != null) SessionsChanged.Set();
            }
            if (s == null)
            {
                if (ok && !stopping && !idleClosing) ReplyBusy(pipe);   // (over the cap: told so, then closed; the client tries again soon)
                else PipeNative.Shut(pipe);
                return;
            }
            Thread t = new Thread(s.Run);
            t.IsBackground = true;
            t.Name = "LGEngine host client";
            s.Reader = t;
            t.Start();
        }

        // An Error "busy" (code Quota) to a client that came while every slot was taken, then the close. Written on a pool thread so that a
        // client that never reads cannot stall the accept loop; at most 8 such replies are in flight (the rest are just closed).
        void ReplyBusy(NamedPipeServerStream pipe)
        {
            if (Interlocked.Increment(ref busyReplies) > 8)
            {
                Interlocked.Decrement(ref busyReplies);
                PipeNative.Shut(pipe);
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    PayloadWriter w = new PayloadWriter();
                    w.I32(WireError.Quota);
                    w.Str("busy");
                    FrameQueue q = new FrameQueue(pipe, delegate { PipeNative.Shut(pipe); });
                    q.Send(GlassFrame.Build(Msg.Error, 0, w.ToArray()));
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    while (!q.Idle && !q.Dead && sw.ElapsedMilliseconds < 300) Thread.Sleep(1);
                }
                catch (Exception) { }
                finally
                {
                    PipeNative.Shut(pipe);
                    Interlocked.Decrement(ref busyReplies);
                }
            });
        }

        void EndSession(Session s)
        {
            lock (gate)
            {
                if (sessions.Remove(s)) Interlocked.Decrement(ref GlassPipeStats.sessions);
            }
            Interlocked.Increment(ref sessionEvents);
            slot.Set();
            SessionsChanged.Set();
        }

        // ---- the render thread's ----

        void CreateOnThread(HostSurface s, uint seq)
        {
            if (s.Closing) return;
            if (stopping)   // (a Create that got in after Stop began: nothing is made, the retire-all has already run or will)
            {
                s.Closing = true;
                s.Owner.Retire(s);
                return;
            }
            string name = null;
            try
            {
                int w = s.Spec.Size.Width, h = s.Spec.Size.Height;
                name = GlassNames.Section(suffix, s.Id);
                s.Section = GlassSection.Create(name, w, h, ++s.Generation);
                s.Mirror = new int[w * h];
                s.State.Contents.Reset(w, h);
                s.State.Props = s.Cur;
                s.Events = new HostEvents(s);
                s.Slot = thread.Open(s.Spec, s.Events, s.State);
                s.Listed = true;
                all.Add(s);
                PayloadWriter p = new PayloadWriter();
                p.U32(s.Cookie);
                p.U32(s.Id);
                p.Str(name);
                p.I32(s.Section.Total);
                p.I32(w);
                p.I32(h);
                s.Owner.Send(Msg.CreateAck, seq, p.ToArray());
                if (stopping) RetireOnThread(s);   // (Stop began meanwhile: released at once, window and section)
            }
            catch (Exception ex)
            {
                s.Closing = true;
                s.Owner.Retire(s);
                if (s.Section != null) { s.Section.Dispose(); s.Section = null; }
                s.Owner.Error(seq, WireError.Internal, "create: " + ex.Message);
            }
        }

        void ResizeOnThread(HostSurface s, int w, int h, uint seq, bool flow)
        {
            if (s.Closing || s.Section == null)
            {
                s.Owner.Error(seq, WireError.NoSurface, "resize: the surface is gone");   // (an answer, so that the client's resize never stays pending)
                return;
            }
            long prevBytes;
            int charged = s.Owner.SetBytes(s, QuotaBytes(w, h), true, out prevBytes);
            if (charged != Session.BytesOk)
            {
                if (charged == Session.BytesOver) s.Owner.Error(seq, WireError.Quota, "more than " + (MaxBytesPerClient >> 20) + " MB of contents");
                else s.Owner.Error(seq, WireError.NoSurface, "resize: the surface is gone");
                return;
            }
            bool done = false;
            try
            {
                string name = GlassNames.Section(suffix, s.Id);
                GlassSection ns = GlassSection.Create(name, w, h, ++s.Generation);
                int[] mirror;
                try
                {
                    mirror = new int[w * h];
                    s.State.Contents.Reset(w, h);
                }
                catch (Exception)
                {
                    ns.Dispose();
                    throw;
                }
                GlassSection old = s.Section;
                s.Section = ns;
                done = true;   // (the new section is the surface's: it counts at its new size)
                s.Mirror = mirror;
                s.LastP = 0;
                s.LastClientVer = -1;
                int was = s.Cur.Size.Width;   // (the width before this resize, for the flow)
                SurfaceProps n = s.Cur.Copy();
                n.Size = new Size(w, h);
                n.SizeVer++;
                if (flow && was > 0)   // (protocol 1.1: the glass flows to the new width, as SetSize(size, contents, flow) does in process)
                {
                    n.FlowFrom = Math.Max(0.86f, Math.Min(1.16f, was / (float)w));
                    n.FlowVer++;
                }
                n.Version = ++s.HostVer;
                s.Cur = n;
                s.State.Props = n;
                old.Dispose();
                PayloadWriter p = new PayloadWriter();
                p.U32(s.Cookie);
                p.U32(s.Id);
                p.Str(name);
                p.I32(ns.Total);
                p.I32(w);
                p.I32(h);
                s.Owner.Send(Msg.CreateAck, seq, p.ToArray());
                if (s.Slot != null) s.Slot.Wake();
            }
            catch (Exception ex)
            {
                if (!done)
                {
                    long ignore;
                    s.Owner.SetBytes(s, prevBytes, false, out ignore);   // (the old section stays: it counts as before)
                }
                s.Owner.Error(seq, WireError.Internal, "resize: " + ex.Message);
            }
        }

        void LookOnThread(HostSurface s, GlassLook look, string profile)
        {
            if (s.Closing) return;
            SurfaceProps n = s.Cur.Copy();
            n.Look = look;
            n.Profile = profile ?? "";
            n.LookVer++;
            n.Version = ++s.HostVer;
            s.Cur = n;
            s.State.Props = n;
            if (s.Slot != null) s.Slot.Wake();
        }

        // Protocol 1.1: a change of the extras, published the way a look is.
        void ExtraOnThread(HostSurface s, Action<SurfaceProps> change)
        {
            if (s.Closing) return;
            SurfaceProps n = s.Cur.Copy();
            change(n);
            n.Version = ++s.HostVer;
            s.Cur = n;
            s.State.Props = n;
            if (s.Slot != null) s.Slot.Wake();
        }

        // A kick to the slosh, only while the surface sloshes at all (the client worked out the push; it may have sent it before SetSlosh took effect).
        void KickOnThread(HostSurface s, float push)
        {
            if (s.Closing) return;
            if (s.Cur.SloshAmount <= 0 || s.Cur.SloshStyle >= 3) return;
            s.State.AddKick(push);
            ExtraOnThread(s, delegate(SurfaceProps n) { n.KickVer++; });
        }

        // Gone at once. The client sends the Cut BEFORE it publishes the state block that says "hidden" (so that no animated hide
        // starts first), and that block (or a later Show) is read by the next pump as usual. (Known: a Show block that the pump read
        // before this ran is overridden by the cut; the client does not send one within the same instant of a cut.)
        void CutOnThread(HostSurface s)
        {
            if (s.Closing) return;
            ExtraOnThread(s, delegate(SurfaceProps n) { n.WantShown = false; n.CutVer++; });
        }

        void RetireOnThread(HostSurface s)
        {
            s.Closing = true;
            if (s.Listed) { all.Remove(s); s.Listed = false; }
            if (s.Slot != null) { thread.CloseSlot(s.Slot); s.Slot = null; }
            if (s.Section != null) { s.Section.Dispose(); s.Section = null; }
        }

        static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }

        static bool In(int v, int lo, int hi) { return v >= lo && v <= hi; }

        static float Bounded(float f, float lo, float hi, float dflt)
        {
            if (!Finite(f)) return dflt;
            return f < lo ? lo : (f > hi ? hi : f);
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        // The render thread, when HostWake was set (before the frames that it may have woken run): each surface's new state block and
        // new contents are copied out of its section.
        void Pump()
        {
            for (int i = 0; i < all.Count; i++) PumpOne(all[i]);
        }

        void PumpOne(HostSurface s)
        {
            if (s.Closing || s.Section == null) return;
            bool changed = false;
            try
            {
                SectionState c;
                if (s.Section.ReadState(out c) && c.Valid == 1 && c.Version != s.LastClientVer)
                {
                    s.LastClientVer = c.Version;
                    SurfaceProps n = s.Cur.Copy();
                    n.WantShown = (c.Flags & GlassSection.FlagShown) != 0;
                    n.PlaceAnimate = (c.Flags & GlassSection.FlagAnimate) != 0;
                    n.Hover = (c.Flags & GlassSection.FlagHover) != 0;
                    n.UseBend = (c.Flags & GlassSection.FlagBend) != 0;
                    n.Place = new Point(Clamp(c.X, -100000, 100000), Clamp(c.Y, -100000, 100000));
                    n.Opacity = Bounded(c.Opacity, 0f, 1f, 1f);
                    Deform d = new Deform();
                    d.ZoomX = Bounded(c.ZoomX, 0f, 8f, 1f);
                    d.ZoomY = Bounded(c.ZoomY, 0f, 8f, 1f);
                    d.LeanX = Bounded(c.LeanX, -8f, 8f, 0f);
                    d.LeanY = Bounded(c.LeanY, -8f, 8f, 0f);
                    d.Swell = Bounded(c.Swell, -8f, 8f, 0f);
                    n.Bend = d;
                    n.MotionStyle = Clamp(c.MotionStyle, 0, 8);
                    n.MotionIn = Clamp(c.MotionIn, 60, 1200);
                    n.MotionOut = Clamp(c.MotionOut, 60, 1200);
                    Rectangle card = new Rectangle(0, 0, s.Section.W, s.Section.H);
                    Rectangle hr = c.Hover;
                    long right = (long)hr.X + hr.Width, bottom = (long)hr.Y + hr.Height;
                    if (hr.Width <= 0 || hr.Height <= 0 || right > int.MaxValue || bottom > int.MaxValue) hr = Rectangle.Empty;
                    else { hr.Intersect(card); if (hr.Width <= 0 || hr.Height <= 0) hr = Rectangle.Empty; }
                    n.HoverRegion = hr;
                    n.CallStamp = c.Stamp;
                    n.Version = ++s.HostVer;
                    s.Cur = n;
                    s.State.Props = n;
                    changed = true;
                }
                Rectangle dirty;
                if (s.Section.ReadContents(s.Mirror, ref s.LastP, out dirty) == 1)
                {
                    s.State.Contents.Put(s.Mirror, s.Section.W, s.Section.H, dirty);
                    changed = true;
                }
            }
            catch (Exception) { }
            // (no Signal: RunDue follows in this same loop iteration)
            if (changed && s.Slot != null) Interlocked.Exchange(ref s.Slot.Woken, 1);
        }
    }
}
