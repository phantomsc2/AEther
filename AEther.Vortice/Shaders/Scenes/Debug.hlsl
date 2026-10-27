#include "../Common/Inputs.hlsl"

cbuffer Parameters
{
    bool CombinedLevel = false;
};

float4 PS(PSin input) : SV_Target
{
    uint bands, rows;
    Histogram0.GetDimensions(bands, rows);
    int bin = min((int)((1 - input.UV.y) * bands), (int)bands - 1);
    if (input.UV.x >= .8)
    {
        // The channels grow away from the seam between their strips.
        bool first = input.UV.x < .9;
        float4 v = first ? Spectrum0.Load(int3(bin, 0, 0)) : Spectrum1.Load(int3(bin, 0, 0));
        float x = first ? (.9 - input.UV.x) / .1 : (input.UV.x - .9) / .1;
        float pixel = 1 / (.1 * Resolution.x);
        float3 bars = saturate((saturate(v.rgb) - x) / pixel + .5) * step(1e-20, v.rgb);
        float total = v.a > 0 ? saturate((saturate(v.a) - x) / pixel + .5) : 0;
        // Total amplitude fills behind the components, softly tinting their overlap.
        float3 background = .25 * total.xxx;
        return float4(CombinedLevel ? background : max(bars, background), 1);
    }

    // Integrate actual row coverage, including fractional pixels at event edges.
    // The full history runs from oldest at the left edge to newest at the seam.
    float age = (.8 - input.UV.x) / .8;
    float halfPixel = HistorySpan / (1.6 * Resolution.x);
    float lo = max(0, age * HistorySpan - halfPixel);
    float hi = age * HistorySpan + halfPixel;
    float footprint = max(1e-8, (hi - lo) * rows);
    lo = max(lo, HistoryNewestAge);
    hi = min(hi, HistoryAvailable + 1.0 / rows);
    if (hi <= lo || HistoryAvailable <= 0) return float4(0, 0, 0, 1);
    float first = (HistogramShift - hi) * rows + .5;
    float end = (HistogramShift - lo) * rows + .5;
    float4 history = 0;
    [loop] for (int row = (int)floor(first); row < (int)ceil(end); row++)
    {
        int slot = (row % (int)rows + (int)rows) % (int)rows;
        float coverage = max(0, min(end, row + 1.0) - max(first, (float)row));
        float4 block = .5 * (Histogram0.Load(int3(bin, slot, 0)) + Histogram1.Load(int3(bin, slot, 0)));
        history += block * (coverage / footprint);
    }
    return float4(CombinedLevel ? history.aaa : history.rgb, 1);
}
