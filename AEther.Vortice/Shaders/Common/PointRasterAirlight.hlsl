#ifndef AETHER_POINT_RASTER_AIRLIGHT
#define AETHER_POINT_RASTER_AIRLIGHT
Texture2D<float4> PointSurface;
Texture2D<float> PointDepth;
Texture2D<float4> PointAirlight;
Texture2D<float4> RasterAirlightLut;
SamplerState RasterLutSampler;
cbuffer RasterAirlightConstants : register(b5) { float4 RasterLookupInfo; };
static const float Pi=3.14159265359;
static const float Near=.025, Far=128;
// Numerical denominator guards only, far below world-coordinate precision.
// No world-space source radius or smoothing is applied to either light kernel.
static const float NumericDistance2=1e-30,NumericDistance=1e-15,HdrMaximum=3.402823466e38;
float3 StoreRadiance(float3 value) { return min(max(value,0),HdrMaximum); }
float3 SingularRadiance(float3 intensity)
{
    return float3(intensity.x>0,intensity.y>0,intensity.z>0)*HdrMaximum;
}
uint RasterHash(uint v) { v^=v>>16; v*=0x7feb352d; v^=v>>15; v*=0x846ca68b; return v^(v>>16); }
float Random(uint v) { return ((RasterHash(v)>>9)+.5)/8388608.0; }
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

float LinearDepth(float z) { return Near*Far/(Far-z*(Far-Near)); }
float3 AirScatter() { return .3*Air.Density*Air.Density*Air.Scattering; }
float3 AirExtinction() { return .3*Air.Density*Air.Density*(Air.Scattering+Air.Absorption); }
#include "PointAirlightIntegral.hlsl"

float4 GeometryProject(float3 world)
{
    float3 origin,forward,right,up; CameraFrame(origin,forward,right,up);
    float3 v=world-origin;
    float z=dot(v,forward);
    return float4(2*dot(v,right)/max(1,AspectRatio),2*dot(v,up)/max(1,1/AspectRatio),
        (Far*z-Near*Far)/(Far-Near),z);
}
float4 PointSurfacePS(GeometryOutput input) : SV_Target
{
    float3 sum=0,extinction=AirExtinction();
    uint count=PointLightCount.Load(0);
    [loop] for (uint i=0;i<count;++i)
    {
        PointLight light=PointLights[i];
        if (!any(light.Intensity>0)) continue;
        float3 delta=light.Position-input.World;
        float r2=dot(delta,delta),r=sqrt(r2);
        if (r2==0) { sum+=SingularRadiance(input.Albedo*light.Intensity); continue; }
        float diffuse=max(0,dot(input.Normal,delta/r))/Pi;
        sum+=input.Albedo*diffuse*exp(-extinction*r)*light.Intensity/max(r2,NumericDistance2);
    }
    return float4(StoreRadiance(sum),1);
}
void PointRay(int2 pixel, out float3 origin, out float3 direction, out bool hit, out float end)
{
    float3 forward,right,up; CameraFrame(origin,forward,right,up);
    float2 screen=(2*(pixel+.5)/Resolution-1)*float2(max(1,AspectRatio),-max(1,1/AspectRatio));
    direction=normalize(2*forward+screen.x*right+screen.y*up);
    float z=PointDepth.Load(int3(pixel,0)); hit=z<1;
    end=hit ? LinearDepth(z)/dot(direction,forward) : 0;
}
float4 IntegratePointAirlight(int2 pixel)
{
    float3 origin,direction; bool hit; float end;
    PointRay(pixel,origin,direction,hit,end);
    float3 sum=0,scatter=AirScatter(),extinction=AirExtinction();
    uint w,h; RasterAirlightLut.GetDimensions(w,h);
    float4 layout=float4((w-1.0)/w,(h-1.0)/h,.5/w,.5/h);
    uint count=PointLightCount.Load(0);
    [branch] if (any(scatter>0))
    [loop] for (uint i=0;i<count;++i)
    {
        PointLight light=PointLights[i];
        if (!any(light.Intensity>0)) continue;
        float3 delta=light.Position-origin;
        float c=dot(delta,direction);
        float3 perpendicular=delta-c*direction;
        float b2=dot(perpendicular,perpendicular),stop=hit ? end : 1e30;
        if (b2==0 && c>=0 && c<=stop)
            sum+=SingularRadiance(scatter*light.Intensity);
        else sum+=scatter*light.Intensity*PointIntegral(c,b2,0,stop,extinction,layout);
    }
    return float4(StoreRadiance(sum),1);
}
float4 PointAirlightPS(PSin input) : SV_Target
{
    return IntegratePointAirlight((int2)input.Position.xy);
}
#endif
