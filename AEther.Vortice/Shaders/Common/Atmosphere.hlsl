#ifndef AETHER_ATMOSPHERE
#define AETHER_ATMOSPHERE

// The including scene declares Parameters and supplies AtmosphereScaleHeight()
// and AtmosphereBeta() before including this transport implementation.
#include "AtmosphereTypes.hlsl"
Texture2D<float4> AtmosphereTransmittance;
Texture2D<float4> AtmosphereSkyView;
SamplerState AtmosphereSampler;
RWTexture2D<float4> AtmosphereTransmittanceOutput;
RWTexture2D<float4> AtmosphereSkyViewOutput;

// Numerical truncation only: exp(-10) is about 0.0045% of base density.
static const float AtmosphereExtent = 10;
static const uint AtmosphereTransmittanceSteps = 128, AtmosphereSkySteps = 64;
float AtmosphereHeight() { return AtmosphereExtent*AtmosphereScaleHeight(); }
float AtmosphereDensityAtAltitude(float h)
{
    // Plain exponential; the finite integration boundary does not reshape density.
    return exp(-max(0,h)/AtmosphereScaleHeight());
}
// Draine (2003), normalized over 4*pi. Shape a=0 gives HG, a=1 gives Cornette-Shanks.
// Coefficients are uniform and evaluated once per Sky-View texel, outside integration.
float4 AtmosphereDraineCoefficients()
{
    float g = lerp(-.95,.95,Phase.Anisotropy), a = 2*Phase.Shape;
    float g2 = g*g;
    float k = (1-g2)/(4*AtmospherePi*(1+a*(1+2*g2)/3));
    return float4(1+g2,2*g,k,a);
}
float AtmosphereDrainePhase(float c, float4 coefficients)
{
    c = clamp(c,-1,1);
    float r = rsqrt(max(1e-6,coefficients.x-coefficients.y*c));
    return coefficients.z*(1+coefficients.w*c*c)*r*r*r;
}
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
// Boundary-distance U; compact altitude V concentrates rows within a few scale heights.
// The table covers non-ground-intersecting rays, including negative zenith cosine at altitude.
float2 AtmosphereTransmittanceCoordinates(float h, float mu)
{
    float H = sqrt(AtmosphereHeight()*(2*AtmosphereRadius+AtmosphereHeight()));
    float rho = sqrt(max(0,h*(2*AtmosphereRadius+h)));
    float dmin = AtmosphereHeight()-h, dmax = rho+H;
    float v = sqrt((h/(h+AtmosphereScaleHeight()))*((AtmosphereExtent+1)/AtmosphereExtent));
    return saturate(float2((AtmosphereDistanceToTop(h,mu)-dmin)/max(dmax-dmin,1e-6),v));
}
void AtmosphereTransmittanceRay(float2 unit, out float h, out float mu)
{
    float H = sqrt(AtmosphereHeight()*(2*AtmosphereRadius+AtmosphereHeight()));
    float q = unit.y*unit.y*(AtmosphereExtent/(AtmosphereExtent+1));
    h = AtmosphereScaleHeight()*q/(1-q);
    float r = AtmosphereRadius+h, rho = sqrt(h*(2*AtmosphereRadius+h));
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
// Stable 1-exp(-x) for optically thin segments, with exact zero-scattering limit.
float3 AtmosphereSegmentScatter(float3 opticalDepth, float3 segmentTransmission)
{
    float3 x = max(0,opticalDepth);
    float3 series = x*(1-x*(.5-x/6));
    return float3(x.x < .01 ? series.x : 1-segmentTransmission.x,
                  x.y < .01 ? series.y : 1-segmentTransmission.y,
                  x.z < .01 ? series.z : 1-segmentTransmission.z);
}

// Transport is independent of the scene's camera, projection, and opaque body.
// origin is inside/on the numerical volume; distance ends at its limit or a surface.
float3 AtmosphereIntegrateTransport(float3 origin, float3 ray, float distance, uint steps, out float3 transmission)
{
    transmission=1;
    if (distance<=0) return 0;
    float h0 = max(0,length(origin)-AtmosphereRadius);
    float mu = dot(origin,ray)/(AtmosphereRadius+h0);
    float step = distance/steps;
    float3 betaStep = AtmosphereBeta()*step;
    float3 sun = AtmosphereDirection(2*AtmospherePi*Sun.Azimuth,AtmosphereAltitudeAngle(Sun.Altitude));
    float3 sun2 = AtmosphereDirection(2*AtmospherePi*Sun2.Azimuth,AtmosphereAltitudeAngle(Sun2.Altitude));
    float4 coefficients = AtmosphereDraineCoefficients();
    float c = dot(ray,sun), c2 = dot(ray,sun2);
    float phase = AtmosphereDrainePhase(c,coefficients), phase2 = AtmosphereDrainePhase(c2,coefficients);
    float3 radiance = 0;
    [loop] for (uint i=0; i<steps; i++)
    {
        float t = (i+.5)*step;
        float h = AtmosphereAltitudeAlongRay(h0,mu,t);
        float3 tau = betaStep*AtmosphereDensityAtAltitude(h);
        float3 segmentTransmission = exp(-tau);
        float3 incoming = 0;
        [branch] if (Sun.Intensity>0)
        {
            float sunMu = clamp((dot(origin,sun)+t*c)/(AtmosphereRadius+h),-1,1);
            incoming += AtmosphereSunTransmittance(h,sunMu)*phase*(40*Sun.Intensity);
        }
        [branch] if (Sun2.Intensity>0)
        {
            float sunMu = clamp((dot(origin,sun2)+t*c2)/(AtmosphereRadius+h),-1,1);
            incoming += AtmosphereSunTransmittance(h,sunMu)*phase2*(40*Sun2.Intensity);
        }
        radiance += transmission*incoming*AtmosphereSegmentScatter(tau,segmentTransmission);
        transmission *= segmentTransmission;
    }
    return max(0,radiance);
}

float3 AtmosphereIntegrate(float3 origin, float3 ray, float distance, uint steps)
{
    float3 transmission;
    return AtmosphereIntegrateTransport(origin,ray,distance,steps,transmission);
}

[numthreads(8,8,1)]
void BuildTransmittance(uint3 id : SV_DispatchThreadID)
{
    uint width,height;
    AtmosphereTransmittanceOutput.GetDimensions(width,height);
    if (id.x>=width || id.y>=height) return;
    float h,mu;
    AtmosphereTransmittanceRay(float2(id.xy)/float2(width-1,height-1),h,mu);
    float step = AtmosphereDistanceToTop(h,mu)/AtmosphereTransmittanceSteps;
    float densitySum = 0;
    [loop] for (uint i=0; i<AtmosphereTransmittanceSteps; i++)
    {
        float t = (i+.5)*step;
        densitySum += AtmosphereDensityAtAltitude(AtmosphereAltitudeAlongRay(h,mu,t));
    }
    AtmosphereTransmittanceOutput[id.xy] = float4(exp(-AtmosphereBeta()*(densitySum*step)),1);
}
#endif
