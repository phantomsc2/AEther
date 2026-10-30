#ifndef AETHER_UNIFORM_RING
#define AETHER_UNIFORM_RING
// Local XY rings in world XY, YZ and ZX planes. These permutations are inverses.
float3 RingLocal(float3 p,uint i) { return i==0 ? p : i==1 ? p.yzx : p.zxy; }
float3 RingWorld(float3 p,uint i) { return i==0 ? p : i==1 ? p.zxy : p.yzx; }
float UniformRingWeight(float3 p,float radius,float softening)
{
    float rho=length(p.xy), axial=p.z*p.z+softening*softening;
    float nearTerm=(radius-rho)*(radius-rho)+axial;
    float farTerm=(radius+rho)*(radius+rho)+axial;
    return 6.28318530718*radius/(sqrt(nearTerm)*sqrt(farTerm));
}
// Average illumination across a camera segment. Approximate curvature locally,
// but integrate the tangent-line 1/sqrt(distance^2+a^2) peak analytically.
float UniformRingAngularAverage(float3 p,float3 direction,float radius,float softening,float g,bool phase);
float UniformRingSegmentPhase(float3 begin,float3 end,float radius,float softening,float3 outgoing,float g)
{
    float3 delta=end-begin;
    float lengthSquared=dot(delta,delta);
    if(lengthSquared<1e-12) return UniformRingWeight(begin,radius,softening)
        *UniformRingAngularAverage(begin,outgoing,radius,softening,g,true);
    float3 midpoint=(begin+end)*.5;
    float rho=length(midpoint.xy);
    float d=length(float2(rho-radius,midpoint.z));
    // Limit tangent sag relative to core width, only subdividing near the ring.
    uint pieces=(uint)clamp(ceil(sqrt(lengthSquared/(2*radius*max(softening,d-.5*sqrt(lengthSquared))))),1,8);
    float3 step=delta/pieces;
    float sum=0;
    [loop] for(uint i=0;i<pieces;++i)
    {
        float3 p=begin+(i+.5)*step;
        float pr=length(p.xy);
        if(pr<1e-5) {
            sum+=UniformRingWeight(p,radius,softening)*UniformRingAngularAverage(p,outgoing,radius,softening,g,true);
            continue;
        }
        float2 radial=p.xy/pr;
        float3 tangent=float3(-radial.y,radial.x,0);
        float3 q=p-float3(radius*radial,0);
        float3 v=step-tangent*dot(step,tangent);
        q-=tangent*dot(q,tangent);
        // Refine the tangent anchor toward closest approach within this piece.
        float vv=dot(v,v);
        float t=vv>1e-12 ? clamp(-dot(q,v)/vv,-.5,.5) : 0;
        float3 anchor=p+t*step;
        radial=anchor.xy/max(length(anchor.xy),1e-6);
        tangent=float3(-radial.y,radial.x,0);
        q=p-float3(radius*radial,0); q-=tangent*dot(q,tangent);
        v=step-tangent*dot(step,tangent); vv=dot(v,v);
        float average;
        if(vv<1e-10) average=rsqrt(dot(q,q)+softening*softening);
        else
        {
            float speed=sqrt(vv), center=dot(q,v)/vv;
            float3 perpendicular=q-v*center;
            float b=sqrt(dot(perpendicular,perpendicular)+softening*softening);
            float lo=speed*(center-.5)/b, hi=speed*(center+.5)/b;
            // Stable asinh difference: avoid subtracting nearly equal large logs.
            if(abs(hi-lo)<.001*max(1,abs((hi+lo)*.5)))
                average=rsqrt(dot(q,q)+softening*softening);
            else
            {
                float ah=sign(hi)*log(abs(hi)+sqrt(1+hi*hi));
                float al=sign(lo)*log(abs(lo)+sqrt(1+lo*lo));
                average=(ah-al)/speed;
            }
        }
        float ar=length(anchor.xy);
        float farTerm=(radius+ar)*(radius+ar)+anchor.z*anchor.z+softening*softening;
        sum+=6.28318530718*radius*rsqrt(farTerm)*average
            *UniformRingAngularAverage(anchor,outgoing,radius,softening,g,true);
    }
    return sum/pieces;
}
float UniformRingSegment(float3 begin,float3 end,float radius,float softening)
{
    return UniformRingSegmentPhase(begin,end,radius,softening,0,0);
}
// HG relative to isotropic 1/(4*pi). Positive g follows light propagation.
float RingPhaseRelative(float cosine,float g)
{
    // +/-1 are delta distributions: approach them with a finite narrow lobe.
    g=clamp(g,-.9999,.9999);
    float ag=abs(g), gap=1-ag;
    float d=gap*gap+2*ag*(1-sign(g)*clamp(cosine,-1,1));
    return (1-g*g)/(d*sqrt(d));
}
// Identical inverse-square-mass quadrature for Lambert and phase.
// For phase, direction points from the scattering position toward the camera.
float UniformRingAngularAverage(float3 p,float3 direction,float radius,float softening,float g,bool phase)
{
    if(phase && g==0) return 1;
    float rho=length(p.xy), axial=p.z*p.z+softening*softening;
    float nearTerm=(radius-rho)*(radius-rho)+axial;
    float farTerm=(radius+rho)*(radius+rho)+axial;
    float k=sqrt(nearTerm/farTerm);
    float2 radial=rho>1e-7 ? p.xy/rho : float2(1,0);
    float2 tangent=float2(-radial.y,radial.x);
    // tan(pi*(u-.5)), u=(i+.5)/8. Inverse ring CDF, no runtime trig.
    static const float tangents[8]={-5.027339492,-1.496605763,-.668178638,-.198912367,
                                   .198912367,.668178638,1.496605763,5.027339492};
    float sum=0;
    [unroll] for(uint i=0;i<8;++i)
    {
        float h=k*tangents[i], d=1+h*h;
        float2 xy=radius*((1-h*h)*radial+2*h*tangent)/d;
        float3 delta=float3(xy,0)-p;
        float cosine=dot(direction,delta)*rsqrt(max(dot(delta,delta),1e-12));
        sum+=phase ? RingPhaseRelative(-cosine,g) : max(0,cosine);
    }
    return sum/8;
}
float UniformRingDiffuse(float3 p,float3 normal,float radius,float softening)
{
    return UniformRingWeight(p,radius,softening)*UniformRingAngularAverage(p,normal,radius,softening,0,false);
}
#endif
