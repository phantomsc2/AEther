#ifndef AETHER_RASTER_LIGHT
#define AETHER_RASTER_LIGHT
#include "Projection.hlsl"

// Scene callbacks. The host owns capacity, never the light count or properties.
struct PointLight { float3 Position; float3 Intensity; };
uint LightCount();
PointLight EvaluateLight(uint index);
static const uint PointLightCapacity=512;
RWStructuredBuffer<PointLight> PointLightOutput : register(u0);
RWByteAddressBuffer PointLightCountOutput : register(u1);
StructuredBuffer<PointLight> PointLights;
ByteAddressBuffer PointLightCount;

[numthreads(64,1,1)]
void PreparePointLights(uint3 id : SV_DispatchThreadID)
{
    uint count=min(LightCount(),PointLightCapacity);
    if (id.x==0) PointLightCountOutput.Store(0,count);
    if (id.x<count) PointLightOutput[id.x]=EvaluateLight(id.x);
}

Texture2D<float4> RasterSurface;
Texture2D<float> RasterDepth;
Texture2D<float4> RasterAirlight;
static const float Pi=3.14159265359;
// Numerical denominator guards only, far below world-coordinate precision.
// No world-space source radius or smoothing is applied to either light kernel.
static const float NumericDistance2=1e-30,NumericDistance=1e-15,HdrMaximum=3.402823466e38;
float3 StoreRadiance(float3 value) { return min(max(value,0),HdrMaximum); }
float3 SingularRadiance(float3 intensity)
{
    return float3(intensity.x>0,intensity.y>0,intensity.z>0)*HdrMaximum;
}
float3 AirScatter();
float3 AirExtinction();
#include "AirlightLookup.hlsl"
#include "PointAirlightIntegral.hlsl"

float4 GeometryProject(float3 world)
{
    return ProjectionClip(SceneCamera(),world);
}
float4 RasterSurfacePS(GeometryOutput input) : SV_Target
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
void RasterRay(int2 pixel, out float3 origin, out float3 direction, out bool hit, out float end)
{
    ProjectionCamera camera=SceneCamera();
    origin=camera.Origin; direction=ProjectionDirection(camera,(pixel+.5)/Resolution);
    float z=RasterDepth.Load(int3(pixel,0)); hit=z<1;
    end=hit ? ProjectionDepthDistance(camera,direction,z) : 0;
}
float4 IntegrateRasterAirlight(int2 pixel)
{
    float3 origin,direction; bool hit; float end;
    RasterRay(pixel,origin,direction,hit,end);
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
float4 RasterAirlightPS(PSin input) : SV_Target
{
    return IntegrateRasterAirlight((int2)input.Position.xy);
}
#endif
