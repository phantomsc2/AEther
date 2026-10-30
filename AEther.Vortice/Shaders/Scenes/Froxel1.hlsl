#include "../Common/Froxel.hlsl"

struct MediumSettings { float Density; float3 Albedo; float NoiseScale; float Contrast; float Speed; float Anisotropy; };
struct LightSettings { float Intensity; float Speed; float Radius; };
struct CameraSettings { float Distance; float Yaw; float Pitch; };
cbuffer Parameters
{
    MediumSettings Medium = { .5, float3(.95,.95,.95), .4, .8, .3, .8125 };
    LightSettings Lights = { .4, .3, .3 };
    CameraSettings Camera = { .42105263, .5, .57 };
};
ProjectionCamera FroxelCamera()
{
    float yaw = (Camera.Yaw-.5)*2*FroxelPi, pitch = (Camera.Pitch-.5)*(160*FroxelPi/180);
    float3 outward = float3(sin(yaw)*cos(pitch),sin(pitch),-cos(yaw)*cos(pitch));
    float3 forward = -outward, right = normalize(cross(float3(0,1,0),forward));
    return MakeProjectionCamera(outward*lerp(2,40,Camera.Distance),forward,right,cross(forward,right));
}
float3 NoiseGradient(int3 cell)
{
    uint h = FroxelHash(asuint(cell.x) ^ FroxelHash(asuint(cell.y)) ^ FroxelHash(asuint(cell.z)+0x9e3779b9));
    // Twelve edge directions, all unit length.
    uint axis = h%3;
    float a = (h&8) ? .70710678 : -.70710678;
    float b = (h&16) ? .70710678 : -.70710678;
    return axis==0 ? float3(0,a,b) : axis==1 ? float3(a,0,b) : float3(a,b,0);
}
float VolumeNoise(float3 p)
{
    int3 cell = (int3)floor(p); float3 f = frac(p);
    float3 w = f*f*f*(f*(f*6-15)+10);
    float result = 0;
    [unroll] for (int z=0;z<2;++z)
    [unroll] for (int y=0;y<2;++y)
    [unroll] for (int x=0;x<2;++x)
    {
        int3 offset = int3(x,y,z);
        float3 weight = lerp(1-w,w,(float3)offset);
        result += weight.x*weight.y*weight.z*dot(NoiseGradient(cell+offset),f-offset);
    }
    return clamp(result*1.6,-1,1);
}
float MeanExtinction() { return .6*Medium.Density*Medium.Density; }
FroxelMedium EvaluateFroxelMedium(float3 world)
{
    float time = T*Medium.Speed;
    float noise = VolumeNoise(world*lerp(.1,1.5,Medium.NoiseScale)+time*float3(.19,-.11,.13));
    float envelope = 1-smoothstep(6,8,length(world));
    FroxelMedium m;
    m.Extinction = MeanExtinction()*max(0,1+Medium.Contrast*noise)*envelope;
    m.Scattering = Medium.Albedo*m.Extinction;
    m.Anisotropy = 1.6*(Medium.Anisotropy-.5);
    return m;
}
float FroxelLightTransmittance(float3 world,float3 light)
{
    float3 delta = world-light; float distance = length(delta);
    if (distance < 1e-6) return 1;
    float3 direction = delta/distance;
    float b = dot(light,direction), c = dot(light,light)-64;
    float discriminant = b*b-c;
    if (discriminant <= 0) return 1;
    float root = sqrt(discriminant);
    float inside = max(0,min(distance,-b+root)-max(0,-b-root));
    // Mean-density approximation: intentionally no heterogeneous shadow march.
    return exp(-MeanExtinction()*inside);
}
float FroxelSourceRadius() { return .05+.95*Lights.Radius*Lights.Radius; }
FroxelLight EvaluateFroxelLight(uint index)
{
    float phase = 2*FroxelPi*(index+.25)/max(1,FroxelLightCount);
    float t = T*Lights.Speed;
    float angle = phase+t*(.4+.05*(index%3));
    FroxelLight light;
    light.Position = float3(4.5*cos(angle),3*sin(phase*1.7+t*.31),4.5*sin(angle));
    float3 color = .55+.45*cos(phase+float3(0,2.0943951,4.1887902));
    float pulse = .5+.5*sin(phase+t*3);
    light.Intensity = 5000*Lights.Intensity*Lights.Intensity*color*pulse*pulse;
    return light;
}
float4 PS(PSin input) : SV_Target { return LinearOutput(FroxelComposite(input)); }
