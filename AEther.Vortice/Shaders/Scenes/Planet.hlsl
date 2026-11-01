#include "../Common/AtmosphereTypes.hlsl"
struct RingSettings { float Radius, Speed, Width, Inclination, Size; };
struct AtmosphereOrbitSettings { float Azimuth, Elevation, Altitude; };
cbuffer Parameters
{
    AtmosphereSettings Atmosphere = { float3(.201, .454, .687), .18, .5 }; // Thin blue limb with a warm inner edge.
    AtmosphereSunSettings Sun = { .25, .5, .5 };
    AtmosphereSunSettings Sun2 = { .5, 110.0/180.0, 0 };
    AtmosphereViewSettings View = { .5, .5 };
    AtmosphereOrbitSettings Orbit = { 0, .5, .1024908 };
    AtmospherePhaseSettings Phase = { .3, .5 };
    RingSettings Ring = { .03030303, .4, .1, .22222222, .05714286 };
    bool ShowAerial=false;
    bool ShowAerialTransmission=false;
    float AerialSlice=.5;
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
        *(AtmosphereRadius*pow(8.82,Orbit.Altitude)); // Surface at zero, original 8.82R maximum.
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
    return asin(saturate(AtmosphereRadius/r));
}
float PlanetAngularScale(float limb)
{
    // Half the non-disk rows reach the projected altitude 2H (or a hemisphere
    // when inside that region). The remaining rows retain full-sphere coverage.
    float core = asin(min(1,(AtmosphereRadius+2*AtmosphereScaleHeight())/length(PlanetObserver())));
    float span = AtmospherePi-limb, width = core-limb;
    // From the surface, distribute non-disk rows across the sky hemisphere;
    // the dense core and limb coincide, so the orbital mapping degenerates.
    return width>1e-6 ? width/(span-width) : 1;
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

ProjectionCamera PlanetDisplayCamera()
{
    ProjectionCamera camera = PlanetCamera();
    float yaw = 2*AtmospherePi*(View.Azimuth-.5), pitch = AtmosphereAltitudeAngle(View.Altitude);
    float3 forward = cos(yaw)*camera.Forward+sin(yaw)*camera.Right;
    camera.Right = cos(yaw)*camera.Right-sin(yaw)*camera.Forward;
    camera.Forward = cos(pitch)*forward+sin(pitch)*camera.Up;
    camera.Up = cross(camera.Forward,camera.Right);
    camera.Focal = 1/tan(AtmospherePi/3); // 120 degree vertical FOV.
    camera.Near=AtmosphereRadius*.0001;
    camera.Far=length(camera.Origin)+AtmosphereRadius+AtmosphereHeight();
    return camera;
}
Texture3D<float4> AerialRadiance, AerialTransmission;
RWTexture3D<float4> AerialRadianceOutput, AerialTransmissionOutput;
Texture2D<float> FroxelSurfaceDepth;
[numthreads(8,8,1)]
void BuildAerial(uint3 id:SV_DispatchThreadID)
{
    uint nx,ny,nz; AerialRadianceOutput.GetDimensions(nx,ny,nz);
    if(id.x>=nx || id.y>=ny) return;
    ProjectionCamera camera=PlanetDisplayCamera();
    float3 ray=ProjectionDirection(camera,float2(id.xy)/float2(nx-1,ny-1));
    float2 shell,body;
    bool atmosphere=PlanetSphere(camera.Origin,ray,AtmosphereRadius+AtmosphereHeight(),shell);
    bool ground=PlanetSphere(camera.Origin,ray,AtmosphereRadius,body);
    float begin=atmosphere?max(0,shell.x):0, end=atmosphere?shell.y:0;
    if(ground) end=min(end,max(0,body.x));
    float3 a=0,t=1; float previous=0;
    for(uint k=0;k<nz;k++)
    {
        float w=k/(float)(nz-1);
        float distance=ProjectionRayDistance(camera,ray,camera.Far*w*w);
        float lo=max(previous,begin),hi=min(distance,end);
        if(hi>lo) { float3 ts; float3 segment=AtmosphereIntegrateTransport(camera.Origin+ray*lo,ray,hi-lo,4,ts); a+=t*segment; t*=ts; }
        AerialRadianceOutput[uint3(id.xy,k)]=float4(min(a,65504),1);
        AerialTransmissionOutput[uint3(id.xy,k)]=float4(t,1);
        previous=distance;
    }
}
uint PlanetHash(uint x) { x^=x>>16; x*=0x7feb352d; x^=x>>15; x*=0x846ca68b; return x^(x>>16); }
float PlanetRandom(uint x) { return (PlanetHash(x)&0xffffff)/16777216.0; }
struct MeteorVertex { float4 Position:SV_POSITION; };
MeteorVertex FroxelGeometryVS(uint vertex:SV_VertexID,uint instance:SV_InstanceID)
{
    uint seed=instance*11+17;
    float a=2*AtmospherePi*PlanetRandom(seed)+T*(.2*Ring.Speed);
    float b=2*AtmospherePi*PlanetRandom(seed+1);
    float baseRadius=lerp(1.02,3,Ring.Radius);
    float tube=min(.15*Ring.Width,.45*(baseRadius-1))*sqrt(PlanetRandom(seed+2));
    float radius=baseRadius+tube*cos(b);
    float3 center=float3(radius*cos(a),tube*sin(b),radius*sin(a));
    uint face=vertex/3,corner=vertex%3;
    float3 signs=float3((face&1)?1:-1,(face&2)?1:-1,(face&4)?1:-1);
    float3 local=corner==0?float3(signs.x,0,0):corner==1?float3(0,signs.y,0):float3(0,0,signs.z);
    float spin=2*AtmospherePi*PlanetRandom(seed+3)+T*(.2*Ring.Speed)*lerp(1,5,PlanetRandom(seed+4));
    local=mul(Ry(spin),mul(Rx(spin*.73),local));
    float size=min(lerp(.005,.04,Ring.Size),.45*(baseRadius-1))*lerp(.6,1,PlanetRandom(seed+5));
    float3 world=AtmosphereRadius*mul(Rz(.5*AtmospherePi*Ring.Inclination),center+size*local);
    MeteorVertex o; o.Position=ProjectionClip(PlanetDisplayCamera(),world); return o;
}
float4 FroxelSurfacePS(MeteorVertex input):SV_TARGET { return float4(0,0,0,1); }
float4 PS(PSin input):SV_TARGET
{
    if(ShowTransmittance) return DisplayOutput(AtmosphereTransmittance.SampleLevel(AtmosphereSampler,input.UV,0).rgb);
    if(ShowSkyView) return DisplayOutput(AtmosphereSkyView.SampleLevel(AtmosphereSampler,input.UV,0).rgb);
    uint nx,ny,nz; AerialRadiance.GetDimensions(nx,ny,nz);
    if(ShowAerial) {
        float3 uv=float3(input.UV,(.5+AerialSlice*(nz-1))/nz);
        if(ShowAerialTransmission) return DisplayOutput(AerialTransmission.SampleLevel(AtmosphereSampler,uv,0).rgb);
        return DisplayOutput(AerialRadiance.SampleLevel(AtmosphereSampler,uv,0).rgb);
    }
    ProjectionCamera camera=PlanetDisplayCamera(); float3 ray=ProjectionDirection(camera,input.UV);
    float2 surface; bool disk=PlanetSphere(camera.Origin,ray,AtmosphereRadius,surface);
    float depth=FroxelSurfaceDepth.Load(int3((int2)input.Position.xy,0));
    float endpoint=disk?max(0,surface.x)*dot(ray,camera.Forward):camera.Far;
    if(depth<1) endpoint=min(endpoint,ProjectionLinearDepth(camera,depth));
    // The existing disk LUT resolves the opaque limb more accurately than a coarse frustum grid.
    bool meteor=depth<1 && (!disk || ProjectionLinearDepth(camera,depth)<max(0,surface.x)*dot(ray,camera.Forward));
    if(meteor) {
        float3 unit=float3(input.UV,sqrt(saturate(endpoint/camera.Far)));
        float3 uv=(.5+unit*(float3(nx,ny,nz)-1))/float3(nx,ny,nz);
        return LinearOutput(AerialRadiance.SampleLevel(AtmosphereSampler,uv,0).rgb);
    }
    uint width,height; AtmosphereSkyView.GetDimensions(width,height);
    return LinearOutput(AtmosphereSkyView.SampleLevel(AtmosphereSampler,PlanetLutUV(ray,disk,width,height),0).rgb);
}
