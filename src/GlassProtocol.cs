using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Drawing;
using Microsoft.Win32.SafeHandles;

namespace LGEngine
{
    // Counters the loopback test reads to prove nothing is left behind (threads, sections, sessions), and the seqlock retry count.
    public static class GlassPipeStats
    {
        internal static int threads, sections, sessions, retries;
        public static int LiveThreads { get { return Thread.VolatileRead(ref threads); } }
        public static int LiveSections { get { return Thread.VolatileRead(ref sections); } }
        public static int LiveSessions { get { return Thread.VolatileRead(ref sessions); } }
        public static int SeqlockRetries { get { return Thread.VolatileRead(ref retries); } }
    }

    // Names (SERVICE.md section 3 / design 1): they carry the protocol major and a hash of the user's SID. A test adds a suffix so
    // that it never meets a real host.
    internal static class GlassNames
    {
        public const int Major = 1;
        static string hash;

        public static string UserHash()
        {
            if (hash != null) return hash;
            string sid;
            using (WindowsIdentity id = WindowsIdentity.GetCurrent()) sid = id.User.Value;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] d = sha.ComputeHash(Encoding.UTF8.GetBytes(sid));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 4; i++) sb.Append(d[i].ToString("x2"));
                hash = sb.ToString();
            }
            return hash;
        }

        static string Clean(string suffix)
        {
            if (string.IsNullOrEmpty(suffix)) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in suffix) if (char.IsLetterOrDigit(c) && c < 128 && sb.Length < 16) sb.Append(c);
            return sb.Length == 0 ? "" : "." + sb;
        }

        static string Tag(string suffix) { return UserHash() + "." + Major + Clean(suffix); }

        // The session id is in the pipe name (pipe names are machine-wide; the Local\ names are per session already), so that a host in
        // another logon session of the same user is never the one a client finds.
        static int sessionId = -1;
        static int SessionId()
        {
            if (sessionId < 0)
            {
                try { using (System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess()) sessionId = p.SessionId; }
                catch (Exception) { sessionId = 0; }
            }
            return sessionId;
        }

        public static string PipeShort(string suffix) { return "LGEngine." + UserHash() + "." + Major + ".s" + SessionId() + Clean(suffix); }
        public static string PipeFull(string suffix) { return @"\\.\pipe\" + PipeShort(suffix); }
        public static string HostMutex(string suffix) { return @"Local\LGEngineHost." + Tag(suffix); }
        public static string HostWake(string suffix) { return @"Local\LGEngine." + Tag(suffix) + ".wake"; }

        public static string Section(string suffix, uint id)
        {
            byte[] n = new byte[8];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(n);
            StringBuilder sb = new StringBuilder();
            foreach (byte b in n) sb.Append(b.ToString("x2"));
            return @"Local\LGEngine.S." + UserHash() + Clean(suffix) + "." + id + "." + sb;
        }
    }

    // DACLs: the current user and SYSTEM only, no inheritance (design 2).
    internal static class GlassSecurity
    {
        static SecurityIdentifier User()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent()) return id.User;
        }

        static SecurityIdentifier SystemSid() { return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null); }

        public static PipeSecurity Pipe()
        {
            PipeSecurity s = new PipeSecurity();
            s.SetAccessRuleProtection(true, false);
            s.AddAccessRule(new PipeAccessRule(User(), PipeAccessRights.FullControl, AccessControlType.Allow));
            s.AddAccessRule(new PipeAccessRule(SystemSid(), PipeAccessRights.FullControl, AccessControlType.Allow));
            return s;
        }

        public static MemoryMappedFileSecurity Section()
        {
            MemoryMappedFileSecurity s = new MemoryMappedFileSecurity();
            s.SetAccessRuleProtection(true, false);
            s.AddAccessRule(new AccessRule<MemoryMappedFileRights>(User(), MemoryMappedFileRights.FullControl, AccessControlType.Allow));
            s.AddAccessRule(new AccessRule<MemoryMappedFileRights>(SystemSid(), MemoryMappedFileRights.FullControl, AccessControlType.Allow));
            return s;
        }

        public static EventWaitHandleSecurity Event()
        {
            EventWaitHandleSecurity s = new EventWaitHandleSecurity();
            s.SetAccessRuleProtection(true, false);
            s.AddAccessRule(new EventWaitHandleAccessRule(User(), EventWaitHandleRights.FullControl, AccessControlType.Allow));
            s.AddAccessRule(new EventWaitHandleAccessRule(SystemSid(), EventWaitHandleRights.FullControl, AccessControlType.Allow));
            return s;
        }
    }

    // The Win32 calls the pipe code needs.
    internal static class PipeNative
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SecurityAttributes
        {
            public int Length;
            public IntPtr Descriptor;
            public int Inherit;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuffer, uint inBuffer, uint timeout, IntPtr attributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ProcessIdToSessionId(uint pid, out uint session);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        // A message-mode, overlapped, duplex server instance with the user-only DACL, remote clients rejected (and, for the
        // first, FILE_FLAG_FIRST_PIPE_INSTANCE: nobody else owns the name). Throws IOException (the Win32 code in Data["win32"]).
        public static NamedPipeServerStream CreateServer(string fullName, bool first)
        {
            byte[] sd = GlassSecurity.Pipe().GetSecurityDescriptorBinaryForm();
            IntPtr pSd = Marshal.AllocHGlobal(sd.Length);
            IntPtr pSa = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SecurityAttributes)));
            try
            {
                Marshal.Copy(sd, 0, pSd, sd.Length);
                SecurityAttributes sa = new SecurityAttributes();
                sa.Length = Marshal.SizeOf(typeof(SecurityAttributes));
                sa.Descriptor = pSd;
                sa.Inherit = 0;
                Marshal.StructureToPtr(sa, pSa, false);
                uint open = 0x3u | 0x40000000u | (first ? 0x00080000u : 0u);   // DUPLEX | OVERLAPPED | FIRST_PIPE_INSTANCE
                uint mode = 0x4u | 0x2u | 0x8u;                                  // TYPE_MESSAGE | READMODE_MESSAGE | REJECT_REMOTE_CLIENTS
                IntPtr h = CreateNamedPipe(fullName, open, mode, 16, 65536, 65536, 0, pSa);
                if (h == new IntPtr(-1))
                {
                    int err = Marshal.GetLastWin32Error();
                    IOException ex = new IOException("CreateNamedPipe failed (" + err + ")");
                    ex.Data["win32"] = err;
                    throw ex;
                }
                SafePipeHandle sh = new SafePipeHandle(h, true);
                try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, sh); }
                catch (Exception) { sh.Dispose(); throw; }
            }
            finally
            {
                Marshal.FreeHGlobal(pSa);
                Marshal.FreeHGlobal(pSd);
            }
        }

        // The peer is a process of this session (design 2).
        public static bool PeerInMySession(PipeStream p, bool peerIsServer, out uint pid)
        {
            pid = 0;
            try
            {
                IntPtr h = p.SafePipeHandle.DangerousGetHandle();
                bool ok = peerIsServer ? GetNamedPipeServerProcessId(h, out pid) : GetNamedPipeClientProcessId(h, out pid);
                uint theirs, mine;
                return ok && ProcessIdToSessionId(pid, out theirs) && ProcessIdToSessionId(GetCurrentProcessId(), out mine) && theirs == mine;
            }
            catch (Exception) { return false; }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, int length, out int needed);

        // The peer process runs as the current user (its token's user SID = ours). A peer that cannot be asked is refused.
        public static bool PeerOwnedByMe(PipeStream p, bool peerIsServer)
        {
            IntPtr proc = IntPtr.Zero, tok = IntPtr.Zero, buf = IntPtr.Zero;
            try
            {
                uint pid;
                IntPtr h = p.SafePipeHandle.DangerousGetHandle();
                if (!(peerIsServer ? GetNamedPipeServerProcessId(h, out pid) : GetNamedPipeClientProcessId(h, out pid))) return false;
                proc = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
                if (proc == IntPtr.Zero) return false;
                if (!OpenProcessToken(proc, 0x8, out tok)) return false;   // TOKEN_QUERY
                int need;
                GetTokenInformation(tok, 1, IntPtr.Zero, 0, out need);   // TokenUser
                if (need <= 0 || need > 4096) return false;
                buf = Marshal.AllocHGlobal(need);
                if (!GetTokenInformation(tok, 1, buf, need, out need)) return false;
                SecurityIdentifier theirs = new SecurityIdentifier(Marshal.ReadIntPtr(buf));
                using (WindowsIdentity me = WindowsIdentity.GetCurrent()) return theirs.Equals(me.User);
            }
            catch (Exception) { return false; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (tok != IntPtr.Zero) CloseHandle(tok);
                if (proc != IntPtr.Zero) CloseHandle(proc);
            }
        }

        // Abort pending IO and close (a blocked Read returns or throws).
        public static void Shut(PipeStream p)
        {
            if (p == null) return;
            try { CancelIoEx(p.SafePipeHandle.DangerousGetHandle(), IntPtr.Zero); }
            catch (Exception) { }
            try { p.Dispose(); }
            catch (Exception) { }
        }
    }

    // Message types, error codes.
    internal static class Msg
    {
        public const int Hello = 1, HelloAck = 2, Create = 3, CreateAck = 4, Destroy = 5, SetLook = 6, Resize = 7, Stats = 8, StatsReply = 9,
            Shown = 0x20, Hidden = 0x21, Appearance = 0x22, Failed = 0x23, Error = 0xFF;
        // Protocol 1.1 (a Hello that carried a minor; session minor >= 1 only): the in-process extras, and the light event.
        public const int SetMotionEx = 10, SetSlosh = 11, Kick = 12, SetButtons = 13, SetPointerArea = 14, SetUpdates = 15, SetBusy = 16, Cut = 17,
            LightMoved = 0x24;
    }

    internal static class WireError
    {
        public const int BadFrame = 1, BadLength = 2, BadVersion = 3, Unsupported = 4, BadPayload = 5, Quota = 6, Rate = 7, NoSurface = 8, Internal = 9, NoHello = 10;
    }

    // Control frame: magic 'LGE1' u32, version u16, type u16, length u32 (<= 64 KB), seq u32, payload. One frame is one pipe message.
    internal static class GlassFrame
    {
        public const uint Magic = 0x3145474C;
        public const int Version = 1, Minor = 1, HeaderSize = 16, MaxPayload = 65536, MaxFrame = HeaderSize + MaxPayload;   // (Minor: protocol 1.1, additive)

        public static int Get32(byte[] b, int o) { return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24); }

        static void Put32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        public static byte[] Build(int type, uint seq, byte[] payload)
        {
            int n = payload == null ? 0 : payload.Length;
            byte[] f = new byte[HeaderSize + n];
            Put32(f, 0, Magic);
            f[4] = Version;
            f[5] = 0;
            f[6] = (byte)type;
            f[7] = (byte)(type >> 8);
            Put32(f, 8, (uint)n);
            Put32(f, 12, seq);
            if (n > 0) Buffer.BlockCopy(payload, 0, f, HeaderSize, n);
            return f;
        }

        // 0 = a good frame of n bytes; else a WireError. type and seq are filled when the header was long enough (for the reply).
        public static int Check(byte[] m, int n, out int type, out uint seq, out int len)
        {
            type = 0;
            seq = 0;
            len = 0;
            if (n < HeaderSize) return WireError.BadFrame;
            seq = (uint)Get32(m, 12);
            if ((uint)Get32(m, 0) != Magic) return WireError.BadFrame;
            if ((m[4] | (m[5] << 8)) != Version) return WireError.BadVersion;
            type = m[6] | (m[7] << 8);
            uint l = (uint)Get32(m, 8);
            if (l > MaxPayload || l != (uint)(n - HeaderSize)) return WireError.BadLength;
            len = (int)l;
            return 0;
        }
    }

    internal sealed class PayloadWriter
    {
        readonly MemoryStream ms = new MemoryStream();

        public void U8(int v) { ms.WriteByte((byte)v); }
        public void U16(int v) { ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); }
        public void I32(int v) { ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 24)); }
        public void U32(uint v) { I32(unchecked((int)v)); }
        public void I64(long v) { I32(unchecked((int)v)); I32(unchecked((int)(v >> 32))); }
        public void F32(float f) { I32(BitConverter.ToInt32(BitConverter.GetBytes(f), 0)); }
        public void F64(double d) { I64(BitConverter.DoubleToInt64Bits(d)); }

        public void Str(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            int n = Math.Min(b.Length, 60000);
            U16(n);
            ms.Write(b, 0, n);
        }

        public void Raw(byte[] b) { ms.Write(b, 0, b.Length); }

        public byte[] ToArray() { return ms.ToArray(); }
    }

    // Reads a payload without ever running past it: any short read, bad length or bad UTF-8 sets Bad (and returns zeros).
    internal sealed class PayloadReader
    {
        static readonly UTF8Encoding Strict = new UTF8Encoding(false, true);
        readonly byte[] buf;
        readonly int end;
        int pos;
        public bool Bad;

        public PayloadReader(byte[] buffer, int start, int length)
        {
            buf = buffer;
            pos = start;
            end = start + length;
        }

        bool Need(int n)
        {
            if (Bad || n < 0 || end - pos < n) { Bad = true; return false; }
            return true;
        }

        public int Remaining { get { return end - pos; } }

        public byte[] Bytes(int n)
        {
            if (!Need(n)) return new byte[0];
            byte[] b = new byte[n];
            Buffer.BlockCopy(buf, pos, b, 0, n);
            pos += n;
            return b;
        }

        public int U8() { if (!Need(1)) return 0; return buf[pos++]; }
        public int U16() { if (!Need(2)) return 0; int v = buf[pos] | (buf[pos + 1] << 8); pos += 2; return v; }
        public int I32() { if (!Need(4)) return 0; int v = GlassFrame.Get32(buf, pos); pos += 4; return v; }
        public uint U32() { return unchecked((uint)I32()); }
        public long I64() { uint lo = U32(); uint hi = U32(); return (long)(((ulong)hi << 32) | lo); }
        public float F32() { return BitConverter.ToSingle(BitConverter.GetBytes(I32()), 0); }
        public double F64() { return BitConverter.Int64BitsToDouble(I64()); }

        public string Str(int maxBytes)
        {
            int n = U16();
            if (Bad) return "";
            if (n > maxBytes || !Need(n)) { Bad = true; return ""; }
            try
            {
                string s = Strict.GetString(buf, pos, n);
                pos += n;
                return s;
            }
            catch (ArgumentException) { Bad = true; return ""; }
        }
    }

    // The create / look messages (every value checked on the way in; a failure is the text of the Error reply).
    internal static class GlassWire
    {
        public const int MaxString = 256, MaxProfileChars = 2400, MaxProfileBytes = 9600;

        static bool In(int v, int lo, int hi) { return v >= lo && v <= hi; }

        // ---- the one validation, used by the client's setters / Create and by the host's readers (a value the host would reject is
        // an ArgumentException on the client, in every mode) ----

        public static bool ScaleOk(float scale) { return !float.IsNaN(scale) && !float.IsInfinity(scale) && (scale == 0f || (scale >= 0.01f && scale <= 8f)); }
        public static bool ElevationOk(int e) { return In(e, 0, 100); }
        public static bool PlaceOk(Point p) { return In(p.X, -100000, 100000) && In(p.Y, -100000, 100000); }
        public static bool LabelOk(string label) { return !string.IsNullOrEmpty(label) && Encoding.UTF8.GetByteCount(label) <= MaxString; }
        public static bool ProfileLengthOk(string profile) { return profile == null || profile.Length <= MaxProfileChars; }

        // null = fine; else the text of the ArgumentException.
        public static string CheckLookValues(GlassLook look, string profile)
        {
            if (!ElevationOk(look.Elevation)) return "look: elevation is not 0..100";
            if (!ProfileLengthOk(profile)) return "profile: over " + MaxProfileChars + " characters";
            return null;
        }

        public static string CheckSpecValues(GlassSpec s)
        {
            if (!ScaleOk(s.Scale)) return "spec: scale is not 0 (the monitor's) or 0.01..8";
            if (!LabelOk(s.Label)) return "spec: the label is empty or over " + MaxString + " UTF-8 bytes";
            if (!PlaceOk(s.Place)) return "spec: the place is outside +-100000";
            return CheckLookValues(s.Look, s.Profile);
        }

        public static void WriteLook(PayloadWriter w, GlassLook l)
        {
            w.I32(l.Dark);
            w.I32(l.Blur);
            w.I32(l.Refraction);
            w.I32(l.Rim);
            w.I32(l.Shadow);
            w.I32(l.Round);
            w.I32(l.Intensity);
            w.I32(l.Elevation);
        }

        public static bool ReadLook(PayloadReader r, out GlassLook l)
        {
            int dark = r.I32(), blur = r.I32(), refr = r.I32(), rim = r.I32(), shadow = r.I32(), round = r.I32(), inten = r.I32(), elev = r.I32();
            l = new GlassLook();
            if (r.Bad) return false;
            if (!In(dark, 0, 100) || !In(blur, 0, 100) || !In(refr, 0, 100) || !In(rim, 0, 100) || !In(shadow, 0, 100) || !In(round, 0, 28) || !In(inten, 0, 150) || !ElevationOk(elev)) return false;
            l = new GlassLook(dark, blur, refr, rim, shadow, round, inten, elev);
            return true;
        }

        public static bool ProfileValid(string profile)
        {
            return profile != null && profile.Length <= MaxProfileChars && GlassSurface.ProfileOk(profile);
        }

        // ---- protocol 1.1: SetButtons (13) ----

        public const int MaxButtons = 32;

        static bool RectOk(Rectangle r) { return In(r.Width, 0, 4096) && In(r.Height, 0, 4096) && In(r.X, -4096, 8192) && In(r.Y, -4096, 8192); }

        static Rectangle RectFix(Rectangle r)
        {
            return new Rectangle(Math.Max(-4096, Math.Min(8192, r.X)), Math.Max(-4096, Math.Min(8192, r.Y)), Math.Max(0, Math.Min(4096, r.Width)), Math.Max(0, Math.Min(4096, r.Height)));
        }

        static float Fix(float v, bool nonNegative)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return 0f;
            return Math.Max(nonNegative ? 0f : -16384f, Math.Min(16384f, v));
        }

        static bool FloatOk(float v, bool nonNegative) { return !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) <= 16384f && (!nonNegative || v >= 0f); }

        static void WriteRect(PayloadWriter w, Rectangle r)
        {
            w.I32(r.X);
            w.I32(r.Y);
            w.I32(r.Width);
            w.I32(r.Height);
        }

        static Rectangle ReadRect(PayloadReader r)
        {
            int x = r.I32(), y = r.I32(), w = r.I32(), h = r.I32();
            return new Rectangle(x, y, w, h);
        }

        // The SetButtons payload (values clamped to what the host accepts). A frame over 64 KB is sent again without the masks.
        public static byte[] Buttons(uint surface, GlassButton[] buttons, bool carry)
        {
            byte[] b = Buttons(surface, buttons, carry, true);
            return b.Length > GlassFrame.MaxPayload ? Buttons(surface, buttons, carry, false) : b;
        }

        static byte[] Buttons(uint surface, GlassButton[] buttons, bool carry, bool masks)
        {
            PayloadWriter w = new PayloadWriter();
            w.U32(surface);
            w.U8(carry ? 1 : 0);
            int n = buttons == null ? 0 : Math.Min(MaxButtons, buttons.Length);
            w.U16(n);
            for (int i = 0; i < n; i++)
            {
                GlassButton g = buttons[i];
                WriteRect(w, RectFix(g.Hit));
                w.F32(Fix(g.Cx, false));
                w.F32(Fix(g.Cy, false));
                w.F32(Fix(g.Hx, true));
                w.F32(Fix(g.Hy, true));
                w.F32(Fix(g.Rad, true));
                w.I32(g.Style);
                w.I32(g.Mark);
                Rectangle box = RectFix(g.MaskBox);
                WriteRect(w, box);
                if (masks && g.Mask != null && box.Width * box.Height > 0 && g.Mask.Length == box.Width * box.Height && box.Width == g.MaskBox.Width && box.Height == g.MaskBox.Height)
                {
                    w.U32((uint)g.Mask.Length);
                    w.Raw(g.Mask);
                }
                else w.U32(0);
            }
            return w.ToArray();
        }

        // null = fine (carry and the buttons are filled); else the text of the Error.
        public static string ReadButtons(PayloadReader r, out bool carry, out GlassButton[] buttons)
        {
            buttons = null;
            carry = r.U8() == 1;
            int count = r.U16();
            if (r.Bad) return "bad payload";
            if (count > MaxButtons) return "more than " + MaxButtons + " buttons";
            GlassButton[] a = new GlassButton[count];
            for (int i = 0; i < count; i++)
            {
                GlassButton g = new GlassButton();
                g.Hit = ReadRect(r);
                g.Cx = r.F32();
                g.Cy = r.F32();
                g.Hx = r.F32();
                g.Hy = r.F32();
                g.Rad = r.F32();
                g.Style = r.I32();
                g.Mark = r.I32();
                g.MaskBox = ReadRect(r);
                uint maskLen = r.U32();
                if (r.Bad) return "bad payload";
                if (!RectOk(g.Hit) || !RectOk(g.MaskBox) || !FloatOk(g.Cx, false) || !FloatOk(g.Cy, false) || !FloatOk(g.Hx, true) || !FloatOk(g.Hy, true) || !FloatOk(g.Rad, true)) return "a button value is out of range";
                if (maskLen != 0)
                {
                    if ((long)maskLen != (long)g.MaskBox.Width * g.MaskBox.Height) return "a mask does not fit its box";
                    g.Mask = r.Bytes((int)maskLen);
                    if (r.Bad) return "bad payload";
                }
                a[i] = g;
            }
            buttons = a;
            return null;
        }

        // The channel's retained spec with the latest props (size, place, look, profile, hover, motion).
        public static byte[] Create(uint cookie, GlassSpec s, SurfaceProps p)
        {
            PayloadWriter w = new PayloadWriter();
            w.U32(cookie);
            w.U8((int)s.Kind);
            w.F32(s.Scale);
            w.I32(p.Size.Width);
            w.I32(p.Size.Height);
            w.I32(p.Place.X);
            w.I32(p.Place.Y);
            w.U8((s.InShots ? 1 : 0) | (s.SavePower ? 2 : 0) | (s.Adapt ? 4 : 0) | (p.Hover ? 8 : 0));
            w.I32(s.Fps);
            w.I32(p.MotionStyle);
            w.I32(p.MotionIn);
            w.I32(p.MotionOut);
            w.I32(s.AnimZoom);
            w.I32(s.AnimLiquid);
            w.I32(s.AnimBounce);
            WriteLook(w, p.Look);
            w.Str(s.Label);
            w.Str(p.Profile);
            return w.ToArray();
        }

        // null = fine. Fills the spec and the first props (version 1).
        public static string ReadCreate(PayloadReader r, out uint cookie, out GlassSpec s, out SurfaceProps p)
        {
            s = new GlassSpec();
            p = new SurfaceProps();
            cookie = r.U32();
            int kind = r.U8();
            float scale = r.F32();
            int w = r.I32(), h = r.I32(), x = r.I32(), y = r.I32();
            int flags = r.U8();
            int fps = r.I32(), style = r.I32(), aIn = r.I32(), aOut = r.I32(), zoom = r.I32(), liquid = r.I32(), bounce = r.I32();
            GlassLook look;
            bool lookOk = ReadLook(r, out look);
            string label = r.Str(MaxString);
            string profile = r.Str(MaxProfileBytes);
            if (r.Bad) return "create: the frame is short or its text is not valid UTF-8";
            if (kind != 0 && kind != 1) return "create: unknown kind " + kind;
            if (!ScaleOk(scale)) return "create: scale is not 0 or 0.01..8";
            if (!In(w, 1, 4096) || !In(h, 1, 4096)) return "create: size " + w + "x" + h + " is not 1..4096";
            if (!PlaceOk(new Point(x, y))) return "create: place is out of range";
            if (!In(fps, 0, 60) || !In(style, 0, 8) || !In(aIn, 60, 1200) || !In(aOut, 60, 1200) || !In(zoom, 0, 50) || !In(liquid, 0, 100) || !In(bounce, 0, 100)) return "create: a motion number is out of range";
            if (!lookOk) return "create: a look slider is out of range";
            if (!LabelOk(label)) return "create: the label is empty or over " + MaxString + " bytes";
            if (!ProfileValid(profile)) return "create: profile is not a GlassProfile string or a preset's name (or over " + MaxProfileChars + " characters)";
            s.Kind = kind == 0 ? GlassKind.Card : GlassKind.Lens;
            s.Scale = scale;
            s.Size = new Size(w, h);
            s.Place = new Point(x, y);
            s.InShots = (flags & 1) != 0;
            s.SavePower = (flags & 2) != 0;
            s.Adapt = (flags & 4) != 0;
            s.Hover = (flags & 8) != 0;
            s.Fps = fps;
            s.AnimStyle = style;
            s.AnimIn = aIn;
            s.AnimOut = aOut;
            s.AnimZoom = zoom;
            s.AnimLiquid = liquid;
            s.AnimBounce = bounce;
            s.Look = look;
            s.Profile = profile;
            s.Label = label;
            p.Version = 1;
            p.Place = s.Place;
            p.Size = s.Size;
            p.Look = look;
            p.Profile = profile;
            p.Hover = s.Hover;
            p.MotionStyle = style;
            p.MotionIn = aIn;
            p.MotionOut = aOut;
            return null;
        }
    }

    // Writes frames to a pipe one at a time without ever blocking the caller: a queue and a chain of BeginWrite calls (no thread of
    // its own). A queue over its limit, or a failed write, kills it: the pipe is then abandoned by its owner.
    internal sealed class FrameQueue
    {
        const int Limit = 1024;
        readonly PipeStream pipe;
        readonly Queue<byte[]> queue = new Queue<byte[]>();
        readonly Action onDead;
        bool busy, dead;

        public FrameQueue(PipeStream pipe, Action onDead)
        {
            this.pipe = pipe;
            this.onDead = onDead;
        }

        public bool Dead { get { lock (queue) return dead; } }
        public bool Idle { get { lock (queue) return queue.Count == 0 && !busy; } }

        public bool Send(byte[] frame)
        {
            bool kill = false;
            lock (queue)
            {
                if (dead) return false;
                if (queue.Count >= Limit) { dead = true; kill = true; }
                else
                {
                    queue.Enqueue(frame);
                    if (!busy)
                    {
                        busy = true;
                        Next();
                        if (dead) kill = true;
                    }
                }
            }
            if (kill) Kill();
            return !kill;
        }

        // (queue held)
        void Next()
        {
            try { pipe.BeginWrite(queue.Peek(), 0, queue.Peek().Length, Done, null); }
            catch (Exception) { dead = true; busy = false; }
        }

        void Done(IAsyncResult ar)
        {
            bool kill = false;
            lock (queue)
            {
                try
                {
                    pipe.EndWrite(ar);
                    queue.Dequeue();
                    if (queue.Count > 0) Next(); else busy = false;
                    if (dead) kill = true;
                }
                catch (Exception) { dead = true; busy = false; kill = true; }
            }
            if (kill) Kill();
        }

        void Kill()
        {
            try { if (onDead != null) onDead(); }
            catch (Exception) { }
        }
    }

    // What the loopback test (another assembly) needs to talk to a host by hand: build frames, read a reply's type.
    public static class GlassProtocolTest
    {
        public const int TypeHello = Msg.Hello, TypeCreate = Msg.Create, TypeCreateAck = Msg.CreateAck, TypeError = Msg.Error, TypeHelloAck = Msg.HelloAck;

        public static byte[] Frame(int type, uint seq, byte[] payload) { return GlassFrame.Build(type, seq, payload); }

        public static byte[] HelloPayload(string name)
        {
            PayloadWriter w = new PayloadWriter();
            w.U16(1);
            w.U16(1);
            w.Str(name);
            return w.ToArray();
        }

        // A Hello that carries the client's protocol minor (protocol 1.1).
        public static byte[] HelloPayload(string name, int minor)
        {
            PayloadWriter w = new PayloadWriter();
            w.U16(1);
            w.U16(1);
            w.Str(name);
            w.U16(minor);
            return w.ToArray();
        }

        // The host's minor in a HelloAck (-1: the ack carried none, a v1 host or a Hello without a minor).
        public static int AckMinor(byte[] buf, int n)
        {
            if (ReplyType(buf, n) != Msg.HelloAck) return -1;
            PayloadReader r = new PayloadReader(buf, GlassFrame.HeaderSize, n - GlassFrame.HeaderSize);
            r.U16();
            r.Str(256);
            if (r.Remaining < 2) return -1;
            int m = r.U16();
            return r.Bad ? -1 : m;
        }

        public const int TypeSetMotionEx = Msg.SetMotionEx, TypeSetSlosh = Msg.SetSlosh, TypeKick = Msg.Kick, TypeSetButtons = Msg.SetButtons, TypeSetPointerArea = Msg.SetPointerArea,
            TypeSetUpdates = Msg.SetUpdates, TypeSetBusy = Msg.SetBusy, TypeCut = Msg.Cut, TypeLightMoved = Msg.LightMoved;

        public static byte[] SetMotionExPayload(uint surface, int zoom, int liquid, int bounce, int travel, int indicator)
        {
            PayloadWriter w = new PayloadWriter();
            w.U32(surface);
            w.I32(zoom);
            w.I32(liquid);
            w.I32(bounce);
            w.I32(travel);
            w.I32(indicator);
            return w.ToArray();
        }

        public static byte[] SetSloshPayload(uint surface, int style, int amount)
        {
            PayloadWriter w = new PayloadWriter();
            w.U32(surface);
            w.I32(style);
            w.I32(amount);
            return w.ToArray();
        }

        public static byte[] KickPayload(uint surface, float push)
        {
            PayloadWriter w = new PayloadWriter();
            w.U32(surface);
            w.F32(push);
            return w.ToArray();
        }

        // count buttons (hit boxes side by side), the first with a mask when withMask.
        public static byte[] SetButtonsPayload(uint surface, bool carry, int count, bool withMask)
        {
            GlassButton[] b = new GlassButton[count];
            for (int i = 0; i < count; i++)
            {
                b[i] = new GlassButton();
                b[i].Hit = new Rectangle(10 + i * 30, 10, 28, 20);
                b[i].Cx = 24f + i * 30;
                b[i].Cy = 20f;
                b[i].Hx = 14f;
                b[i].Hy = 10f;
                b[i].Rad = 8f;
                b[i].Style = 1 + i;
                b[i].Mark = 2;
                b[i].MaskBox = new Rectangle(10 + i * 30, 10, 4, 3);
                if (withMask && i == 0) b[i].Mask = new byte[12];
            }
            return GlassWire.Buttons(surface, b, carry);
        }

        public static byte[] SetPointerAreaPayload(uint surface, int x, int y, int w, int h)
        {
            PayloadWriter pw = new PayloadWriter();
            pw.U32(surface);
            pw.I32(x);
            pw.I32(y);
            pw.I32(w);
            pw.I32(h);
            return pw.ToArray();
        }

        public static byte[] SetUpdatesPayload(uint surface, int fps, int adapt, int savePower)
        {
            PayloadWriter w = new PayloadWriter();
            w.U32(surface);
            w.I32(fps);
            w.U8(adapt);
            w.U8(savePower);
            return w.ToArray();
        }

        public static byte[] SetBusyPayload(uint surface, int busy)
        {
            PayloadWriter w = new PayloadWriter();
            w.U32(surface);
            w.U8(busy);
            return w.ToArray();
        }

        public static byte[] CutPayload(uint surface)
        {
            PayloadWriter w = new PayloadWriter();
            w.U32(surface);
            return w.ToArray();
        }

        // The light in a LightMoved frame (NaN: not one).
        public static float LightOf(byte[] buf, int n)
        {
            if (ReplyType(buf, n) != Msg.LightMoved) return float.NaN;
            PayloadReader r = new PayloadReader(buf, GlassFrame.HeaderSize, n - GlassFrame.HeaderSize);
            r.U32();
            float v = r.F32();
            return r.Bad ? float.NaN : v;
        }

        public static byte[] CreatePayload(uint cookie, string label, int width, int height, string profile)
        {
            GlassSpec s = new GlassSpec();
            s.Label = label;
            s.Size = new Size(width, height);
            s.Place = new Point(-10000, -10000);
            s.Scale = 1f;
            SurfaceProps p = new SurfaceProps();
            p.Size = s.Size;
            p.Place = s.Place;
            p.Look = s.Look;
            p.Profile = profile ?? "";
            p.MotionStyle = s.AnimStyle;
            p.MotionIn = s.AnimIn;
            p.MotionOut = s.AnimOut;
            return GlassWire.Create(cookie, s, p);
        }

        public const int TypeSetLook = Msg.SetLook, TypeResize = Msg.Resize, TypeShown = Msg.Shown, TypeStats = Msg.Stats, TypeStatsReply = Msg.StatsReply;

        // A SetLook payload whose sliders all derive from seed (any seed gives valid values).
        public static byte[] SetLookPayload(uint surface, int seed)
        {
            PayloadWriter w = new PayloadWriter();
            w.U32(surface);
            GlassWire.WriteLook(w, LookFor(seed));
            w.Str("");
            return w.ToArray();
        }

        public static GlassLook LookFor(int seed)
        {
            return new GlassLook(seed % 101, (seed * 7) % 101, (seed * 3) % 101, (seed * 5) % 101, (seed * 11) % 101, seed % 29, seed % 151, (seed * 13) % 101);
        }

        public static byte[] ResizePayload(uint surface, int width, int height) { return ResizePayload(surface, width, height, 0); }

        // flags: bit0 = flow (honoured by a host speaking protocol 1.1 to a client that said so).
        public static byte[] ResizePayload(uint surface, int width, int height, uint flags)
        {
            PayloadWriter w = new PayloadWriter();
            w.U32(surface);
            w.I32(width);
            w.I32(height);
            w.U32(flags);
            return w.ToArray();
        }

        // The surface id in a CreateAck frame (0 = not one).
        public static uint AckSurface(byte[] buf, int n)
        {
            return ReplyType(buf, n) == Msg.CreateAck && n >= GlassFrame.HeaderSize + 8 ? (uint)GlassFrame.Get32(buf, GlassFrame.HeaderSize + 4) : 0;
        }

        // What a client does after a CreateAck: maps the section, asks to be shown and publishes a picture, sets HostWake.
        public static bool ShowFromAck(byte[] buf, int n, string wakeName)
        {
            try
            {
                if (ReplyType(buf, n) != Msg.CreateAck) return false;
                PayloadReader r = new PayloadReader(buf, GlassFrame.HeaderSize, n - GlassFrame.HeaderSize);
                r.U32();
                r.U32();
                string name = r.Str(256);
                int bytes = r.I32(), w = r.I32(), h = r.I32();
                if (r.Bad) return false;
                using (GlassSection s = GlassSection.Open(name, w, h, bytes))
                {
                    SurfaceProps p = new SurfaceProps();
                    p.Version = 1;
                    p.WantShown = true;
                    p.Size = new Size(w, h);
                    p.Place = new Point(-10000, -10000);
                    p.MotionStyle = 4;
                    p.MotionIn = 380;
                    p.MotionOut = 230;
                    s.WriteState(p);
                    s.PublishContents(new int[w * h], new Rectangle(0, 0, w, h));
                }
                using (EventWaitHandle e = EventWaitHandle.OpenExisting(wakeName, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize)) e.Set();
                return true;
            }
            catch (Exception) { return false; }
        }

        // The type of a reply, or -1 if it is not a good frame.
        public static int ReplyType(byte[] buf, int n)
        {
            int type, len;
            uint seq;
            return GlassFrame.Check(buf, n, out type, out seq, out len) == 0 ? type : -1;
        }
    }
}
