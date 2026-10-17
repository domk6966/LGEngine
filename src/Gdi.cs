using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LGEngine
{
    // The little of Windows the CPU backend needs: a bitmap it can draw into and read as int[], the
    // screen read the ordinary way, and a layered window handed its picture.
    public static class Gdi
    {
        public const int SRCCOPY = 0x00CC0020;
        public const int COLORONCOLOR = 3;
        public const int ULW_ALPHA = 2;
        public const byte AC_SRC_ALPHA = 1;
        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int Width, Height; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, int rop);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool StretchBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, int srcWidth, int srcHeight, int rop);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT position, ref SIZE size, IntPtr srcDc, ref POINT srcPosition, int colorKey, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("dwmapi.dll")] public static extern int DwmFlush();

        // The bounds of the display the point is on (or nearest to).
        public static Rectangle DisplayAt(int x, int y)
        {
            POINT p;
            p.X = x;
            p.Y = y;
            IntPtr m = MonitorFromPoint(p, 2);   // MONITOR_DEFAULTTONEAREST
            MONITORINFO info = new MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (m == IntPtr.Zero || !GetMonitorInfo(m, ref info)) return new Rectangle(x, y, 1, 1);
            return Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
        }
    }

    // Where the engine says what it is doing (the app points it at its log).
    public static class GlassLog
    {
        public static Action<string> Sink;

        public static void Write(string format, params object[] args)
        {
            Action<string> sink = Sink;
            if (sink == null) return;
            try { sink(args == null || args.Length == 0 ? format : string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args)); }
            catch (Exception) { }
        }
    }
}
