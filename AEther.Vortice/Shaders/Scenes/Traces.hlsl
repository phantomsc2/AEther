#define GEOMETRY_VERTEX_COUNT 96
#define GEOMETRY_INSTANCE_COUNT 16384
#define GEOMETRY_ADDITIVE
#include "../Common/Inputs.hlsl"

struct DustSettings { float Population; float Spacing; float Radius; };
struct CameraSettings { float Speed; float Heading; float Sway; };
struct TrailSettings { float Duration; };
struct AppearanceSettings { float Gain; float Exposure; };
cbuffer Parameters : register(b1)
{
    DustSettings Dust = { .65, .5, .35 };
    CameraSettings Camera = { .35, .5, .3 };
    TrailSettings Trails = { .45 };
    AppearanceSettings Appearance = { .45, .4 };
};

static const float FocalLength = 1.7;
static const uint SegmentCount = 16;

// Integer hashing keeps positions stable even far along the flight path.
uint Hash(uint x)
{
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    return x ^ (x >> 16);
}
float Random(uint x) { return (Hash(x) >> 8) * (1.0 / 16777216.0); }
float3 Palette(float frequency)
{
    return .5 + .5 * cos(6.2831853 * (frequency * .85 + float3(.03, .36, .68)));
}
float Shutter() { return 2 * Trails.Duration * Trails.Duration; }
void View(float time, out float3 eye, out float3 right, out float3 up, out float3 forward)
{
    float heading = (Camera.Heading - .5) * 6.2831853;
    forward = float3(sin(heading), 0, cos(heading));
    right = float3(cos(heading), 0, -sin(heading));
    up = float3(0, 1, 0);
    // The dust never moves. Every historical view uses this same camera path.
    float travel = time * 32 * Camera.Speed;
    eye = forward * travel + Camera.Sway *
        (right * (8 * sin(travel * .025)) + up * (5 * sin(travel * .019)));
}
float3 ViewPoint(float3 world, float time)
{
    float3 eye, right, up, forward;
    View(time, eye, right, up, forward);
    float3 p = world - eye;
    return float3(dot(p, right), dot(p, up), dot(p, forward));
}

// Read one note column, interpolating only through time in the history ring.
float4 AudioAt(float frequency, float seconds, float pan)
{
    uint bands, rows;
    Histogram0.GetDimensions(bands, rows);
    int band = min((int)(saturate(frequency) * bands), (int)bands - 1);
    float4 now = lerp(Spectrum0.Load(int3(band, 0, 0)), Spectrum1.Load(int3(band, 0, 0)), pan);
    if (HistorySeconds <= 0 || HistorySpan <= 0 || rows == 0) return seconds < .001 ? now : 0;
    float offset = seconds / HistorySeconds * HistorySpan;
    if (offset < HistoryNewestAge)
        return now;
    if (offset > HistoryAvailable) return 0;
    float row = frac(HistogramShift - offset) * rows - .5;
    int low = (int)floor(row);
    int y0 = low < 0 ? (int)rows - 1 : low;
    int y1 = y0 + 1 >= (int)rows ? 0 : y0 + 1;
    float4 left = lerp(Histogram0.Load(int3(band, y0, 0)), Histogram0.Load(int3(band, y1, 0)), frac(row));
    float4 right = lerp(Histogram1.Load(int3(band, y0, 0)), Histogram1.Load(int3(band, y1, 0)), frac(row));
    return lerp(left, right, pan);
}

struct TraceVertex
{
    float4 Position : SV_POSITION;
    nointerpolation float3 Start : TEXCOORD0;
    nointerpolation float3 End : TEXCOORD1;
    nointerpolation float2 Ages : TEXCOORD2;
    nointerpolation float Radius : TEXCOORD3;
    nointerpolation float Frequency : TEXCOORD4;
    nointerpolation float Pan : TEXCOORD5;
    nointerpolation float Visibility : TEXCOORD6;
};

// Exact perspective bounds of a sphere; the caller keeps it in front of the eye.
float4 SphereBounds(float3 p, float radius)
{
    float denominator = max(1e-5, p.z * p.z - radius * radius);
    float2 extent = radius * sqrt(max(0, p.xy * p.xy + p.z * p.z - radius * radius));
    float2 lo = (p.xy * p.z - extent) / denominator;
    float2 hi = (p.xy * p.z + extent) / denominator;
    return float4(lo, hi) * float4(FocalLength / AspectRatio, FocalLength,
        FocalLength / AspectRatio, FocalLength);
}
TraceVertex GeometryVS(uint id : SV_VertexID, uint instance : SV_InstanceID)
{
    uint segment = id / 6, corner = id % 6;
    float2 uv = corner == 0 ? float2(0, 0) : corner == 1 ? float2(0, 1) :
        corner == 2 || corner == 3 ? float2(1, 0) : corner == 4 ? float2(0, 1) : float2(1, 1);
    float3 eye, right, up, forward;
    View(T, eye, right, up, forward);
    float period = 60 + 180 * Dust.Spacing;
    float3 base = float3(Random(instance * 3 + 1), Random(instance * 3 + 2), Random(instance * 3 + 3)) * period;
    // One periodic copy per ID. Wrapping occurs outside the visible sphere, so
    // each entire exposure follows one fixed world point across all segments.
    float3 relative = (frac((base - eye) / period + .5) - .5) * period;
    float3 world = eye + relative;
    float radius = .025 + .3 * Dust.Radius * Dust.Radius;
    float shutter = Shutter();
    float2 ages = shutter * float2(segment, segment + 1) / SegmentCount;
    float3 a = ViewPoint(world, T - ages.x);
    float3 b = ViewPoint(world, T - ages.y);
    float visibility = 1 - smoothstep(period * .32, period * .46, length(relative));
    bool visible = instance < (uint)(512 + 15872 * Dust.Population) && visibility > 0;
    visible = visible && (shutter > .0001 || segment == 0);

    // Clip the time interval before projecting, rather than connecting a trail
    // through infinity when the camera passes a sphere.
    float nearDepth = radius + .15;
    if (max(a.z, b.z) <= nearDepth) visible = false;
    if (visible && min(a.z, b.z) < nearDepth)
    {
        float t = saturate((nearDepth - a.z) / (b.z - a.z));
        float3 p = lerp(a, b, t);
        float age = lerp(ages.x, ages.y, t);
        if (a.z < nearDepth) { a = p; ages.x = age; }
        else { b = p; ages.y = age; }
    }
    float4 bounds = 0;
    if (visible)
    {
        float4 first = SphereBounds(a, radius), last = SphereBounds(b, radius);
        // Pad for analytic pixel coverage, including spheres smaller than a pixel.
        float2 padding = 3 / max(Resolution, float2(1, 1));
        bounds = float4(max(-1, min(first.xy, last.xy) - padding),
            min(1, max(first.zw, last.zw) + padding));
        visible = all(bounds.zw > bounds.xy);
    }
    TraceVertex o;
    o.Position = visible ? float4(lerp(bounds.xy, bounds.zw, uv), .5, 1) : float4(2, 2, 2, 1);
    o.Start = a; o.End = b; o.Ages = ages; o.Radius = radius;
    // Stable spatial identity supplies frequency, stereo and palette, without buffers.
    o.Frequency = Random(instance + 91271);
    o.Pan = Random(instance + 71329);
    o.Visibility = visibility;
    return o;
}

float4 PS(TraceVertex input) : SV_Target
{
    float2 ndc = input.Position.xy / max(Resolution, float2(1, 1)) * float2(2, -2) + float2(-1, 1);
    float3 ray = normalize(float3(ndc * float2(AspectRatio, 1) / FocalLength, 1));
    float3 velocity = input.End - input.Start;
    float3 p = input.Start - ray * dot(input.Start, ray);
    float3 v = velocity - ray * dot(velocity, ray);
    float pixelRadius = max(.00001, max(input.Start.z, input.End.z) / (FocalLength * max(1, Resolution.y)));
    float radius = max(input.Radius, .65 * pixelRadius);
    float aa = .65 * pixelRadius;
    float bound = radius + aa;

    // Solve the full time interval in which this pixel overlaps the swept disk.
    // Sampling only inside that interval prevents holes even when a tiny sphere
    // crosses hundreds of pixels during one exposure.
    float vv = dot(v, v), pv = dot(p, v);
    float lo = 0, hi = 1;
    if (vv > 1e-12)
    {
        float closest = -pv / vv;
        float3 perpendicular = p + closest * v;
        float discriminant = bound * bound - dot(perpendicular, perpendicular);
        clip(discriminant);
        float halfWidth = sqrt(max(0, discriminant) / vv);
        lo = max(0, closest - halfWidth);
        hi = min(1, closest + halfWidth);
    }
    else clip(bound * bound - dot(p, p));
    clip(hi - lo);
    float3 light = 0;
    float3 color = Palette(input.Frequency);
    float flux = min(1, input.Radius * input.Radius / (radius * radius));
    [unroll] for (uint sample = 0; sample < 8; ++sample)
    {
        float t = lerp(lo, hi, (sample + .5) / 8);
        float3 center = lerp(input.Start, input.End, t);
        float3 perpendicular = center - ray * dot(center, ray);
        float distance = length(perpendicular);
        float coverage = 1 - smoothstep(radius - aa, radius + aa, distance);
        // Ray/sphere surface normal, with analytic coverage at its silhouette.
        float front = sqrt(saturate(1 - distance * distance / (radius * radius)));
        float3 normal = -perpendicular / radius - ray * front;
        float shade = .25 + .75 * saturate(dot(normal, normalize(float3(-.4, .6, -1))));
        float age = lerp(input.Ages.x, input.Ages.y, t);
        float4 audio = max(0, AudioAt(input.Frequency, age, input.Pan));
        float energy = 1 - exp(-(audio.x + .45 * audio.y + .2 * audio.z) * (2 + 30 * Appearance.Gain));
        float nearFade = smoothstep(input.Radius + .15, input.Radius + 1, center.z);
        light += color * shade * coverage * nearFade * energy;
    }
    // A normalized shutter integral: changing the segment/sample count does not
    // change exposure, and a stationary camera produces one sphere, not 16 copies.
    float shutter = Shutter();
    float weight = shutter > .0001 ? (input.Ages.y - input.Ages.x) / shutter : 1;
    return float4(light * ((hi - lo) / 8) * weight * flux * input.Visibility, 0);
}

Texture2D<float4> SceneImage;
float4 AfterglowPS(PSin input) : SV_Target
{
    float3 light = max(0, SceneImage.SampleLevel(Linear, input.UV, 0).rgb);
    return float4(pow(saturate(1 - exp(-light * (1 + 5 * Appearance.Exposure))), 1 / 2.2), 1);
}
