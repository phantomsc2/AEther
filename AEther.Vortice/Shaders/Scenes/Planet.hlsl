#include "../Common/AtmosphereTypes.hlsl"
struct AtmosphereOrbitSettings { float Azimuth, Elevation, Altitude; };
cbuffer Parameters
{
    AtmosphereSettings Atmosphere = { float3(.201, .454, .687), .5, .5 }; // H=.1868R, density=.0172x.
    AtmosphereSunSettings Sun = { .309, .714, .111 };
    AtmosphereSunSettings Sun2 = { .5, 110.0/180.0, .25 };
    AtmosphereViewSettings View = { .5, .5 };
    AtmosphereOrbitSettings Orbit = { .130, .023, 0 };
    AtmospherePhaseSettings Phase = { .5, .5 };
    bool ShowTransmittance = false;
    bool ShowSkyView = false;
};
// Three octaves either side of the reference scale height; midpoint matches the shadow preset.
float AtmosphereScaleHeight() { return AtmosphereRadius*.1868*exp2(6*(Atmosphere.ScaleHeight-.5)); }
// Preserve vacuum and the original maximum, with useful thin atmospheres near the midpoint.
float AtmosphereDensityControl() { return pow(Atmosphere.Density,2.53951953); }
float3 AtmosphereBeta() { return .01 * Atmosphere.RayleighScattering * AtmosphereDensityControl(); }
#include "../Common/Atmosphere.hlsl"
#include "../Common/Projection.hlsl"

float3 PlanetObserver()
{
    return AtmosphereDirection(2*AtmospherePi*Orbit.Azimuth,lerp(-89.0,89.0,Orbit.Elevation)*AtmospherePi/180)
        *(2.94*AtmosphereRadius*pow(3.0,Orbit.Altitude));
}
ProjectionCamera PlanetCamera()
{
    float azimuth = 2*AtmospherePi*Orbit.Azimuth;
    float3 origin = PlanetObserver(), forward = -normalize(origin);
    float3 right = float3(-cos(azimuth),0,sin(azimuth));
    return MakeProjectionCamera(origin,forward,right,cross(forward,right));
}
// Analytic sphere roots. Cross-product distance avoids subtracting two large squared distances.
bool PlanetSphere(float3 origin, float3 ray, float radius, out float2 roots)
{
    float b = dot(origin,ray);
    float3 perpendicular = cross(origin,ray);
    float q = radius*radius-dot(perpendicular,perpendicular);
    roots = -b + float2(-1,1)*sqrt(max(0,q));
    return q>=0 && roots.y>0;
}
float PlanetLimbAngle()
{
    float r = length(PlanetObserver());
    return asin(AtmosphereRadius/r);
}
float PlanetAngularScale(float limb)
{
    // Half the non-disk rows reach the projected altitude 2H (or a hemisphere
    // when inside that region). The remaining rows retain full-sphere coverage.
    float core = asin(min(1,(AtmosphereRadius+2*AtmosphereScaleHeight())/length(PlanetObserver())));
    float span = AtmospherePi-limb, width = core-limb;
    return width/(span-width);
}
// Separate disk and annulus row ranges prevent interpolation across the opaque limb.
// Disk rows concentrate near the limb; non-disk rows cover the rest of the sphere,
// with scale-height-dependent spacing concentrating rows around the dense medium.
float3 PlanetLutDirection(uint2 id, uint width, uint height)
{
    uint halfHeight = height/2;
    bool disk = id.y<halfHeight;
    float v = (disk ? id.y : id.y-halfHeight)/(float)(halfHeight-1);
    float limb = PlanetLimbAngle();
    float k = PlanetAngularScale(limb);
    float angle = disk ? limb*(1-(1-v)*(1-v)) : limb+(AtmospherePi-limb)*k*v/(1-v+k);
    float phi = 2*AtmospherePi*id.x/(width-1);
    ProjectionCamera camera = PlanetCamera();
    return normalize(cos(angle)*camera.Forward+sin(angle)*(cos(phi)*camera.Right+sin(phi)*camera.Up));
}
float2 PlanetLutUV(float3 ray, bool disk, uint width, uint height)
{
    ProjectionCamera camera = PlanetCamera();
    float angle = atan2(length(cross(ray,camera.Forward)),dot(ray,camera.Forward));
    float phi = frac(atan2(dot(ray,camera.Up),dot(ray,camera.Right))/(2*AtmospherePi)+1);
    float limb = PlanetLimbAngle();
    float k = PlanetAngularScale(limb), s = saturate((angle-limb)/(AtmospherePi-limb));
    float v = disk ? 1-sqrt(saturate(1-angle/limb)) : s*(1+k)/(k+s);
    float row = (disk ? 0 : height/2)+v*(height/2-1);
    return float2((.5+phi*(width-1))/width,(.5+row)/height);
}

[numthreads(8,8,1)]
void BuildSkyView(uint3 id : SV_DispatchThreadID)
{
    uint width,height;
    AtmosphereSkyViewOutput.GetDimensions(width,height);
    if (id.x>=width || id.y>=height) return;
    float3 origin = PlanetObserver(), ray = PlanetLutDirection(id.xy,width,height);
    float2 shell;
    if (!PlanetSphere(origin,ray,AtmosphereRadius+AtmosphereHeight(),shell))
    {
        AtmosphereSkyViewOutput[id.xy] = float4(0,0,0,1);
        return;
    }
    bool disk = id.y<height/2;
    float begin = max(0,shell.x), end = shell.y;
    if (disk)
    {
        float2 surface;
        // At the duplicated limb endpoint, roundoff can move the ray a little outside.
        PlanetSphere(origin,ray,AtmosphereRadius,surface);
        end = max(begin,surface.x);
    }
    float3 entry = origin+begin*ray;
    AtmosphereSkyViewOutput[id.xy] = float4(AtmosphereIntegrate(entry,ray,max(0,end-begin),AtmosphereSkySteps),1);
}

float4 PS(PSin input) : SV_TARGET
{
    if (ShowTransmittance) return DisplayOutput(AtmosphereTransmittance.SampleLevel(AtmosphereSampler,input.UV,0).rgb);
    if (ShowSkyView) return DisplayOutput(AtmosphereSkyView.SampleLevel(AtmosphereSampler,input.UV,0).rgb);
    ProjectionCamera camera = PlanetCamera();
    float yaw = 2*AtmospherePi*(View.Azimuth-.5), pitch = AtmosphereAltitudeAngle(View.Altitude);
    float3 forward = cos(yaw)*camera.Forward+sin(yaw)*camera.Right;
    camera.Right = cos(yaw)*camera.Right-sin(yaw)*camera.Forward;
    camera.Forward = cos(pitch)*forward+sin(pitch)*camera.Up;
    camera.Up = cross(camera.Forward,camera.Right);
    camera.Focal = 1/tan(AtmospherePi/3); // 120 degree vertical FOV.
    float3 ray = ProjectionDirection(camera,input.UV);
    float2 surface;
    bool disk = PlanetSphere(camera.Origin,ray,AtmosphereRadius,surface);
    uint width,height;
    AtmosphereSkyView.GetDimensions(width,height);
    float3 radiance = AtmosphereSkyView.SampleLevel(AtmosphereSampler,PlanetLutUV(ray,disk,width,height),0).rgb;
    return LinearOutput(radiance);
}
