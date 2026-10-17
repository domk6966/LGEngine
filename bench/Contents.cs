using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace LGBench
{
    // Sample contents for each kind of glass, as the app hands them to the engine: the pop-up's ink
    // (text, symbols, a slider) as premultiplied ARGB with the card's own colour removed; and ways of
    // changing them (a slider moving, a button lighting up) for the contents-only measurements.
    static class Contents
    {
        static int[] Draw(int w, int h, Action<Graphics> draw)
        {
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    draw(g);
                }
                BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                int[] px = new int[w * h];
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                bmp.UnlockBits(data);
                // marked as the app marks keyed contents (GlassOverlay.MarkInk, roughly): odd alpha for colour,
                // even for grey ink, which light glass turns dark
                for (int i = 0; i < px.Length; i++)
                {
                    int p = px[i], a = (p >> 24) & 255;
                    if (a == 0) continue;
                    int r = (p >> 16) & 255, gg = (p >> 8) & 255, b = p & 255;
                    int max = Math.Max(r, Math.Max(gg, b)), min = Math.Min(r, Math.Min(gg, b));
                    int want = (max - min) * 255 > 56 * a ? (a | 1) : (a & ~1);
                    if (want == 0) { px[i] = 0; continue; }
                    px[i] = (want << 24) | (Math.Min(r, want) << 16) | (Math.Min(gg, want) << 8) | Math.Min(b, want);
                }
                return px;
            }
        }

        // A notification: an app icon, a title, two lines of text, and two buttons.
        public static int[] Card(int w, int h, float k, int frame)
        {
            return Draw(w, h, delegate(Graphics g)
            {
                using (Brush icon = new SolidBrush(Color.FromArgb(255, 70, 150, 255))) g.FillEllipse(icon, 20 * k, 20 * k, 40 * k, 40 * k);
                using (Font title = new Font("Segoe UI Semibold", 15f * k, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Font body = new Font("Segoe UI", 14f * k, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Brush white = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                using (Brush grey = new SolidBrush(Color.FromArgb(255, 210, 210, 214)))
                {
                    g.DrawString("Messages  " + frame, title, white, 72 * k, 18 * k);
                    g.DrawString("Alex: are we still on for the review at 3? I have the", body, grey, 72 * k, 44 * k);
                    g.DrawString("numbers from the benchmark ready to go through.", body, grey, 72 * k, 66 * k);
                    using (Pen edge = new Pen(Color.FromArgb(120, 255, 255, 255), 1f * k))
                    using (Brush fill = new SolidBrush(Color.FromArgb(frame % 2 == 1 ? 60 : 30, 255, 255, 255)))
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            Rectangle r = new Rectangle((int)((20 + i * 185) * k), (int)(100 * k), (int)(170 * k), (int)(34 * k));
                            using (GraphicsPath p = Scene.Rounded(r, 8 * k)) { g.FillPath(fill, p); g.DrawPath(edge, p); }
                            g.DrawString(i == 0 ? "Reply" : "Dismiss", body, white, r.Left + 56 * k, r.Top + 7 * k);
                        }
                    }
                }
            });
        }

        // The volume indicator: a speaker symbol, a slider with a knob at 'level' (0-1), the number.
        public static int[] Osd(int w, int h, float k, float level)
        {
            return Draw(w, h, delegate(Graphics g)
            {
                using (Brush white = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                using (Brush track = new SolidBrush(Color.FromArgb(90, 255, 255, 255)))
                using (Font f = new Font("Segoe UI", 13f * k, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    // the symbol: a box and a wedge
                    g.FillRectangle(white, 16 * k, 19 * k, 6 * k, 8 * k);
                    g.FillPolygon(white, new PointF[] { new PointF(22 * k, 19 * k), new PointF(30 * k, 13 * k), new PointF(30 * k, 33 * k), new PointF(22 * k, 27 * k) });
                    float x0 = 44 * k, x1 = w - 48 * k, y = h / 2f;
                    using (GraphicsPath p = Scene.Rounded(Rectangle.Round(new RectangleF(x0, y - 2 * k, x1 - x0, 4 * k)), 2 * k)) g.FillPath(track, p);
                    using (GraphicsPath p = Scene.Rounded(Rectangle.Round(new RectangleF(x0, y - 2 * k, (x1 - x0) * level, 4 * k)), 2 * k)) g.FillPath(white, p);
                    float kx = x0 + (x1 - x0) * level;
                    g.FillEllipse(white, kx - 7 * k, y - 7 * k, 14 * k, 14 * k);
                    g.DrawString(((int)(level * 100)).ToString(), f, white, w - 40 * k, y - 9 * k);
                }
            });
        }

        // A dock capsule: a row of app "icons".
        public static int[] Dock(int w, int h, float k, int lit)
        {
            return Draw(w, h, delegate(Graphics g)
            {
                int n = Math.Max(1, (int)((w - 16 * k) / (48 * k)));
                for (int i = 0; i < n; i++)
                {
                    float x = 8 * k + i * 48 * k + 6 * k, y = (h - 36 * k) / 2;
                    Color c = Color.FromArgb(255, (i * 70 + 60) & 255, (i * 40 + 120) & 255, (i * 110 + 200) & 255);
                    using (Brush b = new SolidBrush(c)) using (GraphicsPath p = Scene.Rounded(Rectangle.Round(new RectangleF(x, y, 36 * k, 36 * k)), 9 * k)) g.FillPath(b, p);
                    if (i == lit) using (Brush dot = new SolidBrush(Color.White)) g.FillEllipse(dot, x + 15 * k, h - 6 * k, 6 * k, 4 * k);
                }
            });
        }

        // The taskbar search pill: a magnifier and a prompt.
        public static int[] Pill(int w, int h, float k, bool hover)
        {
            return Draw(w, h, delegate(Graphics g)
            {
                using (Pen p = new Pen(Color.FromArgb(hover ? 255 : 220, 255, 255, 255), 2f * k))
                {
                    g.DrawEllipse(p, 14 * k, h / 2f - 9 * k, 13 * k, 13 * k);
                    g.DrawLine(p, 25 * k, h / 2f + 3 * k, 31 * k, h / 2f + 9 * k);
                }
                using (Font f = new Font("Segoe UI", 13f * k, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Brush ink = new SolidBrush(Color.FromArgb(hover ? 255 : 230, 255, 255, 255))) g.DrawString("Search", f, ink, 40 * k, h / 2f - 9 * k);
            });
        }
    }
}
