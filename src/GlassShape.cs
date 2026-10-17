using System;

namespace LGEngine
{
    // Everything about the glass that depends only on its size and the look settings, worked out
    // once so that each frame is just "look up, sample, add".
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
    // The shape of a piece of glass and how it bends what is behind it.
    //
    // A notification is a broad, flat card: a flat face with a fine curved rim round the edge
    // (Card). The volume and brightness indicator is a small capsule, and Apple's small glass
    // controls are curved right across, like a glass rod or bead (Lens): the whole of it bends
    // and enlarges what is behind, most of all towards the edge, where the colours part a little,
    // and the glass is clear enough there for the bending to be seen.
    public sealed class GlassShape
    {
        public string Name;
        public float BevelShare, BevelLeast, BevelMost;   // the curved part: a share of the shorter side, within limits (at 100% scaling)
        public float ProfilePower;                        // its cross-section: (1 - t^p)^(1/p); 4 a flat top with a rounded shoulder, 2 a round rod
        public float Magnify;                             // how much the glass enlarges what is behind it, at full refraction (1 = not at all)
        public float Thickness;                           // glass under the curve, as a share of its width: more bends the view further
        public float RimCreep;                            // how fast the view still moves on where the bend would otherwise fold it back
        public float Dispersion;                          // how far apart red and blue are bent, at full refraction
        public float EdgeFrost, EdgeFrostLeast;           // how much softer the curve's picture is than none: a share of the face's, and at least this blur radius
        public float BlendBand;                           // share of the curve, from its inner side, over which face eases into it
        public float EdgeReach, EdgeReachFrom;            // the outer part of the curve (from this share of it outwards) looks out past the edge, as far as this share of the curve's width: what is just outside the glass is seen bent round into its rim, the way thick glass shows it
        public float ReachSoftness;                       // how much of the frosted picture that outer part shows (0 none): what it pulls in reads as a soft glow, not a crisp squeezed line
        public float BottomReach;                         // share of that reach kept along the bottom edge (1 all)
        // How clear the glass is, for the same slider settings (see GlassMap.Tint): the share of the
        // Blur slider's softening its face gets (FaceFrost), of the Dark slider's dimming and tint
        // (Dimming), of the evening out over a lopsided backdrop (Evening), and of the white veil
        // light glass gets at least (Veil). A small control is clearer than a broad card: there is
        // less on it to keep readable, and all of it is near its bright rim.
        public float FaceFrost = 1f, Dimming = 1f, Evening = 1f, Veil = 1f;
        // How it is lit: the light on the rim (SpecGain) and the glow inside it (GlowGain), both
        // against the rim slider's; how far black behind dark glass is lifted (Lift, 0-255: over a
        // dark window Apple's glass still shows as a lighter, lit shape, not a hole); and how far
        // its shadow spreads, against the card's (ShadowShare: a small control sits closer to the
        // screen).
        public float SpecGain = 1f, GlowGain = 1f, Lift = 0f, ShadowShare = 1f;
        // The ray-traced glass (GlassOptions.RayTrace, see GlassTrace), each a share of the curve's width at the
        // default Refraction and Elevation: the shoulder's height (TraceRise), the slab under it (TraceSlab), and the
        // gap between the glass and the screen (TraceGap, at Elevation 50; none at 0, twice as much at 100).
        public float TraceRise = 0.3f, TraceSlab = 2.2f, TraceGap = 0.4f, TracePower = 4f;   // (TracePower: the shoulder's profile, as ProfilePower)
        // A hand-drawn cross-section (see GlassProfile) in place of the power-law shoulder above: null = the analytic
        // shoulder, exactly as it has always been. Set it with WithProfile (a shape is made afresh for every use,
        // GlassShape.Card above, so the profile is carried by a copy, never written into one in use); it is part of
        // GlassMap's Signature.
        public GlassProfile Profile;

        // A copy of this shape with this profile (null: today's analytic shoulder).
        public GlassShape WithProfile(GlassProfile profile)
        {
            GlassShape s = (GlassShape)MemberwiseClone();
            s.Profile = profile;
            return s;
        }

        // (Made from GlassMap's adjustable numbers each time, so that the test rig can try others.)
        public static GlassShape Card
        {
            get
            {
                GlassShape c = new GlassShape();
                c.Name = "card";
                c.BevelShare = GlassMap.BevelShare; c.BevelLeast = GlassMap.BevelLeast; c.BevelMost = GlassMap.BevelMost;
                c.ProfilePower = GlassMap.ProfilePower; c.Magnify = GlassMap.Magnify; c.Thickness = GlassMap.Thickness;
                c.RimCreep = GlassMap.RimCreep; c.Dispersion = GlassMap.Dispersion; c.EdgeFrost = GlassMap.EdgeFrost;
                c.EdgeFrostLeast = GlassMap.EdgeFrostLeast; c.BlendBand = GlassMap.BlendBand;
                c.EdgeReach = GlassMap.EdgeReach; c.EdgeReachFrom = GlassMap.EdgeReachFrom;
                c.ReachSoftness = GlassMap.ReachSoftness; c.BottomReach = GlassMap.BottomReach;
                c.FaceFrost = GlassMap.CardFaceFrost; c.Dimming = GlassMap.CardDimming; c.Evening = GlassMap.CardEvening; c.Veil = GlassMap.CardVeil;
                c.Lift = GlassMap.CardLift;
                return c;
            }
        }

        // Tuned by eye against Apple's small glass controls, over text and over colour.
        public static readonly GlassShape Lens = MakeLens();

        static GlassShape MakeLens()
        {
            GlassShape l = new GlassShape();
            l.Name = "lens";
            l.BevelShare = 0.5f; l.BevelLeast = 5f; l.BevelMost = 40f;   // the whole capsule is curved
            l.ProfilePower = 2.5f;
            l.Magnify = 1.2f;
            l.Thickness = 1.0f;
            l.RimCreep = 0.25f;
            l.Dispersion = 0.06f;
            l.EdgeFrost = 0.2f; l.EdgeFrostLeast = 0.15f;   // (soft enough that what the rim draws out is a smooth smear, as in Apple's, not a stretched fringe of text)
            l.BlendBand = 0.6f;
            l.EdgeReach = 0.45f; l.EdgeReachFrom = 0.45f;
            l.ReachSoftness = 0.85f;
            // The indicator sits just above the taskbar: what is in the gap below it is only ever the
            // bottom of a window (a border, a status bar), which pulled into the rim reads as a stray
            // line under it. Its bottom edge looks out only a little.
            l.BottomReach = 0.15f;
            // Clear, as Apple's small controls are: what is behind stays recognisable through a
            // light frost, little dimmed, and bent at the rim.
            l.FaceFrost = 0.3f;
            l.Dimming = 0.7f;
            l.Evening = 0.2f;
            l.Veil = 0.3f;
            // And lit like a bead: a brighter line on the rim with a deeper glow inside it, so that
            // over a dark window it still reads as a lit shape; its black lifted a little; and a
            // shadow that stays close to it.
            l.SpecGain = 1.4f;
            l.GlowGain = 2.6f;
            l.Lift = 10f;
            l.ShadowShare = 0.7f;
            l.TraceRise = 0.5f; l.TraceSlab = 1.2f; l.TraceGap = 0.4f; l.TracePower = 2f;
            return l;
        }
    }
}
