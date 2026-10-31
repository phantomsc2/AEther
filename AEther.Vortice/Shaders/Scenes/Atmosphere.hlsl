#include "../Common/AtmosphereTypes.hlsl"
cbuffer Parameters
{
    AtmosphereSettings Atmosphere = { float3(.058, .135, .331), 7.0/15.0, .0625 };
    AtmosphereSunSettings Sun = { 0, 2.0/3.0, .5 };
    AtmosphereSunSettings Sun2 = { .5, 110.0/180.0, .25 };
    AtmosphereViewSettings View = { 0, 100.0/180.0 };
    AtmospherePhaseSettings Phase = { .5, .5 };
    bool ShowTransmittance = false;
    bool ShowSkyView = false;
};
float AtmosphereScaleHeight() { return lerp(1.0, 16.0, Atmosphere.ScaleHeight); }
float3 AtmosphereBeta() { return .1 * Atmosphere.RayleighScattering * (16*Atmosphere.Density); }
#include "../Common/Atmosphere.hlsl"
#include "../Common/Projection.hlsl"

// Fixed world azimuth reference: +Z. Top is zenith; bottom is horizon.
// Both azimuth endpoints are stored.
float3 AtmosphereSkyDirection(float2 unit)
{
    float elevation = .5*AtmospherePi*(1-unit.y)*(1-unit.y);
    return AtmosphereDirection(2*AtmospherePi*unit.x,elevation);
}
float2 AtmosphereSkyCoordinates(float3 direction)
{
    float phi = atan2(direction.x,direction.z)/(2*AtmospherePi);
    return float2(frac(phi+1),1-sqrt(saturate(asin(saturate(direction.y))/(.5*AtmospherePi))));
}

[numthreads(8,8,1)]
void BuildSkyView(uint3 id : SV_DispatchThreadID)
{
    uint width,height;
    AtmosphereSkyViewOutput.GetDimensions(width,height);
    if (id.x>=width || id.y>=height) return;
    float3 ray = AtmosphereSkyDirection(float2(id.xy)/float2(width-1,height-1));
    float distance = AtmosphereDistanceToTop(0,ray.y);
    AtmosphereSkyViewOutput[id.xy] = float4(AtmosphereIntegrate(
        float3(0,AtmosphereRadius,0),ray,distance,AtmosphereSkySteps),1);
}
float4 PS(PSin input) : SV_TARGET
{
    // Raw stored RGB: no exposure, tone curve, gamma encoding, or lighting evaluation.
    if (ShowTransmittance)
        return DisplayOutput(AtmosphereTransmittance.SampleLevel(AtmosphereSampler,input.UV,0).rgb);
    if (ShowSkyView)
        return DisplayOutput(AtmosphereSkyView.SampleLevel(AtmosphereSampler,input.UV,0).rgb);
    float yaw = 2*AtmospherePi*View.Azimuth, pitch = AtmosphereAltitudeAngle(View.Altitude);
    float3 forward = AtmosphereDirection(yaw,pitch);
    float3 right = float3(cos(yaw),0,-sin(yaw));
    ProjectionCamera camera = MakeProjectionCamera(0,forward,right,cross(forward,right));
    camera.Focal = 1/tan(AtmospherePi/3); // 120 degree vertical FOV.
    float3 direction = ProjectionDirection(camera,input.UV);
    if (direction.y<0) return LinearOutput(0);
    uint width,height;
    AtmosphereSkyView.GetDimensions(width,height);
    return LinearOutput(AtmosphereSkyView.SampleLevel(AtmosphereSampler,
        AtmosphereTexelUV(AtmosphereSkyCoordinates(direction),width,height),0).rgb);
}
