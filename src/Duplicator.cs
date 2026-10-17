using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LGEngine
{
    // Reads part of the desktop picture straight from the graphics card, through the Desktop
    // Duplication interface Windows provides for screen recorders.
    //
    // Compared with copying from the screen the ordinary way, this costs next to nothing and, more
    // to the point, Windows says when the picture has changed: nothing is read while the desktop
    // sits still, and every change can be had the moment it is on screen. That is what lets the
    // glass follow what is behind it in real time.
    //
    // One object serves one display. Everything here is called from a single thread (the feed's).
    // Any failure is reported, never thrown past the caller's catch: the owner then falls back to
    // the ordinary way of reading the screen.
    public sealed class Duplicator : IDisposable
    {
        const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        const int FormatBgra8 = 87;          // DXGI_FORMAT_B8G8R8A8_UNORM
        const int FormatBgrx8 = 88;          // DXGI_FORMAT_B8G8R8X8_UNORM
        const int FormatBgra8Srgb = 91;      // DXGI_FORMAT_B8G8R8A8_UNORM_SRGB
        const int FormatBgrx8Srgb = 93;      // DXGI_FORMAT_B8G8R8X8_UNORM_SRGB
        const int UsageStaging = 3;          // D3D11_USAGE_STAGING
        const uint CpuAccessRead = 0x20000;  // D3D11_CPU_ACCESS_READ
        const int MapRead = 1;               // D3D11_MAP_READ
        const uint SdkVersion = 7;           // D3D11_SDK_VERSION

        static Guid IID_Factory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        static Guid IID_Output1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        static Guid IID_Output5 = new Guid("80a07424-ab52-42eb-833c-0c42fd282d98");
        static Guid IID_Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [DllImport("dxgi.dll")]
        static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint levelCount,
            uint sdkVersion, out IntPtr device, out int level, out IntPtr context);

        [StructLayout(LayoutKind.Sequential)]
        struct FrameInfo
        {
            public long LastPresentTime, LastMouseUpdateTime;
            public uint AccumulatedFrames;
            public int RectsCoalesced, ProtectedContentMaskedOut, PointerX, PointerY, PointerVisible;
            public uint TotalMetadataBufferSize, PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TextureDesc
        {
            public uint Width, Height, MipLevels, ArraySize;
            public int Format;
            public uint SampleCount, SampleQuality;
            public int Usage;
            public uint BindFlags, CpuAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Box
        {
            public uint Left, Top, Front, Right, Bottom, Back;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Mapped
        {
            public IntPtr Data;
            public uint RowPitch, DepthPitch;
        }

        // The interface methods used, by their position in each interface (fixed by Windows).
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int EnumFn(IntPtr self, uint index, out IntPtr item);                       // IDXGIFactory1 #12, IDXGIAdapter #7
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int DescFn(IntPtr self, IntPtr desc);                                       // IDXGIOutput #7
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void DescOnlyFn(IntPtr self, IntPtr desc);                                  // IDXGIOutputDuplication #7 (gives nothing back)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int DuplicateFn(IntPtr self, IntPtr device, out IntPtr duplication);        // IDXGIOutput1 #22
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int Duplicate1Fn(IntPtr self, IntPtr device, uint flags, uint formatCount, IntPtr formats, out IntPtr duplication);   // IDXGIOutput5 #26
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int AcquireFn(IntPtr self, uint timeoutMs, out FrameInfo info, out IntPtr resource);   // IDXGIOutputDuplication #8
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int RectsFn(IntPtr self, uint bufferSize, IntPtr buffer, out uint required);           // IDXGIOutputDuplication #9
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int ReleaseFrameFn(IntPtr self);                                            // IDXGIOutputDuplication #14
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateTextureFn(IntPtr self, ref TextureDesc desc, IntPtr initial, out IntPtr texture);   // ID3D11Device #5
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int MapFn(IntPtr self, IntPtr resource, uint sub, int type, uint flags, out Mapped mapped);   // ID3D11DeviceContext #14
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void UnmapFn(IntPtr self, IntPtr resource, uint sub);                       // ID3D11DeviceContext #15
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void CopyRegionFn(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, ref Box box);   // ID3D11DeviceContext #46

        static T Method<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr table = Marshal.ReadIntPtr(obj);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, slot * IntPtr.Size), typeof(T));
        }

        static void Free(ref IntPtr obj)
        {
            if (obj != IntPtr.Zero) Marshal.Release(obj);
            obj = IntPtr.Zero;
        }

        IntPtr device, context, display, duplication, staging;   // display: the output, kept so that it can be duplicated again
        AcquireFn acquire;
        RectsFn dirtyRects;
        ReleaseFrameFn releaseFrame;
        CreateTextureFn createTexture;
        MapFn map;
        UnmapFn unmap;
        CopyRegionFn copyRegion;

        Rectangle wanted, clipped;      // screen coordinates: the area asked for, and the part of it on this display
        int stagingW, stagingH;
        IntPtr rectBuffer;
        int rectBufferSize;
        int[][] rows;
        int[] columnAt, rowAt;
        bool first = true;

        // The display's area in screen coordinates.
        public Rectangle Bounds { get; private set; }

        // When (by ScreenFeed.Fine) the picture held was last brought up to date: what Read gives
        // shows the screen as it was then (a part not touched since has not changed since).
        public long FrameFine { get; private set; }

        // For the log: time spent waiting for the graphics card to hand pictures over.
        public long FetchTicks;

        // Why Create returned null.
        public static string LastProblem { get; private set; }

        Duplicator() { }

        // For the display that holds the middle of 'area'. Returns null (see LastProblem) when this
        // way of reading the screen is not available there.
        public static Duplicator Create(Rectangle area)
        {
            Duplicator d = new Duplicator();
            try
            {
                string problem = d.Open(new Point(area.Left + area.Width / 2, area.Top + area.Height / 2));
                if (problem == null)
                {
                    LastProblem = null;
                    return d;
                }
                LastProblem = problem;
            }
            catch (Exception ex)
            {
                LastProblem = ex.Message;
            }
            d.Dispose();
            return null;
        }

        string Open(Point at)
        {
            IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero, output = IntPtr.Zero;
            IntPtr desc = Marshal.AllocHGlobal(128);
            try
            {
                int hr = CreateDXGIFactory1(ref IID_Factory1, out factory);
                if (hr < 0 || factory == IntPtr.Zero) return "no graphics factory (0x" + hr.ToString("X8") + ")";

                // Find the display, and the graphics card it is plugged into.
                EnumFn adapters = Method<EnumFn>(factory, 12);
                bool found = false;
                for (uint a = 0; a < 16 && !found; a++)
                {
                    if (adapters(factory, a, out adapter) < 0 || adapter == IntPtr.Zero) break;
                    EnumFn outputs = Method<EnumFn>(adapter, 7);
                    for (uint o = 0; o < 16 && !found; o++)
                    {
                        if (outputs(adapter, o, out output) < 0 || output == IntPtr.Zero) break;
                        // DXGI_OUTPUT_DESC: 32 characters of name, then the desktop rectangle, "attached", rotation, monitor.
                        if (Method<DescFn>(output, 7)(output, desc) >= 0)
                        {
                            Rectangle r = Rectangle.FromLTRB(Marshal.ReadInt32(desc, 64), Marshal.ReadInt32(desc, 68), Marshal.ReadInt32(desc, 72), Marshal.ReadInt32(desc, 76));
                            bool attached = Marshal.ReadInt32(desc, 80) != 0;
                            int rotation = Marshal.ReadInt32(desc, 84);
                            if (attached && r.Contains(at))
                            {
                                if (rotation > 1) return "the display is rotated";
                                Bounds = r;
                                found = true;
                                break;
                            }
                        }
                        Free(ref output);
                    }
                    if (!found) Free(ref adapter);
                }
                if (!found) return "no display found there";

                int level;
                hr = D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, SdkVersion, out device, out level, out context);
                if (hr < 0 || device == IntPtr.Zero || context == IntPtr.Zero) return "no graphics device (0x" + hr.ToString("X8") + ")";

                hr = Marshal.QueryInterface(output, ref IID_Output1, out display);
                if (hr < 0 || display == IntPtr.Zero) return "the display cannot be duplicated (0x" + hr.ToString("X8") + ")";
                createTexture = Method<CreateTextureFn>(device, 5);
                map = Method<MapFn>(context, 14);
                unmap = Method<UnmapFn>(context, 15);
                copyRegion = Method<CopyRegionFn>(context, 46);
                return Resume() ? null : resumeProblem;
            }
            finally
            {
                Marshal.FreeHGlobal(desc);
                Free(ref output);
                Free(ref adapter);
                Free(ref factory);
            }
        }

        int lastResult;
        string resumeProblem = "Windows would not duplicate the display";

        // For the log: how the display's picture is had (see Resume).
        public string Way { get; private set; }

        // Stops duplicating the display but keeps the graphics device, which is the costly part to
        // set up. Windows does a little extra work on every frame for as long as a display is being
        // duplicated, so this is done whenever no card is on screen; Resume picks it up again.
        public void Park()
        {
            Free(ref staging);
            stagingW = stagingH = 0;
            wanted = clipped = Rectangle.Empty;
            Free(ref duplication);
            acquire = null;
            dirtyRects = null;
            releaseFrame = null;
        }

        // Starts duplicating the display (again). Returns false if Windows will not: the owner then
        // starts over with a new Duplicator, as the display arrangement has probably changed.
        public bool Resume()
        {
            if (duplication != IntPtr.Zero) return true;
            if (display == IntPtr.Zero || device == IntPtr.Zero) return false;

            // The display may have been rearranged or switched to another size meanwhile: everything
            // here is measured against where it was, so that calls for starting over.
            IntPtr desc = Marshal.AllocHGlobal(128);
            try
            {
                if (Method<DescFn>(display, 7)(display, desc) < 0) return false;
                Rectangle now = Rectangle.FromLTRB(Marshal.ReadInt32(desc, 64), Marshal.ReadInt32(desc, 68), Marshal.ReadInt32(desc, 72), Marshal.ReadInt32(desc, 76));
                if (now != Bounds || Marshal.ReadInt32(desc, 80) == 0 || Marshal.ReadInt32(desc, 84) > 1) return false;
            }
            finally
            {
                Marshal.FreeHGlobal(desc);
            }
            // The picture is asked for as 8-bit BGRA (through IDXGIOutput5, Windows 10 1703 on). On a
            // display with HDR switched on the desktop's own picture is 16-bit floating point, which the
            // older call hands over as it is (and which the copy into the 8-bit read-back texture in
            // Wait could not take); asked for this way, Windows converts it to the 8-bit picture a
            // screenshot would show. Where the newer call is not there, the older one, and the
            // picture's format is checked either way: anything but 8-bit BGRA is declined, and the
            // glass reads the screen the ordinary way (which Windows converts itself).
            duplication = IntPtr.Zero;
            Way = null;
            IntPtr output5;
            if (Marshal.QueryInterface(display, ref IID_Output5, out output5) >= 0 && output5 != IntPtr.Zero)
            {
                try
                {
                    int[] formats = { FormatBgra8 };
                    GCHandle pin = GCHandle.Alloc(formats, GCHandleType.Pinned);
                    try
                    {
                        lastResult = Method<Duplicate1Fn>(output5, 26)(output5, device, 0, 1, pin.AddrOfPinnedObject(), out duplication);
                    }
                    finally
                    {
                        pin.Free();
                    }
                }
                catch (Exception)
                {
                    lastResult = -1;
                    duplication = IntPtr.Zero;
                }
                finally
                {
                    Marshal.Release(output5);
                }
                if (lastResult < 0) duplication = IntPtr.Zero;
                else Way = "8-bit (asked for)";
            }
            if (duplication == IntPtr.Zero)
            {
                lastResult = Method<DuplicateFn>(display, 22)(display, device, out duplication);
                if (lastResult < 0 || duplication == IntPtr.Zero)
                {
                    duplication = IntPtr.Zero;
                    resumeProblem = "Windows would not duplicate the display (0x" + lastResult.ToString("X8") + ")";
                    return false;
                }
                Way = "as the display has it";
            }
            // DXGI_OUTDUPL_DESC: the mode (width, height, refresh rate, format, ...), then the rotation.
            int format = -1;
            IntPtr dd = Marshal.AllocHGlobal(64);
            try
            {
                Method<DescOnlyFn>(duplication, 7)(duplication, dd);
                format = Marshal.ReadInt32(dd, 16);
            }
            catch (Exception) { }
            finally
            {
                Marshal.FreeHGlobal(dd);
            }
            if (format != FormatBgra8 && format != FormatBgrx8 && format != FormatBgra8Srgb && format != FormatBgrx8Srgb)
            {
                Free(ref duplication);
                resumeProblem = "the display's picture is not 8-bit (format " + format + "; HDR?)";
                return false;
            }
            Way += ", format " + format;
            acquire = Method<AcquireFn>(duplication, 8);
            dirtyRects = Method<RectsFn>(duplication, 9);
            releaseFrame = Method<ReleaseFrameFn>(duplication, 14);
            first = true;
            return true;
        }

        // The area of the screen to read from now on. Returns true if that is a change: what was held
        // is then gone, and the next change on screen brings a picture of the new area.
        public bool SetArea(Rectangle area)
        {
            if (area == wanted) return false;
            wanted = area;
            clipped = Rectangle.Intersect(area, Bounds);
            columnAt = rowAt = null;
            Free(ref staging);
            stagingW = stagingH = 0;
            if (clipped.Width <= 0 || clipped.Height <= 0) return true;

            TextureDesc desc = new TextureDesc();
            desc.Width = (uint)clipped.Width;
            desc.Height = (uint)clipped.Height;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.Format = FormatBgra8;
            desc.SampleCount = 1;
            desc.Usage = UsageStaging;
            desc.CpuAccessFlags = CpuAccessRead;
            int hr = createTexture(device, ref desc, IntPtr.Zero, out staging);
            if (hr < 0 || staging == IntPtr.Zero)
            {
                staging = IntPtr.Zero;
                throw new InvalidOperationException("could not create a read-back texture (0x" + hr.ToString("X8") + ")");
            }
            stagingW = clipped.Width;
            stagingH = clipped.Height;
            first = true;   // the next frame of any kind fills it, even if nothing in the new area has changed
            return true;
        }

        // Waits up to timeoutMs for the desktop to change.
        // parts: the pieces of the area set that are wanted (in screen pixels); touched[i] is set for
        // each one the change reaches, and only those are copied (a few cards far apart make a big
        // area that is mostly not wanted, and copying that every time would cost for nothing).
        // Returns 1 when there is a new picture of at least one part to Read, 0 when there is not,
        // and -1 when Windows has taken the duplication away (a display change, the lock screen):
        // the owner then makes a new Duplicator.
        public int Wait(int timeoutMs, Rectangle[] parts, bool[] touched)
        {
            for (int i = 0; i < touched.Length; i++) touched[i] = false;
            FrameInfo info;
            IntPtr resource;
            int hr = acquire(duplication, (uint)Math.Max(0, timeoutMs), out info, out resource);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) return 0;
            if (hr < 0) return -1;

            bool fresh = false;
            try
            {
                // No present time: only the mouse pointer moved, and that is not in the picture.
                bool changed = (first || info.LastPresentTime != 0) && staging != IntPtr.Zero && resource != IntPtr.Zero;
                if (changed)
                {
                    if (first) { for (int i = 0; i < touched.Length; i++) touched[i] = true; }
                    else Touches(info, parts, touched);
                    changed = false;
                    for (int i = 0; i < touched.Length; i++) if (touched[i]) changed = true;
                }
                if (changed)
                {
                    IntPtr texture;
                    if (Marshal.QueryInterface(resource, ref IID_Texture2D, out texture) >= 0 && texture != IntPtr.Zero)
                    {
                        for (int i = 0; i < parts.Length && i < touched.Length; i++)
                        {
                            if (!touched[i]) continue;
                            Rectangle part = Rectangle.Intersect(parts[i], clipped);
                            if (part.Width <= 0 || part.Height <= 0) continue;
                            Box box;
                            box.Left = (uint)(part.Left - Bounds.Left);
                            box.Top = (uint)(part.Top - Bounds.Top);
                            box.Right = box.Left + (uint)part.Width;
                            box.Bottom = box.Top + (uint)part.Height;
                            box.Front = 0;
                            box.Back = 1;
                            copyRegion(context, staging, 0, (uint)(part.Left - clipped.Left), (uint)(part.Top - clipped.Top), 0, texture, 0, ref box);
                        }
                        Marshal.Release(texture);
                        fresh = true;
                        first = false;
                        FrameFine = ScreenFeed.Fine();
                    }
                }
            }
            finally
            {
                if (resource != IntPtr.Zero) Marshal.Release(resource);
                releaseFrame(duplication);
            }
            return fresh ? 1 : 0;
        }

        // Which parts the latest change touches. Windows lists what changed as rectangles; if that
        // list cannot be had, or anything was moved rather than redrawn, assume that it touches all.
        void Touches(FrameInfo info, Rectangle[] parts, bool[] touched)
        {
            bool all = info.TotalMetadataBufferSize == 0;   // (changed, but Windows did not say where)
            uint used = 0;
            if (!all)
            {
                int size = (int)info.TotalMetadataBufferSize;
                if (rectBuffer == IntPtr.Zero || rectBufferSize < size)
                {
                    if (rectBuffer != IntPtr.Zero) Marshal.FreeHGlobal(rectBuffer);
                    rectBufferSize = Math.Max(size, 1024);
                    rectBuffer = Marshal.AllocHGlobal(rectBufferSize);
                }
                if (dirtyRects(duplication, (uint)rectBufferSize, rectBuffer, out used) < 0) all = true;
                else if (used < info.TotalMetadataBufferSize) all = true;   // the rest are moves
            }
            for (int i = 0; i < parts.Length && i < touched.Length; i++)
            {
                if (all) { touched[i] = true; continue; }
                Rectangle mine = new Rectangle(parts[i].Left - Bounds.Left, parts[i].Top - Bounds.Top, parts[i].Width, parts[i].Height);
                for (int at = 0; at + 16 <= used; at += 16)
                {
                    Rectangle r = Rectangle.FromLTRB(Marshal.ReadInt32(rectBuffer, at), Marshal.ReadInt32(rectBuffer, at + 4),
                        Marshal.ReadInt32(rectBuffer, at + 8), Marshal.ReadInt32(rectBuffer, at + 12));
                    if (r.IntersectsWith(mine))
                    {
                        touched[i] = true;
                        break;
                    }
                }
            }
        }

        // Copies part of the picture last announced by Wait into target, shrunk by 'step' (each texel
        // is the average of step x step pixels): target is targetW x targetH texels covering the
        // screen from part's top-left corner. Anything asked for that is off this display, or outside
        // the area set, repeats the nearest pixels that are held. Returns false if the picture could
        // not be read.
        public bool Read(Point part, int[] target, int targetW, int targetH, int step)
        {
            if (staging == IntPtr.Zero || stagingW <= 0 || stagingH <= 0 || step < 1) return false;
            if (target == null || target.Length < targetW * targetH) return false;

            // Where each pixel asked for is in the picture held.
            int needW = targetW * step, needH = targetH * step;
            if (columnAt == null || columnAt.Length < needW) columnAt = new int[needW];
            if (rowAt == null || rowAt.Length < needH) rowAt = new int[needH];
            for (int x = 0; x < needW; x++) columnAt[x] = Math.Max(0, Math.Min(stagingW - 1, part.X + x - clipped.Left));
            for (int y = 0; y < needH; y++) rowAt[y] = Math.Max(0, Math.Min(stagingH - 1, part.Y + y - clipped.Top));
            if (rows == null || rows.Length != step || rows[0].Length != stagingW)
            {
                rows = new int[step][];
                for (int i = 0; i < step; i++) rows[i] = new int[stagingW];
            }

            // Only the columns wanted are copied out of each row (the area held can be much wider).
            int fromColumn = columnAt[0], columns = columnAt[needW - 1] - fromColumn + 1;

            Mapped mapped;
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            if (map(context, staging, 0, MapRead, 0, out mapped) < 0 || mapped.Data == IntPtr.Zero) return false;
            FetchTicks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
            try
            {
                int area = step * step;
                for (int ty = 0; ty < targetH; ty++)
                {
                    for (int v = 0; v < step; v++)
                    {
                        IntPtr from = new IntPtr(mapped.Data.ToInt64() + (long)rowAt[ty * step + v] * mapped.RowPitch + (long)fromColumn * 4);
                        Marshal.Copy(from, rows[v], fromColumn, columns);
                    }
                    int at = ty * targetW;
                    if (step == 1)
                    {
                        int[] row = rows[0];
                        for (int tx = 0; tx < targetW; tx++) target[at + tx] = row[columnAt[tx]] & 0xFFFFFF;
                    }
                    else if (step == 2)
                    {
                        // The usual case (200% scaling), so it gets a quick path: red and blue are
                        // averaged together in one go, green in another.
                        int[] upper = rows[0], lower = rows[1];
                        for (int tx = 0; tx < targetW; tx++)
                        {
                            int a = columnAt[tx * 2], b = columnAt[tx * 2 + 1];
                            int p0 = upper[a], p1 = upper[b], p2 = lower[a], p3 = lower[b];
                            int rb = (((p0 & 0xFF00FF) + (p1 & 0xFF00FF) + (p2 & 0xFF00FF) + (p3 & 0xFF00FF)) >> 2) & 0xFF00FF;
                            int g = (((p0 & 0x00FF00) + (p1 & 0x00FF00) + (p2 & 0x00FF00) + (p3 & 0x00FF00)) >> 2) & 0x00FF00;
                            target[at + tx] = rb | g;
                        }
                    }
                    else
                    {
                        for (int tx = 0; tx < targetW; tx++)
                        {
                            int r = 0, g = 0, b = 0, column = tx * step;
                            for (int v = 0; v < step; v++)
                            {
                                int[] row = rows[v];
                                for (int u = 0; u < step; u++)
                                {
                                    int p = row[columnAt[column + u]];
                                    r += (p >> 16) & 255;
                                    g += (p >> 8) & 255;
                                    b += p & 255;
                                }
                            }
                            target[at + tx] = ((r / area) << 16) | ((g / area) << 8) | (b / area);
                        }
                    }
                }
            }
            finally
            {
                unmap(context, staging, 0);
            }
            return true;
        }

        public void Dispose()
        {
            if (rectBuffer != IntPtr.Zero) Marshal.FreeHGlobal(rectBuffer);
            rectBuffer = IntPtr.Zero;
            Free(ref staging);
            Free(ref duplication);
            Free(ref display);
            Free(ref context);
            Free(ref device);
            acquire = null;
        }
    }
}
