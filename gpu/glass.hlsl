// Liquid Glass on the GPU (prototype). The look mirrors the CPU engine, src/GlassEngine.cs
// (GlassMap): same outline, bezel profile and bend tables, lighting, tint, underlay and ink.
// Everything is analytic per pixel; the outline is anti-aliased by its signed distance.

Texture2D texA       : register(t0);   // the input of a helper pass
Texture2D texSoft    : register(t1);   // the frosted picture of what is behind (SOFT_SCALE px per texel)
Texture2D texEdge    : register(t2);   // the lightly frosted one for the bezel (EDGE_SCALE px per texel)
Texture2D texBend    : register(t3);   // 1025 x 1: bendAt, bendIn, reachW across the bezel (t = 0 face, 1 rim)
Texture2D texContent : register(t4);   // the pop-up's own contents, premultiplied BGRA
Texture2D texHalo    : register(t5);   // the contents' ink softened (R), half size
Texture2D texStats   : register(t6);   // 1 x 1: average colour of what is behind the card; w = midLuma*1000 + spread
SamplerState smpLinear : register(s0);
SamplerState smpPoint  : register(s1);
SamplerState smpBorder : register(s2);

cbuffer Params : register(b0) { float4 p[32]; };

#define WIN_SIZE     p[0].xy
#define MARGIN       p[0].z
#define SCALE_K      p[0].w
#define HX           p[1].x
#define HY           p[1].y
#define RAD          p[1].z
#define CORNER       p[1].w
#define CORNER_INV   p[2].x
#define BEVEL        p[2].y
#define RV           p[2].z
#define MAGNIFY      p[2].w
#define PAD          p[3].x
#define BACK_SIZE    p[3].yz
#define CARD_OFF     p[3].w      // extra offset of the card inside the window (animation), x only here; see p[23]
#define SOFT_SIZE    p[4].xy
#define SOFT_SCALE   p[4].z
#define EDGE_SIZE    p[5].xy
#define EDGE_SCALE   p[5].z
#define LIGHT_DIR    p[6].xy
#define SPEC_MIX     p[6].z
#define SPEC_FADE    p[6].w
#define GLOW_MIX     p[7].x
#define GLOW_DEPTH   p[7].y
#define GLOW_POWER   p[7].z
#define LIGHT_LEAST  p[7].w
#define AWAY_SHARE   p[8].x
#define GLINT_MIX    p[8].y
#define GLINT_FADE   p[8].z
#define GLINT_AWAY   p[8].w
#define OUTLINE_A    p[9].x
#define OUTLINE_W    p[9].y
#define SHADOW_SPREAD p[9].z
#define SHADOW_DROP  p[9].w
#define SHADOW_AMT   p[10].x
#define INTENSITY    p[10].y
#define EDGE_LIGHT   p[10].z
#define FACE_GLOW    p[10].w
#define SATURATION   p[11].x
#define DIM          p[11].y
#define ADD_RGB      float3(p[11].z, p[11].w, p[12].x)
#define EVENING      p[12].y
#define VEIL_SHARE   p[12].z
#define APPEARANCE   p[12].w     // 0 dark, 1 light, < 0 adapt to what is behind
#define CHROMA_SPLIT p[13].x
#define SPLIT_MOST   p[13].y
#define FRINGE_FROM  p[13].z
#define BLEND_FROM   p[13].w
#define BLEND_BAND   p[14].x
#define REACH_SOFT   p[14].y
#define BOTTOM_REACH p[14].z
#define GLOW_SPAN    p[14].w
#define CONTENT_OFF  p[15].xy    // where the contents' top-left is, in window pixels
#define CONTENT_SIZE p[15].zw
#define HALO_SIZE    p[16].xy
#define UNDERLAY     p[16].z
#define OPACITY      p[16].w
#define HOVER        p[17]       // x, y (window px), strength, radius
#define BLUR         p[18]       // x, y: one texel step in uv; z: sigma (texels); w: radius (texels)
#define TEXEL        p[19].xy    // 1 / size of the pass input
#define BOX_R        p[19].z
#define STATS_AREA   p[20]       // the card in the soft picture (uv rect)
#define STATS_MID    p[21]       // its middle (uv rect)
#define SHADOW_AUTO  p[22].x     // 1: the shadow deepens over bright or busy backdrops (as the app does)
#define DEBUG_MODE   p[22].y     // 1: show what the glass reads of the backdrop's stats
#define CARD_SHIFT   p[23].xy    // where the card's middle is, from the window's middle (animation)

static const float LightVeil = 0.5, DimPerUnit = 0.65, EvenVeil = 0.7, EvenDim = 0.3, EvenMix = 0.45;
static const float3 LumaW = float3(77, 150, 29) / 256.0;

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.uv = uv;
    return o;
}

// ---- helper passes ----

// Copies, or shrinks by two (sampled between four texels, which averages them).
float4 PSSample(VSOut i) : SV_Target { return texA.Sample(smpLinear, i.uv); }

// A 1-2-1 smoothing in both directions (the CPU engine's Smooth), from four bilinear taps.
float4 PSTent(VSOut i) : SV_Target
{
    float2 o = TEXEL * 0.5;
    return 0.25 * (texA.Sample(smpLinear, i.uv + float2(-o.x, -o.y)) + texA.Sample(smpLinear, i.uv + float2(o.x, -o.y))
                 + texA.Sample(smpLinear, i.uv + float2(-o.x, o.y)) + texA.Sample(smpLinear, i.uv + float2(o.x, o.y)));
}

// One direction of a Gaussian blur.
float4 PSBlur(VSOut i) : SV_Target
{
    float sigma = max(BLUR.z, 0.01);
    int r = (int)BLUR.w;
    float4 sum = 0;
    float wsum = 0;
    [loop]
    for (int j = -r; j <= r; j++)
    {
        float w = exp(-(j * j) / (2.0 * sigma * sigma));
        sum += w * texA.Sample(smpLinear, i.uv + BLUR.xy * j);
        wsum += w;
    }
    return sum / wsum;
}

// The contents' ink (alpha) at half size, into R.
float4 PSAlphaDown(VSOut i) : SV_Target { return texA.Sample(smpBorder, i.uv).a; }

// One direction of a box average of the ink (beyond the edge counts as nothing).
float4 PSBox(VSOut i) : SV_Target
{
    int r = (int)BOX_R;
    float s = 0;
    [loop]
    for (int j = -r; j <= r; j++) s += texA.Sample(smpBorder, i.uv + BLUR.xy * j).r;
    return s / (2 * r + 1);
}

// What the glass needs to know about the picture behind the card (GlassBackdrop.Measure): its
// average colour, how uneven it is (the spread of its brightness) and how bright its middle is.
float4 PSStats(VSOut i) : SV_Target
{
    float3 sum = 0;
    float sumL = 0, sumLL = 0, midL = 0;
    float n = 0, midN = 0;
    [loop]
    for (int y = 0; y < 24; y++)
    {
        [loop]
        for (int x = 0; x < 24; x++)
        {
            float2 f = (float2(x, y) + 0.5) / 24.0;
            float2 uv = lerp(STATS_AREA.xy, STATS_AREA.zw, f);
            float3 c = texSoft.Sample(smpLinear, uv).rgb * 255;
            float l = dot(c, float3(299, 587, 114) / 1000.0);
            sum += c; sumL += l; sumLL += l * l; n += 1;
            if (uv.x >= STATS_MID.x && uv.x < STATS_MID.z && uv.y >= STATS_MID.y && uv.y < STATS_MID.w) { midL += l; midN += 1; }
        }
    }
    float mean = sumL / n;
    float spread = sqrt(max(0, sumLL / n - mean * mean));
    float mid = midN > 0 ? midL / midN : mean;
    return float4(sum / n / 255.0, floor(mid) * 1000 + min(spread, 999));
}

// ---- the glass ----

// Signed distance (negative inside) from q (measured from the card's middle) to its outline, and
// the way out of it: a rectangle whose corners are superellipses (CORNER_INV = 1/n; 0 for arcs).
float Outline(float2 q, out float2 n)
{
    float2 a = abs(q) - (float2(HX, HY) - CORNER);
    float2 s = float2(q.x < 0 ? -1 : 1, q.y < 0 ? -1 : 1);
    if (a.x > 0 && a.y > 0)
    {
        float len = length(a);
        float2 d = a / len;
        float sd;
        if (CORNER_INV <= 0)
        {
            sd = len - CORNER;
            n = d;
        }
        else
        {
            float nn = 1.0 / CORNER_INV;
            float gx = pow(max(d.x, 1e-6), nn - 1), gy = pow(max(d.y, 1e-6), nn - 1);
            float r = 1.0 / pow(gx * d.x + gy * d.y, CORNER_INV);
            sd = len - CORNER * r;
            n = normalize(float2(gx, gy));
        }
        n *= s;
        return sd;
    }
    if (a.x > a.y) { n = float2(s.x, 0); return a.x - CORNER; }
    n = float2(0, s.y);
    return a.y - CORNER;
}

// What is drawn outside the glass (alpha over black): the shadow, a little below the card, and
// the hairline darker edge along the outline. sd: distance outside the outline.
float OutsideShade(float2 q, float sd, float amount)
{
    float cx = abs(q.x) - (HX - RAD), cy = abs(q.y - SHADOW_DROP) - (HY - RAD);
    float s2 = (cx > 0 && cy > 0) ? length(float2(cx, cy)) - RAD : max(cx, cy) - RAD;
    s2 = max(s2, 0);
    float shade = amount * exp(-(s2 * s2) / (2 * SHADOW_SPREAD * SHADOW_SPREAD));
    float edgeLine = 1 - sd / OUTLINE_W;
    return edgeLine > 0 ? max(shade, OUTLINE_A * edgeLine) : shade;
}

// Cubic B-spline sampling from four bilinear taps (the CPU engine's "fine" enlargement: smooth,
// never overshooting, no star-shaped grain from coarse texels). coord in texels.
float4 SampleBSpline(Texture2D t, float2 coord, float2 size)
{
    float2 tc = coord - 0.5;
    float2 f = frac(tc), i0 = floor(tc);
    float2 f2 = f * f, f3 = f2 * f;
    float2 w0 = (1 - 3 * f + 3 * f2 - f3) / 6, w1 = (3 * f3 - 6 * f2 + 4) / 6, w2 = (-3 * f3 + 3 * f2 + 3 * f + 1) / 6, w3 = f3 / 6;
    float2 s0 = w0 + w1, s1 = w2 + w3;
    float2 p0 = (i0 - 0.5 + w1 / s0) / size, p1 = (i0 + 1.5 + w3 / s1) / size;
    return t.Sample(smpLinear, float2(p0.x, p0.y)) * s0.x * s0.y + t.Sample(smpLinear, float2(p1.x, p0.y)) * s1.x * s0.y
         + t.Sample(smpLinear, float2(p0.x, p1.y)) * s0.x * s1.y + t.Sample(smpLinear, float2(p1.x, p1.y)) * s1.x * s1.y;
}

struct TintK { float sat, mul, keep; float3 add, pull; };

// The glass's colour: richer, dimmer, tinted (GlassMap.Tint), and over a lopsided backdrop pulled
// towards its own tone.
float3 Tint(float3 c, TintK k)
{
    float l = dot(c, LumaW);
    return (c * k.sat * k.mul + (l * (1 - k.sat) * k.mul + k.add)) * k.keep + k.pull;
}

float4 PSGlass(VSOut i) : SV_Target
{
    float2 pix = i.pos.xy;   // pixel centre
    float2 q = pix - WIN_SIZE * 0.5 - CARD_SHIFT;   // from the card's middle
    float2 n;
    float sd = Outline(q, n);
    float coverage = saturate(0.5 - sd);

    // what is behind, measured
    float4 st = texStats.Load(int3(0, 0, 0));
    float3 avg = st.xyz;
    float mid = floor(st.w / 1000), spread = st.w - mid * 1000;
    float light = APPEARANCE >= 0 ? APPEARANCE : smoothstep(110, 165, mid);
    float u = saturate((spread - 24) / 46.0);
    u = u * u * (3 - 2 * u);
    float even = u * u * (3 - 2 * u) * EVENING;
    float shadowGain = SHADOW_AUTO > 0 ? min(1.3, 0.85 + 0.45 * saturate((mid - 80) / 150.0) + 0.25 * saturate((spread - 20) / 70.0)) : 1;
    if (DEBUG_MODE == 1) return float4(light, even, mid / 255.0, 1);
    if (DEBUG_MODE == 2) return float4(st.xyz, 1);
    float shadeOut = OutsideShade(q, max(sd, 0), SHADOW_AMT * shadowGain);
    if (coverage <= 0) return float4(0, 0, 0, shadeOut) * OPACITY;

    // the tint's constants (GlassMap.Tint)
    TintK k;
    k.sat = SATURATION;
    float veil = max(1 - DIM, LightVeil * VEIL_SHARE);
    veil += max(0, EvenVeil - veil) * even;
    float dimmed = DIM * (1 - EvenDim * even);
    k.mul = dimmed + (1 - veil - dimmed) * light;
    k.add = lerp(ADD_RGB, veil.xxx, light);
    float mix = EvenMix * even;
    k.keep = 1 - mix;
    float avgL = dot(avg, LumaW);
    k.pull = mix * saturate(avg * k.sat * k.mul + avgL * (1 - k.sat) * k.mul + k.add);

    // across the bezel: t is 0 where the flat face begins and 1 at the rim; dir the way out
    float2 va = abs(q) - (float2(HX, HY) - RV);
    float2 s = float2(q.x < 0 ? -1 : 1, q.y < 0 ? -1 : 1);
    float vsd;
    float2 dir;
    if (va.x > 0 && va.y > 0) { float vlen = length(va); vsd = vlen - RV; dir = va / vlen * s; }
    else if (va.x > va.y) { vsd = va.x - RV; dir = float2(s.x, 0); }
    else { vsd = va.y - RV; dir = float2(0, s.y); }
    float t = vsd >= 0 ? 1 : 1 + vsd / BEVEL;

    float2 look = PAD + float2(HX, HY) + q / MAGNIFY;   // where this pixel looks straight down to (backdrop px)
    float top = max(0, 1 - (q.y + HY) / GLOW_SPAN);
    float3 col;
    if (t <= 0)
    {
        col = Tint(SampleBSpline(texSoft, look / SOFT_SCALE, SOFT_SIZE).rgb, k) + top * top * FACE_GLOW;
    }
    else
    {
        t = min(t, 1);
        float3 tab = texBend.Sample(smpLinear, float2((t * 1024 + 0.5) / 1025.0, 0.5)).xyz;
        float bend = tab.x, reachHere = tab.z;
        if (n.y > 0 && BOTTOM_REACH < 1)
        {
            float less = n.y * (1 - BOTTOM_REACH);
            bend += (tab.y - bend) * less;
            reachHere *= 1 - less;
        }
        float2 shift = -dir * bend;
        float bendLen = length(shift);
        float split = CHROMA_SPLIT;
        if (bendLen * split > SPLIT_MOST) split = SPLIT_MOST / max(bendLen, 1e-4);
        float fringe = saturate((t - FRINGE_FROM) / (1 - FRINGE_FROM) * 1.1);
        split *= fringe * fringe * (3 - 2 * fringe);
        if (bendLen * split < 0.35) split = 0;
        float ease = saturate((t - BLEND_FROM * BLEND_BAND) / ((1 - BLEND_FROM) * BLEND_BAND));
        float share = ease >= 1 ? 1 : ease * ease * (3 - 2 * ease) * (254 / 255.0);
        share = min(share, 1 - REACH_SOFT * reachHere);
        if (share < 8 / 255.0) share = 0; else if (share > 247 / 255.0) share = 1;

        float3 soft = Tint(SampleBSpline(texSoft, (look + shift) / SOFT_SCALE, SOFT_SIZE).rgb, k);
        float3 seen = soft;
        if (share > 0)
        {
            float2 pr = (look + shift * (1 + split)) / EDGE_SCALE;
            float3 er = Tint(texEdge.Sample(smpLinear, pr / EDGE_SIZE).rgb, k);
            float3 eb = er;
            if (split > 0)
            {
                float2 pb = (look + shift * (1 - split)) / EDGE_SCALE;
                eb = Tint(texEdge.Sample(smpLinear, pb / EDGE_SIZE).rgb, k);
            }
            seen = lerp(soft, float3(er.r, (er.g + eb.g) * 0.5, eb.b), share);
        }

        // lit as a glossy curved surface from the upper left (SpecMix, GlowMix, GlintMix)
        float depth = -sd;
        float facing = dot(n, LIGHT_DIR);
        float toward = max(facing, 0), away = max(-facing, 0);
        float lit = LIGHT_LEAST + (1 - LIGHT_LEAST) * (sqrt(toward) * toward + AWAY_SHARE * sqrt(away) * away);
        float inner = 1 - depth / GLOW_DEPTH;
        float specAt = depth / SPEC_FADE;
        float white = ((specAt < 7 ? SPEC_MIX * exp(-specAt) : 0) + (inner > 0 ? GLOW_MIX * pow(inner, GLOW_POWER) : 0)) * lit * EDGE_LIGHT;
        float t2 = toward * toward, t4 = t2 * t2, a2 = away * away, a4 = a2 * a2;
        float glint = t4 * t4 + GLINT_AWAY * a4 * a4;
        if (glint > 0.01) white += GLINT_MIX * glint * exp(-depth / GLINT_FADE) * EDGE_LIGHT;
        white = min(white, 0.95);
        float glow = top * top * FACE_GLOW * (1 - t);
        col = seen * (1 - white) + glow * (1 - white) + white;
    }

    // a hover light (an extra of the GPU path: a soft white spot that follows the pointer)
    if (HOVER.z > 0)
    {
        float d = distance(pix, HOVER.xy);
        float h = HOVER.z * exp(-(d * d) / (2 * HOVER.w * HOVER.w));
        col = col * (1 - h) + h;
    }

    // the contents, with the underlay beneath their ink (only on fully covered pixels, as the CPU engine)
    if (coverage >= 1)
    {
        float2 cpx = pix - CONTENT_OFF;
        bool inside = cpx.x >= 0 && cpx.y >= 0 && cpx.x < CONTENT_SIZE.x && cpx.y < CONTENT_SIZE.y;
        if (UNDERLAY > 0 && inside)
        {
            float hv = texHalo.Sample(smpBorder, cpx / CONTENT_SIZE).r;
            hv = saturate(hv * 3);
            hv = 1 - (1 - hv) * (1 - hv);
            float darkUnder = UNDERLAY * (1 - light), lightUnder = UNDERLAY * light;
            col = saturate(col);
            col += ((1 - col) * lightUnder - col * darkUnder) * hv;
        }
        if (inside)
        {
            float4 c = texContent.Sample(smpPoint, cpx / CONTENT_SIZE);
            if (c.a > 0)
            {
                // grey ink (text, lines) is turned from light to dark on light glass; an odd
                // alpha marks an icon's own white, which stays (GlassOverlay.MarkInk)
                float a8 = round(c.a * 255);
                if (light > 0 && fmod(a8, 2) < 0.5)
                {
                    float mx = max(c.r, max(c.g, c.b)), mn = min(c.r, min(c.g, c.b));
                    if ((mx - mn) * 255 < 40) c.rgb = clamp(c.rgb + (c.a - 2 * c.rgb) * light, 0, c.a);
                }
                col = col * (1 - c.a) + c.rgb;
            }
        }
    }
    col = saturate(col);
    float a = coverage + shadeOut * (1 - coverage);
    return float4(col * coverage, a) * OPACITY;
}
