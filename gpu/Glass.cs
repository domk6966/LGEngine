using System;

namespace LGGpu
{
    // The sliders of a look (Settings.GlassLook).
    internal struct Look
    {
        public int Dark, Blur, Refraction, Rim, Shadow, Round, Intensity;
        public Look(int dark, int blur, int refraction, int rim, int shadow, int round, int intensity)
        {
            Dark = dark; Blur = blur; Refraction = refraction; Rim = rim; Shadow = shadow; Round = round; Intensity = intensity;
        }
        public static Look App { get { return new Look(30, 44, 84, 63, 36, 24, 100); } }       // the user's settings (LiquidGlass.ini)
        public static Look Default { get { return new Look(46, 52, 80, 60, 40, 24, 100); } }   // GlassLook.LiquidGlass
    }

    // The shape of a piece of glass (GlassShape of the CPU engine), same numbers.
    internal sealed class Shape
    {
        public string Name;
        public float BevelShare, BevelLeast, BevelMost, ProfilePower, Magnify, Thickness, RimCreep, Dispersion;
        public float EdgeFrost, EdgeFrostLeast, BlendBand, EdgeReach, EdgeReachFrom, ReachSoftness, BottomReach;
        public float FaceFrost = 1f, Dimming = 1f, Evening = 1f, Veil = 1f, SpecGain = 1f, GlowGain = 1f, Lift = 0f, ShadowShare = 1f;

        public static Shape Card
        {
            get
            {
                Shape c = new Shape();
                c.Name = "card";
                c.BevelShare = 0.4f; c.BevelLeast = 5f; c.BevelMost = 60f;
                c.ProfilePower = 4f; c.Magnify = 1.08f; c.Thickness = 1.6f; c.RimCreep = 0.12f; c.Dispersion = 0.07f;
                c.EdgeFrost = 0.2f; c.EdgeFrostLeast = 0.3f; c.BlendBand = 0.8f;
                c.EdgeReach = 0.4f; c.EdgeReachFrom = 0.45f; c.ReachSoftness = 0.85f; c.BottomReach = 1f;
                c.Lift = 4f;
                return c;
            }
        }

        public static Shape Lens
        {
            get
            {
                Shape l = new Shape();
                l.Name = "lens";
                l.BevelShare = 0.5f; l.BevelLeast = 5f; l.BevelMost = 40f;
                l.ProfilePower = 2.5f; l.Magnify = 1.2f; l.Thickness = 1.0f; l.RimCreep = 0.25f; l.Dispersion = 0.06f;
                l.EdgeFrost = 0.2f; l.EdgeFrostLeast = 0.15f; l.BlendBand = 0.6f;
                l.EdgeReach = 0.45f; l.EdgeReachFrom = 0.45f; l.ReachSoftness = 0.85f; l.BottomReach = 0.15f;
                l.FaceFrost = 0.35f; l.Dimming = 0.7f; l.Evening = 0.4f; l.Veil = 0.6f;
                l.SpecGain = 1.4f; l.GlowGain = 2.6f; l.Lift = 10f; l.ShadowShare = 0.7f;
                return l;
            }
        }
    }

    // Everything about a surface's glass that depends on its size, scale, look, shape and
    // elevation: the numbers the shader takes, and the bend tables across the bezel (worked out
    // exactly as GlassMap does, including the "never fold back" pass that a shader cannot do).
    internal sealed class Geometry
    {
        // GlassMap's fixed parts of the look.
        const float RefractiveIndex = 1.5f;
        const float SpecMix = 0.68f, SpecFade = 0.6f, GlowMix = 0.16f, GlowDepth = 10f, GlowPower = 1.6f, LightLeast = 0.32f, AwayShare = 0.82f;
        const float LightX = -0.6f, LightY = -0.8f, GlintMix = 0.3f, GlintFade = 2.5f, GlintAway = 0.45f;
        const float OutlineAlpha = 0.4f, OutlineWidth = 0.8f, ShadowSpread = 12f, ShadowDrop = 5f, ShadowDepth = 0.40f;
        const float SplitMost = 0.75f, DimPerUnit = 0.65f, FringeFrom = 0.7f, BlendFrom = 0.5f, FrostPerUnit = 0.07f, CornerReach = 1.6f;
        const float UnderlayDefault = 0.3f, UnderlayReach = 5.5f;
        public const int Steps = 1024;

        public int CardW, CardH;
        public float K, Elevation = 50f;
        public Look Look;
        public Shape Shape;
        public float Underlay = UnderlayDefault;
        public int Travel;   // extra room round the card inside its window, for moving it without a new window

        // worked out
        public int Margin, W, H, Pad, BackW, BackH, SoftScale, SoftW, SoftH, EdgeScale, EdgeW, EdgeH, EdgePasses, BlurRadius, HaloRadius;
        public float Sigma, BlurSigmaTex;
        public float[] Bend;   // Steps + 1 entries x 4: bendAt, bendIn, reachW, 0
        float hx, hy, rad, cornerR, cornerInv, bevel, rv, magnify, glowDepth, edgeLight, outline, spread, drop, shadowAmt, intensity;
        float saturation, chromaSplit, dim, addR, addG, addB;

        public Geometry(int cardW, int cardH, float k, Look look, Shape shape, float elevation, int travel)
        {
            CardW = cardW; CardH = cardH; K = k; Look = look; Shape = shape; Elevation = elevation; Travel = travel;
            Work();
        }

        static void Frost(float sigma, int sharpFactor, out int ratio, out int passes)
        {
            ratio = 1;
            passes = 0;
            while (ratio < 12 && sharpFactor * ratio * 1.26f < sigma) ratio++;
            while (passes < 3 && sharpFactor * ratio * Math.Sqrt(0.083 + 0.5 * passes) < sigma) passes++;
        }

        void Work()
        {
            float k = K, e = (Elevation - 50f) / 50f;   // -1 flat .. 0 as the CPU engine .. 1 raised
            float dark = Look.Dark / 100f, refraction = Look.Refraction / 100f, rimLight = Look.Rim / 100f, shadowAmount = Look.Shadow / 100f;
            intensity = Look.Intensity / 100f;
            shadowAmount *= Math.Min(1.6f, 0.35f + 0.65f * intensity);
            shadowAmount *= 1 + 0.35f * e;

            hx = CardW / 2f; hy = CardH / 2f;
            rad = Math.Min(Look.Round * k, Math.Min(hx, hy));
            cornerR = Math.Min(Math.Min(hx, hy), rad * CornerReach);
            cornerInv = 0;
            if (cornerR > rad + 0.01f)
            {
                double diag = ((cornerR - rad) * Math.Sqrt(2) + rad) / cornerR, inv = 0.5 - Math.Log(diag, 2);
                if (inv > 0.02) cornerInv = (float)inv;
            }
            bevel = Math.Max(Shape.BevelLeast * k, Math.Min(Shape.BevelMost * k, Math.Min(CardW, CardH) * Shape.BevelShare));
            bevel *= 1 + 0.25f * e;
            bevel = Math.Min(bevel, Math.Min(hx, hy) * 0.9f);
            float reach = Shape.EdgeReach * bevel * Math.Min(1.5f, refraction * intensity);

            int sharpFactor = Math.Max(1, (int)Math.Round(k));
            Sigma = Look.Blur * FrostPerUnit * k * Shape.FaceFrost;
            // the frosted picture: shrunk to texels no bigger than about two thirds of the blur,
            // then a Gaussian of what is left (the B-spline sampling adds a third of a texel squared)
            SoftScale = 1;
            while (SoftScale < 8 && SoftScale * 2 <= Math.Max(1f, Sigma / 1.5f)) SoftScale *= 2;
            float sigmaTex = Sigma / SoftScale;
            BlurSigmaTex = (float)Math.Sqrt(Math.Max(0.0001, sigmaTex * sigmaTex - 0.33));
            BlurRadius = BlurSigmaTex < 0.3f ? 0 : Math.Min(12, (int)Math.Ceiling(2.5f * BlurSigmaTex));
            // the rim's: as the CPU engine (shrunk by the scale, smoothed a few times)
            float edgeSigma = Math.Min(Sigma, Math.Max(Shape.EdgeFrostLeast * k, Shape.EdgeFrost * Sigma));
            int edgeRatio;
            Frost(edgeSigma, sharpFactor, out edgeRatio, out EdgePasses);
            EdgeScale = sharpFactor >= 2 ? 2 : 1;
            if (EdgeScale > SoftScale) EdgeScale = SoftScale;

            Pad = (int)Math.Ceiling(Math.Max(reach + 3 * k, 2.5f * Sigma + 2 * SoftScale)) + Travel;
            int unit = Math.Max(SoftScale, 8);
            Pad = (Pad + unit - 1) / unit * unit;
            BackW = (CardW + 2 * Pad + unit - 1) / unit * unit;
            BackH = (CardH + 2 * Pad + unit - 1) / unit * unit;
            SoftW = BackW / SoftScale; SoftH = BackH / SoftScale;
            EdgeW = BackW / EdgeScale; EdgeH = BackH / EdgeScale;
            HaloRadius = Math.Max(1, (int)Math.Round(UnderlayReach * k / 2));

            saturation = 1f + (0.42f + 0.30f * dark) * Math.Min(1.2f, intensity);
            chromaSplit = Shape.Dispersion * refraction * intensity;
            float tint = dark * Shape.Dimming, lift = Shape.Lift * Math.Min(1f, dark * 2.5f);
            addR = (16 * 0.5f * tint + lift) / 255f;
            addG = (18 * 0.5f * tint + lift) / 255f;
            addB = (26 * 0.5f * tint + lift * 1.1f) / 255f;
            dim = 1f - DimPerUnit * tint;

            spread = ShadowSpread * k * Shape.ShadowShare * (1 + 0.6f * e);
            drop = ShadowDrop * k * Shape.ShadowShare * (1 + 1.0f * e);
            shadowAmt = ShadowDepth * shadowAmount;
            Margin = (int)Math.Ceiling(spread * 3 + Math.Max(0, drop) * 0.5f);
            W = CardW + Margin * 2 + Travel * 2;
            H = CardH + Margin * 2 + Travel * 2;

            float strength = (0.2f + 1.3f * refraction) * intensity * (1 + 0.3f * e);
            magnify = 1f + (Shape.Magnify - 1f) * Math.Min(1.5f, refraction * intensity);
            rv = Math.Min(Math.Min(hx, hy), Math.Max(rad, bevel));
            glowDepth = Math.Min(GlowDepth * k, bevel);
            edgeLight = rimLight / 0.6f * intensity * (1 + 0.35f * e);
            outline = OutlineAlpha * Math.Min(1f, intensity);

            // the bend tables, exactly as GlassMap
            float[] bendAt = new float[Steps + 1];
            for (int i = 0; i <= Steps; i++)
            {
                float t = i / (float)Steps, tp = (float)Math.Pow(t, Shape.ProfilePower);
                if (tp > 0.9995f) tp = 0.9995f;
                float height = (float)Math.Pow(1 - tp, 1 / Shape.ProfilePower);
                float slope = (float)Math.Pow(t, Shape.ProfilePower - 1) * (float)Math.Pow(1 - tp, 1 / Shape.ProfilePower - 1);
                if (slope > 14f) slope = 14f;
                double angleIn = Math.Atan(slope);
                double angleOut = Math.Asin(Math.Sin(angleIn) / RefractiveIndex);
                bendAt[i] = (float)Math.Tan(angleIn - angleOut) * (height + Shape.Thickness) * bevel * strength;
            }
            float[] bendIn = (float[])bendAt.Clone();
            float[] reachW = new float[Steps + 1];
            if (reach > 0)
            {
                for (int i = 0; i <= Steps; i++)
                {
                    float t = i / (float)Steps, w = (t - Shape.EdgeReachFrom) / (1 - Shape.EdgeReachFrom);
                    if (w <= 0) continue;
                    w = w * w * (3 - 2 * w);
                    reachW[i] = w;
                    float depthHere = (1 - t) * bevel, look = depthHere + bendAt[i];
                    look = (1 - w) * look - w * reach;
                    bendAt[i] = look - depthHere;
                }
            }
            foreach (float[] table in new float[][] { bendAt, bendIn })
            {
                for (int i = 0; i <= Steps; i++)
                {
                    float most = 0.9f * (rv - (1 - i / (float)Steps) * bevel) / magnify;
                    if (table[i] > most) table[i] = Math.Max(0, most);
                }
            }
            foreach (float[] table in new float[][] { bendAt, bendIn })
            {
                float lookedAt = bevel;
                for (int i = 1; i <= Steps; i++)
                {
                    float depthHere = (1 - i / (float)Steps) * bevel, look = depthHere + table[i];
                    float most = lookedAt - Shape.RimCreep * bevel / Steps;
                    if (look > most)
                    {
                        look = most;
                        table[i] = look - depthHere;
                    }
                    lookedAt = look;
                }
            }
            Bend = new float[(Steps + 1) * 4];
            for (int i = 0; i <= Steps; i++)
            {
                Bend[i * 4] = bendAt[i];
                Bend[i * 4 + 1] = bendIn[i];
                Bend[i * 4 + 2] = reachW[i];
            }
        }

        // The shader's numbers for this glass. appearance: 0 dark, 1 light, -1 adapt.
        public static float DebugMode;

        public void Fill(Constants c, float appearance, float opacity, float shiftX, float shiftY, bool shadowAuto)
        {
            float k = K;
            c.Set(0, W, H, Margin, k);
            c.Set(1, hx, hy, rad, cornerR);
            c.Set(2, cornerInv, bevel, rv, magnify);
            c.Set(3, Pad, BackW, BackH, 0);
            c.Set(4, SoftW, SoftH, SoftScale, 0);
            c.Set(5, EdgeW, EdgeH, EdgeScale, 0);
            c.Set(6, LightX, LightY, SpecMix * Shape.SpecGain, SpecFade * k);
            c.Set(7, GlowMix * Shape.GlowGain, glowDepth, GlowPower, LightLeast);
            c.Set(8, AwayShare, GlintMix, GlintFade * k, GlintAway);
            c.Set(9, outline, OutlineWidth * k, spread, drop);
            c.Set(10, shadowAmt, intensity, edgeLight, 8f * intensity / 255f);
            c.Set(11, saturation, dim, addR, addG);
            c.Set(12, addB, Shape.Evening, Shape.Veil, appearance);
            c.Set(13, chromaSplit, SplitMost * k, FringeFrom, BlendFrom);
            c.Set(14, Shape.BlendBand, Shape.ReachSoftness, Shape.BottomReach, CardH * 0.75f);
            // 15, 16 (contents, halo) set by the surface
            c.Set(16, 0, 0, Underlay, opacity);
            c.Set(22, shadowAuto ? 1 : 0, DebugMode, 0, 0);
            c.Set(23, shiftX, shiftY, 0, 0);
        }
    }
}
