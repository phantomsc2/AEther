cbuffer FrameConstants : register(b0)
{
    float T, DT, HistogramShift, AspectRatio;
    float Transient, Motion, MotionVelocity, HistorySpan;
    float PoseX, PoseY, PoseZ, VisualBreath;
    float VisualEnergy, VisualFlash, VisualGlow, VisualSustain;
    float S; float3 Padding;
};
cbuffer SceneConstants : register(b1)
{
    float SliceDepth, HistoryWarp, TimeCompression, ViewFlags;
};
Texture2D<float4> Spectrum0 : register(t0);
Texture2D<float4> Spectrum1 : register(t1);
Texture2D<float4> Histogram0 : register(t2);
Texture2D<float4> Histogram1 : register(t3);
SamplerState Linear : register(s0);
#define HullHistory Linear
struct PSDefaultin { float4 Position : SV_POSITION; float2 UV : TEXCOORDS; };
PSDefaultin VS(uint id : SV_VertexID)
{
    PSDefaultin o;
    o.UV = float2((id << 1) & 2, id & 2);
    o.Position = float4(o.UV * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
float3x3 Rx(float a) { return float3x3(1,0,0, 0,cos(a),-sin(a), 0,sin(a),cos(a)); }
float3x3 Ry(float a) { return float3x3(cos(a),0,sin(a), 0,1,0, -sin(a),0,cos(a)); }
float3x3 Rz(float a) { return float3x3(cos(a),-sin(a),0, sin(a),cos(a),0, 0,0,1); }
