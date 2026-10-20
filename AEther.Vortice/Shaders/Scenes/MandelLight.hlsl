#include "../Common/Inputs.hlsl"
#include "../Common/TransportMoments.hlsl"

// Audio-independent participating-medium prototype. Camera and light rays cache
// occupancy moments over the entire volume; integration has no material boundary.
struct FractalSettings { float Scale; float Potential; float MaximumIterations; };
struct AirSettings { float Density; float ScaleHeight; float3 Scattering; };
struct InteriorSettings { bool Enabled; float Density; float EdgeSoftness; float3 Scattering; float3 Absorption; };
struct EmissionSettings { bool Enabled; float Strength; float3 Color; };
struct TransportSettings { bool Trigonometric; bool OrbitRejection; };
struct LightSettings { float Intensity; float Speed; float Radius; float Phase; float3 Color; bool Enabled; };
struct QualitySettings { float Samples; float TransportSteps; };
struct ViewSettings { bool ShowLightTransport; bool ShowAtmosphereLookup; float CameraSpeed; float TransportLight; float CameraDistance; float HorizontalOffset; float TransportDepth; };
cbuffer Parameters
{
    FractalSettings Fractal = { .75, 1, .2 };
    AirSettings Air = { .3, .4, float3(1,1,1) };
    InteriorSettings Interior = { true, .25, .2, float3(.7,.7,.7), float3(.15,.3,.45) };
    EmissionSettings Emission = { true, .2, float3(1,.35,.1) };
    TransportSettings Transport = { false, true };
    LightSettings Light = { .55, .35, .2, 0, float3(1,1,1), true };
    LightSettings Light2 = { .35, .35, .2, .25, float3(.2,.6,1), false };
    LightSettings Light3 = { .35, .35, .2, .5, float3(1,.3,.1), false };
    LightSettings Light4 = { .35, .35, .2, .75, float3(.2,1,.4), false };
    QualitySettings Quality = { .23404255, .42857143 };
    ViewSettings View = { false, false, .2, 0, .4324711, .5, 1 };
};
// Host selects one light per transport/integration draw. Camera transport is
// shared; angular transport and MIS samples belong to that selected light.
// Index 4 integrates emission once, independently of enabled point lights.
cbuffer LightConstants { uint LightIndex; float3 LightPadding; };
LightSettings CurrentLight()
{
    if(LightIndex==1) return Light2;
    if(LightIndex==2) return Light3;
    if(LightIndex==3) return Light4;
    return Light;
}
Texture2D<float4> LightRadiance;
Texture2D<float4> LightMoments;
Texture2D<float4> LightAirMoments;
Texture2D<float4> LightTransport;
Texture2D<float4> CameraMoments;
Texture2D<float4> CameraAirMoments;
Texture2D<float4> CameraTransport;
Texture2D<uint4> CameraImportance;
Texture2D<float> AtmosphereOpticalDepth;
SamplerState AtmosphereSampler;
static const float Pi = 3.14159265359;
static const float InteriorRadius = 10.5;
static const float AirRadius = 16;
// Camera orientation depends only on orbit time, including at distance zero.
void CameraBasis(out float3 forward, out float3 right, out float3 up)
{
    float sine,cosine;
    sincos(T*.25*View.CameraSpeed,sine,cosine);
    forward=normalize(float3(-9*cosine-13.5*sine,-6,13.5*cosine-9*sine));
    right=normalize(cross(forward,float3(0,1,0)));
    up=cross(right,forward);
}
float3 CameraPosition()
{
    float3 forward,right,up;
    CameraBasis(forward,right,up);
    float distance=40*View.CameraDistance;
    return -distance*forward+(2*View.HorizontalOffset-1)*distance*right;
}

// Potential spans twelve octaves; slider 1 retains the original level 0.5.
float Potential() { return .5 * exp2(12 * (Fractal.Potential - 1)); }

float FractalField(float3 p)
{
    float scale = 8 * Fractal.Scale - 4;
    int iterations = 1 + (int)round(15 * Fractal.MaximumIterations);
    float3 z = p;
    float expansion=max(1,abs(scale));
    float normalization=pow(expansion,-iterations);
    if (iterations>4 && expansion>1.01)
    {
        // Conservative orbit rejection, not a ray step. Box folding removes at
        // most two per coordinate, and sphere folding never shrinks the norm:
        // r_next >= abs(scale)*r - (2*abs(scale)+max(abs(p))).
        // Reject only when this bound proves the FINAL potential is unoccupied.
        float fixedRadius=(2*expansion+max(abs(p.x),max(abs(p.y),abs(p.z))))/(expansion-1);
        float finalRadius=Potential()/normalization;
        float inverseRemaining=normalization;
        [loop] for (int j=0;j<iterations;++j)
        {
            float escapeRadius=fixedRadius+(finalRadius-fixedRadius)*inverseRemaining;
            if (Transport.OrbitRejection && max(abs(z.x),max(abs(z.y),abs(z.z)))>escapeRadius*1.0001+1e-5) return Potential();
            z=2*clamp(z,-1,1)-z;
            z*=1/clamp(dot(z,z),.25,1);
            z=scale*z+p;
            inverseRemaining*=expansion;
        }
        return length(z)*normalization;
    }
    [loop] for (int i = 0; i < iterations; ++i)
    {
        z = 2 * clamp(z, -1, 1) - z;
        float fold = 1 / clamp(dot(z, z), .25, 1);
        z *= fold;
        z = scale * z + p;
    }
    return length(z)*normalization;
}

bool HasInterior() { return Interior.Enabled; }
float InteriorDensity() { return 4*Interior.Density*Interior.Density; }
// Dilute material gradually replaces air. At zero density its geometric support
// must not leave a vacuum cutout in the surrounding atmosphere.
float AirDisplacement() { return saturate(InteriorDensity()); }
float3 InteriorExtinction()
{
    return InteriorDensity()*(Interior.Scattering+Interior.Absorption);
}
float InteriorMask(float3 p)
{
    if (!HasInterior() || dot(p,p)>=InteriorRadius*InteriorRadius) return 0;
    float q=FractalField(p)/Potential();
    // A continuous density field with compact spherical support.
    return 1-smoothstep(1-max(1e-5,Interior.EdgeSoftness),1,q);
}
bool Interval(float3 o, float3 d, float radius, out float a, out float b)
{
    float v = dot(o, d), c = dot(o, o) - radius * radius;
    float disc = v*v - c;
    if (disc <= 0) { a = b = 0; return false; }
    float root = sqrt(disc);
    a = max(0, -v - root); b = -v + root;
    return b > a;
}

float3 CameraRay(float2 uv)
{
    float3 forward,right,up;
    CameraBasis(forward,right,up);
    float aspect=AspectRatio*(View.ShowLightTransport ? .7 : 1);
    float2 screen=(2*uv-1)*float2(max(1,aspect),-max(1,1/aspect));
    return normalize(2*forward+screen.x*right+screen.y*up);
}
float3 LightPosition(LightSettings light)
{
    float angle = T * .5 * light.Speed - .8 + 6.28318530718*light.Phase;
    // The light orbit lies outside the interior's compact support.
    return float3(12*cos(angle), 3*sin(.7*angle + .3), 12*sin(angle));
}
void LightBasis(float3 light, out float3 axis, out float3 u, out float3 v)
{
    float3 delta=CameraPosition()-light;
    float distance=length(delta);
    axis = distance>1e-6 ? delta/distance : float3(0,0,1);
    float3 helper = abs(axis.y) < .95 ? float3(0,1,0) : float3(1,0,0);
    u = normalize(cross(helper, axis)); v = cross(axis, u);
}

struct LightContext
{
    float3 Position;
    float3 Axis;
    float3 U;
    float3 V;
};
LightContext MakeLightContext(LightSettings light)
{
    LightContext source;
    source.Position=LightPosition(light);
    LightBasis(source.Position,source.Axis,source.U,source.V);
    return source;
}
LightContext MakeLightContext() { return MakeLightContext(CurrentLight()); }
LightSettings PreviewLight()
{
    int index=(int)round(3*View.TransportLight);
    if (index==1) return Light2;
    if (index==2) return Light3;
    if (index==3) return Light4;
    return Light;
}
// 1. STATIC PRECALCULATION: cached until atmosphere shape or shader changes.
// HQ unit-density optical depth for outward rays. x is radius/16, y is sqrt(mu).
// Exact endpoint texels and extra grazing-angle resolution reduce interpolation
// error. Only scale height changes this cache; density is applied at lookup time.
float AtmospherePS(PSin input) : SV_Target
{
    float r = AirRadius * (input.Position.x - .5) / 511;
    float u = (input.Position.y - .5) / 255;
    float mu = u*u;
    float along = r*mu;
    float distance = max(0,sqrt(max(0,along*along + AirRadius*AirRadius-r*r))-along);
    float stepLength = distance/1024, tau=0;
    float h=.5+2*Air.ScaleHeight;
    [loop] for(int i=0;i<1024;++i)
    {
        float t=(i+.5)*stepLength;
        float radius=sqrt(max(0,r*r+t*t+2*along*t));
        tau += .08*exp((6-radius)/h)*stepLength;
    }
    return tau;
}
float OutwardOpticalDepth(float r, float mu)
{
    uint width,height; AtmosphereOpticalDepth.GetDimensions(width,height);
    float2 uv=(float2(saturate(r/AirRadius),sqrt(saturate(mu)))
        *float2(width-1,height-1)+.5)/float2(width,height);
    return AtmosphereOpticalDepth.SampleLevel(AtmosphereSampler,uv,0);
}
float OpticalDepthToExit(float3 p, float3 direction)
{
    float r=length(p);
    float mu=r>1e-6 ? clamp(dot(p,direction)/r,-1,1) : 0;
    if(mu>=0) return OutwardOpticalDepth(r,mu);
    float impact=r*sqrt(max(0,1-mu*mu));
    // Inward ray crosses the chord midpoint: full chord minus the near tail.
    return max(0,2*OutwardOpticalDepth(impact,0)-OutwardOpticalDepth(r,-mu));
}
float UnitAirDensity(float3 p)
{
    float r=length(p);
    return r<AirRadius ? .08*exp((6-r)/(.5+2*Air.ScaleHeight)) : 0;
}

// Two independent positive measures: occupied length, and the unit-density air
// displaced by that occupancy. Material edits reuse these geometric integrals.
struct RayTransport
{
    float4 Material : SV_Target0;
    float4 DisplacedAir : SV_Target1;
    float4 Totals : SV_Target2; // occupied length, displaced air depth, support interval
    uint4 Importance : SV_Target3; // 16 camera occupancy bins, eight bits each
};
RayTransport IntegrateTransport(float3 origin, float3 direction, bool camera)
{
    RayTransport result=(RayTransport)0;
    float a,b;
    if (!Interval(origin,direction,InteriorRadius,a,b)) return result;
    result.Totals.zw=float2(a,b);
    if (!HasInterior()) return result;
    // Group work into 16 bins. These are a sampling proposal, independent of
    // the four-moment attenuation representation and of the chosen basis.
    int perBin=8+(int)round(56*Quality.TransportSteps);
    int steps=16*perBin;
    float ds=(b-a)/steps;
    [loop] for (int bin=0;bin<16;++bin)
    {
        float mass=0;
        [loop] for (int i=0;i<perBin;++i)
        {
            float u=(bin*perBin+i+.5)/steps;
            float3 p=origin+lerp(a,b,u)*direction;
            float occupied=InteriorMask(p)*ds;
            if (occupied<=0) continue;
            float air=occupied*UnitAirDensity(p);
            float4 basis=MomentBasis(2*u-1,Transport.Trigonometric);
            result.Material+=occupied*basis;
            result.DisplacedAir+=air*basis;
            result.Totals.xy+=float2(occupied,air);
            mass+=occupied;
        }
        if (camera)
        {
            uint packed=(uint)round(255*saturate(mass/(ds*perBin)))<<(8*(bin&3));
            if (bin<4) result.Importance.x|=packed;
            else if (bin<8) result.Importance.y|=packed;
            else if (bin<12) result.Importance.z|=packed;
            else result.Importance.w|=packed;
        }
    }
    result.Material/=max(result.Totals.x,1e-20);
    result.DisplacedAir/=max(result.Totals.y,1e-20);
    return result;
}

// 2. CAMERA AND LIGHT TRANSPORT: no ray stops at a material entry.
RayTransport CameraTransportPS(PSin input)
{
    if (View.ShowLightTransport)
    {
        if (input.UV.x>=.7) return (RayTransport)0;
        input.UV.x/=.7;
    }
    return IntegrateTransport(CameraPosition(),CameraRay(input.UV),true);
}
float3 AngularDirection(float2 uv, LightContext source)
{
    float gamma=Pi*uv.x, alpha=2*Pi*uv.y;
    return cos(gamma)*source.Axis+sin(gamma)*(cos(alpha)*source.U+sin(alpha)*source.V);
}
RayTransport LightTransportPS(PSin input)
{
    LightContext source=MakeLightContext();
    float3 d=AngularDirection(input.UV,source);
    return IntegrateTransport(source.Position,d,false);
}

float2 AccumulatedMedium(RayTransport ray, float distance, float a, float b)
{
    if (ray.Totals.x<=0 || b<=a || distance<=a) return 0;
    if (distance>=b) return ray.Totals.xy;
    float depth=2*(distance-a)/(b-a)-1;
    return ray.Totals.xy*float2(
        MomentFraction(ray.Material,depth,Transport.Trigonometric),
        MomentFraction(ray.DisplacedAir,depth,Transport.Trigonometric));
}
float3 MediumTransmission(RayTransport ray, float distance, float a, float b, float airDepth)
{
    float2 medium=0;
    if (InteriorDensity()>0 && ray.Totals.x>0)
        medium=AccumulatedMedium(ray,distance,a,b);
    float3 tau=InteriorExtinction()*medium.x
        +Air.Density*Air.Scattering*max(0,airDepth-AirDisplacement()*medium.y);
    return exp(-max(0,tau));
}
RayTransport ReadCameraTransport(int2 pixel)
{
    RayTransport ray;
    ray.Material=CameraMoments.Load(int3(pixel,0));
    ray.DisplacedAir=CameraAirMoments.Load(int3(pixel,0));
    ray.Totals=CameraTransport.Load(int3(pixel,0));
    ray.Importance=CameraImportance.Load(int3(pixel,0));
    return ray;
}

// Allocate samples using the fractal occupancy already measured by camera
// transport. No potential gradients or additional field evaluations are needed.
// A uniform 10% component preserves coverage of sub-step/cache-missed features.
struct DensityProposal { float4 Cdf[4]; };
DensityProposal MakeDensityProposal(RayTransport ray, bool emission)
{
    DensityProposal result;
    float weights[16];
    float rate=dot(InteriorExtinction(),float3(1,1,1)/3);
    float binLength=max(0,ray.Totals.w-ray.Totals.z)/16;
    float prefix=0, sum=0;
    [unroll] for (int i=0;i<16;++i)
    {
        float mass=((ray.Importance[i/4]>>(8*(i%4)))&255)*(binLength/255);
        weights[i]=mass*exp(-rate*(prefix+.5*mass));
        sum+=weights[i];
        prefix+=mass;
    }
    float blend=sum>1e-12 ? .9*(emission ? 1 : saturate(InteriorDensity()*ray.Totals.x)) : 0;
    float cumulative=0;
    [unroll] for (int j=0;j<16;++j)
    {
        cumulative+=(1-blend)/16+blend*weights[j]/max(sum,1e-20);
        result.Cdf[j/4][j%4]=cumulative;
    }
    result.Cdf[3].w=1;
    return result;
}
float DensityPdf(DensityProposal proposal, float t, float a, float b)
{
    int bin=min(15,(int)(16*saturate((t-a)/(b-a))));
    int previous=max(0,bin-1);
    float before=bin>0 ? proposal.Cdf[previous/4][previous%4] : 0;
    return (proposal.Cdf[bin/4][bin%4]-before)*16/(b-a);
}
float DensitySample(DensityProposal proposal, float u, float a, float b, out float pdf)
{
    int bin=0;
    [unroll] for (int i=0;i<15;++i) bin+=u>=proposal.Cdf[i/4][i%4] ? 1 : 0;
    int previous=max(0,bin-1);
    float before=bin>0 ? proposal.Cdf[previous/4][previous%4] : 0;
    float mass=max(1e-7,proposal.Cdf[bin/4][bin%4]-before);
    pdf=mass*16/(b-a);
    return a+(bin+saturate((u-before)/mass))*(b-a)/16;
}
float3 CameraTransmission(float3 p, float3 direction, float distance, float cameraExitDepth,
    RayTransport ray, float a, float b)
{
    float airDepth=Air.Density>0 ? max(0,OpticalDepthToExit(p,-direction)-cameraExitDepth) : 0;
    return MediumTransmission(ray,distance,a,b,airDepth);
}
float3 LightTransmission(float3 p, LightContext source)
{
    float3 delta=p-source.Position;
    float r=length(delta);
    if (r<=1e-6) return 1;
    float3 w=delta/r;
    float x=dot(w,source.U), y=dot(w,source.V);
    float perpendicular=sqrt(x*x+y*y);
    bool forward=dot(source.Position+p,w)>=0;
    float gamma=atan2(perpendicular,dot(w,source.Axis));
    float alpha=perpendicular>1e-6 ? atan2(y,x) : 0;
    float2 uv=float2(gamma/Pi,frac(alpha/(2*Pi)));
    uint width,height; LightTransport.GetDimensions(width,height);
    float2 texel=uv*float2(width,height)-.5;
    int2 base=(int2)floor(texel);
    float2 f=frac(texel);
    float sampleTail=Air.Density>0 ? OpticalDepthToExit(p,forward ? w : -w) : 0;
    float lightTail=Air.Density>0 ? OpticalDepthToExit(source.Position,forward ? w : -w) : 0;
    float airDepth=max(0,forward ? lightTail-sampleTail : sampleTail-lightTail);
    float3 transmission=0;
    [unroll] for (int j=0;j<2;++j)
        [unroll] for (int i=0;i<2;++i)
        {
            int2 at=int2(clamp(base.x+i,0,(int)width-1),(base.y+j+(int)height)%(int)height);
            RayTransport ray;
            ray.Material=LightMoments.Load(int3(at,0));
            ray.DisplacedAir=LightAirMoments.Load(int3(at,0));
            ray.Totals=LightTransport.Load(int3(at,0));
            float weight=(i ? f.x : 1-f.x)*(j ? f.y : 1-f.y);
            // Each ray has its own chord normalization: reconstruct each tap
            // at the receiver distance, exponentiate, then filter transmission.
            transmission+=weight*MediumTransmission(ray,r,ray.Totals.z,ray.Totals.w,airDepth);
        }
    return transmission;
}

float Extinction(float3 p)
{
    return Air.Density*UnitAirDensity(p);
}
float Phase(float cosine) { return 3*(1+cosine*cosine)/(16*Pi); }
float3 Falloff(float r2)
{
    LightSettings light=CurrentLight();
    float radius = .01 + .49*light.Radius*light.Radius;
    return light.Color * (3000*light.Intensity*light.Intensity / (r2+radius*radius));
}
uint RandomHash(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352d;
    value ^= value >> 15;
    value *= 0x846ca68b;
    return value ^ (value >> 16);
}
float Random01(uint seed)
{
    // Open interval: inverse CDFs never receive either endpoint. No time jitter.
    return ((RandomHash(seed) >> 9) + .5) / 8388608.0;
}

// 3. CAMERA EMISSION: one independent draw, including when all lights are off.
// Stratified density-guided integration retains uniform coverage of the support.
float3 RenderEmission(PSin input)
{
    if (!HasInterior() || !Emission.Enabled || Emission.Strength<=0 || !any(Emission.Color>0)) return 0;
    float3 origin=CameraPosition(), direction=CameraRay(input.UV);
    float nearT,farT;
    if (!Interval(origin,direction,InteriorRadius,nearT,farT)) return 0;
    float rayLength=farT-nearT;
    if (rayLength<=1e-6) return 0;
    float cameraExitDepth=length(origin)<AirRadius ? OpticalDepthToExit(origin,-direction) : 0;
    RayTransport camera=ReadCameraTransport((int2)input.Position.xy);
    DensityProposal proposal=MakeDensityProposal(camera,true);
    uint2 pixel=(uint2)input.Position.xy;
    uint seed=RandomHash(pixel.x ^ RandomHash(pixel.y+0x9e3779b9) ^ 0x68bc21eb);
    int samples=1+(int)round(47*Quality.Samples);
    float3 radiance=0;
    [loop] for (int j=0;j<samples;++j)
    {
        float u=min((j+Random01(seed ^ RandomHash((uint)(j+1))))/samples,.99999994);
        float pdf;
        float t=DensitySample(proposal,u,nearT,farT,pdf);
        float3 p=origin+t*direction;
        float mask=InteriorMask(p);
        if (mask<=0) continue;
        float3 transmission=CameraTransmission(p,direction,t,cameraExitDepth,camera,nearT,farT);
        float3 emission=8*Emission.Strength*Emission.Strength*Emission.Color*mask;
        radiance+=transmission*emission/pdf;
    }
    return max(0,radiance/samples);
}

// 4. CAMERA LIGHT SAMPLING: air proposals plus density-guided support sampling.
float3 RenderLight(PSin input)
{
    if (LightIndex==4) return RenderEmission(input);
    LightSettings light=CurrentLight();
    if (!light.Enabled || light.Intensity <= 0 || !any(light.Color>0)) return 0;
    float3 origin = CameraPosition();
    float3 direction=CameraRay(input.UV);
    float nearT, farT;
    if (!Interval(origin, direction, AirRadius, nearT, farT)) return 0;
    float cameraExitDepth=length(origin)<AirRadius ? OpticalDepthToExit(origin,-direction) : 0;

    if (farT<=nearT+1e-6) return 0;
    float interiorNear,interiorFar;
    bool crossesInterior=Interval(origin,direction,InteriorRadius,interiorNear,interiorFar);
    RayTransport camera=ReadCameraTransport((int2)input.Position.xy);
    DensityProposal proposal=MakeDensityProposal(camera,false);
    LightContext lightSource=MakeLightContext();
    float3 lightPosition = lightSource.Position;
    float rayLength = farT - nearT;
    float3 toLight = lightPosition - origin;
    float projection = dot(toLight, direction);
    float3 perpendicularVector = toLight - projection * direction;
    // Geometric equiangular angle: gamma = theta + pi/2 - beta exactly.
    // The finite light-radius falloff remains in the integrand. Near the axis,
    // use uniform distance sampling; every cross-PDF uses the same fallback.
    float perpendicular = length(perpendicularVector);
    float angleNear = atan2(nearT - projection, perpendicular);
    float angleFar = atan2(farT - projection, perpendicular);
    float angleSpan = angleFar - angleNear;
    bool useAngular = perpendicular > 1e-3 && angleSpan > 1e-5;
    float referenceDepth = Air.Density*dot(Air.Scattering,float3(1,1,1)/3)
        *max(0,OpticalDepthToExit(origin+farT*direction,-direction)-cameraExitDepth);
    // Fit density * camera transmission at the two endpoints. Density's log
    // change is exact for this radial profile; its interpolation along the ray
    // is only a proposal approximation, corrected by the MIS PDF.
    float logDensityChange=(length(origin+nearT*direction)-length(origin+farT*direction))
        /(.5+2*Air.ScaleHeight);
    float slope=clamp(logDensityChange-referenceDepth,-20,20);
    bool increasing=slope>0;
    float depth=abs(slope);
    bool useExponential=depth>1e-3;
    float referenceRate=depth/rayLength;
    float exponentialMass=1-exp(-depth);

    // The support interval is an integration domain, never a material shape.
    // Keep its proposal active for air too: changing material concentration must
    // not reshuffle the sample set or change the grain abruptly at zero density.
    float interiorLength=interiorFar-interiorNear;
    bool useBounded=crossesInterior && interiorLength>1e-6;
    int strategies=useBounded ? 3 : 2;

    uint2 pixel = (uint2)input.Position.xy;
    uint seed = RandomHash(pixel.x ^ RandomHash(pixel.y + 0x9e3779b9)
        ^ (LightIndex==0 ? 0 : RandomHash(LightIndex)));
    int samples = 1 + (int)round(47 * Quality.Samples);
    float3 radiance = 0;
    bool hasScattering=(Air.Density>0 && any(Air.Scattering*light.Color>0))
        || (HasInterior() && InteriorDensity()>0 && any(Interior.Scattering*light.Color>0));
    [loop] for (int sampleIndex = 0; sampleIndex < samples && hasScattering; ++sampleIndex)
    {
        [loop] for (int strategy = 0; strategy < strategies; ++strategy)
        {
            float u = (sampleIndex + Random01(seed ^ RandomHash((uint)(sampleIndex * 3 + strategy + 1)))) / samples;
            // Adding the stratum index can round an open-interval value to 1.
            u = min(u, .99999994);
            float t;
            if (strategy == 2)
            {
                float proposalPdf;
                t=DensitySample(proposal,u,interiorNear,interiorFar,proposalPdf);
            }
            else if (strategy == 0 && useAngular)
                t = projection + perpendicular * tan(lerp(angleNear, angleFar, u));
            else if (strategy == 1 && useExponential)
            {
                float distance=-log(1-u*exponentialMass)/referenceRate;
                t=increasing ? farT-distance : nearT+distance;
            }
            else
                t = nearT + u * rayLength;
            t = clamp(t, nearT, farT);

            float pdfAngular = 1 / rayLength;
            if (useAngular)
                pdfAngular = perpendicular / (angleSpan * (perpendicular * perpendicular + (t - projection) * (t - projection)));
            float pdfExponential = 1 / rayLength;
            if (useExponential)
                pdfExponential = referenceRate * exp(-referenceRate * (increasing ? farT-t : t-nearT)) / exponentialMass;
            float pdfInterior=0;
            if (useBounded && t>=interiorNear && t<=interiorFar)
                pdfInterior=DensityPdf(proposal,t,interiorNear,interiorFar);

            float3 p = origin + t * direction;
            float mask=HasInterior() ? InteriorMask(p) : 0;
            float extinction = Extinction(p);
            // Air displacement fades with material concentration, like extinction.
            float3 scattering = extinction*Air.Scattering*(1-AirDisplacement()*mask)
                +InteriorDensity()*Interior.Scattering*mask;
            if (!any(scattering > 1e-8)) continue;
            float3 lightVector = lightPosition - p;
            float distanceSquared = dot(lightVector, lightVector);
            float3 lightDirection = lightVector / max(sqrt(distanceSquared), 1e-6);
            float3 lightTransmission = LightTransmission(p,lightSource);
            float3 cameraTransmission = CameraTransmission(p,direction,t,cameraExitDepth,
                camera,interiorNear,interiorFar);
            float phase = Phase(dot(-lightDirection,-direction));
            // Equal-count balance MIS, PDFs measured per world-space distance.
            float3 integrand = cameraTransmission
                * scattering*lightTransmission
                * phase * Falloff(distanceSquared);
            radiance += integrand / (pdfAngular + pdfExponential + pdfInterior);
        }
    }
    radiance /= samples;
    return max(0,radiance);
}
// One independent draw per light into a shared linear FP32 accumulation image.
float4 LightPS(PSin input) : SV_Target
{
    if(View.ShowLightTransport)
    {
        if(input.UV.x>=.7) return 0;
        input.UV.x/=.7;
    }
    return float4(RenderLight(input),0);
}
float4 DisplayRadiance(PSin input)
{
    float3 radiance=LightRadiance.Load(int3((int2)input.Position.xy,0)).rgb;
    float3 mapped=1-exp(-max(0,radiance));
    float3 encoded=lerp(12.92*mapped,1.055*pow(mapped,1/2.4)-.055,step(.0031308,mapped));
    return float4(encoded,1);
}
float4 PS(PSin input) : SV_Target
{
    if (View.ShowAtmosphereLookup)
    {
        // Full-window, log-scaled unit-density optical depth. Radius increases
        // left to right; sqrt(outward zenith cosine) increases top to bottom.
        float2 uv=.5+(input.UV-.5)*float2(max(1,AspectRatio/2),max(1,2/AspectRatio));
        if(any(uv<0)||any(uv>=1)) return float4(.025,.025,.025,1);
        uint w,h; AtmosphereOpticalDepth.GetDimensions(w,h);
        float depth=AtmosphereOpticalDepth.Load(int3((int2)(uv*float2(w,h)),0));
        float maximum=AtmosphereOpticalDepth.Load(int3(0,0,0));
        float value=saturate(log2(1+depth)/max(1e-6,log2(1+maximum)));
        return float4(value.xxx,1);
    }
    if (!View.ShowLightTransport || input.UV.x < .7) return DisplayRadiance(input);
    // Square map in the right-hand panel: gamma horizontal, slice alpha vertical.
    // Display RGB material opacity at a selectable radial distance (0-32).
    float2 uv = float2((input.UV.x - .7)/.3, input.UV.y);
    float aspect = .3*AspectRatio;
    uv = .5 + (uv-.5)*float2(max(1,aspect),max(1,1/aspect));
    if (any(uv < 0) || any(uv >= 1)) return float4(.025,.025,.025,1);
    uint w,h; LightTransport.GetDimensions(w,h);
    int2 at=(int2)(uv*float2(w,h));
    RayTransport ray;
    ray.Material=LightMoments.Load(int3(at,0));
    ray.DisplacedAir=LightAirMoments.Load(int3(at,0));
    ray.Totals=LightTransport.Load(int3(at,0));
    float occupied=AccumulatedMedium(ray,32*View.TransportDepth,ray.Totals.z,ray.Totals.w).x;
    return float4(1-exp(-InteriorExtinction()*occupied),1);
}
