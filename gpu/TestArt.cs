using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace LGGpu
{
    // Test pictures shared by the GPU rig and the CPU reference: a desktop-like scene and the
    // contents of a notification and of an indicator.
    internal static class TestArt
    {
        // A test picture: gradients, sharp shapes, text, like a desktop (0xFFRRGGBB).
        public static int[] Scene(int w, int h, string kind)
        {
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    if (kind == "white") g.Clear(Color.White);
                    else if (kind == "dark") g.Clear(Color.FromArgb(24, 26, 32));
                    else
                    {
                        using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(18, 52, 110), Color.FromArgb(210, 90, 40), 35f)) g.FillRectangle(lg, 0, 0, w, h);
                        g.FillRectangle(Brushes.White, 0, 0, w / 2, h / 2);
                        g.FillRectangle(new SolidBrush(Color.FromArgb(30, 30, 34)), w / 2, 0, w / 2, h / 2 - 60);
                        g.FillEllipse(new SolidBrush(Color.FromArgb(40, 200, 120)), w * 0.55f, h * 0.55f, 260, 260);
                        g.FillRectangle(new SolidBrush(Color.FromArgb(255, 220, 40)), w * 0.1f, h * 0.6f, 180, 220);
                        g.FillRectangle(new SolidBrush(Color.FromArgb(230, 40, 60)), w * 0.3f, h * 0.7f, 240, 90);
                        using (Pen p = new Pen(Color.Black, 2)) for (int i = 0; i < 12; i++) g.DrawLine(p, 40 + i * 110, 10, 40 + i * 110, h / 2 - 10);
                        using (Font f = new Font("Segoe UI", 18)) for (int i = 0; i < 9; i++) g.DrawString("The quick brown fox jumps over the lazy dog 0123456789", f, Brushes.Black, 30, 20 + i * 44);
                        using (Font f = new Font("Segoe UI", 18)) for (int i = 0; i < 7; i++) g.DrawString("Lorem ipsum dolor sit amet, consectetur adipiscing", f, Brushes.White, w / 2 + 20, 20 + i * 44);
                        using (Font f = new Font("Consolas", 14)) for (int i = 0; i < 6; i++) g.DrawString("taskbar  notifications  volume  brightness  search", f, Brushes.White, 30, h / 2 + 10 + i * 30);
                    }
                }
                return Pixels(bmp);
            }
        }

        // A notification's contents: white text and an icon (premultiplied, transparent elsewhere).
        public static int[] ToastContent(int w, int h, float k)
        {
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    float pad = 20 * k;
                    g.FillRectangle(new SolidBrush(Color.FromArgb(76, 194, 255)), pad, pad, 24 * k, 24 * k);
                    using (Font f = new Font("Segoe UI", 11 * k, FontStyle.Bold)) g.DrawString("Liquid Glass", f, Brushes.White, pad + 32 * k, pad);
                    using (Font f = new Font("Segoe UI", 10 * k)) g.DrawString("A notification drawn over the desktop: the glass, its rim and shadow,\nand this text. The underlay keeps it readable.", f, Brushes.White, pad, pad + 36 * k);
                    using (Font f = new Font("Segoe UI", 9 * k)) g.DrawString("LGEngine - now", f, new SolidBrush(Color.FromArgb(200, 255, 255, 255)), pad, h - pad - 16 * k);
                }
                int ip = (int)(20 * k), isz = (int)(24 * k);
                return InkMarked(bmp, new Rectangle(ip, ip, isz, isz));
            }
        }

        public static int[] LensContent(int w, int h, float k)
        {
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float cy = h / 2f, x0 = 24 * k, x1 = w - 24 * k;
                    g.FillEllipse(Brushes.White, x0 - 6 * k, cy - 10 * k, 20 * k, 20 * k);
                    using (Pen p = new Pen(Color.FromArgb(110, 255, 255, 255), 4 * k)) g.DrawLine(p, x0 + 24 * k, cy, x1, cy);
                    using (Pen p = new Pen(Color.White, 4 * k)) g.DrawLine(p, x0 + 24 * k, cy, x0 + (x1 - x0) * 0.6f, cy);
                }
                return InkMarked(bmp, new Rectangle(0, 0, 0, 0));
            }
        }

        public static int[] Pixels(Bitmap bmp)
        {
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            int[] px = new int[bmp.Width * bmp.Height];
            try { Marshal.Copy(d.Scan0, px, 0, px.Length); }
            finally { bmp.UnlockBits(d); }
            return px;
        }

        // Marks contents the way GlassOverlay.MarkInk does: grey ink (text, lines) gets an even
        // alpha, so light glass may turn it dark; an icon's own colours an odd one, so it stays.
        public static int[] InkMarked(Bitmap bmp, Rectangle icon)
        {
            int[] px = Pixels(bmp);
            int w = bmp.Width;
            for (int i = 0; i < px.Length; i++)
            {
                int p = px[i], a = (int)((uint)p >> 24);
                if (a == 0) continue;
                bool isIcon = icon.Contains(i % w, i / w);
                if (isIcon) { if ((a & 1) == 0) a = a > 0 ? a - 1 : 1; }
                else if ((a & 1) != 0) a = a - 1;
                int r = Math.Min(a, (p >> 16) & 255), g = Math.Min(a, (p >> 8) & 255), b = Math.Min(a, p & 255);
                px[i] = (a << 24) | (r << 16) | (g << 8) | b;
            }
            return px;
        }

        public static void SavePng(int[] px, int w, int h, string path)
        {
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try { Marshal.Copy(px, 0, d.Scan0, px.Length); }
                finally { bmp.UnlockBits(d); }
                bmp.Save(path, ImageFormat.Png);
            }
        }

        // Lays a premultiplied picture over an opaque one, in place.
        public static void Over(int[] dst, int dstW, int dstH, int[] src, int srcW, int srcH, int atX, int atY)
        {
            for (int y = 0; y < srcH; y++)
            {
                int dy = y + atY;
                if (dy < 0 || dy >= dstH) continue;
                for (int x = 0; x < srcW; x++)
                {
                    int dx = x + atX;
                    if (dx < 0 || dx >= dstW) continue;
                    int s = src[y * srcW + x], a = (int)((uint)s >> 24);
                    if (a == 0) continue;
                    int d = dst[dy * dstW + dx], keep = 255 - a;
                    int r = (((d >> 16) & 255) * keep) / 255 + ((s >> 16) & 255);
                    int g = (((d >> 8) & 255) * keep) / 255 + ((s >> 8) & 255);
                    int b = ((d & 255) * keep) / 255 + (s & 255);
                    dst[dy * dstW + dx] = unchecked((int)0xFF000000) | (Math.Min(255, r) << 16) | (Math.Min(255, g) << 8) | Math.Min(255, b);
                }
            }
        }
    }
}
