#include "../Common/Inputs.hlsl"

// Audio-reactive prototype. Independent shadow toggles for transient and tonal
// rings. Homogeneous single scattering, hardware PCF, cached LUT, no history.
struct ShapeSettings { bool Enabled; float Count; float Seed; float Size; float Spread; float Speed; float3 Albedo; };
struct AirSettings { float Density; float3 Scattering; float3 Absorption; };
struct RingLightSettings { bool Enabled; float Count; float Gain; float Radius; float RingRadius; bool Directional; float HalfAngle; bool Shadows; };
struct TonalLightSettings { bool Enabled; float Gain; bool Directional; float HalfAngle; bool Shadows; };
struct QualitySettings { float Samples; float ShadowResolution; float ShadowBias; bool HardwarePCF; bool HalfResolutionAirlight; };
struct SamplingSettings { float OpticalDepthWeight; bool Jitter; bool RandomLight; };
struct FilterSettings { bool Enabled; };
struct ViewSettings { float CameraDistance; float CameraSpeed; float HorizontalOffset; float Exposure;
    bool ShowDepth; bool ShowShadows; float ShadowLight; };
cbuffer Parameters : register(b1)
{
    ShapeSettings Shape = { true, .03101343, .25, .45, .75, .1, float3(.65,.7,.75) };
    AirSettings Air = { .65, float3(1,1,1), float3(.03,.05,.08) };
    RingLightSettings Lights = { true, .62105263, .66666667, .25, .6, true, .33333333, false };
    TonalLightSettings TonalLights = { true, .66666667, false, .33333333, false };
    QualitySettings Quality = { .24409449, 1, .15, true, true };
    SamplingSettings Sampling = { .5, true, true };
    FilterSettings Filter = { true };
    ViewSettings View = { .55, .15, .5, .4, false, false, 0 };
};
cbuffer RasterPassConstants : register(b2) { uint RasterLight; uint RasterFace; uint RasterShadow; float RasterShadowScale; };
// Uploaded once per frame from the current stereo spectra, with no history or
// envelope. Matching host capacity is RasterLightTransport.MaximumLights.
static const int MaxLights=96;
cbuffer RasterLightConstants : register(b3)
{
    float4 RasterLightInfo; // strobe count/active count, tonal count/active count
    float4 RasterLightPositions[MaxLights]; // xyz position, squared soft radius
    float4 RasterLightRadiance[MaxLights]; // RGB intensity, scalar transient intensity
    float4 RasterActiveLights[MaxLights/4]; // packed indices of nonzero lights
    float4 RasterTonalPositions[MaxLights];
    float4 RasterTonalRadiance[MaxLights];
    float4 RasterActiveTonalLights[MaxLights/4];
    float4 RasterLightCones[MaxLights]; // xyz inward unit axis, w cosine; -1 = full sphere
    float4 RasterTonalCones[MaxLights];
    float4 RasterShadowRight[2*MaxLights]; // xyz spot right, w tan(half-angle); 1 for cubes
    float4 RasterShadowUp[2*MaxLights]; // xyz spot up, w first depth-array slice
};
cbuffer RasterAirlightConstants : register(b5)
{
    float4 RasterLookupInfo; // ready, maximum perpendicular distance, asinh warp, reserved
};
Texture2DArray<float> RasterShadows;
SamplerComparisonState RasterShadowSampler;
Texture2D<float4> RasterSurface;
Texture2D<float> RasterDepth;
Texture2D<float4> RasterAirlight;
Texture2D<float4> RasterAirlightLut;
SamplerState RasterLutSampler;
static const float Pi=3.14159265359;
static const float Near=.025, Far=128;
uint RasterHash(uint v) { v^=v>>16; v*=0x7feb352d; v^=v>>15; v*=0x846ca68b; return v^(v>>16); }
float Random(uint v) { return ((RasterHash(v)>>9)+.5)/8388608.0; }
int ActiveStrobeLight(int i) { return (int)RasterActiveLights[i/4][i%4]; }
int ActiveTonalLight(int i) { return (int)RasterActiveTonalLights[i/4][i%4]; }
int ShadowCount() { return (Lights.Shadows ? (int)RasterLightInfo.y : 0)+(TonalLights.Shadows ? (int)RasterLightInfo.w : 0); }
int UnshadowedCount() { return (!Lights.Shadows ? (int)RasterLightInfo.y : 0)+(!TonalLights.Shadows ? (int)RasterLightInfo.w : 0); }
int ActiveLight(int i)
{
    int strobes=Lights.Shadows ? (int)RasterLightInfo.y : 0;
    return i<strobes ? ActiveStrobeLight(i) : MaxLights+ActiveTonalLight(i-strobes);
}
int ActiveUnshadowedLight(int i)
{
    int strobes=!Lights.Shadows ? (int)RasterLightInfo.y : 0;
    return i<strobes ? ActiveStrobeLight(i) : MaxLights+ActiveTonalLight(i-strobes);
}
float4 LightData(int i) { return i<MaxLights ? RasterLightPositions[i] : RasterTonalPositions[i-MaxLights]; }
float4 LightRadiance(int i) { return i<MaxLights ? RasterLightRadiance[i] : RasterTonalRadiance[i-MaxLights]; }
float4 LightCone(int i) { return i<MaxLights ? RasterLightCones[i] : RasterTonalCones[i-MaxLights]; }
float3 LightPosition(int i) { return LightData(i).xyz; }
int ShadowSlice(int i) { return (int)RasterShadowUp[i].w; }
int PreviewMapCount()
{
    int count=(Lights.Shadows ? (int)RasterLightInfo.x : 0)+(TonalLights.Shadows ? (int)RasterLightInfo.z : 0);
    return max(1,count>0 ? count : (int)RasterLightInfo.x);
}
bool InsideCone(float3 offset, float4 cone)
{
    bool inside=true;
    [branch] if (cone.w>=0) inside=dot(offset,cone.xyz)>=cone.w*length(offset);
    return inside;
}
// Intersect the forward nappe of a convex cone with an existing camera interval.
// The caller uses 1e30 for infinity. No cone math is needed for a full sphere.
bool ClipSpotCone(float3 offset, float3 direction, float4 cone, inout float begin, inout float end)
{
    float z=dot(offset,cone.xyz),v=dot(direction,cone.xyz);
    // Squaring the cone equation introduces the backward nappe: remove it first.
    if (v==0) { if (z<0) return false; }
    else if (v>0) begin=max(begin,-z/v);
    else end=min(end,-z/v);
    if (end<=begin) return false;
    if (cone.w==0) return true; // 90-degree half-angle is a half-space.
    float k=cone.w*cone.w;
    float a=v*v-k, b=z*v-k*dot(offset,direction),c=z*z-k*dot(offset,offset);
    if (a==0)
    {
        if (b==0) return c>=0;
        if (b>0) begin=max(begin,-c/(2*b)); else end=min(end,-c/(2*b));
    }
    else
    {
        float discriminant=b*b-a*c;
        if (discriminant<0) return a>0;
        // Stable quadratic roots avoid cancellation for rays near the boundary.
        float q=-b-(b>=0 ? 1 : -1)*sqrt(discriminant);
        float r0=q/a,r1=q!=0 ? c/q : -b/a;
        float lo=min(r0,r1),hi=max(r0,r1);
        if (a<0) { begin=max(begin,lo); end=min(end,hi); }
        else if (v>0) begin=max(begin,hi);
        else end=min(end,lo);
    }
    return end>begin;
}
bool ClipCone(float3 offset, float3 direction, float4 cone, inout float begin, inout float end)
{
    bool intersects=end>begin;
    [branch] if (cone.w>=0) intersects=ClipSpotCone(offset,direction,cone,begin,end);
    return intersects;
}
void CameraFrame(out float3 origin, out float3 forward, out float3 right, out float3 up)
{
    float s,c; sincos(T*.25*View.CameraSpeed,s,c);
    forward=normalize(float3(-9*c-13.5*s,-6,13.5*c-9*s));
    right=normalize(cross(forward,float3(0,1,0))); up=cross(right,forward);
    float distance=40*View.CameraDistance;
    origin=-distance*forward+(2*View.HorizontalOffset-1)*distance*right;
}
float3 CubeDirection(int face, float2 uv)
{
    float2 q=2*uv-1;
    if (face==0) return float3(1,-q.y,-q.x);
    if (face==1) return float3(-1,-q.y,q.x);
    if (face==2) return float3(q.x,1,q.y);
    if (face==3) return float3(q.x,-1,-q.y);
    if (face==4) return float3(q.x,-q.y,1);
    return float3(-q.x,-q.y,-1);
}
void CubeAddress(float3 w, out int face, out float2 uv)
{
    float3 a=abs(w);
    if (a.x>=a.y && a.x>=a.z) { face=w.x>=0 ? 0 : 1; uv=float2(w.x>=0 ? -w.z : w.z,-w.y)/a.x; }
    else if (a.y>=a.z) { face=w.y>=0 ? 2 : 3; uv=float2(w.x,w.y>=0 ? w.z : -w.z)/a.y; }
    else { face=w.z>=0 ? 4 : 5; uv=float2(w.z>=0 ? w.x : -w.x,-w.y)/a.z; }
    uv=.5+.5*uv;
}
float3 FacePosition(float3 p, int face)
{
    if (face==0) return float3(-p.z,p.y,p.x);
    if (face==1) return float3(p.z,p.y,-p.x);
    if (face==2) return float3(p.x,-p.z,p.y);
    if (face==3) return float3(p.x,p.z,-p.y);
    if (face==4) return p;
    return float3(-p.x,p.y,-p.z);
}
float3 ShadowPosition(float3 p, int light, int face)
{
    float3 local=0;
    if (LightCone(light).w<0) local=FacePosition(p,face);
    else local=float3(dot(p,RasterShadowRight[light].xyz),dot(p,RasterShadowUp[light].xyz),dot(p,LightCone(light).xyz));
    return local;
}
float3 ShadowDirection(int light, int face, float2 uv)
{
    float3 ray=0;
    if (LightCone(light).w<0) ray=CubeDirection(face,uv);
    else
    {
        float2 q=(2*uv-1)*RasterShadowRight[light].w;
        ray=LightCone(light).xyz+q.x*RasterShadowRight[light].xyz-q.y*RasterShadowUp[light].xyz;
    }
    return ray;
}
float ProjectDepth(float z) { return (Far*z-Near*Far)/(Far-Near); }
float LinearDepth(float z) { return Near*Far/(Far-z*(Far-Near)); }
float ShadowBias() { return .0005+.02*Quality.ShadowBias; }
float Visibility(int light, float3 p, float3 normal)
{
    float3 delta=p-LightPosition(light);
    bool spot=LightCone(light).w>=0;
    int face=0; float2 uv; float z;
    float tangent=RasterShadowRight[light].w;
    if (spot)
    {
        float3 local=ShadowPosition(delta,light,0);
        if (local.z<=0) return 0;
        if (local.z<Near) return 1;
        z=local.z;
        uv=.5+.5*float2(local.x,-local.y)/(z*tangent);
    }
    else
    {
        z=max(abs(delta.x),max(abs(delta.y),abs(delta.z)));
        if (z<Near) return 1;
        CubeAddress(delta,face,uv);
    }
    int slice=ShadowSlice(light)+face;
    uint w,h,layers; RasterShadows.GetDimensions(w,h,layers);
    // Both projection types render a one-texel guard on every side, preserving
    // the complete hardware 2x2 footprint at cone boundaries and cube seams.
    float scale=(w-2.0)/w;
    uv=.5+(uv-.5)*scale;
    if (Quality.HardwarePCF)
    {
        float biased=max(Near,z-ShadowBias());
        float comparison=ProjectDepth(biased)/biased;
        if (any(normal!=0))
        {
            // Projected receiver-plane depth is affine in face UV. A hardware
            // comparison uses one reference for four texels, so use its minimum
            // over those centers to prevent slope acne with a tight depth bias.
            float plane=dot(delta,normal);
            if (abs(plane)>1e-5)
            {
                float3 n=ShadowPosition(normal,light,face);
                float2 gradient=(2*Near*Far/(Far-Near))*tangent*float2(-n.x,n.y)/(plane*scale*w);
                float2 f=frac(uv*w-.5);
                comparison+=min(-f.x*gradient.x,(1-f.x)*gradient.x)
                    +min(-f.y*gradient.y,(1-f.y)*gradient.y);
            }
        }
        return RasterShadows.SampleCmpLevelZero(RasterShadowSampler,float3(uv,slice),saturate(comparison));
    }
    int2 texel=clamp((int2)(uv*w),0,(int)w-1);
    float depth=RasterShadows.Load(int4(texel,slice,0));
    if (depth>=1) return 1;
    // Compare against the receiver plane at the actual texel-center ray. This
    // removes sloped-face acne without filtering or a large silhouette offset.
    if (any(normal!=0))
    {
        float3 ray=ShadowDirection(light,face,.5+((texel+.5)/w-.5)/scale);
        float denominator=dot(ray,normal);
        if (abs(denominator)>1e-5)
        {
            float plane=dot(delta,normal)/denominator;
            if (plane>0) z=plane;
        }
    }
    return z<=LinearDepth(depth)+ShadowBias() ? 1 : 0;
}
float3 AirScatter() { return .3*Air.Density*Air.Density*Air.Scattering; }
float3 AirExtinction() { return .3*Air.Density*Air.Density*(Air.Scattering+Air.Absorption); }
struct GeometryOutput
{
    float4 Position : SV_POSITION;
    float3 World : TEXCOORD0;
    nointerpolation float3 Normal : TEXCOORD1;
    nointerpolation float3 Albedo : TEXCOORD2;
};
GeometryOutput RasterGeometryVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    int face=vertex/6, corner=vertex%6;
    float2 uv=corner==0 ? float2(0,0) : corner==1 ? float2(0,1) : corner==2 ? float2(1,0)
        : corner==3 ? float2(1,0) : corner==4 ? float2(0,1) : float2(1,1);
    uint seed=(uint)round(65535*Shape.Seed);
    uint id=RasterHash(seed^((instance+1)*0x9e3779b9));
    float3 center=(float3(Random(id),Random(id+1),Random(id+2))*2-1)*(2+10*Shape.Spread);
    float halfSize=(.2+1.3*Shape.Size)*lerp(.7,1.3,Random(id+3));
    float3 local=CubeDirection(face,uv)*halfSize;
    float3 normal=normalize(CubeDirection(face,.5));
    float angle=2*Pi*Random(id+4)+T*.2*Shape.Speed;
    local=mul(Ry(angle),local); normal=mul(Ry(angle),normal);
    local=mul(Rx(.5*Random(id+5)),local); normal=mul(Rx(.5*Random(id+5)),normal);
    GeometryOutput result;
    result.World=center+local; result.Normal=normal;
    result.Albedo=Shape.Albedo*lerp(.65,1,Random(id+6));
    float3 p;
    if (RasterShadow!=0)
    {
        p=ShadowPosition(result.World-LightPosition(RasterLight),RasterLight,RasterFace);
        p.xy*=RasterShadowScale/RasterShadowRight[RasterLight].w;
    }
    else
    {
        float3 origin,forward,right,up; CameraFrame(origin,forward,right,up);
        float3 v=result.World-origin;
        p=float3(2*dot(v,right)/max(1,AspectRatio),2*dot(v,up)/max(1,1/AspectRatio),dot(v,forward));
    }
    result.Position=float4(p.xy,ProjectDepth(p.z),p.z);
    return result;
}
float4 RasterSurfacePS(GeometryOutput input) : SV_Target
{
    float3 radiance=0,extinction=AirExtinction();
    [loop] for (int slot=0;slot<ShadowCount();++slot)
    {
        int i=ActiveLight(slot);
        float3 delta=LightPosition(i)-input.World;
        float r2=dot(delta,delta),r=sqrt(r2); if (r<1e-6) continue;
        if (!InsideCone(-delta,LightCone(i))) continue;
        float diffuse=max(0,dot(input.Normal,delta/r))/Pi;
        if (diffuse<=0) continue;
        float shadow=Visibility(i,input.World,input.Normal);
        radiance+=input.Albedo*diffuse*shadow*exp(-extinction*r)
            *LightRadiance(i).rgb/(r2+LightData(i).w);
    }
    // All unshadowed surface lights share the existing geometry draw.
    [loop] for (int tonalSlot=0;tonalSlot<UnshadowedCount();++tonalSlot)
    {
        int i=ActiveUnshadowedLight(tonalSlot);
        float3 delta=LightPosition(i)-input.World;
        float r2=dot(delta,delta),r=sqrt(r2); if (r<1e-6) continue;
        if (!InsideCone(-delta,LightCone(i))) continue;
        float diffuse=max(0,dot(input.Normal,delta/r))/Pi;
        radiance+=input.Albedo*diffuse*exp(-extinction*r)
            *LightRadiance(i).rgb/(r2+LightData(i).w);
    }
    return float4(radiance,1);
}
float OneMinusExp(float x) { return x<.001 ? x*(1-x*.5+x*x/6) : 1-exp(-x); }
float4 Display(float3 radiance)
{
    float3 mapped=1-exp(-max(0,radiance)*exp2(6*(View.Exposure-.5)));
    return float4(lerp(12.92*mapped,1.055*pow(mapped,1/2.4)-.055,step(.0031308,mapped)),1);
}
struct Source
{
    float3 Intensity; float Radius2;
    float Center; float Perpendicular2; float Height; float Angle; float Span; float PdfScale;
    float Begin; float End;
};
Source DescribeLight(int i, float3 origin, float3 direction, float begin, float end)
{
    Source source;
    source.Intensity=LightRadiance(i).rgb;
    source.Radius2=LightData(i).w;
    float3 delta=LightPosition(i)-origin;
    source.Center=dot(delta,direction);
    float3 perpendicular=delta-source.Center*direction;
    source.Perpendicular2=dot(perpendicular,perpendicular);
    source.Height=sqrt(source.Perpendicular2+source.Radius2);
    source.Begin=begin; source.End=end;
    float x=begin-source.Center,y=end-source.Center;
    source.Angle=atan2(x,source.Height);
    source.Span=end<1e30 ? atan2(source.Height*(end-begin),source.Height*source.Height+x*y)
        : atan2(source.Height,x);
    source.PdfScale=source.Height/max(source.Span,1e-12);
    return source;
}
float SampleDistance(Source source, float u, float optical, float rate, float mass)
{
    if (optical>0 && u<optical)
    {
        float x=min(u/optical,1-1e-6)*mass;
        // Stable -log(1-x) for optically thin clipped intervals.
        float distance=x<.001 ? x*(1+x*(.5+x/3)) : -log(max(1e-7,1-x));
        return clamp(source.Begin+distance/rate,source.Begin,source.End);
    }
    float v=min((u-optical)/max(1-optical,1e-20),1-1e-6);
    return clamp(source.Center+source.Height*tan(source.Angle+v*source.Span),source.Begin,source.End);
}
float OpticalMass(Source source, float rate)
{
    return source.End<1e30 ? OneMinusExp(rate*(source.End-source.Begin)) : 1;
}
float DistancePdf(Source source, float t, float optical, float rate, float mass)
{
    if (t<source.Begin || t>source.End) return 0;
    float x=t-source.Center;
    return optical*rate*exp(-rate*(t-source.Begin))/max(mass,1e-30)
        +(1-optical)*source.PdfScale/(x*x+source.Height*source.Height);
}
float3 SourceContribution(int i, Source source, float t, float3 p, float3 extinction, out float falloff)
{
    float along=t-source.Center;
    float r2=along*along+source.Perpendicular2;
    falloff=rcp(r2+source.Radius2);
    if (r2<1e-12 || !InsideCone(p-LightPosition(i),LightCone(i))) return 0;
    float phase=3*(2-source.Perpendicular2/r2)/(16*Pi);
    return source.Intensity*phase*Visibility(i,p,0)*exp(-extinction*(t+sqrt(r2)))*falloff;
}

float3 TonalTail(float c, float b2, float height, float t, float q, float4 layout, float3 extinction)
{
    float angle=atan((t-c)/height);
    float v=.5+.5*sign(angle)*(1-sqrt(max(0,1-2*abs(angle)/Pi)));
    float2 uv=float2(q,v)*layout.xy+layout.zw;
    float3 tail=RasterAirlightLut.SampleLevel(RasterLutSampler,uv,0).rgb;
    float x=t-c;
    return tail*exp(-extinction*(t+sqrt(x*x+b2)));
}

float3 UnshadowedAirlight(float3 origin, float3 direction, bool hit, float end, float3 scatter, float3 extinction)
{
    if (UnshadowedCount()==0 || !any(scatter>0)) return 0;
    uint width,height; RasterAirlightLut.GetDimensions(width,height);
    float4 layout=float4((width-1.0)/width,(height-1.0)/height,.5/width,.5/height);
    float3 radiance=0;
    // One batched light loop: camera ray/depth and the output write are shared
    // with the shadowed path. No per-light draw, shadow lookup or distance march.
    [loop] for (int slot=0;slot<UnshadowedCount();++slot)
    {
        int i=ActiveUnshadowedLight(slot);
        float3 delta=LightPosition(i)-origin;
        float begin=0,stop=hit ? end : 1e30;
        if (!ClipCone(-delta,direction,LightCone(i),begin,stop)) continue;
        float c=dot(delta,direction);
        float3 perpendicular=delta-c*direction;
        float b2=dot(perpendicular,perpendicular),b=sqrt(b2),h=sqrt(LightData(i).w);
        float H=sqrt(b2+h*h);
        float3 integral=0;
        [branch] if (RasterLookupInfo.x>0 && b<=RasterLookupInfo.y)
        {
            float q=log(b/h+sqrt(1+b2/(h*h)))/RasterLookupInfo.z;
            integral=TonalTail(c,b2,H,begin,q,layout,extinction);
            if (stop<1e30) integral=max(0,integral-TonalTail(c,b2,H,stop,q,layout,extinction));
        }
        else
        {
            // A coefficient edit never uses a stale table. While its background
            // build is pending, evaluate the same integral by angular quadrature.
            float a=atan((begin-c)/H),span=(stop<1e30 ? atan((stop-c)/H) : .5*Pi)-a;
            [loop] for (int j=0;j<16;++j)
            {
                float t=clamp(c+H*tan(a+(j+.5)*span/16),begin,stop);
                float x=t-c,r2=x*x+b2;
                if (r2<1e-12) continue;
                float phase=3*(2-b2/r2)/(16*Pi);
                integral+=phase*exp(-extinction*(t+sqrt(r2)));
            }
            integral*=span/(16*H);
        }
        radiance+=scatter*LightRadiance(i).rgb*integral;
    }
    return radiance;
}
float4 IntegrateAirlight(int2 pixel)
{
    float3 origin,forward,right,up; CameraFrame(origin,forward,right,up);
    float2 screen=(2*(pixel+.5)/Resolution-1)*float2(max(1,AspectRatio),-max(1,1/AspectRatio));
    float3 direction=normalize(2*forward+screen.x*right+screen.y*up);
    float z=RasterDepth.Load(int3(pixel,0)); bool hit=z<1;
    float end=hit ? LinearDepth(z)/dot(direction,forward) : 0;
    float guide=hit ? LinearDepth(z) : 0;
    float3 scatter=AirScatter(),extinction=AirExtinction();
    float3 radiance=UnshadowedAirlight(origin,direction,hit,end,scatter,extinction);
    int count=ShadowCount();
    if (count==0 || !any(scatter>0)) return float4(radiance,guide);
    float rate=min(scatter.x>0 ? extinction.x : 1e20,min(scatter.y>0 ? extinction.y : 1e20,scatter.z>0 ? extinction.z : 1e20));
    rate=max(rate,1e-12);
    // Compact the lights whose cones intersect this visible ray. Both distance
    // proposals and their PDFs use exactly the same per-light clipped interval.
    int rayLights[2*MaxLights]; float2 rayBounds[2*MaxLights];
    int rayCount=0;
    [loop] for (int slot=0;slot<count;++slot)
    {
        int i=ActiveLight(slot);
        float begin=0,stop=hit ? end : 1e30;
        if (!ClipCone(origin-LightPosition(i),direction,LightCone(i),begin,stop)) continue;
        rayLights[rayCount]=i; rayBounds[rayCount]=float2(begin,stop); ++rayCount;
    }
    if (rayCount==0) return float4(radiance,guide);
    int samples=1+(int)round(127*Quality.Samples);
    float optical=saturate(Sampling.OpticalDepthWeight);
    uint seed=RasterHash((uint)pixel.x^RasterHash((uint)pixel.y+1));
    [branch] if (Sampling.RandomLight)
    {
        int offset=Sampling.Jitter ? min((int)(Random(seed^0xa511e9b3)*rayCount),rayCount-1) : 0;
        [loop] for (int s=0;s<samples;++s)
        {
            int slot=(s+offset)%rayCount,i=rayLights[slot];
            Source source=DescribeLight(i,origin,direction,rayBounds[slot].x,rayBounds[slot].y);
            float mass=OpticalMass(source,rate);
            float jitter=Sampling.Jitter ? Random(seed^RasterHash((uint)s+1)) : .5;
            // Stratify distance separately within each light. When every light
            // is visited, average its own samples; otherwise compensate for the
            // uniformly selected subset of ray-intersecting lights.
            int visits=1+(samples-1-s%rayCount)/rayCount;
            float u=min((s/rayCount+jitter)/visits,1-1e-6);
            float weight=samples>=rayCount ? rcp((float)visits) : (float)rayCount/samples;
            float t=SampleDistance(source,u,optical,rate,mass);
            float falloff;
            float3 incoming=SourceContribution(i,source,t,origin+t*direction,extinction,falloff);
            float pdf=DistancePdf(source,t,optical,rate,mass);
            radiance+=scatter*incoming*(weight/max(pdf,1e-30));
        }
        return float4(radiance,guide);
    }
    // All-light estimator: draw from the uniform mixture of each light's
    // conditional distance PDF. Every term is zero outside its own interval.
    [loop] for (int s=0;s<samples;++s)
    {
        float jitter=Sampling.Jitter ? Random(seed^RasterHash((uint)s+1)) : .5;
        float choice=min((s+jitter)/samples,1-1e-6)*rayCount;
        int pick=min((int)choice,rayCount-1);
        Source selected=DescribeLight(rayLights[pick],origin,direction,rayBounds[pick].x,rayBounds[pick].y);
        float t=SampleDistance(selected,frac(choice),optical,rate,OpticalMass(selected,rate));
        float pdf=0;
        float3 p=origin+t*direction,incoming=0;
        [loop] for (int slot=0;slot<rayCount;++slot)
        {
            if (t<rayBounds[slot].x || t>rayBounds[slot].y) continue;
            int i=rayLights[slot];
            Source source=DescribeLight(i,origin,direction,rayBounds[slot].x,rayBounds[slot].y);
            float falloff;
            incoming+=SourceContribution(i,source,t,p,extinction,falloff);
            pdf+=DistancePdf(source,t,optical,rate,OpticalMass(source,rate));
        }
        radiance+=scatter*incoming*(rayCount/(max(pdf,1e-30)*samples));
    }
    return float4(radiance,guide);
}
float4 RasterAirlightPS(PSin input) : SV_Target
{
    int2 pixel=(int2)input.Position.xy;
    // One representative full-resolution ray per 2x2 block. Sampling the actual
    // full-resolution pixel center keeps its guide and integral endpoint aligned.
    if (Quality.HalfResolutionAirlight) pixel=min(2*pixel+1,(int2)Resolution-1);
    return IntegrateAirlight(pixel);
}

float3 ReconstructAirlight(int2 pixel)
{
    uint width,height; RasterAirlight.GetDimensions(width,height);
    float z=RasterDepth.Load(int3(pixel,0));
    float guide=z<1 ? LinearDepth(z) : 0;
    float2 grid=(pixel-1)*.5;
    // With an odd viewport the last representative is clamped to the final
    // full-resolution pixel, so its final lattice interval is one pixel wide.
    if (((int)Resolution.x&1)!=0 && pixel.x>=Resolution.x-2) grid.x=pixel.x+(int)width-(int)Resolution.x;
    if (((int)Resolution.y&1)!=0 && pixel.y>=Resolution.y-2) grid.y=pixel.y+(int)height-(int)Resolution.y;
    int2 base=(int2)floor(grid); float2 f=frac(grid);
    float3 sum=0; float weight=0;
    [unroll] for (int y=0;y<2;++y) [unroll] for (int x=0;x<2;++x)
    {
        int2 at=clamp(base+int2(x,y),0,int2(width,height)-1);
        float4 value=RasterAirlight.Load(int3(at,0));
        if ((guide==0)!=(value.a==0)) continue;
        float difference=abs(value.a-guide)/max(.05,.02*guide);
        float spatial=(x==0 ? 1-f.x : f.x)*(y==0 ? 1-f.y : f.y);
        float w=spatial*exp2(-2*difference*difference);
        sum+=w*value.rgb; weight+=w;
    }
    // Thin/disoccluded geometry can have no matching coarse ray. Integrate that
    // pixel instead of leaking background fog or leaving a dark reconstruction hole.
    [branch] if (weight<1e-5) return IntegrateAirlight(pixel).rgb;
    return sum/weight;
}

float3 FilterAirlight(int2 pixel)
{
    [branch] if (Quality.HalfResolutionAirlight) return ReconstructAirlight(pixel);
    float4 center=RasterAirlight.Load(int3(pixel,0));
    if (!Filter.Enabled) return center.rgb;
    float3 sum=0; float weight=0;
    [unroll] for (int y=-1;y<=1;++y) [unroll] for (int x=-1;x<=1;++x)
    {
        int2 at=clamp(pixel+int2(x,y),0,(int2)Resolution-1);
        float4 value=RasterAirlight.Load(int3(at,0));
        if ((center.a==0)!=(value.a==0)) continue;
        float difference=abs(value.a-center.a)/max(.05,.02*center.a);
        float spatial=(x==0 ? 2 : 1)*(y==0 ? 2 : 1);
        float w=spatial*exp2(-2*difference*difference);
        sum+=w*value.rgb; weight+=w;
    }
    return sum/max(1e-8,weight);
}
float4 PS(PSin input) : SV_Target
{
    int2 pixel=(int2)input.Position.xy;
    if (View.ShowShadows)
    {
        int slot=(int)round((PreviewMapCount()-1)*View.ShadowLight);
        bool strobes=Lights.Shadows || !TonalLights.Shadows;
        int light=strobes && slot<(int)RasterLightInfo.x ? slot
            : MaxLights+slot-(strobes ? (int)RasterLightInfo.x : 0);
        bool spot=LightCone(light).w>=0;
        float2 tile=input.UV*(spot ? float2(1,1) : float2(3,2)); int2 cell=(int2)tile;
        uint w,h,layers; RasterShadows.GetDimensions(w,h,layers);
        float2 uv=.5+(frac(tile)-.5)*((w-2.0)/w);
        float z=RasterShadows.Load(int4(clamp((int2)(uv*w),0,(int)w-1),
            ShadowSlice(light)+(spot ? 0 : cell.x+3*cell.y),0));
        return float4((z>=1 ? 0 : exp(-.06*LinearDepth(z))).xxx,1);
    }
    float3 origin,forward,right,up; CameraFrame(origin,forward,right,up);
    float2 screen=(2*input.UV-1)*float2(max(1,AspectRatio),-max(1,1/AspectRatio));
    float3 direction=normalize(2*forward+screen.x*right+screen.y*up);
    float z=RasterDepth.Load(int3(pixel,0)); bool hit=z<1;
    float end=hit ? LinearDepth(z)/dot(direction,forward) : 0;
    if (View.ShowDepth) return float4((hit ? exp(-.04*end) : 0).xxx,1);
    float3 surface=hit ? RasterSurface.Load(int3(pixel,0)).rgb*exp(-AirExtinction()*end) : 0;
    float3 air=any(AirScatter()>0) ? FilterAirlight(pixel) : 0;
    return Display(surface+air);
}
