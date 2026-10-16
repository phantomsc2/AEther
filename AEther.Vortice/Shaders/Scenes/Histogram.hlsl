#include "../Common/Inputs.hlsl"
#include "../Common/FractalTypes.hlsl"
cbuffer Parameters
{
    bool CombinedLevel = false;
    bool ResidualView = false;
};
float4 PS(PSin input) : SV_Target
{
    uint width, height;
    Histogram0.GetDimensions(width, height);
    float age = 2 * abs(input.UV.x - .5);
    // Wrapping is a storage detail; unavailable history must never reveal newer rows.
    if (age * HistorySpan > HistoryAvailable || age * HistorySpan < HistoryNewestAge)
        return float4(0, 0, 0, 1);
    float frequency = (min(floor((1 - input.UV.y) * width), width - 1) + .5) / width;
    float2 uv = float2(frequency, HistogramShift - age * HistorySpan);
    float4 v = input.UV.x < .5 ? Histogram0.Sample(Linear, uv) : Histogram1.Sample(Linear, uv);
    if (ResidualView)
    {
        float residual = input.UV.x < .5 ? ResidualHistogram0.Sample(Linear, uv).r : ResidualHistogram1.Sample(Linear, uv).r;
        // Positive removed detail is red; negative introduced amplitude is blue.
        return float4(max(residual, 0), 0, max(-residual, 0), 1);
    }
    return float4(CombinedLevel ? v.aaa : v.rgb, 1);
}
