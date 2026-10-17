#include "../Common/Inputs.hlsl"
#include "../Common/GlobalDiagnostics.hlsl"
cbuffer Parameters { bool CombinedLevel = false; bool Intensity = false; bool ShowGlobalSTBR = false; };
float4 PS(PSin input) : SV_Target
{
    if (ShowGlobalSTBR && input.UV.y > .92)
        return float4(GlobalDiagnosticColor(GlobalSTBR, floor(saturate(input.UV.x) * 3.999)), 1);
    uint width, height;
    Spectrum0.GetDimensions(width, height);
    int bin = min((int)((1 - input.UV.y) * width), (int)width - 1);
    float4 v = input.UV.x < .5 ? Spectrum0.Load(int3(bin, 0, 0)) : Spectrum1.Load(int3(bin, 0, 0));
    if (CombinedLevel) v.rgb = v.aaa;
    if (!Intensity) v.rgb = 2 * abs(input.UV.x - .5) < v.rgb;
    return float4(saturate(v.rgb), 1);
}
