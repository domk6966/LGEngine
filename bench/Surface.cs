using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using LGEngine;

namespace LGBench
{
    // -surface: the app's own glass (an indicator, a notification) through the public surface API against the direct path.
    //   (A) a presenter, animator and glow driven as the app's GlassOverlay drives them: the card inside a bigger window picture.
    //   (B) a SurfaceRuntime in the UI-thread mode, behind a GlassSurface, with the contents cropped to the card.
    // Both read the same injected backdrop (GlassPeek.TestScreen) and the same clock (ScreenFeed.TestFine, moved by hand), and are
    // compared pixel for pixel after every frame: the picture the presenter holds, the bent one, and where and how it is shown.
    // (The glow and the animator are the same classes on both sides: what is proved is the surface wiring, not their sums.)
    internal static class SurfaceBench
    {
        const string Wallpaper = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";

        sealed class Group
        {
            public string Name;
            public int N, Bad, Max;
            public Group(string name) { Name = name; }
        }

        static readonly Group Rest = new Group("rest");
        static readonly Group Mid = new Group("mid-animation");
        static readonly Group Api = new Group("new API");
        static int shown;

        static void Note(Group g, bool ok, int diff, string text)
        {
            g.N++;
            if (diff > g.Max) g.Max = diff;
            if (!ok)
            {
                g.Bad++;
                if (shown++ < 12) Program.Say("   surface FAIL [{0}] {1}", g.Name, text);
            }
        }

        // ---- the two sides ----

        interface ISide : IDisposable
        {
            void Show();
            void Hide(bool animate);
            void Resize(Size size, int[] card, bool flow);
            void Put(int[] card);
            void Kick(int way);
            void Slosh(int style, int amount);
            void Interact(bool hover);
            void Pointer(Rectangle area);
            void Buttons(GlassButton[] buttons, bool carry);
            void Updates(bool adapt);
            void Frame();
            GlassPresenter Presenter { get; }
            float Vis { get; }
            bool Moving { get; }
            bool Sloshing { get; }
        }

        // (A) as GlassOverlay.Frame: the window is the card and a margin of its own, the contents are the window's picture.
        sealed class DirectSide : ISide
        {
            const float SloshSpring = 15f, SloshKick = 4.4f, SloshMost = 0.16f;
            const int MorphMs = 260;

            readonly GlassSpec spec;
            readonly bool lens;
            readonly float scale;
            readonly GlassLayer layer;
            readonly GlassPeek peek;
            readonly GlassPresenter presenter;
            readonly GlassAnimator anim = new GlassAnimator();
            readonly GlassGlow glow;
            readonly GlassPeekInput peekIn = new GlassPeekInput();
            readonly GlassAnimateInput animIn = new GlassAnimateInput();
            readonly GlassPresentInput presentIn = new GlassPresentInput();
            readonly GlassLook look;
            readonly GlassShape shape;
            readonly Point off;                 // the card in the window
            readonly Point home;                // the card on the screen
            Size size;
            Rectangle card;
            int[] win;
            int winW, winH;
            bool dirty = true, wantShown, rested = true, cutPending, hover = true, adapt = true, moving;
            Rectangle pointer;
            bool hasPointer;
            float slosh, sloshSpeed, morphFrom = 1f, kickPending, flowFrom = 1f;
            long sloshFine, morphAt;
            bool flowPending, buttonsNew, buttonsCarry;
            GlassButton[] buttons;
            int sloshStyle, sloshAmount;

            public DirectSide(GlassSpec spec, Point off)
            {
                this.spec = spec;
                this.off = off;
                lens = spec.Kind == GlassKind.Lens;
                scale = spec.Scale;
                home = spec.Place;
                look = spec.Look;
                shape = lens ? GlassShape.Lens : GlassShape.Card;
                layer = new GlassLayer(false);
                layer.Text = spec.Label + " (direct)";
                IntPtr unused = layer.Handle;
                peek = new GlassPeek(delegate { });
                peek.Layer = layer;
                presenter = new GlassPresenter(peek);
                presenter.Layer = layer;
                glow = new GlassGlow(presenter, anim, scale, lens);
                presenter.PresentTaskbarDirty = delegate(IntPtr l, IntPtr from, int alpha, int w, int h, int a, int b) { return false; };
                presenter.FirstOnScreen = delegate { };
                anim.SlideNow = delegate { return GlassAnimator.SlideFor(spec.AnimTravel * scale, new Point(home.X - off.X, home.Y - off.Y), card); };
                anim.PowerSaving = delegate { return false; };
                anim.MouseDown = GlassGlow.LeftDown;
                peekIn.LayerVisible = delegate { return presenter.LayerVisible; };
                peekIn.HideLayer = presenter.HideLayer;
                Resize(spec.Size, new int[spec.Size.Width * spec.Size.Height], false);
            }

            public GlassPresenter Presenter { get { return presenter; } }
            public float Vis { get { return anim.Vis; } }
            public bool Moving { get { return moving; } }
            public bool Sloshing { get { return slosh != 0 || sloshSpeed != 0; } }

            public void Show() { wantShown = true; }
            public void Hide(bool animate) { wantShown = false; if (!animate) cutPending = true; }
            public void Interact(bool h) { hover = h; }
            public void Pointer(Rectangle area) { hasPointer = area.Width > 0 && area.Height > 0; pointer = hasPointer ? area : Rectangle.Empty; }
            public void Updates(bool a) { adapt = a; }
            public void Slosh(int style, int amount) { sloshStyle = style; sloshAmount = amount; }

            public void Kick(int way)
            {
                if (sloshAmount <= 0 || sloshStyle >= 3) return;
                if (sloshStyle == 2) way = 1;
                kickPending += way * SloshKick * sloshAmount / 60f;
            }

            public void Buttons(GlassButton[] b, bool carry) { buttons = b; buttonsCarry = carry; buttonsNew = true; }

            public void Resize(Size s, int[] c, bool flow)
            {
                if (flow && size.Width > 0)
                {
                    flowFrom = Math.Max(0.86f, Math.Min(1.16f, size.Width / (float)s.Width));
                    flowPending = true;
                }
                size = s;
                card = new Rectangle(off, s);
                winW = s.Width + 2 * off.X;
                winH = s.Height + 2 * off.Y;
                win = new int[winW * winH];
                Put(c);
                dirty = true;
            }

            public void Put(int[] c)
            {
                for (int y = 0; y < size.Height; y++) Array.Copy(c, y * size.Width, win, (y + off.Y) * winW + off.X, size.Width);
                dirty = true;
            }

            void StepSlosh()
            {
                long now = ScreenFeed.Fine();
                float dt = Math.Min(0.05f, Math.Max(0f, (now - sloshFine) / 1000f));
                sloshFine = now;
                for (float left = dt; left > 0; left -= 0.005f)
                {
                    float step = Math.Min(0.005f, left);
                    sloshSpeed += (-SloshSpring * SloshSpring * slosh - 2 * SloshSpring * sloshSpeed) * step;
                    slosh += sloshSpeed * step;
                }
                if (slosh > SloshMost) slosh = SloshMost; else if (slosh < -SloshMost) slosh = -SloshMost;
                if (Math.Abs(slosh) < 0.002f && Math.Abs(sloshSpeed) < 0.02f) slosh = sloshSpeed = 0;
            }

            void Fill()
            {
                animIn.FastContent = lens;
                animIn.PhaseShown = anim.WantVisible;
                animIn.AnimHover = hover;
                animIn.Sloshing = Sloshing;
                animIn.AnimStyle = spec.AnimStyle;
                animIn.OsdIndicatorStyle = -1;
                animIn.AnimIn = spec.AnimIn;
                animIn.AnimOut = spec.AnimOut;
                animIn.AnimZoom = spec.AnimZoom;
                animIn.AnimLiquid = spec.AnimLiquid;
                animIn.AnimBounce = spec.AnimBounce;
                animIn.Scale = scale;
                animIn.Slosh = slosh;
                animIn.Card = card;
            }

            public void Frame()
            {
                if (buttonsNew)
                {
                    buttonsNew = false;
                    glow.Set(buttons, buttonsCarry);
                    dirty = true;
                }
                long fine = ScreenFeed.Fine();
                if (cutPending)
                {
                    cutPending = false;
                    anim.SetVisible(false);
                    anim.Cut();
                    slosh = sloshSpeed = 0f;
                    morphAt = 0;
                }
                if (flowPending)
                {
                    flowPending = false;
                    if (anim.Vis >= 1f) { morphFrom = flowFrom; morphAt = fine; }
                }
                if (kickPending != 0f)
                {
                    float push = kickPending;
                    kickPending = 0f;
                    if (!Sloshing) sloshFine = fine; else StepSlosh();
                    sloshSpeed += push;
                }
                moving = false;
                if (wantShown != anim.WantVisible)
                {
                    if (wantShown)
                    {
                        if (anim.Vis <= 0) BeginShow();
                        anim.SetVisible(true);
                    }
                    else if (anim.Vis <= 0 || !presenter.LayerVisible || presenter.Out == null)
                    {
                        anim.SetVisible(false);
                        anim.Cut();
                    }
                    else
                    {
                        if (!spec.PeekOnExit) peek.CloseTap();
                        anim.SetVisible(false);
                    }
                }
                bool exiting = !anim.WantVisible && anim.Vis > 0;

                // the pointer: over the window's rectangle (or the area asked for)
                Rectangle wr = new Rectangle(home.X - off.X, home.Y - off.Y, winW, winH);
                bool hovered = false;
                if (hover && !lens)
                {
                    int px, py;
                    GlassGlow.Cursor(out px, out py);
                    Point c = new Point(px, py);
                    Rectangle area = new Rectangle(home, size);
                    if (hasPointer) { area = pointer; area.Offset(home); }
                    hovered = area.Contains(c);
                    if (hovered) { animIn.CursorX = c.X - wr.Left; animIn.CursorY = c.Y - wr.Top; }
                }
                anim.HoverNow = hovered;

                float alpha;
                Deform bend;
                PointF shift;
                Fill();
                if (anim.Animate(animIn, out alpha, out bend, out shift)) moving = true;
                if (morphAt != 0)
                {
                    float t = (ScreenFeed.Fine() - morphAt) / (float)MorphMs;
                    if (t >= 1f || !lens) morphAt = 0;
                    else
                    {
                        float u = t - 1f, e = 1f + 2.2f * u * u * u + 1.2f * u * u;
                        bend.ZoomX *= morphFrom + (1f - morphFrom) * e;
                        bend.ZoomY *= 1f + (morphFrom - 1f) * 0.18f * (float)Math.Sin(t * Math.PI);
                        moving = true;
                    }
                }
                anim.Stretch(scale, shift, ref bend);
                if (Sloshing)
                {
                    StepSlosh();
                    if (!anim.WantVisible || sloshStyle >= 3) slosh = sloshSpeed = 0;
                    else
                    {
                        float amount = Math.Abs(slosh);
                        if (sloshStyle == 1)
                        {
                            bend.ZoomY *= 1f + 0.55f * slosh;
                            bend.ZoomX *= 1f - 0.3f * slosh;
                        }
                        else if (sloshStyle == 2)
                        {
                            float swell = Math.Max(0f, slosh);
                            bend.ZoomX *= 1f + 0.4f * swell;
                            bend.ZoomY *= 1f + 0.4f * swell;
                            bend.Swell += 0.2f * swell;
                        }
                        else
                        {
                            bend.LeanX += slosh;
                            bend.ZoomX *= 1f + 0.12f * amount;
                            bend.ZoomY *= 1f - 0.2f * amount;
                            bend.Swell += 0.15f * amount;
                        }
                        moving = true;
                    }
                }

                if (card.Width > 0 && card.Height > 0 && (anim.Vis > 0 || anim.WantVisible))
                {
                    bool drew = false;
                    if (!exiting || spec.PeekOnExit)
                    {
                        if (presenter.FitMap(card.Width, card.Height, scale, spec.KeepSpare, delegate { return look; }, delegate { return shape; })) dirty = true;
                        Fill();
                        if (anim.ApplyMotion(animIn, presenter.Map, ref moving)) dirty = true;
                        peekIn.WantLive = false;
                        peekIn.Live = false;
                        peekIn.Moving = moving;
                        peekIn.Sloshing = Sloshing;
                        peekIn.InShots = false;
                        peekIn.WholeWindow = false;
                        peekIn.PhaseShown = !(exiting && spec.PeekOnExit);
                        peekIn.LevelBusy = false;
                        peekIn.SavePower = false;
                        peekIn.SaverOn = false;
                        peekIn.OnBattery = false;
                        peekIn.GlassFps = 0;
                        peekIn.BatteryFpsLive = 20;
                        peekIn.Interval = 0;
                        peekIn.LiveGapMs = 66.7;
                        Point origin = new Point(home.X, home.Y);
                        bool backdropChanged = peek.Step(peekIn, presenter.Map, origin, Environment.TickCount);
                        if (peek.Backdrop.HasPicture && presenter.Adapt(adapt)) dirty = true;
                        if (glow.Any) glow.Step(home, hasPointer ? pointer : new Rectangle(0, 0, size.Width, size.Height), 15);
                        if (dirty || backdropChanged || presenter.Primed != presenter.Map || presenter.Out == null)
                        {
                            presenter.Render(PresentIn());
                            dirty = false;
                            drew = true;
                        }
                    }
                    if (!drew && !lens && glow.Any && glow.Paint()) drew = true;
                    if (presenter.Out != null && presenter.Map != null)
                    {
                        int a8 = (int)(alpha * 255f + 0.5f);
                        Point at = new Point(home.X - presenter.Map.Margin + (int)Math.Round(shift.X), home.Y - presenter.Map.Margin + (int)Math.Round(shift.Y));
                        if (drew || a8 != presenter.ShownAlpha || !bend.Same(presenter.Shown) || at != presenter.ShownAt) presenter.Present(PresentIn(), at, a8, bend);
                    }
                }
                if (anim.Vis <= 0 && !anim.WantVisible)
                {
                    presenter.HideLayer();
                    peek.CloseTap();
                    if (!rested)
                    {
                        rested = true;
                        anim.Cut();
                        peek.CloseTap();
                        peek.EndBurst(false);
                        presenter.FreeBitmaps();
                    }
                }
            }

            void BeginShow()
            {
                peek.ResetBehind(false);
                presenter.ResetAdapt();
                presenter.ShownAlpha = -1;
                peek.ResetCounters();
                presenter.ResetCounters();
                anim.Restart();
                peek.LastBackdropChange = Environment.TickCount;
                rested = false;
                dirty = true;
                slosh = sloshSpeed = 0f;
                morphAt = 0;
            }

            GlassPresentInput PresentIn()
            {
                presentIn.Content = win;
                presentIn.ContentW = winW;
                presentIn.ContentH = winH;
                presentIn.ContentFrom = off;
                presentIn.FastContent = lens;
                presentIn.WholeWindow = false;
                return presentIn;
            }

            public void Dispose()
            {
                try { presenter.HideLayer(); } catch (Exception) { }
                try { peek.CloseTap(); } catch (Exception) { }
                try { peek.EndBurst(false); } catch (Exception) { }
                try { presenter.FreeBitmaps(); } catch (Exception) { }
                try { peek.Layer = null; presenter.Layer = null; layer.Dispose(); } catch (Exception) { }
            }
        }

        // The transport of (B): the runtime is made here and run by hand (a frame when the bench says).
        sealed class ManualTransport : IGlassTransport
        {
            public SurfaceRuntime Run;
            public string Name { get { return "manual"; } }
            public IGlassChannel Open(GlassSpec spec, IGlassEvents events, SurfaceState state)
            {
                Run = new SurfaceRuntime(spec, events, state, delegate { });
                return Run;
            }
            public void Wake(IGlassChannel channel) { }
            public void Close(IGlassChannel channel)
            {
                SurfaceRuntime r = channel as SurfaceRuntime;
                if (r != null) r.Dispose();
            }
            public bool Post(Action work) { return false; }
            public void Dispose() { }
        }

        // (B) the same through GlassSurface, with the contents cropped to the card.
        sealed class SurfSide : ISide
        {
            readonly GlassService svc;
            readonly ManualTransport mt = new ManualTransport();
            readonly GlassSurface s;
            readonly Point home;
            Size size;

            public SurfSide(GlassSpec spec)
            {
                home = spec.Place;
                size = spec.Size;
                svc = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.UiThread });
                GlassSpec mine = spec.Clone();
                mine.Label = spec.Label + " (surface)";
                s = new GlassSurface(svc, mt, mine, null);
                s.Failed += delegate(object o, GlassFailedEventArgs e) { Program.Say("   surface: Failed {0} {1}", e.Code, e.Text); };
            }

            public GlassPresenter Presenter { get { return mt.Run.Presenter; } }
            public float Vis { get { return mt.Run.Vis; } }
            public bool Moving { get { return mt.Run.WasMoving; } }
            public bool Sloshing { get { return mt.Run.IsSloshing; } }
            public GlassSurface Surface { get { return s; } }

            public void Show() { s.Show(); }
            public void Hide(bool animate) { s.Hide(animate); }
            public void Resize(Size sz, int[] card, bool flow) { size = sz; s.SetSize(sz, card, flow); }
            public void Put(int[] card) { s.SetContents(card, size.Width, size.Height, Rectangle.Empty); }
            public void Kick(int way) { s.Kick(way); }
            public void Slosh(int style, int amount) { s.SetSlosh(style, amount); }
            public void Interact(bool hover) { s.SetInteraction(hover); }
            public void Pointer(Rectangle area) { s.SetPointerArea(area); }
            public void Buttons(GlassButton[] b, bool carry) { s.SetButtons(b, carry); }
            public void Updates(bool adapt) { s.SetUpdates(15, adapt, false); }
            public void Frame() { mt.Run.RunFrame(); }

            public void Dispose()
            {
                s.Dispose();
                svc.Dispose();
            }
        }

        // ---- comparing ----

        static int Channel(int a, int b)
        {
            int d = Math.Abs(((a >> 24) & 255) - ((b >> 24) & 255));
            d = Math.Max(d, Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)));
            d = Math.Max(d, Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)));
            return Math.Max(d, Math.Abs((a & 255) - (b & 255)));
        }

        static int Pixels(int[] x, int[] y, int count, out int differ)
        {
            int most = 0;
            differ = 0;
            for (int i = 0; i < count; i++)
            {
                if (x[i] == y[i]) continue;
                differ++;
                int d = Channel(x[i], y[i]);
                if (d > most) most = d;
            }
            return most;
        }

        // 0 = the same; else the largest difference of a pixel channel (255 for something else that differs); why says what.
        static int Differ(ISide a, ISide b, out string why)
        {
            why = null;
            GlassPresenter p = a.Presenter, q = b.Presenter;
            if (a.Vis != b.Vis) { why = "vis " + a.Vis + " / " + b.Vis; return 255; }
            if (p.LayerVisible != q.LayerVisible) { why = "layer visible " + p.LayerVisible + " / " + q.LayerVisible; return 255; }
            if (p.ShownAlpha != q.ShownAlpha) { why = "alpha " + p.ShownAlpha + " / " + q.ShownAlpha; return 255; }
            if (p.ShownAt != q.ShownAt) { why = "at " + p.ShownAt + " / " + q.ShownAt; return 255; }
            if (!p.Shown.Same(q.Shown)) { why = "bend differs"; return 255; }
            if ((p.Px == null) != (q.Px == null)) { why = "one has no picture"; return 255; }
            int most = 0, count;
            if (p.Px != null)
            {
                if (p.Px.Length != q.Px.Length) { why = "picture sizes " + p.Px.Length + " / " + q.Px.Length; return 255; }
                most = Pixels(p.Px, q.Px, p.Px.Length, out count);
                if (count > 0) why = count + " px differ in the picture";
            }
            if (most == 0 && !p.Shown.IsNone && p.WarpOut != null && q.WarpOut != null && p.WarpOut.Length == q.WarpOut.Length)
            {
                most = Pixels(p.WarpOut, q.WarpOut, p.WarpOut.Length, out count);
                if (count > 0) why = count + " px differ in the bent picture";
            }
            return most;
        }

        sealed class Pair : IDisposable
        {
            public readonly DirectSide A;
            public readonly SurfSide B;
            public string Tag;
            public Pair(GlassSpec spec, Point off)
            {
                A = new DirectSide(spec, off);
                B = new SurfSide(spec);
            }

            public void Do(Action<ISide> what)
            {
                what(A);
                what(B);
            }

            // n frames, ms apart on the bench clock; each is compared and counted as mid-animation or at rest (by whether A moved).
            public void Run(int n, int ms, string what)
            {
                for (int i = 0; i < n; i++)
                {
                    ScreenFeed.TestFine += ms;
                    GlassPresenter.TestTick = (int)ScreenFeed.TestFine;
                    A.Frame();
                    B.Frame();
                    string why;
                    int d = Differ(A, B, out why);
                    Group g = A.Moving ? Mid : Rest;
                    Note(g, d == 0, d, Tag + " " + what + " frame " + i + ": " + why);
                }
            }

            public void Dispose()
            {
                A.Dispose();
                B.Dispose();
            }
        }

        // ---- the run ----

        public static void Run()
        {
            Program.Say("");
            Program.Say("surface: the app's glass through the surface API (UI-thread mode) against the direct path (off screen)");
            string[] names = { "busy", "light", "dark" };
            float[] scales = { 1f, 1.5f, 2f, 2.5f };
            string[] kinds = { "card", "osd" };
            Dictionary<string, Scene> scenes = new Dictionary<string, Scene>();
            foreach (string n in names) scenes[n] = Scene.Make(n, Wallpaper);
            ScreenFeed.TestFine = 5000000;
            GlassGlow.TestOn = true;
            GlassGlow.TestCursor = new Point(-30000, -30000);
            try
            {
                foreach (string kind in kinds)
                    foreach (float k in scales)
                        foreach (string n in names)
                        {
                            try { Cell(kind, k, scenes[n]); }
                            catch (Exception ex) { Note(Api, false, 255, kind + " " + k + " " + n + " threw " + ex.GetType().Name + " " + ex.Message); }
                        }
                try { ApiChecks(scenes["light"], scenes["busy"]); }
                catch (Exception ex) { Note(Api, false, 255, "api threw " + ex.GetType().Name + " " + ex.Message); }
                try { FixChecks(scenes["light"], scenes["dark"], scenes["busy"]); }
                catch (Exception ex) { Note(Api, false, 255, "fix checks threw " + ex.GetType().Name + " " + ex.Message); }
                try { LoopbackChecks(); }
                catch (Exception ex) { Note(Api, false, 255, "loopback threw " + ex.GetType().Name + " " + ex.Message); }
            }
            finally
            {
                GlassPeek.TestScreen = null;
                GlassPeek.TestShift = Point.Empty;
                ScreenFeed.TestFine = 0;
                GlassPresenter.TestTick = 0;
                GlassGlow.TestOn = false;
                GlassGlow.TestDown = false;
            }
            foreach (Group g in new Group[] { Rest, Mid, Api })
                Program.Say("surface: {0}/{1} checks OK, max diff {2} ({3})", g.N - g.Bad, g.N, g.Max, g.Name);
        }

        static int testVersion = 100;

        static void Backdrop(Scene scene)
        {
            GlassPeek.TestScreen = scene.Px;
            GlassPeek.TestW = scene.W;
            GlassPeek.TestH = scene.H;
            GlassPeek.TestShift = new Point(10000, 10000);
            GlassPeek.TestVersion = ++testVersion;
        }

        static GlassSpec MakeSpec(string kind, float k, Scene scene, out int[] c0)
        {
            bool lens = kind == "osd";
            int cw = (int)Math.Round((lens ? 170 : 396) * k), ch = (int)Math.Round((lens ? 46 : 152) * k);
            int gx = lens ? (scene.W - cw) / 2 : scene.W - cw - (int)(16 * k);
            int gy = lens ? scene.H - ch - (int)(60 * k) : (int)(16 * k);
            GlassSpec spec = new GlassSpec();
            spec.Label = "LG surface bench " + kind;
            spec.Kind = lens ? GlassKind.Lens : GlassKind.Card;
            spec.Look = lens ? new GlassLook(46, 52, 80, 60, 28, 24, 100) : new GlassLook(46, 52, 80, 60, 40, 24, 100);
            spec.Scale = k;
            spec.Size = new Size(cw, ch);
            spec.Place = new Point(gx - 10000, gy - 10000);
            spec.KeepSpare = lens;
            spec.Fps = 15;
            c0 = lens ? Contents.Osd(cw, ch, k, 0.3f) : Contents.Card(cw, ch, k, 0);
            return spec;
        }

        static GlassButton[] CardButtons(int cw, int ch)
        {
            GlassButton[] b = new GlassButton[2];
            for (int i = 0; i < 2; i++)
            {
                GlassButton g = new GlassButton();
                float cx = cw * (i == 0 ? 0.3f : 0.7f), cy = ch * 0.78f, hx = cw * 0.14f, hy = ch * 0.1f;
                g.Cx = cx; g.Cy = cy; g.Hx = hx; g.Hy = hy; g.Rad = hy;
                g.Style = 0; g.Mark = i;
                g.Hit = Rectangle.FromLTRB((int)(cx - hx), (int)(cy - hy), (int)(cx + hx), (int)(cy + hy));
                b[i] = g;
            }
            return b;
        }

        static void Cell(string kind, float k, Scene scene)
        {
            Backdrop(scene);
            int[] c0;
            GlassSpec spec = MakeSpec(kind, k, scene, out c0);
            bool lens = kind == "osd";
            int cw = spec.Size.Width, ch = spec.Size.Height;
            Point home = spec.Place;
            Point off = new Point((int)(10 * k) + 3, (int)(8 * k) + 1);
            Point away = new Point(-30000, -30000);
            using (Pair p = new Pair(spec, off))
            {
                p.Tag = kind + " " + k + " " + scene.Name;
                p.Do(delegate(ISide s) { s.Put(c0); });
                // arrival, then at rest (a settled swell)
                p.Do(delegate(ISide s) { s.Show(); });
                p.Run(40, 16, "arrival");
                if (!lens)
                {
                    // the glass buttons' light: off, on one button, pressed, handed to the other, off again
                    GlassButton[] buttons = CardButtons(cw, ch);
                    p.Do(delegate(ISide s) { s.Interact(false); s.Buttons(buttons, false); });
                    p.Run(3, 16, "buttons");
                    GlassGlow.TestCursor = new Point(home.X + (buttons[0].Hit.Left + buttons[0].Hit.Right) / 2, home.Y + (buttons[0].Hit.Top + buttons[0].Hit.Bottom) / 2);
                    p.Run(14, 16, "glow on");
                    GlassGlow.TestDown = true;
                    p.Run(8, 16, "pressed");
                    GlassGlow.TestDown = false;
                    p.Run(4, 16, "released");
                    GlassGlow.TestCursor = new Point(home.X + (buttons[1].Hit.Left + buttons[1].Hit.Right) / 2, home.Y + (buttons[1].Hit.Top + buttons[1].Hit.Bottom) / 2);
                    p.Run(10, 16, "other button");
                    p.Do(delegate(ISide s) { s.Buttons(CardButtons(cw, ch), true); });   // (the same pieces again: what is lit stays lit)
                    p.Run(4, 16, "carried");
                    GlassGlow.TestCursor = away;
                    p.Run(12, 16, "glow off");
                    p.Do(delegate(ISide s) { s.Buttons(null, false); });
                    p.Run(3, 16, "no buttons");
                    // the pointer on the card's whole window, beyond the card
                    p.Do(delegate(ISide s) { s.Interact(true); s.Pointer(new Rectangle(-24, -24, cw + 48, ch + 48)); });
                    GlassGlow.TestCursor = new Point(home.X - 12, home.Y - 12);
                    p.Run(30, 16, "pointer beyond the card");
                    GlassGlow.TestCursor = away;
                    p.Run(30, 16, "pointer gone");
                    p.Do(delegate(ISide s) { s.Pointer(Rectangle.Empty); });
                    p.Run(3, 16, "area reset");
                    // another size (a taller notification), and back
                    Size big = new Size(cw * 9 / 10, ch * 11 / 10);
                    int[] c1 = Contents.Card(big.Width, big.Height, k, 1);
                    p.Do(delegate(ISide s) { s.Resize(big, c1, false); });
                    p.Run(10, 16, "resized");
                    p.Do(delegate(ISide s) { s.Resize(spec.Size, c0, false); });
                    p.Run(10, 16, "resized back");
                }
                else
                {
                    // the slosh: three styles, a kick each way, the level moving as it goes
                    for (int style = 0; style < 3; style++)
                    {
                        int st = style;
                        p.Do(delegate(ISide s) { s.Slosh(st, 60); s.Kick(1); });
                        for (int i = 0; i < 20; i++)
                        {
                            float level = 0.3f + 0.02f * i;
                            int[] c = Contents.Osd(cw, ch, k, level);
                            p.Do(delegate(ISide s) { s.Put(c); });
                            p.Run(1, 16, "slosh " + st + " up");
                        }
                        p.Do(delegate(ISide s) { s.Kick(-1); });
                        p.Run(10, 16, "slosh " + st + " down");
                        p.Do(delegate(ISide s) { s.Kick(1); });
                        p.Run(3, 16, "slosh " + st + " again");
                        p.Do(delegate(ISide s) { s.Kick(1); });
                        p.Run(50, 16, "slosh " + st + " settling");
                    }
                    p.Do(delegate(ISide s) { s.Slosh(3, 60); s.Kick(1); });
                    p.Run(5, 16, "slosh none");
                    p.Do(delegate(ISide s) { s.Slosh(0, 60); });
                    // another indicator takes over: the glass flows to its width, and back
                    Size wide = new Size((int)Math.Round(cw * 1.12f), ch);
                    int[] w1 = Contents.Osd(wide.Width, wide.Height, k, 0.6f);
                    p.Do(delegate(ISide s) { s.Resize(wide, w1, true); });
                    p.Run(30, 16, "flow wider");
                    p.Do(delegate(ISide s) { s.Resize(spec.Size, c0, true); });
                    p.Run(30, 16, "flow back");
                    p.Do(delegate(ISide s) { s.Resize(wide, w1, false); });
                    p.Run(6, 16, "size without flow");
                    p.Do(delegate(ISide s) { s.Resize(spec.Size, c0, false); });
                    p.Run(6, 16, "size back");
                }
                // leaving
                p.Do(delegate(ISide s) { s.Hide(true); });
                p.Run(25, 16, "exit");
                p.Run(3, 16, "gone");
                // and gone at once
                p.Do(delegate(ISide s) { s.Show(); });
                p.Run(30, 16, "arrival again");
                p.Do(delegate(ISide s) { s.Hide(false); });
                p.Run(3, 16, "cut");
                string left = p.A.Vis == 0 && p.B.Vis == 0 && !p.A.Presenter.LayerVisible && !p.B.Presenter.LayerVisible ? null : "still on screen";
                Note(Api, left == null, 0, p.Tag + " Hide(false): " + left);
            }
        }

        // The new calls, one by one (on the card at scale 1 over a light scene, and an indicator over a busy one).
        static void ApiChecks(Scene light, Scene busy)
        {
            // SetSize with its picture: no blank frame between (the frame after the call already holds the new picture, as the direct path's)
            Backdrop(busy);
            int[] c0;
            GlassSpec osd = MakeSpec("osd", 1f, busy, out c0);
            using (Pair p = new Pair(osd, new Point(11, 9)))
            {
                p.Tag = "api osd";
                p.Do(delegate(ISide s) { s.Put(c0); s.Show(); });
                p.Run(40, 16, "in");
                Size to = new Size(osd.Size.Width + 20, osd.Size.Height);
                int[] c1 = Contents.Osd(to.Width, to.Height, 1f, 0.7f);
                p.Do(delegate(ISide s) { s.Resize(to, c1, false); });
                ScreenFeed.TestFine += 1;
                GlassPresenter.TestTick = (int)ScreenFeed.TestFine;
                p.A.Frame();
                p.B.Frame();
                string why;
                int d = Differ(p.A, p.B, out why);
                int ink = 0;
                int[] px = p.B.Presenter.Px;
                if (px != null) for (int i = 0; i < px.Length; i += 7) if (((px[i] >> 24) & 255) != 0) ink++;
                Note(Api, d == 0 && ink > 0, d, "SetSize with contents: first frame " + why + " opaque " + ink);

                // Kick settles; with no amount it does nothing
                p.Do(delegate(ISide s) { s.Slosh(0, 60); s.Kick(1); });
                p.Run(1, 16, "kick");
                Note(Api, p.B.Sloshing && p.A.Sloshing, 0, "Kick starts a slosh");
                p.Run(70, 16, "kick settling");
                Note(Api, !p.B.Sloshing && !p.A.Sloshing, 0, "Kick settles");
                p.Do(delegate(ISide s) { s.Slosh(0, 0); s.Kick(1); });
                p.Run(2, 16, "kick without amount");
                Note(Api, !p.B.Sloshing, 0, "Kick with no amount does nothing");
                p.Do(delegate(ISide s) { s.Slosh(0, 60); s.Hide(false); });
                p.Run(1, 16, "cut");
                Note(Api, p.B.Vis == 0 && !p.B.Presenter.LayerVisible, 0, "Hide(false) is off at the next frame");
            }

            // the pointer area beyond the card: the swell comes with a pointer there, and goes with it
            Backdrop(light);
            int[] k0;
            GlassSpec card = MakeSpec("card", 1f, light, out k0);
            using (Pair p = new Pair(card, new Point(13, 11)))
            {
                p.Tag = "api card";
                Point home = card.Place;
                p.Do(delegate(ISide s) { s.Put(k0); s.Show(); });
                p.Run(40, 16, "in");
                Note(Api, p.B.Presenter.Shown.IsNone, 0, "no swell with the pointer away");
                p.Do(delegate(ISide s) { s.Pointer(new Rectangle(-30, -30, card.Size.Width + 60, card.Size.Height + 60)); });
                GlassGlow.TestCursor = new Point(home.X - 15, home.Y - 15);
                p.Run(30, 16, "pointer beyond");
                Note(Api, !p.B.Presenter.Shown.IsNone, 0, "swell with the pointer beyond the card, inside its area");
                GlassGlow.TestCursor = new Point(home.X - 45, home.Y - 45);
                p.Run(40, 16, "pointer outside the area");
                Note(Api, p.B.Presenter.Shown.IsNone, 0, "no swell with the pointer outside the area");
                GlassGlow.TestCursor = new Point(-30000, -30000);
                p.Do(delegate(ISide s) { s.Pointer(Rectangle.Empty); });

                // SetUpdates is live: the glass over a light scene is light; with adapting off it goes dark; on again, light
                Note(Api, p.B.Presenter.LightNow() == 1f, 0, "light glass over the light scene (" + p.B.Presenter.LightNow() + ")");
                p.Do(delegate(ISide s) { s.Updates(false); });
                p.Run(40, 16, "adapt off");
                Note(Api, p.B.Presenter.LightNow() == 0f && p.A.Presenter.LightNow() == 0f, 0, "SetUpdates(adapt off) turns the glass dark (" + p.B.Presenter.LightNow() + ")");
                p.Do(delegate(ISide s) { s.Updates(true); });
                p.Run(40, 16, "adapt on");
                Note(Api, p.B.Presenter.LightNow() == 1f && p.A.Presenter.LightNow() == 1f, 0, "SetUpdates(adapt on) turns it light again (" + p.B.Presenter.LightNow() + ")");
                string why;
                int d = Differ(p.A, p.B, out why);
                Note(Api, d == 0, d, "after the change of adapting: " + why);
            }
        }

        // How many pixels of the picture the presenter holds differ from a copy taken earlier.
        static int Changed(int[] before, int[] now)
        {
            if (before == null || now == null || before.Length != now.Length) return -1;
            int n = 0;
            for (int i = 0; i < before.Length; i++) if (before[i] != now[i]) n++;
            return n;
        }

        // The review's fixes: a cut while a Show is wanted again, a notification's exit that keeps looking behind it, and the contents a
        // BeforeRender handler sets during the arrival's first frame.
        static void FixChecks(Scene light, Scene dark, Scene busy)
        {
            // a cut while WantShown is true again restarts the arrival (the direct path does the same: its cut is always applied)
            Backdrop(busy);
            int[] c0;
            GlassSpec osd = MakeSpec("osd", 1f, busy, out c0);
            using (Pair p = new Pair(osd, new Point(11, 9)))
            {
                p.Tag = "fix cut";
                p.Do(delegate(ISide s) { s.Put(c0); s.Show(); });
                p.Run(40, 16, "in");
                Note(Api, p.B.Vis >= 1f, 0, "fully in before the cut (" + p.B.Vis + ")");
                p.Do(delegate(ISide s) { s.Hide(false); s.Show(); });
                p.Run(1, 16, "cut and show");
                Note(Api, p.B.Vis < 0.9f && p.B.Vis == p.A.Vis, 0, "a cut while a Show is wanted restarts the arrival (vis " + p.B.Vis + " / " + p.A.Vis + ")");
                p.Run(40, 16, "in again");
                Note(Api, p.B.Vis >= 1f, 0, "the restarted arrival completes (" + p.B.Vis + ")");
            }

            // PeekOnExit: the exit goes on looking behind the card (a new backdrop during the exit is shaded in), against the direct path;
            // without it the exit keeps the picture it has
            for (int pass = 0; pass < 2; pass++)
            {
                bool peekOn = pass == 0;
                Backdrop(light);
                int[] k0;
                GlassSpec card = MakeSpec("card", 1f, light, out k0);
                card.PeekOnExit = peekOn;
                using (Pair p = new Pair(card, new Point(13, 11)))
                {
                    p.Tag = "fix exit " + (peekOn ? "peeking" : "default");
                    p.Do(delegate(ISide s) { s.Put(k0); s.Show(); });
                    p.Run(40, 16, "in");
                    p.Do(delegate(ISide s) { s.Hide(true); });
                    p.Run(1, 16, "exit begins");
                    int[] was = p.B.Presenter.Px == null ? null : (int[])p.B.Presenter.Px.Clone();
                    Backdrop(dark);
                    p.Run(6, 16, "exit over a new backdrop");
                    int moved = Changed(was, p.B.Presenter.Px);
                    string why;
                    int d = Differ(p.A, p.B, out why);
                    Note(Api, d == 0, d, p.Tag + " against the direct path: " + why);
                    Note(Api, peekOn ? moved > 0 : moved == 0, 0, p.Tag + ": " + moved + " px of the picture changed during the exit");
                    p.Run(12, 16, "exit ends");
                    Note(Api, p.B.Vis == 0 && !p.B.Presenter.LayerVisible, 0, p.Tag + ": gone at the end");
                }
            }

            // the first frame of an arrival holds what a BeforeRender handler set during it (the knob drawn to suit the light)
            Backdrop(busy);
            int[] o0;
            GlassSpec spec = MakeSpec("osd", 1f, busy, out o0);
            int[] marked = Contents.Osd(spec.Size.Width, spec.Size.Height, 1f, 0.9f);
            using (SurfSide a = new SurfSide(spec))
            using (SurfSide b = new SurfSide(spec))
            {
                a.Put(o0);
                b.Put(marked);
                int w = spec.Size.Width, h = spec.Size.Height;
                a.Surface.BeforeRender += delegate(float l) { a.Surface.SetContents(marked, w, h, Rectangle.Empty); };
                a.Show();
                b.Show();
                ScreenFeed.TestFine += 16;
                GlassPresenter.TestTick = (int)ScreenFeed.TestFine;
                a.Frame();
                b.Frame();
                string why;
                int d = Differ(a, b, out why);
                Note(Api, d == 0, d, "the first arrival frame has the contents set in BeforeRender: " + why);
            }
        }

        static void Throws(string what, Action call)
        {
            try { call(); Note(Api, false, 255, what + " did not throw over the pipe"); }
            catch (NotSupportedException) { Note(Api, true, 0, what); }
            catch (Exception ex) { Note(Api, false, 255, what + " threw " + ex.GetType().Name); }
        }

        // Over the pipe the new setters are not supported (they throw NotSupportedException); in process they do not.
        static void LoopbackChecks()
        {
            string suffix = "sb" + System.Diagnostics.Process.GetCurrentProcess().Id;
            GlassHostServer host = new GlassHostServer();
            host.Start(suffix);
            GlassService piped = null, local = null;
            try
            {
                piped = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Host, HostSuffix = suffix });
                GlassSpec spec = Contract.Spec("LG surface bench pipe");
                GlassSurface s = piped.Create(spec);
                int[] px = new int[spec.Size.Width * spec.Size.Height];
                Throws("SetSize(contents, flow)", delegate { s.SetSize(spec.Size, px, false); });
                Throws("Hide(false)", delegate { s.Hide(false); });
                Throws("SetMotion(7)", delegate { s.SetMotion(4, 380, 230, 10, 60, 45, 34); });
                Throws("SetMotion(8)", delegate { s.SetMotion(4, 380, 230, 10, 60, 45, 34, 2); });
                Throws("SetUpdates", delegate { s.SetUpdates(15, true, false); });
                Throws("SetPointerArea", delegate { s.SetPointerArea(new Rectangle(0, 0, 10, 10)); });
                Throws("SetSlosh", delegate { s.SetSlosh(0, 60); });
                Throws("Kick", delegate { s.Kick(1); });
                Throws("SetBusy", delegate { s.SetBusy(true); });
                Throws("SetButtons", delegate { s.SetButtons(new GlassButton[1] { new GlassButton() }, false); });
                Throws("BeforeRender", delegate { s.BeforeRender += delegate(float v) { }; });
                try { s.Hide(true); s.SetSize(new Size(300, 100)); s.Show(); Note(Api, true, 0, "the old calls still work over the pipe"); }
                catch (Exception ex) { Note(Api, false, 255, "an old call threw over the pipe: " + ex.Message); }
                s.Dispose();

                local = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.UiThread });
                GlassSurface t = local.Create(Contract.Spec("LG surface bench local"));
                try
                {
                    t.SetMotion(4, 380, 230, 10, 60, 45, 34);
                    t.SetUpdates(15, true, false);
                    t.SetPointerArea(new Rectangle(0, 0, 10, 10));
                    t.SetSlosh(0, 60);
                    t.Kick(1);
                    t.SetBusy(true);
                    t.SetButtons(null, false);
                    t.BeforeRender += delegate(float v) { };
                    t.Hide(false);
                    Note(Api, true, 0, "the new setters work in process");
                }
                catch (Exception ex) { Note(Api, false, 255, "a new setter threw in process: " + ex.GetType().Name); }
                t.Dispose();
            }
            finally
            {
                if (piped != null) piped.Dispose();
                if (local != null) local.Dispose();
                host.Stop();
            }
        }
    }
}
