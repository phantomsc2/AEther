#ifndef AETHER_INPUTS
#define AETHER_INPUTS
cbuffer FrameConstants
{
    float T, DT, HistogramShift, AspectRatio;
    float Transient, Motion, MotionVelocity, HistorySpan;
    float PoseX, PoseY, PoseZ, VisualBreath;
    float VisualEnergy, VisualFlash, VisualGlow, VisualSustain;
    float S, HistoryAvailable, HistoryNewestAge, FractalDepth;
    float2 Resolution; float MusicalMovementTime, HistorySeconds;
};
Texture2D<float4> Spectrum0;
Texture2D<float4> Spectrum1;
Texture2D<float4> Histogram0;
Texture2D<float4> Histogram1;
// Signed linear residual is stored in red, before class display gains.
Texture2D<float4> ResidualSpectrum0;
Texture2D<float4> ResidualSpectrum1;
Texture2D<float4> ResidualHistogram0;
Texture2D<float4> ResidualHistogram1;
SamplerState Linear;
#define HullHistory Linear
struct PSin { float4 Position : SV_POSITION; float2 UV : TEXCOORDS; };
PSin VS(uint id : SV_VertexID)
{
    PSin o;
    o.UV = float2((id << 1) & 2, id & 2);
    o.Position = float4(o.UV * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
float3x3 Rx(float a) { return float3x3(1,0,0, 0,cos(a),-sin(a), 0,sin(a),cos(a)); }
float3x3 Ry(float a) { return float3x3(cos(a),0,sin(a), 0,1,0, -sin(a),0,cos(a)); }
float3x3 Rz(float a) { return float3x3(cos(a),-sin(a),0, sin(a),cos(a),0, 0,0,1); }

#endif
