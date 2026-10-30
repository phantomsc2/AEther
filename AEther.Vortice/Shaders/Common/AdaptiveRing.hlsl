#ifndef AETHER_ADAPTIVE_RING
#define AETHER_ADAPTIVE_RING
#include "RingLight.hlsl"
Texture2D<float4> RingPrefix;
RWTexture2D<float4> RingPrefixOutput;
static const float RingPi = 3.14159265359;

// Two tiny serial scans, once per frame; no spectrum-width loop at shading points.
// Each row integrates emission over normalized frequency, with B+1 boundaries.
[numthreads(1,1,1)]
void PrepareRingPrefix(uint3 id : SV_DispatchThreadID)
{
    uint bands, rows; Spectrum0.GetDimensions(bands,rows);
    float3 sum = 0;
    RingPrefixOutput[uint2(0,id.y)] = 0;
    [loop] for(uint b=0;b<bands;++b)
    {
        float3 value = id.y==0 ? Spectrum0.Load(int3(b,0,0)).rgb : Spectrum1.Load(int3(b,0,0)).rgb;
        sum += max(0,value)/bands;
        RingPrefixOutput[uint2(b+1,id.y)] = float4(sum,0);
    }
}
float3 RingPrefixAt(float f,uint side,uint bands)
{
    float x = saturate(f)*bands;
    uint b = min((uint)x,bands-1);
    return lerp(RingPrefix.Load(int3(b,side,0)).rgb,RingPrefix.Load(int3(b+1,side,0)).rgb,x-b);
}
// Periodic primitive of emission, integrating angle/pi from -pi to theta.
float3 RingPrimitive(float theta,uint bands)
{
    float x=(theta+RingPi)/(2*RingPi), turns=floor(x);
    float f=2*frac(x);
    float3 left=RingPrefix.Load(int3(bands,0,0)).rgb;
    float3 right=RingPrefix.Load(int3(bands,1,0)).rgb;
    return turns*(left+right)+left+(f<1 ? -RingPrefixAt(1-f,0,bands) : RingPrefixAt(f-1,1,bands));
}
float3 RingArcAverage(float begin,float end,uint bands)
{
    // Avoid subtracting almost equal prefixes inside a single narrow bin.
    float a=(begin+RingPi)/(2*RingPi), b=(end+RingPi)/(2*RingPi);
    float ca=frac(a)*2*bands, cb=frac(b)*2*bands;
    if(floor(a)==floor(b) && floor(ca)==floor(cb))
    {
        uint cell=min((uint)ca,2*bands-1);
        return cell<bands ? max(0,Spectrum0.Load(int3(bands-1-cell,0,0)).rgb)
                          : max(0,Spectrum1.Load(int3(cell-bands,0,0)).rgb);
    }
    return max(0,(RingPrimitive(end,bands)-RingPrimitive(begin,bands))*(RingPi/max(end-begin,1e-7)));
}
float RingAngle(float u,float k)
{
    if(u<=0) return -RingPi;
    if(u>=1) return RingPi;
    float s,c; sincos(RingPi*(u-.5),s,c);
    return 2*atan2(k*s,c);
}
float RingPartitionAngle(float u,float scale,float logarithm)
{
    float v=2*u-1;
    return sign(v)*scale*(exp2(abs(v)*logarithm)-1);
}
float RingMassCoordinate(float angle,float k)
{
    float s,c; sincos(.5*angle,s,c);
    return .5+atan2(s,k*max(0,c))/RingPi;
}
// Logarithmic angular spacing resolves both the local peak and the distant tail.
// Equal-mass arcs alone smear far-away bright bands into the near-field peak.
// Every coarse arc retains its exact geometric integral; uniform fog light is exact.
float3 AdaptiveRingIncident(float3 p,float radius,float softening,uint arcs,float3 normal,bool surface)
{
    uint width, rows; RingPrefix.GetDimensions(width,rows); uint bands=width-1;
    float rho=length(p.xy), axial=p.z*p.z+softening*softening;
    float nearTerm=(radius-rho)*(radius-rho)+axial;
    float farTerm=(radius+rho)*(radius+rho)+axial;
    float k=sqrt(nearTerm/farTerm);
    float phi=rho>1e-7 ? atan2(p.x,-p.y) : 0;
    float scale=2*k, logarithm=log2(1+RingPi/scale);
    float3 sum=0;
    float lo=-RingPi;
    [loop] for(uint i=0;i<arcs;++i)
    {
        float hi=i+1==arcs ? RingPi : RingPartitionAngle((i+1.0)/arcs,scale,logarithm);
        float begin=phi+lo, end=phi+hi;
        float weight=RingArcWeight(p,radius,softening,begin,hi-lo);
        float3 emission=RingArcAverage(begin,end,bands);
        float cosine=1;
        if(surface)
        {
            float angle=phi+RingAngle(.5*(RingMassCoordinate(lo,k)+RingMassCoordinate(hi,k)),k);
            float s,c; sincos(angle,s,c);
            float3 delta=radius*float3(s,-c,0)-p;
            cosine=max(0,dot(normal,delta)*rsqrt(max(dot(delta,delta),1e-12)));
        }
        sum+=weight*emission*cosine;
        lo=hi;
    }
    return sum;
}
#endif
