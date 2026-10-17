using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace LGBench
{
    // A picture of a screen, as int[] (0x00RRGGBB), with the ways the benchmark changes it.
    sealed class Scene
    {
        public readonly string Name;
        public readonly int W, H;
        public readonly int[] Px;

        public Scene(string name, int w, int h, int[] px)
        {
            Name = name;
            W = w;
            H = h;
            Px = px;
        }

        // The part of the screen a glass map reads, shrunk by step into texels (each the average of
        // step x step pixels; anything off the picture repeats its nearest pixel): exactly what the
        // live feed (ScreenFeed/Duplicator.Read) hands the engine.
        public void ReadArea(Rectangle area, int step, int[] into, int texW, int texH)
        {
            int n = step * step;
            for (int ty = 0; ty < texH; ty++)
            {
                int at = ty * texW;
                for (int tx = 0; tx < texW; tx++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int v = 0; v < step; v++)
                    {
                        int y = Math.Max(0, Math.Min(H - 1, area.Top + ty * step + v));
                        int row = y * W;
                        for (int u = 0; u < step; u++)
                        {
                            int x = Math.Max(0, Math.Min(W - 1, area.Left + tx * step + u));
                            int p = Px[row + x];
                            r += (p >> 16) & 255; g += (p >> 8) & 255; b += p & 255;
                        }
                    }
                    into[at + tx] = ((r / n) << 16) | ((g / n) << 8) | (b / n);
                }
            }
        }

        // A copy with a box of it changed (a window redrawn behind the glass): filled with a flat colour
        // and some lines, so the change is plain.
        public Scene Changed(Rectangle box, int seed)
        {
            int[] px = (int[])Px.Clone();
            Random rnd = new Random(seed);
            int colour = (rnd.Next(40, 230) << 16) | (rnd.Next(40, 230) << 8) | rnd.Next(40, 230);
            box = Rectangle.Intersect(box, new Rectangle(0, 0, W, H));
            for (int y = box.Top; y < box.Bottom; y++)
            {
                int row = y * W;
                bool line = ((y - box.Top) / 6) % 5 == 0;
                for (int x = box.Left; x < box.Right; x++) px[row + x] = line ? (colour ^ 0x404040) : colour;
            }
            return new Scene(Name + "+box", W, H, px);
        }

        public static int[] Pixels(Bitmap bmp)
        {
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            int[] px = new int[bmp.Width * bmp.Height];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            bmp.UnlockBits(data);
            for (int i = 0; i < px.Length; i++) px[i] &= 0xFFFFFF;
            return px;
        }

        public static void Save(int[] px, int w, int h, string path)
        {
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb))
            {
                BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                Marshal.Copy(px, 0, data.Scan0, w * h);
                bmp.UnlockBits(data);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        static Bitmap Blank(int w, int h, Color fill, out Graphics g)
        {
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(fill);
            return bmp;
        }

        static Scene From(string name, Bitmap bmp)
        {
            Scene s = new Scene(name, bmp.Width, bmp.Height, Pixels(bmp));
            bmp.Dispose();
            return s;
        }

        // The standard scenes, all 1920 x 1080 and deterministic.
        public static Scene Make(string name, string wallpaper)
        {
            const int W = 1920, H = 1080;
            Graphics g;
            switch (name)
            {
                case "busy":
                {
                    // a light document full of text, with a dark panel at the right and some colour
                    Bitmap bmp = Blank(W, H, Color.FromArgb(236, 238, 242), out g);
                    using (Brush paper = new SolidBrush(Color.FromArgb(250, 250, 250))) g.FillRectangle(paper, 120, 60, 1200, 960);
                    using (Font f = new Font("Segoe UI", 26f, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (Font hd = new Font("Segoe UI Semibold", 40f, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (Brush ink = new SolidBrush(Color.FromArgb(30, 30, 30)))
                    {
                        g.DrawString("Quarterly notes", hd, ink, 150, 84);
                        string line = "The quick brown fox jumps over the lazy dog and keeps going, 0123456789.";
                        for (int i = 0; i < 22; i++) g.DrawString(line, f, ink, new RectangleF(150, 160 + i * 38, 1140, 38));
                    }
                    using (Brush red = new SolidBrush(Color.FromArgb(232, 60, 60))) g.FillEllipse(red, 1380, 160, 150, 150);
                    using (Brush yellow = new SolidBrush(Color.FromArgb(250, 200, 40))) g.FillRectangle(yellow, 1400, 340, 220, 90);
                    using (Brush panel = new SolidBrush(Color.FromArgb(28, 30, 36))) g.FillRectangle(panel, 1420, 480, 460, 540);
                    using (Pen p = new Pen(Color.FromArgb(90, 200, 255), 3f))
                        for (int i = 0; i < 12; i++) g.DrawLine(p, 1440, 510 + i * 34, 1860 - (i * 37 % 160), 510 + i * 34);
                    g.Dispose();
                    return From(name, bmp);
                }
                case "wall":
                {
                    Bitmap bmp = Blank(W, H, Color.FromArgb(30, 60, 120), out g);
                    bool drawn = false;
                    if (!string.IsNullOrEmpty(wallpaper) && File.Exists(wallpaper))
                    {
                        try
                        {
                            using (Image wall = Image.FromFile(wallpaper)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(wall, 0, 0, W, H); }
                            drawn = true;
                        }
                        catch (Exception) { }
                    }
                    if (!drawn)
                    {
                        // a stand-in wallpaper: broad colour sweeps with some soft shapes
                        using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(18, 40, 110), Color.FromArgb(220, 120, 60), 35f)) g.FillRectangle(lg, 0, 0, W, H);
                        Random rnd = new Random(7);
                        for (int i = 0; i < 18; i++)
                        {
                            int cx = rnd.Next(W), cy = rnd.Next(H), r = rnd.Next(80, 400);
                            using (Brush b = new SolidBrush(Color.FromArgb(70, rnd.Next(256), rnd.Next(256), rnd.Next(256)))) g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
                        }
                    }
                    g.Dispose();
                    return From(name, bmp);
                }
                case "grid":
                {
                    Bitmap bmp = Blank(W, H, Color.White, out g);
                    g.SmoothingMode = SmoothingMode.None;
                    using (Pen p = new Pen(Color.FromArgb(205, 205, 205), 1f))
                    {
                        for (int x = 0; x < W; x += 40) g.DrawLine(p, x, 0, x, H);
                        for (int y = 0; y < H; y += 40) g.DrawLine(p, 0, y, W, y);
                    }
                    g.Dispose();
                    return From(name, bmp);
                }
                case "dark":
                {
                    Bitmap bmp = Blank(W, H, Color.FromArgb(17, 17, 17), out g);
                    using (Font f = new Font("Segoe UI", 24f, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (Brush grey = new SolidBrush(Color.FromArgb(150, 150, 150)))
                    using (Brush box = new SolidBrush(Color.FromArgb(32, 32, 34)))
                    using (Pen border = new Pen(Color.FromArgb(70, 70, 74), 2f))
                    using (Brush panel = new SolidBrush(Color.FromArgb(52, 54, 60)))
                    using (Brush dot = new SolidBrush(Color.FromArgb(200, 200, 205)))
                    {
                        for (int i = 0; i < 6; i++) g.DrawString("Extra", f, grey, 40, 60 + i * 120);
                        for (int i = 0; i < 5; i++)
                        {
                            Rectangle r = new Rectangle(160, 40 + i * 180, 700, 90);
                            using (GraphicsPath p = Rounded(r, 24)) { g.FillPath(box, p); g.DrawPath(border, p); }
                            g.FillEllipse(dot, r.Left + 20, r.Top + 30, 30, 30);
                            g.DrawString("Ask anything", f, grey, r.Left + 70, r.Top + 28);
                        }
                        using (GraphicsPath p = Rounded(new Rectangle(950, 120, 500, 360), 30)) g.FillPath(panel, p);
                        for (int i = 0; i < 5; i++) g.DrawString("Settings  >  Display  >  Night light", f, grey, 980, 150 + i * 60);
                    }
                    g.Dispose();
                    return From(name, bmp);
                }
                case "lop":
                {
                    // lopsided: the left half a dark window, the right half a white one, with text on both
                    Bitmap bmp = Blank(W, H, Color.FromArgb(32, 34, 40), out g);
                    using (Brush paper = new SolidBrush(Color.FromArgb(248, 248, 248))) g.FillRectangle(paper, W / 2, 0, W / 2, H);
                    using (Font f = new Font("Segoe UI", 22f, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (Brush ink = new SolidBrush(Color.FromArgb(40, 40, 40)))
                    using (Brush light = new SolidBrush(Color.FromArgb(200, 200, 200)))
                    {
                        for (int i = 0; i < 28; i++) { g.DrawString("Settings  >  System  >  Display", f, light, 30, 20 + i * 36); g.DrawString("Settings  >  System  >  Display", f, ink, W / 2 + 30, 20 + i * 36); }
                    }
                    g.Dispose();
                    return From(name, bmp);
                }
                case "light":
                {
                    // plain light paper with a little faint text: light glass with dark ink
                    Bitmap bmp = Blank(W, H, Color.FromArgb(246, 246, 248), out g);
                    using (Font f = new Font("Segoe UI", 22f, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (Brush ink = new SolidBrush(Color.FromArgb(120, 120, 128)))
                        for (int i = 0; i < 10; i++) g.DrawString("A quiet page with very little on it", f, ink, 200, 100 + i * 90);
                    g.Dispose();
                    return From(name, bmp);
                }
            }
            throw new ArgumentException("no such scene: " + name);
        }

        public static GraphicsPath Rounded(Rectangle r, float radius)
        {
            float d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
