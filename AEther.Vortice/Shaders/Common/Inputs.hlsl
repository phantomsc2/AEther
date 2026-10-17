#ifndef AETHER_INPUTS
#define AETHER_INPUTS
cbuffer FrameConstants
{
    float T, DT, HistogramShift, AspectRatio;
    float Transient, ShapeTime, DistortionMomentum, HistorySpan;
    float PoseX, PoseY, PoseZ, VisualBreath;
    float VisualEnergy, VisualFlash, VisualGlow, VisualSustain;
    float S, HistoryAvailable, HistoryNewestAge, FractalDepth;
    float2 Resolution; float MusicalMovementTime, HistorySeconds;
    // Raw amplitude units: (current S RMS, signed broadband T, rectangle mean B, interaction RMS).
    float4 GlobalSTBR;
};
Texture2D<float4> Spectrum0;
Texture2D<float4> Spectrum1;
Texture2D<float4> Histogram0;
Texture2D<float4> Histogram1;
Texture2D<float4> GlobalHistogram;
SamplerState Linear;
#define HullHistory Linear
// Age is in seconds. Shares the spectral history's coordinates, masks and interpolation.
// Use this for historical modulation or exact row-aligned local B - global B.
float4 GlobalSTBRAtAge(float seconds)
{
    float age = max(0, seconds) / max(HistorySeconds, 1e-6) * HistorySpan;
    if (age > HistoryAvailable || age < HistoryNewestAge) return 0;
    return GlobalHistogram.SampleLevel(Linear, float2(.5, HistogramShift - age), 0);
}
// Renderer sensitivity h must be positive. Zero remains zero; negative T has a separate response.
float GlobalAmplitudeResponse(float amplitude, float h)
{
    float x = max(0, amplitude);
    // This form also stays bounded for the largest finite float magnitudes.
    return x > h ? 1 / (1 + h / x) : x / (h + x);
}
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
