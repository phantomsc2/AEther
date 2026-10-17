#include "../Common/Inputs.hlsl"
#include "../Common/FractalTypes.hlsl"
struct FluidParameters
{
    float Flow;
    float HalfLife;
    float Diffusion;
    float Background;
    float Curl;
};
struct CometParameters
{
    float Size;
    float Morph;
    float Tail;
    float Shockwaves;
    float Background;
};
struct LightingParameters
{
    float FadeTime;
    float Ambient;
};
cbuffer Parameters
{
    GeometryParameters Geometry = { 0.3125, 0.45, 0.5, 0.44, 0.5 };
    QualityParameters Quality = { false, 0.37931034, 0.45 };
    FluidParameters Fluid = { 0.12, 1, 0, 0.069, 0.543 };
    CometParameters Comet = { 1, 0.84, 0.8, 1, 0.472 };
    LightingParameters Lighting = { .1, .04 };
};
#define MANDEL_FLUID_V3
#include "../Common/MandelboxField.hlsl"
#include "../Common/MandelComet.hlsl"
#include "../Common/RadiatingFluid.hlsl"
Texture2D<float4> FluidDye;
Texture2D<float2> FluidAge;
SamplerState FluidSampler;

float3 RadiatingShade(float2 uv, float sampleWidth)
{
    float2 p = (2 * uv - 1) * float2(AspectRatio, 1);
    float2 q = p - CometHead();
    float2 distances = CometDistances(p);
    float surface = distances.x - CometBoundary();
    float pixel = max(1e-5, fwidth(surface) * sampleWidth);
    float outside = smoothstep(-pixel, pixel, surface);
    float relief = smoothstep(-7, -.3, log2(distances.x / distances.y));
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
    // Density gradients supply wet highlights; orbit relief only embosses
    // the material, so notes can spread and mix instead of drawing thin rays.
    // Express the gradient in world units so doubling the grid does not halve
    // the wet highlights. Keep the response at the former 256-cell baseline.
    float sheen = saturate(5 * length(slope * float2(w / AspectRatio, h) / 256));
    float3 material = (1 + 3 * Comet.Tail) * pigment * body
        * (.4 + .65 * relief);
    material += lerp(pigment, float3(.85, .94, 1), .35) * body * sheen * Comet.Tail;

    // Current transients activate every retained plume of the matching color,
    // independent of its position. Stereo blends to illuminate the
    // whole frequency population together.
    float frequency = RadiatingFrequency(pigment);
    float width = min(.015, fwidth(frequency) * sampleWidth);
    float live = .5 * CurrentSpectrum(frequency, .5).g
        + .25 * (CurrentSpectrum(frequency - .5 * width, .5).g
            + CurrentSpectrum(frequency + .5 * width, .5).g);
    float attack = 1 - exp(-6 * max(0, live));
    float strobe = 1 - exp(-4 * max(0, VisualFlash));
    float pulse = .8 * attack * attack + .2 * strobe * strobe;
    // Fade current transient activation into a quiet material background.
    // FadeTime maps the slider to a .05..4 second exponential decay time.
    float2 age = FluidAge.Sample(FluidSampler, fluidUV);
    float seconds = max(0, age.x / max(1e-12, age.y));
    float fadeSeconds = .05 + 3.95 * Lighting.FadeTime;
    float envelope = exp(-seconds / fadeSeconds);
    float illumination = lerp(Lighting.Ambient, Comet.Shockwaves * 7 * pulse, envelope);
    // Ambient still requires decaying dye; empty space supplies no light.
    float3 light = material * illumination * (.75 + Comet.Background);
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
    [loop] for (int y = 0; y < samples; ++y)
        [loop] for (int x = 0; x < samples; ++x)
        {
            float2 offset = (float2(x, y) + .5) * sampleWidth - .5;
            color += RadiatingShade(input.UV + uvX * offset.x + uvY * offset.y, sampleWidth);
        }
    return float4(pow(saturate(color * sampleWidth * sampleWidth), 1 / 2.2), 1);
}
