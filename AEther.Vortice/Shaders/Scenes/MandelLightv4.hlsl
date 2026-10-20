#include "../Common/Inputs.hlsl"

// Audio-independent prototype. Rasterized opaque cubes in an infinite homogeneous
// atmosphere. Single scattering, hard nearest-depth shadows, no image history.
struct ShapeSettings { bool Enabled; float Count; float Seed; float Size; float Spread; float Speed; float3 Albedo; };
struct AirSettings { float Density; float3 Scattering; float3 Absorption; };
struct LightSettings { bool Enabled; float Intensity; float Speed; float Radius; float Orbit; float Phase; float3 Color; };
struct QualitySettings { float Samples; float ShadowResolution; float ShadowBias; };
struct SamplingSettings { float OpticalDepthWeight; bool Jitter; };
struct FilterSettings { bool Enabled; };
struct ViewSettings { float CameraDistance; float CameraSpeed; float HorizontalOffset; float Exposure;
    bool ShowDepth; bool ShowShadows; float ShadowLight; };
cbuffer Parameters : register(b1)
{
    ShapeSettings Shape = { true, .03101343, .25, .45, .75, .1, float3(.65,.7,.75) };
    AirSettings Air = { .22, float3(.85,.9,1), float3(.03,.05,.08) };
    LightSettings Light = { true, .55, .35, .25, .8, 0, float3(1,.8,.55) };
    LightSettings Light2 = { true, .45, .3, .25, .4, .25, float3(.15,.5,1) };
    LightSettings Light3 = { true, .3, .25, .25, .6, .5, float3(1,.2,.08) };
    LightSettings Light4 = { true, .28, .2, .25, .55, .75, float3(.2,1,.5) };
    QualitySettings Quality = { .24409449, .5, .15 };
    SamplingSettings Sampling = { .5, true };
    FilterSettings Filter = { true };
    ViewSettings View = { .55, .15, .5, .4, false, false, 0 };
};
cbuffer RasterPassConstants : register(b2) { uint RasterLight; uint RasterFace; uint RasterShadow; uint RasterPadding; };
Texture2DArray<float> RasterShadows;
Texture2D<float4> RasterSurface;
Texture2D<float> RasterDepth;
Texture2D<float4> RasterAirlight;
static const float Pi=3.14159265359;
static const float Near=.025, Far=128;
uint RasterHash(uint v) { v^=v>>16; v*=0x7feb352d; v^=v>>15; v*=0x846ca68b; return v^(v>>16); }
float Random(uint v) { return ((RasterHash(v)>>9)+.5)/8388608.0; }
LightSettings LightAt(int i)
{
    if (i==1) return Light2;
    if (i==2) return Light3;
    if (i==3) return Light4;
    return Light;
}
float3 LightPosition(int i)
{
    LightSettings l=LightAt(i);
    float a=T*.5*l.Speed-.8+2*Pi*l.Phase;
    float r=3+11*l.Orbit;
    return float3(r*cos(a),3*sin(.7*a+.3),r*sin(a));
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
float ProjectDepth(float z) { return (Far*z-Near*Far)/(Far-Near); }
float LinearDepth(float z) { return Near*Far/(Far-z*(Far-Near)); }
float ShadowBias() { return .0005+.02*Quality.ShadowBias; }
float Visibility(int light, float3 p, float3 normal)
{
    float3 delta=p-LightPosition(light);
    if (max(abs(delta.x),max(abs(delta.y),abs(delta.z)))<Near) return 1;
    int face; float2 uv; CubeAddress(delta,face,uv);
    uint w,h,layers; RasterShadows.GetDimensions(w,h,layers);
    int2 texel=clamp((int2)(uv*w),0,(int)w-1);
    float depth=RasterShadows.Load(int4(texel,6*light+face,0));
    if (depth>=1) return 1;
    float z=max(abs(delta.x),max(abs(delta.y),abs(delta.z)));
    // Compare against the receiver plane at the actual texel-center ray. This
    // removes sloped-face acne without filtering or a large silhouette offset.
    if (any(normal!=0))
    {
        float3 ray=CubeDirection(face,(texel+.5)/w);
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
    if (RasterShadow!=0) p=FacePosition(result.World-LightPosition(RasterLight),RasterFace);
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
    [unroll] for (int i=0;i<4;++i)
    {
        LightSettings light=LightAt(i); if (!light.Enabled || light.Intensity<=0) continue;
        float3 delta=LightPosition(i)-input.World;
        float r2=dot(delta,delta),r=sqrt(r2); if (r<1e-6) continue;
        float radius=.01+.49*light.Radius*light.Radius;
        float diffuse=max(0,dot(input.Normal,delta/r))/Pi;
        float shadow=Visibility(i,input.World,input.Normal);
        radiance+=input.Albedo*diffuse*shadow*exp(-extinction*r)
            *(3000*light.Intensity*light.Intensity*light.Color)/(r2+radius*radius);
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
    float3 Position; float3 Intensity; float Radius2;
    float Center; float Height; float Angle; float Span;
};
float4 RasterAirlightPS(PSin input) : SV_Target
{
    int2 pixel=(int2)input.Position.xy;
    float3 origin,forward,right,up; CameraFrame(origin,forward,right,up);
    float2 screen=(2*input.UV-1)*float2(max(1,AspectRatio),-max(1,1/AspectRatio));
    float3 direction=normalize(2*forward+screen.x*right+screen.y*up);
    float z=RasterDepth.Load(int3(pixel,0)); bool hit=z<1;
    float end=hit ? LinearDepth(z)/dot(direction,forward) : 0;
    // Alpha carries linear camera-space depth for the 3x3 filter (zero = sky).
    float guide=hit ? LinearDepth(z) : 0;
    float3 scatter=AirScatter(),extinction=AirExtinction();
    float3 radiance=0;
    if (!any(scatter>0)) return float4(0,0,0,guide);
    // A scalar optical-depth proposal follows the slowest attenuating active
    // scattering channel. Empty background rays have an infinite endpoint.
    float rate=min(scatter.x>0 ? extinction.x : 1e20,min(scatter.y>0 ? extinction.y : 1e20,scatter.z>0 ? extinction.z : 1e20));
    rate=max(rate,1e-12);
    float mass=hit ? OneMinusExp(rate*end) : 1;
    if (mass<=0) return float4(0,0,0,guide);
    Source sources[4]; int count=0;
    [unroll] for (int l=0;l<4;++l)
    {
        LightSettings light=LightAt(l); if (!light.Enabled || light.Intensity<=0 || !any(light.Color>0)) continue;
        Source source;
        source.Position=LightPosition(l); source.Intensity=3000*light.Intensity*light.Intensity*light.Color;
        float radius=.01+.49*light.Radius*light.Radius; source.Radius2=radius*radius;
        float3 delta=source.Position-origin;
        source.Center=dot(delta,direction);
        float3 perpendicular=delta-source.Center*direction;
        source.Height=sqrt(dot(perpendicular,perpendicular)+source.Radius2);
        source.Angle=atan(-source.Center/source.Height);
        source.Span=(hit ? atan((end-source.Center)/source.Height) : .5*Pi)-source.Angle;
        sources[l]=source; count++;
    }
    if (count==0) return float4(0,0,0,guide);
    int samples=1+(int)round(127*Quality.Samples);
    float optical=saturate(Sampling.OpticalDepthWeight), angular=(1-optical)/count;
    uint seed=RasterHash((uint)pixel.x^RasterHash((uint)pixel.y+1));
    [loop] for (int s=0;s<samples;++s)
    {
        float jitter=Sampling.Jitter ? Random(seed^RasterHash((uint)s+1)) : .5;
        float u=min((s+jitter)/samples,1-1e-6),t=0;
        if (optical>0 && u<optical)
            t=-log(max(1e-7,1-min(u/optical,1-1e-6)*mass))/rate;
        else
        {
            float choice=(u-optical)/max(angular,1e-20);
            [unroll] for (int pick=0;pick<4;++pick)
            {
                LightSettings light=LightAt(pick);
                if (!light.Enabled || light.Intensity<=0 || !any(light.Color>0)) continue;
                if (choice<1)
                {
                    Source source=sources[pick];
                    t=source.Center+source.Height*tan(source.Angle+min(choice,1-1e-6)*source.Span);
                    break;
                }
                choice-=1;
            }
        }
        t=max(0,t); if (hit) t=min(t,end);
        float pdf=optical*rate*exp(-rate*t)/mass;
        [unroll] for (int q=0;q<4;++q)
        {
            LightSettings light=LightAt(q); if (!light.Enabled || light.Intensity<=0 || !any(light.Color>0)) continue;
            Source source=sources[q]; float d=t-source.Center;
            pdf+=angular*source.Height/(max(source.Span,1e-12)*(source.Height*source.Height+d*d));
        }
        float3 p=origin+t*direction,incoming=0;
        [unroll] for (int i=0;i<4;++i)
        {
            LightSettings light=LightAt(i); if (!light.Enabled || light.Intensity<=0 || !any(light.Color>0)) continue;
            Source source=sources[i]; float3 delta=source.Position-p;
            float r2=dot(delta,delta),r=sqrt(r2); if (r<1e-6) continue;
            float mu=dot(delta/r,direction),phase=3*(1+mu*mu)/(16*Pi);
            incoming+=source.Intensity*phase*Visibility(i,p,0)*exp(-extinction*r)/(r2+source.Radius2);
        }
        radiance+=scatter*exp(-extinction*t)*incoming/(max(pdf,1e-30)*samples);
    }
    return float4(radiance,guide);
}

float3 FilterAirlight(int2 pixel)
{
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
        float z=RasterShadows.Load(int4(clamp((int2)(frac(tile)*w),0,(int)w-1),
            6*(int)round(3*View.ShadowLight)+cell.x+3*cell.y,0));
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
