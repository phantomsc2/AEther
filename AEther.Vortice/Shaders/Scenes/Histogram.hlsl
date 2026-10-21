#include "../Common/Inputs.hlsl"
#include "../Common/FractalTypes.hlsl"
cbuffer Parameters
{
    bool CombinedLevel = false;
};
float4 PS(PSin input) : SV_Target
{
    uint width, height;
    Histogram0.GetDimensions(width, height);
    float age = 2 * abs(input.UV.x - .5);
    // Wrapping is a storage detail; unavailable history must never reveal newer rows.
    if (age * HistorySpan > HistoryAvailable + 1.0 / height || age * HistorySpan < HistoryNewestAge)
        return float4(0, 0, 0, 1);
    int frequency = min((int)((1 - input.UV.y) * width), (int)width - 1);
    // A row covers its actual [start, end) analysis interval. Load that block
    // directly so interpolation cannot smear attacks into missing neighboring rows.
    int row = (int)floor((HistogramShift - age * HistorySpan) * height + .5);
    row = (row % (int)height + (int)height) % (int)height;
    float4 v = input.UV.x < .5 ? Histogram0.Load(int3(frequency, row, 0)) : Histogram1.Load(int3(frequency, row, 0));
    return float4(CombinedLevel ? v.aaa : v.rgb, 1);
}
