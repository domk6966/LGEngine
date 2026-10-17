using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

namespace LGEngine
{
    // An editor for a GlassProfile that any app can host: the glass seen from the side, drawn from the engine's own ray trace
    // (GlassSideView), with the top and the underside as curves whose control points are dragged. Translucent glass with a
    // thin outline and a dashed left edge (it goes on), a screen strip under it with the gap, blue view rays straight down,
    // refracted inside and on to the screen, coral dots where the engine samples the picture, amber reflections and an
    // amber rim highlight, and the sizes in points.
    //
    //   Drag a point to move it (the end points slide up and down only; the face handle on the left sets the glass's
    //   thickness, the one at the rim the rim's height), double-click a curve to add a point, right-click a point to take it
    //   away. The rays follow while dragging; ProfileChanged is raised 150 ms after the last change.
    //
    // Profile null means "Today" (the shape's own analytic shoulder); the first edit turns it into control points.
    public class GlassProfileEditor : UserControl
    {
        sealed class Palette
        {
            public Color Back, Panel, PanelEdge, Text, TextDim, Dim, GlassFill, GlassEdge, Ray, Amber, Coral, Screen, ScreenEdge, TopHandle, BotHandle, Warn;
        }

        static readonly Palette LightP = new Palette
        {
            Back = Color.FromArgb(243, 245, 249), Panel = Color.FromArgb(252, 253, 255), PanelEdge = Color.FromArgb(214, 220, 232),
            Text = Color.FromArgb(52, 62, 78), TextDim = Color.FromArgb(120, 132, 152), Dim = Color.FromArgb(132, 146, 170),
            GlassFill = Color.FromArgb(92, 150, 205, 255), GlassEdge = Color.FromArgb(66, 128, 232), Ray = Color.FromArgb(46, 110, 238),
            Amber = Color.FromArgb(240, 158, 30), Coral = Color.FromArgb(255, 106, 84), Screen = Color.FromArgb(204, 209, 218),
            ScreenEdge = Color.FromArgb(160, 168, 184), TopHandle = Color.FromArgb(40, 104, 232), BotHandle = Color.FromArgb(150, 82, 214),
            Warn = Color.FromArgb(206, 96, 20)
        };
        static readonly Palette DarkP = new Palette
        {
            Back = Color.FromArgb(28, 31, 38), Panel = Color.FromArgb(36, 40, 49), PanelEdge = Color.FromArgb(62, 69, 84),
            Text = Color.FromArgb(214, 221, 234), TextDim = Color.FromArgb(138, 150, 172), Dim = Color.FromArgb(124, 138, 164),
            GlassFill = Color.FromArgb(84, 112, 168, 244), GlassEdge = Color.FromArgb(116, 166, 255), Ray = Color.FromArgb(120, 176, 255),
            Amber = Color.FromArgb(255, 192, 92), Coral = Color.FromArgb(255, 128, 108), Screen = Color.FromArgb(74, 80, 94),
            ScreenEdge = Color.FromArgb(104, 112, 130), TopHandle = Color.FromArgb(124, 178, 255), BotHandle = Color.FromArgb(208, 150, 255),
            Warn = Color.FromArgb(255, 160, 80)
        };

        struct Grip
        {
            public bool Top, End, Face;   // (which curve; the point at the outline; the face handle that sets the thickness)
            public int Index;
            public PointF Pos;
            public bool Fixed;            // (the underside's point under the face: nothing to drag)
        }

        // what is set
        GlassShape shape = GlassShape.Card;
        GlassLook look = GlassLook.LiquidGlass;
        GlassProfile profile;
        int cardW = 396, cardH = 152, rayCount = 11;
        bool dark;
        Palette pal = LightP;
        GlassSideView view;
        string presetNote = "";

        // the parts
        ContextMenuStrip presetMenu;
        Rectangle presetRect;
        bool presetHot;
        int presetIndex;
        readonly Button resetButton = new Button();
        readonly Timer changeTimer = new Timer(), moveTimer = new Timer();
        Font font, fontBold;

        // layout and the mapping from points (x across the bezel from the face's end, y up from the screen) to pixels
        float sc = 1f, ox, oy, sx = 1f, sy = 1f, xLeft;
        Rectangle panel, track;
        bool frozen;   // (the mapping stays put while a point is dragged)

        // interaction
        List<Grip> handles = new List<Grip>();
        int hover = -1, dragging = -1, hoverTrack;
        bool dragTop, dragFace, dragTrack, dragEnd;
        PointF dragTo;
        bool movePending, changed;
        float dragFaceB;   // the face's thickness (bezel widths, default thickness) when the drag began
        string hint = "";

        public event EventHandler ProfileChanged;

        public GlassProfileEditor()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Size = new Size(380, 372);
            MinimumSize = new Size(300, 300);
            font = new Font("Segoe UI", 8.25f);
            fontBold = new Font("Segoe UI Semibold", 8.25f);
            resetButton.Text = "Reset";
            resetButton.FlatStyle = FlatStyle.Flat;
            resetButton.Font = font;
            resetButton.UseVisualStyleBackColor = false;
            Controls.Add(resetButton);
            resetButton.Click += delegate { SetProfileInternal(null, true); };
            changeTimer.Interval = 150;
            changeTimer.Tick += delegate { changeTimer.Stop(); if (changed) { changed = false; RaiseChanged(); } };
            moveTimer.Interval = 14;
            moveTimer.Tick += delegate { moveTimer.Stop(); if (movePending) { movePending = false; ApplyDrag(); } };
            FillPresets();
            ApplyTheme();
            Recompute();
        }

        // ---------------------------------------------------------------------------------------------
        // The properties.

        // The profile shown: null is "Today". Setting it does not raise ProfileChanged.
        public GlassProfile Profile
        {
            get { return profile; }
            set { SetProfileInternal(value, false); }
        }

        // The kind of glass (GlassShape.Card or GlassShape.Lens): what "Today" and the presets mean for it.
        public GlassShape Shape
        {
            get { return shape; }
            set
            {
                shape = value ?? GlassShape.Card;
                todayCache = null;
                bool lens = shape.Name == "lens";
                cardW = lens ? 170 : 396; cardH = lens ? 46 : 152;
                FillPresets();
                Recompute();
            }
        }

        // The look (thickness follows Refraction and Intensity, the gap Elevation) the section is drawn for.
        public GlassLook Look
        {
            get { return look; }
            set { look = value; Recompute(); }
        }

        // The size of the glass, in points (the bezel's width follows it).
        public Size CardSizePt
        {
            get { return new Size(cardW, cardH); }
            set { cardW = Math.Max(20, value.Width); cardH = Math.Max(20, value.Height); Recompute(); }
        }

        public int RayCount
        {
            get { return rayCount; }
            set { rayCount = Math.Max(3, Math.Min(25, value)); Recompute(); }
        }

        public bool DarkTheme
        {
            get { return dark; }
            set { dark = value; ApplyTheme(); Invalidate(); }
        }

        // The latest section the engine computed (what is drawn).
        public GlassSideView SideView { get { return view; } }

        void ApplyTheme()
        {
            pal = dark ? DarkP : LightP;
            BackColor = pal.Back;
            ForeColor = pal.Text;
            resetButton.BackColor = dark ? Color.FromArgb(46, 51, 63) : Color.White;
            resetButton.ForeColor = pal.Text;
            resetButton.FlatAppearance.BorderColor = pal.PanelEdge;
            if (presetMenu != null)
            {
                presetMenu.BackColor = resetButton.BackColor;
                presetMenu.ForeColor = pal.Text;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Presets: a button that shows the one in use and opens the list (drawn here, so that it looks the same in both themes).

        static string PresetLabel(GlassPreset p) { return p.MakesRing ? p.Name + " (silver ring)" : p.Name; }

        void FillPresets()
        {
            if (presetMenu != null) presetMenu.Dispose();
            presetMenu = new ContextMenuStrip();
            presetMenu.Font = font;
            presetMenu.ShowImageMargin = false;
            presetMenu.BackColor = resetButton.BackColor;
            presetMenu.ForeColor = pal.Text;
            int n = 0;
            foreach (GlassPreset p in GlassProfile.Presets)
            {
                ToolStripMenuItem item = new ToolStripMenuItem(PresetLabel(p));
                int index = n++;
                item.Click += delegate { PresetPicked(index); };
                presetMenu.Items.Add(item);
            }
            ToolStripMenuItem custom = new ToolStripMenuItem("Custom (edited)");
            custom.Enabled = false;
            presetMenu.Items.Add(custom);
            SelectPresetFor(profile);
        }

        void SelectPresetFor(GlassProfile p)
        {
            int pick = GlassProfile.Presets.Count;   // (Custom)
            presetNote = "";
            if (p == null) pick = 0;
            else
            {
                string[] names = GlassProfile.PresetNames;
                for (int i = 1; i < names.Length; i++)
                    if (GlassProfile.SameLook(p, GlassProfile.Preset(names[i], shape))) { pick = i; break; }
            }
            if (pick < GlassProfile.Presets.Count) presetNote = GlassProfile.Presets[pick].Note;
            presetIndex = pick;
            if (presetMenu != null)
                for (int i = 0; i < presetMenu.Items.Count; i++) ((ToolStripMenuItem)presetMenu.Items[i]).Checked = i == pick;
            Invalidate();
        }

        void PresetPicked(int i)
        {
            if (i < 0 || i >= GlassProfile.Presets.Count) return;   // (Custom: nothing to do)
            SetProfileInternal(GlassProfile.Preset(GlassProfile.PresetNames[i], shape), true);
        }

        void DrawPresetButton(Graphics g)
        {
            float s = sc;
            using (GraphicsPath gp = Rounded(presetRect, 4 * s))
            using (SolidBrush b = new SolidBrush(presetHot ? (dark ? Color.FromArgb(54, 60, 74) : Color.FromArgb(236, 242, 252)) : (dark ? Color.FromArgb(46, 51, 63) : Color.White)))
            using (Pen p = new Pen(presetHot ? pal.TopHandle : pal.PanelEdge))
            {
                g.FillPath(b, gp);
                g.DrawPath(p, gp);
            }
            string text = presetIndex < GlassProfile.Presets.Count ? PresetLabel(GlassProfile.Presets[presetIndex]) : "Custom (edited)";
            using (SolidBrush t = new SolidBrush(pal.Text))
            using (StringFormat f = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(text, font, t, new RectangleF(presetRect.Left + 8 * s, presetRect.Top, presetRect.Width - 28 * s, presetRect.Height), f);
            float cx = presetRect.Right - 13 * s, cy = presetRect.Top + presetRect.Height / 2f;
            using (Pen p = new Pen(pal.TextDim, 1.6f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new PointF[] { new PointF(cx - 3.5f * s, cy - 1.5f * s), new PointF(cx, cy + 2f * s), new PointF(cx + 3.5f * s, cy - 1.5f * s) });
            }
        }

        void SetProfileInternal(GlassProfile p, bool raise)
        {
            profile = p;
            SelectPresetFor(p);
            frozen = false;
            Recompute();
            if (raise) Touched();
        }

        // ---------------------------------------------------------------------------------------------
        // The section, from the engine.

        void Recompute()
        {
            view = GlassSideView.Compute(shape.WithProfile(profile), look, cardW, cardH, rayCount);
            handles.Clear();
            Invalidate();
        }

        void Touched()
        {
            changed = true;
            changeTimer.Stop();
            changeTimer.Start();
        }

        void RaiseChanged()
        {
            EventHandler h = ProfileChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        // The profile the editing works on: today's, as control points, once something is edited.
        GlassProfile todayCache;

        GlassProfile Editing()
        {
            if (profile != null) return profile;
            if (todayCache == null) todayCache = GlassProfile.FromShape(shape, 17);
            return todayCache;
        }

        // ---------------------------------------------------------------------------------------------
        // Layout.

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (Graphics g = CreateGraphics()) sc = g.DpiX / 96f;
            Relayout();
        }

        void Relayout()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            int bar = (int)(28 * sc);
            presetRect = new Rectangle(0, 0, w - (int)(70 * sc), bar);
            resetButton.SetBounds(w - (int)(64 * sc), 0, (int)(64 * sc), bar - 1);
            panel = new Rectangle(0, bar + (int)(8 * sc), w, h - bar - (int)(8 * sc) - (int)(98 * sc));
            track = new Rectangle((int)(84 * sc), h - (int)(22 * sc), w - (int)(84 * sc) - (int)(62 * sc), (int)(14 * sc));
            frozen = false;
            Invalidate();
        }

        // points -> pixels. x: from the face's end; y: up from the screen.
        void Fit()
        {
            float s = sc;
            float left = panel.Left + 80 * s, right = panel.Right - 16 * s, top = panel.Top + 48 * s, bottom = panel.Bottom - 38 * s;
            float B = Math.Max(1f, view.BezelPt), H = Math.Max(1f, view.GapPt + view.FaceThicknessPt);
            const float ext = 0.45f, tail = 0.08f;
            float wPt = B * (ext + 1 + tail);
            float sx0 = (right - left) / wPt, sy0 = (bottom - top) / H;
            sx = Math.Min(sx0, 3.4f * sy0);
            sy = Math.Min(sy0, 2.2f * sx);
            xLeft = left + ((right - left) - sx * wPt) / 2;
            ox = xLeft + ext * B * sx;
            oy = bottom;
        }

        PointF Px(float xPt, float yPt) { return new PointF(ox + xPt * sx, oy - yPt * sy); }

        // ---------------------------------------------------------------------------------------------
        // Painting.

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(pal.Back);
            if (view == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (!frozen) Fit();
            float s = sc;
            using (GraphicsPath pp = Rounded(panel, 8 * s))
            using (SolidBrush pb = new SolidBrush(pal.Panel))
            using (Pen pe = new Pen(pal.PanelEdge))
            {
                g.FillPath(pb, pp);
                g.DrawPath(pe, pp);
            }
            Region keep = g.Clip;
            g.SetClip(Rectangle.Inflate(panel, -1, -1));
            DrawScreen(g);
            DrawGlass(g);
            DrawRays(g);
            DrawDimensions(g);
            BuildHandles();
            DrawHandles(g);
            g.Clip = keep;
            DrawPresetButton(g);
            DrawReadout(g);
        }

        static GraphicsPath Rounded(Rectangle r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void DrawScreen(Graphics g)
        {
            float s = sc;
            RectangleF r = new RectangleF(panel.Left + 10 * s, oy, panel.Width - 20 * s, 15 * s);
            using (SolidBrush b = new SolidBrush(pal.Screen)) g.FillRectangle(b, r);
            using (Pen p = new Pen(pal.ScreenEdge, 1.2f)) g.DrawLine(p, r.Left, r.Top, r.Right, r.Top);
            using (SolidBrush t = new SolidBrush(pal.TextDim))
            using (StringFormat f = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
                g.DrawString("screen", font, t, new RectangleF(r.Left, r.Top, r.Width - 6 * s, r.Height), f);
        }

        PointF[] Poly(PointF[] pts)
        {
            PointF[] o = new PointF[pts.Length];
            for (int i = 0; i < pts.Length; i++) o[i] = Px(pts[i].X, pts[i].Y);
            return o;
        }

        void DrawGlass(Graphics g)
        {
            float B = view.BezelPt;
            PointF[] top = Poly(view.Top), bot = Poly(view.Bottom);
            float xl = xLeft;
            PointF a = new PointF(xl, top[0].Y), b = new PointF(xl, bot[0].Y);
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddLine(a, top[0]);
                path.AddLines(top);
                path.AddLine(top[top.Length - 1], bot[bot.Length - 1]);
                for (int i = bot.Length - 1; i > 0; i--) path.AddLine(bot[i], bot[i - 1]);
                path.AddLine(bot[0], b);
                path.CloseFigure();
                using (SolidBrush fill = new SolidBrush(pal.GlassFill)) g.FillPath(fill, path);
            }
            using (Pen edge = new Pen(pal.GlassEdge, 1.3f * Math.Max(1f, sc * 0.9f)))
            {
                edge.LineJoin = LineJoin.Round;
                // the outline: face, top, the outline's wall, underside, flat bottom (the left edge is dashed: the glass goes on)
                List<PointF> o = new List<PointF>();
                o.Add(a); o.AddRange(top);
                for (int i = bot.Length - 1; i >= 0; i--) o.Add(bot[i]);
                o.Add(b);
                g.DrawLines(edge, o.ToArray());
                using (Pen dash = new Pen(pal.GlassEdge, 1.1f))
                {
                    dash.DashStyle = DashStyle.Dash;
                    g.DrawLine(dash, a, b);
                }
            }
            // the rim highlight: the last stretch of the top surface and the wall, amber, stronger towards the outline
            int from = (int)(top.Length * 0.78f);
            for (int i = from; i < top.Length - 1; i++)
            {
                float k = (i - from) / (float)(top.Length - 1 - from);
                using (Pen p = new Pen(Color.FromArgb((int)(70 + 150 * k), pal.Amber), (1.6f + 2.2f * k) * Math.Max(1f, sc * 0.9f)))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    g.DrawLine(p, top[i], top[i + 1]);
                }
            }
            using (Pen p = new Pen(Color.FromArgb(220, pal.Amber), 3.6f * Math.Max(1f, sc * 0.9f)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawLine(p, top[top.Length - 1], bot[bot.Length - 1]);
            }
        }

        // a straight line with an arrowhead at its end, drawn by hand (so that it looks the same everywhere)
        void Arrow(Graphics g, Color c, float w, PointF p0, PointF p1, bool head, bool dashed)
        {
            using (Pen p = new Pen(c, w))
            {
                if (dashed) p.DashStyle = DashStyle.Dash;
                g.DrawLine(p, p0, p1);
            }
            if (!head) return;
            float dx = p1.X - p0.X, dy = p1.Y - p0.Y, len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 3) return;
            dx /= len; dy /= len;
            float l = 6.5f * Math.Max(1f, sc * 0.9f), hw = 2.8f * Math.Max(1f, sc * 0.9f);
            PointF[] tri = { p1, new PointF(p1.X - dx * l - dy * hw, p1.Y - dy * l + dx * hw), new PointF(p1.X - dx * l + dy * hw, p1.Y - dy * l - dx * hw) };
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, tri);
        }

        float TopYAt(float xPt)
        {
            PointF[] t = view.Top;
            if (xPt <= t[0].X) return t[0].Y;
            for (int i = 1; i < t.Length; i++)
                if (xPt <= t[i].X)
                {
                    float f = (xPt - t[i - 1].X) / Math.Max(1e-6f, t[i].X - t[i - 1].X);
                    return t[i - 1].Y + f * (t[i].Y - t[i - 1].Y);
                }
            return t[t.Length - 1].Y;
        }

        void DrawRays(Graphics g)
        {
            float s = sc, lw = 1.25f * Math.Max(1f, s * 0.9f);
            float yStart = panel.Top + 36 * s;
            Color blue = Color.FromArgb(215, pal.Ray);
            foreach (SideRay r in view.Rays)
            {
                PointF en = Px(r.Entry.X, r.Entry.Y), hit = Px(r.Hit.X, r.Hit.Y);
                // down from above, then inside the glass
                Arrow(g, Color.FromArgb(150, pal.Ray), lw, new PointF(en.X, yStart), en, true, false);
                // the amber reflection off the top: stronger towards the rim, where the surface leans over
                double rx = r.ReflectDx * sx, ry = -r.ReflectDy * sy, rl = Math.Sqrt(rx * rx + ry * ry);
                if (rl > 1e-6)
                {
                    float len = (9 + 30 * (float)Math.Sqrt(r.Reflectance)) * s;
                    PointF tip = new PointF(en.X + (float)(rx / rl) * len, en.Y + (float)(ry / rl) * len);
                    Arrow(g, Color.FromArgb((int)(90 + 160 * Math.Sqrt(r.Reflectance)), pal.Amber), lw, en, tip, true, false);
                }
                Arrow(g, blue, lw, en, hit, false, false);
                if (!r.Tir)
                {
                    PointF sc2 = Px(r.Screen.X, 0f);
                    // (a ray that grazes out and would land far from where the engine looks is drawn faintly)
                    bool far = Math.Abs(r.Screen.X - r.Kept.X) * sx > 14 * s;
                    Arrow(g, far ? Color.FromArgb(70, pal.Ray) : blue, lw, hit, sc2, !far, false);
                }
                else
                {
                    // totally reflected at the underside: up again, until it meets the top
                    PointF p = new PointF(r.Hit.X, r.Hit.Y);
                    float step = view.BezelPt * 0.01f;
                    PointF q = p;
                    for (int i = 0; i < 160; i++)
                    {
                        q = new PointF(q.X + r.TirDx * step, q.Y + r.TirDy * step);
                        if (q.Y >= TopYAt(q.X) || q.X < -view.BezelPt * 0.45f || q.X > view.BezelPt) break;
                    }
                    Arrow(g, Color.FromArgb(220, pal.Amber), lw, hit, Px(q.X, q.Y), true, true);
                }
                // where the engine samples this pixel's view (after its controlled lensing)
                PointF k = Px(r.Kept.X, 0f);
                if (!r.Tir)
                {
                    PointF sc3 = Px(r.Screen.X, 0f);
                    if (Math.Abs(sc3.X - k.X) > 1.5f) using (Pen dp = new Pen(Color.FromArgb(130, pal.Coral), 1f)) { dp.DashStyle = DashStyle.Dot; g.DrawLine(dp, sc3, k); }
                }
                float rad = 3.3f * Math.Max(1f, s * 0.9f);
                float kx = Math.Max(panel.Left + 12 * s, Math.Min(panel.Right - 12 * s, k.X));
                using (SolidBrush cb = new SolidBrush(pal.Coral))
                using (Pen wp = new Pen(pal.Panel, 1.4f))
                {
                    g.FillEllipse(cb, kx - rad, k.Y - rad, rad * 2, rad * 2);
                    g.DrawEllipse(wp, kx - rad, k.Y - rad, rad * 2, rad * 2);
                }
            }
        }

        void DimLineV(Graphics g, float x, float y0, float y1, string label)
        {
            float s = sc;
            using (Pen p = new Pen(pal.Dim, 1f))
            {
                g.DrawLine(p, x, y0, x, y1);
                g.DrawLine(p, x - 3 * s, y0, x + 3 * s, y0);
                g.DrawLine(p, x - 3 * s, y1, x + 3 * s, y1);
            }
            using (SolidBrush t = new SolidBrush(pal.Text))
            using (StringFormat f = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(label, font, t, new RectangleF(panel.Left + 2 * s, Math.Min(y0, y1), x - panel.Left - 6 * s, Math.Abs(y1 - y0)), f);
        }

        void DrawDimensions(Graphics g)
        {
            float s = sc;
            string pt = " pt";
            Func<float, string> n = delegate(float v) { return v.ToString(v < 5 ? "0.0" : "0", CultureInfo.InvariantCulture) + pt; };
            float dimX = xLeft - 12 * s;
            PointF top = Px(0, view.GapPt + view.FaceThicknessPt), flat = Px(0, view.GapPt), scr = Px(0, 0);
            DimLineV(g, dimX, top.Y, flat.Y, "thickness\n" + n(view.FaceThicknessPt));
            DimLineV(g, dimX, flat.Y, scr.Y, scr.Y - flat.Y >= 36 * s ? "gap\n" + n(view.GapPt) : "gap " + n(view.GapPt));
            // the bezel's width, along the top
            float y = panel.Top + 22 * s;
            PointF a = Px(0, 0), b = Px(view.BezelPt, 0);
            using (Pen p = new Pen(pal.Dim, 1f))
            {
                g.DrawLine(p, a.X, y, b.X, y);
                g.DrawLine(p, a.X, y - 3 * s, a.X, y + 3 * s);
                g.DrawLine(p, b.X, y - 3 * s, b.X, y + 3 * s);
            }
            using (SolidBrush t = new SolidBrush(pal.Text))
            using (StringFormat f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far })
                g.DrawString("bezel " + n(view.BezelPt), font, t, new RectangleF(a.X, y - 14 * s, b.X - a.X, 12 * s), f);
            // (the picture is wider than it is true to size)
            float stretch = sx / sy;
            if (stretch > 1.15f)
                using (SolidBrush t = new SolidBrush(pal.TextDim))
                using (StringFormat f = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Far })
                    g.DrawString("width x" + stretch.ToString("0.0", CultureInfo.InvariantCulture), font, t, new RectangleF(panel.Left, panel.Bottom - 16 * s, panel.Width - 10 * s, 14 * s), f);
        }

        // ---------------------------------------------------------------------------------------------
        // Handles.

        float HeightAt(PointF[] poly, float xPt)
        {
            for (int i = 1; i < poly.Length; i++)
                if (xPt <= poly[i].X + 1e-4f)
                {
                    float f = (xPt - poly[i - 1].X) / Math.Max(1e-6f, poly[i].X - poly[i - 1].X);
                    return poly[i - 1].Y + Math.Max(0f, Math.Min(1f, f)) * (poly[i].Y - poly[i - 1].Y);
                }
            return poly[poly.Length - 1].Y;
        }

        void BuildHandles()
        {
            handles.Clear();
            GlassProfile p = Editing();
            float B = view.BezelPt;
            for (int i = 0; i < p.TopPoints.Count; i++)
            {
                ProfilePoint pt = p.TopPoints[i];
                Grip h = new Grip();
                h.Top = true; h.Index = i; h.Face = i == 0; h.End = i == p.TopPoints.Count - 1;
                h.Pos = Px(pt.T * B, HeightAt(view.Top, pt.T * B));
                handles.Add(h);
            }
            for (int i = 0; i < p.BottomPoints.Count; i++)
            {
                ProfilePoint pt = p.BottomPoints[i];
                Grip h = new Grip();
                h.Top = false; h.Index = i; h.End = i == p.BottomPoints.Count - 1; h.Fixed = i == 0;
                h.Pos = Px(pt.T * B, HeightAt(view.Bottom, pt.T * B));
                handles.Add(h);
            }
        }

        void DrawHandles(Graphics g)
        {
            float s = sc, r0 = 4.6f * Math.Max(1f, s * 0.9f);
            for (int i = 0; i < handles.Count; i++)
            {
                Grip h = handles[i];
                Color c = h.Top ? pal.TopHandle : pal.BotHandle;
                bool hot = i == hover || i == dragging;
                float r = hot ? r0 + 1.6f : r0;
                if (h.Fixed)
                {
                    using (Pen p = new Pen(c, 1.2f)) g.DrawEllipse(p, h.Pos.X - 2.5f, h.Pos.Y - 2.5f, 5f, 5f);
                    continue;
                }
                using (SolidBrush b = new SolidBrush(hot ? c : pal.Panel))
                using (Pen p = new Pen(c, 2f))
                {
                    if (h.Face)
                    {
                        PointF[] d = { new PointF(h.Pos.X, h.Pos.Y - r - 1), new PointF(h.Pos.X + r + 1, h.Pos.Y), new PointF(h.Pos.X, h.Pos.Y + r + 1), new PointF(h.Pos.X - r - 1, h.Pos.Y) };
                        g.FillPolygon(b, d);
                        g.DrawPolygon(p, d);
                    }
                    else
                    {
                        g.FillEllipse(b, h.Pos.X - r, h.Pos.Y - r, r * 2, r * 2);
                        g.DrawEllipse(p, h.Pos.X - r, h.Pos.Y - r, r * 2, r * 2);
                    }
                }
            }
        }

        // ---------------------------------------------------------------------------------------------
        // The readout under the picture.

        void DrawReadout(Graphics g)
        {
            float s = sc;
            int y = panel.Bottom + (int)(8 * s);
            string line = "Bend at rim  " + view.BendAtRimPt.ToString("0.0", CultureInfo.InvariantCulture) + " pt     Glass at rim  "
                + view.RimThicknessPt.ToString("0", CultureInfo.InvariantCulture) + " pt     Total-reflection band  " + view.TirBandPt.ToString("0.0", CultureInfo.InvariantCulture) + " pt";
            using (SolidBrush t = new SolidBrush(pal.Text)) g.DrawString(line, font, t, new PointF(4 * s, y));
            y += (int)(19 * s);
            if (view.Ring)
            {
                PointF c = new PointF(11 * s, y + 7 * s);
                PointF[] tri = { new PointF(c.X, c.Y - 6.5f * s), new PointF(c.X + 7 * s, c.Y + 5.5f * s), new PointF(c.X - 7 * s, c.Y + 5.5f * s) };
                using (SolidBrush b = new SolidBrush(pal.Warn)) g.FillPolygon(b, tri);
                using (SolidBrush w = new SolidBrush(pal.Panel))
                using (StringFormat f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("!", fontBold, w, new RectangleF(c.X - 7 * s, c.Y - 5 * s, 14 * s, 14 * s), f);
                using (SolidBrush t = new SolidBrush(pal.Warn))
                    g.DrawString("This underside makes a silver ring (total internal reflection over " + view.TirBandPt.ToString("0.0", CultureInfo.InvariantCulture) + " pt).", fontBold, t, new PointF(22 * s, y));
            }
            else
            {
                string msg = hint.Length > 0 ? hint : (presetNote.Length > 0 ? presetNote + "." : "Drag the points; double-click a curve to add one, right-click a point to remove it.");
                using (SolidBrush t = new SolidBrush(pal.TextDim)) g.DrawString(msg, font, t, new RectangleF(4 * s, y, ClientSize.Width - 8 * s, 32 * s));
            }
            // the edge radius
            float fillet = Editing().Fillet;
            int ty = track.Top + track.Height / 2;
            using (SolidBrush t = new SolidBrush(pal.Text))
            using (StringFormat f = new StringFormat { LineAlignment = StringAlignment.Center })
            {
                g.DrawString("Edge radius", font, t, new RectangleF(4 * s, track.Top - 3 * s, 78 * s, track.Height + 6 * s), f);
                g.DrawString(fillet.ToString("0.0", CultureInfo.InvariantCulture) + " pt", font, t, new RectangleF(track.Right + 8 * s, track.Top - 3 * s, 56 * s, track.Height + 6 * s), f);
            }
            using (Pen p = new Pen(pal.PanelEdge, 3f * Math.Max(1f, s * 0.8f)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawLine(p, track.Left, ty, track.Right, ty);
            }
            float kx = track.Left + track.Width * Math.Min(1f, fillet / 8f);
            using (Pen p = new Pen(pal.TopHandle, 3f * Math.Max(1f, s * 0.8f)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawLine(p, track.Left, ty, kx, ty);
            }
            float kr = (hoverTrack > 0 || dragTrack ? 7f : 6f) * Math.Max(1f, s * 0.9f);
            using (SolidBrush b = new SolidBrush(pal.Panel))
            using (Pen p = new Pen(pal.TopHandle, 2f))
            {
                g.FillEllipse(b, kx - kr, ty - kr, kr * 2, kr * 2);
                g.DrawEllipse(p, kx - kr, ty - kr, kr * 2, kr * 2);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // The mouse.

        int HitHandle(Point m)
        {
            float best = (11 * sc) * (11 * sc);
            int found = -1;
            for (int i = 0; i < handles.Count; i++)
            {
                if (handles[i].Fixed) continue;
                float dx = handles[i].Pos.X - m.X, dy = handles[i].Pos.Y - m.Y, d = dx * dx + dy * dy;
                if (d <= best) { best = d; found = i; }
            }
            return found;
        }

        bool OnTrack(Point m)
        {
            return m.X >= track.Left - 8 * sc && m.X <= track.Right + 8 * sc && m.Y >= track.Top - 6 * sc && m.Y <= track.Bottom + 6 * sc;
        }

        void SetFillet(Point m)
        {
            float v = (m.X - track.Left) / (float)track.Width * 8f;
            v = (float)Math.Round(Math.Max(0f, Math.Min(8f, v)) * 2) / 2f;
            GlassProfile p = Editing();
            if (profile != null && Math.Abs(profile.Fillet - v) < 1e-4f) return;
            profile = GlassProfile.Create("Custom", p.TopPoints, p.BottomPoints, p.Slab, v);
            Recompute();
            SelectPresetFor(profile);
            Touched();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (view == null) return;
            if (e.Button == MouseButtons.Left && presetRect.Contains(e.Location) && presetMenu != null)
            {
                presetMenu.Show(this, new Point(presetRect.Left, presetRect.Bottom + 1));
                return;
            }
            if (e.Button == MouseButtons.Left && OnTrack(e.Location)) { dragTrack = true; SetFillet(e.Location); return; }
            int h = HitHandle(e.Location);
            if (e.Button == MouseButtons.Right)
            {
                if (h >= 0 && !handles[h].End && !handles[h].Face) RemovePoint(handles[h]);
                return;
            }
            if (e.Button != MouseButtons.Left || h < 0) return;
            dragging = h;
            dragTop = handles[h].Top;
            dragFace = handles[h].Face;
            dragEnd = handles[h].End;
            GlassProfile p = Editing();
            dragFaceB = p.Slab + p.TopPoints[p.TopPoints.Count - 1].V;
            frozen = true;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (view == null) return;
            if (dragTrack) { SetFillet(e.Location); return; }
            if (dragging >= 0)
            {
                dragTo = e.Location;
                movePending = true;
                if (!moveTimer.Enabled) moveTimer.Start();
                return;
            }
            int h = HitHandle(e.Location);
            int tr = OnTrack(e.Location) ? 1 : 0;
            bool hot = presetRect.Contains(e.Location);
            if (h != hover || tr != hoverTrack || hot != presetHot)
            {
                hover = h;
                hoverTrack = tr;
                presetHot = hot;
                hint = "";
                if (h >= 0) hint = Describe(handles[h]);
                Cursor = h >= 0 || tr > 0 || hot ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        string Describe(Grip h)
        {
            GlassProfile p = Editing();
            float B = view.BezelPt, thick = Math.Max(0.01f, view.Thick);
            ProfilePoint pt = h.Top ? p.TopPoints[h.Index] : p.BottomPoints[h.Index];
            if (h.Face) return "Face: drag up or down to change the glass's thickness (" + (p.Slab + p.TopPoints[p.TopPoints.Count - 1].V).ToString("0.00", CultureInfo.InvariantCulture) + " bezels)";
            return (h.Top ? "Top" : "Underside") + " point at " + pt.T.ToString("0.00", CultureInfo.InvariantCulture) + " of the bezel, "
                + (h.Top ? "drops " : "rises ") + (pt.V * B * thick).ToString("0.0", CultureInfo.InvariantCulture) + " pt";
        }

        // Move the dragged point to where the mouse is.
        void ApplyDrag()
        {
            if (dragging < 0 || dragging >= handles.Count) return;
            Grip h = handles[dragging];
            GlassProfile p = Editing();
            List<ProfilePoint> top = new List<ProfilePoint>(p.TopPoints), bot = new List<ProfilePoint>(p.BottomPoints);
            float B = view.BezelPt, thick = Math.Max(0.01f, view.Thick), unit = B * thick;   // (one bezel width, in points, at this thickness)
            float t = (dragTo.X - ox) / (sx * B);
            float slab = p.Slab;
            if (dragFace)
            {
                // the thickness: the face's height above the flat bottom, the rim's height (and the shoulder) as they were
                float face = (oy - view.GapPt * sy - dragTo.Y) / sy / unit;
                slab = Math.Max(GlassProfile.MinGlass, Math.Min(GlassProfile.MaxSlab, face - top[top.Count - 1].V));
            }
            else
            {
                List<ProfilePoint> list = h.Top ? top : bot;
                ProfilePoint pt = list[h.Index];
                if (!dragEnd)
                {
                    float lo = list[h.Index - 1].T + 0.012f, hi = list[h.Index + 1].T - 0.012f;
                    pt.T = Math.Max(lo, Math.Min(hi, t));
                }
                float v;
                if (h.Top) v = (dragTo.Y - (oy - (view.GapPt + view.FaceThicknessPt) * sy)) / sy / unit;
                else v = (oy - view.GapPt * sy - dragTo.Y) / sy / unit;
                v = Math.Max(0f, Math.Min(GlassProfile.MaxValue, v));
                if (h.Top && dragEnd)
                {
                    // the rim's height: the face stays where it is, the slab takes up the difference
                    v = Math.Min(v, dragFaceB - GlassProfile.MinGlass);
                    slab = Math.Max(GlassProfile.MinGlass, dragFaceB - v);
                }
                pt.V = v;
                list[h.Index] = pt;
            }
            profile = GlassProfile.Create("Custom", top, bot, slab, p.Fillet);
            view = GlassSideView.Compute(shape.WithProfile(profile), look, cardW, cardH, rayCount);
            hint = Describe(handles[Math.Min(dragging, handles.Count - 1)]);
            Invalidate();
            Touched();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragTrack) { dragTrack = false; Invalidate(); return; }
            if (dragging >= 0)
            {
                if (movePending) { movePending = false; moveTimer.Stop(); ApplyDrag(); }
                dragging = -1;
                frozen = false;
                Capture = false;
                if (profile != null) SelectPresetFor(profile);
                hint = "";
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (dragging < 0 && (hover >= 0 || hoverTrack > 0 || presetHot)) { hover = -1; hoverTrack = 0; presetHot = false; hint = ""; Invalidate(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left || view == null || HitHandle(e.Location) >= 0 || !panel.Contains(e.Location)) return;
            // the nearer of the two curves, at the mouse's x
            float B = view.BezelPt;
            float t = (e.X - ox) / (sx * B);
            if (t < 0.02f || t > 0.98f) return;
            float xPt = t * B;
            float yTop = Px(xPt, HeightAt(view.Top, xPt)).Y, yBot = Px(xPt, HeightAt(view.Bottom, xPt)).Y;
            float dTop = Math.Abs(e.Y - yTop), dBot = Math.Abs(e.Y - yBot);
            if (Math.Min(dTop, dBot) > 14 * sc) return;
            bool top = dTop <= dBot;
            GlassProfile p = Editing();
            List<ProfilePoint> list = new List<ProfilePoint>(top ? p.TopPoints : p.BottomPoints);
            for (int i = 0; i < list.Count; i++) if (Math.Abs(list[i].T - t) < 0.03f) return;
            if (list.Count >= GlassProfile.MaxPoints - 1) return;
            // (on the curve as it is now: the shape does not change, it only gains a handle)
            float unit = Math.Max(0.01f, view.Thick) * B;
            float v = top ? (view.GapPt + view.FaceThicknessPt - HeightAt(view.Top, xPt)) / unit : HeightAt(view.Bottom, xPt) - view.GapPt;
            if (!top) v = (HeightAt(view.Bottom, xPt) - view.GapPt) / unit;
            list.Add(new ProfilePoint(t, Math.Max(0f, v)));
            profile = GlassProfile.Create("Custom", top ? list : new List<ProfilePoint>(p.TopPoints), top ? new List<ProfilePoint>(p.BottomPoints) : list, p.Slab, p.Fillet);
            Recompute();
            SelectPresetFor(profile);
            Touched();
        }

        void RemovePoint(Grip h)
        {
            GlassProfile p = Editing();
            List<ProfilePoint> top = new List<ProfilePoint>(p.TopPoints), bot = new List<ProfilePoint>(p.BottomPoints);
            List<ProfilePoint> list = h.Top ? top : bot;
            if (h.Index <= 0 || h.Index >= list.Count - 1) return;
            list.RemoveAt(h.Index);
            profile = GlassProfile.Create("Custom", top, bot, p.Slab, p.Fillet);
            hover = -1;
            Recompute();
            SelectPresetFor(profile);
            Touched();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                changeTimer.Dispose();
                moveTimer.Dispose();
                if (presetMenu != null) presetMenu.Dispose();
                font.Dispose();
                fontBold.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
