#include "../Common/Inputs.hlsl"

// All controls follow AEther's normalized [0,1] convention.
struct BoxSettings { float Scale; float InnerFoldRadius; float Iterations; };
cbuffer Parameters
{
    // Screenshot defaults: scale -2.152, guarded zero inner radius, 13 iterations.
    BoxSettings Box = { .231, 0, .382 };
    // Fixed volume extent; spacing increases from 6/256 to 6/16 field units.
    float CutScale = 0;
    // Components map to [-2,2]; physical default (-1.468,-.684,.756).
    float3 JuliaSeed = float3(.133,.329,.689);
    // Extinction per fractal-coordinate unit: [0,4], default 1.256.
    float Extinction = .314;
};
Texture2D<float4> SliceImage;

bool JuliaBoxInterior(float3 q)
{
    float3 z=q, seed=4*JuliaSeed-2;
    float scale=8*Box.Scale-4;
    float innerSquared=max(1e-8,Box.InnerFoldRadius*Box.InnerFoldRadius);
    int iterations=1+(int)round(31*Box.Iterations);
    if (dot(z,z)>256) return false;
    [loop] for (int i=0; i<iterations; ++i)
    {
        z=2*clamp(z,-1,1)-z;
        z/=clamp(dot(z,z),innerSquared,1);
        z=scale*z+seed;
        if (dot(z,z)>256) return false;
    }
    return true;
}

struct SliceVarying
{
    float4 Position : SV_POSITION;
    nointerpolation float2 Bounds : TEXCOORD0;
};

SliceVarying SliceVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    uint count=(uint)ceil(exp2(8-4*CutScale));
    SliceVarying o;
    // Increasing instance index walks from the far slab toward the camera.
    o.Bounds=float2(1-2.0*(instance+1)/count,1-2.0*instance/count);
    float2 corner=vertex==0 ? float2(-1,-1) : vertex==1 ? float2(-1,1) : vertex==2 ? float2(1,-1)
        : vertex==3 ? float2(1,-1) : vertex==4 ? float2(-1,1) : float2(1,1);
    // Near-face bounds conservatively cover the entire projected slab, including
    // rays that cross its sides without passing through its midpoint plane.
    float2 projected=corner*2.8/(4+o.Bounds.x)/float2(AspectRatio,1);
    projected+=sign(corner)*2/Resolution;
    o.Position=instance<count ? float4(projected,.5,1) : float4(2,2,.5,1);
    return o;
}

float4 SlicePS(SliceVarying input) : SV_Target
{
    float2 screen=(2*input.Position.xy/Resolution-1)*float2(AspectRatio,-1);
    float3 direction=normalize(float3(screen/2.8,1));
    float3 origin=float3(0,0,-4);
    float begin=(input.Bounds.x+4)/direction.z;
    // Ray starts inside X/Y bounds, before reaching the volume in Z.
    float sideExit=min(1/max(abs(direction.x),1e-20),1/max(abs(direction.y),1e-20));
    float end=min((input.Bounds.y+4)/direction.z,sideExit);
    float distance=max(0,end-begin)*3;
    float3 midpoint=origin+direction*((begin+end)*.5);
    float3 q=3*midpoint+float3(.15,-.2,.37);
    if (distance<=0 || !JuliaBoxInterior(q)) return 0;
    float tau=4*Extinction*distance;
    // Stable 1-exp(-tau) near zero; homogeneous-slab integration otherwise.
    float alpha=tau<.001 ? tau*(1-tau*.5+tau*tau/6) : 1-exp(-tau);
    return float4(float3(.25,.7,1)*alpha,alpha);
}

float4 PS(PSin input) : SV_Target
{
    // Accumulated alpha is physical opacity; final alpha is an output-mode marker.
    return LinearOutput(SliceImage.Load(int3(int2(input.Position.xy),0)).rgb);
}
