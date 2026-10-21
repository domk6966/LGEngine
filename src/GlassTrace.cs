using System;

namespace LGEngine
{
    // The ray-traced glass (GlassOptions.RayTrace): worked out once, when a map is built, and baked into
    // the same per-pixel tables the old builder fills, so a frame costs what it always did.
    //
    // The glass is a real slab floating above the screen. Its top surface is a height field over the
    // outline: flat on the face, then a rounded shoulder across the bezel (height Rise x the bezel's
    // width, profile (1 - t^P)^(1/P), t = 0 where the face ends, 1 at the outline), on a flat slab
    // Slab x the bezel's width thick; its bottom is flat, Gap x the bezel's width above the screen
    // (the Elevation slider: 0 lying on it, twice the gap at 100). The view is orthographic, straight
    // down. A ray hitting the shoulder at distance d in from the outline meets a surface tilted by
    // theta towards the outside; it is partly reflected (Fresnel, Schlick, IOR 1.5), partly refracted
    // in (Snell), crosses the glass leaning inwards, leaves through the flat bottom (refracted again,
    // reflected back partly, and wholly beyond the critical angle: total internal reflection), and
    // crosses the gap to the screen. Because the surface depends only on the distance to the outline and
    // the view is straight down, every ray stays in the plane through the outline's normal: the whole
    // trace is one-dimensional, done once per step of t (Steps of them), per colour (red and blue at a
    // little lower and higher IOR: dispersion). In two dimensions a pixel lands at its own place moved
    // inwards along the normal by the traced offset.
    //
    // Anti-aliasing is exact rather than sampled: each per-step quantity is kept as a running sum, so the
    // average over a pixel's footprint (the stretch of t one pixel covers across the outline) is two
    // look-ups. That is the limit of supersampling the pixel in the direction everything changes in
    // (along the outline nothing does), for the landing places, the light and the coverage alike; and
    // the spread of the landing places within the footprint is the spread of the rays, which sets how
    // frosted that pixel's view is (rays fanning out see a blur, not grit).
    //
    // Controlled lensing (Apple's glass shows no mirrored second copy at the rim): the landing place is
    // kept from crossing past the middle and from turning back on itself going outwards (it creeps on
    // instead); the true rays' spread still sets the frost, so what the rim draws out reads as a soft,
    // smooth streak.
    //
    // The light: the reflected ray sees a soft studio, in linear light: a sky with a bright horizon, a
    // large soft box above and to the left, and a floor below, brighter on the light's side. Near the
    // outline the surface is seen at grazing angles, the reflection rises steeply (Fresnel) and the
    // reflected rays look down at the floor: a crisp, bright rim, strongest towards the light. The rays
    // reflected back inside at the bottom (rising to total reflection right at the edge) see the light
    // piped through the slab from the far side: a fainter rim opposite, and the darkening or brightening
    // band of thick glass just inside the outline. A little light scattered into the frost from the rim
    // gives a soft milky band inside it. The light is laid on in linear light (see GlassMap.LinearDecode)
    // as a screen blend with a soft roll-off, so it never clips and all but vanishes over white.
    internal sealed class GlassTrace
    {
        public const int Steps = 4096;
        internal const float Ior = 1.5f, R0 = 0.04f;

        // Studio (linear radiance, white = 1) and the materials' fixed numbers. (Tunable: see LGBench -ab.)
        internal static float SkyZenith = 0.12f, SkyHorizon = 0.14f;
        internal static float Fillet = 3f;          // the edge radius's width, in points
        internal static float FloorLight = 3f, FloorLeast = 0.3f, FloorFrom = 0.5f;
        internal static float KeyLight = 0.35f, KeyElevation = 32f, KeySize = 22f, KeyWidth = 0.5f;
        internal static float PipeLight = 0.9f, PipeLeast = 0.3f, PipeAway = 0.65f, PipeView = 0.6f;   // (the light piped round the slab: how bright, at the sides, on the far side; and the share of it that is the screen behind, not the studio)
        internal static float MilkLight = 0.025f, MilkDepth = 8f, MilkPower = 1.6f;
        internal static float DispersionIor = 0.75f;   // the red-blue IOR difference, per unit of the shape's Dispersion
        internal static float SpreadFrost = 1f;        // how much the rays' spread frosts the view
        internal static float FoldFrost = 0.35f;       // how much a landing kept from folding back frosts it, per pixel it was moved
        internal static float FloorGlint = 1f;         // the floor's bright patch straight towards the light (a glint where the outline faces it)

        // Per step, interleaved (S of them): the transmitted weight; the landing offsets weighted by it (red, green,
        // blue: kept from folding back; green as traced, and its square); the light's parts (sky, floor, soft box,
        // piped, milky). Kept as running sums: (Steps + 1) x S.
        const int S = 12;   // (0-5: the weight and the landings, for Land; 6-11: the weight again and the light, for Light)
        readonly double[] sums;
        readonly double[] fa = new double[S], fb = new double[S];
        // The light's dependence on which way the outline faces (FacingSteps + 1 entries of 4: floor, piped, soft box,
        // milky), and how deep in (as t) nothing of the light is left and all of the view comes through.
        const int FacingSteps = 2048;
        readonly float[] facingTab;
        readonly float quietBelow;
        readonly float bezel, faceSky, spec, glow, strength;   // (spec, glow: the shape's own gains for the reflected and piped light, and the milky band; strength: the Rim slider's)
        public readonly float LandMost;   // the furthest any (kept) landing is moved, in pixels
        internal readonly TraceGeo Geo;   // the slab's shape
        internal readonly float[] KeptG;  // per step: the green ray's landing after the controlled lensing (pixels)
        public readonly float TirWidth;   // the width of the band (pixels across the outline) where the green ray is totally reflected at the underside

        public GlassTrace(float bezelW, float power, float rise, float slab, float gap, float dispersion, float rv, float magnify, float creep, float k, float specGain, float glowGain, float lightStrength)
            : this(new TraceGeo(Math.Max(1f, bezelW), power, rise, slab, gap, k), dispersion, rv, magnify, creep, k, specGain, glowGain, lightStrength, true)
        {
        }

        // The same, the top and the underside drawn by a GlassProfile (its numbers are bezel widths at the default
        // thickness; 'thick' scales them as the Refraction and Intensity sliders do; 'gap' is already for the Elevation).
        public GlassTrace(float bezelW, GlassProfile profile, float thick, float gap, float dispersion, float rv, float magnify, float creep, float k, float specGain, float glowGain, float lightStrength)
            : this(new TraceGeo(Math.Max(1f, bezelW), profile, thick, gap, k), dispersion, rv, magnify, creep, k, specGain, glowGain, lightStrength, true)
        {
        }

        // (full false: the geometry and the landings only, none of the light: what GlassSideView shows)
        internal GlassTrace(TraceGeo geo, float dispersion, float rv, float magnify, float creep, float k, float specGain, float glowGain, float lightStrength, bool full)
        {
            Geo = geo;
            bezel = geo.B;
            spec = specGain;
            glow = glowGain;
            strength = lightStrength;
            float B = bezel;
            int n = Steps;
            float[] lr = new float[n], lg = new float[n], lb = new float[n], raw = new float[n], w = new float[n];
            float[] sky = null, floorW = null, key = null, pipe = null, milk = null;
            if (full) { sky = new float[n]; floorW = new float[n]; key = new float[n]; pipe = new float[n]; milk = new float[n]; }
            float dIor = DispersionIor * dispersion;
            float[] iors = { Ior - dIor, Ior, Ior + dIor };
            double keyEl = KeyElevation * Math.PI / 180, keySize = KeySize * Math.PI / 180;
            float rawMost = 2 * rv;
            double milkFade = MilkPower / Math.Max(0.5f, MilkDepth * k);
            int tirCells = 0;
            for (int i = 0; i < n; i++)
            {
                // The ray entering at this step meets a surface tilted by theta towards the outside (the shoulder, then the edge
                // radius); see TraceGeo for the rest of its way.
                double t = (i + 0.5) / n, d = (1 - t) * B;
                double theta = geo.Theta[i];
                double ct = Math.Cos(theta), st = Math.Sin(theta), q = 1 - ct;
                double rTop = R0 + (1 - R0) * q * q * q * q * q;
                double tirG = R0, wG = (1 - rTop) * (1 - R0);
                for (int c = 0; c < 3; c++)
                {
                    if (theta < 1e-6) break;   // (straight through: no offset)
                    RayInfo ri;
                    geo.Trace(i, iors[c], rawMost, out ri);
                    if (c == 0) lr[i] = (float)ri.Land; else if (c == 1) lg[i] = (float)ri.Land; else lb[i] = (float)ri.Land;
                    if (c == 1)
                    {
                        tirG = ri.RBot; wG = (1 - rTop) * (1 - ri.RBot);
                        if (ri.Tir) tirCells++;
                    }
                }
                raw[i] = lg[i];
                w[i] = (float)wG + 1e-3f;   // (a trace of weight even in total reflection, so a pixel wholly in it still has a place)
                if (!full) continue;
                pipe[i] = (float)((1 - rTop) * tirG);
                // the reflected ray's elevation (above the horizon): 90 degrees - 2 theta
                double e = Math.PI / 2 - 2 * theta, se = Math.Cos(2 * theta);
                double skyHere, floorHere;
                if (e >= 0) { double u = 1 - se; skyHere = SkyZenith + (SkyHorizon - SkyZenith) * u * u; floorHere = 0; }
                else
                {
                    // below the horizon: dim, but bright straight below (the lit floor under the glass, and the screen)
                    double u2 = (1 + se) * (1 + se), f = (-se - FloorFrom) / (1 - FloorFrom);
                    skyHere = SkyHorizon * u2 * u2;
                    floorHere = f <= 0 ? 0 : f * f * (3 - 2 * f);
                }
                double ke = (e - keyEl) / keySize;
                sky[i] = (float)(rTop * skyHere);
                floorW[i] = (float)(rTop * floorHere);
                key[i] = (float)(rTop * Math.Exp(-ke * ke));
                milk[i] = (float)Math.Exp(-d * milkFade);
            }
            TirWidth = tirCells * B / n;
            faceSky = R0 * SkyZenith;

            // Controlled lensing: each colour's landing kept short of the middle, and never turning back
            // going outwards (it creeps on at least 'creep' as fast as the outline).
            float most = 0;
            foreach (float[] l in new float[][] { lr, lg, lb })
            {
                float lookedAt = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    float t = (i + 0.5f) / n, depth = (1 - t) * B;
                    float cap = 0.9f * (rv - depth) / magnify;
                    float v = l[i];
                    if (v > cap) v = Math.Max(0, cap);
                    float look = depth + v, allowed = lookedAt - creep * B / n;
                    if (i > 0 && look > allowed) { look = allowed; v = look - depth; }
                    lookedAt = look;
                    l[i] = v;
                    if (v > most) most = v;
                }
            }
            LandMost = most;

            KeptG = lg;

            if (full)
            {
                // the facing table
                facingTab = new float[(FacingSteps + 1) * 4];
                float mostFloor = 0, mostPipe = 0, mostKey = 0, mostMilk = 0;
                for (int j = 0; j <= FacingSteps; j++)
                {
                    double facing = -1 + 2.0 * j / FacingSteps;
                    double toward = facing > 0 ? facing : 0, away = facing < 0 ? -facing : 0;
                    double t2 = toward * toward, t8 = t2 * t2 * t2 * t2;
                    // the floor brighter towards the light (and a bright patch straight towards it); the light piped
                    // round the slab brighter there and on the far side; the soft box by how far round from the light
                    double floorLit = FloorLight * (FloorLeast + (1 - FloorLeast) * toward * Math.Sqrt(toward) + FloorGlint * t8);
                    double pipeLit = (1 - PipeView) * PipeLight * (PipeLeast + (1 - PipeLeast) * (toward * Math.Sqrt(toward) + PipeAway * away * Math.Sqrt(away)));
                    double az = Math.Acos(Math.Max(-1, Math.Min(1, facing))) / (Math.PI * KeyWidth / 2);
                    double keyLit = KeyLight * Math.Exp(-az * az);
                    double milkLit = MilkLight * (GlassMap.LightLeast + (1 - GlassMap.LightLeast) * (Math.Sqrt(toward) * toward + GlassMap.AwayShare * Math.Sqrt(away) * away));
                    facingTab[j * 4] = (float)floorLit; facingTab[j * 4 + 1] = (float)pipeLit; facingTab[j * 4 + 2] = (float)keyLit; facingTab[j * 4 + 3] = (float)milkLit;
                    mostFloor = Math.Max(mostFloor, (float)floorLit); mostPipe = Math.Max(mostPipe, (float)pipeLit); mostKey = Math.Max(mostKey, (float)keyLit); mostMilk = Math.Max(mostMilk, (float)milkLit);
                }

                // the running sums, and how deep in nothing of the light is left
                sums = new double[(n + 1) * S];
                double[] run = new double[S];
                float wFace = (1 - R0) * (1 - R0);
                int quiet = -1;
                for (int i = 0; i < n; i++)
                {
                    double wi = w[i];
                    run[0] += wi; run[1] += lr[i] * wi; run[2] += lg[i] * wi; run[3] += lb[i] * wi; run[4] += raw[i] * wi; run[5] += raw[i] * (double)raw[i] * wi;
                    run[6] += wi;
                    run[7] += sky[i]; run[8] += floorW[i]; run[9] += key[i]; run[10] += pipe[i]; run[11] += milk[i];
                    Array.Copy(run, 0, sums, (i + 1) * S, S);
                    double lit = ((sky[i] - faceSky) + floorW[i] * mostFloor + key[i] * mostKey) * spec + milk[i] * mostMilk * glow + (pipe[i] - facePipe) * mostPipe * spec;
                    bool still = Math.Abs(lit * strength) < 0.25 / 4096 && Math.Abs((w[i] + PipeView * (pipe[i] - facePipe)) / wFace - 1) < 0.25 / 256;
                    if (still && quiet == i - 1) quiet = i;
                }
                quietBelow = (quiet + 1) / (float)n;
            }
        }

        // The cross-section and the paths of 'rays' rays through it, for the editor (see GlassSideView): read from the very
        // tables the bake is made of.
        internal GlassSideView SideView(GlassLook look, int cardW, int cardH, GlassShape shape, int rays)
        {
            return SideViewMaker.Make(this, look, cardW, cardH, shape, rays);
        }

        // The running sums at t (all S of them, into dst), between steps as the steps' values are spread evenly.
        void At(float t, double[] dst, int from)
        {
            float x = t * Steps;
            if (x <= 0) { Array.Clear(dst, 0, 6); return; }
            if (x >= Steps) { Array.Copy(sums, Steps * S + from, dst, 0, 6); return; }
            int i = (int)x, o = i * S + from;
            double f = x - i;
            for (int j = 0; j < 6; j++) dst[j] = sums[o + j] + f * (sums[o + S + j] - sums[o + j]);
        }

        // The footprint of a pixel at t (half a pixel each way across the outline), within the glass, and the
        // running sums at its two ends (into fa, fb).
        void Footprint(float t, out float a, out float b, int from, float halfPix)
        {
            float hw = halfPix / bezel;
            a = t - hw;
            b = t + hw;
            if (a < 0) a = 0;
            if (b > 1) b = 1;
            if (b - a < 1e-5f)
            {
                float m = Math.Max(0f, Math.Min(1f, t));
                a = Math.Max(0f, m - 1e-5f);
                b = a + 1e-5f;
            }
            At(a, fa, from);
            At(b, fb, from);
        }

        // Where a pixel at t looks, on average over its footprint: how far in along the normal for red,
        // green and blue (pixels), and the spread of the rays there (pixels).
        public void Land(float t, out float lr, out float lg, out float lb, out float spread)
        {
            float a, b;
            Footprint(t, out a, out b, 0, 0.5f);
            double w = fb[0] - fa[0];
            lr = (float)((fb[1] - fa[1]) / w);
            lg = (float)((fb[2] - fa[2]) / w);
            lb = (float)((fb[3] - fa[3]) / w);
            double m = (fb[4] - fa[4]) / w, m2 = (fb[5] - fa[5]) / w;
            // (and where the landing was kept from folding back, the rays it stands for fan out by how far it was moved)
            double moved = FoldFrost * (m - lg);
            spread = (float)Math.Sqrt(Math.Max(0, m2 - m * m) + moved * moved) * SpreadFrost;
        }

        // The light on a pixel depth pixels in from the outline, its normal facing the light by 'facing'
        // (-1 away, 1 squarely): how much of the view it keeps (x 256, of the face's) and the light laid
        // on it (linear, rolled off: 0 to 4096).
        public void Light(float depth, float facing, out int gain, out int add)
        {
            Light(depth, facing, 0.5f, out gain, out add);
        }

        // (halfPix: half the pixel's extent across the edge, in pixels; 0.5 = one pixel)
        public void Light(float depth, float facing, float halfPix, out int gain, out int add)
        {
            float t = 1 - depth / bezel;
            if (t + halfPix / bezel <= quietBelow) { gain = 256; add = 0; return; }
            float a, b;
            Footprint(t, out a, out b, 6, halfPix);
            double span = (b - a) * Steps;
            double piped = (fb[4] - fa[4]) / span - facePipe;
            double keep = ((fb[0] - fa[0]) / span + PipeView * piped) / ((1 - R0) * (1 - R0));
            int j = (int)((Math.Max(-1f, Math.Min(1f, facing)) + 1) * (FacingSteps / 2) + 0.5f) * 4;
            float[] ft = facingTab;
            // (each part beyond what the flat face itself shows, so face and bezel meet seamlessly)
            double light = (((fb[1] - fa[1]) + (fb[2] - fa[2]) * ft[j] + (fb[3] - fa[3]) * ft[j + 2]) / span - faceSky) * spec
                + (fb[5] - fa[5]) / span * ft[j + 3] * glow + piped * ft[j + 1] * spec;
            light *= strength;
            if (light < 0) light = 0;
            double rolled = 1 - Math.Exp(-light);
            gain = (int)(Math.Min(1.0, keep) * 256 + 0.5);
            add = (int)(rolled * 4096 + 0.5);
            if (add > 4095) add = 4095;
        }
        const float facePipe = (1 - R0) * R0;   // (the bottom's own reflection on the face)

        // (for tuning: the profile at a few depths, in pixels from the outline)
        internal string Describe()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (float d in new float[] { 0.5f, 1.5f, 2.5f, 4f, 6f, 9f, 13f, 20f, 30f, 45f, 70f, 100f })
            {
                if (d > bezel) break;
                float lr, lg, lb, sp;
                Land(1 - d / bezel, out lr, out lg, out lb, out sp);
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "d{0,5:0.0}: land {1,6:0.0} (r-b {2,4:0.0}) spread {3,5:0.0} |", d, lg, lr - lb, sp);
                foreach (float f in new float[] { 0.9f, 0f, -0.9f })
                {
                    int g, a;
                    Light(d, f, out g, out a);
                    sb.AppendFormat(" keep {0,3} light {1,4}", g, a);
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
