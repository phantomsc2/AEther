#include "../Common/Inputs.hlsl"
#include "../Common/Projection.hlsl"

// Three fixed spectral point-light rings. Geometry and lighting live in HLSL.
struct ShapeSettings { bool Enabled; float Count; float Seed; float Size; float Spread; float Speed; float3 Albedo; };
struct AirSettings { float Density; float3 Scattering; float3 Absorption; };
struct LightSettings { bool Enabled; float Count; float Radius; float Gain; };
struct ViewSettings { float CameraDistance; float CameraSpeed; float HorizontalOffset; bool ShowDepth; };
cbuffer Parameters : register(b1)
{
    ShapeSettings Shape = { true, .03101343, .25, .45, .75, .1, float3(.65,.7,.75) };
    AirSettings Air = { .65, float3(1,1,1), float3(.03,.05,.08) };
    LightSettings Lights = { true, .62105263, .57, .66666667 };
    ViewSettings View = { .55, .15, .5, false };
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

uint2 GeometryCount()
{
    return uint2(36,Shape.Enabled ? 1+(uint)round(4095*Shape.Count) : 0);
}
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
uint LightCount() { return Lights.Enabled ? 3*(1+(uint)round(95*Lights.Count)) : 0; }
PointLight EvaluateLight(uint index)
{
    uint count=1+(uint)round(95*Lights.Count), ring=index/count;
    float angle=2*Pi*(index%count)/count;
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
    int2 pixel=(int2)input.Position.xy;
    float3 origin,direction; float end; bool hit;
    RasterRay(pixel,origin,direction,hit,end);
    if (View.ShowDepth) return DisplayOutput((hit ? exp(-.04*end) : 0).xxx);
    float3 surface=hit ? RasterSurface.Load(int3(pixel,0)).rgb*exp(-AirExtinction()*end) : 0;
    return LinearOutput(SceneLightGain*(surface+(any(AirScatter()>0) ? RasterAirlight.Load(int3(pixel,0)).rgb : 0)));
}
