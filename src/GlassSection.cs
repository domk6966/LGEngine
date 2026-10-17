using System;
using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace LGEngine
{
    // The state block as the host reads it (raw: the host validates every field before use).
    internal struct SectionState
    {
        public int Valid, Version, Flags, X, Y, MotionStyle, MotionIn, MotionOut;
        public float Opacity, ZoomX, ZoomY, LeanX, LeanY, Swell;
        public Rectangle Hover;
        public long Stamp;
    }

    // One surface's shared section (SERVICE.md section 3.2): created by the host, mapped by both sides. Mapped memory is touched
    // only through Marshal.Read* / Write* / Copy (no /unsafe), always at offsets computed from the width and height the HOST chose
    // (the host never reads a size back from the section; every rectangle it does read is clipped to the card first).
    //
    // Layout (bytes):   0 header: magic, version, total size, width, height, generation
    //                  64 state block (a seqlock): counter, valid, version, flags, place, opacity, deform, motion, hover region, callStamp
    //                 192 contents control: front (0 or 1), then per buffer {seq, dirty x y w h}
    //                 320 buffer 0, then buffer 1 (each width * height premultiplied BGRA ints)
    //
    // State seqlock: the single writer (the client, under its channel lock) makes the counter odd, writes the fields, makes it even
    // again. The reader reads the counter (odd = retry), the fields, the counter again (changed = retry), up to 8 times, then
    // keeps its last good copy. Retries are counted (GlassPipeStats.SeqlockRetries).
    //
    // Contents: two buffers; the client writes the BACK one and then flips `front`. A buffer's seq is 2p-1 while publish number p is
    // being written into it and 2p when it is complete. Ordering (x86 and x64 keep stores in order; the barriers say it for the
    // compiler and any other CPU): odd seq, barrier, pixels and dirty rect, barrier, even seq, barrier, front. The reader reads
    // front, then that buffer's seq (odd = retry), copies, reads the seq again (changed = the writer lapped it: retry, re-reading
    // front). A reader that missed a publish (p is not last+1) copies the whole picture instead of the dirty rectangle.
    // The client's "lacks" rectangles (what each buffer is missing of the newest picture: dirty forwarding) are kept in the
    // client's memory, not in the section.
    internal sealed class GlassSection : IDisposable
    {
        const int Magic = 0x31534C47;   // "GLS1"
        const int OffMagic = 0, OffVersion = 4, OffTotal = 8, OffW = 12, OffH = 16, OffGen = 20;
        public const int StateOff = 64, CtlOff = 192, PixelOff = 320;
        // state block fields (relative to StateOff)
        const int SCounter = 0, SValid = 4, SVersion = 8, SFlags = 12, SX = 16, SY = 20, SOpacity = 24, SZoomX = 28, SZoomY = 32, SLeanX = 36, SLeanY = 40,
            SSwell = 44, SMStyle = 48, SMIn = 52, SMOut = 56, SHover = 60, SStamp = 80;
        // contents control fields (relative to CtlOff)
        const int CFront = 0, CDesc = 8, CDescStride = 24;

        public const int FlagShown = 1, FlagAnimate = 2, FlagHover = 4, FlagBend = 8;

        [StructLayout(LayoutKind.Explicit)]
        struct FloatInt
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public int I;
        }

        public readonly string Name;
        public readonly int W, H, BufBytes, Total;
        readonly MemoryMappedFile mmf;
        readonly MemoryMappedViewAccessor view;
        readonly IntPtr b;
        bool disposed;
        // client publisher state
        int publishCount, front;
        readonly Rectangle[] lacks = new Rectangle[2];

        public static int SizeFor(int w, int h)
        {
            return checked(PixelOff + 2 * w * h * 4);
        }

        GlassSection(string name, int w, int h, MemoryMappedFile file, MemoryMappedViewAccessor v, bool isHost)
        {
            Name = name;
            W = w;
            H = h;
            BufBytes = w * h * 4;
            Total = SizeFor(w, h);
            mmf = file;
            view = v;
            b = v.SafeMemoryMappedViewHandle.DangerousGetHandle();
            Interlocked.Increment(ref GlassPipeStats.sections);
        }

        // The host makes the section (DACL: this user and SYSTEM).
        public static GlassSection Create(string name, int w, int h, int generation)
        {
            int total = SizeFor(w, h);
            MemoryMappedFile f = MemoryMappedFile.CreateNew(name, total, MemoryMappedFileAccess.ReadWrite, MemoryMappedFileOptions.None, GlassSecurity.Section(), HandleInheritability.None);
            MemoryMappedViewAccessor v = null;
            try
            {
                v = f.CreateViewAccessor(0, total, MemoryMappedFileAccess.ReadWrite);
                GlassSection s = new GlassSection(name, w, h, f, v, true);
                Marshal.WriteInt32(s.b, OffMagic, Magic);
                Marshal.WriteInt32(s.b, OffVersion, 1);
                Marshal.WriteInt32(s.b, OffTotal, total);
                Marshal.WriteInt32(s.b, OffW, w);
                Marshal.WriteInt32(s.b, OffH, h);
                Marshal.WriteInt32(s.b, OffGen, generation);
                return s;
            }
            catch (Exception)
            {
                if (v != null) v.Dispose();
                f.Dispose();
                throw;
            }
        }

        // The client maps the host's section; what the host announced (w, h, bytes) must agree with the section itself.
        public static GlassSection Open(string name, int w, int h, int bytes)
        {
            if (w < 1 || h < 1 || w > 4096 || h > 4096 || bytes != SizeFor(w, h)) throw new InvalidDataException("section: size does not match " + w + "x" + h);
            MemoryMappedFile f = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite, HandleInheritability.None);
            MemoryMappedViewAccessor v = null;
            try
            {
                v = f.CreateViewAccessor(0, bytes, MemoryMappedFileAccess.ReadWrite);
                GlassSection s = new GlassSection(name, w, h, f, v, false);
                if (Marshal.ReadInt32(s.b, OffMagic) != Magic || Marshal.ReadInt32(s.b, OffTotal) != bytes || Marshal.ReadInt32(s.b, OffW) != w || Marshal.ReadInt32(s.b, OffH) != h)
                {
                    s.Dispose();
                    throw new InvalidDataException("section: bad header");
                }
                return s;
            }
            catch (Exception)
            {
                if (v != null && !v.SafeMemoryMappedViewHandle.IsClosed) v.Dispose();
                f.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { view.Dispose(); }
            catch (Exception) { }
            try { mmf.Dispose(); }
            catch (Exception) { }
            Interlocked.Decrement(ref GlassPipeStats.sections);
        }

        static void Retry() { Interlocked.Increment(ref GlassPipeStats.retries); Thread.SpinWait(40); }

        void PutF(int off, float f)
        {
            FloatInt u = new FloatInt();
            u.F = f;
            Marshal.WriteInt32(b, off, u.I);
        }

        float GetF(int off)
        {
            FloatInt u = new FloatInt();
            u.I = Marshal.ReadInt32(b, off);
            return u.F;
        }

        // ---- the state block ----

        // Client side, one writer at a time.
        public void WriteState(SurfaceProps p)
        {
            int s = StateOff;
            int c = Marshal.ReadInt32(b, s + SCounter);
            Marshal.WriteInt32(b, s + SCounter, c + 1);   // odd: being written
            Thread.MemoryBarrier();
            Marshal.WriteInt32(b, s + SValid, 1);
            Marshal.WriteInt32(b, s + SVersion, p.Version);
            Marshal.WriteInt32(b, s + SFlags, (p.WantShown ? FlagShown : 0) | (p.PlaceAnimate ? FlagAnimate : 0) | (p.Hover ? FlagHover : 0) | (p.UseBend ? FlagBend : 0));
            Marshal.WriteInt32(b, s + SX, p.Place.X);
            Marshal.WriteInt32(b, s + SY, p.Place.Y);
            PutF(s + SOpacity, p.Opacity);
            PutF(s + SZoomX, p.Bend.ZoomX);
            PutF(s + SZoomY, p.Bend.ZoomY);
            PutF(s + SLeanX, p.Bend.LeanX);
            PutF(s + SLeanY, p.Bend.LeanY);
            PutF(s + SSwell, p.Bend.Swell);
            Marshal.WriteInt32(b, s + SMStyle, p.MotionStyle);
            Marshal.WriteInt32(b, s + SMIn, p.MotionIn);
            Marshal.WriteInt32(b, s + SMOut, p.MotionOut);
            Marshal.WriteInt32(b, s + SHover, p.HoverRegion.X);
            Marshal.WriteInt32(b, s + SHover + 4, p.HoverRegion.Y);
            Marshal.WriteInt32(b, s + SHover + 8, p.HoverRegion.Width);
            Marshal.WriteInt32(b, s + SHover + 12, p.HoverRegion.Height);
            Marshal.WriteInt64(b, s + SStamp, p.CallStamp);
            Thread.MemoryBarrier();
            Marshal.WriteInt32(b, s + SCounter, c + 2);   // even: done
        }

        // Host side: false = still unstable after 8 tries (keep the last good copy).
        public bool ReadState(out SectionState r)
        {
            r = new SectionState();
            int s = StateOff;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int c1 = Marshal.ReadInt32(b, s + SCounter);
                if ((c1 & 1) != 0) { Retry(); continue; }
                Thread.MemoryBarrier();
                r.Valid = Marshal.ReadInt32(b, s + SValid);
                r.Version = Marshal.ReadInt32(b, s + SVersion);
                r.Flags = Marshal.ReadInt32(b, s + SFlags);
                r.X = Marshal.ReadInt32(b, s + SX);
                r.Y = Marshal.ReadInt32(b, s + SY);
                r.Opacity = GetF(s + SOpacity);
                r.ZoomX = GetF(s + SZoomX);
                r.ZoomY = GetF(s + SZoomY);
                r.LeanX = GetF(s + SLeanX);
                r.LeanY = GetF(s + SLeanY);
                r.Swell = GetF(s + SSwell);
                r.MotionStyle = Marshal.ReadInt32(b, s + SMStyle);
                r.MotionIn = Marshal.ReadInt32(b, s + SMIn);
                r.MotionOut = Marshal.ReadInt32(b, s + SMOut);
                r.Hover = new Rectangle(Marshal.ReadInt32(b, s + SHover), Marshal.ReadInt32(b, s + SHover + 4), Marshal.ReadInt32(b, s + SHover + 8), Marshal.ReadInt32(b, s + SHover + 12));
                r.Stamp = Marshal.ReadInt64(b, s + SStamp);
                Thread.MemoryBarrier();
                if (Marshal.ReadInt32(b, s + SCounter) == c1) return true;
                Retry();
            }
            return false;
        }

        // ---- the contents ----

        static Rectangle Grow(Rectangle a, Rectangle c)
        {
            if (a.Width <= 0 || a.Height <= 0) return c;
            return Rectangle.Union(a, c);
        }

        IntPtr At(long off) { return new IntPtr(b.ToInt64() + off); }

        // Client side: src is W x H, dirty is what changed since the last publish. Writes the back buffer (what it lacks of the newest
        // picture: dirty forwarding), then flips front.
        public void PublishContents(int[] src, Rectangle dirty)
        {
            Rectangle all = new Rectangle(0, 0, W, H);
            dirty.Intersect(all);
            if (dirty.Width <= 0 || dirty.Height <= 0) return;
            lacks[0] = Grow(lacks[0], dirty);
            lacks[1] = Grow(lacks[1], dirty);
            int back = 1 - front;
            Rectangle copy = lacks[back];
            copy.Intersect(all);
            int p = publishCount + 1;
            int d = CtlOff + CDesc + back * CDescStride;
            Marshal.WriteInt32(b, d, 2 * p - 1);
            Thread.MemoryBarrier();
            long bufOff = PixelOff + (long)back * BufBytes;
            for (int y = copy.Top; y < copy.Bottom; y++)
                Marshal.Copy(src, y * W + copy.Left, At(bufOff + ((long)y * W + copy.Left) * 4), copy.Width);
            Marshal.WriteInt32(b, d + 4, dirty.X);
            Marshal.WriteInt32(b, d + 8, dirty.Y);
            Marshal.WriteInt32(b, d + 12, dirty.Width);
            Marshal.WriteInt32(b, d + 16, dirty.Height);
            Thread.MemoryBarrier();
            Marshal.WriteInt32(b, d, 2 * p);
            Thread.MemoryBarrier();
            Marshal.WriteInt32(b, CtlOff + CFront, back);
            lacks[back] = Rectangle.Empty;
            front = back;
            publishCount = p;
        }

        // Host side. 1 = mirror (W x H ints) now holds a newer picture, `dirty` is the part that changed; 0 = nothing new;
        // -1 = unstable or invalid (the mirror is left as it was).
        public int ReadContents(int[] mirror, ref int lastP, out Rectangle dirty)
        {
            dirty = Rectangle.Empty;
            if (mirror == null || mirror.Length < W * H) return -1;
            Rectangle all = new Rectangle(0, 0, W, H);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int f = Marshal.ReadInt32(b, CtlOff + CFront);
                Thread.MemoryBarrier();
                if (f != 0 && f != 1) { Retry(); continue; }
                int d = CtlOff + CDesc + f * CDescStride;
                int a = Marshal.ReadInt32(b, d);
                if (a <= 0) return 0;                            // nothing published yet (or nonsense: ignored)
                if ((a & 1) != 0) { Retry(); continue; }         // being written
                int p = a >> 1;
                if (p == lastP) return 0;
                Rectangle r = new Rectangle(Marshal.ReadInt32(b, d + 4), Marshal.ReadInt32(b, d + 8), Marshal.ReadInt32(b, d + 12), Marshal.ReadInt32(b, d + 16));
                bool wholly = p != lastP + 1;
                if (!wholly)
                {
                    // (long arithmetic: a hostile width must not overflow into the card)
                    long right = (long)r.X + r.Width, bottom = (long)r.Y + r.Height;
                    if (r.X < 0 || r.Y < 0 || r.Width <= 0 || r.Height <= 0 || right > W || bottom > H) wholly = true;
                }
                if (wholly) r = all;
                long bufOff = PixelOff + (long)f * BufBytes;
                for (int y = r.Top; y < r.Bottom; y++)
                    Marshal.Copy(At(bufOff + ((long)y * W + r.Left) * 4), mirror, y * W + r.Left, r.Width);
                Thread.MemoryBarrier();
                if (Marshal.ReadInt32(b, d) != a) { Retry(); continue; }   // the writer lapped this buffer while we copied
                lastP = p;
                dirty = r;
                return 1;
            }
            return -1;
        }
    }
}
