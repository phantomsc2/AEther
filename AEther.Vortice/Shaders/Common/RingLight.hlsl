#ifndef AETHER_RING_LIGHT
#define AETHER_RING_LIGHT
// Exact softened inverse-square integral for an arc of span <= pi.
// Coordinates: ring(t) = R*(sin(t),-cos(t),0). Intensity is per unit length.
float RingArcWeight(float3 p, float radius, float softening, float begin, float span)
{
    float rho = length(p.xy);
    float axial = p.z*p.z+softening*softening;
    float nearTerm = (radius-rho)*(radius-rho)+axial;
    if (rho == 0) return radius*span/nearTerm;
    float farTerm = (radius+rho)*(radius+rho)+axial;
    float delta = sqrt(nearTerm)*sqrt(farTerm);
    float mid = begin+.5*span-atan2(p.x,-p.y);
    float halfSpan = .5*span;
    // A*cos(h)-B*cos(mid), without subtracting nearly equal A and B.
    float denominator = nearTerm*cos(halfSpan)
        +4*radius*rho*sin(.5*(mid+halfSpan))*sin(.5*(mid-halfSpan));
    return radius*2*atan2(delta*sin(halfSpan),denominator)/delta;
}
float3 RingIncident(float3 world, float radius, float softening)
{
    uint bands, rows; Spectrum0.GetDimensions(bands,rows);
    float span = 3.14159265359/bands;
    float3 sum = 0;
    [loop] for (uint bin=0;bin<bands;++bin)
    {
        float3 left = max(0,Spectrum0.Load(int3(bin,0,0)).rgb);
        float3 right = max(0,Spectrum1.Load(int3(bin,0,0)).rgb);
        if (any(left>0)) sum += left*RingArcWeight(world,radius,softening,-(bin+1.0)*span,span);
        if (any(right>0)) sum += right*RingArcWeight(world,radius,softening,bin*span,span);
    }
    return sum;
}
#endif
