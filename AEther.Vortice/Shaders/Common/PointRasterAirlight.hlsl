#ifndef AETHER_POINT_RASTER_AIRLIGHT
#define AETHER_POINT_RASTER_AIRLIGHT
#include "Projection.hlsl"
Texture2D<float4> PointSurface;
Texture2D<float> PointDepth;
Texture2D<float4> PointAirlight;
Texture2D<float4> RasterAirlightLut;
SamplerState RasterLutSampler;
cbuffer RasterAirlightConstants : register(b5) { float4 RasterLookupInfo; };
static const float Pi=3.14159265359;
// Numerical denominator guards only, far below world-coordinate precision.
// No world-space source radius or smoothing is applied to either light kernel.
static const float NumericDistance2=1e-30,NumericDistance=1e-15,HdrMaximum=3.402823466e38;
float3 StoreRadiance(float3 value) { return min(max(value,0),HdrMaximum); }
float3 SingularRadiance(float3 intensity)
{
    return float3(intensity.x>0,intensity.y>0,intensity.z>0)*HdrMaximum;
}
uint RasterHash(uint v) { v^=v>>16; v*=0x7feb352d; v^=v>>15; v*=0x846ca68b; return v^(v>>16); }
float Random(uint v) { return ((RasterHash(v)>>9)+.5)/8388608.0; }
float3 CubeDirection(int face, float2 uv)
{
    float2 q=2*uv-1;
    if (face==0) return float3(1,-q.y,-q.x);
    if (face==1) return float3(-1,-q.y,q.x);
    if (face==2) return float3(q.x,1,q.y);
    if (face==3) return float3(q.x,-1,-q.y);
    if (face==4) return float3(q.x,-q.y,1);
    return float3(-q.x,-q.y,-1);
}

float3 AirScatter() { return .3*Air.Density*Air.Density*Air.Scattering; }
float3 AirExtinction() { return .3*Air.Density*Air.Density*(Air.Scattering+Air.Absorption); }
#include "PointAirlightIntegral.hlsl"

float4 GeometryProject(float3 world)
{
    return ProjectionClip(SceneCamera(),world);
}
float4 PointSurfacePS(GeometryOutput input) : SV_Target
{
    float3 sum=0,extinction=AirExtinction();
    uint count=PointLightCount.Load(0);
    [loop] for (uint i=0;i<count;++i)
    {
        PointLight light=PointLights[i];
        if (!any(light.Intensity>0)) continue;
        float3 delta=light.Position-input.World;
        float r2=dot(delta,delta),r=sqrt(r2);
        if (r2==0) { sum+=SingularRadiance(input.Albedo*light.Intensity); continue; }
        float diffuse=max(0,dot(input.Normal,delta/r))/Pi;
        sum+=input.Albedo*diffuse*exp(-extinction*r)*light.Intensity/max(r2,NumericDistance2);
    }
    return float4(StoreRadiance(sum),1);
}
void PointRay(int2 pixel, out float3 origin, out float3 direction, out bool hit, out float end)
{
    ProjectionCamera camera=SceneCamera();
    origin=camera.Origin; direction=ProjectionDirection(camera,(pixel+.5)/Resolution);
    float z=PointDepth.Load(int3(pixel,0)); hit=z<1;
    end=hit ? ProjectionDepthDistance(camera,direction,z) : 0;
}
float3 PointAirlightContribution(PointLight light, float3 origin, float3 direction, float stop,
    float3 scatter, float3 extinction, float4 layout)
{
    float3 delta=light.Position-origin;
    float c=dot(delta,direction);
    float3 perpendicular=delta-c*direction;
    float b2=dot(perpendicular,perpendicular);
    if (b2==0 && c>=0 && c<=stop) return SingularRadiance(scatter*light.Intensity);
    return scatter*light.Intensity*PointIntegral(c,b2,0,stop,extinction,layout);
}
float3 SumPointAirlight(uint count, float3 origin, float3 direction, float stop,
    float3 scatter, float3 extinction, float4 layout)
{
    float3 sum=0;
    [loop] for (uint i=0;i<count;++i)
    {
        PointLight light=PointLights[i];
        if (any(light.Intensity>0)) sum+=PointAirlightContribution(light,origin,direction,stop,scatter,extinction,layout);
    }
    return sum;
}
// Only the proposal is softened/clamped. Selected sources use the full point kernel.
float PointRayWeight(PointLight light, float3 origin, float3 direction, float stop, float rate, out float distance2)
{
    float3 delta=light.Position-origin;
    float t=clamp(dot(delta,direction),0,stop);
    float3 offset=delta-t*direction;
    distance2=dot(offset,offset);
    float r=sqrt(max(distance2,1e-8));
    float brightness=min(dot(light.Intensity,float3(.2126,.7152,.0722)),1e20);
    return brightness*exp(-min(rate*(t+r),80))/r;
}
// Indices are discrete: interpolate estimator coefficients, never light indices.
struct PointLightSuggestion { uint Index; float Weight; };
StructuredBuffer<PointLightSuggestion> PointLightSuggestions;
RWStructuredBuffer<PointLightSuggestion> PointLightSuggestionOutput : register(u0);
StructuredBuffer<float3> PointLightBaselines;
StructuredBuffer<float4> PointBaseline; // RGB sum and the node's ray endpoint.
RWStructuredBuffer<float3> PointLightBaselineOutput : register(u2);
RWStructuredBuffer<float4> PointBaselineOutput : register(u3);
static const uint PointSuggestionSamples=4,PointSuggestionSpacing=16;
uint2 PointSuggestionSize() { return ((uint2)Resolution+PointSuggestionSpacing-1)/PointSuggestionSpacing+1; }
void PointSuggestionRay(float2 uv, out float3 origin, out float3 direction, out float stop)
{
    ProjectionCamera camera=SceneCamera();
    origin=camera.Origin; direction=ProjectionDirection(camera,uv);
    int2 pixel=clamp((int2)(uv*Resolution),0,(int2)Resolution-1);
    float z=PointDepth.Load(int3(pixel,0));
    stop=z<1 ? ProjectionDepthDistance(camera,direction,z) : 1e30;
}
float3 PointBaselineContribution(PointLight light, float3 origin, float3 direction, float stop,
    float3 scatter, float3 extinction, float4 layout)
{
    float3 delta=light.Position-origin;
    float c=dot(delta,direction);
    float3 perpendicular=delta-c*direction;
    // Bound only the approximation's sharp core to the grid's angular footprint.
    // A coarse node on a point singularity must not contaminate its whole cell.
    float radius=length(delta)*PointSuggestionSpacing/(Resolution.y*SceneCamera().Focal);
    float b2=max(dot(perpendicular,perpendicular),max(radius*radius,1e-8));
    return min(scatter*light.Intensity*PointIntegral(c,b2,0,stop,extinction,layout),1e20);
}
[numthreads(8,8,1)]
void PreparePointLightSuggestions(uint3 id : SV_DispatchThreadID)
{
    uint2 size=PointSuggestionSize();
    if (any(id.xy>=size) || !UsePointLightSampling()) return;
    uint count=PointLightCount.Load(0),baseIndex=(id.y*size.x+id.x)*PointSuggestionSamples;
    uint node=id.y*size.x+id.x;
    PointBaselineOutput[node]=0;
    PointLightSuggestion empty; empty.Index=0; empty.Weight=0;
    [unroll] for (uint s=0;s<PointSuggestionSamples;++s) PointLightSuggestionOutput[baseIndex+s]=empty;
    if (count<=8 || !any(AirScatter()>0)) return;
    float3 origin,direction; float stop;
    PointSuggestionRay((float2)id.xy/(size-1),origin,direction,stop);
    float3 scatter=AirScatter(),extinction=AirExtinction(),baseline=0;
    uint w,h; RasterAirlightLut.GetDimensions(w,h);
    float4 layout=float4((w-1.0)/w,(h-1.0)/h,.5/w,.5/h);
    float rate=dot(extinction,float3(.2126,.7152,.0722)),total=0;
    uint active=0;
    [loop] for (uint i=0;i<count;++i)
    {
        PointLight light=PointLights[i];
        if (!any(light.Intensity>0)) continue;
        float3 value=PointBaselineContribution(light,origin,direction,stop,scatter,extinction,layout);
        PointLightBaselineOutput[node*PointLightCapacity+i]=value;
        baseline+=value;
        float d2;
        total+=PointRayWeight(light,origin,direction,1e30,rate,d2);
        ++active;
    }
    PointBaselineOutput[node]=float4(baseline,stop);
    if (active==0) return;

    // A small uniform mixture keeps even severely attenuated/colorful sources
    // selectable, and keeps CDF intervals above float summation noise.
    bool weighted=total>1e-30;
    float scale=weighted ? (31.0/32)/total : 0;
    float floorProbability=(weighted ? 1.0/32 : 1)/active;
    uint seed=RasterHash(id.x^RasterHash(id.y+1));
    float jitter=Random(seed),cdf=0;
    uint selected=0,remaining=active;
    [loop] for (uint i=0;i<count && selected<PointSuggestionSamples;++i)
    {
        PointLight light=PointLights[i];
        if (!any(light.Intensity>0)) continue;
        float d2,weight=PointRayWeight(light,origin,direction,1e30,rate,d2);
        float previous=cdf;
        // Close the final interval exactly; use the actual interval width in
        // the estimator so selection and compensation share the same CDF.
        cdf=--remaining==0 ? 1 : min(1,cdf+scale*weight+floorProbability);
        uint upto=(uint)clamp(ceil(PointSuggestionSamples*cdf-jitter),0,PointSuggestionSamples);
        [loop] while (selected<upto)
        {
            PointLightSuggestion suggestion;
            suggestion.Index=i; suggestion.Weight=rcp(PointSuggestionSamples*(cdf-previous));
            PointLightSuggestionOutput[baseIndex+selected]=suggestion; ++selected;
        }
    }
}
float PointDepthCompatibility(float stop, float nodeStop)
{
    if (stop>=1e30 || nodeStop>=1e30) return (stop>=1e30 && nodeStop>=1e30) ? 1 : 0;
    // A simple relative-depth gate: fade to zero at a 5% endpoint difference.
    float w=saturate(1-abs(stop-nodeStop)/max(.05*max(stop,nodeStop),1e-6));
    return w*w;
}
float3 SamplePointAirlight(int2 pixel, float3 origin, float3 direction, float stop,
    float3 scatter, float3 extinction, float4 layout)
{
    uint2 size=PointSuggestionSize();
    float2 g=(pixel+.5)/Resolution*(size-1);
    uint2 cell=min((uint2)g,size-2);
    float2 f=g-cell;
    uint4 nodes=uint4(cell.y*size.x+cell.x,cell.y*size.x+cell.x+1,
        (cell.y+1)*size.x+cell.x,(cell.y+1)*size.x+cell.x+1);
    float4 a=float4((1-f.x)*(1-f.y),f.x*(1-f.y),(1-f.x)*f.y,f.x*f.y);
    float4 cached[4],b;
    [unroll] for (uint c=0;c<4;++c)
    {
        cached[c]=PointBaseline[nodes[c]];
        b[c]=a[c]*PointDepthCompatibility(stop,cached[c].w);
    }
    float total=dot(b,float4(1,1,1,1));
    if (total>1e-6) { b/=total; a=b; }
    else b=0; // No matching layer: zero baseline and ordinary light sampling.
    float3 sum=0;
    [unroll] for (uint c=0;c<4;++c) sum+=b[c]*cached[c].rgb;
    [unroll] for (uint y=0;y<2;++y) [unroll] for (uint x=0;x<2;++x)
    {
        float cornerWeight=a[y*2+x];
        uint baseIndex=((cell.y+y)*size.x+cell.x+x)*PointSuggestionSamples;
        [unroll] for (uint s=0;s<PointSuggestionSamples;++s)
        {
            PointLightSuggestion suggestion=PointLightSuggestions[baseIndex+s];
            if (suggestion.Weight>0)
            {
                float3 approximate=0;
                [unroll] for (uint c=0;c<4;++c)
                    approximate+=b[c]*PointLightBaselines[nodes[c]*PointLightCapacity+suggestion.Index];
                float3 actual=PointAirlightContribution(PointLights[suggestion.Index],origin,direction,stop,scatter,extinction,layout);
                // Residuals are signed. Clamping here would change the expectation.
                sum+=(cornerWeight*suggestion.Weight)*(actual-approximate);
            }
        }
    }
    return sum;
}
float4 IntegratePointAirlight(int2 pixel)
{
    float3 origin,direction; bool hit; float end;
    PointRay(pixel,origin,direction,hit,end);
    float3 sum=0,scatter=AirScatter(),extinction=AirExtinction();
    uint w,h; RasterAirlightLut.GetDimensions(w,h);
    float4 layout=float4((w-1.0)/w,(h-1.0)/h,.5/w,.5/h);
    uint count=PointLightCount.Load(0);
    [branch] if (any(scatter>0))
    {
        float stop=hit ? end : 1e30;
        [branch] if (UsePointLightSampling() && count>8)
            sum=SamplePointAirlight(pixel,origin,direction,stop,scatter,extinction,layout);
        else sum=SumPointAirlight(count,origin,direction,stop,scatter,extinction,layout);
    }
    // Preserve signed Monte Carlo estimates until final scene composition.
    return float4(clamp(sum,-HdrMaximum,HdrMaximum),1);
}
float4 PointAirlightPS(PSin input) : SV_Target
{
    return IntegratePointAirlight((int2)input.Position.xy);
}
#endif
