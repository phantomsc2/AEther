#define FROXEL_CUSTOM_SOURCE
#include "../Common/Froxel.hlsl"
#include "../Common/VolumeNoise.hlsl"
#include "../Common/RingLight.hlsl"

struct MediumSettings { float Density; float3 Albedo; float NoiseScale; float Contrast; float Speed; };
struct RingSettings { float Radius; float Softening; float Gain; };
struct CameraSettings { float Distance; float Yaw; float Pitch; };
cbuffer Parameters
{
    MediumSettings Medium = { .159, float3(.95,.95,.95), .720, .796, .730 };
    RingSettings Ring = { .5, .285714286, .5 };
    CameraSettings Camera = { .253, .574, .572 };
};
ProjectionCamera FroxelCamera()
{
    float yaw = (Camera.Yaw-.5)*2*FroxelPi, pitch = (Camera.Pitch-.5)*(160*FroxelPi/180);
    float3 outward = float3(sin(yaw)*cos(pitch),sin(pitch),-cos(yaw)*cos(pitch));
    float3 forward = -outward, right = normalize(cross(float3(0,1,0),forward));
    return MakeProjectionCamera(outward*lerp(2,40,Camera.Distance),forward,right,cross(forward,right));
}
FroxelMedium EvaluateFroxelMedium(float3 world)
{
    float noise = VolumeNoise(world*lerp(.1,1.5,Medium.NoiseScale)
        +T*Medium.Speed*float3(.19,-.11,.13));
    FroxelMedium m;
    m.Extinction = .6*Medium.Density*Medium.Density*max(0,1+Medium.Contrast*noise)
        *(1-smoothstep(6,8,length(world)));
    m.Scattering = Medium.Albedo*m.Extinction;
    m.Anisotropy = 0;
    return m;
}
float3 EvaluateFroxelSource(float3 world, FroxelMedium medium)
{
    float radius = 1+7*Ring.Radius;
    float softening = .01+.49*Ring.Softening*Ring.Softening;
    float gain = 1000*exp2(16*(Ring.Gain-.5));
    // Exact arc illumination for the softened source model. No light-path attenuation.
    return medium.Scattering*(gain/(4*FroxelPi))*RingIncident(world,radius,softening);
}
float4 PS(PSin input) : SV_Target { return LinearOutput(FroxelRadiance(input.UV)); }
