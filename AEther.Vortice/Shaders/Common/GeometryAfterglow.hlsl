// A pure image-space effect. Only the completed geometry image feeds the fluid;
// its state never supplies surface positions, normals, silhouettes or lighting.
Texture2D<float4> SceneImage;
Texture2D<float4> FluidDye;
SamplerState FluidSampler;

float2 GeometryUV(float2 p) { return .5 + .5 * p / float2(AspectRatio, 1); }
float4 FlightFluidSource(float2 p, float substepOffset)
{
    return max(0, SceneImage.SampleLevel(Linear, GeometryUV(p), 0));
}
float2 FlightCarrier(float2 p)
{
    float depth = SceneImage.SampleLevel(Linear, GeometryUV(p), 0).a * 120;
    return p * (1 + 12 * Camera.Speed) * MusicActivity() / max(8, depth);
}
float2 FlightTurbulence(float2 p, float4 source)
{
    return .12 * Effects.Turbulence * MusicActivity() * (1 + MusicInteraction(GlobalSTBR))
        * float2(sin(5 * p.y + MusicalMovementTime * .2), -sin(5 * p.x - MusicalMovementTime * .17));
}
float FlightDilution(float2 p) { return 0; }
float FlightSlice(float2 p, float4 source) { return 0; }
float4 FlightPigment(float2 p, float4 source)
{
    float3 light = min(source.rgb, 8);
    return float4(light, dot(light, float3(.2126, .7152, .0722)));
}
float4 AfterglowPS(PSin input) : SV_Target
{
    float3 scene = SceneImage.SampleLevel(Linear, input.UV, 0).rgb;
    float3 glow = FluidDye.SampleLevel(FluidSampler, input.UV, 0).rgb;
    float3 light = scene + .4 * Effects.Amount * glow;
    return float4(pow(saturate(1 - exp(-max(0, light) * (1 + 3 * Effects.Exposure))), 1 / 2.2), 1);
}
