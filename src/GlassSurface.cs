using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace LGEngine
{
    // The public surface API (SERVICE.md sections 2 and 12, steps 5 and 7): what a feature gives the engine to make a piece of glass.
    public enum GlassKind { Card, Lens }

    // Auto and InProcess: the engine runs in this process on a render thread of its own (any thread may call; nothing needs a message
    // loop). UiThread: the step-5 way, kept for comparison: the engine runs on the creating thread, which must pump messages and
    // is the only thread that may call. Host: the calls go over a pipe and shared memory to a GlassHostServer (phase 2); with no
    // host running the service is offline (every surface fails with "host unavailable"), it never throws. Auto stays in-process
    // until phase 2b starts a host on demand.
    public enum GlassMode { Auto, InProcess, Host, UiThread }

    public sealed class GlassConnectOptions
    {
        public GlassMode Mode = GlassMode.Auto;
        public string HostSuffix = "";   // Host mode: a suffix on the pipe / event names, so that a test never meets a real host
        public string HostPath;          // Host / Auto mode: the LGEngineHost.exe to start when no host answers (null = beside this assembly)
    }

    // Everything a surface is made from. Copied by Create: changing a spec afterwards changes nothing.
    public sealed class GlassSpec
    {
        public GlassKind Kind;
        // (the app's notification look: Settings.GlassDark ... GlassIntensity, GlassElevation)
        public GlassLook Look = new GlassLook(78, 35, 70, 70, 30, 8, 100, GlassLook.DefaultElevation);
        public string Profile = "";     // a GlassProfile string or a preset's name; empty = none (today's analytic shoulder)
        public float Scale;             // 0 = the scale of the monitor the surface is placed on
        public Size Size;               // the card, in screen pixels
        public Point Place;             // the card's top left, in screen pixels
        public bool InShots;            // whether the glass shows in screenshots
        public bool SavePower;          // fewer frames on battery / with battery saver
        public int Fps = 15;            // how often the background is re-read (Settings.GlassFps); 0 = frozen
        public bool Adapt = true;       // light glass over light backgrounds, dark over dark
        public bool Hover = true;       // a card swells and its light follows the pointer
        public int AnimStyle = 4;       // Settings.StyleSlideZoom (0 none, 1 fade, 2 slide, 3 zoom, 4 slide + zoom, 5 drop, 6 pop, 7 melt, 8 ripple)
        public int AnimIn = 380, AnimOut = 230, AnimZoom = 10, AnimLiquid = 60, AnimBounce = 45;
        public string Label = "";       // the layer window's title (required; test windows are labelled)
        // Step 6 (in-process modes only; over the pipe both are ignored for now, the host uses the defaults): KeepSpare keeps the map
        // of the other card of an indicator as a spare (a card switch is then cheap); AnimTravel is how far, at 100%, the card slides in from.
        public bool KeepSpare;
        public int AnimTravel = 34;
        // Step 6, in-process modes only (ignored over the pipe: the host always uses false): the exit keeps looking at what is behind
        // the card (a live backdrop, new shading) instead of playing with the picture it has. A notification's leaving needs it.
        public bool PeekOnExit;

        internal GlassSpec Clone() { return (GlassSpec)MemberwiseClone(); }
    }

    public sealed class GlassFailedEventArgs : EventArgs
    {
        public readonly int Code;
        public readonly string Text;
        public GlassFailedEventArgs(int code, string text) { Code = code; Text = text; }
    }

    public sealed class GlassAppearanceEventArgs : EventArgs
    {
        public readonly bool Light;
        public GlassAppearanceEventArgs(bool light) { Light = light; }
    }

    // One piece of glass drawn on the contents that lights up under the pointer (a notification's button or reply box): its
    // shape and where the pointer counts as on it, all in card pixels, and how much of its box is label (which the light leaves
    // alone). The engine runs the light (SetButtons); the owner draws the pieces. In-process modes only.
    public sealed class GlassButton
    {
        public Rectangle Hit;              // where the pointer counts as on it
        public float Cx, Cy, Hx, Hy, Rad;  // a rounded box about (Cx, Cy), Hx by Hy either side, corners Rad round
        public int Style, Mark;            // (which kind of piece, which mark: told apart when the same pieces come again with carry)
        public byte[] Mask;                // box-sized, 255 where a pixel is label or mark; MaskBox is its box (null: nothing is label)
        public Rectangle MaskBox;

        internal GlassButton Copy() { return (GlassButton)MemberwiseClone(); }
    }

    // The runtime tells the surface the glass has gone lighter or darker (called on the frame's thread, from inside the frame).
    internal interface IGlassLightEvents
    {
        void LightMoved(float light);
    }

    // Failed codes.
    public static class GlassFailure
    {
        public const int BadSpec = 1, BadContents = 2, BadArgument = 3, FrameFailed = 4, TransportFailed = 5, Rejected = 6;
    }

    // What the runtime reports back (the surface is the sink). Called on the thread the frame runs on; the sink hops to the
    // client's SynchronizationContext.
    internal interface IGlassEvents
    {
        void Shown();
        void Hidden();
        void AppearanceChanged(bool light);
        void Failed(int code, string text);
        void RequestRejected(int code, string text);   // non-fatal: the host refused a request (a quota); the surface stays as it was
    }

    // What the runtime's frames have done for this showing (reset by each arrival): the app's per-pop-up log line and its time to
    // the first glass on screen. Written by the frame's thread, read by the owner (plain numbers; a late read is a little old).
    internal sealed class SurfaceStats
    {
        public bool Began, HasFirst;          // an arrival has begun / a picture with some opacity has gone to Windows since
        public int BeganTick, FirstTick;      // Environment.TickCount at each
        public int Frames;                    // pictures shaded (as the old glass counted its drawn frames)
        public long FrameTicks;               // what they cost (Stopwatch ticks)
        public int LivePictures, Looks, PromptPeeks, SelfIgnored;
        public long LookTicks, RenderedPx, PresentedPx;
        public int ShadesFull, ShadesInc, ShadesContent, FullPresents, RectPresents;
        public bool LightGoal;
    }

    // The surface's state block, shared with the runtime: the latest properties (a new immutable copy per call; the runtime
    // reads the newest reference), the contents' double buffer and the latency ring. The render thread reads the same.
    internal sealed class SurfaceState
    {
        public volatile SurfaceProps Props;
        public readonly SurfaceContents Contents = new SurfaceContents();
        public readonly GlassLatency Latency = new GlassLatency();
        public int Frames;   // pictures put on screen
        public readonly SurfaceStats Stats = new SurfaceStats();
        long statsAt;
        readonly object kickGate = new object();
        float kickPush;

        // Kicks to the slosh (the push each one gives), taken by the runtime at its next frame.
        public void AddKick(float push)
        {
            lock (kickGate) kickPush += push;
        }

        public float TakeKick()
        {
            lock (kickGate)
            {
                float p = kickPush;
                kickPush = 0f;
                return p;
            }
        }

        // Called by the render thread after a recorded sample: puts the latency summary in EngineStats, at most every 250 ms.
        public void OnSample()
        {
            long now = Stopwatch.GetTimestamp();
            if (statsAt != 0 && now - statsAt < Stopwatch.Frequency / 4) return;
            statsAt = now;
            Latency.Publish();
        }
    }

    // Immutable once published: GlassSurface copies the last one, changes the copy, stamps it and publishes it (copy-on-write,
    // the copy-modify-publish under one lock so that concurrent setters never lose an update).
    internal sealed class SurfaceProps
    {
        public int Version, LookVer, SizeVer;
        public long CallStamp;   // Stopwatch ticks at the call that made this copy
        public Point Place;
        public bool PlaceAnimate;
        public Size Size;
        public float Opacity = 1f;
        public Deform Bend = Deform.None;
        public bool UseBend;
        public int MotionStyle, MotionIn, MotionOut;
        public bool Hover;
        public Rectangle HoverRegion;   // in card px; empty = the whole card
        public GlassLook Look;
        public string Profile = "";
        public bool WantShown;

        // Step 6 (in-process only). -1 / false / null = "as the spec says": a props made elsewhere (the pipe's) keeps today's behaviour.
        public int MotionZoom = -1, MotionLiquid = -1, MotionBounce = -1, MotionTravel = -1, OsdStyle = -1;
        public int Fps = -1, Adapt = -1, SavePower = -1;   // (Adapt, SavePower: 0 / 1)
        public bool HasPointer;
        public Rectangle Pointer;       // card px, may reach past the card
        public int SloshStyle, SloshAmount;
        public int KickVer, CutVer, FlowVer;
        public float FlowFrom = 1f;
        public bool Busy;
        public GlassButton[] Buttons;
        public int ButtonsVer;
        public bool ButtonsCarry;

        public SurfaceProps Copy() { return (SurfaceProps)MemberwiseClone(); }
    }

    // The contents, premultiplied ARGB (card colour removed), card-sized: copied in, never held by reference. Two buffers: the
    // writer only ever writes the back one; the reader (the runtime) takes the newest picture with Front(), which swaps under the
    // lock, and then owns the front buffer until its next Front(). So two Puts in one frame never touch the buffer being read.
    // Dirty forwarding (HANDOFF 4.5): each buffer remembers the rectangle it still lacks of the newest picture (every Put adds its
    // dirty rect to both; writing a buffer fills in what it lacks, from the caller's full picture). After a Reset both lack all of it.
    internal sealed class SurfaceContents
    {
        readonly object gate = new object();
        int[] front = new int[0], back = new int[0];
        int w, h, version;
        bool backFresh;                  // back holds a newer picture than front
        Rectangle frontLacks, backLacks;
        Rectangle pubDirty;              // what has changed since the last Take (a reader that forwards the picture elsewhere)

        // Fresh zeroed buffers of a new size. Throws (OutOfMemoryException) before anything has changed.
        public void Reset(int width, int height)
        {
            int[] a = new int[width * height];
            int[] b = new int[width * height];
            Rectangle all = new Rectangle(0, 0, width, height);
            lock (gate)
            {
                w = width;
                h = height;
                front = a;
                back = b;
                backFresh = false;
                frontLacks = backLacks = all;
                pubDirty = all;
                version++;
            }
        }

        // A new size and its picture in one step (no moment with the new size and blank contents): both buffers hold the picture.
        public void Replace(int[] src, int width, int height)
        {
            int[] a = new int[width * height];
            int[] b = new int[width * height];
            Array.Copy(src, a, a.Length);
            Array.Copy(src, b, b.Length);
            lock (gate)
            {
                w = width;
                h = height;
                front = a;
                back = b;
                backFresh = false;
                frontLacks = backLacks = Rectangle.Empty;
                pubDirty = new Rectangle(0, 0, width, height);
                version++;
            }
        }

        static Rectangle Grow(Rectangle a, Rectangle b)
        {
            if (a.Width <= 0 || a.Height <= 0) return b;
            return Rectangle.Union(a, b);
        }

        // src is width x height (the caller checked it holds that many pixels), dirty inside it. False: the card is not that size.
        public bool Put(int[] src, int width, int height, Rectangle dirty)
        {
            lock (gate)
            {
                if (width != w || height != h) return false;
                frontLacks = Grow(frontLacks, dirty);
                backLacks = Grow(backLacks, dirty);
                Rectangle copy = backLacks;
                copy.Intersect(new Rectangle(0, 0, w, h));
                for (int y = copy.Top; y < copy.Bottom; y++) Array.Copy(src, y * w + copy.Left, back, y * w + copy.Left, copy.Width);
                backLacks = Rectangle.Empty;
                backFresh = true;
                pubDirty = Grow(pubDirty, dirty);
                version++;
                return true;
            }
        }

        // Front() for the one reader that sends the picture to another process (the pipe transport): also says what has changed
        // since its last Take. (The render-thread and UI-thread transports never Take.)
        public int[] Take(out int width, out int height, out int ver, out Rectangle dirty)
        {
            lock (gate)
            {
                int[] f = Front(out width, out height, out ver);
                dirty = pubDirty;
                pubDirty = Rectangle.Empty;
                return f;
            }
        }

        // The newest picture. The reader may use the returned array until its next Front().
        public int[] Front(out int width, out int height, out int ver)
        {
            lock (gate)
            {
                if (backFresh)
                {
                    int[] t = front;
                    front = back;
                    back = t;
                    Rectangle l = frontLacks;
                    frontLacks = backLacks;
                    backLacks = l;
                    backFresh = false;
                }
                width = w;
                height = h;
                ver = version;
                return front;
            }
        }
    }

    // One piece of glass. Every call returns at once and never throws: it copies its argument into the surface's state, stamps
    // the time, and wakes the transport. Any thread may call (with the default render-thread transport). Bad input raises Failed
    // once; a failed surface (and a disposed one) is silent from then on. The one exception: a value outside the limits the host
    // enforces (GlassWire.Check*: scale, label, profile length, elevation, place) is an ArgumentException from SetLook / SetPlace / Create, in every mode.
    //
    // Events (Shown, Hidden, AppearanceChanged, Failed): raised by Post on the SynchronizationContext that was current on the
    // thread that called GlassService.Create (a WinForms or WPF thread gets them on its own thread); when there was none, they
    // are raised on the engine's render thread, so a handler there must be quick. A handler may call back into the surface
    // (Dispose inside Failed, Set* inside Shown) from either thread.
    public sealed class GlassSurface : IDisposable
    {
        const int MaxSide = 4096;

        readonly GlassService owner;
        readonly IGlassTransport transport;
        readonly GlassSpec spec;
        readonly SynchronizationContext context;
        readonly SurfaceState state = new SurfaceState();
        readonly Sink sink;
        readonly object propGate = new object();   // copy-modify-publish of the properties; Dispose
        readonly object failGate = new object();   // the Failed handlers and the undelivered failure
        volatile IGlassChannel channel;
        volatile bool failed, disposed;
        EventHandler<GlassFailedEventArgs> failedHandlers;
        GlassFailedEventArgs undelivered;   // a failure with nobody listening yet: handed to the first listener (so a bad spec is not lost)
        bool failedDelivered;
        readonly System.Collections.Generic.List<Size> sizes = new System.Collections.Generic.List<Size>();   // the card sizes this surface has had (propGate): a picture of an earlier one lost a race with SetSize and is dropped

        public event EventHandler Shown;
        public event EventHandler Hidden;
        public event EventHandler<GlassAppearanceEventArgs> AppearanceChanged;
        // A request the host refused without failing the surface (a resize over the client's quota): the surface stays Shown at its last
        // size and contents. Raised like the other events. Never raised by the in-process modes (no quota there).
        public event EventHandler<GlassFailedEventArgs> RequestRejected;

        // How light the glass is now, 0 (dark) to 1 (light), eased as the glass turns (the map's own appearance); and an event when it
        // changes, raised like the others. Never raised over the pipe (and Light stays at its start value there).
        volatile float light = 0f;
        public float Light { get { return light; } }
        public event Action<float> LightMoved;
        Action<float> beforeRender;

        // In-process modes only (NotSupportedException over the pipe): called on the frame's thread, inside the frame, the moment the
        // glass has turned lighter or darker and before it is drawn; a handler may call SetContents, which then is in that same
        // frame (an owner that draws to suit the light, such as the indicator's knob, stays exact through the ease). Keep it quick.
        public event Action<float> BeforeRender
        {
            add
            {
                NeedLocal("BeforeRender");
                lock (propGate) beforeRender += value;
            }
            remove { lock (propGate) beforeRender -= value; }
        }

        public event EventHandler<GlassFailedEventArgs> Failed
        {
            add
            {
                GlassFailedEventArgs late = null;
                lock (failGate)
                {
                    failedHandlers += value;
                    if (undelivered != null && !failedDelivered)
                    {
                        late = undelivered;
                        undelivered = null;
                        failedDelivered = true;
                    }
                }
                if (late != null)
                {
                    try { value(this, late); } catch (Exception) { }
                }
            }
            remove
            {
                lock (failGate) failedHandlers -= value;
            }
        }

        public bool HasFailed { get { return failed; } }
        public bool IsDisposed { get { return disposed; } }
        public string Label { get { return spec.Label; } }
        public int Frames
        {
            get
            {
                IGlassStats remote = channel as IGlassStats;
                return remote != null ? remote.FramesNow() : Thread.VolatileRead(ref state.Frames);
            }
        }

        internal SurfaceStats Stats { get { return state.Stats; } }   // (in-process only; the app is built with the engine)

        public GlassLatencyInfo Latency
        {
            get
            {
                IGlassStats remote = channel as IGlassStats;
                if (remote != null) return remote.LatencyNow();
                state.Latency.Publish();
                return state.Latency.Snapshot();
            }
        }

        internal GlassSurface(GlassService owner, IGlassTransport transport, GlassSpec spec, SynchronizationContext context)
        {
            this.owner = owner;
            this.transport = transport;
            this.spec = spec;
            this.context = context;
            sink = new Sink(this);
            int code;
            string text = CheckSpec(spec, out code);
            if (text == null && spec.Scale == 0) spec.Scale = MonitorScale(spec.Place);
            Size size = text == null ? spec.Size : new Size(1, 1);
            SurfaceProps p = new SurfaceProps();
            p.Place = spec.Place;
            p.Size = size;
            p.Look = spec.Look;
            p.Profile = spec.Profile ?? "";
            p.Hover = spec.Hover;
            p.MotionStyle = spec.AnimStyle;
            p.MotionIn = spec.AnimIn;
            p.MotionOut = spec.AnimOut;
            state.Props = p;
            sizes.Add(size);
            if (text == null)
            {
                try { state.Contents.Reset(size.Width, size.Height); }
                catch (Exception ex)
                {
                    text = "contents: could not allocate " + size.Width + "x" + size.Height + " (" + ex.Message + ")";
                    code = GlassFailure.BadContents;
                }
            }
            if (text != null)
            {
                try { state.Contents.Reset(1, 1); } catch (Exception) { }
                RaiseFailed(code, text);
            }
            else
            {
                try { channel = transport.Open(spec, sink, state); }
                catch (Exception ex) { RaiseFailed(GlassFailure.TransportFailed, "the transport would not open the surface: " + ex.Message); }
            }
        }

        // ---- the calls ----

        // The contents: premultiplied ARGB, card colour removed, exactly the card's size (what GlassMap.Shade takes). dirty: the
        // part that changed since the last call (an empty rectangle: all of it).
        public void SetContents(int[] premultiplied, int w, int h, Rectangle dirty)
        {
            long stamp = Stopwatch.GetTimestamp();
            if (!Usable()) return;
            try
            {
                if (premultiplied == null) { RaiseFailed(GlassFailure.BadContents, "contents: null"); return; }
                if (w <= 0 || h <= 0 || w > MaxSide || h > MaxSide) { RaiseFailed(GlassFailure.BadContents, "contents: " + w + "x" + h + " is empty or over " + MaxSide); return; }
                SurfaceProps seen = state.Props;
                Size card = seen.Size;
                if ((w != card.Width || h != card.Height) && WasSize(w, h)) return;   // (a SetSize won the race)
                if (w != card.Width || h != card.Height) { RaiseFailed(GlassFailure.BadContents, "contents: " + w + "x" + h + " does not match the card " + card.Width + "x" + card.Height); return; }
                if (premultiplied.Length < w * h) { RaiseFailed(GlassFailure.BadContents, "contents: the array holds " + premultiplied.Length + " pixels, " + (w * h) + " needed"); return; }
                if (dirty.Width <= 0 || dirty.Height <= 0) dirty = new Rectangle(0, 0, w, h);
                else if (dirty.Left < 0 || dirty.Top < 0 || dirty.Right > w || dirty.Bottom > h) { RaiseFailed(GlassFailure.BadContents, "contents: the dirty rectangle " + dirty + " is outside the card"); return; }
                IGlassChannel ch;
                lock (propGate)
                {
                    ch = channel;
                    if (disposed || failed || ch == null) return;
                    // (a SetSize that won the race makes this picture stale: dropped, not a failure)
                    if (state.Props.SizeVer != seen.SizeVer || !state.Contents.Put(premultiplied, w, h, dirty)) return;
                    PublishLocked(stamp, delegate(SurfaceProps n) { });
                }
                WakeChannel(ch);
            }
            catch (Exception ex) { RaiseFailed(GlassFailure.BadContents, "contents: " + ex.Message); }
        }

        // The card's top left, in screen pixels. animate: glide there instead of jumping.
        public void SetPlace(Point place, bool animate)
        {
            long stamp = Stopwatch.GetTimestamp();
            if (!Usable()) return;
            if (!GlassWire.PlaceOk(place)) throw new ArgumentException("place: outside +-100000", "place");
            Publish(stamp, delegate(SurfaceProps n) { n.Place = place; n.PlaceAnimate = animate; });
        }

        // A new card size: the old contents are dropped (zeros) until SetContents brings the new ones.
        public void SetSize(Size size)
        {
            long stamp = Stopwatch.GetTimestamp();
            if (!Usable()) return;
            if (size.Width <= 0 || size.Height <= 0 || size.Width > MaxSide || size.Height > MaxSide) { RaiseFailed(GlassFailure.BadArgument, "size: " + size.Width + "x" + size.Height + " is not 1.." + MaxSide); return; }
            string bad = null;
            IGlassChannel ch;
            lock (propGate)
            {
                ch = channel;
                if (disposed || ch == null) return;
                try { state.Contents.Reset(size.Width, size.Height); }   // (with the publish: contents and props never differ in size)
                catch (Exception ex) { bad = "contents: could not allocate " + size.Width + "x" + size.Height + " (" + ex.Message + ")"; }
                if (bad == null)
                {
                    if (sizes.Count >= 16) sizes.RemoveAt(0);
                    sizes.Add(size);
                }
                if (bad == null) PublishLocked(stamp, delegate(SurfaceProps n) { n.Size = size; n.SizeVer++; });
            }
            if (bad != null) { RaiseFailed(GlassFailure.BadContents, bad); return; }
            WakeChannel(ch);
        }

        public void SetOpacity(float opacity)
        {
            long stamp = Stopwatch.GetTimestamp();
            if (!Usable()) return;
            if (float.IsNaN(opacity) || float.IsInfinity(opacity)) { RaiseFailed(GlassFailure.BadArgument, "opacity: not a number"); return; }
            float o = Math.Max(0f, Math.Min(1f, opacity));
            Publish(stamp, delegate(SurfaceProps n) { n.Opacity = o; });
        }

        // Used in place of the animator's own bend while it is not Deform.None (a slosh, a morph, an owner's own animation).
        public void SetDeform(Deform deform)
        {
            long stamp = Stopwatch.GetTimestamp();
            if (!Usable()) return;
            if (!Finite(deform.ZoomX) || !Finite(deform.ZoomY) || !Finite(deform.LeanX) || !Finite(deform.LeanY) || !Finite(deform.Swell)) { RaiseFailed(GlassFailure.BadArgument, "deform: not a number"); return; }
            Publish(stamp, delegate(SurfaceProps n) { n.Bend = deform; n.UseBend = !deform.IsNone; });
        }

        // The arrival and leaving: Settings' style number (0..8) and the two durations (clamped to 60..1200 ms).
        public void SetMotion(int style, int inMs, int outMs)
        {
            long stamp = Stopwatch.GetTimestamp();
            if (!Usable()) return;
            int s = Math.Max(0, Math.Min(8, style)), i = Math.Max(60, Math.Min(1200, inMs)), o = Math.Max(60, Math.Min(1200, outMs));
            Publish(stamp, delegate(SurfaceProps n) { n.MotionStyle = s; n.MotionIn = i; n.MotionOut = o; });
        }

        // The whole card reacts to the pointer (or not).
        public void SetInteraction(bool hover)
        {
            SetInteraction(hover, Rectangle.Empty);
        }

        // hover: the card swells and its light follows the pointer, but only while the pointer is inside region (card pixels;
        // empty = the whole card).
        public void SetInteraction(bool hover, Rectangle region)
        {
            long stamp = Stopwatch.GetTimestamp();
            if (!Usable()) return;
            if (region.Width <= 0 || region.Height <= 0) region = Rectangle.Empty;
            Publish(stamp, delegate(SurfaceProps n) { n.Hover = hover; n.HoverRegion = region; });
        }

        // A new look (sliders clamped to their ranges) and profile (a string or a preset's name; empty = none).
        public void SetLook(GlassLook look, string profile)
        {
            long stamp = Stopwatch.GetTimestamp();
            if (!Usable()) return;
            string range = GlassWire.CheckLookValues(look, profile);   // (the host's limits: the same call it makes)
            if (range != null) throw new ArgumentException(range, "look");
            if (!ProfileOk(profile)) { RaiseFailed(GlassFailure.BadSpec, "profile: not a GlassProfile string or a preset's name"); return; }
            GlassLook l = Clamp(look);
            string pr = profile ?? "";
            Publish(stamp, delegate(SurfaceProps n) { n.Look = l; n.Profile = pr; n.LookVer++; });
        }

        public void Show()
        {
            long stamp = Stopwatch.GetTimestamp();
            if (!Usable()) return;
            Publish(stamp, delegate(SurfaceProps n) { n.WantShown = true; });
        }

        public void Hide()
        {
            long stamp = Stopwatch.GetTimestamp();
            if (!Usable()) return;
            Publish(stamp, delegate(SurfaceProps n) { n.WantShown = false; });
        }

        // ---- step 6: what the app's own glass needs (in-process modes only: each of these throws NotSupportedException over the pipe) ----

        // A new card size and its picture in one step: the glass never shows the new size with blank contents. flow: the glass
        // flows to the new width (another indicator taking over: clamp 0.86..1.16, 260 ms, only once fully in; a lens only).
        public void SetSize(Size size, int[] premultiplied, bool flow)
        {
            long stamp = Stopwatch.GetTimestamp();
            NeedLocal("SetSize(size, contents, flow)");
            if (!Usable()) return;
            if (size.Width <= 0 || size.Height <= 0 || size.Width > MaxSide || size.Height > MaxSide) { RaiseFailed(GlassFailure.BadArgument, "size: " + size.Width + "x" + size.Height + " is not 1.." + MaxSide); return; }
            if (premultiplied == null || premultiplied.Length < size.Width * size.Height) { RaiseFailed(GlassFailure.BadContents, "contents: the array holds " + (premultiplied == null ? 0 : premultiplied.Length) + " pixels, " + (size.Width * size.Height) + " needed"); return; }
            string bad = null;
            IGlassChannel ch;
            lock (propGate)
            {
                ch = channel;
                if (disposed || ch == null) return;
                try { state.Contents.Replace(premultiplied, size.Width, size.Height); }
                catch (Exception ex) { bad = "contents: could not allocate " + size.Width + "x" + size.Height + " (" + ex.Message + ")"; }
                if (bad == null)
                {
                    if (sizes.Count >= 16) sizes.RemoveAt(0);
                    sizes.Add(size);
                    int was = state.Props.Size.Width;
                    PublishLocked(stamp, delegate(SurfaceProps n)
                    {
                        n.Size = size;
                        n.SizeVer++;
                        if (flow && was > 0)
                        {
                            n.FlowFrom = Math.Max(0.86f, Math.Min(1.16f, was / (float)size.Width));
                            n.FlowVer++;
                        }
                    });
                }
            }
            if (bad != null) { RaiseFailed(GlassFailure.BadContents, bad); return; }
            WakeChannel(ch);
        }

        // animate false: gone at once, no exit (the owner is closing it). Hide(true) is Hide().
        public void Hide(bool animate)
        {
            if (animate) { Hide(); return; }
            long stamp = Stopwatch.GetTimestamp();
            NeedLocal("Hide(false)");
            if (!Usable()) return;
            Publish(stamp, delegate(SurfaceProps n) { n.WantShown = false; n.CutVer++; });
        }

        // The arrival and leaving with every number of Settings' motion page (zoom, liquid, bounce 0..100 / 0..50, travel in points at 100%).
        public void SetMotion(int style, int inMs, int outMs, int zoom, int liquid, int bounce, int travel)
        {
            SetMotion(style, inMs, outMs, zoom, liquid, bounce, travel, -1);
        }

        // indicatorStyle: a lens's own appearance style (0..8), or -1 for the same as the card's.
        public void SetMotion(int style, int inMs, int outMs, int zoom, int liquid, int bounce, int travel, int indicatorStyle)
        {
            long stamp = Stopwatch.GetTimestamp();
            NeedLocal("SetMotion(7)");
            if (!Usable()) return;
            int s = Math.Max(0, Math.Min(8, style)), i = Math.Max(60, Math.Min(1200, inMs)), o = Math.Max(60, Math.Min(1200, outMs));
            int z = Math.Max(0, Math.Min(50, zoom)), l = Math.Max(0, Math.Min(100, liquid)), b = Math.Max(0, Math.Min(100, bounce)), t = Math.Max(0, Math.Min(160, travel));
            int ind = indicatorStyle < 0 ? -1 : Math.Min(8, indicatorStyle);
            Publish(stamp, delegate(SurfaceProps n) { n.MotionStyle = s; n.MotionIn = i; n.MotionOut = o; n.MotionZoom = z; n.MotionLiquid = l; n.MotionBounce = b; n.MotionTravel = t; n.OsdStyle = ind; });
        }

        // How often the background is looked at, whether the glass adapts to it, and whether it saves power: live (the spec's start values).
        public void SetUpdates(int fps, bool adapt, bool savePower)
        {
            long stamp = Stopwatch.GetTimestamp();
            NeedLocal("SetUpdates");
            if (!Usable()) return;
            int f = Math.Max(0, Math.Min(60, fps));
            Publish(stamp, delegate(SurfaceProps n) { n.Fps = f; n.Adapt = adapt ? 1 : 0; n.SavePower = savePower ? 1 : 0; });
        }

        // Where the pointer counts as on the card (hover swell, the buttons' light), in card pixels; it may reach past the card (a
        // notification's whole window), and its top left may be negative. Empty: the card.
        public void SetPointerArea(Rectangle area)
        {
            long stamp = Stopwatch.GetTimestamp();
            NeedLocal("SetPointerArea");
            if (!Usable()) return;
            bool has = area.Width > 0 && area.Height > 0;
            Publish(stamp, delegate(SurfaceProps n) { n.HasPointer = has; n.Pointer = has ? area : Rectangle.Empty; });
        }

        // How a lens moves when its level changes: style 0 lean, 1 bounce, 2 pulse, 3 none; amount 0..100 (0: still).
        public void SetSlosh(int style, int amount)
        {
            long stamp = Stopwatch.GetTimestamp();
            NeedLocal("SetSlosh");
            if (!Usable()) return;
            int s = Math.Max(0, Math.Min(3, style)), a = Math.Max(0, Math.Min(100, amount));
            Publish(stamp, delegate(SurfaceProps n) { n.SloshStyle = s; n.SloshAmount = a; });
        }

        // The level changed the given way (+1 up, -1 down): a kick to the slosh (SetSlosh says how it shows).
        public void Kick(int way)
        {
            long stamp = Stopwatch.GetTimestamp();
            NeedLocal("Kick");
            if (!Usable()) return;
            SurfaceProps p = state.Props;
            if (p.SloshAmount <= 0 || p.SloshStyle >= 3) return;
            if (p.SloshStyle == 2) way = 1;   // (a pulse swells the same whichever way the level went)
            state.AddKick(way * 4.4f * p.SloshAmount / 60f);   // (4.4: the lean per second a kick gives; 60 is the long-standing amount)
            Publish(stamp, delegate(SurfaceProps n) { n.KickVer++; });
        }

        // The level is on the move (the owner animates it): the glass keeps looking behind itself as it does for a slosh.
        public void SetBusy(bool busy)
        {
            long stamp = Stopwatch.GetTimestamp();
            NeedLocal("SetBusy");
            if (!Usable()) return;
            Publish(stamp, delegate(SurfaceProps n) { n.Busy = busy; });
        }

        // The glass pieces drawn on the contents that light up under the pointer (null or none: no light). carry: these are the same
        // pieces as before, changed a little, so what was lit stays lit. Call before the SetContents they belong to.
        public void SetButtons(GlassButton[] buttons, bool carry)
        {
            long stamp = Stopwatch.GetTimestamp();
            NeedLocal("SetButtons");
            if (!Usable()) return;
            GlassButton[] copy = null;
            if (buttons != null && buttons.Length > 0)
            {
                copy = new GlassButton[buttons.Length];
                for (int i = 0; i < copy.Length; i++)
                {
                    if (buttons[i] == null) { RaiseFailed(GlassFailure.BadArgument, "buttons: null entry"); return; }
                    copy[i] = buttons[i].Copy();
                }
            }
            Publish(stamp, delegate(SurfaceProps n) { n.Buttons = copy; n.ButtonsVer++; n.ButtonsCarry = carry; });
        }

        void NeedLocal(string what)
        {
            if (channel is IGlassStats) throw new NotSupportedException(what + " is not supported over the pipe");
        }

        public void Dispose()
        {
            IGlassChannel ch;
            lock (propGate)
            {
                if (disposed) return;
                disposed = true;
                ch = channel;
                channel = null;
            }
            if (ch != null)
            {
                try { transport.Close(ch); } catch (Exception) { }
            }
            owner.Forget(this);
        }

        // ---- inside ----

        // The service was disposed while this surface was being made: it never lived (failed, then closed).
        internal void FailLate(string text)
        {
            RaiseFailed(GlassFailure.TransportFailed, text);
            Dispose();
        }

        bool WasSize(int w, int h)
        {
            lock (propGate) return sizes.Contains(new Size(w, h));
        }

        bool Usable() { return !disposed && !failed && channel != null; }

        // Copy the newest properties, let the caller change the copy, stamp it, publish it: one step under a lock, so two
        // threads setting different things never lose each other's change. Then wake the transport (outside the lock).
        void Publish(long stamp, Action<SurfaceProps> change)
        {
            IGlassChannel ch;
            lock (propGate)
            {
                ch = channel;
                if (disposed || ch == null) return;
                PublishLocked(stamp, change);
            }
            WakeChannel(ch);
        }

        // (propGate held)
        void PublishLocked(long stamp, Action<SurfaceProps> change)
        {
            SurfaceProps n = state.Props.Copy();
            change(n);
            n.Version++;
            n.CallStamp = stamp;
            state.Props = n;
        }

        void WakeChannel(IGlassChannel ch)
        {
            try { transport.Wake(ch); }
            catch (Exception ex) { RaiseFailed(GlassFailure.TransportFailed, "wake: " + ex.Message); }
        }

        // Runs a (client-facing) handler call where the events go: on the client's context, else on the render thread.
        void Deliver(Action action)
        {
            if (context != null)
            {
                try { context.Post(delegate { action(); }, null); return; }
                catch (Exception) { }   // (a dead context: fall through to the engine's thread)
            }
            bool posted = false;
            try { posted = transport.Post(action); }
            catch (Exception) { }
            if (!posted) action();
        }

        void RaiseFailed(int code, string text)
        {
            lock (propGate)
            {
                if (failed || disposed) return;
                failed = true;
            }
            IGlassChannel ch = channel;
            if (ch != null)
            {
                try { ch.Stop(); } catch (Exception) { }   // (posted to the render thread by its channel)
            }
            GlassFailedEventArgs args = new GlassFailedEventArgs(code, text);
            Deliver(delegate
            {
                EventHandler<GlassFailedEventArgs> h;
                lock (failGate)
                {
                    if (failedDelivered) return;
                    h = failedHandlers;
                    if (h == null) { undelivered = args; return; }
                    failedDelivered = true;
                }
                try { h(this, args); } catch (Exception) { }
            });
        }

        static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }

        static GlassLook Clamp(GlassLook l)
        {
            return new GlassLook(Math.Max(0, Math.Min(100, l.Dark)), Math.Max(0, Math.Min(100, l.Blur)), Math.Max(0, Math.Min(100, l.Refraction)),
                Math.Max(0, Math.Min(100, l.Rim)), Math.Max(0, Math.Min(100, l.Shadow)), Math.Max(0, Math.Min(28, l.Round)), Math.Max(0, Math.Min(150, l.Intensity)), Math.Max(0, Math.Min(100, l.Elevation)));
        }

        // Empty, a preset's name or a profile string that parses. (Anything else is garbage.)
        internal static bool ProfileOk(string text)
        {
            if (text == null) return true;
            text = text.Trim();
            if (text.Length == 0) return true;
            foreach (string name in GlassProfile.PresetNames) if (name == text) return true;
            return GlassProfile.Parse(text) != null;
        }

        static string CheckSpec(GlassSpec s, out int code)
        {
            code = GlassFailure.BadSpec;
            if (s.Kind != GlassKind.Card && s.Kind != GlassKind.Lens) return "spec: unknown kind";
            if (s.Size.Width <= 0 || s.Size.Height <= 0 || s.Size.Width > MaxSide || s.Size.Height > MaxSide) return "spec: size " + s.Size.Width + "x" + s.Size.Height + " is not 1.." + MaxSide;
            if (!GlassWire.ScaleOk(s.Scale)) return "spec: scale is not 0 (the monitor's) or 0.01..8";
            if (!ProfileOk(s.Profile)) return "spec: profile is not a GlassProfile string or a preset's name";
            s.Look = Clamp(s.Look);
            s.Fps = Math.Max(0, Math.Min(60, s.Fps));
            s.AnimStyle = Math.Max(0, Math.Min(8, s.AnimStyle));
            s.AnimIn = Math.Max(60, Math.Min(1200, s.AnimIn));
            s.AnimOut = Math.Max(60, Math.Min(1200, s.AnimOut));
            s.AnimZoom = Math.Max(0, Math.Min(50, s.AnimZoom));
            s.AnimLiquid = Math.Max(0, Math.Min(100, s.AnimLiquid));
            s.AnimBounce = Math.Max(0, Math.Min(100, s.AnimBounce));
            s.AnimTravel = Math.Max(0, Math.Min(160, s.AnimTravel));
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PT { public int X, Y; }

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromPoint(PT pt, uint flags);

        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

        // The display scale of the monitor nearest to the place (1 = 96 dpi).
        static float MonitorScale(Point at)
        {
            try
            {
                PT pt;
                pt.X = at.X;
                pt.Y = at.Y;
                uint dx, dy;
                IntPtr mon = MonitorFromPoint(pt, 2);   // (MONITOR_DEFAULTTONEAREST)
                if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) return Math.Max(1f, Math.Min(4f, dx / 96f));
            }
            catch (Exception) { }
            return 1f;
        }

        // The events from the runtime (called on the frame's thread; handed to the client's context; nothing after Dispose).
        sealed class Sink : IGlassEvents, IGlassLightEvents
        {
            readonly GlassSurface s;
            public Sink(GlassSurface surface) { s = surface; }

            public void LightMoved(float value)
            {
                s.light = value;
                Action<float> before = s.beforeRender;
                if (before != null)
                {
                    try { before(value); } catch (Exception) { }
                }
                s.Deliver(delegate { if (s.disposed) return; Action<float> h = s.LightMoved; if (h != null) try { h(value); } catch (Exception) { } });
            }

            public void Shown()
            {
                s.Deliver(delegate { if (s.disposed) return; EventHandler h = s.Shown; if (h != null) try { h(s, EventArgs.Empty); } catch (Exception) { } });
            }

            public void Hidden()
            {
                s.Deliver(delegate { if (s.disposed) return; EventHandler h = s.Hidden; if (h != null) try { h(s, EventArgs.Empty); } catch (Exception) { } });
            }

            public void AppearanceChanged(bool light)
            {
                s.Deliver(delegate { if (s.disposed) return; EventHandler<GlassAppearanceEventArgs> h = s.AppearanceChanged; if (h != null) try { h(s, new GlassAppearanceEventArgs(light)); } catch (Exception) { } });
            }

            public void Failed(int code, string text) { s.RaiseFailed(code, text); }

            public void RequestRejected(int code, string text)
            {
                s.Deliver(delegate { if (s.disposed) return; EventHandler<GlassFailedEventArgs> h = s.RequestRejected; if (h != null) try { h(s, new GlassFailedEventArgs(code, text)); } catch (Exception) { } });
            }
        }
    }
}
