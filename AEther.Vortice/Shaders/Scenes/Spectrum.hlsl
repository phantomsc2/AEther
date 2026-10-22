#include "../Common/Inputs.hlsl"

cbuffer Parameters
{
    bool ShowS = true;
    bool ShowT = true;
    bool ShowB = true;
    bool ShowR = true;
    bool ShowSum = false;
};
float4 PS(PSin input) : SV_Target
{
    uint width, height;
    Spectrum0.GetDimensions(width, height);
    int bin = min((int)((1 - input.UV.y) * width), (int)width - 1);
    float4 v = input.UV.x < .5 ? Spectrum0.Load(int3(bin, 0, 0)) : Spectrum1.Load(int3(bin, 0, 0));
    float x = 2 * abs(input.UV.x - .5);
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
