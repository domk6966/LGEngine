using System;

namespace LGEngine
{
    // One ray's journey through the slab (lengths in pixels, x = distance in from the outline, z = height above the flat
    // bottom). The bake (GlassTrace) and the editor's side view (GlassSideView) both read their rays from here.
    internal struct RayInfo
    {
        public double Theta;          // the top surface's tilt where it enters
        public double Dev;            // the lean inwards inside the glass, from straight down
        public double D0;             // how far in from the outline it enters
        public double ZTop;           // the height of the top there
        public double HitD, HitZ;     // where it meets the underside
        public double Phi;            // the underside's tilt there
        public double Incidence;      // the angle it meets the underside at (Dev + Phi)
        public double RBot;           // the underside's reflection (1 in total internal reflection)
        public double Land;           // how far in from the entry it reaches the screen, 0..rawMost (rawMost in total reflection)
        public bool Tir;
    }

    // The slab's shape for the trace: per step of t, the top's tilt and height; the underside as a table. Either the
    // shape's own analytic shoulder (a power law, exactly as it has always been worked out) or a GlassProfile's.
    internal sealed class TraceGeo
    {
        public readonly float B;                // the bezel's width, pixels
        public readonly float GapB;             // the gap between the flat bottom and the screen, pixels
        public readonly double[] Theta, ZTop;   // per step (pixels for ZTop)
        public readonly bool HasBottom;
        public readonly float[] BotZ;           // underside height, bezel widths, per node (Steps + 1)
        readonly double[] botPhi;
        readonly double fillet, phiRadius, phiEnd;

        // The analytic shoulder: height rise x (1 - t^power) of the bezel's width on a slab 'slab' thick, an edge radius
        // of GlassTrace.Fillet points turning it to vertical; a flat underside.
        public TraceGeo(float bezel, float power, float rise, float slab, float gap, float k)
        {
            B = bezel;
            GapB = gap * B;
            int n = GlassTrace.Steps;
            Theta = new double[n];
            ZTop = new double[n];
            double thetaEnd = Math.Atan(rise * power);
            fillet = Math.Max(0.5, GlassTrace.Fillet * k);
            double radius = fillet / Math.Max(0.05, 1 - Math.Sin(thetaEnd));
            for (int i = 0; i < n; i++)
            {
                double t = (i + 0.5) / n, tp = Math.Pow(t, power);
                double p = 1 - tp;
                double theta = Math.Atan(rise * power * tp / t), d = (1 - t) * B;
                theta = Round(theta, d, fillet, radius, thetaEnd);
                Theta[i] = theta;
                ZTop[i] = (slab + rise * p) * B;
            }
        }

        // A profile's contours (values in bezel widths at the default thickness; scaled by 'thick').
        public TraceGeo(float bezel, GlassProfile profile, float thick, float gap, float k)
        {
            B = bezel;
            GapB = gap * B;
            int n = GlassTrace.Steps;
            GlassProfile.Tables tab = profile.Tabulate(n, thick);
            Theta = new double[n];
            ZTop = new double[n];
            fillet = Math.Max(0.5, tab.Fillet * k);
            double thetaEnd = tab.ThetaEnd;
            double radius = fillet / Math.Max(0.05, 1 - Math.Sin(thetaEnd));
            for (int i = 0; i < n; i++)
            {
                double t = (i + 0.5) / n, d = (1 - t) * B;
                Theta[i] = Round(tab.Theta[i], d, fillet, radius, thetaEnd);
                ZTop[i] = tab.ZTop[i] * B;
            }
            HasBottom = tab.HasBottom;
            BotZ = tab.BotZ;
            botPhi = tab.Phi;
            phiEnd = tab.PhiEnd;
            phiRadius = fillet / Math.Max(0.05, 1 - Math.Sin(phiEnd));
        }

        // The edge radius: within 'fillet' pixels of the outline the surface turns down to vertical, by how far round the
        // circle it is (seamless where it starts); and never past 89 degrees.
        static double Round(double theta, double d, double fillet, double radius, double thetaEnd)
        {
            if (d < fillet)
            {
                double g = (Math.Asin(Math.Max(0, Math.Min(1, 1 - d / radius))) - thetaEnd) / Math.Max(1e-3, Math.PI / 2 - thetaEnd);
                theta += (Math.PI / 2 - theta) * Math.Max(0, Math.Min(1, g));
            }
            if (theta > 1.5533) theta = 1.5533;   // (89 degrees)
            return theta;
        }

        // The underside's height (pixels) at x pixels in from the outline.
        double Under(double x)
        {
            double tt = 1 - x / B;
            int n = GlassTrace.Steps;
            if (tt <= 0) return 0;
            if (tt >= 1) return BotZ[n] * B;
            double u = tt * n;
            int j = (int)u;
            double f = u - j;
            return (BotZ[j] + f * (BotZ[j + 1] - BotZ[j])) * B;
        }

        // Trace the ray that enters at step i, for a glass of this index (rawMost: the furthest a landing may go).
        public void Trace(int i, double ior, double rawMost, out RayInfo r)
        {
            r = new RayInfo();
            double theta = Theta[i], st = Math.Sin(theta), zTop = ZTop[i];
            double t = (i + 0.5) / GlassTrace.Steps, d = (1 - t) * B;
            double sr = st / ior, dev = theta - Math.Asin(sr);    // (leaning inwards by dev inside the glass)
            r.Theta = theta; r.Dev = dev; r.D0 = d; r.ZTop = zTop;
            if (!HasBottom)
            {
                // The flat underside: a ray leaving it at 'dev' from the normal.
                double sOut = ior * Math.Sin(dev);
                double tn = Math.Tan(dev);
                r.HitD = d + zTop * tn; r.HitZ = 0; r.Phi = 0; r.Incidence = dev;
                if (sOut >= 0.9999)
                {
                    r.RBot = 1; r.Tir = true;
                    r.Land = rawMost;
                }
                else
                {
                    double co = Math.Sqrt(1 - sOut * sOut), qo = 1 - co;
                    r.RBot = GlassTrace.R0 + (1 - GlassTrace.R0) * qo * qo * qo * qo * qo;
                    double land = zTop * tn + GapB * sOut / co;
                    r.Land = land > rawMost ? rawMost : land;
                }
                return;
            }

            // The underside rises towards the outline. Find where the ray meets it: F(s) = (height of the ray after a path
            // s) - (height of the underside under it) falls from the glass's thickness to (minus) the underside's height
            // at the flat bottom's plane; a secant search (Illinois) on the table.
            double sd = Math.Sin(dev), cd = Math.Cos(dev);
            double lo = 0, hi = zTop / Math.Max(1e-6, cd);
            double flo = zTop - Under(d), fhi = -Under(d + hi * sd);
            double s;
            if (flo <= 0) s = 0;
            else if (fhi >= 0) s = hi;
            else
            {
                s = hi;
                int side = 0;
                for (int it = 0; it < 30; it++)
                {
                    s = (lo * fhi - hi * flo) / (fhi - flo);
                    double fs = zTop - s * cd - Under(d + s * sd);
                    if (Math.Abs(fs) < 1e-4) break;
                    if (fs > 0) { lo = s; flo = fs; if (side == 1) fhi *= 0.5; side = 1; }
                    else { hi = s; fhi = fs; if (side == -1) flo *= 0.5; side = -1; }
                }
            }
            double dx = d + s * sd, zb = Under(dx);
            int n = GlassTrace.Steps;
            int c = (int)((1 - dx / B) * n);
            c = c < 0 ? 0 : (c >= n ? n - 1 : c);
            double phi = botPhi[c];
            if (dx < fillet)
            {
                // (the underside's edge radius, as the top's)
                double g = (Math.Asin(Math.Max(0, Math.Min(1, 1 - dx / phiRadius))) - phiEnd) / Math.Max(1e-3, Math.PI / 2 - phiEnd);
                phi += (Math.PI / 2 - phi) * Math.Max(0, Math.Min(1, g));
            }
            double inc = dev + phi, si = ior * Math.Sin(inc);
            r.HitD = dx; r.HitZ = zb; r.Phi = phi; r.Incidence = inc;
            if (inc >= 1.55 || inc <= -1.55 || Math.Abs(si) >= 0.9999)   // (a hand-drawn underside may lean either way)
            {
                r.RBot = 1; r.Tir = true;
                r.Land = rawMost;
            }
            else
            {
                // out through a surface tilted by phi: it leaves leaning (o - phi) inwards from straight down
                double o = Math.Asin(si), co = Math.Cos(o), qo = 1 - co;
                r.RBot = GlassTrace.R0 + (1 - GlassTrace.R0) * qo * qo * qo * qo * qo;
                double land = (dx - d) + (zb + GapB) * Math.Tan(o - phi);
                r.Land = !(land > 0) ? 0 : (land > rawMost ? rawMost : land);   // (not less than 0, and never NaN)
            }
        }
    }
}
