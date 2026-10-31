#include "../Common/Atmosphere.hlsl"
#include "../Common/Projection.hlsl"

[numthreads(8,8,1)]
void BuildTransmittance(uint3 id : SV_DispatchThreadID)
{
    uint width,height;
    AtmosphereTransmittanceOutput.GetDimensions(width,height);
    if (id.x>=width || id.y>=height) return;
    float h,mu;
    AtmosphereTransmittanceRay(float2(id.xy)/float2(width-1,height-1),h,mu);
    float distance = AtmosphereDistanceToTop(h,mu), step = distance/256;
    float column = 0;
    [loop] for (uint i=0; i<256; i++)
        column += exp(-AtmosphereAltitudeAlongRay(h,mu,(i+.5)*step)/AtmosphereScaleHeight())*step;
    AtmosphereTransmittanceOutput[id.xy] = float4(exp(-AtmosphereBeta()*column),1);
}

[numthreads(8,8,1)]
void BuildSkyView(uint3 id : SV_DispatchThreadID)
{
    uint width,height;
    AtmosphereSkyViewOutput.GetDimensions(width,height);
    if (id.x>=width || id.y>=height) return;
    // Canonical sun in the YZ plane. Azimuth is applied only during display lookup.
    float3 sun = AtmosphereDirection(0,AtmosphereAltitudeAngle(Sun.Altitude));
    float3 ray = AtmosphereSkyDirection(float2(id.xy)/float2(width-1,height-1));
    float distance = AtmosphereDistanceToTop(0,ray.y);
    float3 transmission = 1, radiance = 0;
    float c = dot(ray,sun), phase = (3.0/(16*AtmospherePi))*(1+c*c);
    float previousDistance = 0;
    [loop] for (uint i=0; i<128; i++)
    {
        float f = (i+1)/128.0, end = distance*f*f;
        float step = end-previousDistance, t = .5*(end+previousDistance);
        float h = AtmosphereAltitudeAlongRay(0,ray.y,t);
        float sunMu = clamp((AtmosphereRadius*sun.y+t*c)/(AtmosphereRadius+h),-1,1);
        float3 tau = AtmosphereBeta()*exp(-h/AtmosphereScaleHeight())*step;
        float3 incoming = AtmosphereSunTransmittance(h,sunMu);
        radiance += transmission*incoming*AtmosphereSegmentScatter(tau)*phase*(40*Sun.Intensity);
        transmission *= exp(-tau);
        previousDistance = end;
    }
    AtmosphereSkyViewOutput[id.xy] = float4(max(0,radiance),1);
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
    camera.Focal = 1/tan(AtmospherePi/6); // 60 degree vertical FOV.
    float3 direction = ProjectionDirection(camera,input.UV);
    if (direction.y<0) return LinearOutput(0);
    float azimuth = 2*AtmospherePi*Sun.Azimuth;
    float3 relative = float3(cos(azimuth)*direction.x-sin(azimuth)*direction.z,
        direction.y,sin(azimuth)*direction.x+cos(azimuth)*direction.z);
    uint width,height;
    AtmosphereSkyView.GetDimensions(width,height);
    return LinearOutput(AtmosphereSkyView.SampleLevel(AtmosphereSampler,
        AtmosphereTexelUV(AtmosphereSkyCoordinates(relative),width,height),0).rgb);
}
