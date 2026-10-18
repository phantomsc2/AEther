#include "../Common/Inputs.hlsl"
#include "../Common/FractalTypes.hlsl"
struct FluidParameters
{
    float Flow;
    float HalfLife;
    float Diffusion;
    float Background;
    float Curl;
    bool PeriodicBoundary;
};
struct LightingParameters
{
    float Strobe;
    float Ambient;
    float SpatialFalloff;
};
cbuffer Parameters
{
    GeometryParameters Geometry = { 0.3125, 0.45, 0.5, 0.526, 0.5 };
    QualityParameters Quality = { false, 0.37931034, 0.45 };
    FluidParameters Fluid = { 0.089, 0.145, 0, 0.324, 0.243, false };
    LightingParameters Lighting = { .7, .008, 0.5 };
};
#define MANDEL_FLUID_V3
#include "../Common/MandelboxField.hlsl"
#include "../Common/RadiatingCore.hlsl"
#include "../Common/RadiatingFluid.hlsl"
Texture2D<float4> FluidDye;
SamplerState FluidSampler;

float3 RadiatingShade(float2 uv, float sampleWidth, float pulse)
{
    float2 p = (2 * uv - 1) * float2(AspectRatio, 1);
    float2 q = p - RadiatingCenter();
    float surface = RadiatingDistances(p).x - RadiatingBoundary();
    float pixel = max(1e-5, fwidth(surface) * sampleWidth);
    float outside = smoothstep(-pixel, pixel, surface);
    // Direct projection preserves the solver's outward explosion. The
    // emitter applies the inverse projection to follow the same fractal.
    float2 fluidPoint = .55 * q;
    float2 fluidUV = .5 + .5 * fluidPoint / float2(AspectRatio, 1);
    float4 dye = FluidDye.Sample(FluidSampler, fluidUV);
    uint w, h; FluidDye.GetDimensions(w, h);
    float2 tap = 1.0 / float2(w, h);
    float2 slope = float2(
        FluidDye.Sample(FluidSampler, fluidUV + float2(tap.x, 0)).a
            - FluidDye.Sample(FluidSampler, fluidUV - float2(tap.x, 0)).a,
        FluidDye.Sample(FluidSampler, fluidUV + float2(0, tap.y)).a
            - FluidDye.Sample(FluidSampler, fluidUV - float2(0, tap.y)).a);
    // Shape wispy edges in the shading, keeping undiffused pigment in the sim.
    float density = max(0, dye.a);
    float body = (1 - exp(-.9 * density)) * smoothstep(0, .035, density);
    float3 pigment = max(0, dye.rgb) / max(.001, dye.a);
    // Cloud relief comes only from transported density, never from a second
    // procedural background. A soft point light sits just in front of the core.
    float2 gradient = slope * float2(w / AspectRatio, h) * .55 / 4;
    float3 normal = normalize(float3(-.18 * gradient, 1));
    float3 toLight = normalize(float3(-q, .45));
    float diffuse = .22 + .78 * saturate(dot(normal, toLight));
    float falloff = 1 / (1 + 100.0 * Lighting.SpatialFalloff * dot(q, q));
    float ambient = Lighting.Ambient * (.12 + 1.8 * MusicBody(GlobalSTBR));
    float illumination = 18 * Lighting.Strobe * pulse * falloff * diffuse;
    // Every light term needs real pigment. Age never gates a new flash: a
    // returning cloud in the periodic fluid can be revealed again by any hit.
    float3 light = 4.5 * pigment * body * (ambient + illumination);
    light *= outside;
    light = max(0, light);
    return light / (1 + light);
}

float4 PS(const PSin input) : SV_Target
{
    int samples = Quality.Antialiasing ? 2 : 1;
    float sampleWidth = 1.0 / samples;
    float2 uvX = ddx(input.UV), uvY = ddy(input.UV);
    float3 color = 0;
    // Flash already has a 45 ms release in AudioHistory. Positive global T
    // supplies broadband attacks even without a strong local spectral peak.
    float flash = 1 - exp(-4 * max(0, VisualFlash));
    float rise = MusicRise(GlobalSTBR);
    float pulse = max(flash * flash, rise * rise);
    [loop] for (int y = 0; y < samples; ++y)
        [loop] for (int x = 0; x < samples; ++x)
        {
            float2 offset = (float2(x, y) + .5) * sampleWidth - .5;
            color += RadiatingShade(input.UV + uvX * offset.x + uvY * offset.y, sampleWidth, pulse);
        }
    return float4(pow(saturate(color * sampleWidth * sampleWidth), 1 / 2.2), 1);
}
