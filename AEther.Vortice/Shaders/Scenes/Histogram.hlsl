#include "../Common/Inputs.hlsl"
#include "../Common/FractalTypes.hlsl"
#include "../Common/GlobalDiagnostics.hlsl"
cbuffer Parameters
{
    bool CombinedLevel = false;
    bool ShowGlobalSTBR = false;
};
float4 PS(PSin input) : SV_Target
{
    uint width, height;
    Histogram0.GetDimensions(width, height);
    float age = 2 * abs(input.UV.x - .5);
    // Wrapping is a storage detail; unavailable history must never reveal newer rows.
    if (age * HistorySpan > HistoryAvailable || age * HistorySpan < HistoryNewestAge)
        return float4(0, 0, 0, 1);
    if (ShowGlobalSTBR && input.UV.y > .84)
        return float4(GlobalDiagnosticColor(GlobalSTBRAtAge(age * HistorySeconds),
            floor(saturate((input.UV.y - .84) / .16) * 3.999)), 1);
    float frequency = (min(floor((1 - input.UV.y) * width), width - 1) + .5) / width;
    float2 uv = float2(frequency, HistogramShift - age * HistorySpan);
    float4 v = input.UV.x < .5 ? Histogram0.Sample(Linear, uv) : Histogram1.Sample(Linear, uv);
    return float4(CombinedLevel ? v.aaa : v.rgb, 1);
}
