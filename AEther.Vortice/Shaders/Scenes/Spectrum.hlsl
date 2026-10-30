#include "../Common/Inputs.hlsl"

cbuffer Parameters
{
    bool ShowS = true;
    bool ShowT = true;
    bool ShowB = true;
    bool ShowR = true;
    bool ShowSum = false;
};
#include "../Common/SpectrumView.hlsl"
float4 PS(PSin input) : SV_Target
{
    return DrawSpectrum(input.UV, ShowS, ShowT, ShowB, ShowR, ShowSum);
}
