#define FROXEL_CUSTOM_SOURCE
#define FROXEL_CUSTOM_RASTER
#define FROXEL_SEGMENT_SOURCE
#include "../Common/Froxel.hlsl"
#include "../Common/VolumeNoise.hlsl"
#include "../Common/UniformRing.hlsl"

struct MediumSettings { float Density; float3 Albedo; float NoiseScale; float Contrast; float Speed; float Anisotropy; };
struct RingSettings { float Radius; float Softening; float Gain; float Speed; };
struct CloudSettings { float Speed; float Spread; float Size; };
struct CameraSettings { float Distance; float Yaw; float Pitch; };
cbuffer Parameters
{
    CloudSettings Cloud = { .057, .174, .5 };
    MediumSettings Medium = { .36, float3(.888,.840,.716), .720, .796, .730, .8 };
    RingSettings Ring = { .5, .285714286, .40, .5 };
    CameraSettings Camera = { .232, .523, .561 };
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
    m.Extinction = .6*Medium.Density*Medium.Density*max(0,1+Medium.Contrast*noise);
    m.Scattering = Medium.Albedo*m.Extinction;
    m.Anisotropy = 2*Medium.Anisotropy-1;
    return m;
}
float3 RingColor(uint i)
{
    return i==0 ? float3(1,.18,.04) : i==1 ? float3(.04,.7,1) : float3(.45,.08,1);
}
float RingCycle(uint i)
{
    float wave=.5+.5*sin(T*(2*Ring.Speed)*(2*FroxelPi/8)+i*(2*FroxelPi/3));
    return wave*wave;
}
float3 ThreeRingLight(float3 world,float3 normal,bool surface)
{
    float radius=1+7*Ring.Radius, softening=.01+.49*Ring.Softening*Ring.Softening;
    float gain=20*exp2(16*(Ring.Gain-.5));
    float3 sum=0;
    [unroll] for(uint i=0;i<3;++i)
    {
        float3 p=RingLocal(world,i), n=RingLocal(normal,i);
        float weight=surface ? UniformRingDiffuse(p,n,radius,softening) : UniformRingWeight(p,radius,softening);
        if(!surface)
        {
            float3 outgoing=normalize(FroxelCamera().Origin-world);
            weight*=UniformRingAngularAverage(p,RingLocal(outgoing,i),radius,softening,2*Medium.Anisotropy-1,true);
        }
        sum+=RingColor(i)*RingCycle(i)*weight;
    }
    return gain*sum;
}
float3 EvaluateFroxelSource(float3 world, FroxelMedium medium)
{
    return medium.Scattering*ThreeRingLight(world,0,false)/(4*FroxelPi);
}
float3 EvaluateFroxelSegment(float3 begin,float3 end,float3 scattering)
{
    float radius=1+7*Ring.Radius, softening=.01+.49*Ring.Softening*Ring.Softening;
    float3 light=0;
    float3 direction=end-begin;
    float3 outgoing=-direction*rsqrt(max(dot(direction,direction),1e-12));
    float g=2*Medium.Anisotropy-1;
    [unroll] for(uint i=0;i<3;++i)
        light+=RingColor(i)*RingCycle(i)*UniformRingSegmentPhase(RingLocal(begin,i),RingLocal(end,i),
            radius,softening,RingLocal(outgoing,i),g);
    return scattering*light*(20*exp2(16*(Ring.Gain-.5))/(4*FroxelPi));
}
float3 EvaluateFroxelSurface(float3 world,float3 normal,float3 albedo)
{
    return albedo*ThreeRingLight(world,normal,true)/FroxelPi;
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
    output.World=RingWorld(center+size*local,instance%3);
    output.Normal=RingWorld(normal,instance%3);
    output.Albedo=lerp(.35,.7,FroxelRandom(seed+7));
    output.Position=ProjectionClip(FroxelCamera(),output.World);
    return output;
}
float4 PS(PSin input) : SV_Target { return LinearOutput(FroxelComposite(input)); }
