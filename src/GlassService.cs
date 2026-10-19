using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace LGEngine
{
#if LGCLIENT
    // The client library has no engine (GlassProfile would pull in GlassShape and the whole map): it cannot check a profile string
    // itself, so GlassSurface's early check accepts it and the host's check (the same GlassProfile.Parse) answers with Failed.
    internal static class GlassProfile
    {
        public static readonly string[] PresetNames = new string[0];
        public static object Parse(string text) { return text; }
    }
#endif

    // The glass surface service (SERVICE.md): features connect, create surfaces, hand in contents and dynamic properties.
    // The engine runs in this process on a render thread of its own (GlassRenderThread); any thread may create surfaces and call
    // them. GlassMode.Host talks to a GlassHostServer over a pipe instead (see ModeUsed).
    public sealed class GlassService : IDisposable
    {
        readonly IGlassTransport transport;
        readonly GlassMode modeUsed;
        readonly object gate = new object();
        readonly List<GlassSurface> surfaces = new List<GlassSurface>();
        volatile bool disposed;

        GlassService(IGlassTransport transport, GlassMode used)
        {
            this.transport = transport;
            this.modeUsed = used;
        }

        public string Transport { get { return transport.Name; } }

        // The mode actually in use: InProcess for Auto (the render thread); Host for Host (the pipe transport, offline when no host
        // runs); UiThread for UiThread.
        public GlassMode ModeUsed { get { return modeUsed; } }

        // The render thread's pacing figures (empty for the UI-thread mode).
#if !LGCLIENT
        public GlassPacingInfo Pacing
        {
            get
            {
                RenderThreadTransport t = transport as RenderThreadTransport;
                return t == null ? new GlassPacingInfo() : t.Pacing;
            }
        }

        // Whether the engine's render thread is running (false for the UI-thread mode, and after Dispose). For tests.
        public bool EngineThreadAlive
        {
            get
            {
                RenderThreadTransport t = transport as RenderThreadTransport;
                return t != null && t.Alive;
            }
        }
#else
        public bool EngineThreadAlive { get { return false; } }
#endif

        // Size of the DWM timing structure the latency and pacing read (a check that its layout is right: 292).
        public static int TimingStructSize { get { return GlassVblank.StructSize; } }

        // Never throws, except ArgumentException("HostPath must be a full path") for a HostPath that is not fully qualified
        // (X:\... or \\server\share\...).
        public static GlassService Connect(GlassConnectOptions options)
        {
            GlassMode mode = options == null ? GlassMode.Auto : options.Mode;
            if (options != null && !string.IsNullOrEmpty(options.HostPath) && !FullyQualified(options.HostPath))
                throw new ArgumentException("HostPath must be a full path", "options");
#if !LGCLIENT
            if (mode == GlassMode.UiThread) return new GlassService(new InProcessTransport(), GlassMode.UiThread);
            if (mode == GlassMode.InProcess) return new GlassService(new RenderThreadTransport("render thread"), GlassMode.InProcess);
#endif
            // Host and Auto: the host process (GlassHostServer), over its pipe and sections; started on demand when no host answers
            // (HostPath, or LGEngineHost.exe beside this assembly). With no host reachable, Host mode is offline (every surface it makes
            // is Failed "host unavailable") and Auto falls back to the in-process render thread.
            string suffix = options == null || options.HostSuffix == null ? "" : options.HostSuffix;
            string path = ResolveHostPath(options);
            PipeTransport pt = new PipeTransport(suffix, delegate { return StartHost(path, suffix); });
            GlassService svc = new GlassService(pt, GlassMode.Host);
            // Lost and Restarted are wired before the transport starts connecting (no drop is missed); they go to the context that was
            // current here, as the surfaces' events go to Create's (no context: on the I/O thread).
            SynchronizationContext ctx = SynchronizationContext.Current;
            pt.Lost = delegate { svc.Raise(ctx, true); };
            pt.Restarted = delegate { svc.Raise(ctx, false); };
            pt.Start();
#if !LGCLIENT
            if (mode != GlassMode.Host && !pt.EverConnected)
            {
                pt.Dispose();
                return new GlassService(new RenderThreadTransport("render thread (host unavailable)"), GlassMode.InProcess);
            }
#endif
            return svc;
        }

        // X:\... or \\server\share\... ("/" counts as "\"). Path.IsPathRooted also takes "C:foo" and "\foo", which hang on the
        // process's current drive or folder.
        static bool FullyQualified(string p)
        {
            if (p.Length < 3) return false;
            bool sep1 = p[1] == '\\' || p[1] == '/', sep2 = p[2] == '\\' || p[2] == '/';
            if (p[1] == ':') return sep2 && ((p[0] >= 'A' && p[0] <= 'Z') || (p[0] >= 'a' && p[0] <= 'z'));
            return (p[0] == '\\' || p[0] == '/') && sep1;
        }

        void Raise(SynchronizationContext ctx, bool lost)
        {
            SendOrPostCallback run = delegate
            {
                EventHandler h = lost ? Lost : ServiceRestarted;
                if (h != null) h(this, EventArgs.Empty);
            };
            if (ctx == null) { run(null); return; }
            try { ctx.Post(run, null); }
            catch (Exception) { }
        }

        // Test seam: replaces the Process.Start below (args: the exe path, the suffix; true = a host was started and its pipe is worth
        // waiting for). The bench starts a GlassHostServer in its own process with it.
        internal static Func<string, string, bool> HostStarter;

        // HostPath, or LGEngineHost.exe beside the assembly that holds this class (null if that cannot be told).
        static string ResolveHostPath(GlassConnectOptions options)
        {
            if (options != null && !string.IsNullOrEmpty(options.HostPath)) return options.HostPath;
            try
            {
                string dir = Path.GetDirectoryName(typeof(GlassService).Assembly.Location);
                return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "LGEngineHost.exe");
            }
            catch (Exception) { return null; }
        }

        // Starts the host process: no window, no shell. False when there is no exe or it would not start.
        static bool StartHost(string path, string suffix)
        {
            Func<string, string, bool> seam = HostStarter;
            if (seam != null)
            {
                try { return seam(path, suffix); }
                catch (Exception) { return false; }
            }
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                ProcessStartInfo psi = new ProcessStartInfo(path);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.WorkingDirectory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(suffix)) psi.Arguments = "--suffix \"" + suffix.Replace("\"", "") + "\"";
                Process p = Process.Start(psi);
                if (p == null) return false;
                p.Dispose();
                return true;
            }
            catch (Exception) { return false; }
        }

        // Test: this client dies without a word (the pipe closes, nothing is told to the host). Dispose afterwards only cleans up.
        internal void AbandonForTest()
        {
            PipeTransport t = transport as PipeTransport;
            if (t != null) t.Abandon();
        }

        // Host mode: the pipe broke (the surfaces keep their state and wait); and the host came back and the surfaces were created
        // again (Restarted only once every surface is back with its last state). Raised on the SynchronizationContext that was current at
        // Connect (posted), else on the service's I/O thread.
        public event EventHandler Lost;
        public event EventHandler ServiceRestarted;

        // Host mode: whether the pipe to the host is up right now (always true for the in-process modes).
        public bool HostConnected
        {
            get
            {
                PipeTransport t = transport as PipeTransport;
                return t == null || t.Connected;
            }
        }

        // A new surface. Throws only for a null spec, an empty Label or a value over the host's limits (ArgumentException: scale,
        // label over 256 bytes, profile over 2400 characters, elevation); any other fault in the spec makes a surface that has
        // already failed (its Failed event is raised to the first listener). The surface's events go to the
        // SynchronizationContext current on this thread (see GlassSurface).
        public GlassSurface Create(GlassSpec spec)
        {
            if (disposed) throw new ObjectDisposedException("GlassService");
            if (spec == null) throw new ArgumentNullException("spec");
            if (string.IsNullOrEmpty(spec.Label)) throw new ArgumentException("spec.Label is required", "spec");
            string range = GlassWire.CheckSpecValues(spec);   // (the host's limits, the same call it makes)
            if (range != null) throw new ArgumentException(range, "spec");
            GlassSurface s = new GlassSurface(this, transport, spec.Clone(), SynchronizationContext.Current);
            bool late;
            lock (gate)
            {
                late = disposed;   // (a Dispose that won the race: it has already taken its list of surfaces)
                if (!late) surfaces.Add(s);
            }
            if (late) s.FailLate("service disposed");
            return s;
        }

        internal void Forget(GlassSurface s)
        {
            lock (gate) surfaces.Remove(s);
        }

        // Disposes every surface (their windows and bitmaps go on the render thread), then stops the thread (waits up to 2 s).
        public void Dispose()
        {
            GlassSurface[] all;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                all = surfaces.ToArray();
                surfaces.Clear();
            }
            try
            {
                foreach (GlassSurface s in all) s.Dispose();
            }
            finally
            {
                transport.Dispose();
            }
        }

        public static string SelfTest()
        {
            return "service: in-process transport ready; host pipe transport built (GlassHostServer)";
        }
    }
}
