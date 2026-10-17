using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LGGpu
{
    // A 2D texture with the views the passes need.
    internal sealed class Tex : IDisposable
    {
        public IntPtr Ptr, Srv, Rtv;
        public int W, H, Format;
        public void Dispose()
        {
            Com.Release(ref Srv);
            Com.Release(ref Rtv);
            Com.Release(ref Ptr);
        }
    }

    // The D3D11 device on the adapter that drives one display, with everything the glass passes
    // need. One per process (one display at a time in this prototype). All calls on one thread
    // (the engine's render thread), except where noted.
    internal sealed class Gfx : IDisposable
    {
        public const int FmtBgra8 = 87, FmtR8 = 61, FmtRgba32F = 2, FmtRgba16F = 10;
        const int UsageDefault = 0, UsageImmutable = 1, UsageDynamic = 2, UsageStaging = 3;
        public const uint BindShaderResource = 8, BindRenderTarget = 0x20, BindConstant = 4;
        const uint CpuWrite = 0x10000, CpuRead = 0x20000;
        const int MapRead = 1, MapWriteDiscard = 4;

        public IntPtr Device, Context, Adapter, Output, Factory2, DComp;
        public RECT DisplayBounds;
        public string AdapterName = "?";
        public int FeatureLevel;

        // shaders
        public IntPtr VS, PSSample, PSTent, PSBlur, PSAlphaDown, PSBox, PSStats, PSGlass;
        IntPtr sampLinear, sampPoint, sampBorder, blendOver, blendOff, raster, cbuffer;
        IntPtr samplers3;   // native array of the three sampler pointers
        IntPtr[] srvScratch = new IntPtr[8];
        IntPtr srvNative, rtvNative, vpNative, cbNative, clearNative, boxNative, descNative;

        // context methods
        SetSlotsFn psSetSrvs, psSetSamplers, psSetConstants;
        SetShaderFn psSetShader, vsSetShader;
        DrawFn draw;
        MapFn map;
        UnmapFn unmap;
        SetIntFn iaSetTopology;
        SetPtrFn iaSetLayout, rsSetState, genMips;
        OMSetRenderTargetsFn omSetTargets;
        OMSetBlendStateFn omSetBlend;
        RSSetViewportsFn rsSetViewports;
        public CopyRegionFn CopyRegion;
        public CopyResourceFn CopyResource;
        UpdateSubresourceFn updateSub;
        ClearRtvFn clearRtv;
        VoidFn flush;
        SetPtrFn queryBegin, queryEnd;
        GetDataFn getData;
        CreateTexture2DFn createTexture;
        CreateViewFn createSrv, createRtv;
        CreateBufferFn createBuffer;

        public static string LastProblem;
        public static bool DebugLayer;   // D3D11_CREATE_DEVICE_DEBUG (needs the SDK layers): see DebugMessages

        // What the debug layer has to say about a device (and clears it).
        public static string DebugMessages(IntPtr device)
        {
            if (device == IntPtr.Zero) return "";
            Guid iidInfoQueue = new Guid("6543dbb6-1b48-42f5-ab82-e97ec74326f6");
            IntPtr q = Com.Query(device, iidInfoQueue);
            if (q == IntPtr.Zero) return "(no debug layer)";
            StringBuilder sb = new StringBuilder();
            try
            {
                NumFn count = Com.Method<NumFn>(q, 8);   // GetNumStoredMessages
                GetMessageFn get = Com.Method<GetMessageFn>(q, 5);
                ulong n = count(q);
                for (ulong i = 0; i < n && i < 40; i++)
                {
                    IntPtr len;
                    if (get(q, i, IntPtr.Zero, out len) < 0 || len == IntPtr.Zero) continue;
                    IntPtr buf = Marshal.AllocHGlobal(len);
                    try
                    {
                        if (get(q, i, buf, out len) < 0) continue;
                        // D3D11_MESSAGE: Category, Severity, ID (ints), then const char* pDescription, SIZE_T length
                        int severity = Marshal.ReadInt32(buf, 4), id = Marshal.ReadInt32(buf, 8);
                        IntPtr text = Marshal.ReadIntPtr(buf, 16);
                        sb.AppendLine("  [sev " + severity + " id " + id + "] " + Marshal.PtrToStringAnsi(text));
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                if (n > 40) sb.AppendLine("  (+" + (n - 40) + " more)");
                Com.Method<VoidFn>(q, 4)(q);   // ClearStoredMessages
            }
            finally { Com.Release(ref q); }
            return sb.ToString();
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate ulong NumFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetMessageFn(IntPtr self, ulong index, IntPtr message, out IntPtr length);

        // Creates the device on the adapter of the display that holds 'at' (screen coordinates).
        public static Gfx Create(int atX, int atY)
        {
            Gfx g = new Gfx();
            try
            {
                string problem = g.Open(atX, atY);
                if (problem == null) return g;
                LastProblem = problem;
            }
            catch (Exception ex)
            {
                LastProblem = ex.Message;
            }
            g.Dispose();
            return null;
        }

        string Open(int atX, int atY)
        {
            IntPtr factory;
            Com.Check(Dll.CreateDXGIFactory1(ref IID.Factory1, out factory), "CreateDXGIFactory1");
            IntPtr desc = Marshal.AllocHGlobal(512);
            try
            {
                EnumFn adapters = Com.Method<EnumFn>(factory, 12);
                bool found = false;
                for (uint a = 0; a < 16 && !found; a++)
                {
                    IntPtr adapter;
                    if (adapters(factory, a, out adapter) < 0 || adapter == IntPtr.Zero) break;
                    EnumFn outputs = Com.Method<EnumFn>(adapter, 7);
                    for (uint o = 0; o < 16 && !found; o++)
                    {
                        IntPtr output;
                        if (outputs(adapter, o, out output) < 0 || output == IntPtr.Zero) break;
                        if (Com.Method<DescFn>(output, 7)(output, desc) >= 0)
                        {
                            RECT r = new RECT(Marshal.ReadInt32(desc, 64), Marshal.ReadInt32(desc, 68), Marshal.ReadInt32(desc, 72), Marshal.ReadInt32(desc, 76));
                            bool attached = Marshal.ReadInt32(desc, 80) != 0;
                            int rotation = Marshal.ReadInt32(desc, 84);
                            if (attached && atX >= r.Left && atX < r.Right && atY >= r.Top && atY < r.Bottom)
                            {
                                if (rotation > 1) return "the display is rotated";
                                DisplayBounds = r;
                                Output = output;
                                Adapter = adapter;
                                found = true;
                                // DXGI_ADAPTER_DESC1: WCHAR Description[128] first
                                if (Com.Method<DescFn>(adapter, 10)(adapter, desc) >= 0) AdapterName = Marshal.PtrToStringUni(desc, 128).TrimEnd('\0');
                                break;
                            }
                        }
                        Com.Release(ref output);
                    }
                    if (!found) Com.Release(ref adapter);
                }
                if (!found) return "no display found at " + atX + "," + atY;
            }
            finally
            {
                Marshal.FreeHGlobal(desc);
                Com.Release(ref factory);
            }

            int level;
            int hr = Dll.D3D11CreateDevice(Adapter, 0, IntPtr.Zero, 0x20 /* BGRA support */ | (DebugLayer ? 0x2u : 0u), IntPtr.Zero, 0, 7, out Device, out level, out Context);
            if (hr < 0 && DebugLayer) hr = Dll.D3D11CreateDevice(Adapter, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out Device, out level, out Context);
            if (hr < 0) return "D3D11CreateDevice failed " + Com.Hex(hr);
            FeatureLevel = level;
            Com.Check(Com.Method<GetParentFn>(Adapter, 6)(Adapter, ref IID.Factory2, out Factory2), "IDXGIAdapter::GetParent(IDXGIFactory2)");

            createTexture = Com.Method<CreateTexture2DFn>(Device, 5);
            createSrv = Com.Method<CreateViewFn>(Device, 7);
            createRtv = Com.Method<CreateViewFn>(Device, 9);
            createBuffer = Com.Method<CreateBufferFn>(Device, 3);
            psSetSrvs = Com.Method<SetSlotsFn>(Context, 8);
            psSetShader = Com.Method<SetShaderFn>(Context, 9);
            psSetSamplers = Com.Method<SetSlotsFn>(Context, 10);
            vsSetShader = Com.Method<SetShaderFn>(Context, 11);
            draw = Com.Method<DrawFn>(Context, 13);
            map = Com.Method<MapFn>(Context, 14);
            unmap = Com.Method<UnmapFn>(Context, 15);
            psSetConstants = Com.Method<SetSlotsFn>(Context, 16);
            iaSetLayout = Com.Method<SetPtrFn>(Context, 17);
            iaSetTopology = Com.Method<SetIntFn>(Context, 24);
            queryBegin = Com.Method<SetPtrFn>(Context, 27);
            queryEnd = Com.Method<SetPtrFn>(Context, 28);
            getData = Com.Method<GetDataFn>(Context, 29);
            omSetTargets = Com.Method<OMSetRenderTargetsFn>(Context, 33);
            omSetBlend = Com.Method<OMSetBlendStateFn>(Context, 35);
            rsSetState = Com.Method<SetPtrFn>(Context, 43);
            rsSetViewports = Com.Method<RSSetViewportsFn>(Context, 44);
            CopyRegion = Com.Method<CopyRegionFn>(Context, 46);
            CopyResource = Com.Method<CopyResourceFn>(Context, 47);
            updateSub = Com.Method<UpdateSubresourceFn>(Context, 48);
            clearRtv = Com.Method<ClearRtvFn>(Context, 50);
            genMips = Com.Method<SetPtrFn>(Context, 54);
            flush = Com.Method<VoidFn>(Context, 111);

            srvNative = Marshal.AllocHGlobal(IntPtr.Size * 8);
            rtvNative = Marshal.AllocHGlobal(IntPtr.Size);
            vpNative = Marshal.AllocHGlobal(24);
            cbNative = Marshal.AllocHGlobal(IntPtr.Size);
            clearNative = Marshal.AllocHGlobal(16);
            boxNative = Marshal.AllocHGlobal(24);
            descNative = Marshal.AllocHGlobal(512);
            samplers3 = Marshal.AllocHGlobal(IntPtr.Size * 3);

            CreateStates();
            string shaderProblem = CompileShaders();
            if (shaderProblem != null) return shaderProblem;
            return null;
        }

        void CreateStates()
        {
            sampLinear = CreateSampler(0x15, 3);
            sampPoint = CreateSampler(0, 3);
            sampBorder = CreateSampler(0x15, 4);
            Marshal.WriteIntPtr(samplers3, 0, sampLinear);
            Marshal.WriteIntPtr(samplers3, IntPtr.Size, sampPoint);
            Marshal.WriteIntPtr(samplers3, IntPtr.Size * 2, sampBorder);

            // blend: premultiplied "over", and none
            int[] bd = new int[2 + 8 * 8];
            bd[2] = 1; bd[3] = 2; bd[4] = 6; bd[5] = 1; bd[6] = 2; bd[7] = 6; bd[8] = 1; bd[9] = 0x0F;
            blendOver = CreateFromInts(20, bd);
            bd[2] = 0;
            blendOff = CreateFromInts(20, bd);

            RasterizerDesc rd = new RasterizerDesc();
            rd.FillMode = 3; rd.CullMode = 1; rd.DepthClipEnable = 1;
            Marshal.StructureToPtr(rd, descNative, false);
            Com.Check(Com.Method<CreateFromDescFn>(Device, 22)(Device, descNative, out raster), "CreateRasterizerState");

            BufferDesc cb = new BufferDesc();
            cb.ByteWidth = Constants.Size;
            cb.Usage = UsageDynamic;
            cb.BindFlags = BindConstant;
            cb.CpuAccessFlags = CpuWrite;
            Com.Check(createBuffer(Device, ref cb, IntPtr.Zero, out cbuffer), "CreateBuffer(constants)");
            Marshal.WriteIntPtr(cbNative, cbuffer);
        }

        IntPtr CreateFromInts(int slot, int[] values)
        {
            IntPtr mem = Marshal.AllocHGlobal(values.Length * 4);
            try
            {
                Marshal.Copy(values, 0, mem, values.Length);
                IntPtr obj;
                Com.Check(Com.Method<CreateFromDescFn>(Device, slot)(Device, mem, out obj), "Create state #" + slot);
                return obj;
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        IntPtr CreateSampler(int filter, int address)
        {
            SamplerDesc sd = new SamplerDesc();
            sd.Filter = filter;
            sd.AddressU = sd.AddressV = sd.AddressW = address;
            sd.ComparisonFunc = 1;
            sd.MaxLOD = float.MaxValue;
            Marshal.StructureToPtr(sd, descNative, false);
            IntPtr s;
            Com.Check(Com.Method<CreateFromDescFn>(Device, 23)(Device, descNative, out s), "CreateSamplerState");
            return s;
        }

        public string ShaderSource;

        string CompileShaders()
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(Gfx).Assembly.Location), "glass.hlsl");
            if (!File.Exists(path)) return "glass.hlsl not found next to the exe";
            ShaderSource = File.ReadAllText(path);
            string err;
            VS = Compile("VSMain", "vs_4_0", true, out err); if (VS == IntPtr.Zero) return err;
            PSSample = Compile("PSSample", "ps_4_0", false, out err); if (PSSample == IntPtr.Zero) return err;
            PSTent = Compile("PSTent", "ps_4_0", false, out err); if (PSTent == IntPtr.Zero) return err;
            PSBlur = Compile("PSBlur", "ps_4_0", false, out err); if (PSBlur == IntPtr.Zero) return err;
            PSAlphaDown = Compile("PSAlphaDown", "ps_4_0", false, out err); if (PSAlphaDown == IntPtr.Zero) return err;
            PSBox = Compile("PSBox", "ps_4_0", false, out err); if (PSBox == IntPtr.Zero) return err;
            PSStats = Compile("PSStats", "ps_4_0", false, out err); if (PSStats == IntPtr.Zero) return err;
            PSGlass = Compile("PSGlass", "ps_4_0", false, out err); if (PSGlass == IntPtr.Zero) return err;
            return null;
        }

        IntPtr Compile(string entry, string target, bool vertex, out string error)
        {
            error = null;
            byte[] src = Encoding.ASCII.GetBytes(ShaderSource);
            IntPtr mem = Marshal.AllocHGlobal(src.Length);
            Marshal.Copy(src, 0, mem, src.Length);
            IntPtr code = IntPtr.Zero, errors = IntPtr.Zero, shader = IntPtr.Zero;
            try
            {
                // D3DCOMPILE_OPTIMIZATION_LEVEL3 | D3DCOMPILE_ENABLE_STRICTNESS
                int hr = Dll.D3DCompile(mem, new IntPtr(src.Length), "glass.hlsl", IntPtr.Zero, IntPtr.Zero, entry, target, (1u << 15) | (1u << 11), 0, out code, out errors);
                if (errors != IntPtr.Zero)
                {
                    IntPtr p = Com.Method<GetBufferPointerFn>(errors, 3)(errors);
                    string text = Marshal.PtrToStringAnsi(p);
                    if (hr < 0) { error = "shader " + entry + ": " + text; return IntPtr.Zero; }
                }
                if (hr < 0) { error = "shader " + entry + " failed " + Com.Hex(hr); return IntPtr.Zero; }
                IntPtr bytes = Com.Method<GetBufferPointerFn>(code, 3)(code);
                IntPtr size = Com.Method<GetBufferPointerFn>(code, 4)(code);
                hr = Com.Method<CreateShaderFn>(Device, vertex ? 12 : 15)(Device, bytes, size, IntPtr.Zero, out shader);
                if (hr < 0) { error = "create shader " + entry + " failed " + Com.Hex(hr); return IntPtr.Zero; }
                return shader;
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
                Com.Release(ref code);
                Com.Release(ref errors);
            }
        }

        // ---- resources ----

        public Tex CreateTex(int w, int h, int format, bool renderTarget, bool shaderResource)
        {
            return CreateTex(w, h, format, renderTarget, shaderResource, IntPtr.Zero, 0, UsageDefault, 0);
        }

        public Tex CreateTex(int w, int h, int format, bool renderTarget, bool shaderResource, IntPtr initial, int initialPitch, int usage, uint cpu)
        {
            TextureDesc d = new TextureDesc();
            d.Width = (uint)Math.Max(1, w);
            d.Height = (uint)Math.Max(1, h);
            d.MipLevels = 1;
            d.ArraySize = 1;
            d.Format = format;
            d.SampleCount = 1;
            d.Usage = usage;
            d.BindFlags = (renderTarget ? BindRenderTarget : 0) | (shaderResource ? BindShaderResource : 0);
            d.CpuAccessFlags = cpu;
            Tex t = new Tex();
            t.W = (int)d.Width; t.H = (int)d.Height; t.Format = format;
            IntPtr init = IntPtr.Zero;
            if (initial != IntPtr.Zero)
            {
                SubresourceData sd = new SubresourceData();
                sd.Data = initial;
                sd.RowPitch = (uint)initialPitch;
                sd.SlicePitch = 0;
                init = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SubresourceData)));
                Marshal.StructureToPtr(sd, init, false);
            }
            try
            {
                Com.Check(createTexture(Device, ref d, init, out t.Ptr), "CreateTexture2D " + w + "x" + h + " fmt " + format);
            }
            finally { if (init != IntPtr.Zero) Marshal.FreeHGlobal(init); }
            if (shaderResource) Com.Check(createSrv(Device, t.Ptr, IntPtr.Zero, out t.Srv), "CreateShaderResourceView");
            if (renderTarget) Com.Check(createRtv(Device, t.Ptr, IntPtr.Zero, out t.Rtv), "CreateRenderTargetView");
            return t;
        }

        public Tex CreateStaging(int w, int h, int format)
        {
            return CreateTex(w, h, format, false, false, IntPtr.Zero, 0, UsageStaging, CpuRead);
        }

        // A texture another device can open (D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX), with its handle.
        public static string SharedNote = "";

        public static IntPtr CreateSharedTexture(IntPtr device, int w, int h, int format, out IntPtr sharedHandle, out IntPtr keyedMutex)
        {
            TextureDesc d = new TextureDesc();
            d.Width = (uint)w; d.Height = (uint)h; d.MipLevels = 1; d.ArraySize = 1; d.Format = format; d.SampleCount = 1;
            IntPtr tex = IntPtr.Zero;
            int hr = -1;
            uint[][] tries = { new uint[] { BindShaderResource | BindRenderTarget, 0x10 }, new uint[] { BindShaderResource, 0x10 }, new uint[] { BindShaderResource | BindRenderTarget, 0x2 }, new uint[] { BindShaderResource | BindRenderTarget, 0 } };
            foreach (uint[] t in tries)
            {
                d.BindFlags = t[0];
                d.MiscFlags = t[1];
                hr = Com.Method<CreateTexture2DFn>(device, 5)(device, ref d, IntPtr.Zero, out tex);
                SharedNote += "shared texture bind 0x" + t[0].ToString("X") + " misc 0x" + t[1].ToString("X") + ": " + Com.Hex(hr) + "; ";
                if (hr >= 0 && tex != IntPtr.Zero) break;
                tex = IntPtr.Zero;
            }
            if (tex == IntPtr.Zero) throw new InvalidOperationException("CreateTexture2D(shared) failed: " + SharedNote + DebugMessages(device));
            IntPtr res = Com.Query(tex, IID.DxgiResource);
            if (res == IntPtr.Zero) throw new InvalidOperationException("no IDXGIResource on the shared texture");
            try { Com.Check(Com.Method<OutPtrFn>(res, 8)(res, out sharedHandle), "IDXGIResource::GetSharedHandle"); }
            finally { Com.Release(ref res); }
            keyedMutex = Com.Query(tex, IID.KeyedMutex);
            if (keyedMutex == IntPtr.Zero) throw new InvalidOperationException("no IDXGIKeyedMutex on the shared texture");
            return tex;
        }

        // Opens a texture shared by another device on this one.
        public Tex OpenShared(IntPtr sharedHandle, int w, int h, int format, out IntPtr keyedMutex)
        {
            Tex t = new Tex();
            t.W = w; t.H = h; t.Format = format;
            Com.Check(Com.Method<OpenSharedResourceFn>(Device, 28)(Device, sharedHandle, ref IID.Texture2D, out t.Ptr), "ID3D11Device::OpenSharedResource");
            keyedMutex = Com.Query(t.Ptr, IID.KeyedMutex);
            if (keyedMutex == IntPtr.Zero) throw new InvalidOperationException("no IDXGIKeyedMutex on the opened texture");
            return t;
        }

        public static bool Lock(IntPtr keyedMutex, uint ms) { return Com.Method<AcquireSyncFn>(keyedMutex, 8)(keyedMutex, 0, ms) == 0; }
        public static void Unlock(IntPtr keyedMutex) { Com.Method<ReleaseSyncFn>(keyedMutex, 9)(keyedMutex, 0); }

        public IntPtr CreateRtvFor(IntPtr texture)
        {
            IntPtr rtv;
            Com.Check(createRtv(Device, texture, IntPtr.Zero, out rtv), "CreateRenderTargetView(back buffer)");
            return rtv;
        }

        public void Upload(Tex t, int[] pixels, int w, int h)
        {
            GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try { updateSub(Context, t.Ptr, 0, IntPtr.Zero, pin.AddrOfPinnedObject(), (uint)(w * 4), 0); }
            finally { pin.Free(); }
        }

        public void UploadRegion(Tex t, int[] pixels, int srcW, RECT part)
        {
            // part: the rows and columns of 'pixels' (srcW wide) to send, to the same place in t
            if (part.IsEmpty) return;
            GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                Box box = new Box();
                box.Left = (uint)part.Left; box.Top = (uint)part.Top; box.Right = (uint)part.Right; box.Bottom = (uint)part.Bottom; box.Front = 0; box.Back = 1;
                Marshal.StructureToPtr(box, boxNative, false);
                IntPtr from = IntPtr.Add(pin.AddrOfPinnedObject(), (part.Top * srcW + part.Left) * 4);
                updateSub(Context, t.Ptr, 0, boxNative, from, (uint)(srcW * 4), 0);
            }
            finally { pin.Free(); }
        }

        // Reads a texture back (through a staging copy): BGRA ints, top-down. Stalls the CPU.
        public int[] ReadBack(Tex src, Tex staging)
        {
            CopyResource(Context, staging.Ptr, src.Ptr);
            Mapped m;
            Com.Check(map(Context, staging.Ptr, 0, MapRead, 0, out m), "Map(staging)");
            try
            {
                int[] px = new int[src.W * src.H];
                for (int y = 0; y < src.H; y++) Marshal.Copy(IntPtr.Add(m.Data, (int)(y * m.RowPitch)), px, y * src.W, src.W);
                return px;
            }
            finally { unmap(Context, staging.Ptr, 0); }
        }

        // Reads a small float texture back (RGBA32F): one float4 per pixel.
        public float[] ReadBackFloats(Tex src, Tex staging)
        {
            CopyResource(Context, staging.Ptr, src.Ptr);
            Mapped m;
            Com.Check(map(Context, staging.Ptr, 0, MapRead, 0, out m), "Map(staging)");
            try
            {
                float[] px = new float[src.W * src.H * 4];
                for (int y = 0; y < src.H; y++) Marshal.Copy(IntPtr.Add(m.Data, (int)(y * m.RowPitch)), px, y * src.W * 4, src.W * 4);
                return px;
            }
            finally { unmap(Context, staging.Ptr, 0); }
        }

        public void Copy(Tex dst, int dx, int dy, IntPtr srcTexture, RECT src)
        {
            if (src.IsEmpty) return;
            Box box;
            box.Left = (uint)src.Left; box.Top = (uint)src.Top; box.Right = (uint)src.Right; box.Bottom = (uint)src.Bottom; box.Front = 0; box.Back = 1;
            CopyRegion(Context, dst.Ptr, 0, (uint)dx, (uint)dy, 0, srcTexture, 0, ref box);
        }

        public void Clear(Tex t, float r, float g, float b, float a)
        {
            Marshal.Copy(new float[] { r, g, b, a }, 0, clearNative, 4);
            clearRtv(Context, t.Rtv, clearNative);
        }

        public void Flush() { flush(Context); }

        // ---- passes ----

        public void SetConstants(Constants c)
        {
            Mapped m;
            Com.Check(map(Context, cbuffer, 0, MapWriteDiscard, 0, out m), "Map(constants)");
            Marshal.Copy(c.Data, 0, m.Data, c.Data.Length);
            unmap(Context, cbuffer, 0);
        }

        public static volatile string Stage = "";

        // Draws one full-screen pass of 'ps' into rtv (w x h) reading the given textures at t0.. (null allowed).
        public void Pass(IntPtr ps, IntPtr rtv, int w, int h, bool blendOverTarget, params Tex[] inputs)
        {
            if (rtv == IntPtr.Zero) throw new InvalidOperationException("Pass: no render target");
            for (int i = 0; i < 8; i++) Marshal.WriteIntPtr(srvNative, i * IntPtr.Size, i < inputs.Length && inputs[i] != null ? inputs[i].Srv : IntPtr.Zero);
            // (unbind everything first: a texture drawn into last pass may be read this pass)
            Stage = "pass: unbind";
            omSetTargets(Context, 0, IntPtr.Zero, IntPtr.Zero);
            Stage = "pass: srvs";
            psSetSrvs(Context, 0, 8, srvNative);
            Marshal.WriteIntPtr(rtvNative, rtv);
            Stage = "pass: rtv";
            omSetTargets(Context, 1, rtvNative, IntPtr.Zero);
            Viewport vp = new Viewport();
            vp.Width = w; vp.Height = h; vp.MaxDepth = 1;
            Marshal.StructureToPtr(vp, vpNative, false);
            Stage = "pass: viewport";
            rsSetViewports(Context, 1, vpNative);
            rsSetState(Context, raster);
            Stage = "pass: blend";
            omSetBlend(Context, blendOverTarget ? blendOver : blendOff, IntPtr.Zero, 0xFFFFFFFF);
            iaSetLayout(Context, IntPtr.Zero);
            iaSetTopology(Context, 4);
            Stage = "pass: shaders";
            vsSetShader(Context, VS, IntPtr.Zero, 0);
            psSetShader(Context, ps, IntPtr.Zero, 0);
            psSetSamplers(Context, 0, 3, samplers3);
            psSetConstants(Context, 0, 1, cbNative);
            Stage = "pass: draw";
            draw(Context, 3, 0);
            Stage = "pass: unbind after";
            omSetTargets(Context, 0, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < 8; i++) Marshal.WriteIntPtr(srvNative, i * IntPtr.Size, IntPtr.Zero);
            psSetSrvs(Context, 0, 8, srvNative);
            Stage = "";
        }

        // ---- GPU timing (timestamp queries) ----

        public sealed class GpuTimer : IDisposable
        {
            public IntPtr Disjoint, Start, End;
            public bool Pending;
            public void Dispose() { Com.Release(ref Disjoint); Com.Release(ref Start); Com.Release(ref End); }
        }

        public GpuTimer CreateTimer()
        {
            GpuTimer t = new GpuTimer();
            t.Disjoint = CreateQuery(3);   // D3D11_QUERY_TIMESTAMP_DISJOINT
            t.Start = CreateQuery(2);      // D3D11_QUERY_TIMESTAMP
            t.End = CreateQuery(2);
            return t;
        }

        IntPtr CreateQuery(int type)
        {
            QueryDesc qd = new QueryDesc();
            qd.Query = type;
            Marshal.StructureToPtr(qd, descNative, false);
            IntPtr q;
            Com.Check(Com.Method<CreateFromDescFn>(Device, 24)(Device, descNative, out q), "CreateQuery");
            return q;
        }

        public void TimerBegin(GpuTimer t) { queryBegin(Context, t.Disjoint); queryEnd(Context, t.Start); }
        public void TimerEnd(GpuTimer t) { queryEnd(Context, t.End); queryEnd(Context, t.Disjoint); t.Pending = true; }

        // Milliseconds of GPU time between begin and end, or -1 if not (yet) known. wait: block until known.
        public double TimerRead(GpuTimer t, bool wait)
        {
            if (!t.Pending) return -1;
            IntPtr data = Marshal.AllocHGlobal(16);
            try
            {
                uint flags = wait ? 0u : 1u;   // D3D11_ASYNC_GETDATA_DONOTFLUSH when not waiting
                int hr;
                for (int spin = 0; ; spin++)
                {
                    hr = getData(Context, t.Disjoint, data, 16, flags);
                    if (hr == 0) break;
                    if (!wait) return -1;
                    if (spin > 200000) return -1;
                    System.Threading.Thread.Sleep(0);
                }
                long freq = Marshal.ReadInt64(data, 0);
                int disjoint = Marshal.ReadInt32(data, 8);
                if (getData(Context, t.Start, data, 8, 0) != 0) return -1;
                long a = Marshal.ReadInt64(data, 0);
                if (getData(Context, t.End, data, 8, 0) != 0) return -1;
                long b = Marshal.ReadInt64(data, 0);
                t.Pending = false;
                if (disjoint != 0 || freq == 0) return -1;
                return (b - a) * 1000.0 / freq;
            }
            finally { Marshal.FreeHGlobal(data); }
        }

        // ---- swap chains and composition ----

        public sealed class Chain : IDisposable
        {
            public IntPtr SwapChain, BackBuffer, Rtv, Waitable, Target, Visual;
            public int W, H;
            public void Dispose()
            {
                Com.Release(ref Visual);
                Com.Release(ref Target);
                Com.Release(ref Rtv);
                Com.Release(ref BackBuffer);
                Com.Release(ref SwapChain);
                Waitable = IntPtr.Zero;   // (owned by the swap chain)
            }
        }

        public void EnsureDComp()
        {
            if (DComp != IntPtr.Zero) return;
            IntPtr dxgiDevice = Com.Query(Device, IID.DxgiDevice);
            if (dxgiDevice == IntPtr.Zero) throw new InvalidOperationException("no IDXGIDevice");
            try { Com.Check(Dll.DCompositionCreateDevice(dxgiDevice, ref IID.DCompDevice, out DComp), "DCompositionCreateDevice"); }
            finally { Com.Release(ref dxgiDevice); }
        }

        // A premultiplied-alpha composition swap chain of w x h, shown in hwnd through a visual.
        public Chain CreateChain(IntPtr hwnd, int w, int h, bool waitable)
        {
            EnsureDComp();
            Chain c = new Chain();
            c.W = w; c.H = h;
            SwapChainDesc1 d = new SwapChainDesc1();
            d.Width = (uint)w;
            d.Height = (uint)h;
            d.Format = FmtBgra8;
            d.SampleCount = 1;
            d.BufferUsage = 0x20;   // DXGI_USAGE_RENDER_TARGET_OUTPUT
            d.BufferCount = 2;
            d.Scaling = 0;          // STRETCH (required for composition)
            d.SwapEffect = 3;       // FLIP_SEQUENTIAL
            d.AlphaMode = 1;        // PREMULTIPLIED
            d.Flags = waitable ? 0x40u : 0u;   // FRAME_LATENCY_WAITABLE_OBJECT
            IntPtr dxgiDevice = Com.Query(Device, IID.DxgiDevice);
            try
            {
                Com.Check(Com.Method<CreateSwapChainForCompositionFn>(Factory2, 24)(Factory2, dxgiDevice, ref d, IntPtr.Zero, out c.SwapChain), "CreateSwapChainForComposition");
            }
            finally { Com.Release(ref dxgiDevice); }
            if (waitable)
            {
                IntPtr sc2 = Com.Query(c.SwapChain, IID.SwapChain2);
                if (sc2 != IntPtr.Zero)
                {
                    Com.Method<UintFn>(sc2, 31)(sc2, 1);   // SetMaximumFrameLatency(1)
                    c.Waitable = Com.Method<HandleFn>(sc2, 33)(sc2);
                    Com.Release(ref sc2);
                }
            }
            Com.Check(Com.Method<GetBufferFn>(c.SwapChain, 9)(c.SwapChain, 0, ref IID.Texture2D, out c.BackBuffer), "GetBuffer");
            c.Rtv = CreateRtvFor(c.BackBuffer);
            Com.Check(Com.Method<CreateTargetForHwndFn>(DComp, 6)(DComp, hwnd, 1, out c.Target), "CreateTargetForHwnd");
            Com.Check(Com.Method<OutPtrFn>(DComp, 7)(DComp, out c.Visual), "CreateVisual");
            Com.Check(Com.Method<PtrFn>(c.Visual, 15)(c.Visual, c.SwapChain), "IDCompositionVisual::SetContent");
            Com.Check(Com.Method<PtrFn>(c.Target, 3)(c.Target, c.Visual), "IDCompositionTarget::SetRoot");
            Commit();
            return c;
        }

        public void Commit()
        {
            if (DComp != IntPtr.Zero) Com.Check(Com.Method<NoArgFn>(DComp, 3)(DComp), "IDCompositionDevice::Commit");
        }

        // Moves a chain's visual within its window (slots 4 and 6: SetOffsetX/Y(float)).
        public int SetVisualOffset(Chain c, float x, float y)
        {
            int hr = Com.Method<FloatFn>(c.Visual, 4)(c.Visual, x);
            if (hr < 0) return hr;
            return Com.Method<FloatFn>(c.Visual, 6)(c.Visual, y);
        }

        public int Present(Chain c, uint sync)
        {
            return Com.Method<PresentFn>(c.SwapChain, 8)(c.SwapChain, sync, 0);
        }

        public void Dispose()
        {
            Com.Release(ref VS); Com.Release(ref PSSample); Com.Release(ref PSTent); Com.Release(ref PSBlur);
            Com.Release(ref PSAlphaDown); Com.Release(ref PSBox); Com.Release(ref PSStats); Com.Release(ref PSGlass);
            Com.Release(ref sampLinear); Com.Release(ref sampPoint); Com.Release(ref sampBorder);
            Com.Release(ref blendOver); Com.Release(ref blendOff); Com.Release(ref raster); Com.Release(ref cbuffer);
            Com.Release(ref DComp);
            Com.Release(ref Factory2);
            Com.Release(ref Output);
            Com.Release(ref Adapter);
            Com.Release(ref Context);
            Com.Release(ref Device);
            foreach (IntPtr p in new IntPtr[] { srvNative, rtvNative, vpNative, cbNative, clearNative, boxNative, descNative, samplers3 })
                if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            srvNative = rtvNative = vpNative = cbNative = clearNative = boxNative = descNative = samplers3 = IntPtr.Zero;
        }
    }

    // The shader's parameters: float4 p[32] (see glass.hlsl for the slots).
    internal sealed class Constants
    {
        public const int Slots = 32;
        public const uint Size = Slots * 16;
        public readonly float[] Data = new float[Slots * 4];
        public void Set(int slot, float x, float y, float z, float w)
        {
            int o = slot * 4;
            Data[o] = x; Data[o + 1] = y; Data[o + 2] = z; Data[o + 3] = w;
        }
    }
}
