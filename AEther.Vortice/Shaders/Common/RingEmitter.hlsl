// Circular emitter; local S supplies material and local B supplies launch speed.
float RingEmitter(float2 p)
{
    float distance = abs(length(p) - .5 * Emitter.Radius * min(1, AspectRatio));
    float edge = max(1e-6, max(.01 * Emitter.EdgeSoftness, fwidth(length(p))));
    float width = .1 * Emitter.Width * min(1, AspectRatio);
    return 1 - smoothstep(width, width + edge, distance);
}
bool RingHistorySample(float frequency, float age, float2 p, out float3 stb)
{
    stb = 0;
    // Ring age is passed directly, avoiding seconds-to-ring rounding at row bounds.
    if (HistoryAvailable <= 0 || age < HistoryNewestAge || age > HistoryAvailable) return false;
    float side = p.x / max(1e-6, length(p));
    float right = .5 + .5 * side * abs(side);
    float2 uv = float2(saturate(frequency), HistogramShift - age);
    stb = max(0, lerp(Histogram0.SampleLevel(Linear, uv, 0).rgb,
        Histogram1.SampleLevel(Linear, uv, 0).rgb, right));
    return true;
}
bool RingEmissionSample(float2 p, float sourceAge, out float3 stb)
{
    stb = 0;
    uint bands, rows;
    Histogram0.GetDimensions(bands, rows);
    if (HistoryNewestAge > sourceAge + 2.0 / rows) return false;
    float frequency = atan2(abs(p.x), p.y) / 3.14159265;
    return RingHistorySample(frequency, max(sourceAge, HistoryNewestAge), p, stb);
}
float2 RingBoundaryVelocity(float2 p)
{
    float3 stb;
    RingEmissionSample(p, 0, stb);
    float background = AmplitudeResponse(stb.b * exp2(20 * Fluid.VelocityGain - 10), .01);
    float speed = 20 * Fluid.EmissionVelocity * background;
    float2 normal = p / max(1e-6, length(p));
    float2 tangent = float2(-normal.y, normal.x);
    // Mirror circulation across the centerline: positive on the left, negative
    // on the right. Screen Y increases downward, so both tangents curl upward.
    // Mirror the strength variation too, and taper the flip smoothly at x = 0.
    float angle = atan2(p.y, abs(p.x));
    float variation = 1 + .25 * sin(6 * angle + .7 * T) + .125 * sin(11 * angle - .9 * T);
    float spin = -normal.x * variation;
    float curlDrive = AmplitudeResponse(stb.b, .01);
    float swirl = 20 * Fluid.Curl * curlDrive * spin;
    return speed * normal + swirl * tangent;
}
float RingSignalFade(float seconds)
{
    // History duration is the shared time scale for material decay and lighting.
    // At one history window, the lighting handoff is complete and pigment is 1/e.
    return exp(-max(0, seconds) / max(HistorySeconds, 1e-6));
}
float3 RingPalette(float frequency)
{
    // Same palette as V3. Apply before transport so mixing blends real pigments.
    return .5 + .5 * cos(6.2831853 * (frequency * .85 + float3(.04, .38, .62)));
}
