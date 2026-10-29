#ifndef AETHER_POINT_AIRLIGHT_INTEGRAL
#define AETHER_POINT_AIRLIGHT_INTEGRAL
// Homogeneous point-light scattering using the GPU-generated RGB tail table.
// Shares table coordinates and resources with AirlightLookup.hlsl.
static const float4 PointNodes=float4(.1834346425,.5255324099,.7966664774,.9602898565);
static const float4 PointWeights=float4(.3626837834,.3137066459,.2223810345,.1012285363);
float3 PointTail(float c, float b2, float height, float t, float q, float4 layout, float3 extinction)
{
    float x=t-c;
    // atan2(b,abs(x)) computes the angular distance from an endpoint without
    // subtracting two nearly equal pi/2 values for a nearly collinear ray.
    float endpoint=atan2(height,abs(x));
    float v=.5+.5*sign(x)*(1-sqrt(2*endpoint/Pi));
    float2 uv=float2(q,v)*layout.xy+layout.zw;
    float3 tail=RasterAirlightLut.SampleLevel(RasterLutSampler,uv,0).rgb;
    // The table stores b*tail. Factoring 1/b out preserves the true singularity
    // and gives the b=0 table column a finite limit, with no soft source radius.
    return tail*exp(-extinction*(t+sqrt(x*x+b2)))/height;
}
float3 CollinearPointIntegral(float c, float begin, float stop, float3 extinction)
{
    const float phase=3/(8*Pi);
    if (c>stop)
    {
        // Before the source, camera+light distance is the constant c.
        float mass=rcp(max(c-stop,NumericDistance))-rcp(max(c-begin,NumericDistance));
        return phase*mass*exp(-extinction*c);
    }
    // After the source, substitute u=1/(t-c), exactly cancelling 1/r^2.
    float lo=stop<1e30 ? rcp(max(stop-c,NumericDistance)) : 0;
    float hi=rcp(max(begin-c,NumericDistance));
    float3 sum=0;
    [unroll] for (int panel=0;panel<2;++panel)
    [unroll] for (int node=0;node<4;++node)
    [unroll] for (int side=-1;side<=1;side+=2)
    {
        float u=lerp(lo,hi,(panel+.5+.5*side*PointNodes[node])*.5);
        float r=rcp(max(u,1e-30)),t=c+r;
        sum+=PointWeights[node]*exp(-extinction*(t+r));
    }
    return phase*sum*((hi-lo)*.25);
}

float3 PointIntegral(float c, float b2, float begin, float stop, float3 extinction, float4 layout)
{
    float b=sqrt(b2),H=max(b,NumericDistance);
    float3 integral=0;
    [branch] if (b==0 || (c<begin && b<1e-5*(begin-c)) || (stop<1e30 && c>stop && b<1e-5*(c-stop)))
        integral=CollinearPointIntegral(c,begin,stop,extinction);
    else if (b<=LookupMaximumDistance)
    {
        float q=log(1+b/LookupDistanceScale)/LookupWarp;
        integral=PointTail(c,b2,H,begin,q,layout,extinction);
        if (stop<1e30) integral=max(0,integral-PointTail(c,b2,H,stop,q,layout,extinction));
    }
    else
    {
        // Outside the table domain, use direct angular quadrature.
        float a=atan((begin-c)/H),span=(stop<1e30 ? atan((stop-c)/H) : .5*Pi)-a;
        [loop] for (int j=0;j<16;++j)
        {
            float t=clamp(c+H*tan(a+(j+.5)*span/16),begin,stop);
            float x=t-c,r2=x*x+b2;
            float phase=3*(2-b2/max(r2,NumericDistance2))/(16*Pi);
            integral+=phase*exp(-extinction*(t+sqrt(r2)));
        }
        integral*=span/(16*H);
    }
    return integral;
}
#endif
