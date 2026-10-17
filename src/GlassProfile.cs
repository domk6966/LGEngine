using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace LGEngine
{
    // One control point of a contour: T across the bezel (0 where the flat face ends, 1 at the outline) and V, how far
    // the surface is from its reference there, in bezel widths at the default thickness.
    public struct ProfilePoint
    {
        public float T, V;
        public ProfilePoint(float t, float v) { T = t; V = v; }
    }

    // An entry of GlassProfile.Presets.
    public sealed class GlassPreset
    {
        public readonly string Name, Note;
        public readonly bool MakesRing;   // the strongly curved underside that makes the silver ring (total internal reflection)
        internal GlassPreset(string name, string note, bool ring) { Name = name; Note = note; MakesRing = ring; }
    }

    // The cross-section of a piece of glass across its bezel, drawn by hand: plain, immutable data (it travels as a
    // string: ToString / Parse), so it can cross a process boundary or sit in a settings file.
    //
    //   Top:    how far the top surface falls below the flat face at t (0 at the face, 1 at the outline): control points
    //           (t, drop) in bezel widths at the default thickness. The face itself is the point (0, 0).
    //   Bottom: how far the underside rises above the flat bottom at t, the same way (0, 0) under the face.
    //   Slab:   the glass under the outline's top edge (above the flat bottom), in bezel widths.
    //   Fillet: the edge radius that turns both surfaces vertical in the last points of the bezel, in points.
    //
    // Everything but the fillet and the gap under the glass (the Elevation slider) scales with the thickness (the
    // Refraction x Intensity sliders). The curves between control points are monotone cubics (Fritsch-Carlson: no
    // overshoot between points), tabulated to GlassTrace.Steps; the slopes of the table, by central differences, are the
    // tilts of the surfaces (theta, phi) the ray trace works from.
    //
    // A null profile (GlassShape.Profile) is "Today": the analytic power-law shoulder of the shape, unchanged.
    public sealed class GlassProfile
    {
        public const int MaxPoints = 64;
        public const float MaxValue = 3f;        // furthest a contour may be from its reference (bezel widths)
        public const float MinGlass = 0.12f;     // thinnest glass left anywhere (bezel widths): the underside is kept this far below the top
        public const float MaxSlab = 6f, MaxFillet = 12f, DefaultSlab = 2.2f, DefaultFillet = 3f;
        public const float MinStep = 0.004f;     // closest two points may be in t

        public readonly string Name;
        public readonly float Slab, Fillet;
        readonly ProfilePoint[] top, bot;
        readonly ReadOnlyCollection<ProfilePoint> topView, botView;
        readonly Curve topCurve, botCurve;
        string text, hash;

        GlassProfile(string name, ProfilePoint[] top, ProfilePoint[] bot, float slab, float fillet)
        {
            Name = name; this.top = top; this.bot = bot; Slab = slab; Fillet = fillet;
            topView = new ReadOnlyCollection<ProfilePoint>(top);
            botView = new ReadOnlyCollection<ProfilePoint>(bot);
            topCurve = new Curve(top);
            botCurve = new Curve(bot);
        }

        public ReadOnlyCollection<ProfilePoint> TopPoints { get { return topView; } }
        public ReadOnlyCollection<ProfilePoint> BottomPoints { get { return botView; } }

        // True when the underside is not flat.
        public bool HasBottom
        {
            get { for (int i = 0; i < bot.Length; i++) if (bot[i].V > 1e-5f) return true; return false; }
        }

        // ---------------------------------------------------------------------------------------------
        // Making one. Points are sorted, kept in 0..1 and 0..MaxValue, rounded to 4 places (so that the string form
        // round-trips exactly), the face point (0, 0) and an end point at t = 1 are always there.

        public static GlassProfile Create(string name, IList<ProfilePoint> top, IList<ProfilePoint> bottom, float slab, float fillet)
        {
            return new GlassProfile(CleanName(name), Clean(top), Clean(bottom), Round(Clamp(slab, MinGlass, MaxSlab), 3), Round(Clamp(fillet, 0f, MaxFillet), 3));
        }

        public GlassProfile WithTop(IList<ProfilePoint> p) { return Create(Name, p, bot, Slab, Fillet); }
        public GlassProfile WithBottom(IList<ProfilePoint> p) { return Create(Name, top, p, Slab, Fillet); }
        public GlassProfile WithSlab(float slab) { return Create(Name, top, bot, slab, Fillet); }
        public GlassProfile WithFillet(float fillet) { return Create(Name, top, bot, Slab, fillet); }
        public GlassProfile WithName(string name) { return Create(name, top, bot, Slab, Fillet); }

        static float Clamp(float v, float lo, float hi)
        {
            if (float.IsNaN(v)) return lo;
            return v < lo ? lo : (v > hi ? hi : v);
        }

        static float Round(float v, int places) { return (float)Math.Round(v, places); }

        static string CleanName(string n)
        {
            if (n == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in n)
            {
                if (c == ';' || c == '=' || c == ',' || c == ':' || char.IsControl(c)) continue;
                sb.Append(c);
                if (sb.Length >= 32) break;
            }
            return sb.ToString().Trim();
        }

        static ProfilePoint[] Clean(IList<ProfilePoint> src)
        {
            List<ProfilePoint> l = new List<ProfilePoint>();
            if (src != null)
                for (int i = 0; i < src.Count; i++)
                    l.Add(new ProfilePoint(Round(Clamp(src[i].T, 0f, 1f), 6), Round(Clamp(src[i].V, 0f, MaxValue), 6)));
            l.Sort(delegate(ProfilePoint a, ProfilePoint b) { int c = a.T.CompareTo(b.T); return c != 0 ? c : a.V.CompareTo(b.V); });
            List<ProfilePoint> o = new List<ProfilePoint>();
            for (int i = 0; i < l.Count; i++)
            {
                if (o.Count > 0 && l[i].T - o[o.Count - 1].T < MinStep) continue;
                o.Add(l[i]);
            }
            if (o.Count > 0 && o[0].T < MinStep) o[0] = new ProfilePoint(0f, 0f); else o.Insert(0, new ProfilePoint(0f, 0f));
            ProfilePoint last = o[o.Count - 1];
            if (o.Count == 1) o.Add(new ProfilePoint(1f, 0f));
            else if (last.T > 1f - MinStep) o[o.Count - 1] = new ProfilePoint(1f, last.V);
            else o.Add(new ProfilePoint(1f, last.V));
            if (o.Count > MaxPoints)
            {
                List<ProfilePoint> d = new List<ProfilePoint>();
                for (int i = 0; i < MaxPoints; i++) d.Add(o[(int)Math.Round(i * (o.Count - 1) / (double)(MaxPoints - 1))]);
                o = d;
            }
            return o.ToArray();
        }

        // ---------------------------------------------------------------------------------------------
        // The string form: "name=Tighter rim;top=0:0,0.5:0.02,1:0.3;bot=0:0,1:0;slab=2.2;fillet=3" (invariant culture;
        // the name is left out when empty).

        public override string ToString()
        {
            if (text == null) text = Build(true);
            return text;
        }

        string Build(bool withName)
        {
            StringBuilder sb = new StringBuilder();
            if (withName && Name.Length > 0) sb.Append("name=").Append(Name).Append(';');
            sb.Append("top="); Points(sb, top);
            sb.Append(";bot="); Points(sb, bot);
            sb.Append(";slab=").Append(Fmt(Slab)).Append(";fillet=").Append(Fmt(Fillet));
            return sb.ToString();
        }

        static string Fmt(float v) { return v.ToString("0.######", CultureInfo.InvariantCulture); }

        static void Points(StringBuilder sb, ProfilePoint[] p)
        {
            for (int i = 0; i < p.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Fmt(p[i].T)).Append(':').Append(Fmt(p[i].V));
            }
        }

        // A short hash of the look (not the name): six hex digits, part of a map's Signature.
        public string Hash
        {
            get
            {
                if (hash == null)
                {
                    uint h = 2166136261u;
                    foreach (char c in Build(false)) h = (h ^ c) * 16777619u;
                    hash = h.ToString("x8").Substring(0, 6);
                }
                return hash;
            }
        }

        // The text of a profile ("" for null: Today).
        public static string Text(GlassProfile p) { return p == null ? "" : p.ToString(); }

        // The same look (the name does not count)?
        public static bool SameLook(GlassProfile a, GlassProfile b)
        {
            if (a == null || b == null) return a == b;
            return a.Hash == b.Hash && a.Build(false) == b.Build(false);
        }

        // A profile from its string form, or null (Today) if it is empty or anything in it is not valid.
        public static GlassProfile Parse(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            if (s.Length == 0 || s.Length > 2400) return null;
            try
            {
                string name = "";
                ProfilePoint[] top = null, bot = null;
                float slab = DefaultSlab, fillet = DefaultFillet;
                foreach (string part in s.Split(';'))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = part.Substring(0, eq).Trim().ToLowerInvariant(), val = part.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "name": name = val; break;
                        case "top": top = ParsePoints(val); if (top == null || top.Length == 0) return null; break;
                        case "bot": case "bottom": bot = ParsePoints(val); if (bot == null) return null; break;
                        case "slab": if (!Num(val, out slab) || slab < MinGlass || slab > MaxSlab) return null; break;
                        case "fillet": if (!Num(val, out fillet) || fillet < 0 || fillet > MaxFillet) return null; break;
                    }
                }
                if (top == null) return null;
                return Create(name, top, bot, slab, fillet);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static bool Num(string s, out float v)
        {
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;
            return !float.IsNaN(v) && !float.IsInfinity(v);
        }

        static ProfilePoint[] ParsePoints(string s)
        {
            List<ProfilePoint> l = new List<ProfilePoint>();
            if (s.Length > 0)
            {
                foreach (string item in s.Split(','))
                {
                    string[] tv = item.Split(':');
                    float t, v;
                    if (tv.Length != 2 || !Num(tv[0].Trim(), out t) || !Num(tv[1].Trim(), out v)) return null;
                    if (t < 0 || t > 1 || v < 0 || v > MaxValue) return null;
                    l.Add(new ProfilePoint(t, v));
                }
            }
            if (l.Count > MaxPoints) return null;
            return l.ToArray();
        }

        // ---------------------------------------------------------------------------------------------
        // Presets. Each is made from a shape (it knows card from lens), so they follow that shape's own numbers.

        static readonly GlassPreset[] presets =
        {
            new GlassPreset("Today", "The shape's own analytic profile", false),
            new GlassPreset("Tighter rim", "A flatter face and a steeper, narrower rim", false),
            new GlassPreset("Thin lens", "Half the slab, a gently curved underside", false),
            new GlassPreset("Pillow", "The underside mirrors the top: gives the silver glass-ring look (total internal reflection)", true),
            new GlassPreset("Flat pane", "No curve: a flat slab with a rounded edge", false)
        };

        public static ReadOnlyCollection<GlassPreset> Presets { get { return new ReadOnlyCollection<GlassPreset>(presets); } }

        public static string[] PresetNames
        {
            get
            {
                string[] n = new string[presets.Length];
                for (int i = 0; i < n.Length; i++) n[i] = presets[i].Name;
                return n;
            }
        }

        static bool IsLens(GlassShape shape) { return shape != null && shape.Name == "lens"; }

        // The preset's profile for this shape; null for "Today" (and any name not in the list).
        public static GlassProfile Preset(string name, GlassShape shape)
        {
            if (shape == null) shape = GlassShape.Card;
            float power = shape.TracePower, rise = shape.TraceRise, slab = shape.TraceSlab;
            switch (name)
            {
                case "Tighter rim":
                    if (IsLens(shape)) { power = 3.5f; rise = 0.65f; } else { power = 7f; rise = 0.45f; }
                    return Sample("Tighter rim", power, rise, slab, DefaultFillet, 11, 0f);
                case "Thin lens":
                    return Sample("Thin lens", power, rise, slab * 0.5f, DefaultFillet, 11, 0.18f);
                case "Pillow":
                    return Sample("Pillow", power, rise, rise + 0.15f, DefaultFillet, 11, 1f);
                case "Flat pane":
                    return Sample("Flat pane", power, 0f, slab, DefaultFillet, 2, 0f);
            }
            return null;
        }

        // The editor's start for "Today": the shape's analytic profile as control points.
        public static GlassProfile FromShape(GlassShape shape, int points)
        {
            if (shape == null) shape = GlassShape.Card;
            return Sample("Today", shape.TracePower, shape.TraceRise, shape.TraceSlab, DefaultFillet, points, 0f);
        }

        // A power-law shoulder, drop = rise x t^power, sampled at 'points' places (closer together towards the outline,
        // where it bends most), with the underside 'bottomShare' times the same curve.
        public static GlassProfile Sample(string name, double power, double rise, double slab, double fillet, int points, double bottomShare)
        {
            if (points < 2) points = 2;
            List<ProfilePoint> tp = new List<ProfilePoint>(), bp = new List<ProfilePoint>();
            for (int i = 0; i < points; i++)
            {
                double u = i / (double)(points - 1), t = points > 12 ? u : 1 - Math.Pow(1 - u, 1.8), drop = rise * Math.Pow(t, power);
                tp.Add(new ProfilePoint((float)t, (float)drop));
                if (bottomShare > 0 || i == 0 || i == points - 1) bp.Add(new ProfilePoint((float)t, (float)(drop * bottomShare)));   // (a flat underside: its two ends)
            }
            return Create(name, tp, bp, (float)slab, (float)fillet);
        }

        // ---------------------------------------------------------------------------------------------
        // Tabulating for the ray trace.

        internal sealed class Tables
        {
            public double[] Theta;       // per step: the top surface's tilt (radians, not less than 0)
            public double[] ZTop;        // per step: the top's height above the flat bottom (bezel widths)
            public float[] BotZ;         // per node (Steps + 1): the underside's height above the flat bottom (bezel widths)
            public double[] Phi;         // per step: the underside's tilt
            public bool HasBottom;
            public double ThetaEnd, PhiEnd;   // the tilts at the outline, before the edge radius
            public float Fillet;         // points
        }

        // (values scaled by 'thick'; the slopes are those of the scaled curves, by central differences of the table)
        internal Tables Tabulate(int n, float thick)
        {
            Tables r = new Tables();
            r.Theta = new double[n]; r.ZTop = new double[n]; r.Phi = new double[n]; r.BotZ = new float[n + 1];
            r.Fillet = Fillet;
            double d1 = topCurve.At(1.0), dMax = d1 + Slab - MinGlass;
            double[] drop = new double[n + 1];
            double[] bz = new double[n + 1];
            int s1 = 0, s2 = 0;
            double most = 0;
            for (int j = 0; j <= n; j++)
            {
                double t = j / (double)n;
                drop[j] = Math.Min(dMax, topCurve.At(t, ref s1));
                double b = botCurve.At(t, ref s2), room = Slab + d1 - drop[j] - MinGlass;
                bz[j] = Math.Max(0, Math.Min(b, room)) * thick;
                r.BotZ[j] = (float)bz[j];
                if (bz[j] > most) most = bz[j];
            }
            r.HasBottom = most > 1e-5;
            for (int i = 0; i < n; i++)
            {
                r.Theta[i] = Math.Max(0, Math.Atan((drop[i + 1] - drop[i]) * n * thick));
                r.ZTop[i] = (Slab + d1 - 0.5 * (drop[i] + drop[i + 1])) * thick;
                r.Phi[i] = Math.Atan((bz[i + 1] - bz[i]) * n);
            }
            r.ThetaEnd = r.Theta[n - 1];
            r.PhiEnd = r.Phi[n - 1];
            return r;
        }

        // ---------------------------------------------------------------------------------------------
        // The monotone cubic (Fritsch-Carlson) through a contour's points.

        sealed class Curve
        {
            readonly double[] x, y, m;

            public Curve(ProfilePoint[] p)
            {
                int n = p.Length;
                x = new double[n]; y = new double[n]; m = new double[n];
                for (int i = 0; i < n; i++) { x[i] = p[i].T; y[i] = p[i].V; }
                if (n < 2) return;
                double[] h = new double[n - 1], del = new double[n - 1];
                for (int i = 0; i < n - 1; i++) { h[i] = x[i + 1] - x[i]; del[i] = (y[i + 1] - y[i]) / h[i]; }
                // slopes by central differences (one-sided, three-point, at the ends)
                for (int i = 1; i < n - 1; i++) m[i] = (h[i] * del[i - 1] + h[i - 1] * del[i]) / (h[i - 1] + h[i]);   // (exact for a parabola, on any spacing)
                if (n == 2) { m[0] = m[1] = del[0]; }
                else
                {
                    m[0] = End(h[0], h[1], del[0], del[1]);
                    m[n - 1] = End(h[n - 2], h[n - 3], del[n - 2], del[n - 3]);
                }
                // no overshoot: a point where the data turns around is flat; and the slopes at the two ends of a
                // segment kept within three times its secant (the Fritsch-Carlson circle)
                for (int i = 1; i < n - 1; i++) if (del[i - 1] * del[i] <= 0) m[i] = 0;
                for (int i = 0; i < n - 1; i++)
                {
                    if (del[i] == 0) { m[i] = 0; m[i + 1] = 0; continue; }
                    double a = m[i] / del[i], b = m[i + 1] / del[i];
                    if (a < 0) { m[i] = 0; a = 0; }
                    if (b < 0) { m[i + 1] = 0; b = 0; }
                    double ss = a * a + b * b;
                    if (ss > 9)
                    {
                        double tau = 3 / Math.Sqrt(ss);
                        m[i] = tau * a * del[i];
                        m[i + 1] = tau * b * del[i];
                    }
                }
            }

            // (a three-point end slope, kept in the data's direction and within three times the first secant)
            static double End(double h0, double h1, double d0, double d1)
            {
                double s = ((2 * h0 + h1) * d0 - h0 * d1) / (h0 + h1);
                if (s * d0 <= 0) return 0;
                if (d0 * d1 <= 0 && Math.Abs(s) > 3 * Math.Abs(d0)) return 3 * d0;
                return s;
            }

            public double At(double t) { int seg = 0; return At(t, ref seg); }

            public double At(double t, ref int seg)
            {
                int last = x.Length - 2;
                if (last < 0) return y[0];
                if (seg > last) seg = last;
                while (seg > 0 && t < x[seg]) seg--;
                while (seg < last && t > x[seg + 1]) seg++;
                double h = x[seg + 1] - x[seg], s = (t - x[seg]) / h, s2 = s * s, s3 = s2 * s;
                return (2 * s3 - 3 * s2 + 1) * y[seg] + (s3 - 2 * s2 + s) * h * m[seg] + (-2 * s3 + 3 * s2) * y[seg + 1] + (s3 - s2) * h * m[seg + 1];
            }
        }
    }
}
