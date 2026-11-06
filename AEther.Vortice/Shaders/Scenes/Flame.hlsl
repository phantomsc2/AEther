#include "../Common/Inputs.hlsl"

struct FernSettings
{
    float Growth;
    float Bend;
    float Spread;
    float FrondScale;
};

struct FilteringSettings
{
    bool Mipmaps;
    bool Trilinear;
    bool Anisotropic;
};

cbuffer Parameters
{
    FernSettings Fern = {.5, .75, .5, .5};
    FilteringSettings Filtering = {false, false, false};
    float DensityGain = .5;
};

Texture2D<float> FlameDensity;
SamplerState FlameSampler;
SamplerState FlameDisplaySampler;

// Physical domain [-4,4] x [0,12]. Matrix rows are stored explicitly.
struct Branch
{
    float4 A;
    float2 Center;
};

Branch FernBranch(int i)
{
    Branch f;
    float g = .78 + .14 * Fern.Growth;
    float b = -.08 + .16 * Fern.Bend;
    f.A = float4(g, b, -b, g);
    f.Center = float2(0, 6.7);
    if (i != 0)
    {
        float a = radians(-20 + 40 * Fern.Spread) * (i == 1 ? 1 : -1);
        float k = .75 + .5 * Fern.FrondScale;
        float4 base = i == 1 ? float4(.20, -.26, .23, .22) : float4(-.15, .28, .26, .24);
        f.A = k * float4(cos(a) * base.xy - sin(a) * base.zw, sin(a) * base.xy + cos(a) * base.zw);
        f.Center = i == 1 ? float2(-1.56, 2.92) : float2(1.68, 2.84);
    }
    // This is part of the affine family, not a correction to transported density.
    float2 e = float2(dot(abs(f.A.xy), float2(4, 6)), dot(abs(f.A.zw), float2(4, 6)));
    float2 available = .98 * float2(4, 6) - abs(f.Center - float2(0, 6));
    f.A *= min(1, min(available.x / e.x, available.y / e.y));
    return f;
}

float2 DensityUV(float2 p) { return float2((p.x + 4) / 8, 1 - p.y / 12); }

float IteratePS(PSin input) : SV_Target
{
    float2 y = float2(8 * input.UV.x - 4, 12 * (1 - input.UV.y));
    float density = 0;
    [unroll] for (int i = 0; i < 3; ++i)
    {
        Branch f = FernBranch(i);
        float det = f.A.x * f.A.w - f.A.y * f.A.z;
        float2 d = y - f.Center;
        float2 x = float2(f.A.w * d.x - f.A.y * d.y, -f.A.z * d.x + f.A.x * d.y) / det + float2(0, 6);
        float2 uv = DensityUV(x);
        // UV change per destination pixel, including physical-domain scale and Y inversion.
        float2 dx = float2(f.A.w, -f.A.z) * (8 / Resolution.x) / det;
        float2 dy = float2(-f.A.y, f.A.x) * (-12 / Resolution.y) / det;
        float2 du_dx = dx * float2(1.0 / 8, -1.0 / 12);
        float2 du_dy = dy * float2(1.0 / 8, -1.0 / 12);
        // Zero-border filtering retains footprints that overlap the domain even if their center is outside.
        density += FlameDensity.SampleGrad(FlameSampler, uv, du_dx, du_dy) / (3 * abs(det));
    }
    return density;
}

float4 PS(PSin input) : SV_Target
{
    float scale = .95 * min(Resolution.x / 8, Resolution.y / 12);
    float2 p = (input.Position.xy - .5 * Resolution) / scale * float2(1, -1) + float2(0, 6);
    float2 uv = DensityUV(p);
    float r = 0;
    if (all(uv >= 0) && all(uv <= 1)) r = FlameDensity.SampleLevel(FlameDisplaySampler, uv, 0);
    float brightness = log2(1 + exp2(-4 + 16 * DensityGain) * r);
    return LinearOutput(float3(.18, 1, .35) * brightness);
}
