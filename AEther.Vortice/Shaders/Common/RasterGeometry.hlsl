#ifndef AETHER_RASTER_GEOMETRY
#define AETHER_RASTER_GEOMETRY
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
