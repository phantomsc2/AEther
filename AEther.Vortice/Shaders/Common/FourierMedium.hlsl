#ifndef AETHER_FOURIER_MEDIUM
#define AETHER_FOURIER_MEDIUM

// 125 real degrees of freedom: DC and 62 complex-conjugate mode pairs.
// Pair.xy multiplies cos(k.p), sin(k.p). Geometry aggregation is shared by lights.
cbuffer FourierConstants
{
    float4 FourierModes[63];
    float4 FourierBounds; // common box half-extent, fundamental frequency
};
struct FourierPhase { float4 X; float4 Y; float4 Z; };
float2 ComplexProduct(float2 a, float2 b) { return float2(a.x*b.x-a.y*b.y,a.x*b.y+a.y*b.x); }
float4 PhaseAxis(float c, float s) { return float4(c,s,c*c-s*s,2*c*s); }
FourierPhase MakeFourierPhase(float3 p)
{
    float3 s,c; sincos(FourierBounds.y*p,s,c);
    FourierPhase b; b.X=PhaseAxis(c.x,s.x); b.Y=PhaseAxis(c.y,s.y); b.Z=PhaseAxis(c.z,s.z); return b;
}
float2 AxisMode(float4 b, int n)
{
    if (n==0) return float2(1,0);
    float2 v=abs(n)==1 ? b.xy : b.zw;
    return float2(v.x,n<0 ? -v.y : v.y);
}
int3 ModeFrequency(int i)
{
    int j=i+62;
    return int3(j/25-2,(j/5)%5-2,j%5-2);
}
float2 ModePhase(FourierPhase b, int3 n)
{
    return ComplexProduct(ComplexProduct(AxisMode(b.X,n.x),AxisMode(b.Y,n.y)),AxisMode(b.Z,n.z));
}
void AdvanceFourierPhase(inout FourierPhase b, float3 cosine, float3 sine)
{
    float2 x=ComplexProduct(b.X.xy,float2(cosine.x,sine.x));
    float2 y=ComplexProduct(b.Y.xy,float2(cosine.y,sine.y));
    float2 z=ComplexProduct(b.Z.xy,float2(cosine.z,sine.z));
    b.X=PhaseAxis(x.x,x.y); b.Y=PhaseAxis(y.x,y.y); b.Z=PhaseAxis(z.x,z.y);
}
bool FourierInterval(float3 o, float3 d, out float lo, out float hi)
{
    float3 inverse=(2*step(0,d)-1)/max(abs(d),1e-20);
    float3 a=(-FourierBounds.x-o)*inverse, b=(FourierBounds.x-o)*inverse;
    float3 first=min(a,b),last=max(a,b);
    lo=max(0,max(first.x,max(first.y,first.z))); hi=min(last.x,min(last.y,last.z));
    return hi>lo;
}
float FourierDensity(float3 p)
{
    if (any(abs(p)>=FourierBounds.x)) return 0;
    FourierPhase b=MakeFourierPhase(p);
    float density=0;
    [loop] for (int i=0;i<63;++i) density+=dot(FourierModes[i].xy,ModePhase(b,ModeFrequency(i)));
    return max(0,density);
}
// Elementary line integral of the full finite Fourier expansion. Near zero
// frequency use the endpoint-average identity tan(delta/2)/(delta/2), evaluated
// to FP32 accuracy by a short series. This avoids cancellation and 0/0.
float FourierColumnUnclipped(float3 o, float3 d, float lo, float hi)
{
    if (hi<=lo) return 0;
    float length=hi-lo;
    FourierPhase first=MakeFourierPhase(o+lo*d), last=MakeFourierPhase(o+hi*d);
    float mass=0;
    [loop] for (int i=0;i<63;++i)
    {
        int3 n=ModeFrequency(i);
        float2 a=ModePhase(first,n), b=ModePhase(last,n);
        float frequency=FourierBounds.y*dot(float3(n),d);
        float delta=frequency*length;
        float2 integral;
        if (abs(delta)<.05)
        {
            float d2=delta*delta;
            integral=.5*(a+b)*length*(1+d2*(1.0/12+d2/120));
        }
        else integral=float2(b.y-a.y,a.x-b.x)/frequency;
        mass+=dot(FourierModes[i].xy,integral);
    }
    return max(0,mass);
}
float FourierColumn(float3 o, float3 d, float lo, float hi)
{
    float a,b;
    if (!FourierInterval(o,d,a,b)) return 0;
    return FourierColumnUnclipped(o,d,max(lo,a),min(hi,b));
}
float FourierSinc(float x)
{
    float x2=x*x;
    return abs(x)<.05 ? 1-x2*(1.0/6-x2/120) : sin(x)/x;
}

#endif
