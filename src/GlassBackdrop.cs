using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LGEngine
{
    // The picture of what is on screen behind a glass card, at the sharpnesses its map asks for:
    // as read (Sharp, one texel per SharpFactor pixels), and the two made from that which a frame
    // is actually drawn with: softened fully for the frosted face (Soft), and less so for the bent
    // view in the rim (Edge). Each new picture is compared with the last, and only the part that
    // differs is shrunk and softened again (and the map is told what changed: see Changed*).
    public sealed class GlassBackdrop : IDisposable
    {
        // A change this small (a caret, a spinner, a clock digit) is not worth a redraw of its own
        // behind frosted glass: it is picked up at most this often instead.
        const int SmallChangeHoldMs = 1500;

        Dib dib, full;
        int[] fresh, raw, tmp, work, edgeRaw, edgeTmp, edgeWork, fullPx, columnAt;
        bool have;
        int smallSince;

        public int[] Sharp { get; private set; }
        public int[] Edge { get; private set; }
        public int[] Soft { get; private set; }
        public bool HasPicture { get { return have; } }

        // Goes up whenever Soft and Edge change (so their colouring can be kept until they do).
        public int Version { get; private set; }

        // What the latest picture changed since the one before (Version ChangeFrom; -1 when there
        // was none, or it was all different): in texels of Sharp, of Soft and of Edge, the last two
        // taking in everything the softening spreads the change to. Partial: a part only.
        public Rectangle ChangedSharp { get; private set; }
        public Rectangle ChangedSoft { get; private set; }
        public Rectangle ChangedEdge { get; private set; }
        public int ChangeFrom { get; private set; }
        public bool Partial { get; private set; }

        public GlassBackdrop()
        {
            ChangeFrom = -1;
        }

        // How bright what is behind the glass is, on average (0-255).
        public int Luma()
        {
            int[] soft = Soft;
            if (lumaVersion == Version || soft == null || soft.Length == 0) return luma;
            long sum = 0;
            for (int i = 0; i < soft.Length; i++)
            {
                int p = soft[i];
                sum += ((p >> 16) & 255) * 299 + ((p >> 8) & 255) * 587 + (p & 255) * 114;
            }
            luma = (int)(sum / (soft.Length * 1000L));
            lumaVersion = Version;
            return luma;
        }
        int luma, lumaVersion = -1;

        // What the glass needs to know about the picture behind a card (worked out once for each
        // picture, from the face's soft picture, and only for the part behind the card itself, not
        // the padding round it): how bright it is behind the card's middle, where the contents are
        // (MidLuma, 0-255); how uneven it is across the whole card (Spread: the standard deviation of
        // its brightness, so ~0 for a plain wall, ~110 for half black and half white); and its average
        // colour (AvgR, AvgG, AvgB).
        public int MidLuma, Spread, AvgR = 128, AvgG = 128, AvgB = 128;
        int measuredVersion = -1;
        GlassMap measuredFor;

        public void Measure(GlassMap map)
        {
            int[] soft = Soft;
            if (soft == null || map == null || soft.Length != map.BackW * map.BackH || soft.Length == 0) return;
            if (measuredVersion == Version && measuredFor == map) return;
            int w = map.BackW, h = map.BackH, f = Math.Max(1, map.Factor);
            int x0 = Math.Max(0, Math.Min(w - 1, map.Pad / f)), x1 = Math.Max(x0 + 1, Math.Min(w, (map.Pad + map.CardW + f - 1) / f));
            int y0 = Math.Max(0, Math.Min(h - 1, map.Pad / f)), y1 = Math.Max(y0 + 1, Math.Min(h, (map.Pad + map.CardH + f - 1) / f));
            // the middle: the card less just under a fifth of its width at each side and a fifth of its height above and below
            int mx0 = x0 + (x1 - x0) * 18 / 100, mx1 = x1 - (x1 - x0) * 18 / 100, my0 = y0 + (y1 - y0) * 20 / 100, my1 = y1 - (y1 - y0) * 20 / 100;
            if (mx1 <= mx0) { mx0 = x0; mx1 = x1; }
            if (my1 <= my0) { my0 = y0; my1 = y1; }
            long sumR = 0, sumG = 0, sumB = 0, sumL = 0, sumLL = 0, midL = 0;
            int count = 0, midCount = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * w;
                bool midRow = y >= my0 && y < my1;
                for (int x = x0; x < x1; x++)
                {
                    int p = soft[row + x], r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
                    int l = (r * 299 + g * 587 + b * 114) / 1000;
                    sumR += r; sumG += g; sumB += b; sumL += l; sumLL += l * l;
                    count++;
                    if (midRow && x >= mx0 && x < mx1) { midL += l; midCount++; }
                }
            }
            if (count == 0) return;
            double mean = sumL / (double)count, variance = sumLL / (double)count - mean * mean;
            Spread = (int)Math.Sqrt(Math.Max(0.0, variance) + 0.5);
            MidLuma = midCount > 0 ? (int)(midL / midCount) : (int)mean;
            AvgR = (int)(sumR / count);
            AvgG = (int)(sumG / count);
            AvgB = (int)(sumB / count);
            measuredVersion = Version;
            measuredFor = map;
        }

        public void Forget()
        {
            have = false;
            smallSince = 0;
            Version++;
            ChangeFrom = -1;
            Partial = false;
        }

        // The part of the screen a card at cardLeft, cardTop needs a picture of.
        public static Rectangle AreaFor(GlassMap map, int cardLeft, int cardTop)
        {
            return new Rectangle(cardLeft - map.Pad, cardTop - map.Pad, map.SharpW * map.SharpFactor, map.SharpH * map.SharpFactor);
        }

        // Where the next picture goes: SharpW x SharpH texels, each the average (or one) of
        // SharpFactor x SharpFactor screen pixels of AreaFor. Follow with Accept.
        public int[] Buffer(GlassMap map)
        {
            if (Sharp == null || Sharp.Length != map.SharpW * map.SharpH || Soft == null || Soft.Length != map.BackW * map.BackH
                || Edge == null || Edge.Length != map.EdgeW * map.EdgeH)
            {
                Sharp = new int[map.SharpW * map.SharpH];
                Edge = new int[map.EdgeW * map.EdgeH];
                edgeRaw = new int[Edge.Length];
                edgeTmp = new int[Edge.Length];
                edgeWork = new int[Edge.Length];
                fresh = new int[Sharp.Length];
                Soft = new int[map.BackW * map.BackH];
                raw = new int[Soft.Length];
                tmp = new int[Soft.Length];
                work = new int[Soft.Length];
                Forget();
            }
            return fresh;
        }

        // The next picture from a picture of the screen held as int[] (screenW x screenH, 0xRRGGBB):
        // what the live supply would give of that screen (every texel the average of its pixels,
        // anything off the picture a repeat of its nearest pixels), taken in with Accept.
        public bool ReadPicture(GlassMap map, int[] screen, int screenW, int screenH, int cardLeft, int cardTop)
        {
            int[] into = Buffer(map);
            Rectangle area = AreaFor(map, cardLeft, cardTop);
            int step = map.SharpFactor, n = step * step;
            if (columnAt == null || columnAt.Length < area.Width) columnAt = new int[area.Width];
            for (int x = 0; x < area.Width; x++) columnAt[x] = Math.Max(0, Math.Min(screenW - 1, area.Left + x));
            for (int ty = 0; ty < map.SharpH; ty++)
            {
                int at = ty * map.SharpW;
                for (int tx = 0; tx < map.SharpW; tx++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int v = 0; v < step; v++)
                    {
                        int row = Math.Max(0, Math.Min(screenH - 1, area.Top + ty * step + v)) * screenW;
                        for (int u = 0; u < step; u++)
                        {
                            int p = screen[row + columnAt[tx * step + u]];
                            r += (p >> 16) & 255;
                            g += (p >> 8) & 255;
                            b += p & 255;
                        }
                    }
                    into[at + tx] = ((r / n) << 16) | ((g / n) << 8) | (b / n);
                }
            }
            return Accept(map, false);
        }

        // The same as Read, but giving exactly the picture the live supply (ScreenFeed) would give of
        // the same screen: every texel the average of its pixels, and anything off the display a
        // repeat of the nearest pixels on it. Used for the first picture when live ones are to
        // follow, so that the first of those does not make the glass visibly "settle".
        public bool ReadExact(GlassMap map, int cardLeft, int cardTop)
        {
            int[] into = Buffer(map);
            Rectangle area = AreaFor(map, cardLeft, cardTop);
            int step = map.SharpFactor, w = area.Width, h = area.Height;
            if (full == null || full.Width != w || full.Height != h)
            {
                if (full != null) full.Dispose();
                full = null;
                full = new Dib(w, h);
                fullPx = new int[w * h];
            }
            IntPtr screen = Gdi.GetDC(IntPtr.Zero);
            try
            {
                Gdi.BitBlt(full.Dc, 0, 0, w, h, screen, area.Left, area.Top, Gdi.SRCCOPY);
            }
            finally
            {
                Gdi.ReleaseDC(IntPtr.Zero, screen);
            }
            full.Read(fullPx);

            Rectangle display = Gdi.DisplayAt(area.Left + w / 2, area.Top + h / 2);
            Rectangle held = Rectangle.Intersect(area, display);
            if (held.Width <= 0 || held.Height <= 0) held = area;
            if (columnAt == null || columnAt.Length < w) columnAt = new int[w];
            for (int x = 0; x < w; x++) columnAt[x] = Math.Max(held.Left, Math.Min(held.Right - 1, area.Left + x)) - area.Left;
            int n = step * step;
            for (int ty = 0; ty < map.SharpH; ty++)
            {
                int at = ty * map.SharpW;
                if (step == 2)
                {
                    int upper = (Math.Max(held.Top, Math.Min(held.Bottom - 1, area.Top + ty * 2)) - area.Top) * w;
                    int lower = (Math.Max(held.Top, Math.Min(held.Bottom - 1, area.Top + ty * 2 + 1)) - area.Top) * w;
                    for (int tx = 0; tx < map.SharpW; tx++)
                    {
                        int a = columnAt[tx * 2], b = columnAt[tx * 2 + 1];
                        int p0 = fullPx[upper + a], p1 = fullPx[upper + b], p2 = fullPx[lower + a], p3 = fullPx[lower + b];
                        int rb = (((p0 & 0xFF00FF) + (p1 & 0xFF00FF) + (p2 & 0xFF00FF) + (p3 & 0xFF00FF)) >> 2) & 0xFF00FF;
                        int g = (((p0 & 0x00FF00) + (p1 & 0x00FF00) + (p2 & 0x00FF00) + (p3 & 0x00FF00)) >> 2) & 0x00FF00;
                        into[at + tx] = rb | g;
                    }
                    continue;
                }
                for (int tx = 0; tx < map.SharpW; tx++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int v = 0; v < step; v++)
                    {
                        int row = (Math.Max(held.Top, Math.Min(held.Bottom - 1, area.Top + ty * step + v)) - area.Top) * w;
                        for (int u = 0; u < step; u++)
                        {
                            int p = fullPx[row + columnAt[tx * step + u]];
                            r += (p >> 16) & 255;
                            g += (p >> 8) & 255;
                            b += p & 255;
                        }
                    }
                    into[at + tx] = ((r / n) << 16) | ((g / n) << 8) | (b / n);
                }
            }
            return Accept(map, false);
        }

        // cardLeft, cardTop: where the card is on screen. Reads that part of the screen the ordinary
        // way. Returns true when there is a new picture worth redrawing for.
        public bool Read(GlassMap map, int cardLeft, int cardTop)
        {
            Buffer(map);
            if (dib == null || dib.Width != map.SharpW || dib.Height != map.SharpH)
            {
                if (dib != null) dib.Dispose();
                dib = new Dib(map.SharpW, map.SharpH);
            }

            int step = map.SharpFactor, left = cardLeft - map.Pad, top = cardTop - map.Pad;
            IntPtr screen = Gdi.GetDC(IntPtr.Zero);
            try
            {
                if (step == 1)
                {
                    Gdi.BitBlt(dib.Dc, 0, 0, map.SharpW, map.SharpH, screen, left, top, Gdi.SRCCOPY);
                }
                else
                {
                    // The quick shrink: reading the screen is the costly step, and this one uses a quarter of
                    // the processor time of the smooth shrink. The soft picture is smoothed afterwards anyway.
                    Gdi.SetStretchBltMode(dib.Dc, Gdi.COLORONCOLOR);
                    Gdi.StretchBlt(dib.Dc, 0, 0, map.SharpW, map.SharpH, screen, left, top, map.SharpW * step, map.SharpH * step, Gdi.SRCCOPY);
                }
            }
            finally
            {
                Gdi.ReleaseDC(IntPtr.Zero, screen);
            }
            dib.Read(fresh);
            return Accept(map, true);
        }

        // Takes the picture just written into Buffer(). Returns true when it differs enough from
        // the one in use to be worth redrawing for; it is then the one in use.
        // holdSmall: let a very small change wait a while (see SmallChangeHoldMs).
        public bool Accept(GlassMap map, bool holdSmall)
        {
            Rectangle box = new Rectangle(0, 0, map.SharpW, map.SharpH);
            bool partial = false;
            if (have)
            {
                int limit = Math.Max(6, fresh.Length / 400);   // a quarter of a percent of the picture
                int moved = Compare(fresh, Sharp, map.SharpW, map.SharpH, limit, out box);
                if (box.IsEmpty)
                {
                    smallSince = 0;
                    return false;
                }
                if (moved <= limit && holdSmall)
                {
                    int now = Environment.TickCount;
                    if (smallSince == 0) smallSince = now == 0 ? 1 : now;
                    if (unchecked(now - smallSince) < SmallChangeHoldMs) return false;
                }
                partial = box.Width < map.SharpW || box.Height < map.SharpH;
            }
            smallSince = 0;
            ChangeFrom = have ? Version : -1;
            Partial = have && partial;
            have = true;
            Version = (Version + 1) & 0x3FFFFFFF;
            int[] swap = Sharp;
            Sharp = fresh;
            fresh = swap;
            ChangedSharp = box;

            // The rim's picture: shrunk and smoothed a little, where it changed.
            Rectangle edgeBox = Shrunk(box, map.EdgeRatio, map.EdgeW, map.EdgeH);
            int[] small = Sharp;
            if (map.EdgeRatio > 1)
            {
                Shrink(Sharp, map.SharpW, map.EdgeRatio, edgeRaw, map.EdgeW, map.EdgeH, edgeBox);
                small = edgeRaw;
            }
            SmoothPart(small, edgeTmp, edgeWork, Edge, map.EdgeW, map.EdgeH, edgeBox, map.EdgePasses);
            ChangedEdge = Grow(edgeBox, map.EdgePasses, map.EdgeW, map.EdgeH);

            // The face's: shrunk further, and smoothed more.
            Rectangle softBox = Shrunk(box, map.Ratio, map.BackW, map.BackH);
            small = Sharp;
            if (map.Ratio > 1)
            {
                Shrink(Sharp, map.SharpW, map.Ratio, raw, map.BackW, map.BackH, softBox);
                small = raw;
            }
            SmoothPart(small, tmp, work, Soft, map.BackW, map.BackH, softBox, map.SoftPasses);
            ChangedSoft = Grow(softBox, map.SoftPasses, map.BackW, map.BackH);
            return true;
        }

        // The texels of a picture shrunk by ratio that a box of the sharp picture's texels falls in.
        static Rectangle Shrunk(Rectangle box, int ratio, int w, int h)
        {
            if (ratio <= 1) return Rectangle.Intersect(box, new Rectangle(0, 0, w, h));
            return Rectangle.Intersect(Rectangle.FromLTRB(box.Left / ratio, box.Top / ratio, (box.Right + ratio - 1) / ratio, (box.Bottom + ratio - 1) / ratio), new Rectangle(0, 0, w, h));
        }

        static Rectangle Grow(Rectangle r, int by, int w, int h)
        {
            return Rectangle.Intersect(Rectangle.FromLTRB(r.Left - by, r.Top - by, r.Right + by, r.Bottom + by), new Rectangle(0, 0, w, h));
        }

        // dst = src smoothed n times (see Smooth), exactly as if the whole picture were, but only where a change
        // within box reaches (box grown by n). Pass i is worked out over box grown by 2n - i in work (each pass
        // needs the one before a texel further out), so nothing of the previous picture's passes leaks in.
        static void SmoothPart(int[] src, int[] tmp, int[] work, int[] dst, int w, int h, Rectangle box, int n)
        {
            if (box.IsEmpty) return;
            if (n == 0) { CopyRect(src, dst, w, box); return; }
            for (int i = 1; i < n; i++) Smooth(i == 1 ? src : work, tmp, work, w, h, Grow(box, 2 * n - i, w, h));
            Smooth(n == 1 ? src : work, tmp, dst, w, h, Grow(box, n, w, h));
        }

        static void CopyRect(int[] src, int[] dst, int w, Rectangle r)
        {
            for (int y = r.Top; y < r.Bottom; y++) Array.Copy(src, y * w + r.Left, dst, y * w + r.Left, r.Width);
        }

        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int memcmp(int[] a, int[] b, UIntPtr count);

        // Whether two pictures are exactly the same (for a screen-sized window, a few times faster
        // than comparing in a loop here).
        public static bool Same(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            if (a.Length == 0) return true;
            try
            {
                return memcmp(a, b, new UIntPtr((ulong)a.Length * 4UL)) == 0;
            }
            catch (Exception)
            {
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
                return true;
            }
        }

        // How many texels differ noticeably between two pictures (stops counting just past
        // 'limit'), and the box of all those that differ at all (empty if none).
        static int Compare(int[] a, int[] b, int w, int h, int limit, out Rectangle box)
        {
            box = Rectangle.Empty;
            if (Same(a, b)) return 0;   // (most pictures from the live feed are the last one again: a few times quicker than the loop)
            int count = 0, x0 = w, x1 = -1, y0 = h, y1 = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w, first = -1, last = -1;
                for (int x = 0; x < w; x++)
                {
                    int p = a[row + x], q = b[row + x];
                    if (p == q) continue;
                    if (first < 0) first = x;
                    last = x;
                    if (count <= limit)
                    {
                        int dr = ((p >> 16) & 255) - ((q >> 16) & 255), dg = ((p >> 8) & 255) - ((q >> 8) & 255), db = (p & 255) - (q & 255);
                        if (dr < 0) dr = -dr;
                        if (dg < 0) dg = -dg;
                        if (db < 0) db = -db;
                        if (dr + dg + db > 30) count++;
                    }
                }
                if (first < 0) continue;
                if (y < y0) y0 = y;
                y1 = y;
                if (first < x0) x0 = first;
                if (last > x1) x1 = last;
            }
            if (y1 >= y0) box = Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
            return count;
        }

        // Averages ratio x ratio blocks, for the texels of dst within r. (Red and blue are summed
        // together in one int, green in another: a channel's sum is at most 255 x ratio x ratio,
        // which fits in its sixteen bits for any ratio up to 16.)
        static void Shrink(int[] src, int srcW, int ratio, int[] dst, int w, int h, Rectangle r)
        {
            int area = ratio * ratio;
            for (int y = r.Top; y < r.Bottom; y++)
            {
                int rowStart = y * ratio * srcW, o = y * w;
                for (int x = r.Left; x < r.Right; x++)
                {
                    uint rb = 0, g = 0;
                    int start = rowStart + x * ratio;
                    for (int v = 0; v < ratio; v++)
                    {
                        int at = start + v * srcW;
                        for (int u = 0; u < ratio; u++)
                        {
                            uint p = (uint)src[at + u];
                            rb += p & 0xFF00FF;
                            g += p & 0x00FF00;
                        }
                    }
                    dst[o + x] = (((int)(rb >> 16) / area) << 16) | (((int)(g >> 8) / area) << 8) | ((int)(rb & 0xFFFF) / area);
                }
            }
        }

        // A light 1-2-1 blur in both directions, to hide the blockiness of the shrunken picture: the
        // texels of dst within r (tmp is scratch). (Red and blue are blended together in one int and
        // green in another: 1 + 2 + 1 of a channel, and the half added to round, is at most 1022,
        // well within the sixteen bits each has.)
        static void Smooth(int[] src, int[] tmp, int[] dst, int w, int h, Rectangle r)
        {
            if (r.IsEmpty) return;
            int ya = Math.Max(0, r.Top - 1), yb = Math.Min(h, r.Bottom + 1), xa = r.Left, xb = r.Right;   // (the pass down reads only r's columns)
            for (int y = ya; y < yb; y++)
            {
                int row = y * w;
                for (int x = xa; x < xb; x++)
                {
                    uint a = (uint)src[row + (x > 0 ? x - 1 : x)], b = (uint)src[row + x], c = (uint)src[row + (x < w - 1 ? x + 1 : x)];
                    uint rb = (((a & 0xFF00FF) + 2 * (b & 0xFF00FF) + (c & 0xFF00FF) + 0x020002) >> 2) & 0xFF00FF;
                    uint g = (((a & 0x00FF00) + 2 * (b & 0x00FF00) + (c & 0x00FF00) + 0x000200) >> 2) & 0x00FF00;
                    tmp[row + x] = (int)(rb | g);
                }
            }
            for (int y = r.Top; y < r.Bottom; y++)
            {
                int up = (y > 0 ? y - 1 : y) * w, row = y * w, down = (y < h - 1 ? y + 1 : y) * w;
                for (int x = r.Left; x < r.Right; x++)
                {
                    uint a = (uint)tmp[up + x], b = (uint)tmp[row + x], c = (uint)tmp[down + x];
                    uint rb = (((a & 0xFF00FF) + 2 * (b & 0xFF00FF) + (c & 0xFF00FF) + 0x020002) >> 2) & 0xFF00FF;
                    uint g = (((a & 0x00FF00) + 2 * (b & 0x00FF00) + (c & 0x00FF00) + 0x000200) >> 2) & 0x00FF00;
                    dst[row + x] = (int)(rb | g);
                }
            }
        }

        public void Release()
        {
            if (dib != null) dib.Dispose();
            if (full != null) full.Dispose();
            dib = full = null;
            Sharp = Edge = Soft = fresh = raw = tmp = work = edgeRaw = edgeTmp = edgeWork = fullPx = columnAt = null;
            Forget();
        }

        public void Dispose() { Release(); }
    }
}
