#include "../Common/Inputs.hlsl"
#include "../Common/Projection.hlsl"

// Audio-reactive prototype. Independent shadow toggles for transient and tonal
// rings plus a live-S filament ring. Homogeneous single scattering, no history.
struct ShapeSettings { bool Enabled; float Count; float Seed; float Size; float Spread; float Speed; float3 Albedo; };
struct AirSettings { float Density; float3 Scattering; float3 Absorption; };
struct RingLightSettings { bool Enabled; float Count; float Gain; float RingRadius; bool Directional; float HalfAngle; bool Shadows; float Spread; };
struct TonalLightSettings { bool Enabled; float Gain; bool Directional; float HalfAngle; bool Shadows; };
struct FilamentRingSettings { float Intensity; };
struct QualitySettings { float Samples; float ShadowResolution; float ShadowBias; bool HardwarePCF; bool HalfResolutionAirlight; };
struct SamplingSettings { float OpticalDepthWeight; bool Jitter; bool RandomLight; };
struct FilterSettings { bool Enabled; };
struct ViewSettings { float CameraDistance; float CameraSpeed; float HorizontalOffset; float Exposure;
    bool ShowDepth; bool ShowShadows; float ShadowLight; };
cbuffer Parameters : register(b1)
{
    ShapeSettings Shape = { true, .03101343, .25, .45, .75, .1, float3(.65,.7,.75) };
    AirSettings Air = { .65, float3(1,1,1), float3(.03,.05,.08) };
    RingLightSettings Lights = { true, .62105263, .66666667, .6, true, .33333333, false, .75 };
    TonalLightSettings TonalLights = { true, .66666667, false, .33333333, false };
    FilamentRingSettings RingLight = { .5 };
    QualitySettings Quality = { .24409449, 1, .15, true, true };
    SamplingSettings Sampling = { .5, true, true };
    FilterSettings Filter = { true };
    ViewSettings View = { .55, .15, .5, .4, false, false, 0 };
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
#include "../Common/RasterAirlight.hlsl"

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
float4 Display(float3 radiance)
{
    float3 mapped=1-exp(-max(0,radiance)*exp2(6*(View.Exposure-.5)));
    return float4(lerp(12.92*mapped,1.055*pow(mapped,1/2.4)-.055,step(.0031308,mapped)),1);
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
    ProjectionCamera camera=SceneCamera();
    float3 direction=ProjectionDirection(camera,input.UV);
    float z=RasterDepth.Load(int3(pixel,0)); bool hit=z<1;
    float end=hit ? ProjectionDepthDistance(camera,direction,z) : 0;
    if (View.ShowDepth) return float4((hit ? exp(-.04*end) : 0).xxx,1);
    float3 surface=hit ? RasterSurface.Load(int3(pixel,0)).rgb*exp(-AirExtinction()*end) : 0;
    float3 air=any(AirScatter()>0) ? FilterAirlight(pixel) : 0;
    return Display(surface+air);
}
