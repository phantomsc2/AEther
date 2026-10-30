#define FROXEL_CUSTOM_SOURCE
#define FROXEL_CUSTOM_RASTER
#include "../Common/Froxel.hlsl"
#include "../Common/VolumeNoise.hlsl"
#include "../Common/AdaptiveRing.hlsl"

struct MediumSettings { float Density; float3 Albedo; float NoiseScale; float Contrast; float Speed; };
struct RingSettings { float Radius; float Softening; float Gain; };
struct CloudSettings { float Speed; float Spread; float Size; };
struct CameraSettings { float Distance; float Yaw; float Pitch; };
cbuffer Parameters
{
    CloudSettings Cloud = { .5, .5, .5 };
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
    // Log-spaced adaptive arcs; isotropic scattering, no light-path attenuation.
    return medium.Scattering*(gain/(4*FroxelPi))*AdaptiveRingIncident(world,radius,softening,FroxelLightCount,0,false);
}
float3 EvaluateFroxelSurface(float3 world,float3 normal,float3 albedo)
{
    float radius=1+7*Ring.Radius, softening=.01+.49*Ring.Softening*Ring.Softening;
    float gain=1000*exp2(16*(Ring.Gain-.5));
    return albedo*(gain/FroxelPi)*AdaptiveRingIncident(world,radius,softening,FroxelLightCount,normal,true);
}
FroxelVertex EvaluateFroxelVertex(uint vertex,uint instance)
{
    uint seed=FroxelHash(instance+17);
    float time=T*(2*Cloud.Speed);
    float a=2*FroxelPi*FroxelRandom(seed)+time*lerp(.08,.16,FroxelRandom(seed+1));
    float b=2*FroxelPi*FroxelRandom(seed+2)+time*lerp(.12,.22,FroxelRandom(seed+3));
    float tube=3*Cloud.Spread*sqrt(FroxelRandom(seed+4));
    float r=1+7*Ring.Radius+tube*cos(b);
    float3 center=float3(r*cos(a),r*sin(a),tube*sin(b));
    uint face=vertex/3, corner=vertex%3;
    float3 signs=float3((face&1)?1:-1,(face&2)?1:-1,(face&4)?1:-1);
    float3 local=corner==0 ? float3(signs.x,0,0) : corner==1 ? float3(0,signs.y,0) : float3(0,0,signs.z);
    float3 normal=normalize(signs);
    float size=lerp(.055,.13,FroxelRandom(seed+5))*(2*Cloud.Size);
    float spin=time*lerp(.15,.4,FroxelRandom(seed+6))+2*FroxelPi*FroxelRandom(seed+8);
    local=mul(Ry(spin),mul(Rx(spin*.73),local));
    normal=mul(Ry(spin),mul(Rx(spin*.73),normal));
    FroxelVertex output;
    output.World=center+size*local;
    output.Normal=normal;
    output.Albedo=lerp(.35,.7,FroxelRandom(seed+7));
    output.Position=ProjectionClip(FroxelCamera(),output.World);
    return output;
}
float4 PS(PSin input) : SV_Target { return LinearOutput(FroxelComposite(input)); }
