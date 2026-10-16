// Scene shaders retain their established display-encoded colors. Decode only
// at presentation, so FP16 scRGB is linear and the existing palette stays intact.
cbuffer PresentationConstants : register(b3)
{
    float ScRgbOutput, ReferenceWhiteScale, OutputPadding0, OutputPadding1;
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
float3 DecodeSrgb(float3 value)
{
    return float3(value.r <= .04045 ? value.r / 12.92 : pow((value.r + .055) / 1.055, 2.4),
        value.g <= .04045 ? value.g / 12.92 : pow((value.g + .055) / 1.055, 2.4),
        value.b <= .04045 ? value.b / 12.92 : pow((value.b + .055) / 1.055, 2.4));
}
float4 PS(PSin input) : SV_Target
{
    int2 p = int2(input.Position.xy);
    float3 encoded = saturate(SceneImage.Load(int3(p, 0)).rgb);
    if (ScRgbOutput > .5) return float4(DecodeSrgb(encoded) * ReferenceWhiteScale, 1);
    uint width, height; SceneImage.GetDimensions(width, height);
    uint index = 3 * (p.y * width + p.x);
    float3 noise = float3(OutputHash(index), OutputHash(index + 1), OutputHash(index + 2));
    noise = (floor(noise / 256.0) / 16777216.0 - .5) / 255.0;
    // Fixed spatial noise avoids flicker; half an 8-bit step leaves black black.
    return float4(saturate(encoded + noise), 1);
}
