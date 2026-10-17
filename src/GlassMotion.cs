using System;

namespace LGEngine
{
    // How the glass itself moves this frame (GlassMap.Motion): the bend of its edge and the light on its rim
    // scaled, a soft local swell of the lensing, the rim light leaning towards a point, a ripple across. Nothing
    // is traced again: a frame only scales the bezel's baked offsets and light (see GlassMap.MotionView), and the
    // flat face is left as it is. The default value is neutral: the glass exactly as built, and setting it back to
    // neutral draws the static glass again to the last pixel.
    public struct GlassMotion
    {
        float bendLess, lightLess;   // (stored as 1 - the value, so that a default-constructed motion is neutral)

        // How much of the baked bend the edge shows (1 as built): 0 is a flat pane (the glass condensing as it
        // arrives), less than 1 a glass pressed thinner, more a fuller one.
        public float Bend
        {
            get { return 1 - bendLess; }
            set { bendLess = 1 - value; }
        }

        // How bright the rim's light is (1 as built).
        public float Light
        {
            get { return 1 - lightLess; }
            set { lightLess = 1 - value; }
        }

        // A soft swell of the lensing about (SwellX, SwellY) (pixels of the card, from its top left): Swell more of
        // the bend there (a share), falling off over SwellRadius pixels.
        public float SwellX, SwellY, Swell, SwellRadius;

        // The rim's light leaning towards (LeanX, LeanY) (pixels of the card): Lean (0-1) brighter on the side of
        // the rim that faces it, a little dimmer opposite.
        public float LeanX, LeanY, Lean;

        // A ripple running across the glass: Ripple (a share of the bend) up and down, RippleLength pixels from
        // crest to crest, at phase RipplePhase (radians).
        public float Ripple, RippleLength, RipplePhase;

        public static readonly GlassMotion Neutral = new GlassMotion();

        public bool IsNeutral
        {
            get { return bendLess == 0 && lightLess == 0 && Swell == 0 && Lean == 0 && Ripple == 0; }
        }

        // Whether the light differs from the light as built.
        public bool MovesLight
        {
            get { return lightLess != 0 || Lean != 0; }
        }

        public bool Same(GlassMotion o)
        {
            return bendLess == o.bendLess && lightLess == o.lightLess && SwellX == o.SwellX && SwellY == o.SwellY && Swell == o.Swell && SwellRadius == o.SwellRadius
                && LeanX == o.LeanX && LeanY == o.LeanY && Lean == o.Lean && Ripple == o.Ripple && RippleLength == o.RippleLength && RipplePhase == o.RipplePhase;
        }
    }
}
