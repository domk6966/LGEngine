using System;
using System.Runtime.InteropServices;

namespace LGGpu
{
    // Raw COM vtable interop for D3D11, DXGI, DirectComposition and the shader compiler: no
    // SharpDX, no NuGet, C# 5 on .NET Framework 4 (the same way src/Duplicator.cs does it).
    internal static class Com
    {
        public static T Method<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr table = Marshal.ReadIntPtr(obj);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, slot * IntPtr.Size), typeof(T));
        }

        public static void Release(ref IntPtr obj)
        {
            if (obj != IntPtr.Zero) Marshal.Release(obj);
            obj = IntPtr.Zero;
        }

        public static IntPtr Query(IntPtr obj, Guid iid)
        {
            IntPtr p;
            if (obj == IntPtr.Zero || Marshal.QueryInterface(obj, ref iid, out p) < 0) return IntPtr.Zero;
            return p;
        }

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new InvalidOperationException(what + " failed (0x" + hr.ToString("X8") + ")");
        }

        public static string Hex(int hr) { return "0x" + hr.ToString("X8"); }
    }

    internal static class IID
    {
        public static Guid Factory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        public static Guid Factory2 = new Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        public static Guid DxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        public static Guid DxgiDevice1 = new Guid("77db970f-6276-48ba-ba28-070143b4392c");
        public static Guid Output1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        public static Guid Output5 = new Guid("80a07424-ab52-42eb-833c-0c42fd282d98");
        public static Guid Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        public static Guid SwapChain2 = new Guid("a8be2ac4-199f-4946-b331-79599fb98de7");
        public static Guid DCompDevice = new Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
        public static Guid DxgiResource = new Guid("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
        public static Guid KeyedMutex = new Guid("9d8e1289-d7b3-465f-8126-250e349af85d");
    }

    // ---- structs (layouts fixed by the Windows headers) ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct TextureDesc
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BufferDesc
    {
        public uint ByteWidth;
        public int Usage;
        public uint BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SubresourceData
    {
        public IntPtr Data;
        public uint RowPitch, SlicePitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Box
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Mapped
    {
        public IntPtr Data;
        public uint RowPitch, DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SamplerDesc
    {
        public int Filter, AddressU, AddressV, AddressW;
        public float MipLODBias;
        public uint MaxAnisotropy;
        public int ComparisonFunc;
        public float Border0, Border1, Border2, Border3;
        public float MinLOD, MaxLOD;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RasterizerDesc
    {
        public int FillMode, CullMode, FrontCounterClockwise, DepthBias;
        public float DepthBiasClamp, SlopeScaledDepthBias;
        public int DepthClipEnable, ScissorEnable, MultisampleEnable, AntialiasedLineEnable;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Viewport
    {
        public float X, Y, Width, Height, MinDepth, MaxDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryDesc
    {
        public int Query;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SwapChainDesc1
    {
        public uint Width, Height;
        public int Format, Stereo;
        public uint SampleCount, SampleQuality, BufferUsage, BufferCount;
        public int Scaling, SwapEffect, AlphaMode;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FrameInfo
    {
        public long LastPresentTime, LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced, ProtectedContentMaskedOut, PointerX, PointerY, PointerVisible;
        public uint TotalMetadataBufferSize, PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public bool Intersects(RECT o) { return o.Left < Right && o.Right > Left && o.Top < Bottom && o.Bottom > Top; }
        public static RECT Intersect(RECT a, RECT b)
        {
            RECT r = new RECT(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));
            if (r.Right <= r.Left || r.Bottom <= r.Top) return new RECT(0, 0, 0, 0);
            return r;
        }
        public bool IsEmpty { get { return Right <= Left || Bottom <= Top; } }
        public override string ToString() { return Left + "," + Top + " " + Width + "x" + Height; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    // ---- delegates, by interface ----

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int EnumFn(IntPtr self, uint index, out IntPtr item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int DescFn(IntPtr self, IntPtr desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void DescVoidFn(IntPtr self, IntPtr desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetParentFn(IntPtr self, ref Guid iid, out IntPtr parent);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int OutPtrFn(IntPtr self, out IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int NoArgFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate IntPtr HandleFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int UintFn(IntPtr self, uint v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int FloatFn(IntPtr self, float v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int PtrFn(IntPtr self, IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Duplicate1Fn(IntPtr self, IntPtr device, uint flags, uint formatCount, IntPtr formats, out IntPtr duplication);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int DuplicateFn(IntPtr self, IntPtr device, out IntPtr duplication);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int AcquireFn(IntPtr self, uint timeoutMs, out FrameInfo info, out IntPtr resource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int RectsFn(IntPtr self, uint bufferSize, IntPtr buffer, out uint required);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int CreateSwapChainForCompositionFn(IntPtr self, IntPtr device, ref SwapChainDesc1 desc, IntPtr restrict, out IntPtr swapChain);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetBufferFn(IntPtr self, uint index, ref Guid iid, out IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int PresentFn(IntPtr self, uint sync, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int CreateTexture2DFn(IntPtr self, ref TextureDesc desc, IntPtr initial, out IntPtr texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int CreateBufferFn(IntPtr self, ref BufferDesc desc, IntPtr initial, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int CreateViewFn(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int CreateShaderFn(IntPtr self, IntPtr bytecode, IntPtr length, IntPtr linkage, out IntPtr shader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int CreateFromDescFn(IntPtr self, IntPtr desc, out IntPtr obj);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void SetShaderFn(IntPtr self, IntPtr shader, IntPtr instances, uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void SetSlotsFn(IntPtr self, uint start, uint count, IntPtr array);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void DrawFn(IntPtr self, uint count, uint start);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int MapFn(IntPtr self, IntPtr resource, uint sub, int type, uint flags, out Mapped mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void UnmapFn(IntPtr self, IntPtr resource, uint sub);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void SetIntFn(IntPtr self, int v);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void SetPtrFn(IntPtr self, IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void OMSetRenderTargetsFn(IntPtr self, uint count, IntPtr views, IntPtr depth);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void OMSetBlendStateFn(IntPtr self, IntPtr state, IntPtr factor, uint mask);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void RSSetViewportsFn(IntPtr self, uint count, IntPtr viewports);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void CopyRegionFn(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, ref Box box);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void CopyResourceFn(IntPtr self, IntPtr dst, IntPtr src);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void UpdateSubresourceFn(IntPtr self, IntPtr resource, uint sub, IntPtr box, IntPtr data, uint rowPitch, uint depthPitch);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void ClearRtvFn(IntPtr self, IntPtr rtv, IntPtr colour);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void VoidFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetDataFn(IntPtr self, IntPtr query, IntPtr data, uint size, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int CreateTargetForHwndFn(IntPtr self, IntPtr hwnd, int topmost, out IntPtr target);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate IntPtr GetBufferPointerFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int AcquireSyncFn(IntPtr self, ulong key, uint ms);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int ReleaseSyncFn(IntPtr self, ulong key);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int OpenSharedResourceFn(IntPtr self, IntPtr handle, ref Guid iid, out IntPtr resource);

    internal static class Win
    {
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000;
        public const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
        public const int SW_SHOWNOACTIVATE = 4, SW_HIDE = 0;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        public const uint SWP_NOSIZE = 1, SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 4, SWP_SHOWWINDOW = 0x40, SWP_NOOWNERZORDER = 0x200;
        public const uint WM_NCHITTEST = 0x84, WM_DESTROY = 2, WM_PAINT = 0xF, WM_TIMER = 0x113, WM_QUIT = 0x12, WM_ERASEBKGND = 0x14;
        public const int HTTRANSPARENT = -1;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEX cls);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowExW(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] public static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG msg);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint ms, uint wakeMask);
        [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
        [DllImport("user32.dll")] public static extern IntPtr BeginPaint(IntPtr hwnd, IntPtr ps);
        [DllImport("user32.dll")] public static extern bool EndPaint(IntPtr hwnd, IntPtr ps);
        [DllImport("user32.dll")] public static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);
        [DllImport("user32.dll")] public static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(int colour);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern int GetPixel(IntPtr dc, int x, int y);
        [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] public static extern bool SetThreadPriority(IntPtr thread, int priority);
        [DllImport("kernel32.dll")] public static extern bool SetThreadInformation(IntPtr thread, int cls, ref ThreadPowerThrottling info, uint size);
        [DllImport("kernel32.dll")] public static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint ms);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("dwmapi.dll")] public static extern int DwmFlush();

        [StructLayout(LayoutKind.Sequential)]
        public struct ThreadPowerThrottling { public uint Version, ControlMask, StateMask; }
        public const int ThreadPowerThrottlingInfo = 4;   // THREAD_INFORMATION_CLASS ThreadPowerThrottling

        // EcoQoS for the calling thread on (idle) or off (animating): the scheduler then prefers
        // efficient cores and lower clocks for it.
        public static void SetEfficient(bool on)
        {
            ThreadPowerThrottling t = new ThreadPowerThrottling();
            t.Version = 1;
            t.ControlMask = 1;   // THREAD_POWER_THROTTLING_EXECUTION_SPEED
            t.StateMask = on ? 1u : 0u;
            try { SetThreadInformation(GetCurrentThread(), ThreadPowerThrottlingInfo, ref t, 12); }
            catch (Exception) { }
        }
    }

    internal static class Dll
    {
        [DllImport("dxgi.dll")] public static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
        [DllImport("d3d11.dll")]
        public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint levelCount, uint sdkVersion, out IntPtr device, out int level, out IntPtr context);
        [DllImport("dcomp.dll")] public static extern int DCompositionCreateDevice(IntPtr dxgiDevice, ref Guid iid, out IntPtr device);
        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
        public static extern int D3DCompile(IntPtr src, IntPtr srcSize, string name, IntPtr defines, IntPtr include, string entry, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);
    }
}
