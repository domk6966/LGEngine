using System;
using System.Runtime.InteropServices;

namespace LGEngine
{
    // A 32-bit top-down bitmap with its own device context, readable and writable as int[].
    public sealed class Dib : IDisposable
    {
        public readonly int Width, Height;
        public readonly IntPtr Dc;
        readonly IntPtr bitmap, oldBitmap, bits;
        bool disposed;

        public Dib(int width, int height)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            Gdi.BITMAPINFOHEADER header = new Gdi.BITMAPINFOHEADER();
            header.biSize = Marshal.SizeOf(typeof(Gdi.BITMAPINFOHEADER));
            header.biWidth = Width;
            header.biHeight = -Height;
            header.biPlanes = 1;
            header.biBitCount = 32;

            IntPtr screen = Gdi.GetDC(IntPtr.Zero);
            Dc = Gdi.CreateCompatibleDC(screen);
            bitmap = Gdi.CreateDIBSection(screen, ref header, 0, out bits, IntPtr.Zero, 0);
            Gdi.ReleaseDC(IntPtr.Zero, screen);
            if (Dc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                if (bitmap != IntPtr.Zero) Gdi.DeleteObject(bitmap);
                if (Dc != IntPtr.Zero) Gdi.DeleteDC(Dc);
                disposed = true;
                throw new InvalidOperationException("could not create an off-screen bitmap");
            }
            oldBitmap = Gdi.SelectObject(Dc, bitmap);
        }

        public void Read(int[] target) { Marshal.Copy(bits, target, 0, Width * Height); }
        // Rows firstRow to firstRow + rows only.
        public void Read(int[] target, int firstRow, int rows)
        {
            firstRow = Math.Max(0, firstRow);
            rows = Math.Min(Height - firstRow, rows);
            if (rows > 0) Marshal.Copy(IntPtr.Add(bits, firstRow * Width * 4), target, 0, rows * Width);
        }
        public void Write(int[] source) { Marshal.Copy(source, 0, bits, Width * Height); }
        public void Write(int[] source, int firstRow, int rows)
        {
            firstRow = Math.Max(0, firstRow);
            rows = Math.Min(Height - firstRow, rows);
            if (rows > 0) Marshal.Copy(source, firstRow * Width, IntPtr.Add(bits, firstRow * Width * 4), rows * Width);
        }

        // The pixels within r only (rows whole when r is nearly as wide as the picture).
        public void WriteRect(int[] source, System.Drawing.Rectangle r)
        {
            r = System.Drawing.Rectangle.Intersect(r, new System.Drawing.Rectangle(0, 0, Width, Height));
            if (r.Width <= 0 || r.Height <= 0) return;
            if (r.Width > Width * 3 / 4) { Write(source, r.Top, r.Height); return; }
            for (int y = r.Top; y < r.Bottom; y++) Marshal.Copy(source, y * Width + r.Left, IntPtr.Add(bits, (y * Width + r.Left) * 4), r.Width);
        }

        // Columns x0 to x1 of every row only.
        public void WriteColumns(int[] source, int x0, int x1)
        {
            x0 = Math.Max(0, x0);
            x1 = Math.Min(Width, x1);
            if (x1 <= x0) return;
            if (x1 - x0 > Width * 3 / 4) { Write(source); return; }
            for (int y = 0; y < Height; y++) Marshal.Copy(source, y * Width + x0, IntPtr.Add(bits, (y * Width + x0) * 4), x1 - x0);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Gdi.SelectObject(Dc, oldBitmap);
            Gdi.DeleteObject(bitmap);
            Gdi.DeleteDC(Dc);
        }
    }
}
