using System;
using System.Reflection;
using System.Text;

namespace LGEngine
{
    // Switches for the ways the CPU backend can do its work, so that LGBench can try each (-ab) and
    // the best be kept. The defaults are the winners. (Those marked "map" are baked in when a map is
    // built; the rest are read every frame.)
    public static class GlassOptions
    {
        // (map) The bent view through the glass is worked out once per ViewStep x ViewStep pixels and
        // smoothly enlarged to the pixels; 1 is every pixel on its own. See GlassMap.
        public static int ViewStep = 1;
        // (map) With ViewStep 1: where the glass sees only the frosted picture (the face and the inner bezel, which
        // change slowly across), every other cell each way is sampled and the rest are averaged from two neighbours
        // (only where that stands in for sampling: see GlassMap.DeriveSlack); the rim is sampled at every pixel, so
        // what it bends into view stays as sharp as before. 0 samples every pixel.
        public static bool FaceStep2 = true;
        // (map) The light on the rim, the glint and the hairline outside the outline are averaged over
        // each pixel's own extent across the edge, rather than read at its centre: no beading along
        // curved edges. 0 reads them at the centre, as before.
        public static bool AnalyticEdge = true;
        // (map) The rim's less-frosted picture is enlarged with a cubic B-spline (to one texel a pixel)
        // before the rim samples it, rather than read straight with linear blending.
        public static bool CubicEdge = false;
        // A new picture of the backdrop with only part of it changed: only what that part reaches is
        // drawn again (and reported as dirty). 0 draws the whole glass for every new picture.
        public static bool Incremental = true;
        // (map) The glass is ray-traced when its map is built (see GlassTrace): a physical slab, Fresnel light from a
        // soft studio laid on in linear light, exact anti-aliasing. 0 builds it the old, hand-tuned way.
        public static bool RayTrace = true;

        public static string Describe()
        {
            StringBuilder sb = new StringBuilder();
            foreach (FieldInfo f in typeof(GlassOptions).GetFields(BindingFlags.Static | BindingFlags.Public))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(f.Name).Append('=').Append(f.GetValue(null));
            }
            return sb.ToString();
        }
    }
}
