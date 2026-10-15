// Compiled before Mandelbox.hlsl, sharing its field, onset fronts and spatial AA.
#define MANDEL_FLUID
Texture2D<float4> FluidDye : register(t4);
SamplerState FluidSampler : register(s1);

float3 FluidLight(float2 uv, float relief)
{
    float4 dye = FluidDye.Sample(FluidSampler, uv);
    // The moving pigment lights the folds without pinning its motion to them.
    float scattering = lerp(.65, 1.3, relief);
    float3 light = dye.rgb * scattering * 1.65;
    // Broad, dim scattering gives the wisps volume around the crisp structure.
    uint w, h; FluidDye.GetDimensions(w, h);
    float2 spread = 2.5 / float2(w, h);
    float3 halo = FluidDye.Sample(FluidSampler, uv + float2(spread.x, 0)).rgb
        + FluidDye.Sample(FluidSampler, uv - float2(spread.x, 0)).rgb
        + FluidDye.Sample(FluidSampler, uv + float2(0, spread.y)).rgb
        + FluidDye.Sample(FluidSampler, uv - float2(0, spread.y)).rgb;
    return light + .12 * halo;
}
