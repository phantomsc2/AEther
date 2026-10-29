#include "../Common/Inputs.hlsl"
#include "../Common/Projection.hlsl"

// Three fixed spectral point-light rings. Geometry and lighting live in HLSL.
struct ShapeSettings { float Seed; float Size; float Spread; float Speed; float3 Albedo; };
struct AirSettings { float Density; float3 Scattering; float3 Absorption; };
struct LightSettings { float Radius; float Gain; };
struct ViewSettings { float CameraDistance; float CameraSpeed; float HorizontalOffset; };
struct ResourceSizes { uint PointLights; uint2 Geometry; uint2 RasterAirlightLut; };
cbuffer Parameters : register(b1)
{
    ResourceSizes Sizes = { 180, uint2(36,128), uint2(513,1025) };
    ShapeSettings Shape = { .25, .45, .75, .1, float3(.65,.7,.75) };
    AirSettings Air = { .65, float3(1,1,1), float3(.03,.05,.08) };
    LightSettings Lights = { .57, .66666667 };
    ViewSettings View = { .55, .15, .5 };
};
ProjectionCamera SceneCamera()
{
    float3 origin,forward,right,up;
    float s,c; sincos(T*.25*View.CameraSpeed,s,c);
    forward=normalize(float3(-9*c-13.5*s,-6,13.5*c-9*s));
    right=normalize(cross(forward,float3(0,1,0))); up=cross(right,forward);
    float distance=40*View.CameraDistance;
    origin=-distance*forward+(2*View.HorizontalOffset-1)*distance*right;
    return MakeProjectionCamera(origin,forward,right,up);
}
#include "../Common/RasterGeometry.hlsl"

#include "../Common/RasterLight.hlsl"

float3 AirScatter() { return .3*Air.Density*Air.Density*Air.Scattering; }
float3 AirExtinction() { return .3*Air.Density*Air.Density*(Air.Scattering+Air.Absorption); }
GeometryVertex EvaluateGeometry(uint vertex, uint instance)
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
    GeometryVertex result;
    result.World=center+local; result.Normal=normal;
    result.Albedo=Shape.Albedo*lerp(.65,1,Random(id+6));
    return result;
}
// Fixed lighting calibration preserves the former default exposure (-0.6 stops).
static const float SceneLightGain = exp2(-.6);
PointLight EvaluateLight(uint index)
{
    uint total=Sizes.PointLights;
    uint first=(total+2)/3, second=(total+1)/3;
    uint ring=index<first ? 0 : index<first+second ? 1 : 2;
    uint start=ring==0 ? 0 : ring==1 ? first : first+second;
    uint count=ring==0 ? first : ring==1 ? second : total-first-second;
    float angle=2*Pi*(index-start)/max(1,count);
    float2 p=float2(sin(angle),cos(angle));
    float frequency=atan2(abs(p.x),-p.y)/Pi;
    float stereo=.5+.5*p.x*abs(p.x);
    // Spectrum has two rows; sample the live STB row center, not the diagnostic row.
    float3 stb=max(0,lerp(Spectrum0.SampleLevel(Linear,float2(frequency,.25),0).rgb,
        Spectrum1.SampleLevel(Linear,float2(frequency,.25),0).rgb,stereo));
    PointLight light;
    light.Position=10*Lights.Radius*(ring==0 ? float3(p.x,0,p.y)
        : ring==1 ? float3(p.x,p.y,0) : float3(0,p.x,p.y));
    float3 color=.5+.5*cos(2*Pi*(frequency*.85+float3(.04,.38,.62)));
    light.Intensity=3000*exp2(12*Lights.Gain-6)*stb[ring]*color;
    return light;
}
float4 PS(PSin input) : SV_Target
{
    uint w,h; RasterSurface.GetDimensions(w,h);
    int2 pixel=min(int2(input.UV*float2(w,h)),int2(w-1,h-1));
    float3 origin,direction; float end; bool hit;
    RasterRay(pixel,origin,direction,hit,end);
    float3 surface=hit ? RasterSurface.Load(int3(pixel,0)).rgb*exp(-AirExtinction()*end) : 0;
    return LinearOutput(SceneLightGain*(surface+(any(AirScatter()>0) ? RasterAirlight.SampleLevel(Linear,input.UV,0).rgb : 0)));
}
