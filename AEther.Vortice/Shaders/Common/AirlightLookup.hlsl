#ifndef AETHER_AIRLIGHT_LOOKUP
#define AETHER_AIRLIGHT_LOOKUP
Texture2D<float4> RasterAirlightLut;
SamplerState RasterLutSampler;
// State texel 0: completed coefficients/validity; texel 1: requested coefficients/dirty flag.
Texture2D<float4> RasterLookupState;
RWTexture2D<float4> RasterLookupOutputState : register(u0);
RWTexture2D<float4> RasterLookupOutput : register(u0);
static const float LookupMaximumDistance=128, LookupDistanceScale=.0001;
static const float LookupWarp=log(1+LookupMaximumDistance/LookupDistanceScale);

[numthreads(1,1,1)]
void PrepareAirlightLookup(uint3 id : SV_DispatchThreadID)
{
    float3 extinction=AirExtinction();
    float4 previous=RasterLookupOutputState[uint2(0,0)];
    RasterLookupOutputState[uint2(1,0)]=float4(extinction,previous.a==0 || any(previous.rgb!=extinction));
}

// d is angular distance from the positive infinite endpoint. Avoid tan(pi/2).
float LookupDistance(float b, float d)
{
    if (d>=Pi) return 0;
    return d<.5*Pi ? b*(1+cos(d))/sin(d) : b*sin(d)/(1-cos(d));
}

[numthreads(8,8,1)]
void BuildAirlightLookup(uint3 id : SV_DispatchThreadID)
{
    uint w,h; RasterLookupOutput.GetDimensions(w,h);
    if (id.x>=w || id.y>=h) return;
    float4 state=RasterLookupState.Load(int3(1,0,0));
    if (state.a==0) return;
    float b=LookupDistanceScale*(exp(LookupWarp*id.x/(w-1))-1);
    float v=2.0*id.y/(h-1)-1;
    float endpoint=.5*Pi*(1-abs(v))*(1-abs(v));
    float span=v>=0 ? endpoint : Pi-endpoint;
    float3 sum=0;
    if (span>0)
    {
        float start=LookupDistance(b,span);
        const float4 nodes=float4(-.8611363116,-.3399810436,.3399810436,.8611363116);
        const float4 weights=float4(.3478548451,.6521451549,.6521451549,.3478548451);
        // Independent texels integrate their scaled tail with composite Gauss quadrature.
        [loop] for (int panel=0;panel<64;++panel)
        [unroll] for (int j=0;j<4;++j)
        {
            float d=span*(1-(panel+.5+.5*nodes[j])/64);
            float phase=3*(1+cos(d)*cos(d))/(16*Pi);
            float length=b==0 ? 0 : max(0,LookupDistance(b,d)-start);
            sum+=weights[j]*phase*exp(-state.rgb*length);
        }
        sum*=span/(2*64);
    }
    RasterLookupOutput[id.xy]=float4(sum,0);
}

[numthreads(1,1,1)]
void CommitAirlightLookup(uint3 id : SV_DispatchThreadID)
{
    RasterLookupOutputState[uint2(0,0)]=float4(RasterLookupOutputState[uint2(1,0)].rgb,1);
}
#endif
