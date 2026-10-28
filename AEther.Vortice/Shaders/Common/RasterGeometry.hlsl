#ifndef AETHER_RASTER_GEOMETRY
#define AETHER_RASTER_GEOMETRY

// Scene callbacks: counts are (vertices per instance, instances).
// Positions and normals are in world space. Projection is supplied by the consumer.
struct GeometryVertex { float3 World; float3 Normal; float3 Albedo; };
uint2 GeometryCount();
GeometryVertex EvaluateGeometry(uint vertex, uint instance);
float4 GeometryProject(float3 world);

RWByteAddressBuffer GeometryDrawArguments : register(u0);
[numthreads(1,1,1)]
void PrepareGeometry(uint3 id : SV_DispatchThreadID)
{
    GeometryDrawArguments.Store4(0,uint4(GeometryCount(),0,0));
}

struct GeometryOutput
{
    float4 Position : SV_POSITION;
    float3 World : TEXCOORD0;
    nointerpolation float3 Normal : TEXCOORD1;
    nointerpolation float3 Albedo : TEXCOORD2;
};
GeometryOutput RasterGeometryVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    GeometryVertex v=EvaluateGeometry(vertex,instance);
    GeometryOutput result;
    result.World=v.World; result.Normal=v.Normal; result.Albedo=v.Albedo;
    result.Position=GeometryProject(v.World);
    return result;
}
#endif
