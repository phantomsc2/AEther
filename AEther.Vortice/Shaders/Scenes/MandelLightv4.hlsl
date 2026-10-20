#include "../Common/Inputs.hlsl"

// Audio-reactive prototype. Opaque cubes and a static frequency/stereo light ring
// in infinite homogeneous air. Single scattering, hardware PCF, no image history.
struct ShapeSettings { bool Enabled; float Count; float Seed; float Size; float Spread; float Speed; float3 Albedo; };
struct AirSettings { float Density; float3 Scattering; float3 Absorption; };
struct RingLightSettings { bool Enabled; float Count; float Gain; float Radius; float RingRadius; };
struct QualitySettings { float Samples; float ShadowResolution; float ShadowBias; bool HardwarePCF; bool HalfResolutionAirlight; };
struct SamplingSettings { float OpticalDepthWeight; bool Jitter; bool RandomLight; };
struct FilterSettings { bool Enabled; };
struct ViewSettings { float CameraDistance; float CameraSpeed; float HorizontalOffset; float Exposure;
    bool ShowDepth; bool ShowShadows; float ShadowLight; };
cbuffer Parameters : register(b1)
{
    ShapeSettings Shape = { true, .03101343, .25, .45, .75, .1, float3(.65,.7,.75) };
    AirSettings Air = { .65, float3(1,1,1), float3(.03,.05,.08) };
    RingLightSettings Lights = { true, .61290323, .66666667, .25, .6 };
    QualitySettings Quality = { .24409449, 1, .15, true, true };
    SamplingSettings Sampling = { .5, true, true };
    FilterSettings Filter = { true };
    ViewSettings View = { .55, .15, .5, .4, false, false, 0 };
};
cbuffer RasterPassConstants : register(b2) { uint RasterLight; uint RasterFace; uint RasterShadow; float RasterShadowScale; };
// Uploaded once per frame from the current stereo spectra, with no history or
// envelope. Matching host capacity is RasterLightTransport.MaximumLights.
static const int MaxLights=32;
cbuffer RasterLightConstants : register(b3)
{
    float4 RasterLightInfo; // total count, nonzero count, reserved, reserved
    float4 RasterLightPositions[32]; // xyz position, squared soft radius
    float4 RasterLightRadiance[32]; // RGB intensity, scalar transient intensity
    float4 RasterActiveLights[8]; // packed indices of nonzero lights
};
Texture2DArray<float> RasterShadows;
SamplerComparisonState RasterShadowSampler;
Texture2D<float4> RasterSurface;
Texture2D<float> RasterDepth;
Texture2D<float4> RasterAirlight;
static const float Pi=3.14159265359;
static const float Near=.025, Far=128;
uint RasterHash(uint v) { v^=v>>16; v*=0x7feb352d; v^=v>>15; v*=0x846ca68b; return v^(v>>16); }
float Random(uint v) { return ((RasterHash(v)>>9)+.5)/8388608.0; }
int ActiveLight(int i) { return (int)RasterActiveLights[i/4][i%4]; }
float3 LightPosition(int i) { return RasterLightPositions[i].xyz; }
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
float ProjectDepth(float z) { return (Far*z-Near*Far)/(Far-Near); }
float LinearDepth(float z) { return Near*Far/(Far-z*(Far-Near)); }
float ShadowBias() { return .0005+.02*Quality.ShadowBias; }
float Visibility(int light, float3 p, float3 normal)
{
    float3 delta=p-LightPosition(light);
    if (max(abs(delta.x),max(abs(delta.y),abs(delta.z)))<Near) return 1;
    int face; float2 uv; CubeAddress(delta,face,uv);
    uint w,h,layers; RasterShadows.GetDimensions(w,h,layers);
    // Each 90-degree face has a one-texel rendered guard on every side. Even
    // at cube edges/corners, the full hardware 2x2 footprint contains geometry.
    float scale=(w-2.0)/w;
    uv=.5+(uv-.5)*scale;
    float z=max(abs(delta.x),max(abs(delta.y),abs(delta.z)));
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
                float3 n=FacePosition(normal,face);
                float2 gradient=(2*Near*Far/(Far-Near))*float2(-n.x,n.y)/(plane*scale*w);
                float2 f=frac(uv*w-.5);
                comparison+=min(-f.x*gradient.x,(1-f.x)*gradient.x)
                    +min(-f.y*gradient.y,(1-f.y)*gradient.y);
            }
        }
        return RasterShadows.SampleCmpLevelZero(RasterShadowSampler,float3(uv,6*light+face),saturate(comparison));
    }
    int2 texel=clamp((int2)(uv*w),0,(int)w-1);
    float depth=RasterShadows.Load(int4(texel,6*light+face,0));
    if (depth>=1) return 1;
    // Compare against the receiver plane at the actual texel-center ray. This
    // removes sloped-face acne without filtering or a large silhouette offset.
    if (any(normal!=0))
    {
        float3 ray=CubeDirection(face,.5+((texel+.5)/w-.5)/scale);
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
        p=FacePosition(result.World-LightPosition(RasterLight),RasterFace);
        p.xy*=RasterShadowScale;
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
    [loop] for (int slot=0;slot<(int)RasterLightInfo.y;++slot)
    {
        int i=ActiveLight(slot);
        float3 delta=LightPosition(i)-input.World;
        float r2=dot(delta,delta),r=sqrt(r2); if (r<1e-6) continue;
        float diffuse=max(0,dot(input.Normal,delta/r))/Pi;
        if (diffuse<=0) continue;
        float shadow=Visibility(i,input.World,input.Normal);
        radiance+=input.Albedo*diffuse*shadow*exp(-extinction*r)
            *RasterLightRadiance[i].rgb/(r2+RasterLightPositions[i].w);
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
};
Source DescribeLight(int i, float3 origin, float3 direction, bool hit, float end)
{
    Source source;
    source.Intensity=RasterLightRadiance[i].rgb;
    source.Radius2=RasterLightPositions[i].w;
    float3 delta=LightPosition(i)-origin;
    source.Center=dot(delta,direction);
    float3 perpendicular=delta-source.Center*direction;
    source.Perpendicular2=dot(perpendicular,perpendicular);
    source.Height=sqrt(source.Perpendicular2+source.Radius2);
    source.Angle=atan(-source.Center/source.Height);
    source.Span=(hit ? atan((end-source.Center)/source.Height) : .5*Pi)-source.Angle;
    source.PdfScale=source.Height/max(source.Span,1e-12);
    return source;
}
float SampleDistance(Source source, float u, float optical, float rate, float mass)
{
    if (optical>0 && u<optical)
        return -log(max(1e-7,1-min(u/optical,1-1e-6)*mass))/rate;
    float v=min((u-optical)/max(1-optical,1e-20),1-1e-6);
    return source.Center+source.Height*tan(source.Angle+v*source.Span);
}
float3 SourceContribution(int i, Source source, float t, float3 p, float3 extinction, out float falloff)
{
    float along=t-source.Center;
    float r2=along*along+source.Perpendicular2;
    falloff=rcp(r2+source.Radius2);
    if (r2<1e-12) return 0;
    float phase=3*(2-source.Perpendicular2/r2)/(16*Pi);
    return source.Intensity*phase*Visibility(i,p,0)*exp(-extinction*(t+sqrt(r2)))*falloff;
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
    float3 radiance=0;
    int count=(int)RasterLightInfo.y;
    if (count==0 || !any(scatter>0)) return float4(0,0,0,guide);
    float rate=min(scatter.x>0 ? extinction.x : 1e20,min(scatter.y>0 ? extinction.y : 1e20,scatter.z>0 ? extinction.z : 1e20));
    rate=max(rate,1e-12);
    float mass=hit ? OneMinusExp(rate*end) : 1;
    if (mass<=0) return float4(0,0,0,guide);
    int samples=1+(int)round(127*Quality.Samples);
    float optical=saturate(Sampling.OpticalDepthWeight), angular=(1-optical)/count;
    uint seed=RasterHash((uint)pixel.x^RasterHash((uint)pixel.y+1));
    [branch] if (Sampling.RandomLight)
    {
        int offset=Sampling.Jitter ? min((int)(Random(seed^0xa511e9b3)*count),count-1) : 0;
        [loop] for (int s=0;s<samples;++s)
        {
            int i=ActiveLight((s+offset)%count);
            // Build only the selected light's proposal. A 20-light ring does not
            // require 20 angular proposals or a large Source array per pixel.
            Source source=DescribeLight(i,origin,direction,hit,end);
            float jitter=Sampling.Jitter ? Random(seed^RasterHash((uint)s+1)) : .5;
            float u=min((s+jitter)/samples,1-1e-6);
            float t=max(0,SampleDistance(source,u,optical,rate,mass));
            if (hit) t=min(t,end);
            float falloff;
            float3 incoming=SourceContribution(i,source,t,origin+t*direction,extinction,falloff);
            float pdf=optical*rate*exp(-rate*t)/mass+(1-optical)*source.PdfScale*falloff;
            // Uniform selection among nonzero lights, compensated in the joint PDF.
            radiance+=scatter*incoming*(count/(max(pdf,1e-30)*samples));
        }
        return float4(radiance,guide);
    }
    // The optional all-light reference path stores only one PDF scalar per
    // active light. Full Source arrays would scale register pressure with count.
    float pdfScales[MaxLights];
    [loop] for (int a=0;a<count;++a)
        pdfScales[a]=DescribeLight(ActiveLight(a),origin,direction,hit,end).PdfScale;
    [loop] for (int s=0;s<samples;++s)
    {
        float jitter=Sampling.Jitter ? Random(seed^RasterHash((uint)s+1)) : .5;
        float u=min((s+jitter)/samples,1-1e-6),t;
        if (optical>0 && u<optical)
            t=-log(max(1e-7,1-min(u/optical,1-1e-6)*mass))/rate;
        else
        {
            float choice=(u-optical)/max(angular,1e-20);
            int pick=min((int)choice,count-1);
            Source source=DescribeLight(ActiveLight(pick),origin,direction,hit,end);
            t=source.Center+source.Height*tan(source.Angle+min(choice-pick,1-1e-6)*source.Span);
        }
        t=max(0,t); if (hit) t=min(t,end);
        float pdf=optical*rate*exp(-rate*t)/mass;
        float3 p=origin+t*direction,incoming=0;
        [loop] for (int a=0;a<count;++a)
        {
            int i=ActiveLight(a);
            Source source=DescribeLight(i,origin,direction,hit,end);
            float falloff;
            incoming+=SourceContribution(i,source,t,p,extinction,falloff);
            pdf+=angular*pdfScales[a]*falloff;
        }
        radiance+=scatter*incoming/(max(pdf,1e-30)*samples);
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
        float2 tile=input.UV*float2(3,2); int2 cell=(int2)tile;
        uint w,h,layers; RasterShadows.GetDimensions(w,h,layers);
        float2 uv=.5+(frac(tile)-.5)*((w-2.0)/w);
        float z=RasterShadows.Load(int4(clamp((int2)(uv*w),0,(int)w-1),
            6*(int)round((RasterLightInfo.x-1)*View.ShadowLight)+cell.x+3*cell.y,0));
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
