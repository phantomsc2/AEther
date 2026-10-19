#include "../Common/Inputs.hlsl"
// All controls are normalized to [0,1] for the shader parameter UI.
struct EmitterParameters
{
    float Radius;            // 0 .. .5 simulation units, scaled for aspect.
    float Width;             // 0 .. .1 simulation units (half-width).
    float EdgeSoftness;      // 0 .. .01 units, with pixel antialiasing minimum.
    float Rate;              // 0 .. 200 density units per second, scaled by local S.
    float MaterialGain;      // Local S material sensitivity; .5 = unity gain.
    float Capacity;          // 0 .. 100 density units.
    float HighFrequencyBoost; // 0 = flat; 1 = 16x at the highest frequency.
};
struct FluidParameters
{
    float EmissionVelocity;  // 0 .. 20 simulation units per second, scaled by local B.
    float VelocityGain;      // Local B sensitivity; .5 = unity gain.
    float AmbientVelocity;   // Additional constant outward speed, 0 .. 20 units per second.
    float Viscosity;         // 0 .. .1 via .1 * slider^3; no dye diffusion.
    float Curl;              // Local B-driven counter-rotating launch velocity.
    bool PeriodicBoundary;
    bool LowQuality;         // 128x128 simulation instead of the default 512x512.
};
struct DisplayParameters
{
    float Exposure;          // 1/4096x .. 4096x; .5 = unity.
};
struct LightingParameters
{
    float Ambient;           // Low baseline for old, strobe-lit material.
    float FlashStrength;     // 0 .. 1023x live spectral T multiplier; 0 disables flashes.
};
cbuffer Parameters
{
    EmitterParameters Emitter = { .056, 1, .1, .462, 1, .12, .25 };
    FluidParameters Fluid = { .619, .177, .081, .207, .15, false, false };
    DisplayParameters Display = { .75 };
    LightingParameters Lighting = { 0, .494 };
};
#define RING_FLUID
#include "../Common/RingEmitter.hlsl"
Texture2D<float4> FluidDye;
Texture2D<float2> FluidAge;
SamplerState FluidSampler;

// Preserved for future lookup experiments: exact particle age, carried
// frequency, and current screen-space stereo still address the raw histogram.
float3 RingMaterialHistogram(float2 uv)
{
    float4 dye = max(0, FluidDye.Sample(FluidSampler, uv));
    float2 material = FluidAge.Sample(FluidSampler, uv);
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
    float4 dye = max(0, FluidDye.Sample(FluidSampler, input.UV));
    float2 material = FluidAge.Sample(FluidSampler, input.UV);
    if (dye.a <= 0) return float4(0, 0, 0, 1);
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
    float seconds = max(0, T - material.x / dye.a);
    // Linear lighting handoff over the configured history duration.
    // Fresh material is fully steady; age >= HistorySeconds is fully strobe-lit.
    float strobeWeight = saturate(seconds / max(HistorySeconds, 1e-6));
    float strobe = Lighting.Ambient + transient;
    float illumination = lerp(1, strobe, strobeWeight);
    float gain = exp2(24 * Display.Exposure - 12);
    return float4(max(0, gain * pigment * illumination), 1);
}
