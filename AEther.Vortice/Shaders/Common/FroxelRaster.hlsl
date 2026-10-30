#ifndef AETHER_FROXEL_RASTER
#define AETHER_FROXEL_RASTER
Texture2D<float4> FroxelSurface;
Texture2D<float> FroxelSurfaceDepth;

struct FroxelVertex
{
    float4 Position : SV_POSITION;
    float3 World : TEXCOORD0;
    nointerpolation float3 Normal : TEXCOORD1;
    nointerpolation float3 Albedo : TEXCOORD2;
};
FroxelVertex FroxelGeometryVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    uint seed = FroxelHash(instance+17);
    float a = 2*FroxelPi*FroxelRandom(seed) + T*lerp(1.3,2.5,FroxelRandom(seed+1));
    float b = 2*FroxelPi*FroxelRandom(seed+2) + T*lerp(3,5,FroxelRandom(seed+3));
    float tube = .95*sqrt(FroxelRandom(seed+4));
    float r = 4.5+tube*cos(b);
    float3 center = float3(r*cos(a),r*sin(a),tube*sin(b));
    uint face = vertex/3, corner = vertex%3;
    float3 signs = float3((face&1) ? 1 : -1,(face&2) ? 1 : -1,(face&4) ? 1 : -1);
    float3 local = corner==0 ? float3(signs.x,0,0) : corner==1 ? float3(0,signs.y,0) : float3(0,0,signs.z);
    float3 normal = normalize(signs);
    float size = lerp(.055,.13,FroxelRandom(seed+5));
    float spin = T*lerp(4,9,FroxelRandom(seed+6))+a;
    local = mul(Ry(spin),mul(Rx(spin*.73),local));
    normal = mul(Ry(spin),mul(Rx(spin*.73),normal));
    FroxelVertex output;
    output.World = center+size*local;
    output.Normal = normal;
    output.Albedo = lerp(float3(.25,.32,.4),float3(.85,.8,.7),FroxelRandom(seed+7));
    output.Position = ProjectionClip(FroxelCamera(),output.World);
    return output;
}
float4 FroxelSurfacePS(FroxelVertex input) : SV_Target
{
    float3 sum = 0;
    [loop] for(uint i=0;i<FroxelLightCount;++i)
    {
        FroxelLight light = FroxelLights[i];
        float3 delta = light.Position-input.World;
        float r2 = dot(delta,delta);
        float diffuse = max(0,dot(input.Normal,delta*rsqrt(max(r2,1e-12))))/FroxelPi;
        float radius = FroxelSourceRadius();
        sum += input.Albedo*diffuse*light.Intensity*FroxelLightTransmittance(input.World,light.Position)/(r2+radius*radius);
    }
    return float4(min(sum,65504),1);
}
// Prefix textures store values at the FAR boundary of each segment.
// Reconstruct the remaining fraction analytically instead of interpolating prefixes in Z.
float4 FroxelAtDepth(float2 uv,float depth)
{
    depth = clamp(depth,0,64);
    float slice = log2(1+depth)/log2(65.0)*FroxelDepth;
    uint k = min((uint)floor(slice),FroxelDepth-1);
    float4 prefix = float4(0,0,0,1);
    if(k>0) prefix = FroxelIntegrated.SampleLevel(FroxelSampler,float3(uv,(k-.5)/FroxelDepth),0);
    float4 source = FroxelSource.SampleLevel(FroxelSampler,float3(uv,(k+.5)/FroxelDepth),0);
    ProjectionCamera camera = FroxelCamera();
    float3 ray = ProjectionDirection(camera,uv);
    float distance = max(0,depth-FroxelSliceDepth(k))/dot(ray,camera.Forward);
    return float4(prefix.rgb+prefix.a*source.rgb*FroxelSegmentWeight(source.a,distance),
        prefix.a*exp(-source.a*distance));
}
float3 FroxelComposite(PSin input)
{
    int2 pixel = (int2)input.Position.xy;
    float depth = FroxelSurfaceDepth.Load(int3(pixel,0));
    if(depth>=1) return FroxelRadiance(input.UV);
    float viewDepth = ProjectionLinearDepth(FroxelCamera(),depth);
    float4 fog = FroxelAtDepth(input.UV,viewDepth);
    return fog.rgb+fog.a*FroxelSurface.Load(int3(pixel,0)).rgb;
}
#endif
