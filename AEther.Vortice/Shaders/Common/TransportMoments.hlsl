#ifndef TRANSPORT_MOMENTS_HLSL
#define TRANSPORT_MOMENTS_HLSL

// Four real coefficients in either basis. Reconstruction adapted from the CC0
// MBOIT reference shaders by Muenstermann, Krumpen, Klein and Peters (2018):
// https://momentsingraphics.de/I3D2018.html
// Return cumulative mass fractions, applying the RGB extinction separately.
static const float MomentPi = 3.14159265359;
static const float MomentBias = 2e-5;
static const float MomentArc = .95 * MomentPi; // Leave pi/10 for wrapping.

float MomentSafe(float x) { return (x < 0 ? -1 : 1) * max(abs(x), 1e-15); }
float2 MomentConjugate(float2 z) { return float2(z.x, -z.y); }
float2 MomentMultiply(float2 a, float2 b)
{
    return float2(a.x*b.x-a.y*b.y, a.x*b.y+a.y*b.x);
}
float2 MomentDivide(float2 a, float2 b)
{
    return MomentMultiply(a,MomentConjugate(b))/max(dot(b,b),1e-25);
}
float2 MomentSquareRoot(float2 z)
{
    float r=length(z);
    return float2(sqrt(max(0,.5*(r+z.x))),
        (z.y<0 ? -1 : 1)*sqrt(max(0,.5*(r-z.x))));
}
float MomentCircleParameter(float2 z)
{
    float p=abs(z.y)-abs(z.x);
    p=z.x<0 ? 2-p : p;
    return z.y<0 ? 6-p : p;
}
float MomentRootWeight(float reference, float2 root)
{
    float p=MomentCircleParameter(root);
    float wrapStart=6+cos(.1*MomentPi)-sin(.1*MomentPi);
    return (p<reference ? 1 : 0)+saturate((p-wrapStart)/(7-wrapStart));
}
float4 MomentBasis(float depth, bool trigonometric)
{
    if (trigonometric)
    {
        float2 z;
        sincos(MomentArc*(depth+1),z.y,z.x);
        return float4(z,MomentMultiply(z,z));
    }
    float d2=depth*depth;
    return float4(depth,d2,d2*depth,d2*d2);
}

float PowerMomentFraction(float4 moments, float depth)
{
    // A small mixture with the uniform distribution makes the Hankel matrix
    // positive definite even for empty gaps and very narrow occupied intervals.
    float4 b=lerp(moments,float4(0,1.0/3,0,1.0/5),MomentBias);
    float d11=max(1e-12,b.y-b.x*b.x);
    float l21d11=b.z-b.x*b.y;
    float l21=l21d11/d11;
    float d22=max(1e-12,b.w-b.y*b.y-l21d11*l21);
    float3 c=float3(1,depth,depth*depth);
    c.y-=b.x;
    c.z-=b.y+l21*c.y;
    c.y/=d11;
    c.z/=d22;
    c.y-=l21*c.z;
    c.x-=dot(c.yz,b.xy);
    c/=max(1e-20,max(abs(c.x),max(abs(c.y),abs(c.z))));

    float fraction;
    if (abs(c.z)<1e-7)
    {
        // Limit of the canonical reconstruction when one root goes to infinity.
        float root=-c.x/MomentSafe(c.y);
        float weight=saturate((b.x-depth)/MomentSafe(root-depth));
        fraction=.5*(1-weight)+(root<depth ? weight : 0);
    }
    else
    {
        float discriminant=sqrt(max(0,c.y*c.y-4*c.x*c.z));
        float q=-.5*(c.y+(c.y<0 ? -discriminant : discriminant));
        float z1=q/MomentSafe(c.z), z2=c.x/MomentSafe(q);
        float f0=.5, f1=z1<depth ? 1 : 0, f2=z2<depth ? 1 : 0;
        float f01=(f1-f0)/MomentSafe(z1-depth);
        float f12=(f2-f1)/MomentSafe(z2-z1);
        float f012=(f12-f01)/MomentSafe(z2-depth);
        float p1=f01-f012*z1-f012*depth;
        float p0=f0-(f01-f012*z1)*depth;
        fraction=p0+b.x*p1+b.y*f012;
    }
    // Two-moment bounds provide a finite limit for rare arithmetic degeneracies.
    float delta=depth-b.x;
    float bound=d11/(d11+delta*delta);
    return isfinite(fraction) ? saturate(fraction) : (delta<0 ? .5*bound : 1-.5*bound);
}

float TrigonometricMomentFraction(float4 moments, float depth)
{
    float2 b1=moments.xy*(1-MomentBias), b2=moments.zw*(1-MomentBias);
    float d11=max(1e-12,1-dot(b1,b1));
    float2 l21=(b1-MomentMultiply(b2,MomentConjugate(b1)))/d11;
    float d22=max(1e-12,1-dot(b2,b2)-d11*dot(l21,l21));
    float2 z;
    sincos(MomentArc*(depth+1),z.y,z.x);
    float2 c0=float2(1,0), c1=z-b1;
    float2 c2=MomentMultiply(z,z)-b2-MomentMultiply(l21,c1);
    c1/=d11;
    c2/=d22;
    c1-=MomentMultiply(MomentConjugate(l21),c2);
    c0-=MomentMultiply(MomentConjugate(b1),c1)+MomentMultiply(MomentConjugate(b2),c2);
    float scale=max(1e-20,max(length(c0),max(length(c1),length(c2))));
    c0=MomentConjugate(c0)/scale;
    c1=MomentConjugate(c1)/scale;
    c2=MomentConjugate(c2)/scale;
    float reference=MomentCircleParameter(z);
    float fraction;
    if (dot(c2,c2)<1e-14)
    {
        float2 root=-MomentDivide(c0,c1);
        root/=max(length(root),1e-12);
        float weight=MomentRootWeight(reference,root);
        float2 p1=MomentDivide(float2(weight-.5,0),root-z);
        fraction=(float2(.5,0)+MomentMultiply(b1-z,p1)).x;
    }
    else
    {
        float2 middle=.5*MomentDivide(c1,c2), constant=MomentDivide(c0,c2);
        float2 discriminant=MomentSquareRoot(MomentMultiply(middle,middle)-constant);
        float2 q1=-middle-discriminant, q2=-middle+discriminant;
        float2 root1=dot(q1,q1)>dot(q2,q2) ? q1 : q2;
        float2 root2=MomentDivide(constant,root1);
        root1/=max(length(root1),1e-12);
        root2/=max(length(root2),1e-12);
        float f1=MomentRootWeight(reference,root1), f2=MomentRootWeight(reference,root2);
        float2 f01=MomentDivide(float2(f1-.5,0),root1-z);
        float2 f12=MomentDivide(float2(f2-f1,0),root2-root1);
        float2 f012=MomentDivide(f12-f01,root2-z);
        float2 p1=f01-MomentMultiply(f012,root1+z);
        float2 p0=float2(.5,0)-MomentMultiply(f01-MomentMultiply(f012,root1),z);
        fraction=(p0+MomentMultiply(b1,p1)+MomentMultiply(b2,f012)).x;
    }
    return isfinite(fraction) ? saturate(fraction) : saturate(.5*(depth+1));
}

float MomentFraction(float4 moments, float depth, bool trigonometric)
{
    if (depth<=-1) return 0;
    if (depth>=1) return 1;
    return trigonometric ? TrigonometricMomentFraction(moments,depth)
        : PowerMomentFraction(moments,depth);
}
#endif
