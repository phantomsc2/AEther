#include "../Common/Inputs.hlsl"
#include "../Common/SpectrumView.hlsl"
#include "../Common/HistogramView.hlsl"

cbuffer Parameters
{
    bool CombinedLevel = false;
};

float4 PS(PSin input) : SV_Target
{
    // Each stereo half contains a history panel and a mirrored spectrum panel.
    float x = input.UV.x;
    if (x >= .4 && x < .6)
    {
        float u = x < .5 ? .5 - (x - .4) * 5 : .5 + (.6 - x) * 5;
        return DrawSpectrum(float2(u, input.UV.y),
                            true, true, true, true, CombinedLevel);
    }
    float u = x < .5 ? x * 1.25 : .5 + (x - .6) * 1.25;
    return DrawHistogram(float2(u, input.UV.y), .8 * Resolution.x, CombinedLevel);
}
