#include "Postprocessing.hlsl"
cbuffer PresentationConstants : register(b3)
{
    float ScRgbOutput, ReferenceWhiteScale, ExposureGain, OutputHeadroom;
    float DitherOutput; float3 OutputPadding;
};
Texture2D<float4> SceneImage : register(t9);

uint OutputHash(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352d;
    value ^= value >> 15;
    value *= 0x846ca68b;
    return value ^ (value >> 16);
}
float4 PS(PSin input) : SV_Target
{
    int2 p = int2(input.Position.xy);
    float3 encoded = MapOutput(SceneImage.Load(int3(p, 0)), ExposureGain, OutputHeadroom,
        ScRgbOutput > .5, ReferenceWhiteScale);
    if (ScRgbOutput > .5 || DitherOutput < .5) return float4(encoded, 1);
    uint width, height; SceneImage.GetDimensions(width, height);
    uint index = 3 * (p.y * width + p.x);
    float3 noise = float3(OutputHash(index), OutputHash(index + 1), OutputHash(index + 2));
    noise = (floor(noise / 256.0) / 16777216.0 - .5) / 255.0;
    // Fixed spatial noise avoids flicker; half an 8-bit step leaves black black.
    return float4(saturate(encoded + noise), 1);
}
