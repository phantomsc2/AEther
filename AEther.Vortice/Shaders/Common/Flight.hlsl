#ifndef AETHER_FLIGHT
#define AETHER_FLIGHT
#include "Inputs.hlsl"
#include "Projection.hlsl"

float2 FlightRotate(float2 p, float angle)
{
    float s = sin(angle), c = cos(angle);
    return float2(c * p.x - s * p.y, s * p.x + c * p.y);
}

ProjectionCamera FlightCamera(float3 origin, float2 steering)
{
    float yaw=steering.x-.5,pitch=steering.y-.5;
    float3 forward=float3(sin(yaw)*cos(pitch),sin(pitch),cos(yaw)*cos(pitch));
    float3 right=float3(cos(yaw),0,-sin(yaw)),up=cross(forward,right);
    return MakeProjectionCamera(origin,forward,right,up);
}
// World-Z cohorts are geometric planes shared by all forward-flight sketches.
float FlightSliceDistance(ProjectionCamera camera, float3 direction, float worldZ)
{
    return ProjectionPlane(camera,direction,float3(0,0,1),worldZ);
}

// Age is a fraction of the retained history, with new material at the far plane.
// The sketches traverse that history in one second (the application's default span).
float4 FlightAudio(float frequency, float age, float right)
{
    if (age < 0 || age >= 1) return 0;
    frequency = saturate(frequency);
    float4 current = lerp(Spectrum0.SampleLevel(Linear, float2(frequency, .5), 0),
        Spectrum1.SampleLevel(Linear, float2(frequency, .5), 0), right);
    float offset = age * HistorySpan;
    float4 recorded = 0;
    if (age >= 0 && age < 1 && offset <= HistoryAvailable && offset >= HistoryNewestAge)
    {
        float2 uv = float2(frequency, HistogramShift - offset);
#ifdef FLIGHT_SMOOTH_HISTORY
        recorded = lerp(FilteredHistogram0.SampleLevel(Linear, uv, 0),
            FilteredHistogram1.SampleLevel(Linear, uv, 0), right);
#else
        recorded = lerp(Histogram0.SampleLevel(Linear, uv, 0),
            Histogram1.SampleLevel(Linear, uv, 0), right);
#endif
    }
    return max(0, lerp(recorded, current, 1 - smoothstep(0, .035, age)));
}

float3 FlightPalette(float frequency, float birth)
{
    return .5 + .5 * cos(6.2831853 * (frequency * .8 + .025 * birth
        + float3(.03, .34, .65)));
}

float FlightLine(float distance, float width)
{
    float aa = max(fwidth(distance), .0001);
    return 1 - smoothstep(width, width + aa, abs(distance));
}

float FlightLevel(float4 audio, float gain)
{
    return 1 - exp(-(2 + 18 * gain) * (audio.r + .8 * audio.g + .45 * audio.b));
}

float4 FlightOutput(float3 light, float2 screen, float glow)
{
    light *= exp(-.12 * dot(screen, screen));
    return float4(pow(saturate(1 - exp(-max(0, light) * (1 + 3 * glow))), 1 / 2.2), 1);
}
#endif
