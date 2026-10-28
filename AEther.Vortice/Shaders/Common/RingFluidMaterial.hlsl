// Ring scene behavior. Material channel meanings, decay and inlet rules live
// entirely in HLSL; the host only reflects the two floating-point outputs.
#include "FluidSimulation.hlsl"
struct FluidMaterial { float4 Color : SV_Target0; float2 Age : SV_Target1; };
Texture2D<float4> FluidMaterial0;
Texture2D<float2> FluidMaterial1;
Texture2D<float4> FluidPredicted0;
Texture2D<float2> FluidPredicted1;
SamplerState FluidSampler;
float RingOutletRadius()
{
    return (.5 * Emitter.Radius + .1 * Emitter.Width) * min(1, AspectRatio);
}
float RingOutletCollar()
{
    // Cover the emitter's feather and the adjacent interpolation stencil only.
    return .01 * Emitter.EdgeSoftness + 2 * length(CellSize);
}
float2 PreventEmitterReentry(float2 p, float2 velocity)
{
    if (abs(length(p) - RingOutletRadius()) > RingOutletCollar()) return velocity;
    float2 normal = p / max(1e-6, length(p));
    // A one-way inlet removes only inward normal motion. Tangential flow and
    // existing outward momentum are preserved; no outward force is added here.
    return velocity - normal * min(0, dot(velocity, normal));
}
float2 FluidApplyBoundary(int2 cell, float2 v)
{
    float2 position = World((cell + .5) / GridSize);
    float2 faceX = position + float2(.5 * CellSize.x, 0);
    float2 faceY = position + float2(0, .5 * CellSize.y);
    float2 mask = float2(RingEmitter(faceX), RingEmitter(faceY));
    if (any(mask > 0))
    {
        float2 boundary = float2(RingBoundaryVelocity(faceX).x,
            RingBoundaryVelocity(faceY).y);
        v = lerp(v, boundary, mask);
    }
    if (abs(length(position) - RingOutletRadius()) <= RingOutletCollar() + length(CellSize))
    {
        // Reconstruct the cross component at each staggered face before testing
        // its radial direction. The correction remains local to the inlet edge.
        float yAtX = .25 * (ProjectedVelocity(cell).y
            + ProjectedVelocity(cell + int2(1, 0)).y
            + ProjectedVelocity(cell - int2(0, 1)).y
            + ProjectedVelocity(cell + int2(1, -1)).y);
        float xAtY = .25 * (ProjectedVelocity(cell).x
            + ProjectedVelocity(cell + int2(0, 1)).x
            + ProjectedVelocity(cell - int2(1, 0)).x
            + ProjectedVelocity(cell + int2(-1, 1)).x);
        v = float2(PreventEmitterReentry(faceX, float2(v.x, yAtX)).x,
            PreventEmitterReentry(faceY, float2(xAtY, v.y)).y);
    }
    return v;
}
FluidMaterial PredictMaterial(PSin input)
{
    float2 uv = Backtrace(input.UV);
    FluidMaterial output;
    output.Color = FluidMaterial0.SampleLevel(ClampFluid, uv, 0);
    output.Age = FluidMaterial1.SampleLevel(ClampFluid, uv, 0);
    return output;
}
FluidMaterial UpdateMaterial(PSin input)
{
    // Carry one frequency-tinted S material, plus independent age/frequency metadata.
    // No channel beyond the existing two-channel age texture is required.
    int2 cell = int2(input.Position.xy);
    float2 p = World(input.UV);
    float4 dye = max(0, FluidPredicted0.Load(int3(cell, 0)));
    float2 material = FluidPredicted1.Load(int3(cell, 0));
    float oldDensity = dye.a;
    float birthMass = material.x;
    float frequencyMass = material.y;
    // Fade the carried signal, leaving coordinate/density moments intact.
    // This preserves exponential decay when different particle ages mix.
    float3 signalMass = dye.rgb
        * RingSignalFade(StepSeconds + CatchupSeconds);

    float emitter = RingEmitter(p);
    float3 source;
    RingEmissionSample(p, SourceAge, source);
    // S alone generates material. B controls velocity; T is lighting only.
    float materialDrive = AmplitudeResponse(source.r
        * exp2(20 * Emitter.MaterialGain - 10), .035);
    float dose = StepSeconds * (200 * Emitter.Rate) * emitter * materialDrive;
    float capacity = max(1e-6, 100 * Emitter.Capacity);
    float addedDensity = max(0, capacity - oldDensity) * (1 - exp(-dose / capacity));
    float density = oldDensity + addedDensity;
    float sampledAge = max(SourceAge, HistoryNewestAge);
    float emittedAt = T - sampledAge * HistorySeconds / max(HistorySpan, 1e-6);
    float emittedFrequency = atan2(abs(p.x), p.y) / 3.14159265;
    // Account for the source row already being older than the current frame.
    float3 emittedMaterial = source.r * RingPalette(emittedFrequency)
        * RingSignalFade(sampledAge * HistorySeconds / max(HistorySpan, 1e-6));
    birthMass += emittedAt * addedDensity;
    frequencyMass += emittedFrequency * addedDensity;
    signalMass += emittedMaterial * addedDensity;

    // Refresh both representations together at an active inlet, at any capacity.
    float inlet = Emitter.Rate > 0 && materialDrive > 0 ? emitter : 0;
    // Preserve the 60 Hz inlet response when transport follows a faster display.
    inlet = 1 - pow(1 - saturate(inlet), 60 * StepSeconds);
    birthMass = lerp(birthMass, emittedAt * density, inlet);
    frequencyMass = lerp(frequencyMass, emittedFrequency * density, inlet);
    signalMass = lerp(signalMass, emittedMaterial * density, inlet);
    FluidMaterial output;
    output.Color = float4(signalMass, density);
    output.Age = float2(birthMass, frequencyMass);
    return output;
}
