#include "../Common/Inputs.hlsl"

struct BranchSettings { float Reach; float Spread; float Angle; float Bend; float Fold; };
struct InkSettings { float Width; float Gain; float Memory; };
cbuffer Parameters
{
    BranchSettings Branches = { .55, .5, .45, .15, 0 };
    InkSettings Ink = { .35, .5, .55 };
};
Texture2D<float4> PreviousImage;

// Local coordinates: x runs along an edge; y grows inward from that edge.
float2 ToUV(float2 p, int edge)
{
    if (edge == 0) return float2(.5 + .5 * p.x, p.y);
    if (edge == 1) return float2(1 - p.y, .5 + .5 * p.x);
    if (edge == 2) return float2(.5 - .5 * p.x, 1 - p.y);
    return float2(p.y, .5 - .5 * p.x);
}

float3 ReadInk(float2 p, int edge)
{
    float2 uv = ToUV(p, edge);
    // Explicit zero outside the texture; never repeat or smear its borders.
    if (any(uv < 0) || any(uv > 1)) return 0;
    return PreviousImage.SampleLevel(Linear, uv, 0).rgb;
}

float3 Branch(float2 p, int edge, float2 s, float a, float2 b)
{
    // Pull back an affine copy. The optional shear and abs activation have
    // explicit inverses/preimages, so no distance estimator or optimization.
    float2 q = p - b;
    float c = cos(a), t = sin(a);
    q = float2(c * q.x + t * q.y, -t * q.x + c * q.y) / s;
    q.x -= .5 * Branches.Bend * sin(6 * q.y);
    float3 ink = ReadInk(q, edge);
    if (Branches.Fold > 0)
    {
        float2 f = float2(abs(q.x), q.y);
        ink = lerp(ink, max(ReadInk(f, edge), ReadInk(float2(-f.x, f.y), edge)), Branches.Fold);
    }
    return ink;
}

float4 PS(PSin input) : SV_Target
{
    float2 uv = input.UV;
    float3 ink = 0;
    [unroll] for (int edge = 0; edge < 4; edge++)
    {
        float2 p = edge == 0 ? float2(2 * uv.x - 1, uv.y)
            : edge == 1 ? float2(2 * uv.y - 1, 1 - uv.x)
            : edge == 2 ? float2(1 - 2 * uv.x, 1 - uv.y)
            : float2(1 - 2 * uv.y, uv.x);
        float a = .2 + .9 * Branches.Angle;
        float w = .22 + .22 * Branches.Spread;
        float h = .68 + .22 * Branches.Reach;
        float3 copies = Branch(p, edge, float2(.88, h), .025, float2(0, .075));
        copies = max(copies, Branch(p, edge, float2(w, .43), a, float2(-.055, .18)));
        copies = max(copies, Branch(p, edge, float2(w, .43), -a, float2(.055, .18)));
        ink = max(ink, copies);

        float f = saturate(.5 + .5 * p.x);
        float4 audio = max(0, .5 * (Spectrum0.SampleLevel(Linear, float2(f, .5), 0)
            + Spectrum1.SampleLevel(Linear, float2(f, .5), 0)));
        float energy = 1 - exp(-(audio.x + .35 * audio.y) * (1 + 12 * Ink.Gain));
        float width = max(1.5 / max(Resolution.x, Resolution.y), .002 + .012 * Ink.Width);
        // Several narrow roots on each edge give repeated fern-like crowns.
        float root = 1 - smoothstep(.015, .045, abs(frac(f * 7) - .5));
        float seed = root * (1 - smoothstep(width, 2 * width, p.y)) * energy;
        float3 tint = .55 + .45 * cos(6.2831853 * (f * .4 + edge * .13 + float3(0, .3, .6)));
        ink = max(ink, seed * tint);
    }
    // Max union cannot amplify light as a sum of branches would. Every update
    // contracts intensity, and a small floor removes half-float residuals.
    ink *= .87 + .1 * Ink.Memory;
    ink = max(0, ink - .0001);
    return float4(ink, 1);
}

float4 FeedbackDisplayPS(PSin input) : SV_Target
{
    return float4(PreviousImage.SampleLevel(Linear, input.UV, 0).rgb, 1);
}
