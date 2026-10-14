#include "states.fxi"
#include "globals.fxi"

Texture2D<float4> Spectrum0 : register(t0);
Texture2D<float4> Spectrum1 : register(t1);
Texture2D<float4> Histogram0 : register(t2);
Texture2D<float4> Histogram1 : register(t3);
SamplerState HullHistory
{
    Filter = MIN_MAG_LINEAR_MIP_POINT;
    AddressU = CLAMP;
    AddressV = WRAP;
};
float SliceDepth = 0;
float HistoryWarp = .7;
float TimeCompression = 4;

// A finite-iteration 3D Mandelbox distance estimate, evaluated on a plane.
// Audio changes illumination; the box/sphere folds and scale remain fixed.
float2 BoxDistance(float3 p)
{
    float3 z = p;
    float derivative = 1;
    float coarse = 0;
    [unroll] for (int i = 0; i < 9; ++i)
    {
        z = 2 * clamp(z, -1, 1) - z;
        float fold = 1 / clamp(dot(z, z), .25, 1);
        z *= fold;
        derivative *= fold;
        z = -1.5 * z + p;
        derivative = 1.5 * derivative + 1;
        if (i == 3) coarse = length(z) / derivative;
    }
    return float2(length(z) / derivative, coarse);
}

float4 History(float frequency, float age)
{
    if (age < 0 || age >= HistorySpan) return 0;
    float2 uv = float2(saturate(frequency), HistogramShift - age);
    return .5 * (Histogram0.Sample(HullHistory, uv) + Histogram1.Sample(HullHistory, uv));
}

float4 PS(const PSDefaultin IN) : SV_Target
{
    float2 screen = 2 * IN.UV - 1;
    screen.x *= AspectRatio;
    screen.y = -screen.y;
    // Musical time traverses another dimension of the fixed 3D field. No
    // wall-time drift in silence, and no transient-driven fractal parameter jumps.
    float3 p = float3(screen * (2.05 + .035 * VisualBreath), SliceDepth + .24 * sin(5 * S));
    p = mul(Rz(PoseZ), p);
    p = mul(Rx(PoseX), p);
    p = mul(Ry(PoseY), p);
    float radius = length(screen);
    float outer = smoothstep(.5, 2.4, radius);
    float2 distances = max(1e-8, BoxDistance(p));
    // Equal ratios of distance become nested hulls. A steady tone selects a
    // fixed shell in this field, rather than displacing the fractal surface.
    float deepFrequency = (log2(distances.x) + 17) / 17;
    float coarseFrequency = (log2(distances.y) + 10) / 10;
    float frequency = saturate(lerp(deepFrequency, coarseFrequency, outer));
    // Project time as r(t) = horizon * (1 - exp(-k*t)). A new row therefore
    // covers a large central area; its outward speed falls exponentially.
    // Warp the projected radius with the fold field so equal-age fronts hug
    // the fractal while retaining a center-to-edge order.
    float foldCoordinate = smoothstep(-7, -.3, log2(distances.x / distances.y));
    // In screen coordinates the horizon is the nearest viewport boundary.
    // Equal-age fronts inherit a box-like outer shape and the full span
    // of recorded history remain visible instead of being clipped offscreen.
    float viewRadius = max(abs(screen.x) / AspectRatio, abs(screen.y));
    float warpedRadius = viewRadius * (1 + saturate(HistoryWarp)
        * lerp(.48, .28, outer) * (2 * foldCoordinate - 1));
    float age = -log(max(.001, 1 - warpedRadius / 1.02)) / TimeCompression;
    // Time and frequency occupy separate dimensions of the history texture.
    // Angle places a note on a ray; the distance field places an attack on a hull.
    float rayFrequency = frac((atan2(screen.y, screen.x) + 3.14159265) / 6.2831853);
    float footprint = max(fwidth(frequency), .028 * outer);
    float timeFootprint = min(.025, fwidth(age));
    float4 hull = History(frequency, age);
    float4 ray = History(rayFrequency, age);
    [unroll] for (int tap = -1; tap <= 1; ++tap)
    {
        float offset = tap / 2.0;
        hull += History(frequency + offset * footprint, age + offset * timeFootprint);
        ray += History(rayFrequency, age + offset * timeFootprint);
    }
    hull *= .25;
    ray *= .25;
    float4 currentHull = .5 * (Spectrum0.Sample(Linear, float2(frequency, .5))
                         + Spectrum1.Sample(Linear, float2(frequency, .5)));
    float4 currentRay = .5 * (Spectrum0.Sample(Linear, float2(rayFrequency, .5))
                        + Spectrum1.Sample(Linear, float2(rayFrequency, .5)));
    float center = exp(-8 * radius);
    hull = lerp(hull, currentHull, center);
    ray = lerp(ray, currentRay, center);
    float dust = smoothstep(.28, .85, age);
    float trail = exp(-2.3 * age);
    // The rays inherit nested box detail rather than looking like flat cones.
    // Suppress the subpixel part of this modulation to keep the image stable.
    float foldPhase = 14 * log2(distances.x);
    float detail = .5 + .5 * cos(foldPhase) * saturate(.8 / max(.001, fwidth(foldPhase)));
    float3 rayPalette = .5 + .5 * cos(6.2831853 * (rayFrequency * .75 + float3(.04, .38, .62)));
    float3 hullPalette = .5 + .5 * cos(6.2831853 * (frequency * .8 + float3(.04, .38, .62)));
    // A note holds one angular route while its age advances; changing chords
    // selects new rays rather than moving existing ones sideways.
    float sustain = 3.6 * max(0, ray.r) * trail * (.28 + 1.45 * detail);
    // Attacks are crisp near the core, then widen and disperse into a faint
    // coarse glow near the old-history horizon.
    float attack = 3.1 * max(0, hull.g) * trail;
    float4 dispersed = .5 * (History(frequency - .07 * dust, age)
                            + History(frequency + .07 * dust, age));
    float broad = 2.0 * max(0, dispersed.g) * dust * trail;
    float3 light = rayPalette * sustain
        + hullPalette * attack * (1 - .7 * dust)
        + float3(.36, .53, .67) * broad;
    light += hullPalette * .05 * max(0, hull.b) * trail;
    light *= 1 + 2.8 * VisualFlash + .45 * VisualGlow;
    // The undelayed onset ignites the core immediately; the recorded event
    // subsequently propagates through the history field without repeated triggers.
    float coreFlash = VisualFlash * exp(-1.8 * radius);
    light += float3(.6, .78, 1) * coreFlash * (1 + 2 * max(0, hull.b));
    float vignette = 1 - .38 * smoothstep(.25, 2.5, length(screen));
    float3 color = 1 - exp(-light * vignette);
    return float4(pow(saturate(color), 1 / 2.2), 1);
}

technique11 t0
{
    pass p0
    {
        SetRasterizerState(RasterizerDefault);
        SetDepthStencilState(DepthStencilNone, 0);
        SetBlendState(BlendNone, float4(0, 0, 0, 0), 0xFFFFFFFF);
        SetVertexShader(CompileShader(vs_4_0, VSDefault()));
        SetGeometryShader(0);
        SetPixelShader(CompileShader(ps_4_0, PS()));
    }
}
