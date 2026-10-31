#ifndef AETHER_ATMOSPHERE
#define AETHER_ATMOSPHERE

// Distances in km, scattering/extinction in inverse km. No aerosols or absorption.
static const float AtmospherePi = 3.141592653589793;
static const float AtmosphereRadius = 6360.0;
struct AtmosphereSettings
{
    float3 RayleighScattering;
    float ScaleHeight;
    float Height;
};
struct AtmosphereSunSettings { float Azimuth, Altitude, Intensity; };
struct AtmosphereViewSettings { float Azimuth, Altitude; };
cbuffer Parameters
{
    AtmosphereSettings Atmosphere = { float3(.058, .135, .331), 7.0/15.0, 80.0/140.0 };
    AtmosphereSunSettings Sun = { 0, 2.0/3.0, .5 };
    AtmosphereViewSettings View = { 0, 100.0/180.0 };
    bool ShowTransmittance = false;
    bool ShowSkyView = false;
};
Texture2D<float4> AtmosphereTransmittance;
Texture2D<float4> AtmosphereSkyView;
SamplerState AtmosphereSampler;
RWTexture2D<float4> AtmosphereTransmittanceOutput;
RWTexture2D<float4> AtmosphereSkyViewOutput;

float AtmosphereHeight() { return lerp(20.0, 160.0, Atmosphere.Height); }
float AtmosphereScaleHeight() { return lerp(1.0, 16.0, Atmosphere.ScaleHeight); }
float3 AtmosphereBeta() { return .1 * Atmosphere.RayleighScattering; }
float AtmosphereAltitudeAngle(float value) { return AtmospherePi * (value - .5); }
float3 AtmosphereDirection(float azimuth, float altitude)
{
    return float3(cos(altitude)*sin(azimuth), sin(altitude), cos(altitude)*cos(azimuth));
}
// Avoid cancellation in r-R and at the atmosphere boundary, especially at zenith.
float AtmosphereAltitudeAlongRay(float h, float mu, float d)
{
    float r = AtmosphereRadius + h;
    float q = h*(2*AtmosphereRadius+h) + d*(2*r*mu+d);
    return max(0, q / (sqrt(max(0, AtmosphereRadius*AtmosphereRadius+q)) + AtmosphereRadius));
}
float AtmosphereDistanceToTop(float h, float mu)
{
    float r = AtmosphereRadius+h, top = AtmosphereRadius+AtmosphereHeight();
    float c = max(0, (AtmosphereHeight()-h)*(top+r));
    float b = r*mu, root = sqrt(b*b+c);
    return b >= 0 ? c/max(root+b, 1e-6) : root-b;
}
bool AtmospherePlanetOccludes(float h, float mu)
{
    float b = (AtmosphereRadius+h)*mu;
    return mu < 0 && b*b > h*(2*AtmosphereRadius+h);
}
float2 AtmosphereTexelUV(float2 unit, uint width, uint height)
{
    float2 size = float2(width,height);
    return (.5+saturate(unit)*(size-1))/size;
}
// Bruneton boundary-distance mapping: x is distance, y is sqrt(r*r-R*R).
// The table covers non-ground-intersecting rays, including negative zenith cosine at altitude.
float2 AtmosphereTransmittanceCoordinates(float h, float mu)
{
    float H = sqrt(AtmosphereHeight()*(2*AtmosphereRadius+AtmosphereHeight()));
    float rho = sqrt(max(0,h*(2*AtmosphereRadius+h)));
    float dmin = AtmosphereHeight()-h, dmax = rho+H;
    return saturate(float2((AtmosphereDistanceToTop(h,mu)-dmin)/max(dmax-dmin,1e-6),rho/H));
}
void AtmosphereTransmittanceRay(float2 unit, out float h, out float mu)
{
    float H = sqrt(AtmosphereHeight()*(2*AtmosphereRadius+AtmosphereHeight()));
    float rho = unit.y*H, r = sqrt(AtmosphereRadius*AtmosphereRadius+rho*rho);
    h = rho*rho/(r+AtmosphereRadius);
    float d = lerp(AtmosphereHeight()-h,rho+H,unit.x);
    mu = d > 1e-6 ? clamp((H*H-rho*rho-d*d)/(2*r*d),-1,1) : 1;
}
float3 AtmosphereSunTransmittance(float h, float mu)
{
    if (AtmospherePlanetOccludes(h,mu)) return 0;
    uint width,height;
    AtmosphereTransmittance.GetDimensions(width,height);
    return AtmosphereTransmittance.SampleLevel(AtmosphereSampler,
        AtmosphereTexelUV(AtmosphereTransmittanceCoordinates(h,mu),width,height),0).rgb;
}
// Top row is zenith; bottom row is horizon. Both azimuth endpoints are stored.
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
// Stable 1-exp(-x) for optically thin segments, with exact zero-scattering limit.
float3 AtmosphereSegmentScatter(float3 opticalDepth)
{
    float3 x = max(0,opticalDepth);
    float3 series = x*(1-x*(.5-x/6));
    return float3(x.x < .01 ? series.x : 1-exp(-x.x),
                  x.y < .01 ? series.y : 1-exp(-x.y),
                  x.z < .01 ? series.z : 1-exp(-x.z));
}
#endif
