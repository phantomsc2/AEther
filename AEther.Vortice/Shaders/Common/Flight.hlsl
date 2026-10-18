#ifndef AETHER_FLIGHT
#define AETHER_FLIGHT
#include "Inputs.hlsl"

float2 FlightRotate(float2 p, float angle)
{
    float s = sin(angle), c = cos(angle);
    return float2(c * p.x - s * p.y, s * p.x + c * p.y);
}

float2 FlightScreen(float2 uv, float2 steering)
{
    return float2((2 * uv.x - 1) * AspectRatio, 1 - 2 * uv.y)
        - (steering * 2 - 1) * float2(.8, .5);
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

float FlightLevel(float4 audio, float gain, float age)
{
    float local = 1 - exp(-(2 + 18 * gain) * (audio.r + .8 * audio.g + .45 * audio.b));
    float4 global = MusicAtAge(age);
    return local + (1 - local) * .45 * MusicBody(global);
}

float4 FlightOutput(float3 light, float2 screen, float glow)
{
    light *= exp(-.12 * dot(screen, screen));
    return float4(pow(saturate(1 - exp(-max(0, light) * (1 + 3 * glow))), 1 / 2.2), 1);
}
#endif
