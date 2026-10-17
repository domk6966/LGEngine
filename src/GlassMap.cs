using System;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace LGEngine
{
    // Everything about a piece of glass that depends only on its size and the look settings, worked
    // out once so that each frame is just "look up, sample, add".
    //
    // The glass is modelled as a real slab: flat on top, with a rounded bezel around the edge.
    // Through the flat face you see a softened ("frosted"), tinted view of what is behind. On the
    // bezel the surface tilts, so the view bends by Snell's law, more the steeper it gets and a
    // little differently for red and blue, which near the rim folds the picture back on itself the
    // way thick glass does. The bezel is lit as a glossy curved surface from the upper left, with a
    // fainter answer on the far side, where the light comes back out.
    //
    // Each frame is drawn from two pictures of the screen behind the card: "soft" for the face, and
    // "edge", a less softened one, for the bent view in the bezel. (A little frost there keeps fine
    // detail, text above all, from turning to grit where the picture is squeezed.) Face eases into
    // rim over the inner part of the bezel, so the whole reads as one sheet of glass.
    //
    // How a frame is drawn (the CPU backend):
    //   1. The two pictures are tinted (the glass's colour: richer, darker or veiled) once each.
    //   2. The "view": what is seen through the glass, worked out once for each ViewStep x ViewStep
    //      pixels (GlassOptions.ViewStep; 2 by default) from a table saying where each such cell
    //      looks in the two pictures. The view is smooth by nature (a frosted picture, bent), so
    //      enlarging it smoothly to the pixels loses nothing visible and costs a quarter of the
    //      sampling.
    //   3. Each pixel: the view enlarged to it (a linear blend of the four cells round it), the
    //      light on the bezel (a per-pixel table: the rim line, the glow, the glint), the glass's
    //      colour kept (glassColour, for frames where only the contents change), then the contents'
    //      ink laid over it and the outline's smoothed edge applied.
    //   When only part of the backdrop changed (GlassBackdrop says which texels), only the cells that
    //   look there are sampled again and only the pixels they reach drawn: Shade returns that rect.
    //   When only the contents changed, only the pixels under the changed ink (and its underlay) are
    //   done, from glassColour, over a kept picture of the bare glass.
    public sealed class GlassMap
    {
        const float RefractiveIndex = 1.5f;
        // Fixed parts of the look. (Not constants only so that the benchmark can try others.)
        // The curved edge is a share of the card's shorter side, within limits (at 100% scaling): wide
        // enough that the whole card reads as a drop of liquid glass, everything behind it bending
        // more and more towards its edges, not a flat pane with a lip.
        internal static float BevelShare = 0.4f, BevelLeast = 5f, BevelMost = 60f;
        // The light on the edge, measured off Apple's own pictures of Liquid Glass (over plain dark
        // and light): a bright line right on the outline, fading inwards within about SpecFade
        // points, and inside it a faint glow GlowDepth points deep that fades evenly to nothing.
        // Both blend the glass towards white rather than add light to it, so that over a light
        // background they all but vanish, as Apple's do. Both are strongest at the top (the light
        // comes from above, a little to the left), a little less at the bottom (AwayShare), and at
        // the sides only LightLeast of that. All at the Rim slider's default; more or less with it.
        internal static float SpecMix = 0.68f, SpecFade = 0.6f;
        internal static float GlowMix = 0.16f, GlowDepth = 10f, GlowPower = 1.6f;
        internal static float LightLeast = 0.32f, AwayShare = 0.82f;
        // The light comes from the upper left (LightX, LightY: the way to it, on screen). Where the
        // outline faces it squarely (the top left corner, and more faintly the bottom right one
        // opposite) the light gathers into a glint a few points deep, as on Apple's glass.
        internal static float LightX = -0.6f, LightY = -0.8f;
        internal static float GlintMix = 0.3f, GlintPower = 8f, GlintFade = 2.5f, GlintAway = 0.45f;
        // And just outside the outline, a hairline darker edge (Apple's has one all the way round):
        // this much of what is behind is darkened, fading out over OutlineWidth points.
        internal static float OutlineAlpha = 0.4f, OutlineWidth = 0.8f;
        internal static float ShadowSpread = 12f, ShadowDrop = 5f, ShadowDepth = 0.40f;
        internal static float RimCreep = 1f;           // how fast the view still moves on where the bend would otherwise fold it back (1 = as fast as on the face; 0.12 stretched text behind the rim into vertical bars)
        internal static float Thickness = 1.6f;        // glass under the bezel, as a share of its width: more bends the view further
        internal static float Dispersion = 0.07f;      // how far apart red and blue are bent, at full refraction
        internal static float SplitMost = 0.75f;       // ...but never more than this many points either way
        internal static float LightVeil = 0.5f;        // light glass is at least this far towards white (see Tint)
        internal static float DimPerUnit = 0.45f;      // how much of what is behind dark glass keeps out, at the Dark slider's full (see Tint)
        // Over a lopsided background (half a dark window, half a white one) the glass would show two
        // very different halves. The more uneven it is (Even, 0-1), the more the glass is pulled
        // towards its own plain tone, so the halves differ less: light glass is veiled up to
        // EvenVeil of the way to white, dark glass dimmed a further EvenDim, and EvenMix of
        // the way to the backdrop's average colour is taken in (see Tint). (Lightly: the halves
        // should still show, as they do through Apple's glass; this only keeps the text readable.)
        internal static float EvenVeil = 0.7f, EvenDim = 0.3f, EvenMix = 0.45f;
        // The card's clarity (see GlassShape.FaceFrost and the rest).
        internal static float CardFaceFrost = 1f, CardDimming = 1f, CardEvening = 0.3f, CardVeil = 0.3f, CardLift = 4f;
        internal static float FringeFrom = 0.7f;      // red and blue part only from this far across the curve outwards (real glass fringes at its rim)
        internal static float BlendFrom = 0.5f;       // the curve is frosted alone as far as this share of its BlendBand, and only then starts turning clear
        internal static float FrostPerUnit = 0.03f;    // blur radius of the face per step of the Blur slider, at 100% scaling
        internal static float EdgeFrost = 0.2f;       // the rim's picture is softened this much of the face's...
        internal static float EdgeFrostLeast = 0.3f;   // ...but at least this much (a blur radius, at 100% scaling)
        internal static float BlendBand = 0.8f;       // share of the bezel, from its inner side, over which face eases into rim
        internal static float ProfilePower = 4f;       // the bezel's cross-section: (1 - t^p)^(1/p); 4 is a flat top with a rounded shoulder, 2 a round rod
        // The outer part of the curve looks out past the edge (see GlassShape.EdgeReach): what is
        // just beyond the glass is seen squeezed and bent round into its rim, as a drop of liquid
        // glass shows its surroundings. Nothing behind the glass is hidden: looking only inwards,
        // with a curve this wide, the rim would draw out what is well inside it and leave out
        // whatever is under the edge.
        internal static float ReachSoftness = 0.85f, BottomReach = 1f;
        internal static float EdgeReach = 0.4f, EdgeReachFrom = 0.45f;
        internal static float Magnify = 1.08f;        // how much the glass enlarges what is behind it, like a lens (1 = not at all)
        // The corners are continuous, as Apple's are: not a circular arc meeting the straight
        // edges with a sudden change of curvature, but a curve that starts bending this many
        // radii before the corner, gently at first (a superellipse, with the exponent that puts
        // its diagonal just where the circular corner's would be). 1 is a plain circular arc.
        internal static float CornerReach = 1.6f;
        // The soft picture is enlarged (see Fine) until its texels are no more than this many pixels
        // across. (2: at 100% and 125% scaling, where the picture read is shrunk by only two or three,
        // its own texels read straight would already show the grain of linear blending.)
        internal static float FineMost = 2f;
        // Elevation (GlassLook.Elevation, 0-100; 50 is the long-standing look): how far the glass
        // floats off the screen. Higher: the shadow spreads wider, falls further and lightens at its
        // far edge (ElevSpread, ElevDrop), the bezel bends the view a little more and reads a little
        // thicker (ElevBend, ElevBevel), and the rim and glint are brighter (ElevLight). Lower: all
        // of that the other way, and a hint of contact shadow right at the edge (ElevContact: how
        // dark, at elevation 0, fading out over ElevContactWidth points).
        internal static float ElevSpread = 0.6f, ElevSpreadDown = 0.35f, ElevDrop = 0.8f, ElevDepth = 0.15f;
        internal static float ElevBend = 0.25f, ElevBevel = 0.12f, ElevLight = 0.3f, ElevGlint = 0.4f;
        internal static float ElevContact = 0.35f, ElevContactWidth = 1.5f;
        internal static float TraceContact = 0.12f;   // (ray-traced glass: the faint contact shadow at the usual elevation)

        public readonly int CardW, CardH, Margin, W, H;
        // The screen behind the card is read at one texel per SharpFactor pixels (SharpW x SharpH,
        // covering the card and Pad pixels around it). From that come the two pictures a frame is
        // drawn from, each shrunk and then smoothed a number of times: "soft" (by Ratio, to
        // BackW x BackH) and "edge" (by EdgeRatio, to EdgeW x EdgeH).
        public readonly int SharpFactor, Pad, SharpW, SharpH;
        public readonly int Ratio, Factor, BackW, BackH, SoftPasses;
        public readonly int EdgeRatio, EdgeFactor, EdgeW, EdgeH, EdgePasses;
        // The soft picture is drawn from through a finer copy of it (FineW x FineH: Fine texels to
        // each of its own), made with a smooth (cubic B-spline) enlargement when its own texels
        // would be more than about two pixels across: read straight from texels that coarse, the
        // face shows the blocky, star-shaped grain of linear blending wherever the picture changes
        // quickly. The finer copy is made once with each new picture (see Tint).
        public readonly int Fine, FineW, FineH;
        readonly int[] fineTap, fineWeight;   // for each of the Fine places within a texel: the first of the four texels blended (from its own) and their four weights (of 256)
        // The rim's picture likewise, when GlassOptions.CubicEdge: EdgeFine texels to each of its own.
        public readonly int EdgeFine, EdgeFineW, EdgeFineH;
        readonly int[] edgeTap, edgeWeight;
        public readonly string Signature;
        public readonly int Elevation;
        public readonly GlassShape Shape;
        public readonly GlassLook Look;
        public readonly float Scale;

        // 0 for dark glass, 1 for light (or between, while it changes over): set before each Shade.
        // Light glass is tinted towards white as far as dark glass is towards black, and the grey
        // ink of the contents (text, lines, symbols) is turned from light to dark on it.
        public float Appearance;

        // How uneven the picture behind the card is, 0 (even) to 1 (two quite different halves),
        // and its average colour (0-255 each): set before each Shade, like Appearance. They are
        // folded into Tint's tables, so evening the glass out costs nothing per pixel.
        public float Even;

        public int AvgR = 128, AvgG = 128, AvgB = 128;

        // What never changes from frame to frame: the shadow around the card, and nothing elsewhere.
        readonly int[] template;
        int[] shadowKeep;

        // The glass row by row: the run of pixels it covers (spanFrom to spanTo, the outermost only
        // partly), and within that the flat face (faceFrom to faceTo; empty where there is none).
        // What is in between on either side is the bezel.
        readonly int[] spanFrom, spanTo, faceFrom, faceTo, rowGlow;
        // The bezel pixel by pixel, in the order they are drawn (row by row, left run then right
        // run; rowBz: the first of each row's): its light, as how much of the view it keeps (x 256)
        // << 16 | the light added to it; and how much of the pixel it covers (of 255) | the shadow
        // under it << 8 (for an outermost pixel it only partly covers).
        readonly int[] rowBz, bzLight, bzMix;
        readonly int bezelCount;
        // The ray-traced glass (see GlassTrace) lights its bezel in linear light: bzLight is then how much of
        // the view it keeps (x 256) << 16 | the light laid on (0-4096, already rolled off), and a pixel is
        // encode(v x keep + (1 - v x keep) x light), v the view decoded to linear light (a screen blend: it
        // never clips, and all but vanishes over white).
        readonly bool linearLight;
        // Moving glass (see Motion): per bezel pixel, which way its outline faces (0-255 round the circle, 0 = right,
        // 64 = down); the light the bezel is drawn with (bzLight, or motionLight while the light moves); and, made the
        // first time the glass moves, the sampled cells whose places the bend moves (bent: per cell its index, its
        // straight-down places in the rim's and the soft picture, x | y << 16 of its view cell), their places this
        // frame (motionTab, a copy of cellTab) and the straight-down geometry they are worked out from.
        readonly byte[] bzFacing;
        int[] lightNow, motionLight, motionTab, bent;
        byte[] moveCell;   // (per view cell: 1 where the glass moving changes it)
        int bentCount;
        readonly float magnifyM, edgeStepM, fineStepM;
        // How the glass moves this frame (see GlassMotion), set before each Shade like Appearance; and the motion
        // the drawn picture shows.
        public GlassMotion Motion;
        GlassMotion drawnMotion;
        static readonly int[] LinearDecode = MakeDecode();
        static readonly byte[] LinearEncode = MakeEncode();

        static double ToLinear(double v) { return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }

        static int[] MakeDecode()
        {
            int[] d = new int[256];
            for (int v = 0; v < 256; v++) d[v] = (int)Math.Round(ToLinear(v / 255.0) * 4096);
            return d;
        }

        static byte[] MakeEncode()
        {
            byte[] e = new byte[4097];
            for (int i = 0; i <= 4096; i++)
            {
                double l = i / 4096.0, v = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
                e[i] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v * 255)));
            }
            // (exactly back where nothing is laid on: decode then encode is the identity)
            int[] d = MakeDecode();
            for (int v = 0; v < 256; v++) e[d[v]] = (byte)v;
            return e;
        }
        readonly byte[] coverMap;   // per pixel: how much the glass covers it (255 all)
        // Where any pixel of the picture is seen from, at most, in pixels of the area read: how far
        // the bend and the dispersion can take its look from straight below it (for what a change
        // in the backdrop reaches).
        readonly float lookReach;

        // The view (see above): VW x VH cells of ViewStep pixels; for each pixel column and row, the
        // cell it lies in and how far towards the next (of 256); for each cell row, the cells wanted
        // (vFrom to vTo) and the first entry of that row in the cell table; and the table itself,
        // CellStride ints a cell:
        //   0, 1 where red looks and 2, 3 where blue looks in the edge picture, 4, 5 where it looks
        //     in the soft one (texels x 256)
        //   6 how much of the edge picture it shows (of 255) | 256 when red and blue look in
        //     different places
        public readonly int ViewStep, VW, VH;
        readonly int[] colV, colVMix, rowV, rowVMix;
        readonly int[] vFrom, vTo, rowCell;
        readonly int[] cellTab;     // (after the build: the sampled cells only, CellStride ints each, in rows)
        readonly int cellCount;     // how many sampled cells
        readonly int[] cellAt;      // the view index each sampled cell fills
        // derived cells (see DeriveAcross): the view index of each, and where its own frosted sample would be (texel x | y << 16)
        readonly int[] acrossAt, acrossPos, acrossEdge, downAt, downPos, downEdge;   // (Edge: its own red rim sample, likewise)
        readonly int acrossCount, downCount;
        // (ViewStep 1 with GlassOptions.FaceStep2) some frosted-only cells are averaged from two neighbours instead of
        // sampled: DeriveAcross (on even rows, from the cells left and right), DeriveDown (on odd rows, from above and
        // below); deriveGrow: how many fine texels further a change can reach a derived cell through them
        const int DeriveAcross = 512, DeriveDown = 1024;
        readonly bool derive;
        readonly int deriveGrow, edgeGrow;
        // how far (fine texels, x 256) a derived cell's sample may be from the middle of its two neighbours' for the
        // average to stand in for it (where the view is bent, it is not straight across, and the cell is sampled)
        internal static int DeriveSlack = 48, DeriveShare = 8, EdgeNear = 64;
        const int CellStride = 7;
        int[] view;
        uint[] vrow;

        readonly float saturation, addR, addG, addB, dim, evening, veilShare;
        int[] tintedSoft, tintedEdge, tintedEdgeCoarse;
        int[] tintedCoarse, fineRow;   // (the soft picture tinted at its own size, and one row of it, on the way to tintedSoft: see Tint)
        int[] tintedFromSoft, tintedFromEdge;
        int tintedVersion = -1;
        float tintedAppearance, tintedEven;
        int tintedAvgR, tintedAvgG, tintedAvgB;
        bool viewValid;
        // The glass's own colour at each pixel before the contents go on it (red, green, blue in ten
        // bits each, as they may run past 255 before the contents' ink is laid over them), and the
        // finished picture without any contents (the shadow, and the glass as it shows, smoothed
        // edge and all): see rowState.
        int[] glassColour, finalPx;
        float finalGain;
        // Per row: 1 glassColour holds the current glass there; 2 finalPx does too; 4 the picture
        // last drawn into (lastDst) holds the current picture there, contents and all.
        readonly byte[] rowState;
        int[] lastDst;
        int[] ownPart, lumaPart;   // (see Tint)

        // Right under the contents' ink (text, symbols, the slider) the glass is made a little
        // darker (dark glass) or lighter (light glass), fading out over UnderlayReach points round
        // it: so clear glass can stay clear, and what is written on it still reads over the
        // brightest or busiest part of what is behind. Underlay: how much, 0-1 (0 none).
        // (Apple's glass does the same, in effect: its text sits on a dimming layer, not on the
        // raw material.)
        internal static float UnderlayDefault = 0.22f, UnderlayReach = 5.5f;
        public float Underlay = UnderlayDefault;
        readonly float scaleK;
        byte[] halo;            // per pixel of the picture, 0-255: how much of the underlay is there
        byte[] haloRowAny;      // per row: whether any of it is on that row (most rows of a card have none)
        byte[] haloAcross, haloDown;   // (the ink softened across, then once down, on the way there)
        int[] haloRaw, haloRow, haloSum;
        int[] lastContent;      // the contents the underlay was made from, to see what has changed
        int lastContentW, lastContentH, lastShift, lastDrop, lastReach;
        bool haloWhole;         // the underlay covers the whole picture (not just some rows)

        // --------------------------------------------------------------------------------------
        // Building the map.

        // The numbers the ray trace is made from, in one place: the map builds from them, and GlassSideView shows what they make.
        internal static float BevelFor(int cardW, int cardH, float k, float elev, GlassShape shape)
        {
            float hx = cardW / 2f, hy = cardH / 2f;
            float bevel = Math.Max(shape.BevelLeast * k, Math.Min(shape.BevelMost * k, Math.Min(cardW, cardH) * shape.BevelShare));
            return Math.Min(bevel * (1 + ElevBevel * elev), Math.Min(hx, hy) * 0.9f);
        }

        internal static float RvFor(int cardW, int cardH, float k, GlassLook s, float bevel)
        {
            float hx = cardW / 2f, hy = cardH / 2f;
            float rad = Math.Min(s.Round * k, Math.Min(hx, hy));
            return Math.Min(Math.Min(hx, hy), Math.Max(rad, bevel));
        }

        internal static float MagnifyFor(GlassShape shape, float refraction, float intensity)
        {
            return 1f + (shape.Magnify - 1f) * Math.Min(1.5f, refraction * intensity);
        }

        internal static float ThickFor(float refraction, float intensity)
        {
            return Math.Max(0f, Math.Min(1.6f, (0.2f + 1.3f * refraction) * intensity / 1.24f));
        }

        // The ray trace for this glass (full false: the geometry and the landings only, for GlassSideView). The slab's thickness
        // and the shoulder's height go with the Refraction slider (and Intensity: at 0 it is a flat pane), the gap under it with
        // Elevation; the contours are the shape's analytic shoulder, or the shape's GlassProfile.
        internal static GlassTrace BuildTrace(int cardW, int cardH, float k, GlassLook s, GlassShape shape, bool full)
        {
            float elev = (s.Elevation - GlassLook.DefaultElevation) / 50f;
            float refraction = s.Refraction / 100f, rimLight = s.Rim / 100f, intensity = s.Intensity / 100f;
            float bevel = BevelFor(cardW, cardH, k, elev, shape), rv = RvFor(cardW, cardH, k, s, bevel);
            float magnify = MagnifyFor(shape, refraction, intensity), thick = ThickFor(refraction, intensity);
            float gap = shape.TraceGap * s.Elevation / 50f, bezel = Math.Max(1f, bevel);
            TraceGeo geo = shape.Profile == null
                ? new TraceGeo(bezel, shape.TracePower, shape.TraceRise * thick, shape.TraceSlab * thick, gap, k)
                : new TraceGeo(bezel, shape.Profile, thick, gap, k);
            return new GlassTrace(geo, shape.Dispersion * refraction * intensity, rv, magnify, shape.RimCreep, k, shape.SpecGain, shape.GlowGain, rimLight / 0.6f * intensity, full);
        }

        public static string SignatureFor(int cardW, int cardH, float k, GlassLook s)
        {
            return SignatureFor(cardW, cardH, k, s, GlassShape.Card);
        }

        public static string SignatureFor(int cardW, int cardH, float k, GlassLook s, GlassShape shape)
        {
            return cardW + "x" + cardH + "@" + k.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + s.Dark + "," + s.Blur + "," + s.Refraction + "," + s.Rim + ","
                + s.Shadow + "," + s.Intensity + "," + s.Round + (s.Elevation != GlassLook.DefaultElevation ? ",E" + s.Elevation : "") + "/" + shape.Name
                + (shape.Profile != null ? "/p" + shape.Profile.Hash : "")
                + (GlassOptions.ViewStep != 1 ? "/v" + GlassOptions.ViewStep : "") + (GlassOptions.FaceStep2 ? "" : "/nf") + (GlassOptions.AnalyticEdge ? "" : "/pt") + (GlassOptions.CubicEdge ? "/ce" : "")
                + (GlassOptions.RayTrace ? "/rt" : "");
        }

        // A picture softened to about 'sigma' pixels is made by shrinking it and then smoothing it a
        // few times; together those amount to a blur of about factor x sqrt(0.083 + 0.5 x passes).
        static void Frost(float sigma, int sharpFactor, out int ratio, out int passes)
        {
            ratio = 1;
            passes = 0;
            while (ratio < 12 && sharpFactor * ratio * 1.26f < sigma) ratio++;
            while (passes < 3 && sharpFactor * ratio * Math.Sqrt(0.083 + 0.5 * passes) < sigma) passes++;
        }

        // The weights of a cubic B-spline enlargement by 'fine' (see Fine): for each of the fine
        // places within a texel, the first of the four texels blended (from the texel's own) and
        // their four weights, of 256 exactly.
        static void SplineTaps(int fine, out int[] tap, out int[] weight)
        {
            tap = new int[fine];
            weight = new int[fine * 4];
            for (int j = 0; j < fine; j++)
            {
                float centre = (j + 0.5f) / fine - 0.5f;   // (from the texel this fine one lies in)
                int bas = (int)Math.Floor(centre);
                float t = centre - bas, t2 = t * t, t3 = t2 * t;
                float[] w = { (1 - t) * (1 - t) * (1 - t) / 6, (3 * t3 - 6 * t2 + 4) / 6, (-3 * t3 + 3 * t2 + 3 * t + 1) / 6, t3 / 6 };
                int sum = 0, biggest = 0;
                for (int q = 0; q < 4; q++)
                {
                    int wi = (int)(w[q] * 256 + 0.5f);
                    weight[j * 4 + q] = wi;
                    sum += wi;
                    if (wi > weight[j * 4 + biggest]) biggest = q;
                }
                weight[j * 4 + biggest] += 256 - sum;
                tap[j] = bas - 1;
            }
        }

        public GlassMap(int cardW, int cardH, float k, GlassLook s) : this(cardW, cardH, k, s, GlassShape.Card)
        {
        }

        // A map for this glass: built, or (when one with the same signature was built lately) a new map sharing that
        // one's baked tables, which never change once built, with its own state for drawing. (A notification of a
        // size seen before, the look switched back: no bake at all.)
        public static GlassMap Make(int cardW, int cardH, float k, GlassLook s, GlassShape shape)
        {
            string signature = SignatureFor(cardW, cardH, k, s, shape);
            lock (bakedLock)
            {
                for (int i = 0; i < baked.Count; i++)
                {
                    if (baked[i].Signature != signature) continue;
                    GlassMap hit = baked[i];
                    baked.RemoveAt(i);
                    baked.Insert(0, hit);
                    CacheHits++;
                    return new GlassMap(hit, true);
                }
            }
            GlassMap made = new GlassMap(cardW, cardH, k, s, shape);
            lock (bakedLock)
            {
                baked.Insert(0, new GlassMap(made, false));
                long bytes = 0;
                for (int i = 0; i < baked.Count; i++)
                {
                    bytes += baked[i].TableBytes;
                    if (i > 0 && (i >= CacheMost || bytes > CacheBytes)) { baked.RemoveRange(i, baked.Count - i); break; }
                }
            }
            return made;
        }

        public static GlassMap Make(int cardW, int cardH, float k, GlassLook s)
        {
            return Make(cardW, cardH, k, s, GlassShape.Card);
        }

        // (the maps kept, most recently used first; at most CacheMost of them, and within CacheBytes of tables)
        static readonly System.Collections.Generic.List<GlassMap> baked = new System.Collections.Generic.List<GlassMap>();
        static readonly object bakedLock = new object();
        internal static int CacheMost = 4;
        internal static long CacheBytes = 40L << 20;
        public static int CacheHits;

        // About how much memory the baked tables take.
        long TableBytes
        {
            get
            {
                return 4L * (template.Length + bzLight.Length + bzMix.Length + cellTab.Length + cellAt.Length + colV.Length * 2 + rowV.Length * 2
                    + acrossAt.Length * 3 + downAt.Length * 3 + spanFrom.Length * 6 + vFrom.Length * 3) + coverMap.Length;
            }
        }

        // A map sharing 'from's baked tables; with fresh state for drawing (or none, for the cache's own copy).
        GlassMap(GlassMap from, bool state)
        {
            CardW = from.CardW; CardH = from.CardH; Margin = from.Margin; W = from.W; H = from.H;
            SharpFactor = from.SharpFactor; Pad = from.Pad; SharpW = from.SharpW; SharpH = from.SharpH;
            Ratio = from.Ratio; Factor = from.Factor; BackW = from.BackW; BackH = from.BackH; SoftPasses = from.SoftPasses;
            EdgeRatio = from.EdgeRatio; EdgeFactor = from.EdgeFactor; EdgeW = from.EdgeW; EdgeH = from.EdgeH; EdgePasses = from.EdgePasses;
            Fine = from.Fine; FineW = from.FineW; FineH = from.FineH; fineTap = from.fineTap; fineWeight = from.fineWeight;
            EdgeFine = from.EdgeFine; EdgeFineW = from.EdgeFineW; EdgeFineH = from.EdgeFineH; edgeTap = from.edgeTap; edgeWeight = from.edgeWeight;
            Signature = from.Signature; Elevation = from.Elevation; Shape = from.Shape; Look = from.Look; Scale = from.Scale;
            template = from.template;
            spanFrom = from.spanFrom; spanTo = from.spanTo; faceFrom = from.faceFrom; faceTo = from.faceTo; rowGlow = from.rowGlow;
            rowBz = from.rowBz; bzLight = from.bzLight; bzMix = from.bzMix; bezelCount = from.bezelCount; linearLight = from.linearLight;
            coverMap = from.coverMap; lookReach = from.lookReach;
            ViewStep = from.ViewStep; VW = from.VW; VH = from.VH;
            colV = from.colV; colVMix = from.colVMix; rowV = from.rowV; rowVMix = from.rowVMix;
            vFrom = from.vFrom; vTo = from.vTo; rowCell = from.rowCell;
            cellTab = from.cellTab; cellCount = from.cellCount; cellAt = from.cellAt;
            acrossAt = from.acrossAt; acrossPos = from.acrossPos; acrossEdge = from.acrossEdge; downAt = from.downAt; downPos = from.downPos; downEdge = from.downEdge;
            acrossCount = from.acrossCount; downCount = from.downCount; derive = from.derive; deriveGrow = from.deriveGrow; edgeGrow = from.edgeGrow;
            saturation = from.saturation; addR = from.addR; addG = from.addG; addB = from.addB; dim = from.dim; evening = from.evening; veilShare = from.veilShare;
            scaleK = from.scaleK;
            bzFacing = from.bzFacing; magnifyM = from.magnifyM; edgeStepM = from.edgeStepM; fineStepM = from.fineStepM;
            lightNow = bzLight;
            if (state)
            {
                rowState = new byte[H];
                view = new int[VW * VH];
                vrow = new uint[VW + 1];
            }
        }

        public GlassMap(int cardW, int cardH, float k, GlassLook s, GlassShape shape)
        {
            long buildTick = System.Diagnostics.Stopwatch.GetTimestamp();
            Signature = SignatureFor(cardW, cardH, k, s, shape);
            Shape = shape;
            Look = s;
            Scale = k;
            scaleK = k;
            Elevation = s.Elevation;
            float elev = (s.Elevation - GlassLook.DefaultElevation) / 50f;   // -1 flat on the screen, 0 the usual, 1 well above it
            float dark = s.Dark / 100f, refraction = s.Refraction / 100f, rimLight = s.Rim / 100f, shadowAmount = s.Shadow / 100f;
            // Intensity scales everything that makes it look like glass rather than a tinted panel:
            // the bending, the colour fringes and the light on the edge. At 0 it is a flat dark card.
            float intensity = s.Intensity / 100f;
            shadowAmount *= Math.Min(1.6f, 0.35f + 0.65f * intensity);   // (the shadow too: it is what lifts the glass off the screen)
            shadowAmount *= 1 + ElevDepth * elev;
            float spreadK = elev >= 0 ? 1 + ElevSpread * elev : 1 + ElevSpreadDown * elev, dropK = Math.Max(0.2f, 1 + ElevDrop * elev);

            CardW = cardW;
            CardH = cardH;
            Margin = (int)Math.Ceiling(ShadowSpread * 3 * k * spreadK + Math.Max(0f, ShadowDrop * k * (dropK - 1)));
            W = cardW + Margin * 2;
            H = cardH + Margin * 2;

            float hx = cardW / 2f, hy = cardH / 2f;
            float rad = Math.Min(s.Round * k, Math.Min(hx, hy));
            // The corners (see CornerReach): each fills a box cornerR square, and its curve is a
            // superellipse: a circular arc where the box is no bigger than the radius, as in a
            // pill, and otherwise of the exponent n that puts its diagonal just where the circular
            // arc's would be, so the glass looks the same size, with only the corners smoother.
            // (The curve is tabulated by the direction from the box's centre, as the cosine of its
            // angle in CornerSteps steps: its radius as a share of the box, and the way out of it.)
            float cornerR = Math.Min(Math.Min(hx, hy), rad * CornerReach);
            float[] corner = null;
            if (cornerR > rad + 0.01f)
            {
                double diag = ((cornerR - rad) * Math.Sqrt(2) + rad) / cornerR, inv = 0.5 - Math.Log(diag, 2);
                if (inv > 0.02)
                {
                    double n = 1 / inv;
                    corner = new float[(CornerSteps + 1) * 3];
                    for (int i = 0; i <= CornerSteps; i++)
                    {
                        double u = i / (double)CornerSteps, v = Math.Sqrt(Math.Max(0, 1 - u * u));
                        double gx = Math.Pow(u, n - 1), gy = Math.Pow(v, n - 1), gl = Math.Sqrt(gx * gx + gy * gy);
                        corner[i * 3] = (float)(1 / Math.Pow(gx * u + gy * v, inv));
                        corner[i * 3 + 1] = (float)(gx / gl);
                        corner[i * 3 + 2] = (float)(gy / gl);
                    }
                }
            }
            float bevel = BevelFor(cardW, cardH, k, elev, shape);
            // How far past the edge the rim looks (see GlassShape.EdgeReach), with the Refraction slider.
            // (The ray-traced glass looks only where its rays land: inwards.)
            bool traced = GlassOptions.RayTrace;
            linearLight = traced;
            float reach = traced ? 0 : shape.EdgeReach * bevel * Math.Min(1.5f, refraction * intensity);

            SharpFactor = Math.Max(1, (int)Math.Round(k));
            float sigma = s.Blur * FrostPerUnit * k * shape.FaceFrost;
            Frost(sigma, SharpFactor, out Ratio, out SoftPasses);
            Factor = SharpFactor * Ratio;
            Pad = Math.Max(Factor * 3, (int)Math.Ceiling(reach + 3 * k));   // (the picture read must take in what the rim looks out at)
            BackW = (cardW + Pad * 2 + Factor - 1) / Factor;
            BackH = (cardH + Pad * 2 + Factor - 1) / Factor;
            SharpW = BackW * Ratio;
            SharpH = BackH * Ratio;

            // The finer copy of the soft picture (see Fine): each of its texels a cubic B-spline blend
            // of the four nearest of the soft picture's own, in each direction: smooth, and never
            // overshooting (its weights are all positive, so four of them can be summed in packed
            // pairs of channels).
            Fine = Math.Max(1, (int)Math.Ceiling(Factor / Math.Max(1f, FineMost) - 0.001f));
            FineW = BackW * Fine;
            FineH = BackH * Fine;
            float fineStep = Factor / (float)Fine;   // (pixels per fine texel)
            fineStepM = fineStep;
            SplineTaps(Fine, out fineTap, out fineWeight);

            // The rim: less frosted than the face, but never quite clear, and never more than the face.
            float edgeSigma = Math.Min(sigma, Math.Max(shape.EdgeFrostLeast * k, shape.EdgeFrost * sigma));
            Frost(edgeSigma, SharpFactor, out EdgeRatio, out EdgePasses);
            if (EdgeRatio > Ratio) EdgeRatio = Ratio;
            EdgeFactor = SharpFactor * EdgeRatio;
            EdgeW = Math.Max(2, SharpW / EdgeRatio);
            EdgeH = Math.Max(2, SharpH / EdgeRatio);
            EdgeFine = GlassOptions.CubicEdge ? Math.Max(1, EdgeFactor) : 1;
            EdgeFineW = EdgeW * EdgeFine;
            EdgeFineH = EdgeH * EdgeFine;
            float edgeStep = EdgeFactor / (float)EdgeFine;   // (pixels per texel the rim samples)
            edgeStepM = edgeStep;
            if (EdgeFine > 1) SplineTaps(EdgeFine, out edgeTap, out edgeWeight);

            saturation = 1f + (0.42f + 0.30f * dark) * Math.Min(1.2f, intensity);   // (vivid: Apple's glass takes up the colour of what is behind it)
            float chromaR = 1f + shape.Dispersion * refraction * intensity;   // (red bent this much more than green, blue as much less)
            float tint = dark * shape.Dimming, lift = shape.Lift * Math.Min(1f, dark * 2.5f);   // (the lift goes with the tint: clear glass at Dark 0 adds nothing)
            addR = 16 * 0.5f * tint + lift;
            addG = 18 * 0.5f * tint + lift;
            addB = 26 * 0.5f * tint + lift * 1.1f;
            dim = 1f - DimPerUnit * tint;
            evening = shape.Evening;
            veilShare = shape.Veil;

            float spread = ShadowSpread * k * shape.ShadowShare * spreadK, drop = ShadowDrop * k * shape.ShadowShare * dropK;
            float strength = (0.2f + 1.3f * refraction) * intensity * (1 + ElevBend * elev);
            float magnify = MagnifyFor(shape, refraction, intensity);
            magnifyM = magnify;

            // The bezel's shape depends only on how far across it a pixel is (t: 0 where the flat face
            // begins, 1 at the rim), so it is worked out once per step of t rather than per pixel.
            // It is a rounded ("squircle") shoulder. Its slope gives the angle the view enters the
            // glass at; Snell's law gives the angle it continues at; the thickness of glass below
            // turns that into a sideways shift.
            // The bend follows an outline a little rounder than the glass's own: one whose corners
            // are at least as round as the curve is wide. Then however far in the curve reaches, its
            // direction turns smoothly round each corner, rather than flipping from one edge's to the
            // next's along a seam on the diagonal (and a pill's is just its own).
            float rv = RvFor(cardW, cardH, k, s, bevel);

            const int Steps = 1024;
            float[] bendAt = new float[Steps + 1];
            for (int i = 0; i <= Steps; i++)
            {
                float t = i / (float)Steps, tp = (float)Math.Pow(t, shape.ProfilePower);
                if (tp > 0.9995f) tp = 0.9995f;
                float height = (float)Math.Pow(1 - tp, 1 / shape.ProfilePower);
                float slope = (float)Math.Pow(t, shape.ProfilePower - 1) * (float)Math.Pow(1 - tp, 1 / shape.ProfilePower - 1);
                if (slope > 14f) slope = 14f;
                double angleIn = Math.Atan(slope);
                double angleOut = Math.Asin(Math.Sin(angleIn) / RefractiveIndex);
                bendAt[i] = (float)Math.Tan(angleIn - angleOut) * (height + shape.Thickness) * bevel * strength;
            }

            // A lens's rim looks out past its edge: from EdgeReachFrom outwards, the place each step
            // looks at is drawn on towards a point that far outside, so what lies just beyond the glass
            // (a window's edge, the taskbar below) is seen bent round into it.
            // (bendIn keeps the bend without that, for edges that look out less: see BottomReach.)
            float[] bendIn = (float[])bendAt.Clone();
            float[] reachW = new float[Steps + 1];
            if (reach > 0)
            {
                for (int i = 0; i <= Steps; i++)
                {
                    float t = i / (float)Steps, w = (t - shape.EdgeReachFrom) / (1 - shape.EdgeReachFrom);
                    if (w <= 0) continue;
                    w = w * w * (3 - 2 * w);
                    reachW[i] = w;
                    float depthHere = (1 - t) * bevel, look = depthHere + bendAt[i];
                    look = (1 - w) * look - w * reach;
                    bendAt[i] = look - depthHere;
                }
            }

            // Nor may it ever reach past the middle of the glass (or, in a corner, the middle of the
            // corner's curve) and come up with what is beyond: crossed over, the two sides would show
            // as an X of doubled lines. (The view is enlarged too, which already brings the place
            // straight below that much nearer the middle.)
            foreach (float[] table in new float[][] { bendAt, bendIn })
            {
                for (int i = 0; i <= Steps; i++)
                {
                    float most = 0.9f * (rv - (1 - i / (float)Steps) * bevel) / magnify;
                    if (table[i] > most) table[i] = Math.Max(0, most);
                }
            }

            // Right at the rim a real bezel bends the view so far that the picture folds back on
            // itself and shows a second, mirrored copy. On a screen full of text that reads as grit,
            // and Apple's glass does not do it: there the rim draws the picture out into smooth
            // streaks instead. So, working outwards, the place each step looks at is never allowed to
            // turn back: where it would, it creeps on slowly instead.
            foreach (float[] table in new float[][] { bendAt, bendIn })
            {
                float lookedAt = bevel;
                for (int i = 1; i <= Steps; i++)
                {
                    float depthHere = (1 - i / (float)Steps) * bevel, look = depthHere + table[i];
                    float most = lookedAt - shape.RimCreep * bevel / Steps;
                    if (look > most)
                    {
                        look = most;
                        table[i] = look - depthHere;
                    }
                    lookedAt = look;
                }
            }
            float bendMost = 0;
            for (int i = 0; i <= Steps; i++) bendMost = Math.Max(bendMost, Math.Max(Math.Abs(bendAt[i]), Math.Abs(bendIn[i])));
            // The ray-traced glass (see GlassTrace): the slab's thickness and the shoulder's height go with the
            // Refraction slider (and Intensity: at 0 it is a flat pane), the gap under it with Elevation.
            GlassTrace trace = null;
            if (traced)
            {
                trace = BuildTrace(cardW, cardH, k, s, shape, true);
                bendMost = trace.LandMost;
            }
            lookReach = bendMost * (1 + SplitMost) + SplitMost * k + 2;

            // The shadow likewise depends only on the distance from the card (in quarter pixels).
            int[] shadowAt = new int[(int)(spread * 4 * 4) + 2];
            // (at low elevation, a contact shadow hugging the edge: see ElevContact)
            float contact = elev < 0 ? -elev * ElevContact * shadowAmount : 0, contactWidth = ElevContactWidth * k;
            // (the ray-traced glass is always grounded by a faint one, fading as it rises)
            if (traced) contact += TraceContact * shadowAmount * (1 - 0.6f * Math.Max(0f, elev));
            for (int i = 0; i < shadowAt.Length; i++)
            {
                float far = i / 4f;
                float a = ShadowDepth * shadowAmount * (float)Math.Exp(-(far * far) / (2 * spread * spread));
                if (contact > 0) a = Math.Max(a, contact * (float)Math.Exp(-far / contactWidth));
                shadowAt[i] = (int)(255 * a);
            }

            // Which way the light comes from, on screen.
            float l2x = LightX, l2y = LightY;
            float glowDepth = Math.Min(GlowDepth * k, bevel);    // (the glow stays within the curve)

            float edgeLight = rimLight / 0.6f * intensity * (1 + ElevLight * elev);       // (at the Rim slider's default, Apple's)
            float glintLight = edgeLight * (1 + ElevGlint * elev) / (1 + ElevLight * elev);
            float outline = 255 * OutlineAlpha * Math.Min(1f, intensity), outlineW = OutlineWidth * k;
            bool analytic = GlassOptions.AnalyticEdge;
            // What is drawn outside the glass (as alpha over black), by where a pixel is from the
            // card's middle and how far it is outside the outline: the shadow, which falls a little
            // below the card, and the hairline darker edge right along the outline. Under the
            // outermost pixels of the glass too, which it only partly covers: left out there, it
            // shows as a broken, jagged rim wherever the edge runs between pixels.
            Func<float, float, float, int> Outside = delegate(float px, float py, float sd)
            {
                // (the shadow by the plain rounded outline: too soft for the corners' curve to show)
                float cx = Math.Abs(px) - (hx - rad), cy = Math.Abs(py - drop) - (hy - rad), s2;
                if (cx > 0 && cy > 0) s2 = (float)Math.Sqrt(cx * cx + cy * cy) - rad; else s2 = Math.Max(cx, cy) - rad;
                int step = s2 <= 0 ? 0 : (int)(s2 * 4), shade = step < shadowAt.Length ? shadowAt[step] : 0;
                float edgeLine;
                if (analytic)
                {
                    // the hairline averaged over the pixel's extent outside the outline
                    float s0 = Math.Max(0f, sd - 0.5f), s1 = Math.Min(outlineW, sd + 0.5f);
                    edgeLine = s1 > s0 ? ((s1 - s0) - (s1 * s1 - s0 * s0) / (2 * outlineW)) / Math.Max(0.001f, sd + 0.5f - s0) : 0;
                }
                else edgeLine = 1 - sd / outlineW;
                return edgeLine > 0 ? Math.Max(shade, (int)(outline * edgeLine)) : shade;
            };

            BuildTicks[0] += System.Diagnostics.Stopwatch.GetTimestamp() - buildTick; buildTick = System.Diagnostics.Stopwatch.GetTimestamp();
            template = new int[W * H];
            spanFrom = new int[H];
            spanTo = new int[H];
            faceFrom = new int[H];
            faceTo = new int[H];
            rowBz = new int[H];
            rowGlow = new int[H];
            rowState = new byte[H];
            int room = (cardW + 3) * (cardH + 3) + 64;
            int[] light = new int[room], mixes = new int[room];
            byte[] facings = new byte[room];
            int count = 0;
            float specFade = SpecFade * k, glintFade = GlintFade * k;

            // Per row: scan in from the left until the flat face starts, then in from the right until it
            // starts again; everything covered before / after is the bezel, in order. (No reliance on the
            // two sides being mirror images to the last bit: at some scales they are not, by rounding.)
            // Pixels inside the covered run that are, by rounding, just outside the outline get a bezel
            // entry with no cover, so the tables always hold exactly one entry per bezel pixel.
            float[] rsd = new float[W], rt = new float[W], rnx = new float[W], rny = new float[W];
            for (int y = 0; y < H; y++)
            {
                spanFrom[y] = spanTo[y] = faceFrom[y] = faceTo[y] = 0;
                rowBz[y] = count;
                float py = y - Margin - hy + 0.5f;
                float top = 1 - (py + hy) / (cardH * 0.75f);
                rowGlow[y] = top <= 0 ? 0 : (int)(top * top * 8f * intensity + 0.5f);
                int firstCov = -1, lastCov = -1, faceL = -1, faceR = -1;
                if (Math.Abs(py) - hy >= 0.5f)
                {
                    // (a row wholly outside the glass: only the shadow there, the same mirrored left to right)
                    for (int x = 0; x < (W + 1) / 2; x++)
                    {
                        float px = x - Margin - hx + 0.5f;
                        float nx, ny, sd = Outline(px, py, hx, hy, cornerR, corner, out nx, out ny);
                        int shade = Outside(px, py, sd);
                        if (shade > 0) template[y * W + x] = template[y * W + W - 1 - x] = shade << 24;
                    }
                    continue;
                }
                for (int x = 0; x < W; x++)
                {
                    float px = x - Margin - hx + 0.5f;
                    float nx, ny, sd = Outline(px, py, hx, hy, cornerR, corner, out nx, out ny);
                    rsd[x] = sd; rnx[x] = nx; rny[x] = ny; rt[x] = 1;
                    if (sd >= 0.5f)
                    {
                        int shade = Outside(px, py, sd);
                        if (shade > 0) template[y * W + x] = shade << 24;
                        continue;
                    }
                    if (firstCov < 0) firstCov = x;
                    lastCov = x;
                    float t = Across(px, py, hx, hy, rv, bevel);
                    rt[x] = t;
                    if (t <= 0) { faceL = x; break; }
                }
                if (firstCov < 0) continue;
                if (faceL >= 0)
                {
                    lastCov = -1;
                    faceR = faceL;
                    for (int x = W - 1; x > faceL; x--)
                    {
                        float px = x - Margin - hx + 0.5f;
                        float nx, ny, sd = Outline(px, py, hx, hy, cornerR, corner, out nx, out ny);
                        rsd[x] = sd; rnx[x] = nx; rny[x] = ny; rt[x] = 1;
                        if (sd >= 0.5f)
                        {
                            int shade = Outside(px, py, sd);
                            if (shade > 0) template[y * W + x] = shade << 24;
                            continue;
                        }
                        if (lastCov < 0) lastCov = x;
                        float t = Across(px, py, hx, hy, rv, bevel);
                        rt[x] = t;
                        if (t <= 0) { faceR = x; break; }
                    }
                    if (lastCov < 0) lastCov = faceR;
                    faceFrom[y] = faceL;
                    faceTo[y] = faceR + 1;
                }
                else faceFrom[y] = faceTo[y] = lastCov + 1;
                spanFrom[y] = firstCov;
                spanTo[y] = lastCov + 1;

                for (int x = firstCov; x < spanTo[y]; x++)
                {
                    if (x == faceFrom[y]) { x = faceTo[y] - 1; continue; }
                    if (count >= room) break;   // (cannot happen: every covered pixel is within a pixel of the card)
                    float px = x - Margin - hx + 0.5f;
                    float sd = rsd[x], nx = rnx[x], ny = rny[x], t = rt[x];
                    float depth = -sd;

                    float coverage = 0.5f - sd;
                    int cover = coverage >= 1 ? 255 : coverage <= 0 ? 0 : (int)(coverage * 255), under = coverage >= 1 ? 0 : Outside(px, py, Math.Max(0f, sd));
                    mixes[count] = cover | (under << 8);

                    float facing = nx * l2x + ny * l2y;
                    if (trace != null)
                    {
                        // (ray-traced: the light and how much of the view comes through, averaged over the pixel)
                        int tg, ta;
                        trace.Light(depth, facing, out tg, out ta);
                        light[count] = (tg << 16) | ta;
                        facings[count] = Facing(nx, ny);
                        count++;
                        continue;
                    }
                    // Light on the edge (see SpecMix): how far towards white, from the line on the
                    // outline and the glow inside it, by how squarely this part of the edge faces the
                    // light or is directly opposite it.
                    float toward = facing > 0 ? facing : 0, away = facing < 0 ? -facing : 0;
                    float lit = LightLeast + (1 - LightLeast) * ((float)Math.Sqrt(toward) * toward + AwayShare * (float)Math.Sqrt(away) * away);
                    float inner = 1 - depth / glowDepth;
                    // (the line on the outline has faded to nothing a few of its widths in: no need to
                    // work it out there, which is most of the curve)
                    float spec = depth < 7 * specFade ? Falloff(depth, specFade, analytic) : 0;
                    float white = (spec * SpecMix * shape.SpecGain + (inner > 0 ? GlowMix * shape.GlowGain * (float)Math.Pow(inner, GlowPower) : 0)) * lit * edgeLight;
                    float glintAt;
                    if (GlintPower == 8f)
                    {
                        // (the usual power, as four squarings rather than two Pow calls per pixel)
                        float t2 = toward * toward, t4 = t2 * t2, a2 = away * away, a4 = a2 * a2;
                        glintAt = t4 * t4 + GlintAway * a4 * a4;
                    }
                    else glintAt = (float)Math.Pow(toward, GlintPower) + GlintAway * (float)Math.Pow(away, GlintPower);
                    if (glintAt > 0.01f) white += GlintMix * glintAt * Falloff(depth, glintFade, analytic) * glintLight;
                    if (white > 0.95f) white = 0.95f;
                    float glow = top < 0 ? 0 : top * top * 8f * (1 - Math.Min(1f, Math.Max(0f, t))) * intensity;
                    float gain = 1 - white;
                    float lightHere = glow * gain + 255f * white;
                    light[count] = ((gain >= 255 ? 65535 : (int)(gain * 256)) << 16) | (lightHere > 2000 ? 2000 : (int)lightHere);
                    facings[count] = Facing(nx, ny);
                    count++;
                }
            }
            bezelCount = count;
            bzLight = new int[Math.Max(1, count)];
            bzMix = new int[Math.Max(1, count)];
            Array.Copy(light, bzLight, count);
            Array.Copy(mixes, bzMix, count);
            bzFacing = new byte[Math.Max(1, count)];
            Array.Copy(facings, bzFacing, count);
            lightNow = bzLight;
            coverMap = new byte[W * H];
            for (int y = 0; y < H; y++)
            {
                int row = y * W, b = rowBz[y];
                for (int x = spanFrom[y]; x < faceFrom[y]; x++, b++) coverMap[row + x] = (byte)(bzMix[b] & 255);
                for (int x = faceFrom[y]; x < faceTo[y]; x++) coverMap[row + x] = 255;
                for (int x = faceTo[y]; x < spanTo[y]; x++, b++) coverMap[row + x] = (byte)(bzMix[b] & 255);
            }

            BuildTicks[1] += System.Diagnostics.Stopwatch.GetTimestamp() - buildTick; buildTick = System.Diagnostics.Stopwatch.GetTimestamp();
            // The view: its cells, where each pixel reads it, and what each cell samples.
            int vs = Math.Max(1, Math.Min(4, GlassOptions.ViewStep));
            ViewStep = vs;
            VW = (W + vs - 1) / vs;
            VH = (H + vs - 1) / vs;
            colV = new int[W];
            colVMix = new int[W];
            rowV = new int[H];
            rowVMix = new int[H];
            for (int x = 0; x < W; x++) Place(x, vs, VW, out colV[x], out colVMix[x]);
            for (int y = 0; y < H; y++) Place(y, vs, VH, out rowV[y], out rowVMix[y]);
            vFrom = new int[VH];
            vTo = new int[VH];
            rowCell = new int[VH];
            for (int vy = 0; vy < VH; vy++) { vFrom[vy] = VW; vTo[vy] = 0; }
            for (int y = 0; y < H; y++)
            {
                if (spanTo[y] <= spanFrom[y]) continue;
                int c0 = colV[spanFrom[y]], c1 = Math.Min(VW - 1, colV[spanTo[y] - 1] + 1);
                for (int vy = rowV[y]; vy <= Math.Min(VH - 1, rowV[y] + 1); vy++)
                {
                    if (c0 < vFrom[vy]) vFrom[vy] = c0;
                    if (c1 + 1 > vTo[vy]) vTo[vy] = c1 + 1;
                }
            }
            int cells = 0;
            for (int vy = 0; vy < VH; vy++)
            {
                rowCell[vy] = cells;
                if (vTo[vy] > vFrom[vy]) cells += vTo[vy] - vFrom[vy]; else vFrom[vy] = vTo[vy] = 0;
            }
            cellCount = cells;
            cellTab = new int[Math.Max(1, cells) * CellStride];
            view = new int[VW * VH];
            vrow = new uint[VW + 1];
            float edgeMaxX = EdgeFineW - 1.001f, edgeMaxY = EdgeFineH - 1.001f, softMaxX = FineW - 1.001f, softMaxY = FineH - 1.001f;
            if (trace != null && vs == 1)
            {
                // Ray-traced: where each cell's rays land on average over it, per colour, and how far they spread
                // (which frosts the view there). The glass is the same mirrored left to right and top to bottom, so
                // each cell of the top left quarter is worked out once and written to the (up to) four it stands for.
                float most = SplitMost * k, clear = Math.Max(edgeSigma + 0.5f * k, sigma);
                for (int qy = 0; qy < (VH + 1) / 2; qy++)
                {
                    float qpy = qy + 0.5f - Margin - hy;
                    int vy2 = VH - 1 - qy;
                    bool hasA = vTo[qy] > vFrom[qy], hasB = vTo[vy2] > vFrom[vy2];
                    if (!hasA && !hasB) continue;
                    int qa = hasA ? Math.Min(vFrom[qy], VW - vTo[qy]) : VW, qb = hasB ? Math.Min(vFrom[vy2], VW - vTo[vy2]) : VW, qfrom = Math.Min(qa, qb);
                    for (int qx = Math.Max(0, qfrom); qx < (VW + 1) / 2; qx++)
                    {
                        float qpx = qx + 0.5f - Margin - hx;
                        float nx, ny, sd = Outline(qpx, qpy, hx, hy, cornerR, corner, out nx, out ny);
                        float t = Across(qpx, qpy, hx, hy, rv, bevel);
                        if (t > 1 || sd > 0) t = 1;   // (a cell whose middle is just outside the glass samples as the rim does)
                        float tdx = 0, tdy = 0, lr = 0, lg = 0, lb = 0, fanned = 0;
                        int tshare = 0;
                        if (t > 0)
                        {
                            Direction(qpx, qpy, hx, hy, rv, out tdx, out tdy);
                            trace.Land(t, out lr, out lg, out lb, out fanned);
                            // (red and blue never more than SplitMost points from green, and parting only towards the
                            // rim; where they would part by too little to see, one look for both, which is cheaper)
                            float rimward = (t - FringeFrom) / (1 - FringeFrom) * 1.1f;
                            rimward = rimward <= 0 ? 0 : rimward >= 1 ? 1 : rimward * rimward * (3 - 2 * rimward);
                            lr = lg + Math.Max(-most, Math.Min(most, lr - lg)) * rimward;
                            lb = lg + Math.Max(-most, Math.Min(most, lb - lg)) * rimward;
                            if (Math.Abs(lr - lb) < 0.7f) lr = lb = lg;
                            // frosted on the face, turning clear across the middle of the curve (a frosted top, a
                            // polished edge); and frosted again where the rays fan out wider than the rim's picture
                            // is soft (micro-roughness: a pixel sees all its rays land on)
                            float ease = (t - BlendFrom * shape.BlendBand) / ((1 - BlendFrom) * shape.BlendBand);
                            ease = ease <= 0 ? 0 : ease >= 1 ? 1 : ease * ease * (3 - 2 * ease);
                            float fan = (fanned - edgeSigma) / (clear - edgeSigma);
                            fan = fan <= 0 ? 0 : fan >= 1 ? 1 : fan * fan * (3 - 2 * fan);
                            float sh = ease * (1 - fan);
                            tshare = sh >= 1 ? 255 : (int)(sh * 254);
                            if (tshare < 8) tshare = 0; else if (tshare > 247) tshare = 255;
                        }
                        for (int m = 0; m < 4; m++)
                        {
                            int vx = (m & 1) == 0 ? qx : VW - 1 - qx, vy = (m & 2) == 0 ? qy : vy2;
                            if ((m & 1) != 0 && vx == qx) continue;
                            if ((m & 2) != 0 && vy == qy) continue;
                            if (vx < vFrom[vy] || vx >= vTo[vy]) continue;
                            float sx = (m & 1) == 0 ? 1 : -1, sy = (m & 2) == 0 ? 1 : -1;
                            float lookX = Pad + hx + sx * qpx / magnify, lookY = Pad + hy + sy * qpy / magnify, dx = sx * tdx, dy = sy * tdy;
                            int o = (rowCell[vy] + vx - vFrom[vy]) * CellStride;
                            cellTab[o] = Fixed((lookX - dx * lr) / edgeStep - 0.5f, edgeMaxX);
                            cellTab[o + 1] = Fixed((lookY - dy * lr) / edgeStep - 0.5f, edgeMaxY);
                            cellTab[o + 2] = Fixed((lookX - dx * lb) / edgeStep - 0.5f, edgeMaxX);
                            cellTab[o + 3] = Fixed((lookY - dy * lb) / edgeStep - 0.5f, edgeMaxY);
                            cellTab[o + 4] = Fixed((lookX - dx * lg) / fineStep - 0.5f, softMaxX);
                            cellTab[o + 5] = Fixed((lookY - dy * lg) / fineStep - 0.5f, softMaxY);
                            bool tsplit = cellTab[o + 2] != cellTab[o] || cellTab[o + 3] != cellTab[o + 1];
                            cellTab[o + 6] = tshare | (tsplit ? 256 : 0);
                        }
                    }
                }
            }
            for (int vy = 0; vy < VH && !(trace != null && vs == 1); vy++)
            {
                float py = vy * vs + vs * 0.5f - Margin - hy;
                for (int vx = vFrom[vy], o = rowCell[vy] * CellStride; vx < vTo[vy]; vx++, o += CellStride)
                {
                    float px = vx * vs + vs * 0.5f - Margin - hx;
                    float nx, ny, sd = Outline(px, py, hx, hy, cornerR, corner, out nx, out ny);
                    float t = Across(px, py, hx, hy, rv, bevel);
                    if (t > 1 || sd > 0) t = 1;   // (a cell whose middle is just outside the glass samples as the rim does)
                    // Where this cell looks straight down to (in pixels of the area read), and how far
                    // the bezel shifts that, towards the middle of the card.
                    float lookX = Pad + hx + px / magnify, lookY = Pad + hy + py / magnify;
                    float shiftX = 0, shiftY = 0, split = 0, reachHere = 0;
                    if (trace != null)
                    {
                        // Ray-traced: where its rays land on average over the cell, per colour, and how
                        // far they spread (which frosts the view there).
                        float tdx = 0, tdy = 0, lr = 0, lg = 0, lb = 0, fanned = 0;
                        if (t > 0)
                        {
                            Direction(px, py, hx, hy, rv, out tdx, out tdy);
                            trace.Land(t, out lr, out lg, out lb, out fanned);
                        }
                        // (red and blue never more than SplitMost points from green; and where they would part
                        // by too little to see, one look for both, which is cheaper)
                        float most = SplitMost * k, rimward = (t - FringeFrom) / (1 - FringeFrom) * 1.1f;
                        rimward = rimward <= 0 ? 0 : rimward >= 1 ? 1 : rimward * rimward * (3 - 2 * rimward);   // (the colours part only towards the rim)
                        lr = lg + Math.Max(-most, Math.Min(most, lr - lg)) * rimward;
                        lb = lg + Math.Max(-most, Math.Min(most, lb - lg)) * rimward;
                        if (Math.Abs(lr - lb) < 0.7f) lr = lb = lg;
                        cellTab[o] = Fixed((lookX - tdx * lr) / edgeStep - 0.5f, edgeMaxX);
                        cellTab[o + 1] = Fixed((lookY - tdy * lr) / edgeStep - 0.5f, edgeMaxY);
                        cellTab[o + 2] = Fixed((lookX - tdx * lb) / edgeStep - 0.5f, edgeMaxX);
                        cellTab[o + 3] = Fixed((lookY - tdy * lb) / edgeStep - 0.5f, edgeMaxY);
                        cellTab[o + 4] = Fixed((lookX - tdx * lg) / fineStep - 0.5f, softMaxX);
                        cellTab[o + 5] = Fixed((lookY - tdy * lg) / fineStep - 0.5f, softMaxY);
                        int tshare = 0;
                        if (t > 0)
                        {
                            // frosted on the face, turning clear across the middle of the curve (a frosted top,
                            // a polished edge); and frosted again where the rays fan out wider than the rim's
                            // picture is soft (micro-roughness: a pixel sees all its rays land on)
                            float ease = (t - BlendFrom * shape.BlendBand) / ((1 - BlendFrom) * shape.BlendBand);
                            ease = ease <= 0 ? 0 : ease >= 1 ? 1 : ease * ease * (3 - 2 * ease);
                            float clear = Math.Max(edgeSigma + 0.5f * k, sigma), fan = (fanned - edgeSigma) / (clear - edgeSigma);
                            fan = fan <= 0 ? 0 : fan >= 1 ? 1 : fan * fan * (3 - 2 * fan);
                            float sh = ease * (1 - fan);
                            tshare = sh >= 1 ? 255 : (int)(sh * 254);
                            if (tshare < 8) tshare = 0; else if (tshare > 247) tshare = 255;
                        }
                        bool tsplit = cellTab[o + 2] != cellTab[o] || cellTab[o + 3] != cellTab[o + 1];
                        cellTab[o + 6] = tshare | (tsplit ? 256 : 0);
                        continue;
                    }
                    if (t > 0)
                    {
                        float dirX, dirY;
                        Direction(px, py, hx, hy, rv, out dirX, out dirY);
                        int at = (int)(t * Steps);
                        float bend = bendAt[at];
                        reachHere = reachW[at];
                        if (ny > 0 && shape.BottomReach < 1)
                        {
                            // along the bottom edge, mostly the bend without the look out past it
                            float less = ny * (1 - shape.BottomReach);
                            bend = bend + (bendIn[at] - bend) * less;
                            reachHere *= 1 - less;
                        }
                        shiftX = -dirX * bend;
                        shiftY = -dirY * bend;
                        // (red and blue bent a little more and less than green, but never more than
                        // SplitMost points either way: where the bend is steepest a share of it would part
                        // a line of text into coloured flecks)
                        split = chromaR - 1;
                        float bendLen = (float)Math.Sqrt(shiftX * shiftX + shiftY * shiftY);
                        if (bendLen * split > SplitMost * k) split = SplitMost * k / bendLen;
                        // (and only towards the rim, where real glass shows its colour fringes: none over the inner
                        // half of the curve, and where there is too little to see, one look for both, which is cheaper)
                        float fringe = (t - FringeFrom) / (1 - FringeFrom) * 1.1f;
                        split *= fringe <= 0 ? 0 : (fringe >= 1 ? 1 : fringe * fringe * (3 - 2 * fringe));
                        if (bendLen * split < 0.35f) split = 0;
                    }
                    cellTab[o] = Fixed((lookX + shiftX * (1 + split)) / edgeStep - 0.5f, edgeMaxX);
                    cellTab[o + 1] = Fixed((lookY + shiftY * (1 + split)) / edgeStep - 0.5f, edgeMaxY);
                    cellTab[o + 2] = Fixed((lookX + shiftX * (1 - split)) / edgeStep - 0.5f, edgeMaxX);
                    cellTab[o + 3] = Fixed((lookY + shiftY * (1 - split)) / edgeStep - 0.5f, edgeMaxY);
                    cellTab[o + 4] = Fixed((lookX + shiftX) / fineStep - 0.5f, softMaxX);
                    cellTab[o + 5] = Fixed((lookY + shiftY) / fineStep - 0.5f, softMaxY);
                    int share = 0;
                    if (t > 0)
                    {
                        // (frosted only, then turning clear across the middle of the curve: BlendFrom to BlendBand)
                        float ease = (t - BlendFrom * shape.BlendBand) / ((1 - BlendFrom) * shape.BlendBand);
                        if (ease < 0) ease = 0;
                        share = ease >= 1 ? 255 : (int)(ease * ease * (3 - 2 * ease) * 254);
                        // (where it looks out past the edge, partly the frosted picture: see ReachSoftness)
                        share = Math.Min(share, 255 - (int)(shape.ReachSoftness * reachHere * 255));
                        if (share < 8) share = 0; else if (share > 247) share = 255;   // (all but one picture: just that one, which is cheaper)
                    }
                    bool splitLook = cellTab[o + 2] != cellTab[o] || cellTab[o + 3] != cellTab[o + 1];
                    cellTab[o + 6] = Math.Max(0, share) | (splitLook ? 256 : 0);
                }
            }
            // (the frosted-only cells change slowly across: with FaceStep2, every other one each way is sampled and
            // the rest are the average of two sampled or averaged neighbours; the rim's cells are all sampled)
            BuildTicks[2] += System.Diagnostics.Stopwatch.GetTimestamp() - buildTick; buildTick = System.Diagnostics.Stopwatch.GetTimestamp();
            derive = vs == 1 && GlassOptions.FaceStep2;
            int reachMost = 0, edgeReachMost = 0;
            if (derive)
            {
                for (int vy = 0; vy < VH; vy++)
                {
                    for (int vx = vFrom[vy]; vx < vTo[vy]; vx++)
                    {
                        int o = (rowCell[vy] + vx - vFrom[vy]) * CellStride;
                        if ((vy & 1) == 0)
                        {
                            if ((vx & 1) == 0 || vx - 1 < vFrom[vy] || vx + 1 >= vTo[vy]) continue;
                            if (!Straight(o, o - CellStride, o + CellStride, ref reachMost, ref edgeReachMost)) continue;
                            cellTab[o + 6] |= DeriveAcross;
                        }
                        else
                        {
                            if (vy + 1 >= VH || vx < vFrom[vy - 1] || vx >= vTo[vy - 1] || vx < vFrom[vy + 1] || vx >= vTo[vy + 1]) continue;
                            int up = (rowCell[vy - 1] + vx - vFrom[vy - 1]) * CellStride, dn = (rowCell[vy + 1] + vx - vFrom[vy + 1]) * CellStride;
                            if (!Straight(o, up, dn, ref reachMost, ref edgeReachMost)) continue;
                            cellTab[o + 6] |= DeriveDown;
                        }
                    }
                }
            }
            // (a derived cell down is the average of cells that may be averages across: twice the reach, and the
            // two texels a sample reads)
            deriveGrow = 2 * ((reachMost + 255) >> 8) + 2;
            edgeGrow = 2 * ((edgeReachMost + 255) >> 8) + 2;
            // The table kept: the sampled cells only (a quarter of the face's and all the rim's), so that working out
            // the view reads no more of it than it needs; the derived ones as lists.
            int nS = 0, nA = 0, nD = 0;
            for (int i = 0; i < cells; i++)
            {
                int kind = cellTab[i * CellStride + 6] & (DeriveAcross | DeriveDown);
                if (kind == 0) nS++; else if (kind == DeriveAcross) nA++; else nD++;
            }
            int[] kept = new int[Math.Max(1, nS) * CellStride];
            cellAt = new int[Math.Max(1, nS)];
            acrossAt = new int[Math.Max(1, nA)];
            acrossPos = new int[Math.Max(1, nA)];
            downAt = new int[Math.Max(1, nD)];
            downPos = new int[Math.Max(1, nD)];
            acrossEdge = new int[Math.Max(1, nA)];
            downEdge = new int[Math.Max(1, nD)];
            nS = nA = nD = 0;
            for (int vy = 0; vy < VH; vy++)
            {
                for (int vx = vFrom[vy], o = rowCell[vy] * CellStride; vx < vTo[vy]; vx++, o += CellStride)
                {
                    int kind = cellTab[o + 6] & (DeriveAcross | DeriveDown), vi = vy * VW + vx;
                    int pos = (cellTab[o + 4] >> 8) | ((cellTab[o + 5] >> 8) << 16), epos = (cellTab[o] >> 8) | ((cellTab[o + 1] >> 8) << 16);
                    if (kind == 0) { Array.Copy(cellTab, o, kept, nS * CellStride, CellStride); cellAt[nS++] = vi; }
                    else if (kind == DeriveAcross) { acrossAt[nA] = vi; acrossEdge[nA] = epos; acrossPos[nA++] = pos; }
                    else { downAt[nD] = vi; downEdge[nD] = epos; downPos[nD++] = pos; }
                }
            }
            cellTab = kept;
            cellCount = nS;
            acrossCount = nA;
            BuildTicks[3] += System.Diagnostics.Stopwatch.GetTimestamp() - buildTick;
            downCount = nD;
        }

        // Whether cell o can be the average of cells a and b (either side of it) rather than sampled: the three look
        // at the pictures alike (the same split, near the same share of the rim's picture) and each of o's samples is
        // within DeriveSlack of the middle of a's and b's; the rim's (sharper) samples must also be close together
        // (EdgeNear), so nothing it bends into view is lost between them. reach / edgeReach: the furthest a
        // neighbour's frosted / rim sample is from o's so far (texels x 256; the rim's includes its red-blue split).
        bool Straight(int o, int a, int b, ref int reach, ref int edgeReach)
        {
            int[] t = cellTab;
            int mo = t[o + 6], ma = t[a + 6], mb = t[b + 6];
            if (((mo ^ ma) & 256) != 0 || ((mo ^ mb) & 256) != 0) return false;
            int so = mo & 255, sa = ma & 255, sb = mb & 255;
            if (Math.Abs(sa + sb - 2 * so) > 2 * DeriveShare) return false;
            if (so != 255 || sa != 255 || sb != 255)
            {
                if (!Near(t[o + 4], t[a + 4], t[b + 4], int.MaxValue) || !Near(t[o + 5], t[a + 5], t[b + 5], int.MaxValue)) return false;
            }
            bool rim = so != 0 || sa != 0 || sb != 0;
            if (rim)
            {
                for (int i = 0; i < ((mo & 256) != 0 ? 4 : 2); i++) if (!Near(t[o + i], t[a + i], t[b + i], EdgeNear)) return false;
            }
            reach = Math.Max(reach, Math.Max(Math.Max(Math.Abs(t[a + 4] - t[o + 4]), Math.Abs(t[b + 4] - t[o + 4])), Math.Max(Math.Abs(t[a + 5] - t[o + 5]), Math.Abs(t[b + 5] - t[o + 5]))));
            for (int i = 0; i < 4; i++) edgeReach = Math.Max(edgeReach, Math.Max(Math.Abs(t[a + i] - t[o + i]), Math.Abs(t[b + i] - t[o + i])) + Math.Abs(t[o + (i & 1) + 2] - t[o + (i & 1)]));
            return true;
        }

        // Whether position x (texels x 256) is within DeriveSlack of the middle of a and b, and a and b no more than
        // 'most' apart.
        static bool Near(int x, int a, int b, int most)
        {
            return Math.Abs(a + b - 2 * x) <= 2 * DeriveSlack && Math.Abs(a - b) <= most;
        }

        // The position of pixel x (or row y) in the view: the cell it reads and how far towards the
        // next (of 256).
        static void Place(int x, int vs, int cells, out int cell, out int mix)
        {
            float u = (x + 0.5f) / vs - 0.5f;
            if (u < 0) u = 0;
            if (u > cells - 1) u = cells - 1;
            cell = (int)u;
            mix = (int)((u - cell) * 256f + 0.5f);
            if (cell >= cells - 1) { cell = Math.Max(0, cells - 2); mix = cells > 1 ? 256 : 0; }
        }

        // The mean of exp(-d / fade) over a pixel's extent across the edge (depth d at its middle,
        // and only the part of it inside the glass), or just its value at the middle.
        static float Falloff(float depth, float fade, bool analytic)
        {
            if (!analytic) return (float)Math.Exp(-depth / fade);
            float d0 = Math.Max(0f, depth - 0.5f), d1 = depth + 0.5f;
            if (d1 - d0 < 0.01f) return (float)Math.Exp(-depth / fade);
            return fade * (float)(Math.Exp(-d0 / fade) - Math.Exp(-d1 / fade)) / (d1 - d0);
        }

        // Across the curve, by its own outline (see rv): 1 at the rim, 0 (or less) where the flat
        // face begins.
        static float Across(float px, float py, float hx, float hy, float rv, float bevel)
        {
            float vax = Math.Abs(px) - (hx - rv), vay = Math.Abs(py) - (hy - rv), vsd;
            if (vax > 0 && vay > 0) vsd = (float)Math.Sqrt(vax * vax + vay * vay) - rv;
            else vsd = Math.Max(vax, vay) - rv;
            return vsd >= 0 ? 1 : 1 + vsd / bevel;
        }

        // The way out across the curve.
        static void Direction(float px, float py, float hx, float hy, float rv, out float dirX, out float dirY)
        {
            float vax = Math.Abs(px) - (hx - rv), vay = Math.Abs(py) - (hy - rv);
            if (vax > 0 && vay > 0)
            {
                float vlen = (float)Math.Sqrt(vax * vax + vay * vay);
                dirX = (px < 0 ? -vax : vax) / vlen;
                dirY = (py < 0 ? -vay : vay) / vlen;
            }
            else if (vax > vay) { dirX = px < 0 ? -1 : 1; dirY = 0; }
            else { dirX = 0; dirY = py < 0 ? -1 : 1; }
        }

        // The signed distance (negative inside) from (px, py), measured from the card's middle, to
        // its outline: a rectangle of half-sides hx, hy whose corners each fill a box 'corner'
        // square with a superellipse of exponent n (2: a circular arc; see CornerReach), and the
        // way out of it there (nx, ny). In a corner it is the distance along the line from the
        // box's centre: a hair more than the true distance, within a few percent near the curve.
        // curve: the corner's curve tabulated by direction (see the constructor), or null for a
        // circular arc.
        const int CornerSteps = 1024;
        static float Outline(float px, float py, float hx, float hy, float corner, float[] curve, out float nx, out float ny)
        {
            float ax = Math.Abs(px) - (hx - corner), ay = Math.Abs(py) - (hy - corner);
            if (ax > 0 && ay > 0)
            {
                float len = (float)Math.Sqrt(ax * ax + ay * ay), sd;
                if (curve == null)
                {
                    sd = len - corner;
                    nx = ax / len;
                    ny = ay / len;
                }
                else
                {
                    float at = ax / len * CornerSteps;
                    int i = (int)at;
                    if (i >= CornerSteps) i = CornerSteps - 1;
                    float f = at - i, g = 1 - f;
                    int o = i * 3;
                    sd = len - corner * (curve[o] * g + curve[o + 3] * f);
                    nx = curve[o + 1] * g + curve[o + 4] * f;
                    ny = curve[o + 2] * g + curve[o + 5] * f;
                }
                if (px < 0) nx = -nx;
                if (py < 0) ny = -ny;
                return sd;
            }
            if (ax > ay) { nx = px < 0 ? -1 : 1; ny = 0; return ax - corner; }
            nx = 0;
            ny = py < 0 ? -1 : 1;
            return ay - corner;
        }

        // A place in a picture as texels x 256, kept where its four neighbours can be read.
        static int Fixed(float at, float most)
        {
            if (at < 0) at = 0; else if (at > most) at = most;
            return (int)(at * 256f);
        }

        // For each texel of the picture behind the card (SharpW x SharpH), how much of what is there
        // still shows through the card's shadow, out of 256 (256 where there is none).
        public int[] ShadowKeep()
        {
            if (shadowKeep != null) return shadowKeep;
            int f = SharpFactor, off = Margin - Pad;   // (a texel's first pixel, in the template: its place x f + off)
            int[] keep = new int[SharpW * SharpH];
            for (int ty = 0; ty < SharpH; ty++)
                for (int tx = 0; tx < SharpW; tx++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < f; dy++)
                    {
                        int y = ty * f + dy + off;
                        if (y < 0 || y >= H) continue;
                        for (int dx = 0; dx < f; dx++)
                        {
                            int x = tx * f + dx + off;
                            if (x >= 0 && x < W) sum += (int)((uint)template[y * W + x] >> 24);
                        }
                    }
                    keep[ty * SharpW + tx] = 256 - sum * 256 / (255 * f * f);
                }
            shadowKeep = keep;
            return keep;
        }

        // The rectangle the glass covers within its picture (shadow left out).
        public Rectangle GlassBounds
        {
            get
            {
                int y0 = H, y1 = 0, x0 = W, x1 = 0;
                for (int y = 0; y < H; y++)
                {
                    if (spanTo[y] <= spanFrom[y]) continue;
                    if (y < y0) y0 = y;
                    y1 = y + 1;
                    if (spanFrom[y] < x0) x0 = spanFrom[y];
                    if (spanTo[y] > x1) x1 = spanTo[y];
                }
                return y1 > y0 ? Rectangle.FromLTRB(x0, y0, x1, y1) : Rectangle.Empty;
            }
        }

        // --------------------------------------------------------------------------------------
        // The underlay under the contents' ink.

        // Brings the underlay up to date with the contents for a drawing of rows fromRow to toRow:
        // only the rows whose contents have changed since last time (and those within reach of
        // them) are made again. The contents are compared row by row, which costs far less than
        // making the underlay, and nothing at all when they are the same. Returns the part of the
        // picture the changed contents (and their underlay) reach, or empty if nothing changed.
        Rectangle UpdateHalo(int[] content, int contentW, int contentH, int shift, int drop, int fromRow, int toRow, bool on)
        {
            int r = Math.Max(1, (int)Math.Round(UnderlayReach * scaleK)), reach = on ? 2 * r : 0;
            if (on && (halo == null || halo.Length != W * H))
            {
                halo = new byte[W * H];
                haloRowAny = new byte[H];
                haloAcross = new byte[W * H];
                haloDown = new byte[W * H];
                haloRaw = new int[W];
                haloRow = new int[W];
                haloSum = new int[W];
                lastContent = null;
            }
            int x0 = Margin, x1 = Margin + CardW;
            int xa = Math.Max(x0, -shift), xb = Math.Min(x1, contentW - shift);
            if (lastContent == null || lastContent.Length != content.Length || lastContentW != contentW || lastContentH != contentH || lastShift != shift || lastDrop != drop || lastReach != r || (on && !haloWhole))
            {
                if (lastContent == null || lastContent.Length != content.Length) lastContent = new int[content.Length];
                Array.Copy(content, lastContent, content.Length);
                lastContentW = contentW;
                lastContentH = contentH;
                lastShift = shift;
                lastDrop = drop;
                lastReach = r;
                if (on) { BuildHalo(content, contentW, contentH, shift, drop, 0, H, r); haloWhole = true; }
                return new Rectangle(0, 0, W, H);
            }
            if (xb <= xa) return Rectangle.Empty;
            int c0 = int.MaxValue, c1 = int.MinValue, cx0 = int.MaxValue, cx1 = int.MinValue;
            for (int y = Math.Max(0, fromRow - reach), end = Math.Min(H, toRow + reach); y < end; y++)
            {
                int cy = y + drop;
                if (cy < 0 || cy >= contentH) continue;
                int at = cy * contentW + shift;
                int firstX = -1, lastX = -1;
                for (int x = xa; x < xb; x++)
                {
                    if (content[at + x] != lastContent[at + x]) { if (firstX < 0) firstX = x; lastX = x; }
                }
                if (firstX < 0) continue;
                Array.Copy(content, at + firstX, lastContent, at + firstX, lastX - firstX + 1);
                if (y < c0) c0 = y;
                if (y + 1 > c1) c1 = y + 1;
                if (firstX < cx0) cx0 = firstX;
                if (lastX + 1 > cx1) cx1 = lastX + 1;
            }
            if (c1 <= c0) return Rectangle.Empty;
            if (on) BuildHalo(content, contentW, contentH, shift, drop, Math.Max(0, c0 - reach), Math.Min(H, c1 + reach), r);
            return Rectangle.FromLTRB(Math.Max(0, cx0 - reach), Math.Max(0, c0 - reach), Math.Min(W, cx1 + reach), Math.Min(H, c1 + reach));
        }

        // Makes the underlay for rows a to b from the contents: their ink (alpha) softened twice
        // across and twice down by a box the width of the reach (a smooth, triangular fall-off),
        // then strengthened, so that the whole of a glyph's surroundings get it and it fades out
        // beyond. Only the card's own part of the contents counts.
        void BuildHalo(int[] content, int contentW, int contentH, int shift, int drop, int a, int b, int r)
        {
            int w = W;
            int x0 = Margin, x1 = Margin + CardW;
            int xa = Math.Max(x0, -shift), xb = Math.Min(x1, contentW - shift);
            byte[] across = haloAcross, down = haloDown, outp = halo;
            int[] raw = haloRaw, row = haloRow, sum = haloSum;
            // across: rows a - 2r to b + 2r, which the two passes down draw on
            int ya = Math.Max(0, a - 2 * r), yb = Math.Min(H, b + 2 * r);
            for (int y = ya; y < yb; y++)
            {
                int cy = y + drop, o = y * w;
                if (cy < 0 || cy >= contentH || xb <= xa) { Array.Clear(across, o, w); continue; }
                int crow = cy * contentW + shift;
                Array.Clear(raw, 0, w);
                bool any = false;
                for (int x = xa; x < xb; x++)
                {
                    int v = (int)((uint)content[crow + x] >> 24);
                    if (v != 0) { raw[x] = v; any = true; }
                }
                if (!any) { Array.Clear(across, o, w); continue; }
                BoxAcross(raw, row, w, r);
                BoxAcross(row, raw, w, r);
                for (int x = 0; x < w; x++) across[o + x] = (byte)raw[x];
            }
            // down, once: rows a - r to b + r
            BoxDown(across, down, w, Math.Max(0, a - r), Math.Min(H, b + r), r, sum, false);
            // and again, into the underlay itself, strengthened
            BoxDown(down, outp, w, a, b, r, sum, true);
        }

        // A box average of width 2r + 1 along a row (what is beyond its ends counts as nothing).
        static void BoxAcross(int[] src, int[] dst, int w, int r)
        {
            int span = 2 * r + 1, sum = 0;
            for (int x = 0; x < r && x < w; x++) sum += src[x];
            for (int x = 0; x < w; x++)
            {
                if (x + r < w) sum += src[x + r];
                if (x - r - 1 >= 0) sum -= src[x - r - 1];
                dst[x] = sum / span;
            }
        }

        // The same down the columns, for rows a to b of dst (what is above the picture or below it
        // counts as nothing). strengthen: the result is made stronger and eased, for the underlay
        // itself (see BuildHalo).
        void BoxDown(byte[] src, byte[] dst, int w, int a, int b, int r, int[] sum, bool strengthen)
        {
            if (b <= a) return;
            int span = 2 * r + 1, h = H;
            Array.Clear(sum, 0, w);
            // (to begin with, the window of the row before a: rows a - r - 1 to a + r - 1)
            for (int y = a - r - 1; y < a + r && y < h; y++)
            {
                if (y < 0) continue;
                int o = y * w;
                for (int x = 0; x < w; x++) sum[x] += src[o + x];
            }
            for (int y = a; y < b; y++)
            {
                int add = y + r, take = y - r - 1, o = y * w;
                if (add < h) { int ao = add * w; for (int x = 0; x < w; x++) sum[x] += src[ao + x]; }
                if (take >= 0) { int to = take * w; for (int x = 0; x < w; x++) sum[x] -= src[to + x]; }
                if (!strengthen)
                {
                    for (int x = 0; x < w; x++) dst[o + x] = (byte)(sum[x] / span);
                }
                else
                {
                    bool any = false;
                    for (int x = 0; x < w; x++)
                    {
                        int v = sum[x] * 3 / span;
                        if (v == 0) { dst[o + x] = 0; continue; }
                        if (v > 255) v = 255;
                        v = 255 - (255 - v) * (255 - v) / 255;   // (eased: full under the ink, fading out gently)
                        dst[o + x] = (byte)v;
                        any = true;
                    }
                    haloRowAny[y] = any ? (byte)1 : (byte)0;
                }
            }
        }

        // Lays the underlay (hv, 0-255) under one pixel of the glass: towards black by darkUnder
        // and towards white by lightUnder (each x 256, of the whole way).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Under(int hv, int darkUnder, int lightUnder, ref int R, ref int G, ref int B)
        {
            if (R > 255) R = 255;
            if (G > 255) G = 255;
            if (B > 255) B = 255;
            R += (((255 - R) * lightUnder - R * darkUnder) * hv) >> 16;
            G += (((255 - G) * lightUnder - G * darkUnder) * hv) >> 16;
            B += (((255 - B) * lightUnder - B * darkUnder) * hv) >> 16;
        }

        // --------------------------------------------------------------------------------------
        // The glass's colour: the tinted pictures and the view.

        // Applies the glass's colour (richer, darker, tinted) to texels from to to of a backdrop
        // picture, so the per-pixel work afterwards is only "read and blend".
        void Tint(int[] src, int[] dst, int from, int to)
        {
            // Each channel comes out as (luma + (c - luma) x sat) x dim + add: a part from the channel
            // itself and a part from the brightness, each looked up (x 256) rather than worked out.
            // (Light glass: the same dimming, made up towards white rather than a blue-black.)
            // (Light glass is made up towards white at least LightVeil of the way, whatever the tint:
            // under it dark text has to read over the darkest part of what is behind.)
            // (A lopsided backdrop, see Even: the veil grows towards EvenVeil and the dim towards
            // EvenDim, flattening the difference between its bright and dark parts; and then each
            // colour is taken part of the way to the tinted average, which is just one more constant:
            // each table is made (1 - mix) as strong and the average's share added to the luma one.)
            if (ownPart == null) TintTables();
            int[] own = ownPart, part = lumaPart;
            for (int i = from; i < to; i++)
            {
                int p = src[i], r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
                int luma = (r * 77 + g * 150 + b * 29) >> 8;
                int R = (own[r] + part[luma]) >> 8, G = (own[g] + part[256 + luma]) >> 8, B = (own[b] + part[512 + luma]) >> 8;
                if ((uint)R > 255) R = R < 0 ? 0 : 255;
                if ((uint)G > 255) G = G < 0 ? 0 : 255;
                if ((uint)B > 255) B = B < 0 ? 0 : 255;
                dst[i] = (R << 16) | (G << 8) | B;
            }
        }

        void TintTables()
        {
            float even = Math.Max(0f, Math.Min(1f, Even)) * evening, light = Appearance;
            float veil = Math.Max(1 - dim, LightVeil * veilShare);
            veil += Math.Max(0f, EvenVeil - veil) * even;
            float dimmed = dim * (1 - EvenDim * even), mul = dimmed + (1 - veil - dimmed) * light, white = 255 * veil;
            if (ownPart == null) { ownPart = new int[256]; lumaPart = new int[3 * 256]; }
            float[] add = { addR + (white - addR) * light, addG + (white - addG) * light, addB + (white - addB) * light };
            float mix = EvenMix * even, keepShare = 1 - mix;
            float avgLuma = (AvgR * 77 + AvgG * 150 + AvgB * 29) / 256f;
            int[] avg = { AvgR, AvgG, AvgB };
            float[] pull = new float[3];
            for (int c = 0; c < 3; c++)
            {
                float tinted = avg[c] * saturation * mul + avgLuma * (1 - saturation) * mul + add[c];
                pull[c] = mix * Math.Max(0f, Math.Min(255f, tinted));
            }
            for (int v = 0; v < 256; v++)
            {
                ownPart[v] = (int)(v * saturation * mul * keepShare * 256);
                for (int c = 0; c < 3; c++) lumaPart[c * 256 + v] = (int)(((v * (1 - saturation) * mul + add[c]) * keepShare + pull[c]) * 256);
            }
        }

        // Makes rows ya to yb (of src) of the finer copy of a picture (see Fine): dst, (w x fine) x
        // (h x fine), each texel of it a cubic B-spline blend of the sixteen nearest of src (w x h),
        // one direction at a time. row: room for w + 4.
        static void Enlarge(int[] src, int w, int h, int fine, int[] tap, int[] weight, int[] row, int[] dst, int ya, int yb)
        {
            int dw = w * fine, last = h - 1;
            for (int y = Math.Max(0, ya); y < Math.Min(h, yb); y++)
            {
                for (int j = 0; j < fine; j++)
                {
                    int r0 = y + tap[j];
                    int ra = (r0 < 0 ? 0 : r0) * w, rb = (r0 + 1 < 0 ? 0 : r0 + 1 > last ? last : r0 + 1) * w;
                    int rc = (r0 + 2 > last ? last : r0 + 2) * w, rd = (r0 + 3 > last ? last : r0 + 3) * w;
                    uint wa = (uint)weight[j * 4], wb = (uint)weight[j * 4 + 1], wc = (uint)weight[j * 4 + 2], wd = (uint)weight[j * 4 + 3];
                    // down: the four texel rows blended into one, two texels wider at each end
                    for (int x = 0; x < w; x++)
                    {
                        uint a = (uint)src[ra + x], b = (uint)src[rb + x], c = (uint)src[rc + x], d = (uint)src[rd + x];
                        uint rbb = (((a & 0xFF00FF) * wa + (b & 0xFF00FF) * wb + (c & 0xFF00FF) * wc + (d & 0xFF00FF) * wd) >> 8) & 0xFF00FF;
                        uint gg = (((a & 0x00FF00) * wa + (b & 0x00FF00) * wb + (c & 0x00FF00) * wc + (d & 0x00FF00) * wd) >> 8) & 0x00FF00;
                        row[x + 2] = (int)(rbb | gg);
                    }
                    row[0] = row[1] = row[2];
                    row[w + 2] = row[w + 3] = row[w + 1];
                    // across: each fine texel from the four texels of that row around it
                    int o = (y * fine + j) * dw;
                    for (int i = 0; i < fine; i++)
                    {
                        int from = tap[i] + 2;
                        uint ia = (uint)weight[i * 4], ib = (uint)weight[i * 4 + 1], ic = (uint)weight[i * 4 + 2], id = (uint)weight[i * 4 + 3];
                        for (int x = 0, at = from; x < w; x++, at++)
                        {
                            uint a = (uint)row[at], b = (uint)row[at + 1], c = (uint)row[at + 2], d = (uint)row[at + 3];
                            uint rbb = (((a & 0xFF00FF) * ia + (b & 0xFF00FF) * ib + (c & 0xFF00FF) * ic + (d & 0xFF00FF) * id) >> 8) & 0xFF00FF;
                            uint gg = (((a & 0x00FF00) * ia + (b & 0x00FF00) * ib + (c & 0x00FF00) * ic + (d & 0x00FF00) * id) >> 8) & 0x00FF00;
                            dst[o + x * fine + i] = (int)(rbb | gg);
                        }
                    }
                }
            }
        }

        // One picture's colour at a place between its texels (x, y in 1/256ths of a texel), blended
        // from the four around it: red and blue together, green on its own.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Sample(int[] pic, int w, int x, int y)
        {
            int at = (y >> 8) * w + (x >> 8);
            uint wx = (uint)(x & 255), wy = (uint)(y & 255), ix = 256 - wx, iy = 256 - wy;
            uint p00 = (uint)pic[at], p10 = (uint)pic[at + 1], p01 = (uint)pic[at + w], p11 = (uint)pic[at + w + 1];
            uint topRb = (((p00 & 0xFF00FF) * ix + (p10 & 0xFF00FF) * wx) >> 8) & 0xFF00FF, topG = (((p00 & 0xFF00) * ix + (p10 & 0xFF00) * wx) >> 8) & 0xFF00;
            uint lowRb = (((p01 & 0xFF00FF) * ix + (p11 & 0xFF00FF) * wx) >> 8) & 0xFF00FF, lowG = (((p01 & 0xFF00) * ix + (p11 & 0xFF00) * wx) >> 8) & 0xFF00;
            return (((topRb * iy + lowRb * wy) >> 8) & 0xFF00FF) | (((topG * iy + lowG * wy) >> 8) & 0xFF00);
        }

        // Whether a sample's four texels (x, y as Sample reads them) lie within a changed rect of texels.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool Touches(int x, int y, int left, int top, int right, int bottom)
        {
            int tx = x >> 8, ty = y >> 8;
            return tx + 1 >= left && tx < right && ty + 1 >= top && ty < bottom;
        }

        // What one cell of the view sees.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        uint SeeCell(int[] tab, int[] lo, int[] hi, int o)
        {
            // (tab: cellTab, or motionTab while the glass moves)
            int mix = tab[o + 6];
            uint share = (uint)(mix & 255), seen;
            if (share == 0) return Sample(lo, FineW, tab[o + 4], tab[o + 5]);
            seen = Sample(hi, EdgeFineW, tab[o], tab[o + 1]);
            if ((mix & 256) != 0)
            {
                uint blue = Sample(hi, EdgeFineW, tab[o + 2], tab[o + 3]);
                seen = (seen & 0xFF0000) | ((((seen & 0xFF00) + (blue & 0xFF00)) >> 1) & 0xFF00) | (blue & 0xFF);
            }
            if (share != 255)
            {
                uint frosted = Sample(lo, FineW, tab[o + 4], tab[o + 5]), rest = 256 - share;
                seen = ((((seen & 0xFF00FF) * share + (frosted & 0xFF00FF) * rest) >> 8) & 0xFF00FF) | ((((seen & 0xFF00) * share + (frosted & 0xFF00) * rest) >> 8) & 0xFF00);
            }
            return seen;
        }

        // Which way (nx, ny) points, as 0-255 round the circle (0 right, 64 down, 128 left, 192 up), from a cheap
        // pseudo-angle (smooth and in order, all a light that leans needs).
        static byte Facing(float nx, float ny)
        {
            float ax = Math.Abs(nx), ay = Math.Abs(ny), s = ax + ay;
            if (s < 1e-6f) return 0;
            float q = ny / s;                        // -1 .. 1 within a half
            float a = nx >= 0 ? q : 2 - q;           // -1 .. 3 round the circle
            if (a < 0) a += 4;
            return (byte)((int)(a * 64 + 0.5f) & 255);
        }

        // The cells the bend moves, and their straight-down places, found the first time the glass moves.
        void MakeMotion()
        {
            int[] tab = cellTab;
            int n = cellCount;
            int[] list = new int[Math.Max(1, n) * 6];
            float hx = CardW / 2f, hy = CardH / 2f;
            float edgeMaxX = EdgeFineW - 1.001f, edgeMaxY = EdgeFineH - 1.001f, softMaxX = FineW - 1.001f, softMaxY = FineH - 1.001f;
            int count = 0;
            for (int i = 0, o = 0; i < n; i++, o += CellStride)
            {
                int vi = cellAt[i], vx = vi % VW, vy = vi / VW;
                float px = vx * ViewStep + ViewStep * 0.5f - Margin - hx, py = vy * ViewStep + ViewStep * 0.5f - Margin - hy;
                float lookX = Pad + hx + px / magnifyM, lookY = Pad + hy + py / magnifyM;
                int ex = Fixed(lookX / edgeStepM - 0.5f, edgeMaxX), ey = Fixed(lookY / edgeStepM - 0.5f, edgeMaxY);
                int sx = Fixed(lookX / fineStepM - 0.5f, softMaxX), sy = Fixed(lookY / fineStepM - 0.5f, softMaxY);
                if (tab[o] == ex && tab[o + 1] == ey && tab[o + 2] == ex && tab[o + 3] == ey && tab[o + 4] == sx && tab[o + 5] == sy) continue;
                int b = count * 6;
                list[b] = i; list[b + 1] = ex; list[b + 2] = ey; list[b + 3] = sx; list[b + 4] = sy; list[b + 5] = vx | (vy << 16);
                count++;
            }
            bent = list;
            bentCount = count;
            motionTab = (int[])cellTab.Clone();
            // (and every view cell that follows them: they, and the derived cells averaged from them, in the order ViewAll
            // works them out; the face pixels on those are drawn again as the glass moves)
            byte[] moves = new byte[VW * VH];
            for (int b = 0; b < count; b++) moves[cellAt[list[b * 6]]] = 1;
            for (int i = 0; i < acrossCount; i++) { int vi = acrossAt[i]; if ((moves[vi - 1] | moves[vi + 1]) != 0) moves[vi] = 1; }
            for (int i = 0; i < downCount; i++) { int vi = downAt[i]; if ((moves[vi - VW] | moves[vi + VW]) != 0) moves[vi] = 1; }
            moveCell = moves;
        }

        // The view while the glass moves (see Motion): only the bent cells, each at its places scaled between
        // straight down and the baked landing by the bend this frame (global, swollen near SwellX/Y, rippled), then
        // the derived cells; and the bezel's light scaled by which way each pixel's outline faces. Nothing traced.
        void MotionView(int[] lo, int[] hi)
        {
            if (bent == null) MakeMotion();
            GlassMotion m = Motion;
            int[] tab = cellTab, mt = motionTab, list = bent, v = view, at = cellAt;
            float bend = m.Bend;
            // the swell by squared distance (in SwellTab steps of the radius), the ripple by column
            bool swell = m.Swell != 0 && m.SwellRadius > 0, ripple = m.Ripple != 0 && m.RippleLength > 0;
            float swellK = swell ? SwellSteps / (3f * m.SwellRadius * m.SwellRadius) : 0;
            if (swell)
            {
                if (swellTab == null) swellTab = new float[SwellSteps];
                for (int j = 0; j < SwellSteps; j++) swellTab[j] = m.Swell * (float)Math.Exp(-3.0 * j / SwellSteps);   // (to e^-3 at the radius x sqrt(3))
            }
            if (ripple)
            {
                if (rippleCol == null || rippleCol.Length < VW) rippleCol = new float[VW];
                for (int x = 0; x < VW; x++) rippleCol[x] = m.Ripple * (float)Math.Sin(2 * Math.PI * (x * ViewStep - Margin) / m.RippleLength + m.RipplePhase);
            }
            int edgeMaxX = (int)((EdgeFineW - 1.001f) * 256), edgeMaxY = (int)((EdgeFineH - 1.001f) * 256), softMaxX = (int)((FineW - 1.001f) * 256), softMaxY = (int)((FineH - 1.001f) * 256);
            float sx0 = m.SwellX + Margin, sy0 = m.SwellY + Margin;
            for (int b = 0, e = bentCount * 6; b < e; b += 6)
            {
                int i = list[b], o = i * CellStride, xy = list[b + 5], vx = xy & 0xFFFF, vy = xy >> 16;
                float s = 1;
                if (swell)
                {
                    float dx = vx * ViewStep - sx0, dy = vy * ViewStep - sy0;
                    int j = (int)((dx * dx + dy * dy) * swellK);
                    if (j < SwellSteps) s += swellTab[j];
                }
                if (ripple) s += rippleCol[vx];
                int k = (int)(bend * s * 256);
                int ex = list[b + 1], ey = list[b + 2], fx = list[b + 3], fy = list[b + 4];
                mt[o] = Clamp(ex + (((tab[o] - ex) * k) >> 8), edgeMaxX);
                mt[o + 1] = Clamp(ey + (((tab[o + 1] - ey) * k) >> 8), edgeMaxY);
                mt[o + 2] = Clamp(ex + (((tab[o + 2] - ex) * k) >> 8), edgeMaxX);
                mt[o + 3] = Clamp(ey + (((tab[o + 3] - ey) * k) >> 8), edgeMaxY);
                mt[o + 4] = Clamp(fx + (((tab[o + 4] - fx) * k) >> 8), softMaxX);
                mt[o + 5] = Clamp(fy + (((tab[o + 5] - fy) * k) >> 8), softMaxY);
                v[at[i]] = (int)SeeCell(mt, lo, hi, o);
            }
            at = acrossAt;
            for (int i = 0, n = acrossCount; i < n; i++) { int vi = at[i]; v[vi] = (int)Mid((uint)v[vi - 1], (uint)v[vi + 1]); }
            at = downAt;
            for (int i = 0, n = downCount, vw = VW; i < n; i++) { int vi = at[i]; v[vi] = (int)Mid((uint)v[vi - vw], (uint)v[vi + vw]); }

            // the light: scaled by how squarely each pixel's outline faces the point the light leans to
            if (!m.MovesLight) { lightNow = bzLight; return; }
            if (motionLight == null) motionLight = new int[bzLight.Length];
            if (facingScale == null) facingScale = new int[256];
            double toward = Math.Atan2(m.LeanY - CardH / 2f, m.LeanX - CardW / 2f);
            for (int j = 0; j < 256; j++)
            {
                double c = Math.Cos(j * Math.PI / 128 - toward);
                double f = m.Light * (1 + m.Lean * ((c > 0 ? c * c : 0) - 0.3 * (c < 0 ? c * c : 0)));
                facingScale[j] = (int)(Math.Max(0, f) * 256);
            }
            int[] src = bzLight, dst = motionLight, scale = facingScale;
            byte[] face = bzFacing;
            int most = linearLight ? 4095 : 2000;
            for (int i = 0, n = bezelCount; i < n; i++)
            {
                int lv = src[i], lt = ((lv & 0xFFFF) * scale[face[i]]) >> 8;
                dst[i] = (lv & ~0xFFFF) | (lt > most ? most : lt);
            }
            lightNow = dst;
        }
        const int SwellSteps = 256;
        float[] swellTab, rippleCol;
        int[] facingScale;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Clamp(int v, int most)
        {
            return v < 0 ? 0 : v > most ? most : v;
        }

        // Works out the whole view from the tinted pictures: the sampled cells, then those averaged across (on even
        // rows, from sampled cells), then those averaged down (on odd rows, from cells of the rows either side).
        void ViewAll(int[] lo, int[] hi)
        {
            int[] v = view, at = cellAt;
            for (int i = 0, o = 0, n = cellCount; i < n; i++, o += CellStride) v[at[i]] = (int)SeeCell(cellTab, lo, hi, o);
            at = acrossAt;
            for (int i = 0, n = acrossCount; i < n; i++) { int vi = at[i]; v[vi] = (int)Mid((uint)v[vi - 1], (uint)v[vi + 1]); }
            at = downAt;
            for (int i = 0, n = downCount, vw = VW; i < n; i++) { int vi = at[i]; v[vi] = (int)Mid((uint)v[vi - vw], (uint)v[vi + vw]); }
        }

        // The average of two view colours, channel by channel (rounded half up).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Mid(uint a, uint b)
        {
            return (((a & 0xFF00FF) + (b & 0xFF00FF) + 0x010001) >> 1 & 0xFF00FF) | (((a & 0xFF00) + (b & 0xFF00) + 0x100) >> 1 & 0xFF00);
        }

        // Works out again the cells that look at changed texels (edge: of the rim's picture as it is
        // sampled; fine: of the finer soft picture), and returns the pixels they reach (empty if none).
        // (Derived cells, see DeriveAcross, are worked out again where the change, grown by deriveGrow, reaches their
        // own sample: that covers every change to the cells they are averaged from.)
        Rectangle ViewChanged(int[] lo, int[] hi, Rectangle edge, Rectangle fine)
        {
            int[] v = view, tab = cellTab, at = cellAt;
            int el = edge.Left, et = edge.Top, er = edge.Right, eb = edge.Bottom;
            int fl = fine.Left, ft = fine.Top, fr = fine.Right, fb = fine.Bottom;
            bool edgeAny = er > el && eb > et, fineAny = fr > fl && fb > ft;
            int lo0 = int.MaxValue, hi0 = -1, vw = VW, cx0 = VW, cx1 = -1;
            for (int i = 0, o = 0, n = cellCount; i < n; i++, o += CellStride)
            {
                int mix = tab[o + 6], share = mix & 255;
                bool hit = false;
                if (share != 0 && edgeAny)
                {
                    hit = Touches(tab[o], tab[o + 1], el, et, er, eb);
                    if (!hit && (mix & 256) != 0) hit = Touches(tab[o + 2], tab[o + 3], el, et, er, eb);
                }
                if (!hit && share != 255 && fineAny) hit = Touches(tab[o + 4], tab[o + 5], fl, ft, fr, fb);
                if (!hit) continue;
                int vi = at[i];
                v[vi] = (int)SeeCell(cellTab, lo, hi, o);
                if (vi < lo0) lo0 = vi;
                hi0 = vi;
                int vx = vi % vw;
                if (vx < cx0) cx0 = vx;
                if (vx > cx1) cx1 = vx;
            }
            if ((fineAny || edgeAny) && acrossCount + downCount > 0)
            {
                int gl = fl - deriveGrow, gt = ft - deriveGrow, gr = fr + deriveGrow, gb = fb + deriveGrow;
                int hl = el - edgeGrow, ht = et - edgeGrow, hr = er + edgeGrow, hb = eb + edgeGrow;
                for (int pass = 0; pass < 2; pass++)
                {
                    int[] list = pass == 0 ? acrossAt : downAt, where = pass == 0 ? acrossPos : downPos, whereEdge = pass == 0 ? acrossEdge : downEdge;
                    int step = pass == 0 ? 1 : vw;
                    for (int i = 0, n = pass == 0 ? acrossCount : downCount; i < n; i++)
                    {
                        int p = where[i], tx = p & 0xFFFF, ty = p >> 16;
                        if (!fineAny || tx + 1 < gl || tx >= gr || ty + 1 < gt || ty >= gb)
                        {
                            p = whereEdge[i]; tx = p & 0xFFFF; ty = p >> 16;
                            if (!edgeAny || tx + 1 < hl || tx >= hr || ty + 1 < ht || ty >= hb) continue;
                        }
                        int vi = list[i];
                        v[vi] = (int)Mid((uint)v[vi - step], (uint)v[vi + step]);
                        if (vi < lo0) lo0 = vi;
                        if (vi > hi0) hi0 = vi;
                        int vx = vi % vw;
                        if (vx < cx0) cx0 = vx;
                        if (vx > cx1) cx1 = vx;
                    }
                }
            }
            if (hi0 < 0) return Rectangle.Empty;
            int cy0 = lo0 / vw, cy1 = hi0 / vw;
            // the pixels that read any of those cells (each pixel reads its cell and the next)
            int x0 = 0, x1 = W, y0 = 0, y1 = H;
            while (x0 < W && colV[x0] + 1 < cx0) x0++;
            while (x1 > x0 && colV[x1 - 1] > cx1) x1--;
            while (y0 < H && rowV[y0] + 1 < cy0) y0++;
            while (y1 > y0 && rowV[y1 - 1] > cy1) y1--;
            return Rectangle.FromLTRB(x0, y0, x1, y1);
        }

        // --------------------------------------------------------------------------------------
        // Drawing.

        // Puts into a picture what Shade leaves alone: the shadow, and nothing where there is no
        // glass. To be done once for each picture Shade is to draw into.
        public void Prime(int[] dst)
        {
            PrimeInto(dst, ShadowGain, 0, H);
            primedGain = ShadowGain;
            if (dst == lastDst) for (int y = 0; y < H; y++) rowState[y] &= 3;
        }

        void PrimeInto(int[] dst, float shadowGain, int fromRow, int toRow)
        {
            int from = fromRow * W, to = toRow * W;
            Array.Copy(template, from, dst, from, to - from);
            if (shadowGain != 1f)
            {
                int gain = (int)(shadowGain * 256f);
                for (int i = from; i < to; i++)
                {
                    int p = template[i];
                    if (p == 0 || (p & 0xFFFFFF) != 0) continue;   // (only the shadow: black, with some alpha)
                    int a = (((int)((uint)p >> 24)) * gain) >> 8;
                    dst[i] = (a > 255 ? 255 : a) << 24;
                }
            }
        }

        // How deep the shadow is drawn, as a share of the look's own (see Prime).
        public float ShadowGain = 1f;
        // Where a frame's time goes, summed (Stopwatch ticks): the tinting and enlarging of the pictures, the view, the
        // underlay, the drawing of the glass, and the contents-only drawing. (For the benchmark and the stats.)
        public static readonly long[] PhaseTicks = new long[5];
        // Where building maps takes its time, summed (Stopwatch ticks): the profile (and the ray trace), the rows (outline, shadow,
        // bezel light), the view's cells, the derived cells. (For the benchmark.)
        public static readonly long[] BuildTicks = new long[4];
        public static readonly string[] PhaseNames = { "tint", "view", "halo", "compose", "contents" };
        // What the last Shade (the GlassBackdrop overloads) did about the backdrop: 0 nothing drawn, 1 only contents (or rows)
        // drawn again, 2 an incremental re-shade (part of the backdrop changed), 3 the whole backdrop shaded again. (For the stats.)
        public int LastKind { get; private set; }
        float primedGain = 1f;
        public float PrimedGain { get { return primedGain; } }

        // Draws the glass into dst (W x H, premultiplied), which has been Primed.
        // soft, edge: the two pictures of what is behind (BackW x BackH and EdgeW x EdgeH).
        // content: the pop-up's own picture with its card colour removed (premultiplied),
        // contentW x contentH, with the card at cardLeft, cardTop; or null.
        public void Shade(int[] soft, int[] edge, int[] content, int contentW, int contentH, int cardLeft, int cardTop, int[] dst)
        {
            Draw(soft, edge, -1, null, content, contentW, contentH, cardLeft, cardTop, dst, 0, H);
        }

        // backdropVersion: GlassBackdrop.Version of soft and edge (-1 if not known). The glass's
        // colouring of them is only worked out again when that changes (it does not when only
        // the pop-up's contents have).
        public void Shade(int[] soft, int[] edge, int backdropVersion, int[] content, int contentW, int contentH, int cardLeft, int cardTop, int[] dst)
        {
            Draw(soft, edge, backdropVersion, null, content, contentW, contentH, cardLeft, cardTop, dst, 0, H);
        }

        // The same for rows fromRow to toRow only (the rest of dst is left as it is).
        public void Shade(int[] soft, int[] edge, int backdropVersion, int[] content, int contentW, int contentH, int cardLeft, int cardTop, int[] dst, int fromRow, int toRow)
        {
            Draw(soft, edge, backdropVersion, null, content, contentW, contentH, cardLeft, cardTop, dst, fromRow, toRow);
        }

        // The same from a backdrop, which knows what changed in it since the last picture: only what
        // that reaches is drawn again, and only the contents that changed. Returns the part of dst
        // drawn (empty if nothing was).
        public Rectangle Shade(GlassBackdrop backdrop, int[] content, int contentW, int contentH, int cardLeft, int cardTop, int[] dst)
        {
            return Draw(backdrop.Soft, backdrop.Edge, backdrop.Version, backdrop, content, contentW, contentH, cardLeft, cardTop, dst, 0, H);
        }

        public Rectangle Shade(GlassBackdrop backdrop, int[] content, int contentW, int contentH, int cardLeft, int cardTop, int[] dst, int fromRow, int toRow)
        {
            return Draw(backdrop.Soft, backdrop.Edge, backdrop.Version, backdrop, content, contentW, contentH, cardLeft, cardTop, dst, fromRow, toRow);
        }

        Rectangle Draw(int[] soft, int[] edge, int backdropVersion, GlassBackdrop backdrop, int[] content, int contentW, int contentH, int cardLeft, int cardTop, int[] dst, int fromRow, int toRow)
        {
            fromRow = Math.Max(0, fromRow);
            toRow = Math.Min(H, toRow);
            LastKind = 0;
            if (toRow <= fromRow || dst == null || dst.Length != W * H) return Rectangle.Empty;
            if (soft == null || soft.Length != BackW * BackH || edge == null || edge.Length != EdgeW * EdgeH) return Rectangle.Empty;
            if (glassColour == null) { glassColour = new int[W * H]; finalPx = new int[W * H]; }
            long tick = System.Diagnostics.Stopwatch.GetTimestamp();
            if (dst != lastDst)
            {
                lastDst = dst;
                for (int y = 0; y < H; y++) rowState[y] &= 3;
            }
            if (primedGain != finalGain) { for (int y = 0; y < H; y++) rowState[y] &= 5; finalGain = primedGain; }

            // 1. The glass's own colour: the tinted pictures and the view, kept while the backdrop
            //    and the glass's appearance stay the same; made again where they changed.
            bool stale = backdropVersion < 0 || backdropVersion != tintedVersion || soft != tintedFromSoft || edge != tintedFromEdge || Appearance != tintedAppearance || Even != tintedEven || !viewValid;
            Rectangle affected = Rectangle.Empty;   // (the pixels whose glass changed)
            bool whole = false;
            LastKind = 1;
            if (stale)
            {
                if (tintedSoft == null || tintedSoft.Length != FineW * FineH) tintedSoft = new int[FineW * FineH];
                if (tintedEdge == null || tintedEdge.Length != EdgeFineW * EdgeFineH) tintedEdge = new int[EdgeFineW * EdgeFineH];
                if (Fine > 1 && (tintedCoarse == null || tintedCoarse.Length != soft.Length)) tintedCoarse = new int[soft.Length];
                if (EdgeFine > 1 && (tintedEdgeCoarse == null || tintedEdgeCoarse.Length != edge.Length)) tintedEdgeCoarse = new int[edge.Length];
                if (fineRow == null || fineRow.Length != Math.Max(BackW, EdgeW) + 4) fineRow = new int[Math.Max(BackW, EdgeW) + 4];
                // (a part of the picture changed, and the glass's colouring is otherwise as it was: the
                // average colour behind it may have moved a little, in which case the tint tables are
                // kept as they are, so the whole picture stays tinted alike)
                bool partial = GlassOptions.Incremental && backdrop != null && viewValid && backdropVersion >= 0 && backdrop.ChangeFrom == tintedVersion
                    && soft == tintedFromSoft && edge == tintedFromEdge && Appearance == tintedAppearance && Even == tintedEven && backdrop.Partial
                    && ownPart != null && Math.Abs(AvgR - tintedAvgR) <= 12 && Math.Abs(AvgG - tintedAvgG) <= 12 && Math.Abs(AvgB - tintedAvgB) <= 12
                    && Motion.IsNeutral && drawnMotion.IsNeutral;   // (moving glass: whole frames)
                if (!partial) { ownPart = null; tintedAvgR = AvgR; tintedAvgG = AvgG; tintedAvgB = AvgB; }
                if (partial)
                {
                    LastKind = 2;
                    Rectangle cs = backdrop.ChangedSoft, ce = backdrop.ChangedEdge;
                    // the soft picture: the changed texel rows tinted again, and the finer copy made again where they reach
                    Rectangle fine;
                    if (Fine == 1)
                    {
                        Tint(soft, tintedSoft, cs.Top * BackW, cs.Bottom * BackW);
                        fine = cs;
                    }
                    else
                    {
                        Tint(soft, tintedCoarse, cs.Top * BackW, cs.Bottom * BackW);
                        Enlarge(tintedCoarse, BackW, BackH, Fine, fineTap, fineWeight, fineRow, tintedSoft, cs.Top - 2, cs.Bottom + 2);
                        fine = Rectangle.FromLTRB(Math.Max(0, (cs.Left - 2) * Fine), Math.Max(0, (cs.Top - 2) * Fine), Math.Min(FineW, (cs.Right + 2) * Fine), Math.Min(FineH, (cs.Bottom + 2) * Fine));
                    }
                    Rectangle edgeFine;
                    if (EdgeFine == 1)
                    {
                        Tint(edge, tintedEdge, ce.Top * EdgeW, ce.Bottom * EdgeW);
                        edgeFine = ce;
                    }
                    else
                    {
                        Tint(edge, tintedEdgeCoarse, ce.Top * EdgeW, ce.Bottom * EdgeW);
                        Enlarge(tintedEdgeCoarse, EdgeW, EdgeH, EdgeFine, edgeTap, edgeWeight, fineRow, tintedEdge, ce.Top - 2, ce.Bottom + 2);
                        edgeFine = Rectangle.FromLTRB(Math.Max(0, (ce.Left - 2) * EdgeFine), Math.Max(0, (ce.Top - 2) * EdgeFine), Math.Min(EdgeFineW, (ce.Right + 2) * EdgeFine), Math.Min(EdgeFineH, (ce.Bottom + 2) * EdgeFine));
                    }
                    tick = Lap(0, tick);
                    affected = ViewChanged(tintedSoft, tintedEdge, edgeFine, fine);
                    tick = Lap(1, tick);
                    for (int y = affected.Top; y < affected.Bottom; y++) rowState[y] &= 5;   // (the bare glass there is no longer as kept)
                }
                else
                {
                    if (Fine == 1) Tint(soft, tintedSoft, 0, soft.Length);
                    else
                    {
                        // (tinted at its own size, which is cheaper, then enlarged: see Fine)
                        Tint(soft, tintedCoarse, 0, soft.Length);
                        Enlarge(tintedCoarse, BackW, BackH, Fine, fineTap, fineWeight, fineRow, tintedSoft, 0, BackH);
                    }
                    if (EdgeFine == 1) Tint(edge, tintedEdge, 0, edge.Length);
                    else
                    {
                        Tint(edge, tintedEdgeCoarse, 0, edge.Length);
                        Enlarge(tintedEdgeCoarse, EdgeW, EdgeH, EdgeFine, edgeTap, edgeWeight, fineRow, tintedEdge, 0, EdgeH);
                    }
                    tick = Lap(0, tick);
                    ViewAll(tintedSoft, tintedEdge);   // (moving: the cells it does not move are needed as built too)
                    if (Motion.IsNeutral) lightNow = bzLight; else MotionView(tintedSoft, tintedEdge);
                    drawnMotion = Motion;
                    tick = Lap(1, tick);
                    affected = new Rectangle(0, 0, W, H);
                    whole = true;
                    LastKind = 3;
                    for (int y = 0; y < H; y++) rowState[y] = 0;
                }
                tintedFromSoft = soft;
                tintedFromEdge = edge;
                tintedVersion = backdropVersion;
                tintedAppearance = Appearance;
                tintedEven = Even;
                viewValid = true;
            }

            // 1b. Only the glass moved (see Motion): the bent cells and the bezel's light again, and the bezel drawn again
            //     (the flat face is as it was). Back at neutral, the view as built: the static glass to the last pixel.
            bool keepFace = false;
            if (!stale && !Motion.Same(drawnMotion))
            {
                if (Motion.IsNeutral) { ViewAll(tintedSoft, tintedEdge); lightNow = bzLight; } else MotionView(tintedSoft, tintedEdge);
                drawnMotion = Motion;
                affected = new Rectangle(0, 0, W, H);
                keepFace = !Motion.IsNeutral;   // (the settling frame draws the face too: a whole frame on the way may have drawn it from moving cells)
                LastKind = 4;
                tick = Lap(1, tick);
            }

            // 2. The contents: what of them changed (and the underlay under them, brought up to date).
            int flip = (int)(Appearance * 256);
            int contentShift = cardLeft - Margin, contentDrop = cardTop - Margin;
            bool haloOn = Underlay > 0 && content != null;
            int darkUnder = 0, lightUnder = 0;
            Rectangle contentChanged = Rectangle.Empty;
            if (content != null)
            {
                contentChanged = UpdateHalo(content, contentW, contentH, contentShift, contentDrop, fromRow, toRow, haloOn);
                if (haloOn)
                {
                    int under = (int)(Math.Min(1f, Underlay) * 256);
                    darkUnder = under * (256 - flip) >> 8;
                    lightUnder = under * flip >> 8;
                }
            }
            byte[] haloMap = haloOn ? halo : null;
            tick = Lap(2, tick);

            // 3. What has to be drawn: rows of dst that do not hold the current picture, in full; the
            //    pixels the backdrop's change reached; and the pixels under changed contents.
            Rectangle done = Rectangle.Empty;
            int rows0 = fromRow, rows1 = toRow;
            // (rows not yet drawn into dst at all, or whose glass is not known as a whole: whole rows)
            int wholeFrom = -1, wholeTo = -1;
            for (int y = rows0; y < rows1; y++)
            {
                if ((rowState[y] & 5) == 5) continue;
                if (wholeFrom < 0) wholeFrom = y;
                wholeTo = y + 1;
            }
            if (wholeFrom >= 0)
            {
                // (everything from the first such row to the last: simpler than many pieces, and nearly always all of them)
                Compose(wholeFrom, wholeTo, 0, W, content, contentW, contentH, contentShift, contentDrop, flip, haloMap, darkUnder, lightUnder, dst, true, false);
                done = Rectangle.FromLTRB(0, wholeFrom, W, wholeTo);
            }
            if (!whole && !affected.IsEmpty)
            {
                Rectangle part = Rectangle.Intersect(affected, Rectangle.FromLTRB(0, rows0, W, rows1));
                if (!part.IsEmpty && !done.Contains(part))
                {
                    Compose(part.Top, part.Bottom, part.Left, part.Right, content, contentW, contentH, contentShift, contentDrop, flip, haloMap, darkUnder, lightUnder, dst, false, keepFace);
                    done = done.IsEmpty ? part : Rectangle.Union(done, part);
                }
            }
            tick = Lap(3, tick);
            if (content != null && !contentChanged.IsEmpty)
            {
                Rectangle part = Rectangle.Intersect(contentChanged, Rectangle.FromLTRB(0, rows0, W, rows1));
                if (!part.IsEmpty && (keepFace || !done.Contains(part)))   // (a moving frame left most of the face as it was: its contents are laid on here)
                {
                    Rectangle did = ContentsOnly(part, content, contentW, contentH, contentShift, contentDrop, flip, haloMap, darkUnder, lightUnder, dst);
                    if (!did.IsEmpty) done = done.IsEmpty ? did : Rectangle.Union(done, did);
                }
            }
            Lap(4, tick);
            return done;
        }

        static long Lap(int phase, long since)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            PhaseTicks[phase] += now - since;
            return now;
        }

        // Draws rows y0 to y1, columns x0 to x1 of the glass from the view: its colour kept in
        // glassColour, and with the contents on it into dst. wholeRows: every column of the glass on
        // those rows is drawn (so the rows then hold the current picture, and the glass).
        void Compose(int y0, int y1, int x0, int x1, int[] content, int contentW, int contentH, int shift, int drop, int flip, byte[] haloMap, int darkUnder, int lightUnder, int[] dst, bool wholeRows, bool keepFace)
        {
            int w = W, vw = VW;
            int[] glass = glassColour, v = view;
            uint[] vr = vrow;
            for (int y = y0; y < y1; y++)
            {
                int from = Math.Max(spanFrom[y], x0), to = Math.Min(spanTo[y], x1);
                if (wholeRows) rowState[y] |= 5;   // (the glass and the picture are current on this row)
                rowState[y] &= 5;                  // (the bare glass kept for it is not, if the glass changed: see MakeFinalRow)
                if (to <= from) continue;

                // the two cell rows this pixel row lies between, blended for the cells it reads
                int vy = rowV[y];
                uint down = (uint)rowVMix[y], up = 256 - down;
                int ca = colV[from], cb = Math.Min(vw - 1, colV[to - 1] + 1);
                int ra = vy * vw, rb = (vy + 1 < VH ? vy + 1 : vy) * vw;
                if (down == 0) for (int c = ca; c <= cb; c++) vr[c] = (uint)v[ra + c];
                else
                {
                    for (int c = ca; c <= cb; c++)
                    {
                        uint a = (uint)v[ra + c], b = (uint)v[rb + c];
                        vr[c] = ((((a & 0xFF00FF) * up + (b & 0xFF00FF) * down) >> 8) & 0xFF00FF) | ((((a & 0x00FF00) * up + (b & 0x00FF00) * down) >> 8) & 0x00FF00);
                    }
                }

                int row = y * w, glow = rowGlow[y];
                int ff = faceFrom[y], ft = faceTo[y];
                int contentY = y + drop;
                int contentRow = content != null && contentY >= 0 && contentY < contentH ? contentY * contentW : -1;

                // the bezel's left run
                int e = Math.Min(to, ff), i = rowBz[y] + (from - spanFrom[y]);
                for (int x = from; x < e; x++, i++) BezelPixel(x, i, row, vr, contentRow, contentW, shift, flip, haloMap, darkUnder, lightUnder, content, glass, dst, glow);
                // the face
                // (keepFace: only the face pixels whose cells the glass moving changed; the rest is as it was)
                bool someFace = keepFace && moveCell != null && ViewStep == 1;
                int cellRow = vy * VW;
                for (int x = Math.Max(from, ff), end = Math.Min(to, ft); x < end; x++)
                {
                    if (someFace && moveCell[cellRow + x] == 0) continue;
                    int c = colV[x];
                    uint f = (uint)colVMix[x], p0 = vr[c], p1 = vr[c + 1], g = 256 - f;
                    uint rbb = (((p0 & 0xFF00FF) * g + (p1 & 0xFF00FF) * f) >> 8) & 0xFF00FF, gg = (((p0 & 0x00FF00) * g + (p1 & 0x00FF00) * f) >> 8) & 0x00FF00;
                    int R = (int)(rbb >> 16) + glow, G = (int)(gg >> 8) + glow, B = (int)(rbb & 255) + glow;
                    glass[row + x] = (R << 20) | (G << 10) | B;   // (at most 255 and a glow of a few: well within ten bits)
                    if (haloMap != null)
                    {
                        int hv = haloMap[row + x];
                        if (hv != 0) Under(hv, darkUnder, lightUnder, ref R, ref G, ref B);
                    }
                    if (contentRow >= 0)
                    {
                        int cx = x + shift;
                        if (cx >= 0 && cx < contentW) Ink(content[contentRow + cx], flip, ref R, ref G, ref B);
                    }
                    if (R > 255) R = 255;
                    if (G > 255) G = 255;
                    if (B > 255) B = 255;
                    dst[row + x] = unchecked((int)0xFF000000) | (R << 16) | (G << 8) | B;
                }
                // the bezel's right run
                int start = Math.Max(from, ft);
                i = rowBz[y] + (ff - spanFrom[y]) + (start - ft);
                for (int x = start; x < to; x++, i++) BezelPixel(x, i, row, vr, contentRow, contentW, shift, flip, haloMap, darkUnder, lightUnder, content, glass, dst, glow);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void BezelPixel(int x, int i, int row, uint[] vr, int contentRow, int contentW, int shift, int flip, byte[] haloMap, int darkUnder, int lightUnder, int[] content, int[] glass, int[] dst, int glow)
        {
            int c = colV[x];
            uint f = (uint)colVMix[x], p0 = vr[c], p1 = vr[c + 1], g = 256 - f;
            uint rbb = (((p0 & 0xFF00FF) * g + (p1 & 0xFF00FF) * f) >> 8) & 0xFF00FF, gg = (((p0 & 0x00FF00) * g + (p1 & 0x00FF00) * f) >> 8) & 0x00FF00;
            int lv = lightNow[i], gain = (int)((uint)lv >> 16), lt = lv & 0xFFFF;
            int R, G, B;
            if (linearLight)
            {
                // (in linear light: see linearLight)
                int[] dec = LinearDecode;
                byte[] enc = LinearEncode;
                R = (int)(rbb >> 16) + glow; G = (int)(gg >> 8) + glow; B = (int)(rbb & 255) + glow;
                if (R > 255) R = 255;
                if (G > 255) G = 255;
                if (B > 255) B = 255;
                int lr = (dec[R] * gain) >> 8, lg = (dec[G] * gain) >> 8, lb = (dec[B] * gain) >> 8;
                R = enc[lr + (((4096 - lr) * lt) >> 12)];
                G = enc[lg + (((4096 - lg) * lt) >> 12)];
                B = enc[lb + (((4096 - lb) * lt) >> 12)];
            }
            else
            {
                R = (((int)(rbb >> 16) * gain) >> 8) + lt; G = (((int)(gg >> 8) * gain) >> 8) + lt; B = (((int)(rbb & 255) * gain) >> 8) + lt;
                if (R > 1023) R = 1023;
                if (G > 1023) G = 1023;
                if (B > 1023) B = 1023;
            }
            glass[row + x] = (R << 20) | (G << 10) | B;
            int mix = bzMix[i], A = mix & 255;
            if (haloMap != null && A == 255)   // (not on the outermost, partly covered pixels: nor are the contents)
            {
                int hv = haloMap[row + x];
                if (hv != 0) Under(hv, darkUnder, lightUnder, ref R, ref G, ref B);
            }
            if (contentRow >= 0)
            {
                int cx = x + shift;
                if (cx >= 0 && cx < contentW) Ink(content[contentRow + cx], flip, ref R, ref G, ref B);
            }
            if (R > 255) R = 255;
            if (G > 255) G = 255;
            if (B > 255) B = 255;
            if (A == 255) dst[row + x] = unchecked((int)0xFF000000) | (R << 16) | (G << 8) | B;
            else dst[row + x] = Edge(R, G, B, A, (mix >> 8) & 255);
        }

        // An outermost pixel: the glass over what is under it (the shadow and hairline, as alpha
        // over black), by how much of the pixel the glass covers.
        // (Windows lays the picture over the screen in sRGB, premultiplied: out = ours + screen x
        // (1 - alpha). A coverage worked in linear light cannot be expressed in that form for every
        // screen behind, so the edge is blended as Windows will blend it.)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Edge(int R, int G, int B, int A, int under)
        {
            return ((A + under * (255 - A) / 255) << 24) | ((R * A / 255) << 16) | ((G * A / 255) << 8) | (B * A / 255);
        }

        // Lays one pixel of the pop-up's contents (premultiplied) over the glass colour R, G, B.
        // flip (0-256): how far its grey ink (text, lines, symbols; not colours) is turned from
        // light to dark, for light glass (see Appearance).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Ink(int c, int flip, ref int R, ref int G, ref int B)
        {
            int ca = (c >> 24) & 255;
            if (ca == 0) return;
            int cr = (c >> 16) & 255, cg = (c >> 8) & 255, cb = c & 255;
            if (flip != 0 && (ca & 1) == 0)   // (an odd alpha marks the white or grey of an icon: see GlassOverlay.MarkInk)
            {
                int max = cr > cg ? (cr > cb ? cr : cb) : (cg > cb ? cg : cb), min = cr < cg ? (cr < cb ? cr : cb) : (cg < cb ? cg : cb);
                if (max - min < 40)
                {
                    cr += ((ca - 2 * cr) * flip) >> 8;
                    cg += ((ca - 2 * cg) * flip) >> 8;
                    cb += ((ca - 2 * cb) * flip) >> 8;
                    if (cr < 0) cr = 0; else if (cr > ca) cr = ca;
                    if (cg < 0) cg = 0; else if (cg > ca) cg = ca;
                    if (cb < 0) cb = 0; else if (cb > ca) cb = ca;
                }
            }
            int keep = 255 - ca;
            R = R * keep / 255 + cr;
            G = G * keep / 255 + cg;
            B = B * keep / 255 + cb;
        }

        // The finished picture of the bare glass on row y (see finalPx), from glassColour.
        void MakeFinalRow(int y)
        {
            int w = W, row = y * w;
            PrimeInto(finalPx, primedGain, y, y + 1);
            int[] glass = glassColour, fin = finalPx;
            int b = rowBz[y];
            for (int x = spanFrom[y]; x < faceFrom[y]; x++, b++) fin[row + x] = Bare(glass[row + x], bzMix[b]);
            for (int x = faceFrom[y]; x < faceTo[y]; x++) fin[row + x] = Bare(glass[row + x], 255);
            for (int x = faceTo[y]; x < spanTo[y]; x++, b++) fin[row + x] = Bare(glass[row + x], bzMix[b]);
            rowState[y] |= 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Bare(int g, int mix)
        {
            int R = (g >> 20) & 1023, G = (g >> 10) & 1023, B = g & 1023, A = mix & 255;
            if (R > 255) R = 255;
            if (G > 255) G = 255;
            if (B > 255) B = 255;
            if (A == 255) return unchecked((int)0xFF000000) | (R << 16) | (G << 8) | B;
            return Edge(R, G, B, A, (mix >> 8) & 255);
        }

        // Only the contents have changed within 'part': the bare glass copied there, and the
        // contents laid over it wherever there are any (or any of their underlay). Returns what was
        // done. (The outermost, partly covered pixels of the outline never have contents on them:
        // Windows' own border is left out of the contents.)
        Rectangle ContentsOnly(Rectangle part, int[] content, int contentW, int contentH, int shift, int drop, int flip, byte[] haloMap, int darkUnder, int lightUnder, int[] dst)
        {
            int w = W, x0 = Math.Max(part.Left, Margin), x1 = Math.Min(part.Right, Margin + CardW);
            int xa = Math.Max(x0, -shift), xb = Math.Min(x1, contentW - shift);
            if (xb <= xa) return Rectangle.Empty;
            int[] glass = glassColour, fin = finalPx;
            for (int y = part.Top; y < part.Bottom; y++)
            {
                int row = y * w;
                if ((rowState[y] & 2) == 0) MakeFinalRow(y);
                Array.Copy(fin, row + xa, dst, row + xa, xb - xa);
                int contentY = y + drop;
                bool inRow = contentY >= 0 && contentY < contentH;
                bool haloHere = haloMap != null && haloRowAny[y] != 0;
                if (!inRow && !haloHere) continue;
                int crow = inRow ? contentY * contentW + shift : 0;
                for (int x = xa; x < xb; x++)
                {
                    int c = inRow ? content[crow + x] : 0, hv = haloHere ? haloMap[row + x] : 0;
                    if ((c == 0 && hv == 0) || coverMap[row + x] != 255) continue;
                    int g = glass[row + x], R = (g >> 20) & 1023, G = (g >> 10) & 1023, B = g & 1023;
                    if (hv != 0) Under(hv, darkUnder, lightUnder, ref R, ref G, ref B);
                    Ink(c, flip, ref R, ref G, ref B);
                    if (R > 255) R = 255;
                    if (G > 255) G = 255;
                    if (B > 255) B = 255;
                    dst[row + x] = unchecked((int)0xFF000000) | (R << 16) | (G << 8) | B;
                }
            }
            return Rectangle.FromLTRB(xa, part.Top, xb, part.Bottom);
        }
    }
}
