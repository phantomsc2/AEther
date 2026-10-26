#include "../Common/Inputs.hlsl"

cbuffer Parameters
{
    bool ShowS = true;
    bool ShowT = true;
    bool ShowB = true;
    bool ShowR = true;
    bool ShowSum = false;
    bool CompareSmoothers = false;
};
float4 PS(PSin input) : SV_Target
{
    uint width, height;
    Spectrum0.GetDimensions(width, height);
    int bin = min((int)((1 - input.UV.y) * width), (int)width - 1);
    float4 v = input.UV.x < .5 ? Spectrum0.Load(int3(bin, 0, 0)) : Spectrum1.Load(int3(bin, 0, 0));
    float x = 2 * abs(input.UV.x - .5);
    if (CompareSmoothers)
    {
        // Same input on both sides: stereo-averaged linear amplitudes, same row.
        // Left/red=A (log model), right/cyan=B (complex model); raw=white marker.
        float4 comparison = .5 * (Spectrum0.Load(int3(bin, 1, 0)) + Spectrum1.Load(int3(bin, 1, 0)));
        float observed = comparison.g;
        float amplitude = input.UV.x < .5 ? comparison.b : comparison.a;
        float marker = observed > 0 && abs(x - observed) < 1.5 / Resolution.x ? 1 : 0;
        float3 color = input.UV.x < .5 ? float3(1, 0, 0) : float3(0, 1, 1);
        return float4(max(color * (x < amplitude ? 1 : 0), marker.xxx), 1);
    }
    bool raw = !(ShowS || ShowT || ShowB || ShowR);
    // Magnitude of the signed residual, computed before clamping the fitted effects.
    float r = abs(input.UV.x < .5 ? Spectrum0.Load(int3(bin, 1, 0)).r : Spectrum1.Load(int3(bin, 1, 0)).r);
    if (raw || ShowSum)
    {
        // Residual brightness is only a display tint; sum its full amplitude.
        float sum = raw ? v.a : dot(v.rgb, float3(ShowS, ShowT, ShowB)) + (ShowR ? r : 0);
        float bar = x < sum ? 1 : 0;
        return float4(bar.xxx, 1);
    }
    v.rgb = (x < v.rgb) * float3(ShowS, ShowT, ShowB);
    float residualBar = ShowR && x < r ? .25 : 0;
    return float4(max(saturate(v.rgb), residualBar.xxx), 1);
}
