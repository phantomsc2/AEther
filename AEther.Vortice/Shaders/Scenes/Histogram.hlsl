#include "../Common/Inputs.hlsl"

cbuffer Parameters
{
    bool CombinedLevel = false;
};
#include "../Common/HistogramView.hlsl"
float4 PS(PSin input) : SV_Target
{
    return DrawHistogram(input.UV, Resolution.x, CombinedLevel);
}
