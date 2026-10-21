#include "../Common/Inputs.hlsl"
Texture2D<float> ResidualSpectrum;
cbuffer Parameters { bool CombinedLevel = false; bool Intensity = false; };
float4 PS(PSin input) : SV_Target
{
    uint width, height;
    Spectrum0.GetDimensions(width, height);
    int bin = min((int)((1 - input.UV.y) * width), (int)width - 1);
    float4 v = input.UV.x < .5 ? Spectrum0.Load(int3(bin, 0, 0)) : Spectrum1.Load(int3(bin, 0, 0));
    if (CombinedLevel) v.rgb = v.aaa;
    if (!Intensity) v.rgb = 2 * abs(input.UV.x - .5) < v.rgb;
    float r = ResidualSpectrum.Load(int3(bin, input.UV.x < .5 ? 0 : 1, 0));
    float distancePixels = abs(2 * abs(input.UV.x - .5) - r) * Resolution.x * .5;
    float outline = r > 0 ? 1 - smoothstep(.25, 1.25, distancePixels) : 0;
    return float4(lerp(saturate(v.rgb), float3(.8, .8, .8), outline), 1);
}
