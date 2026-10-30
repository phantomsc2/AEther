#ifndef AETHER_FROXEL
#define AETHER_FROXEL
#include "Projection.hlsl"

cbuffer FroxelConstants { uint FroxelWidth, FroxelHeight, FroxelDepth, FroxelLightCount; };
struct FroxelLight { float3 Position; float3 Intensity; };
struct FroxelMedium { float3 Scattering; float Extinction; float Anisotropy; };
ProjectionCamera FroxelCamera();
FroxelLight EvaluateFroxelLight(uint index);
FroxelMedium EvaluateFroxelMedium(float3 world);
float FroxelLightTransmittance(float3 world, float3 light);
float FroxelSourceRadius();
#ifdef FROXEL_CUSTOM_SOURCE
float3 EvaluateFroxelSource(float3 world, FroxelMedium medium);
#endif

RWStructuredBuffer<FroxelLight> FroxelLightOutput;
StructuredBuffer<FroxelLight> FroxelLights;
RWTexture3D<float4> FroxelSourceOutput;
Texture3D<float4> FroxelSource;
RWTexture3D<float4> FroxelIntegratedOutput;
Texture3D<float4> FroxelIntegrated;
SamplerState FroxelSampler;
static const float FroxelPi = 3.14159265359;

// Shifted logarithmic view depth: boundary zero includes the camera origin.
float FroxelSliceDepth(float slice) { return exp2(log2(65.0) * slice / FroxelDepth) - 1; }
uint FroxelHash(uint x) { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; return x ^ (x >> 16); }
// Historical volume integrator's open-interval hash sampling, without time jitter.
float FroxelRandom(uint x) { return ((FroxelHash(x) >> 9) + .5) / 8388608.0; }
// Incoming direction travels FROM the source; outgoing direction points TO the camera.
float FroxelPhase(float cosine, float g)
{
    float d = 1 + g*g - 2*g*cosine;
    return (1-g*g) / (4*FroxelPi*d*sqrt(d));
}
float FroxelSegmentWeight(float extinction, float distance)
{
    float tau = extinction * distance;
    // Avoid cancellation, including exactly zero extinction.
    return tau < .001 ? distance*(1 - .5*tau + tau*tau/6) : (1-exp(-tau))/extinction;
}

[numthreads(64,1,1)]
void PrepareFroxelLights(uint3 id : SV_DispatchThreadID)
{
    if (id.x < FroxelLightCount) FroxelLightOutput[id.x] = EvaluateFroxelLight(id.x);
}
[numthreads(8,8,1)]
void InjectFroxels(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= FroxelWidth || id.y >= FroxelHeight || id.z >= FroxelDepth) return;
    ProjectionCamera camera = FroxelCamera();
    float3 ray = ProjectionDirection(camera, (id.xy+.5)/float2(FroxelWidth,FroxelHeight));
    float lo = FroxelSliceDepth(id.z), hi = FroxelSliceDepth(id.z+1);
    uint seed = id.x + 0x9e3779b9*id.y + 0x85ebca6b*id.z;
    float jitter = FroxelRandom(seed);
#ifdef FROXEL_CUSTOM_SOURCE
    // Low-discrepancy temporal rotation, no previous-frame image or persistent state.
    jitter = frac(jitter + frac(floor(T*60)*.61803398875));
#endif
    float z = lerp(lo,hi,jitter);
    float3 world = ProjectionPosition(camera,ray,z);
    FroxelMedium medium = EvaluateFroxelMedium(world);
    float3 source = 0;
#ifdef FROXEL_CUSTOM_SOURCE
    if (any(medium.Scattering > 0)) source = EvaluateFroxelSource(world,medium);
#else
    if (any(medium.Scattering > 0))
    [loop] for (uint i=0; i<FroxelLightCount; ++i)
    {
        FroxelLight light = FroxelLights[i];
        float3 delta = world-light.Position;
        float r2 = dot(delta,delta), r = sqrt(r2);
        float cosine = r > 1e-6 ? dot(delta/r,-ray) : 0;
        float radius = FroxelSourceRadius();
        source += medium.Scattering * FroxelPhase(cosine,medium.Anisotropy)
            * light.Intensity * FroxelLightTransmittance(world,light.Position) / (r2+radius*radius);
    }
#endif
    // Saturate only at the storage format's limit, before float16 conversion.
    FroxelSourceOutput[id] = float4(min(source,65504),medium.Extinction);
}
[numthreads(8,8,1)]
void IntegrateFroxels(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= FroxelWidth || id.y >= FroxelHeight) return;
    ProjectionCamera camera = FroxelCamera();
    float3 ray = ProjectionDirection(camera,(id.xy+.5)/float2(FroxelWidth,FroxelHeight));
    float rayScale = rcp(dot(ray,camera.Forward));
    float3 radiance = 0; float transmittance = 1;
    float previous = 0;
    [loop] for (uint k=0; k<FroxelDepth; ++k)
    {
        float next = FroxelSliceDepth(k+1);
        float distance = (next-previous)*rayScale;
        float4 source = FroxelSource.Load(int4(id.xy,k,0));
        radiance += transmittance*source.rgb*FroxelSegmentWeight(source.a,distance);
        transmittance *= exp(-source.a*distance);
        FroxelIntegratedOutput[uint3(id.xy,k)] = float4(min(radiance,65504),transmittance);
        previous = next;
    }
}
float3 FroxelRadiance(float2 uv)
{
    return FroxelIntegrated.SampleLevel(FroxelSampler,float3(uv,1-.5/FroxelDepth),0).rgb;
}
#if !defined(FROXEL_CUSTOM_SOURCE) || defined(FROXEL_CUSTOM_RASTER)
#include "FroxelRaster.hlsl"
#endif
#endif
