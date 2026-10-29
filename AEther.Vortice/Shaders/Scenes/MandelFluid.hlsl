#include "../Common/Inputs.hlsl"
#include "../Common/RingFluidParameters.hlsl"
// Fixed lighting calibration preserves the former default exposure (+6 stops).
static const float SceneLightGain = 64;
struct LightingParameters
{
    float Ambient;           // Steady illumination, additive with live flashes at every age.
    float FlashStrength;     // 0 .. 1023x live spectral T multiplier; 0 disables flashes.
};
struct ResourceSizes { uint2 FluidVelocity; };
cbuffer Parameters
{
    ResourceSizes Sizes = { uint2(512,512) };
    EmitterParameters Emitter = RING_EMITTER_DEFAULTS;
    FluidParameters Fluid = RING_FLUID_DEFAULTS;
    LightingParameters Lighting = { .1960, .3330 };
};
#include "../Common/RingEmitter.hlsl"
#include "../Common/RingFluidMaterial.hlsl"

// Preserved for future lookup experiments: exact particle age, carried
// frequency, and current screen-space stereo still address the raw histogram.
float3 RingMaterialHistogram(float2 uv)
{
    float4 dye = max(0, FluidMaterial0.Sample(FluidSampler, uv));
    float2 material = FluidMaterial1.Sample(FluidSampler, uv);
    if (dye.a <= 0) return 0;
    float seconds = max(0, T - material.x / dye.a);
    float frequency = saturate(material.y / dye.a);
    float2 p = (2 * uv - 1) * float2(AspectRatio, 1);
    float3 stb;
    RingHistorySample(frequency, seconds * HistorySpan / max(HistorySeconds, 1e-6), p, stb);
    return stb * RingSignalFade(seconds);
}

float4 PS(const PSin input) : SV_Target
{
    float4 dye = max(0, FluidMaterial0.Sample(FluidSampler, input.UV));
    float2 material = FluidMaterial1.Sample(FluidSampler, input.UV);
    if (dye.a <= 0) return LinearOutput(0);
    // One S material, tinted once at emission and faded during transport.
    // Mixing blends actual source colors instead of recoloring an average label.
    // B controls launch velocity; T only controls illumination below.
    float3 pigment = max(0, dye.rgb / dye.a);
    float2 p = (2 * input.UV - 1) * float2(AspectRatio, 1);
    float frequency = saturate(material.y / dye.a);
    // Live T follows the material's carried frequency near the source, then
    // blends toward the emitter's screen-angle frequency/stereo map outside.
    // Blend signal values rather than frequency labels to avoid inventing bands.
    float3 angularSource;
    bool live = RingEmissionSample(p, 0, angularSource);
    float3 fluidSource = 0;
    if (live) RingHistorySample(frequency, HistoryNewestAge, p, fluidSource);
    float radius = length(p);
    float2 normal = p / max(1e-6, radius);
    float screenEdge = 1 / max(1e-6, max(abs(normal.x) / AspectRatio, abs(normal.y)));
    float emitterEdge = (.5 * Emitter.Radius + .1 * Emitter.Width) * min(1, AspectRatio);
    float angularWeight = smoothstep(emitterEdge, max(emitterEdge + 1e-6, screenEdge), radius);
    float liveT = lerp(fluidSource.g, angularSource.g, angularWeight);
    float transient = max(0, liveT) * (exp2(10 * Lighting.FlashStrength) - 1);
    // Add lighting before exposure and presentation; material age only affects
    // pigment decay during transport, never the balance of ambient and flashes.
    float illumination = Lighting.Ambient + transient;
    return LinearOutput(SceneLightGain * pigment * illumination);
}
