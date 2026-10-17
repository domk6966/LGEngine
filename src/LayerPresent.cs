using System;
using System.Runtime.InteropServices;

namespace LGEngine
{
    // Hands Windows only some rows of a layered window's picture (src: its DC, all of it already up
    // to date), where nothing else about the window changes: cheaper than the whole picture every
    // time, for an indicator whose slider's band is all that moves. False if Windows would not
    // (the caller then hands over the whole picture). See GlassOverlay.Present.
    public static class LayerPresent
    {
        static IntPtr scratch;

        public static bool Rows(IntPtr hwnd, IntPtr srcDc, int alpha, int w, int h, int fromRow, int toRow)
        {
            return Rect(hwnd, srcDc, alpha, w, h, new System.Drawing.Rectangle(0, fromRow, w, toRow - fromRow));
        }

        // The same for any rectangle of the picture (a dirty rect from GlassMap.Shade).
        public static bool Rect(IntPtr hwnd, IntPtr srcDc, int alpha, int w, int h, System.Drawing.Rectangle r)
        {
            r = System.Drawing.Rectangle.Intersect(r, new System.Drawing.Rectangle(0, 0, w, h));
            if (r.Width <= 0 || r.Height <= 0) return true;
            if (scratch == IntPtr.Zero) scratch = Marshal.AllocHGlobal(64);
            // scratch: POINT src (0..7), SIZE (8..15), BLENDFUNCTION (16..19), RECT dirty (20..35)
            Marshal.WriteInt32(scratch, 0, 0);
            Marshal.WriteInt32(scratch, 4, 0);
            Marshal.WriteInt32(scratch, 8, w);
            Marshal.WriteInt32(scratch, 12, h);
            Marshal.WriteInt32(scratch, 16, (Math.Max(0, Math.Min(255, alpha)) << 16) | (1 << 24));   // AC_SRC_OVER, 0, alpha, AC_SRC_ALPHA
            Marshal.WriteInt32(scratch, 20, r.Left);
            Marshal.WriteInt32(scratch, 24, r.Top);
            Marshal.WriteInt32(scratch, 28, r.Right);
            Marshal.WriteInt32(scratch, 32, r.Bottom);
            Info info = new Info();
            info.cbSize = Marshal.SizeOf(typeof(Info));
            info.hdcSrc = srcDc;
            info.pptSrc = scratch;
            info.psize = IntPtr.Add(scratch, 8);
            info.pblend = IntPtr.Add(scratch, 16);
            info.dwFlags = 2;   // ULW_ALPHA
            info.prcDirty = IntPtr.Add(scratch, 20);
            return UpdateLayeredWindowIndirect(hwnd, ref info);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Info
        {
            public int cbSize;
            public IntPtr hdcDst, pptDst, psize, hdcSrc, pptSrc;
            public int crKey;
            public IntPtr pblend;
            public int dwFlags;
            public IntPtr prcDirty;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindowIndirect(IntPtr hwnd, ref Info info);
    }
}
