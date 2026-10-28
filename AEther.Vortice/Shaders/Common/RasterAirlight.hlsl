#ifndef AETHER_RASTER_AIRLIGHT
#define AETHER_RASTER_AIRLIGHT
#include "Projection.hlsl"

// Homogeneous single-scattering transport for the raster geometry contract.
// The scene supplies settings and SceneCamera; see doc/raster-airlight-contract.md.
cbuffer RasterPassConstants : register(b2) { uint RasterLight; uint RasterFace; uint RasterShadow; float RasterShadowScale; };
// Shared light records uploaded by the host. Matching capacity is
// RasterLightTransport.MaximumLights; the host currently supplies spectral rings.
static const int MaxLights=96;
cbuffer RasterLightConstants : register(b3)
{
    float4 RasterLightInfo; // strobe count/active count, tonal count/active count
    float4 RasterLightPositions[MaxLights]; // xyz position, w reserved
    float4 RasterLightRadiance[MaxLights]; // RGB intensity, scalar transient intensity
    float4 RasterActiveLights[MaxLights/4]; // packed indices of nonzero lights
    float4 RasterTonalPositions[MaxLights];
    float4 RasterTonalRadiance[MaxLights];
    float4 RasterActiveTonalLights[MaxLights/4];
    float4 RasterLightCones[MaxLights]; // xyz inward unit axis, w cosine; -1 = full sphere
    float4 RasterTonalCones[MaxLights];
    float4 RasterShadowRight[2*MaxLights]; // xyz spot right, w tan(half-angle); 1 for cubes
    float4 RasterShadowUp[2*MaxLights]; // xyz spot up, w first depth-array slice
    float4 RasterRingRadiance; // independent live-S filament intensity, RGB
    float4 RasterRingGeometry; // xyz unit normal, w radius; center is world origin
};
cbuffer RasterAirlightConstants : register(b5)
{
    float4 RasterLookupInfo; // ready, maximum perpendicular distance, log warp, coordinate scale
};
Texture2DArray<float> RasterShadows;
SamplerComparisonState RasterShadowSampler;
Texture2D<float4> RasterSurface;
Texture2D<float> RasterDepth;
Texture2D<float4> RasterAirlight;
Texture2D<float4> RasterAirlightLut;
SamplerState RasterLutSampler;
static const float Pi=3.14159265359;
static const float Near=ProjectionNear, Far=ProjectionFar; // Shadow frusta.
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
int ActiveStrobeLight(int i) { return (int)RasterActiveLights[i/4][i%4]; }
int ActiveTonalLight(int i) { return (int)RasterActiveTonalLights[i/4][i%4]; }
int ShadowCount() { return (Lights.Shadows ? (int)RasterLightInfo.y : 0)+(TonalLights.Shadows ? (int)RasterLightInfo.w : 0); }
int UnshadowedCount() { return (!Lights.Shadows ? (int)RasterLightInfo.y : 0)+(!TonalLights.Shadows ? (int)RasterLightInfo.w : 0); }
int ActiveLight(int i)
{
    int strobes=Lights.Shadows ? (int)RasterLightInfo.y : 0;
    return i<strobes ? ActiveStrobeLight(i) : MaxLights+ActiveTonalLight(i-strobes);
}
int ActiveUnshadowedLight(int i)
{
    int strobes=!Lights.Shadows ? (int)RasterLightInfo.y : 0;
    return i<strobes ? ActiveStrobeLight(i) : MaxLights+ActiveTonalLight(i-strobes);
}
float4 LightData(int i) { return i<MaxLights ? RasterLightPositions[i] : RasterTonalPositions[i-MaxLights]; }
float4 LightRadiance(int i) { return i<MaxLights ? RasterLightRadiance[i] : RasterTonalRadiance[i-MaxLights]; }
float4 LightCone(int i) { return i<MaxLights ? RasterLightCones[i] : RasterTonalCones[i-MaxLights]; }
float3 LightPosition(int i) { return LightData(i).xyz; }
int ShadowSlice(int i) { return (int)RasterShadowUp[i].w; }
int PreviewMapCount()
{
    int count=(Lights.Shadows ? (int)RasterLightInfo.x : 0)+(TonalLights.Shadows ? (int)RasterLightInfo.z : 0);
    return max(1,count>0 ? count : (int)RasterLightInfo.x);
}
bool InsideCone(float3 offset, float4 cone)
{
    bool inside=true;
    [branch] if (cone.w>=0) inside=dot(offset,cone.xyz)>=cone.w*length(offset);
    return inside;
}
// Intersect the forward nappe of a convex cone with an existing camera interval.
// The caller uses 1e30 for infinity. No cone math is needed for a full sphere.
bool ClipSpotCone(float3 offset, float3 direction, float4 cone, inout float begin, inout float end)
{
    float z=dot(offset,cone.xyz),v=dot(direction,cone.xyz);
    // Squaring the cone equation introduces the backward nappe: remove it first.
    if (v==0) { if (z<0) return false; }
    else if (v>0) begin=max(begin,-z/v);
    else end=min(end,-z/v);
    if (end<=begin) return false;
    if (cone.w==0) return true; // 90-degree half-angle is a half-space.
    float k=cone.w*cone.w;
    float a=v*v-k, b=z*v-k*dot(offset,direction),c=z*z-k*dot(offset,offset);
    if (a==0)
    {
        if (b==0) return c>=0;
        if (b>0) begin=max(begin,-c/(2*b)); else end=min(end,-c/(2*b));
    }
    else
    {
        float discriminant=b*b-a*c;
        if (discriminant<0) return a>0;
        // Stable quadratic roots avoid cancellation for rays near the boundary.
        float q=-b-(b>=0 ? 1 : -1)*sqrt(discriminant);
        float r0=q/a,r1=q!=0 ? c/q : -b/a;
        float lo=min(r0,r1),hi=max(r0,r1);
        if (a<0) { begin=max(begin,lo); end=min(end,hi); }
        else if (v>0) begin=max(begin,hi);
        else end=min(end,lo);
    }
    return end>begin;
}
bool ClipCone(float3 offset, float3 direction, float4 cone, inout float begin, inout float end)
{
    bool intersects=end>begin;
    [branch] if (cone.w>=0) intersects=ClipSpotCone(offset,direction,cone,begin,end);
    return intersects;
}
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
void CubeAddress(float3 w, out int face, out float2 uv)
{
    float3 a=abs(w);
    if (a.x>=a.y && a.x>=a.z) { face=w.x>=0 ? 0 : 1; uv=float2(w.x>=0 ? -w.z : w.z,-w.y)/a.x; }
    else if (a.y>=a.z) { face=w.y>=0 ? 2 : 3; uv=float2(w.x,w.y>=0 ? w.z : -w.z)/a.y; }
    else { face=w.z>=0 ? 4 : 5; uv=float2(w.z>=0 ? w.x : -w.x,-w.y)/a.z; }
    uv=.5+.5*uv;
}
float3 FacePosition(float3 p, int face)
{
    if (face==0) return float3(-p.z,p.y,p.x);
    if (face==1) return float3(p.z,p.y,-p.x);
    if (face==2) return float3(p.x,-p.z,p.y);
    if (face==3) return float3(p.x,p.z,-p.y);
    if (face==4) return p;
    return float3(-p.x,p.y,-p.z);
}
float3 ShadowPosition(float3 p, int light, int face)
{
    float3 local=0;
    if (LightCone(light).w<0) local=FacePosition(p,face);
    else local=float3(dot(p,RasterShadowRight[light].xyz),dot(p,RasterShadowUp[light].xyz),dot(p,LightCone(light).xyz));
    return local;
}
float3 ShadowDirection(int light, int face, float2 uv)
{
    float3 ray=0;
    if (LightCone(light).w<0) ray=CubeDirection(face,uv);
    else
    {
        float2 q=(2*uv-1)*RasterShadowRight[light].w;
        ray=LightCone(light).xyz+q.x*RasterShadowRight[light].xyz-q.y*RasterShadowUp[light].xyz;
    }
    return ray;
}
float ProjectDepth(float z) { return ProjectionClipDepth(z,Near,Far); }
float LinearDepth(float z) { return ProjectionLinearDepth(z,Near,Far); }
float ShadowBias() { return .0005+.02*Quality.ShadowBias; }
float Visibility(int light, float3 p, float3 normal)
{
    float3 delta=p-LightPosition(light);
    bool spot=LightCone(light).w>=0;
    int face=0; float2 uv; float z;
    float tangent=RasterShadowRight[light].w;
    if (spot)
    {
        float3 local=ShadowPosition(delta,light,0);
        if (local.z<=0) return 0;
        if (local.z<Near) return 1;
        z=local.z;
        uv=.5+.5*float2(local.x,-local.y)/(z*tangent);
    }
    else
    {
        z=max(abs(delta.x),max(abs(delta.y),abs(delta.z)));
        if (z<Near) return 1;
        CubeAddress(delta,face,uv);
    }
    int slice=ShadowSlice(light)+face;
    uint w,h,layers; RasterShadows.GetDimensions(w,h,layers);
    // Both projection types render a one-texel guard on every side, preserving
    // the complete hardware 2x2 footprint at cone boundaries and cube seams.
    float scale=(w-2.0)/w;
    uv=.5+(uv-.5)*scale;
    if (Quality.HardwarePCF)
    {
        float biased=max(Near,z-ShadowBias());
        float comparison=ProjectDepth(biased)/biased;
        if (any(normal!=0))
        {
            // Projected receiver-plane depth is affine in face UV. A hardware
            // comparison uses one reference for four texels, so use its minimum
            // over those centers to prevent slope acne with a tight depth bias.
            float plane=dot(delta,normal);
            if (abs(plane)>1e-5)
            {
                float3 n=ShadowPosition(normal,light,face);
                float2 gradient=(2*Near*Far/(Far-Near))*tangent*float2(-n.x,n.y)/(plane*scale*w);
                float2 f=frac(uv*w-.5);
                comparison+=min(-f.x*gradient.x,(1-f.x)*gradient.x)
                    +min(-f.y*gradient.y,(1-f.y)*gradient.y);
            }
        }
        return RasterShadows.SampleCmpLevelZero(RasterShadowSampler,float3(uv,slice),saturate(comparison));
    }
    int2 texel=clamp((int2)(uv*w),0,(int)w-1);
    float depth=RasterShadows.Load(int4(texel,slice,0));
    if (depth>=1) return 1;
    // Compare against the receiver plane at the actual texel-center ray. This
    // removes sloped-face acne without filtering or a large silhouette offset.
    if (any(normal!=0))
    {
        float3 ray=ShadowDirection(light,face,.5+((texel+.5)/w-.5)/scale);
        float denominator=dot(ray,normal);
        if (abs(denominator)>1e-5)
        {
            float plane=dot(delta,normal)/denominator;
            if (plane>0) z=plane;
        }
    }
    return z<=LinearDepth(depth)+ShadowBias() ? 1 : 0;
}
float3 AirScatter() { return .3*Air.Density*Air.Density*Air.Scattering; }
float3 AirExtinction() { return .3*Air.Density*Air.Density*(Air.Scattering+Air.Absorption); }
// Ring geometry is exact; illumination is the infinite line tangent at its
// closest point (pi/r, nearest-point phase/BRDF and source transmittance).
float3 RingPlanar(float3 p)
{
    return p-RasterRingGeometry.xyz*dot(p,RasterRingGeometry.xyz);
}
float3 RingOffset(float3 p)
{
    float z=dot(p,RasterRingGeometry.xyz);
    float3 v=RingPlanar(p);
    float rho=length(v);
    // On the axis every ring point is equally close. Choose world +Y
    // consistently; the uploaded normal is horizontal. No radius softening.
    float3 radial=rho>0 ? v/max(rho,NumericDistance) : float3(0,1,0);
    return (rho-RasterRingGeometry.w)*radial+z*RasterRingGeometry.xyz;
}
float3 RingTangent(float3 p)
{
    float3 v=RingPlanar(p);
    float rho=length(v);
    return cross(RasterRingGeometry.xyz,rho>0 ? v/max(rho,NumericDistance) : float3(0,1,0));
}
float FilamentPhase(float3 q, float3 direction)
{
    float mu=dot(q,direction)*rsqrt(max(dot(q,q),NumericDistance2));
    return 3*(1+mu*mu)/(16*Pi);
}
float4 GeometryProject(float3 world)
{
    if (RasterShadow!=0)
    {
        float3 p=ShadowPosition(world-LightPosition(RasterLight),RasterLight,RasterFace);
        return ProjectionViewClip(p,(RasterShadowScale/RasterShadowRight[RasterLight].w).xx,Near,Far);
    }
    return ProjectionClip(SceneCamera(),world);
}
float4 RasterSurfacePS(GeometryOutput input) : SV_Target
{
    float3 radiance=0,extinction=AirExtinction();
    [loop] for (int slot=0;slot<ShadowCount();++slot)
    {
        int i=ActiveLight(slot);
        float3 delta=LightPosition(i)-input.World;
        float r2=dot(delta,delta),r=sqrt(r2);
        if (r2==0) { radiance+=SingularRadiance(input.Albedo*LightRadiance(i).rgb); continue; }
        if (!InsideCone(-delta,LightCone(i))) continue;
        float diffuse=max(0,dot(input.Normal,delta/max(r,NumericDistance)))/Pi;
        if (diffuse<=0) continue;
        float shadow=Visibility(i,input.World,input.Normal);
        radiance+=input.Albedo*diffuse*shadow*exp(-extinction*r)
            *LightRadiance(i).rgb/max(r2,NumericDistance2);
    }
    // All unshadowed surface lights share the existing geometry draw.
    [loop] for (int tonalSlot=0;tonalSlot<UnshadowedCount();++tonalSlot)
    {
        int i=ActiveUnshadowedLight(tonalSlot);
        float3 delta=LightPosition(i)-input.World;
        float r2=dot(delta,delta),r=sqrt(r2);
        if (r2==0) { radiance+=SingularRadiance(input.Albedo*LightRadiance(i).rgb); continue; }
        if (!InsideCone(-delta,LightCone(i))) continue;
        float diffuse=max(0,dot(input.Normal,delta/max(r,NumericDistance)))/Pi;
        radiance+=input.Albedo*diffuse*exp(-extinction*r)
            *LightRadiance(i).rgb/max(r2,NumericDistance2);
    }
    [branch] if (any(RasterRingRadiance.rgb>0))
    {
        float3 q=RingOffset(input.World);
        float r2=dot(q,q),r=sqrt(r2);
        if (r2==0) radiance+=SingularRadiance(input.Albedo*RasterRingRadiance.rgb);
        else
        {
            // Local infinite 1D source: pi/r, with a nearest-point BRDF.
            float diffuse=max(0,dot(input.Normal,-q))/max(r,NumericDistance)/Pi;
            radiance+=input.Albedo*diffuse*RasterRingRadiance.rgb
                *exp(-extinction*r)*(Pi/max(r,NumericDistance));
        }
    }
    // Preserve finite values in the RGBA32F render target. This is a
    // storage-format bound, not a source-size or distance-falloff clamp.
    return float4(StoreRadiance(radiance),1);
}
float OneMinusExp(float x) { return x<.001 ? x*(1-x*.5+x*x/6) : 1-exp(-x); }
float NegativeLogOneMinus(float x) { return x<.001 ? x*(1+x*(.5+x/3)) : -log(max(1e-7,1-x)); }
// Positive nodes/weights of Gauss-Legendre quadrature; each node is mirrored.
static const float4 FilamentNodes=float4(.1834346425,.5255324099,.7966664774,.9602898565);
static const float4 FilamentWeights=float4(.3626837834,.3137066459,.2223810345,.1012285363);
float FilamentAsinh(float x)
{
    float a=abs(x);
    if (a<.001) return x*(1-x*x/6);
    // Keep tiny distances and very long optical tails within float range.
    if (a>1e10) return sign(x)*(log(a)+.69314718056);
    return sign(x)*log(a+sqrt(1+a*a));
}
float3 RingSegmentIntegral(float3 origin, float3 direction, float begin, float end, float3 extinction)
{
    if (end<=begin) return 0;
    // Each interval has monotonic distance to the ring. Center its transform
    // at the nearer endpoint, so both close encounters receive their own nodes.
    float3 q0=RingOffset(origin+begin*direction),q1=RingOffset(origin+end*direction);
    float center=dot(q0,q0)<=dot(q1,q1) ? begin : end;
    float3 p=origin+center*direction,q=RingOffset(p);
    float b=length(q);
    if (b==0) return SingularRadiance(1);
    float tangentDot=dot(direction,RingTangent(p));
    float k=sqrt(max(0,1-tangentDot*tangentDot));
    // Curvature supplies a useful width even for a ray tangent to the ring,
    // where a straight-line transform would have k=0. This shapes nodes only;
    // every evaluation uses the actual ring distance and the full Jacobian.
    float speed=max(k,sqrt(b/max(RasterRingGeometry.w,NumericDistance)));
    float scale=max(b/max(speed,NumericDistance),NumericDistance);
    float u0=FilamentAsinh((begin-center)/scale),u1=FilamentAsinh((end-center)/scale);
    int panels=min(32,max(1,(int)ceil((u1-u0)/2)));
    float step=(u1-u0)/panels;
    float3 sum=0;
    [loop] for (int panel=0;panel<panels;++panel)
    {
        float middle=u0+(panel+.5)*step;
        [unroll] for (int node=0;node<4;++node)
        [unroll] for (int side=-1;side<=1;side+=2)
        {
            float u=middle+side*.5*step*FilamentNodes[node];
            float t=clamp(center+scale*sinh(u),begin,end);
            float3 q=RingOffset(origin+t*direction);
            float r=length(q);
            float jacobian=scale*cosh(u)/max(r,NumericDistance);
            sum+=FilamentWeights[node]*jacobian*FilamentPhase(q,direction)*exp(-extinction*(t+r));
        }
    }
    return sum*(.5*step);
}
// Half the derivative of squared distance to the ring. Its derivative changes
// sign only when rho^3=R*K, K=|planarOrigin x planarDirection|^2. These analytic
// breakpoints bracket ALL stationary points, including two separate minima.
float RingDistanceSlope(float3 origin, float3 direction, float3 planarOrigin, float3 planarDirection,
    float t, float axis, float a, float area2, float side)
{
    float3 v=planarOrigin+t*planarDirection;
    float rho=length(v);
    float radialSlope=dot(v,planarDirection)/max(rho,NumericDistance);
    // One-sided limits at a ray crossing the ring axis; distance has a cusp.
    if (area2==0 && t==axis) radialSlope=side*sqrt(a);
    return dot(origin+t*direction,direction)-RasterRingGeometry.w*radialSlope;
}
float RingStationaryPoint(float3 origin, float3 direction, float3 planarOrigin, float3 planarDirection,
    float begin, float end, float axis, float a, float area2, float beginSlope)
{
    float t=.5*(begin+end);
    [loop] for (int iteration=0;iteration<24;++iteration)
    {
        float slope=RingDistanceSlope(origin,direction,planarOrigin,planarDirection,t,axis,a,area2,1);
        if (slope==0) break;
        if ((slope>0)==(beginSlope>0)) begin=t; else end=t;
        float3 v=planarOrigin+t*planarDirection;
        float rho2=max(dot(v,v),NumericDistance2);
        float derivative=dot(direction,direction)-RasterRingGeometry.w*(area2/rho2)*rsqrt(rho2);
        float next=derivative!=0 ? t-slope/derivative : t;
        if (!(next>begin && next<end) || next==t) next=.5*(begin+end);
        if (next==begin || next==end) break;
        t=next;
    }
    return t;
}
bool RayCrossesRing(float3 origin, float3 direction, float end)
{
    float z=dot(origin,RasterRingGeometry.xyz),dz=dot(direction,RasterRingGeometry.xyz);
    float radius2=RasterRingGeometry.w*RasterRingGeometry.w;
    if (dz!=0)
    {
        float t=-z/dz;
        float3 v=RingPlanar(origin+t*direction);
        return t>=0 && t<=end && dot(v,v)==radius2;
    }
    if (z!=0) return false;
    // Coplanar rays: stable circle intersections, including a tangent contact.
    float a=dot(direction,direction),b=dot(origin,direction),c=dot(origin,origin)-radius2;
    float discriminant=b*b-a*c;
    if (discriminant<0) return false;
    float q=-b-(b>=0 ? 1 : -1)*sqrt(discriminant);
    float t0=q/a,t1=q!=0 ? c/q : -b/a;
    return (t0>=0 && t0<=end) || (t1>=0 && t1<=end);
}
float3 RingAirlight(float3 origin, float3 direction, bool hit, float end, float3 scatter, float3 extinction)
{
    if (!any(RasterRingRadiance.rgb>0) || !any(scatter>0)) return 0;
    // Preserve the mathematical singularity even beyond the numerical tail.
    if (RayCrossesRing(origin,direction,hit ? end : 1e30))
        return SingularRadiance(RasterRingRadiance.rgb*scatter);
    float rate=min(scatter.x>0 ? extinction.x : 1e20,min(scatter.y>0 ? extinction.y : 1e20,scatter.z>0 ? extinction.z : 1e20));
    rate=max(rate,1e-12);
    float rayEnd=hit ? min(end,20/rate) : 20/rate;
    float3 planarOrigin=RingPlanar(origin),planarDirection=RingPlanar(direction);
    float a=dot(planarDirection,planarDirection);
    float axis=a>0 ? -dot(planarOrigin,planarDirection)/a : 0;
    float3 closest=planarOrigin+axis*planarDirection;
    float3 area=cross(planarOrigin,planarDirection);
    float area2=dot(area,area);
    float criticalRho=pow(max(RasterRingGeometry.w*area2/dot(direction,direction),0),1.0/3.0);
    float span=a>0 ? sqrt(max(0,(criticalRho*criticalRho-dot(closest,closest))/a)) : 0;
    float3 cuts=clamp(float3(axis-span,axis,axis+span),0,rayEnd);
    float3 integral=0;
    [loop] for (int interval=0;interval<4;++interval)
    {
        float lo=interval==0 ? 0 : interval==1 ? cuts.x : interval==2 ? cuts.y : cuts.z;
        float hi=interval==0 ? cuts.x : interval==1 ? cuts.y : interval==2 ? cuts.z : rayEnd;
        if (hi<=lo) continue;
        float g0=RingDistanceSlope(origin,direction,planarOrigin,planarDirection,lo,axis,a,area2,1);
        float g1=RingDistanceSlope(origin,direction,planarOrigin,planarDirection,hi,axis,a,area2,-1);
        if ((g0<0 && g1>0) || (g0>0 && g1<0))
        {
            float stationary=RingStationaryPoint(origin,direction,planarOrigin,planarDirection,lo,hi,axis,a,area2,g0);
            integral+=RingSegmentIntegral(origin,direction,lo,stationary,extinction)
                +RingSegmentIntegral(origin,direction,stationary,hi,extinction);
        }
        else integral+=RingSegmentIntegral(origin,direction,lo,hi,extinction);
    }
    float3 result=RasterRingRadiance.rgb*scatter*(Pi*integral);
    // An exact singular interval can overflow before storage saturation. Do
    // not let 0*infinity introduce NaN in a channel with zero scattering.
    return StoreRadiance(float3(scatter.x>0 ? result.x : 0,scatter.y>0 ? result.y : 0,scatter.z>0 ? result.z : 0));
}
struct Source
{
    float3 Intensity;
    float Center; float Perpendicular2; float Height; float Angle; float Span; float PdfScale;
    float Begin; float End;
};
Source DescribeLight(int i, float3 origin, float3 direction, float begin, float end)
{
    Source source;
    source.Intensity=LightRadiance(i).rgb;
    float3 delta=LightPosition(i)-origin;
    source.Center=dot(delta,direction);
    float3 perpendicular=delta-source.Center*direction;
    source.Perpendicular2=dot(perpendicular,perpendicular);
    source.Height=max(sqrt(source.Perpendicular2),NumericDistance);
    source.Begin=begin; source.End=end;
    float x=begin-source.Center,y=end-source.Center;
    source.Angle=atan2(x,source.Height);
    source.Span=end<1e30 ? atan2(source.Height*(end-begin),source.Height*source.Height+x*y)
        : atan2(source.Height,x);
    source.PdfScale=source.Height/max(source.Span,1e-30);
    return source;
}
float SampleDistance(Source source, float u, float optical, float rate, float mass)
{
    // Sub-precision angular spans cannot be inverted reliably. Use the exact
    // optical proposal/PDF instead; this changes sampling, never illumination.
    if (source.Span<1e-5) optical=1;
    if (optical>0 && u<optical)
    {
        float x=min(u/optical,1-1e-6)*mass;
        // Stable -log(1-x) for optically thin clipped intervals.
        float distance=NegativeLogOneMinus(x);
        return clamp(source.Begin+distance/rate,source.Begin,source.End);
    }
    float v=min((u-optical)/max(1-optical,1e-20),1-1e-6);
    return clamp(source.Center+source.Height*tan(source.Angle+v*source.Span),source.Begin,source.End);
}
float OpticalMass(Source source, float rate)
{
    return source.End<1e30 ? OneMinusExp(rate*(source.End-source.Begin)) : 1;
}
float DistancePdf(Source source, float t, float optical, float rate, float mass)
{
    if (t<source.Begin || t>source.End) return 0;
    if (source.Span<1e-5) optical=1;
    float x=t-source.Center;
    return optical*rate*exp(-rate*(t-source.Begin))/max(mass,1e-30)
        +(1-optical)*source.PdfScale/(x*x+source.Height*source.Height);
}
float3 SourceContribution(int i, Source source, float t, float3 p, float3 extinction, out float falloff)
{
    float along=t-source.Center;
    float r2=along*along+source.Perpendicular2;
    falloff=rcp(max(r2,NumericDistance2));
    if (!InsideCone(p-LightPosition(i),LightCone(i))) return 0;
    float phase=3*(2-source.Perpendicular2/max(r2,NumericDistance2))/(16*Pi);
    return source.Intensity*phase*Visibility(i,p,0)*exp(-extinction*(t+sqrt(r2)))*falloff;
}

#include "PointAirlightIntegral.hlsl"

float3 UnshadowedAirlight(float3 origin, float3 direction, bool hit, float end, float3 scatter, float3 extinction)
{
    if (UnshadowedCount()==0 || !any(scatter>0)) return 0;
    uint width,height; RasterAirlightLut.GetDimensions(width,height);
    float4 layout=float4((width-1.0)/width,(height-1.0)/height,.5/width,.5/height);
    float3 radiance=0;
    // One batched light loop: camera ray/depth and the output write are shared
    // with the shadowed path. No per-light draw, shadow lookup or distance march.
    [loop] for (int slot=0;slot<UnshadowedCount();++slot)
    {
        int i=ActiveUnshadowedLight(slot);
        float3 delta=LightPosition(i)-origin;
        float begin=0,stop=hit ? end : 1e30;
        if (!ClipCone(-delta,direction,LightCone(i),begin,stop)) continue;
        float c=dot(delta,direction);
        float3 perpendicular=delta-c*direction;
        float b2=dot(perpendicular,perpendicular),b=sqrt(b2),H=max(b,NumericDistance);
        float3 integral=0;
        [branch] if (b==0 && c>=begin && c<=stop)
        {
            radiance+=SingularRadiance(scatter*LightRadiance(i).rgb);
            continue;
        }
        integral=PointIntegral(c,b2,begin,stop,extinction,layout);
        radiance+=scatter*LightRadiance(i).rgb*integral;
    }
    return radiance;
}
float4 IntegrateAirlight(int2 pixel)
{
    ProjectionCamera camera=SceneCamera();
    float3 origin=camera.Origin,direction=ProjectionDirection(camera,(pixel+.5)/Resolution);
    float z=RasterDepth.Load(int3(pixel,0)); bool hit=z<1;
    float end=hit ? ProjectionDepthDistance(camera,direction,z) : 0;
    float guide=hit ? ProjectionLinearDepth(camera,z) : 0;
    float3 scatter=AirScatter(),extinction=AirExtinction();
    float3 radiance=UnshadowedAirlight(origin,direction,hit,end,scatter,extinction);
    [branch] if (any(RasterRingRadiance.rgb>0))
        radiance+=RingAirlight(origin,direction,hit,end,scatter,extinction);
    int count=ShadowCount();
    if (count==0 || !any(scatter>0)) return float4(StoreRadiance(radiance),guide);
    float rate=min(scatter.x>0 ? extinction.x : 1e20,min(scatter.y>0 ? extinction.y : 1e20,scatter.z>0 ? extinction.z : 1e20));
    rate=max(rate,1e-12);
    // Compact the lights whose cones intersect this visible ray. Both distance
    // proposals and their PDFs use exactly the same per-light clipped interval.
    int rayLights[2*MaxLights]; float2 rayBounds[2*MaxLights];
    int rayCount=0;
    [loop] for (int slot=0;slot<count;++slot)
    {
        int i=ActiveLight(slot);
        float begin=0,stop=hit ? end : 1e30;
        if (!ClipCone(origin-LightPosition(i),direction,LightCone(i),begin,stop)) continue;
        rayLights[rayCount]=i; rayBounds[rayCount]=float2(begin,stop); ++rayCount;
    }
    if (rayCount==0) return float4(StoreRadiance(radiance),guide);
    int samples=1+(int)round(127*Quality.Samples);
    float optical=saturate(Sampling.OpticalDepthWeight);
    uint seed=RasterHash((uint)pixel.x^RasterHash((uint)pixel.y+1));
    [branch] if (Sampling.RandomLight)
    {
        int offset=Sampling.Jitter ? min((int)(Random(seed^0xa511e9b3)*rayCount),rayCount-1) : 0;
        [loop] for (int s=0;s<samples;++s)
        {
            int slot=(s+offset)%rayCount,i=rayLights[slot];
            Source source=DescribeLight(i,origin,direction,rayBounds[slot].x,rayBounds[slot].y);
            float mass=OpticalMass(source,rate);
            float jitter=Sampling.Jitter ? Random(seed^RasterHash((uint)s+1)) : .5;
            // Stratify distance separately within each light. When every light
            // is visited, average its own samples; otherwise compensate for the
            // uniformly selected subset of ray-intersecting lights.
            int visits=1+(samples-1-s%rayCount)/rayCount;
            float u=min((s/rayCount+jitter)/visits,1-1e-6);
            float weight=samples>=rayCount ? rcp((float)visits) : (float)rayCount/samples;
            float t=SampleDistance(source,u,optical,rate,mass);
            float falloff;
            float3 incoming=SourceContribution(i,source,t,origin+t*direction,extinction,falloff);
            float pdf=DistancePdf(source,t,optical,rate,mass);
            radiance+=scatter*incoming*(weight/max(pdf,1e-30));
        }
        return float4(StoreRadiance(radiance),guide);
    }
    // All-light estimator: draw from the uniform mixture of each light's
    // conditional distance PDF. Every term is zero outside its own interval.
    [loop] for (int s=0;s<samples;++s)
    {
        float jitter=Sampling.Jitter ? Random(seed^RasterHash((uint)s+1)) : .5;
        float choice=min((s+jitter)/samples,1-1e-6)*rayCount;
        int pick=min((int)choice,rayCount-1);
        Source selected=DescribeLight(rayLights[pick],origin,direction,rayBounds[pick].x,rayBounds[pick].y);
        float t=SampleDistance(selected,frac(choice),optical,rate,OpticalMass(selected,rate));
        float pdf=0;
        float3 p=origin+t*direction,incoming=0;
        [loop] for (int slot=0;slot<rayCount;++slot)
        {
            if (t<rayBounds[slot].x || t>rayBounds[slot].y) continue;
            int i=rayLights[slot];
            Source source=DescribeLight(i,origin,direction,rayBounds[slot].x,rayBounds[slot].y);
            float falloff;
            incoming+=SourceContribution(i,source,t,p,extinction,falloff);
            pdf+=DistancePdf(source,t,optical,rate,OpticalMass(source,rate));
        }
        radiance+=scatter*incoming*(rayCount/(max(pdf,1e-30)*samples));
    }
    return float4(StoreRadiance(radiance),guide);
}
float4 RasterAirlightPS(PSin input) : SV_Target
{
    int2 pixel=(int2)input.Position.xy;
    // One representative full-resolution ray per 2x2 block. Sampling the actual
    // full-resolution pixel center keeps its guide and integral endpoint aligned.
    if (Quality.HalfResolutionAirlight) pixel=min(2*pixel+1,(int2)Resolution-1);
    return IntegrateAirlight(pixel);
}

float3 ReconstructAirlight(int2 pixel)
{
    uint width,height; RasterAirlight.GetDimensions(width,height);
    float z=RasterDepth.Load(int3(pixel,0));
    float guide=z<1 ? ProjectionLinearDepth(SceneCamera(),z) : 0;
    float2 grid=(pixel-1)*.5;
    // With an odd viewport the last representative is clamped to the final
    // full-resolution pixel, so its final lattice interval is one pixel wide.
    if (((int)Resolution.x&1)!=0 && pixel.x>=Resolution.x-2) grid.x=pixel.x+(int)width-(int)Resolution.x;
    if (((int)Resolution.y&1)!=0 && pixel.y>=Resolution.y-2) grid.y=pixel.y+(int)height-(int)Resolution.y;
    int2 base=(int2)floor(grid); float2 f=frac(grid);
    float3 sum=0; float weight=0;
    [unroll] for (int y=0;y<2;++y) [unroll] for (int x=0;x<2;++x)
    {
        int2 at=clamp(base+int2(x,y),0,int2(width,height)-1);
        float4 value=RasterAirlight.Load(int3(at,0));
        if ((guide==0)!=(value.a==0)) continue;
        float difference=abs(value.a-guide)/max(.05,.02*guide);
        float spatial=(x==0 ? 1-f.x : f.x)*(y==0 ? 1-f.y : f.y);
        float w=spatial*exp2(-2*difference*difference);
        sum+=w*value.rgb; weight+=w;
    }
    // Thin/disoccluded geometry can have no matching coarse ray. Integrate that
    // pixel instead of leaking background fog or leaving a dark reconstruction hole.
    [branch] if (weight<1e-5) return IntegrateAirlight(pixel).rgb;
    return sum/weight;
}

float3 FilterAirlight(int2 pixel)
{
    [branch] if (Quality.HalfResolutionAirlight) return ReconstructAirlight(pixel);
    float4 center=RasterAirlight.Load(int3(pixel,0));
    if (!Filter.Enabled) return center.rgb;
    float3 sum=0; float weight=0;
    [unroll] for (int y=-1;y<=1;++y) [unroll] for (int x=-1;x<=1;++x)
    {
        int2 at=clamp(pixel+int2(x,y),0,(int2)Resolution-1);
        float4 value=RasterAirlight.Load(int3(at,0));
        if ((center.a==0)!=(value.a==0)) continue;
        float difference=abs(value.a-center.a)/max(.05,.02*center.a);
        float spatial=(x==0 ? 2 : 1)*(y==0 ? 2 : 1);
        float w=spatial*exp2(-2*difference*difference);
        sum+=w*value.rgb; weight+=w;
    }
    return sum/max(1e-8,weight);
}
#endif
