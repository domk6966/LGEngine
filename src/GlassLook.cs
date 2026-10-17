using System;

namespace LGEngine
{
    // The sliders that make up one glass look. All 0-100 except Round (a corner radius, in points)
    // and Intensity (0-150). Elevation: how far off the screen the glass floats (0 flat on it, 100
    // well above it; 50 is the long-standing look): see GlassMap for what it changes.
    public struct GlassLook
    {
        public int Dark, Blur, Refraction, Rim, Shadow, Round, Intensity;
        int elevationPlus50;   // (stored offset by 50, so that a default-constructed look has the long-standing elevation)

        public int Elevation
        {
            get { return elevationPlus50 + 50; }
            set { elevationPlus50 = Math.Max(0, Math.Min(100, value)) - 50; }
        }

        public const int DefaultElevation = 50;

        public GlassLook(int dark, int blur, int refraction, int rim, int shadow, int round, int intensity)
        {
            Dark = dark;
            Blur = blur;
            Refraction = refraction;
            Rim = rim;
            Shadow = shadow;
            Round = round;
            Intensity = intensity;
            elevationPlus50 = 0;
        }

        public GlassLook(int dark, int blur, int refraction, int rim, int shadow, int round, int intensity, int elevation)
            : this(dark, blur, refraction, rim, shadow, round, intensity)
        {
            Elevation = elevation;
        }

        // The values that come closest to the Liquid Glass of macOS 26 and iOS 26 in its dark style
        // (matched by eye against Apple's own pictures of it).
        public static readonly GlassLook LiquidGlass = new GlassLook(46, 52, 80, 60, 40, 24, 100);

        // Apple's glass is not tuned one slider at a time: clearer glass is also sharper, bends what
        // is behind it more visibly and leans more on its edge light, and more tinted glass is
        // frostier with a deeper shadow. This is the rest of a look worked out from its tint that
        // way (passing through LiquidGlass), keeping the corners, intensity and elevation of 'keep'.
        public static GlassLook FromTint(int tint, GlassLook keep)
        {
            float d = tint - LiquidGlass.Dark;
            return new GlassLook(tint, Percent(LiquidGlass.Blur + d * 0.5f), Percent(LiquidGlass.Refraction - d * 0.25f),
                Percent(LiquidGlass.Rim - d * 0.2f), Percent(LiquidGlass.Shadow + d * 0.25f), keep.Round, keep.Intensity, keep.Elevation);
        }

        static int Percent(float v)
        {
            return Math.Max(0, Math.Min(100, (int)Math.Round(v)));
        }

        public bool Equals(GlassLook o)
        {
            return Dark == o.Dark && Blur == o.Blur && Refraction == o.Refraction && Rim == o.Rim && Shadow == o.Shadow && Round == o.Round && Intensity == o.Intensity && Elevation == o.Elevation;
        }
    }
}
