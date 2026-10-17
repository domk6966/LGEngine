using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LGEngine
{
    // A click-through, always-on-top window that shows one premultiplied picture (a Dib) at a
    // given place and opacity, and can hide itself from screen captures.
    public sealed class GlassLayer : Form
    {
        bool capturable;
        bool shown;

        // Puts a picture (premultiplied) on screen at the given place and opacity, and shows the
        // window if it was not showing. Throws if Windows refuses the picture.
        public void Put(Dib picture, Point at, int alpha)
        {
            Win.POINT position, zero;
            position.X = at.X;
            position.Y = at.Y;
            zero.X = 0;
            zero.Y = 0;
            Win.SIZE size;
            size.Width = picture.Width;
            size.Height = picture.Height;
            Win.BLENDFUNCTION blend = new Win.BLENDFUNCTION();
            blend.SourceConstantAlpha = (byte)Math.Max(0, Math.Min(255, alpha));
            blend.AlphaFormat = Win.AC_SRC_ALPHA;
            IntPtr screen = Win.GetDC(IntPtr.Zero);
            try
            {
                if (!Win.UpdateLayeredWindow(Handle, screen, ref position, ref size, picture.Dc, ref zero, 0, ref blend, Win.ULW_ALPHA))
                {
                    throw new InvalidOperationException("Windows refused to draw the glass (error " + Marshal.GetLastWin32Error() + ")");
                }
            }
            finally
            {
                Win.ReleaseDC(IntPtr.Zero, screen);
            }
            if (!shown && alpha > 0)
            {
                Win.ShowWindow(Handle, Win.SW_SHOWNOACTIVATE);
                shown = true;
            }
        }

        // Takes the window off screen (Put shows it again).
        public void Conceal()
        {
            if (shown) Win.ShowWindow(Handle, Win.SW_HIDE);
            shown = false;
        }

        public GlassLayer(bool capturable)
        {
            this.capturable = capturable;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Win.WS_EX_LAYERED | Win.WS_EX_NOACTIVATE | Win.WS_EX_TOOLWINDOW | Win.WS_EX_TRANSPARENT | Win.WS_EX_TOPMOST;
                return cp;
            }
        }

        // True when this window is invisible to screen captures, so the picture taken of "what is
        // behind the glass" can never contain the glass itself. Live backgrounds depend on it.
        public bool Excluded { get; private set; }

        // Whether this window shows in screenshots and recordings.
        public bool Capturable { get { return capturable; } }

        // Lets the window into screenshots and recordings, or takes it out of them again (for
        // the moment of a look behind it). What is on screen does not change either way.
        // Returns false if Windows would not have it.
        public bool SetCapturable(bool want)
        {
            if (want == capturable) return true;
            if (!IsHandleCreated)
            {
                capturable = want;
                return true;
            }
            if (!Win.SetWindowDisplayAffinity(Handle, want ? 0u : Win.WDA_EXCLUDEFROMCAPTURE)) return false;
            capturable = want;
            Excluded = !want;
            return true;
        }

        // Nothing outside this program may close a glass window (a "close" sent to every window
        // of the program by a task manager, say): it is taken away only by its owner. The owner
        // disposes of it, which does not come through here.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason != CloseReason.ApplicationExitCall && e.CloseReason != CloseReason.WindowsShutDown) e.Cancel = true;
            base.OnFormClosing(e);
        }

        // True while the window is on screen as far as Windows is concerned.
        public bool OnScreen { get { return shown && IsHandleCreated && Win.IsWindowVisible(Handle); } }

        public bool Showing { get { return shown; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Excluded = !capturable && Win.SetWindowDisplayAffinity(Handle, Win.WDA_EXCLUDEFROMCAPTURE);
        }

        // The few Windows calls this window needs.
        static class Win
        {
            public const int WS_EX_LAYERED = 0x00080000;
            public const int WS_EX_NOACTIVATE = 0x08000000;
            public const int WS_EX_TOOLWINDOW = 0x00000080;
            public const int WS_EX_TRANSPARENT = 0x00000020;
            public const int WS_EX_TOPMOST = 0x00000008;
            public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
            public const int ULW_ALPHA = 2;
            public const byte AC_SRC_ALPHA = 1;
            public const int SW_HIDE = 0;
            public const int SW_SHOWNOACTIVATE = 4;

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int X, Y;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct SIZE
            {
                public int Width, Height;
            }

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            public struct BLENDFUNCTION
            {
                public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
            }

            [DllImport("user32.dll")]
            public static extern IntPtr GetDC(IntPtr hwnd);

            [DllImport("user32.dll")]
            public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT position, ref SIZE size, IntPtr srcDc, ref POINT srcPosition, int colorKey, ref BLENDFUNCTION blend, int flags);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ShowWindow(IntPtr hwnd, int command);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool IsWindowVisible(IntPtr hwnd);
        }
    }
}
