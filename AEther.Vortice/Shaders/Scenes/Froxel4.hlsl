#define FROXEL_CUSTOM_SOURCE
#define FROXEL_CUSTOM_RASTER
#define FROXEL_SEGMENT_SOURCE
#define FROXEL_PREPARED_LIGHTING
#include "../Common/Froxel.hlsl"
#include "../Common/VolumeNoise.hlsl"
#include "../Common/LineLight.hlsl"

struct MediumSettings { float Density; float3 Albedo; float NoiseScale; float Contrast; float Speed; };
struct TubeSettings { float Radius; float Softening; float Gain; };
struct CloudSettings { float Speed; float Spread; float Size; float Distance; };
struct CameraSettings { float Distance; float Yaw; float Pitch; };
cbuffer Parameters
{
    CloudSettings Cloud = { .057, .375, .5, .466666667 };
    MediumSettings Medium = { .36, float3(.888,.840,.716), .720, .796, .730 };
    TubeSettings Tube = { .214285714, .285714286, .4 };
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
    m.Anisotropy = 0;
    return m;
}
[numthreads(64,1,1)]
void PrepareLineLights(uint3 id:SV_DispatchThreadID)
{
    uint bands,rows;Spectrum0.GetDimensions(bands,rows);
    if(id.x>=2*bands) return;
    uint side=id.x/bands,bin=id.x%bands;
    float angle=(side==0 ? -1 : 1)*((bin+.5)*FroxelPi/bands);
    float radius=1+7*Tube.Radius;
    LineLight light;
    light.Origin=float3(radius*sin(angle),-radius*cos(angle),0);
    light.Axis=float3(0,0,1);
    light.Softening=.01+.49*Tube.Softening*Tube.Softening;
    float3 spectrum=side==0 ? Spectrum0.Load(int3(bin,0,0)).rgb : Spectrum1.Load(int3(bin,0,0)).rgb;
    light.Intensity=max(0,spectrum)*(1000*exp2(16*(Tube.Gain-.5)))*(FroxelPi/bands);
    light.Padding0=0;light.Padding1=0;
    LineLightOutput[id.x]=light;
}
float3 EvaluateFroxelSource(float3 world,FroxelMedium medium)
{
    uint count,stride;LineLights.GetDimensions(count,stride);
    float3 sum=0;
    [loop] for(uint i=0;i<count;++i)
    {
        LineLight light=LineLights[i];
        if(any(light.Intensity>0)) sum+=light.Intensity*LineWeight(world,light);
    }
    return medium.Scattering*sum/(4*FroxelPi);
}
float3 EvaluateFroxelSegment(float3 begin,float3 end,float3 scattering)
{
    uint count,stride;LineLights.GetDimensions(count,stride);
    float3 sum=0;
    [loop] for(uint i=0;i<count;++i)
    {
        LineLight light=LineLights[i];
        if(any(light.Intensity>0)) sum+=light.Intensity
            *LineSegmentAverage(begin,end,light);
    }
    return scattering*sum/(4*FroxelPi);
}
float3 EvaluateFroxelSurface(float3 world,float3 normal,float3 albedo)
{
    uint count,stride;LineLights.GetDimensions(count,stride);
    float3 sum=0;
    [loop] for(uint i=0;i<count;++i)
    {
        LineLight light=LineLights[i];
        if(any(light.Intensity>0)) sum+=light.Intensity*LineWeight(world,light)
            *LineDiffuseAverage(world,light,normal);
    }
    return albedo*sum/FroxelPi;
}
FroxelVertex EvaluateFroxelVertex(uint vertex,uint instance)
{
    uint seed=FroxelHash(instance+17);
    float time=T*(2*Cloud.Speed);
    float angle=2*FroxelPi*FroxelRandom(seed),z=2*FroxelRandom(seed+1)-1;
    float ring=sqrt(max(0,1-z*z));
    float distance=1+15*Cloud.Distance,spread=4*Cloud.Spread;
    float inner=max(0,distance-spread),outer=distance+spread;
    float radius=pow(lerp(inner*inner*inner,outer*outer*outer,FroxelRandom(seed+4)),1.0/3);
    float3 center=radius*float3(ring*cos(angle),z,ring*sin(angle));
    center=mul(Ry(time*.12),mul(Rx(time*.07),center));
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
