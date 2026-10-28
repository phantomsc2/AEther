#ifndef AETHER_POINT_LIGHTS
#define AETHER_POINT_LIGHTS

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
#endif
