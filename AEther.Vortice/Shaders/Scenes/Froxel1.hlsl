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
#include "../Common/VolumeNoise.hlsl"
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
