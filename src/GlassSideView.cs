using System;
using System.Drawing;

namespace LGEngine
{
    // One view ray through the cross-section. Points are in points (at 100% scaling): x across the bezel from where the flat
    // face ends (0) to the outline (BezelPt), y up from the screen.
    public sealed class SideRay
    {
        public float T;                   // where it enters: 0 at the face's end, 1 at the outline
        public PointF Entry;              // on the top surface
        public PointF Hit;                // on the underside, where it leaves the glass (or is reflected back)
        public PointF Screen;             // where it reaches the screen as traced (not set in total reflection)
        public PointF Kept;               // where the engine samples that pixel's view, on the screen, after its controlled lensing
        public bool Tir;                  // totally reflected at the underside
        public float ThetaDeg, PhiDeg;    // the tilt of the top where it enters, of the underside where it hits it
        public float Reflectance;         // the top surface's reflection (Fresnel): the amber ray's strength, 0..1
        public float ReflectDx, ReflectDy;   // the direction the top reflects it, unit: x towards the outline, y up
        public float TirDx, TirDy;        // the direction the underside reflects it in total reflection (same sense)
        public float RawLandPt, KeptLandPt;   // how far in from the entry it lands, as traced and as kept
    }

    // The engine's own cross-section of a piece of glass and the paths of rays through it, for an editor to draw. It comes from
    // the very ray trace the map is baked from (GlassMap.BuildTrace, GlassTrace, TraceGeo), without the light.
    public sealed class GlassSideView
    {
        public PointF[] Top, Bottom;        // the two surfaces from the face's end (t = 0) to the outline (t = 1)
        public SideRay[] Rays;
        public float BezelPt, GapPt;        // the bezel's width and the gap between the flat bottom and the screen
        public float FaceThicknessPt;       // the glass under the face
        public float RimThicknessPt;        // and at the outline, where the underside rises to meet the top
        public float Thick;                 // the thickness scale of the Refraction and Intensity sliders
        public float TirBandPt;             // how wide, across the outline, the rays are totally reflected at the underside
        public float BendAtRimPt;           // how far the view is drawn in at the rim: the landing of the ray 12% of the bezel in
        public float BendMaxPt;             // the furthest any pixel's view is drawn in
        public bool Ring;                   // the total-reflection band is wide enough to read as a silver ring round the glass

        // A band wider than this share of the bezel (and than RingLeastPt) is a ring; the usual edge radius alone makes a thin one.
        public const float RingShare = 0.06f, RingLeastPt = 2.5f;

        // The bezel's width in points for a glass of this size (at 100% scaling).
        public static float BezelFor(GlassShape shape, GlassLook look, int cardWPt, int cardHPt)
        {
            float elev = (look.Elevation - GlassLook.DefaultElevation) / 50f;
            return Math.Max(1f, GlassMap.BevelFor(cardWPt, cardHPt, 1f, elev, shape));
        }

        // The cross-section for this shape (with its Profile, or the analytic shoulder if none) and look, for a card of
        // cardWPt x cardHPt points, with 'rays' rays (spread more thickly towards the rim, where it matters).
        public static GlassSideView Compute(GlassShape shape, GlassLook look, int cardWPt, int cardHPt, int rays)
        {
            if (shape == null) shape = GlassShape.Card;
            if (rays < 1) rays = 1;
            GlassTrace tr = GlassMap.BuildTrace(cardWPt, cardHPt, 1f, look, shape, false);
            return tr.SideView(look, cardWPt, cardHPt, shape, rays);
        }

        internal GlassSideView() { }
    }

    // (GlassTrace.SideView: the part that reads the trace's tables back out)
    internal static class SideViewMaker
    {
        internal static GlassSideView Make(GlassTrace tr, GlassLook look, int cardW, int cardH, GlassShape shape, int rays)
        {
            TraceGeo geo = tr.Geo;
            int n = GlassTrace.Steps;
            float B = geo.B;
            float elev = (look.Elevation - GlassLook.DefaultElevation) / 50f;
            float refraction = look.Refraction / 100f, intensity = look.Intensity / 100f;
            float bevel = GlassMap.BevelFor(cardW, cardH, 1f, elev, shape), rv = GlassMap.RvFor(cardW, cardH, 1f, look, bevel);
            float rawMost = 2 * rv;
            float gap = geo.GapB;
            GlassSideView v = new GlassSideView();
            v.BezelPt = B;
            v.GapPt = gap;
            v.Thick = GlassMap.ThickFor(refraction, intensity);
            v.FaceThicknessPt = (float)geo.ZTop[0];
            float rimUnder = geo.HasBottom ? geo.BotZ[n] * B : 0f;
            v.RimThicknessPt = (float)geo.ZTop[n - 1] - rimUnder;
            v.TirBandPt = tr.TirWidth;
            v.BendMaxPt = tr.LandMost;
            v.BendAtRimPt = tr.KeptG[Cell(0.88f)];
            v.Ring = v.TirBandPt > Math.Max(GlassSideView.RingLeastPt, GlassSideView.RingShare * B);

            // the two surfaces (about 64 stretches; the engine reads its tables at every one of 4096)
            const int M = 64;
            v.Top = new PointF[M + 1];
            v.Bottom = new PointF[M + 1];
            for (int j = 0; j <= M; j++)
            {
                float t = j / (float)M;
                int i = Cell(t);
                v.Top[j] = new PointF(t * B, gap + (float)geo.ZTop[i]);
                v.Bottom[j] = new PointF(t * B, gap + (geo.HasBottom ? geo.BotZ[Math.Min(n, (int)Math.Round(t * n))] * B : 0f));
            }

            // the rays
            v.Rays = new SideRay[rays];
            for (int r = 0; r < rays; r++)
            {
                float u = rays == 1 ? 0.5f : r / (float)(rays - 1);
                float t = 1 - (float)Math.Pow(1 - u, 2.0);
                int i = Cell(t);
                RayInfo ri;
                geo.Trace(i, GlassTrace.Ior, rawMost, out ri);
                SideRay s = new SideRay();
                double tt = (i + 0.5) / n;
                float ex = (float)(tt * B);
                s.T = (float)tt;
                s.Entry = new PointF(ex, gap + (float)ri.ZTop);
                s.ThetaDeg = (float)(ri.Theta * 180 / Math.PI);
                double ct = Math.Cos(ri.Theta), q = 1 - ct;
                s.Reflectance = (float)(GlassTrace.R0 + (1 - GlassTrace.R0) * q * q * q * q * q);
                // reflected at the top: tilted outwards by 2 theta from straight up
                s.ReflectDx = (float)Math.Sin(2 * ri.Theta);
                s.ReflectDy = (float)Math.Cos(2 * ri.Theta);
                s.Hit = new PointF((float)(B - ri.HitD), gap + (float)ri.HitZ);
                s.PhiDeg = (float)(ri.Phi * 180 / Math.PI);
                s.Tir = ri.Tir;
                s.RawLandPt = (float)ri.Land;
                s.KeptLandPt = tr.KeptG[i];
                s.Kept = new PointF(ex - s.KeptLandPt, 0f);
                if (ri.Tir)
                {
                    // reflected back up inside, off a surface tilted by phi
                    double sd = Math.Sin(ri.Dev), cd = Math.Cos(ri.Dev), sp = Math.Sin(ri.Phi), cp = Math.Cos(ri.Phi), dn = Math.Cos(ri.Dev + ri.Phi);
                    // (the ray runs (sd, -cd) in (inwards, up); the underside's outward normal is (-sp, -cp))
                    double rx = sd + 2 * dn * sp, ry = -cd + 2 * dn * cp;
                    s.TirDx = (float)(-rx);   // (x here runs towards the outline)
                    s.TirDy = (float)ry;
                    s.Screen = new PointF(float.NaN, 0f);
                }
                else
                {
                    // out through the underside, leaning (o - phi) inwards: on to the screen at the landing as traced
                    s.Screen = new PointF(ex - (float)ri.Land, 0f);
                }
                v.Rays[r] = s;
            }
            return v;
        }

        static int Cell(float t)
        {
            int i = (int)(t * GlassTrace.Steps);
            return i < 0 ? 0 : (i >= GlassTrace.Steps ? GlassTrace.Steps - 1 : i);
        }
    }
}
