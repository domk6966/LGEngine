using System;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace LGEngine
{
    // How a piece of glass is drawn this frame, besides where: its size, and the ways a drop of
    // liquid gives as it moves (see GlassWarp). Lean: one side taller than the other (LeanX, by its
    // height, + the right side) or wider (LeanY, by its width, + the bottom). Swell: its middle
    // bulging out (+) or drawn in (-), across and down alike.
    public struct Deform
    {
        public float ZoomX, ZoomY, LeanX, LeanY, Swell;
        public static readonly Deform None = new Deform { ZoomX = 1, ZoomY = 1 };
        public bool IsNone
        {
            get { return Math.Abs(ZoomX - 1) < 0.002f && Math.Abs(ZoomY - 1) < 0.002f && Math.Abs(LeanX) < 0.002f && Math.Abs(LeanY) < 0.002f && Math.Abs(Swell) < 0.002f; }
        }
        public bool Same(Deform o)
        {
            return ZoomX == o.ZoomX && ZoomY == o.ZoomY && LeanX == o.LeanX && LeanY == o.LeanY && Swell == o.Swell;
        }
    }

    // Bends a finished picture of the glass (premultiplied) for an animation frame: every row drawn
    // wider or narrower by its own amount, then every column taller or shorter by its own. Where a
    // row or column is drawn larger (or about the same), each pixel is a smooth blend of the two
    // nearest; where it is drawn smaller, each pixel is the average of all it covers (each source
    // pixel by how much of it), so a picture squeezed to a third of its height still shows every row
    // of its text, faintly, instead of every third. (The two agree where the size is just the same.)
    // What is drawn from past the picture's edge is transparent, so its soft edges and shadow stay
    // soft. The bends are measured by the card's own size (cardW, cardH) within the picture.
    public static class GlassWarp
    {
        [ThreadStatic] static int[] colAt, colBy;

        // src, dst: w x h. rows: scratch of w x h. rowScale, colScale: scratch of at least h and w.
        public static void Bend(int[] src, int w, int h, int cardW, int cardH, Deform look, int[] rows, int[] dst, float[] rowScale, float[] colScale)
        {
            float cx = w / 2f, cy = h / 2f, hx = Math.Max(1f, cardW / 2f), hy = Math.Max(1f, cardH / 2f);
            for (int y = 0; y < h; y++)
            {
                float v = Math.Max(-1f, Math.Min(1f, (y + 0.5f - cy) / hy));
                rowScale[y] = 1f / Math.Max(0.05f, look.ZoomX * (1f + look.LeanY * v) * (1f + look.Swell * (1f - v * v)));
            }
            for (int x = 0; x < w; x++)
            {
                float u = Math.Max(-1f, Math.Min(1f, (x + 0.5f - cx) / hx));
                colScale[x] = 1f / Math.Max(0.05f, look.ZoomY * (1f + look.LeanX * u) * (1f + look.Swell * (1f - u * u)));
            }
            // rows: where each pixel of the row comes from, in 1/65536ths of a pixel, stepping along
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                float step = rowScale[y];
                int at = (int)((cx + (0.5f - cx) * step - 0.5f) * 65536f), by = (int)(step * 65536f);
                for (int x = 0; x < w; x++, at += by) rows[row + x] = Pick(src, row, 1, w, at, by);
            }
            // then columns: where each comes from moves down by its own step from one row to the next
            if (colAt == null || colAt.Length < w) { colAt = new int[w]; colBy = new int[w]; }
            int[] colPos = colAt, colStep = colBy;
            for (int x = 0; x < w; x++)
            {
                colStep[x] = (int)(colScale[x] * 65536f);
                colPos[x] = (int)((cy - 0.5f + (0.5f - cy) * colScale[x]) * 65536f);
            }
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int at = colPos[x];
                    colPos[x] = at + colStep[x];
                    dst[row + x] = Pick(rows, x, w, h, at, colStep[x]);
                }
            }
        }

        // One pixel of a line of the picture (n pixels, the first at src[first], each 'stride' on),
        // drawn about place 'at' (1/65536ths of a pixel, from the first pixel's middle) at 'by' source
        // pixels to one (also 1/65536ths): a blend of the two nearest, or, where the line is drawn
        // smaller (by more than one), the average of the source pixels the drawn one spans.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Pick(int[] src, int first, int stride, int n, int at, int by)
        {
            if (by <= 65536)
            {
                int i = at >> 16;
                if (i < -1 || i >= n) return 0;
                int a = i >= 0 ? src[first + i * stride] : 0, b = i + 1 < n ? src[first + (i + 1) * stride] : 0;
                return a == b ? a : Blend(a, b, (at >> 8) & 255);
            }
            return Box(src, first, stride, n, at, by);
        }

        // The average over the span 'by' wide about 'at' (see Pick), each source pixel by how much of it
        // the span covers (in 1/256ths of a pixel); nothing past the ends.
        static int Box(int[] src, int first, int stride, int n, int at, int by)
        {
            int s0 = at - (by >> 1) + 32768, s1 = s0 + by;   // (from pixel middles to pixel edges: pixel i spans i to i + 1)
            int i0 = s0 >> 16, i1 = (s1 - 1) >> 16;
            if (i1 < 0 || i0 >= n) return 0;
            int sa = 0, sr = 0, sg = 0, sb = 0;
            for (int i = Math.Max(0, i0), last = Math.Min(n - 1, i1); i <= last; i++)
            {
                int wgt = 256;
                if (i == i0) wgt = 256 - ((s0 >> 8) & 255);
                if (i == i1) wgt -= 255 - (((s1 - 1) >> 8) & 255);
                if (wgt <= 0) continue;
                int p = src[first + i * stride];
                if (p == 0) continue;
                sa += ((p >> 24) & 255) * wgt;
                sr += ((p >> 16) & 255) * wgt;
                sg += ((p >> 8) & 255) * wgt;
                sb += (p & 255) * wgt;
            }
            int total = by >> 8, half = total >> 1;
            if (total <= 0) return 0;
            int A = (sa + half) / total, R = (sr + half) / total, G = (sg + half) / total, B = (sb + half) / total;
            if (A > 255) A = 255;
            if (R > A) R = A;
            if (G > A) G = A;
            if (B > A) B = A;
            return (A << 24) | (R << 16) | (G << 8) | B;
        }

        // The bent picture brought up to date after only the box 'changed' of the picture src was
        // changed (see Bend): the rows of its first step in that box, and what its second makes of
        // them, are worked out again, the same way and to the last digit; everything else in rows and
        // dst stays as it was, from the whole bending that left rowScale and colScale as they are.
        // Returns the part of dst that was done again.
        public static Rectangle BendPart(int[] src, int w, int h, int[] rows, int[] dst, float[] rowScale, float[] colScale, Rectangle changed)
        {
            changed = Rectangle.Intersect(changed, new Rectangle(0, 0, w, h));
            if (changed.IsEmpty) return Rectangle.Empty;
            float cx = w / 2f, cy = h / 2f;
            // how far the bending moves anything: the most any row or column is drawn away from its own,
            // and half the widest span a pixel averages where it is drawn smaller
            float devRow = 0, devCol = 0, spanRow = 1, spanCol = 1;
            for (int y = 0; y < h; y++) { float d = Math.Abs(1f / rowScale[y] - 1f); if (d > devRow) devRow = d; if (rowScale[y] > spanRow) spanRow = rowScale[y]; }
            for (int x = 0; x < w; x++) { float d = Math.Abs(1f / colScale[x] - 1f); if (d > devCol) devCol = d; if (colScale[x] > spanCol) spanCol = colScale[x]; }
            int moveX = (int)Math.Ceiling(w * 0.5f * devRow + spanRow * 0.5f) + 3, moveY = (int)Math.Ceiling(h * 0.5f * devCol + spanCol * 0.5f) + 3;
            int r0 = changed.Top, r1 = changed.Bottom;
            int xa = Math.Max(0, changed.Left - moveX), xb = Math.Min(w, changed.Right + moveX + 1);
            int ya = Math.Max(0, r0 - 1 - moveY), yb = Math.Min(h, r1 + 1 + moveY);

            for (int y = r0; y < r1; y++)
            {
                int row = y * w;
                float step = rowScale[y];
                int at = (int)((cx + (0.5f - cx) * step - 0.5f) * 65536f), by = (int)(step * 65536f);
                at += xa * by;
                for (int x = xa; x < xb; x++, at += by) rows[row + x] = Pick(src, row, 1, w, at, by);
            }
            if (colAt == null || colAt.Length < w) { colAt = new int[w]; colBy = new int[w]; }
            int[] colPos = colAt, colStep = colBy;
            for (int x = xa; x < xb; x++)
            {
                colStep[x] = (int)(colScale[x] * 65536f);
                colPos[x] = (int)((cy - 0.5f + (0.5f - cy) * colScale[x]) * 65536f) + ya * colStep[x];
            }
            for (int y = ya; y < yb; y++)
            {
                int row = y * w;
                for (int x = xa; x < xb; x++)
                {
                    int at = colPos[x];
                    colPos[x] = at + colStep[x];
                    dst[row + x] = Pick(rows, x, w, h, at, colStep[x]);
                }
            }
            return Rectangle.FromLTRB(xa, ya, xb, yb);
        }

        // Two premultiplied pixels mixed, the second making up f of 256.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Blend(int a, int b, int f)
        {
            uint ua = (uint)a, ub = (uint)b, keep = (uint)(256 - f), take = (uint)f;
            uint rb = (((ua & 0xFF00FF) * keep + (ub & 0xFF00FF) * take) >> 8) & 0xFF00FF;
            uint ag = ((((ua >> 8) & 0xFF00FF) * keep + ((ub >> 8) & 0xFF00FF) * take)) & 0xFF00FF00;
            return (int)(rb | ag);
        }
    }
}
