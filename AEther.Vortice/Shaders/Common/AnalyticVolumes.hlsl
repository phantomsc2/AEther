#ifndef AETHER_ANALYTIC_VOLUMES
#define AETHER_ANALYTIC_VOLUMES

// A compact ellipsoid has rho = strength * max(0, 1-|local(p)|^2)^3.
// On an intersecting ray this becomes peak * (1-z*z)^3, -1 < z < 1.
// Chord = (center distance, inverse half-length, peak, full column density).
float4 EllipsoidChord(float3 origin, float3 direction, float3 center, float3 inverseRadii, float strength)
{
    float3 o=(origin-center)*inverseRadii;
    float3 d=direction*inverseRadii;
    float a=dot(d,d);
    float middle=-dot(o,d)/max(a,1e-20);
    float3 perpendicular=o+middle*d;
    float height=1-dot(perpendicular,perpendicular);
    if (height<=0 || strength<=0) return 0;
    float halfLength=sqrt(height/a);
    float peak=strength*height*height*height;
    return float4(middle,1/halfLength,peak,peak*halfLength*(32.0/35.0));
}

// Normalized integral from -1 to z: a Beta(4,4) CDF after y=(z+1)/2.
// Evaluate the small tail directly; differences near either endpoint then avoid
// subtracting two numbers close to one. No numerical ray integration is used.
float BlobLowerTail(float z)
{
    float y=saturate(.5*(z+1));
    float y2=y*y;
    return y2*y2*(35+y*(-84+y*(70-20*y)));
}
float BlobFraction(float lo, float hi)
{
    if (hi<=lo || hi<=-1 || lo>=1) return 0;
    if (lo<=-1 && hi>=1) return 1;
    if (lo<=-1) return hi<=0 ? BlobLowerTail(hi) : 1-BlobLowerTail(-hi);
    if (hi>=1) return lo>=0 ? BlobLowerTail(-lo) : 1-BlobLowerTail(lo);
    lo=clamp(lo,-1,1); hi=clamp(hi,-1,1);
    if (lo>=0) return max(0,BlobLowerTail(-lo)-BlobLowerTail(-hi));
    if (hi<=0) return max(0,BlobLowerTail(hi)-BlobLowerTail(lo));
    return max(0,1-BlobLowerTail(lo)-BlobLowerTail(-hi));
}
float BlobColumn(float4 chord, float lo, float hi)
{
    if (chord.w<=0 || hi<=lo) return 0;
    return chord.w*BlobFraction((lo-chord.x)*chord.y,(hi-chord.x)*chord.y);
}
float BlobDensity(float4 chord, float t)
{
    float z=(t-chord.x)*chord.y;
    float h=max(0,1-z*z);
    return chord.z*h*h*h;
}

// Cheap normalized tent proposal on the same chord. A rational CDF warp below
// can favor the near side without iterative inversion or additional exp/log.
float BlobTentCdf(float z)
{
    z=clamp(z,-1,1);
    return z<0 ? .5*(1+z)*(1+z) : 1-.5*(1-z)*(1-z);
}
float BlobTentInverse(float u)
{
    return u<.5 ? sqrt(2*u)-1 : 1-sqrt(2*(1-u));
}

#endif
