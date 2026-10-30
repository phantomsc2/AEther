#ifndef AETHER_VOLUME_NOISE
#define AETHER_VOLUME_NOISE
float3 NoiseGradient(int3 cell)
{
    uint h = FroxelHash(asuint(cell.x) ^ FroxelHash(asuint(cell.y)) ^ FroxelHash(asuint(cell.z)+0x9e3779b9));
    // Twelve edge directions, all unit length.
    uint axis = h%3;
    float a = (h&8) ? .70710678 : -.70710678;
    float b = (h&16) ? .70710678 : -.70710678;
    return axis==0 ? float3(0,a,b) : axis==1 ? float3(a,0,b) : float3(a,b,0);
}
float VolumeNoise(float3 p)
{
    int3 cell = (int3)floor(p); float3 f = frac(p);
    float3 w = f*f*f*(f*(f*6-15)+10);
    float result = 0;
    [unroll] for (int z=0;z<2;++z)
    [unroll] for (int y=0;y<2;++y)
    [unroll] for (int x=0;x<2;++x)
    {
        int3 offset = int3(x,y,z);
        float3 weight = lerp(1-w,w,(float3)offset);
        result += weight.x*weight.y*weight.z*dot(NoiseGradient(cell+offset),f-offset);
    }
    return clamp(result*1.6,-1,1);
}

#endif
