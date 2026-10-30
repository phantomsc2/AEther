#ifndef AETHER_LINE_LIGHT
#define AETHER_LINE_LIGHT
static const float LinePi=3.14159265359;
// A normalized axis through Origin; intensity is per unit length.
struct LineLight { float3 Origin; float Softening; float3 Axis; float Padding0; float3 Intensity; float Padding1; };
StructuredBuffer<LineLight> LineLights;
RWStructuredBuffer<LineLight> LineLightOutput;
float LineWeight(float3 p,LineLight light)
{
    float3 relative=p-light.Origin;
    float axial=dot(relative,light.Axis);
    float3 perpendicular=relative-axial*light.Axis;
    float b=sqrt(dot(perpendicular,perpendicular)+light.Softening*light.Softening);
    return LinePi/b;
}
// Eight uniform-angle samples carry equal softened inverse-square weight.
// This quadrature is only needed for Lambert surfaces; fog is isotropic.
float LineDiffuseAverage(float3 p,LineLight light,float3 normal)
{
    float3 relative=p-light.Origin;
    float axial=dot(relative,light.Axis);
    float3 perpendicular=relative-axial*light.Axis;
    float b=sqrt(dot(perpendicular,perpendicular)+light.Softening*light.Softening);
    // Infinite-line angular bounds are constant; no per-light endpoint trig.
    float angle=-LinePi*.5,span=LinePi;
    float s,c,stepS,stepC;
    sincos(angle+span/16,s,c);sincos(span/8,stepS,stepC);
    float sum=0;
    [unroll] for(uint i=0;i<8;++i)
    {
        float3 delta=light.Axis*(b*s)-perpendicular*c;
        float cosine=dot(normal,delta)*rsqrt(max(dot(delta,delta),1e-12));
        sum+=max(0,cosine);
        float nextS=s*stepC+c*stepS;
        c=c*stepC-s*stepS;s=nextS;
    }
    return sum/8;
}
float LineAsinh(float x) { return sign(x)*log(abs(x)+sqrt(1+x*x)); }
// Average of the infinite-axis core along a camera segment, without angular factor.
float LineCoreAverage(float3 begin,float3 end,LineLight light)
{
    float3 q=begin-light.Origin,v=end-begin;
    q-=light.Axis*dot(q,light.Axis);v-=light.Axis*dot(v,light.Axis);
    float vv=dot(v,v);
    if(vv<1e-12) return rsqrt(dot(q,q)+light.Softening*light.Softening);
    float center=dot(q,v)/vv,speed=sqrt(vv);
    float3 perpendicular=q-center*v;
    float b=sqrt(dot(perpendicular,perpendicular)+light.Softening*light.Softening);
    float lo=speed*center/b,hi=speed*(center+1)/b;
    if(abs(hi-lo)<.001*max(1,abs((hi+lo)*.5)))
    {
        float3 mid=q+.5*v;
        return rsqrt(dot(mid,mid)+light.Softening*light.Softening);
    }
    return (LineAsinh(hi)-LineAsinh(lo))/speed;
}
// The full infinite-line peak is analytic, including the parallel-ray limit.
float LineSegmentAverage(float3 begin,float3 end,LineLight light)
{
    return LinePi*LineCoreAverage(begin,end,light);
}
#endif
