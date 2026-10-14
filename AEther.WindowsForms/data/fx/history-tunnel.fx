#include "states.fxi"
#include "globals.fxi"

Texture2D<float4> Histogram0 : register(t0);
Texture2D<float4> Histogram1 : register(t1);
SamplerState TunnelHistory
{
    Filter = MIN_MAG_LINEAR_MIP_POINT;
    AddressU = CLAMP;
    AddressV = WRAP;
};

float4 PS(const PSDefaultin IN) : SV_Target
{
    float2 screen = 2 * IN.UV - 1;
    screen.x *= AspectRatio;
    screen.y = -screen.y;
    // The vanishing point follows the slow pose; attacks never teleport it.
    screen -= .08 * float2(sin(PoseY), sin(PoseX));
    screen = mul(Rot2(PoseZ), screen);
    float radius = length(screen);
    float angle = atan2(screen.y, screen.x);
    // Each stereo channel covers half the circumference, low to high to low.
    float frequency = abs(angle) / 3.14159265;
    uint width, height;
    Histogram0.GetDimensions(width, height);
    float bin = min(floor(frequency * width), width - 1);
    float u = (bin + .5) / width;

    // New events appear at the horizon. Recorded history grows in radius,
    // r(age) = .07 / (1 - age): a straight perspective cylinder in time.
    float age = 1 - .07 / max(.0001, radius);
    if (age < 0 || age >= HistorySpan) return float4(.002, .003, .008, 1);
    float2 uv = float2(u, HistogramShift - age);
    float4 signal = angle < 0 ? Histogram0.Sample(TunnelHistory, uv)
                             : Histogram1.Sample(TunnelHistory, uv);
    // Integrate the projected footprint when distant time rows are subpixel.
    // Frequency stays discrete; time sampling remains continuous.
    float footprint = min(.08, fwidth(age));
    [unroll] for (int tap = 1; tap <= 3; ++tap)
    {
        float sampleAge = clamp(age + (tap / 3.0 - .5) * footprint, 0, HistorySpan - .00001);
        float2 sampleUV = float2(u, HistogramShift - sampleAge);
        signal += angle < 0 ? Histogram0.Sample(TunnelHistory, sampleUV)
                           : Histogram1.Sample(TunnelHistory, sampleUV);
    }
    signal *= .25;
    float across = abs(frac(frequency * width) - .5);
    float antialias = max(.015, fwidth(frequency * width));
    float core = 1 - smoothstep(.10, .10 + antialias, across);
    float halo = exp(-16 * across * across);
    float3 palette = .5 + .5 * cos(6.2831853 * (frequency * .75 + .025 * S + float3(.02, .36, .65)));
    float rod = max(0, signal.r) * (3 * core + .8 * halo);
    float pulse = 2.7 * max(0, signal.g);
    float3 light = palette * rod + float3(.55, .8, 1) * pulse;
    light += palette * .025 * max(0, signal.b) * halo;
    float depthFade = smoothstep(.07, .18, radius);
    light *= depthFade * (1 + .8 * VisualFlash + .3 * VisualGlow);
    // Fade the last valid rows rather than exposing the history ring seam.
    light *= saturate((HistorySpan - age) * height / 3);
    float3 color = 1 - exp(-light);
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
