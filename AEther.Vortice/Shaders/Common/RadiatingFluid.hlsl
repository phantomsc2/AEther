// V3's solver is centered at the origin and projected around the animated
// silhouette. All positions use viewport-height units.
float RadiatingFrequency(float3 pigment)
{
    // Invert the fixed cosine palette. Least squares recovers its circular
    // phase from all three channels, so mixtures follow their resulting hue.
    float3 c = cos(6.2831853 * float3(.04, .38, .62));
    float3 s = sin(6.2831853 * float3(.04, .38, .62));
    float3 y = 2 * pigment - 1;
    float cc = dot(c, c), ss = dot(s, s), cs = dot(c, s);
    float u = dot(y, c) * ss - dot(y, s) * cs;
    float v = dot(y, c) * cs - dot(y, s) * cc;
    if (u * u + v * v < 1e-8) return .5;
    float phase = frac(atan2(v, u) / 6.2831853 + 1);
    // The palette spans 85% of the hue circle. Clamp mixtures falling in the
    // unused gap to the nearer spectral endpoint instead of wrapping bass.
    return phase > .85 ? (phase < .925 ? 1 : 0) : phase / .85;
}
float RadiatingStereo(float2 p)
{
    float side = p.x / max(.001, length(p));
    return .5 + .5 * side * abs(side);
}
float RadiatingActivity() { return smoothstep(.005, .5, PoseX + .6 * PoseZ); }
float4 RadiatingRead(float frequency, float age, float right)
{
    float2 uv = float2(saturate(frequency), HistogramShift - age);
    return max(0, lerp(Histogram0.SampleLevel(Linear, uv, 0),
        Histogram1.SampleLevel(Linear, uv, 0), right));
}
float4 RadiatingSource(float2 p, float sourceAge)
{
    float age = max(sourceAge, HistoryNewestAge);
    uint bands, rows; Histogram0.GetDimensions(bands, rows);
    // No endless injection from the last row after capture stops.
    if (HistoryNewestAge > sourceAge + 2.0 / rows || age > HistoryAvailable || HistoryAvailable <= 0) return 0;
    float frequency = atan2(abs(p.x), p.y) / 3.14159265;
    float right = RadiatingStereo(p);
    float4 source = RadiatingRead(frequency, age, right);
    // Separated tonal bands add shear to each other's streams. Exclude the
    // local band so a lone note cannot count itself again. The probe centers
    // stay inside the spectrum. Alpha carries this chord drive.
    float other = 0;
    [unroll] for (int i = 0; i < 16; ++i)
    {
        float f = (i + .5) / 16;
        if (abs(f - frequency) > .1)
            other = max(other, RadiatingRead(f, age, right).r);
    }
    source.a = min(1 - exp(-3 * source.r), 1 - exp(-3 * other));
    return source;
}
float2 RadiatingCarrier(float2 p)
{
    return RadiatingActivity() * Fluid.Flow * 2.4 * p;
}
float RadiatingDilution(float2 p) { return RadiatingActivity() * Fluid.Flow * 4.8; }
float RadiatingBackground(float4 source)
{
    // One local B response drives both stirring and pigment injection. The
    // Background control sets its sensitivity; silence supplies exactly zero.
    return Fluid.Background * 3 * (1 - exp(-3 * max(0, source.b)));
}
float2 RadiatingTurbulence(float2 p, float4 source)
{
    float r = length(p), t = CometTime();
    float tone = 1 - exp(-3 * source.r);
    float background = RadiatingBackground(source);
    float strength = ((.8 * tone + 4 * source.a) * (1 + background) + 3 * background)
        * (.12 + 4 * Fluid.Curl * Fluid.Curl);
    float2 force = 0;
    // Localized counter-rotating vortices roll whole streams into spirals.
    // Curl changes their forcing as well as the small-scale confinement;
    // diffusion remains a separate pigment mixing control.
    [unroll] for (int i = 0; i < 8; ++i)
    {
        float spin = i % 2 == 0 ? 1 : -1;
        float angle = i * 2.399963 + spin * .13 * t;
        float orbit = .22 + .095 * i;
        float2 center = orbit * float2(cos(angle), sin(angle));
        float size = .13 + .025 * (i % 3);
        float2 d = (p - center) / size;
        force += spin * float2(-d.y, d.x) * exp(-dot(d, d)) * 2.5;
    }
    force += .18 * float2(-p.y, p.x) * exp(-2 * r * r);
    return force * strength * RadiatingActivity();
}
float4 RadiatingPigment(float2 p, float4 source)
{
    // Same inverse display projection and distance field as the black head.
    // Evaluate only on the simulation grid, not in every pressure pass.
    float2 screen = p / .55 + CometHead();
    float surface = CometDistances(screen).x - CometBoundary();
    float band = max(.0015, 1.25 * fwidth(surface));
    float angle = atan2(p.y, p.x);
    float streams = .25 + .75 * pow(.5 + .5 * sin(7 * angle - 1.2 * CometTime()), 2);
    float emitter = exp(-abs(surface) / band) * streams;
    float tone = 1 - exp(-3 * source.r);
    float background = RadiatingBackground(source);
    float density = 2.2 * (tone * (1 + background) + background) * emitter;
    float frequency = atan2(abs(p.x), p.y) / 3.14159265;
    // Color is a frequency identity, so it must not rotate with the clock.
    return float4(CometPalette(frequency, 0) * density, density);
}
