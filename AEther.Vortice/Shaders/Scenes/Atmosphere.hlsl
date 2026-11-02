#include "../Common/AtmosphereTypes.hlsl"
cbuffer Parameters
{
    TransportSettings Transport = { false, true, true };
    AtmosphereSettings Atmosphere = { float3(.5,.5,.5), .5, .5 };
    AtmosphereSunSettings Sun = { .150, .536, .827 };
    AtmosphereViewSettings View = { .223, .536 };
    AtmospherePhaseSettings Phase = { .5, .5, false };
    bool ShowTransmittance = false;
    bool ShowSkyView = false;
    bool ShowMultipleScattering = false;
};
// Midpoints reproduce the reference sunset; power curves retain vacuum and maximum scattering.
float AtmosphereScaleHeight() { return 16*exp2(8*(Atmosphere.ScaleHeight-.5)); }
float3 AtmosphereBeta() { return .1*pow(Atmosphere.RayleighScattering,float3(4.3510744405,2.8889686876,1.0291463457))
        * (16*pow(Atmosphere.Density,4.0831412353)); }
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
    AtmosphereSkySunOutput.GetDimensions(width,height);
    if (id.x>=width || id.y>=height) return;
    float3 ray = AtmosphereSkyDirection(float2(id.xy)/float2(width-1,height-1));
    float distance = AtmosphereDistanceToTop(0,ray.y);
    AtmosphereStoreSky(id.xy,AtmosphereIntegrate(
        float3(0,AtmosphereRadius,0),ray,distance,AtmosphereSkySteps));
}
float4 PS(PSin input) : SV_TARGET
{
    // Reconstructed linear radiance previews bypass exposure and display encoding.
    if (ShowTransmittance)
        return DisplayOutput(AtmosphereTransmittance.SampleLevel(AtmosphereSampler,input.UV,0).rgb);
    if (ShowMultipleScattering) return DisplayOutput(AtmosphereMultipleScattering.SampleLevel(AtmosphereSampler,input.UV,0).rgb);
    if (ShowSkyView)
        {
        uint w,h; AtmosphereSkySun.GetDimensions(w,h);
        float2 unit=saturate((input.UV*float2(w,h)-.5)/float2(w-1,h-1));
        return DisplayOutput(AtmosphereCompose(AtmosphereSampleSky(input.UV),AtmosphereSkyDirection(unit)));
    }
    float yaw = 2*AtmospherePi*View.Azimuth, pitch = AtmosphereAltitudeAngle(View.Altitude);
    float3 forward = AtmosphereDirection(yaw,pitch);
    float3 right = float3(cos(yaw),0,-sin(yaw));
    ProjectionCamera camera = MakeProjectionCamera(0,forward,right,cross(forward,right));
    camera.Focal = 1/tan(AtmospherePi/3); // 120 degree vertical FOV.
    float3 direction = ProjectionDirection(camera,input.UV);
    if (direction.y<0) return LinearOutput(0);
    uint width,height;
    AtmosphereSkySun.GetDimensions(width,height);
    return LinearOutput(min(AtmosphereCompose(AtmosphereSampleSky(
        AtmosphereTexelUV(AtmosphereSkyCoordinates(direction),width,height)),direction),65504));
}
