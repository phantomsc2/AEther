#ifndef AETHER_POSTPROCESSING
#define AETHER_POSTPROCESSING
float3 DecodeSrgb(float3 value)
{
    return float3(value.r <= .04045 ? value.r / 12.92 : pow((value.r + .055) / 1.055, 2.4),
        value.g <= .04045 ? value.g / 12.92 : pow((value.g + .055) / 1.055, 2.4),
        value.b <= .04045 ? value.b / 12.92 : pow((value.b + .055) / 1.055, 2.4));
}
float3 EncodeSrgb(float3 value)
{
    return float3(value.r <= .0031308 ? 12.92 * value.r : 1.055 * pow(value.r, 1 / 2.4) - .055,
        value.g <= .0031308 ? 12.92 * value.g : 1.055 * pow(value.g, 1 / 2.4) - .055,
        value.b <= .0031308 ? 12.92 * value.b : 1.055 * pow(value.b, 1 / 2.4) - .055);
}
float3 ToneMap(float3 illumination, float gain, float headroom)
{
    float3 x = max(0, illumination) * gain / headroom;
    // Avoid cancellation around black without changing the exponential shoulder.
    float3 mapped = float3(x.r < .001 ? x.r * (1 - x.r * .5 + x.r * x.r / 6) : 1 - exp(-x.r),
        x.g < .001 ? x.g * (1 - x.g * .5 + x.g * x.g / 6) : 1 - exp(-x.g),
        x.b < .001 ? x.b * (1 - x.b * .5 + x.b * x.b / 6) : 1 - exp(-x.b));
    return headroom * mapped;
}
float3 MapOutput(float4 scene, float gain, float headroom, bool scRgb, float whiteScale)
{
    if (scene.a < .5)
    {
        float3 mapped = ToneMap(scene.rgb, gain, headroom);
        return scRgb ? mapped * whiteScale : EncodeSrgb(mapped);
    }
    float3 encoded = saturate(scene.rgb);
    return scRgb ? DecodeSrgb(encoded) * whiteScale : encoded;
}
#endif
