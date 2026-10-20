#include "../Common/Inputs.hlsl"
#include "../Common/AnalyticVolumes.hlsl"
#include "../Common/FourierMedium.hlsl"

// Audio-independent prototype: a shared positive 125-coefficient Fourier medium
// feeds adaptive deep cubemaps. No per-object loops run in a GPU lighting pass.
struct ShapeSettings { float Count; float Seed; float Size; float Spread; float Repetitions; float Motion; float Speed; };
struct MaterialSettings { bool Enabled; float Density; float3 Scattering; float3 Absorption; };
struct AirSettings { float Density; float3 Scattering; };
struct EmissionSettings { bool Enabled; float Strength; float3 Color; };
struct LightSettings { bool Enabled; float Intensity; float Speed; float Radius; float Orbit; float Phase; float3 Color; };
struct QualitySettings { float Samples; float ShadowResolution; bool FourShadowTaps; bool ExactShadows; };
struct ViewSettings { float CameraDistance; float CameraSpeed; float HorizontalOffset; float Exposure;
    bool ShowDensitySlice; float Slice; bool ShowTransport; float TransportLight; float TransportDepth; };
cbuffer Parameters
{
    ShapeSettings Shape = { .12328767, .25, .75, .85, .5, 0, .1 };
    MaterialSettings Material = { true, .55, float3(.8,.85,.9), float3(.06,.15,.3) };
    AirSettings Air = { .28, float3(.8,.9,1) };
    EmissionSettings Emission = { false, .1, float3(.15,.5,1) };
    LightSettings Light = { true, .55, .35, .25, .8, 0, float3(1,.8,.55) };
    LightSettings Light2 = { true, .45, .3, .25, .4, .25, float3(.15,.5,1) };
    LightSettings Light3 = { true, .3, .25, .25, .6, .5, float3(1,.2,.08) };
    LightSettings Light4 = { true, .28, .2, .25, .55, .75, float3(.2,1,.5) };
    QualitySettings Quality = { .49606299, .5, false, false };
    ViewSettings View = { .4324711, .15, .5, .5, false, .5, false, 0, .7 };
};
cbuffer FourierPassConstants { uint FourierLight; uint FourierFace; float FourierResolution; float FourierPadding; };
Texture2DArray<float4> FourierDeep0;
Texture2DArray<float4> FourierDeep1;
Texture2DArray<float4> FourierDeep2;
Texture2DArray<float4> FourierDeep3;
static const float Pi=3.14159265359;
static const float AirRadius=16;
LightSettings LightAt(int index)
{
    if (index==1) return Light2;
    if (index==2) return Light3;
    if (index==3) return Light4;
    return Light;
}
float3 CloudSpace(float3 p)
{
    float s,c; sincos(T*.2*Shape.Speed,s,c);
    p=float3(c*p.x+s*p.z,p.y,-s*p.x+c*p.z);
    return float3(p.x,.95533649*p.y-.29552021*p.z,.29552021*p.y+.95533649*p.z);
}
float3 LightPosition(int index)
{
    LightSettings light=LightAt(index);
    float angle=T*.5*light.Speed-.8+2*Pi*light.Phase;
    float radius=3+11*light.Orbit;
    return CloudSpace(float3(radius*cos(angle),3*sin(.7*angle+.3),radius*sin(angle)));
}
// Six standard cube faces; both out-of-face filter taps and map construction
// use this same mapping. Array slices are 6*light+face, independent of the camera.
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
    if (a.x>=a.y && a.x>=a.z)
    {
        face=w.x>=0 ? 0 : 1;
        uv=float2(w.x>=0 ? -w.z : w.z,-w.y)/a.x;
    }
    else if (a.y>=a.z)
    {
        face=w.y>=0 ? 2 : 3;
        uv=float2(w.x,w.y>=0 ? w.z : -w.z)/a.y;
    }
    else
    {
        face=w.z>=0 ? 4 : 5;
        uv=float2(w.z>=0 ? w.x : -w.x,-w.y)/a.z;
    }
    uv=.5+.5*uv;
}
struct DeepOutput { float4 A : SV_Target0; float4 B : SV_Target1; float4 C : SV_Target2; float4 D : SV_Target3; };
DeepOutput FourierShadowPS(PSin input)
{
    DeepOutput result=(DeepOutput)0;
    float3 origin=LightPosition(FourierLight),direction=normalize(CubeDirection(FourierFace,input.UV));
    float lo,hi;
    if (!FourierInterval(origin,direction,lo,hi)) return result;
    // Exact cumulative material at 17 candidates; retain the eight most useful
    // depth knots by greedy maximum-error splitting. The resulting curve is a
    // monotone approximation, not a moment reconstruction or a first-hit surface.
    float profile[17]; profile[0]=0;
    [loop] for (int i=1;i<17;++i)
        profile[i]=max(profile[i-1],FourierColumnUnclipped(origin,direction,lo,lerp(lo,hi,i/16.0)));
    int knots[8];
    [unroll] for (int init=0;init<8;++init) knots[init]=16;
    knots[0]=0;
    [loop] for (int count=2;count<8;++count)
    {
        float best=-1; int chosen=1,slot=1;
        [loop] for (int segment=0;segment<count-1;++segment)
        {
            int left=knots[segment],right=knots[segment+1];
            [loop] for (int j=left+1;j<right;++j)
            {
                float fit=lerp(profile[left],profile[right],(j-left)/(float)(right-left));
                float error=abs(profile[j]-fit);
                if (error>best) { best=error; chosen=j; slot=segment+1; }
            }
        }
        [unroll] for (int shift=7;shift>0;--shift) if (shift>slot) knots[shift]=knots[shift-1];
        knots[slot]=chosen;
    }
    float2 nodes[8];
    [unroll] for (int node=0;node<8;++node) nodes[node]=float2(lerp(lo,hi,knots[node]/16.0),profile[knots[node]]);
    result.A=float4(nodes[0],nodes[1]); result.B=float4(nodes[2],nodes[3]);
    result.C=float4(nodes[4],nodes[5]); result.D=float4(nodes[6],nodes[7]);
    return result;
}
float DeepColumn(int2 pixel, int slice, float distance)
{
    // Air samples beyond the medium only need total material, one texture read.
    float4 d=FourierDeep3.Load(int4(pixel,slice,0));
    if (distance>=d.z) return d.w;
    float4 a=FourierDeep0.Load(int4(pixel,slice,0));
    if (distance<=a.x) return 0;
    float4 b=FourierDeep1.Load(int4(pixel,slice,0)),c=FourierDeep2.Load(int4(pixel,slice,0));
    float2 nodes[8]={a.xy,a.zw,b.xy,b.zw,c.xy,c.zw,d.xy,d.zw};
    if (distance<=nodes[0].x) return 0;
    [unroll] for (int i=0;i<7;++i)
        if (distance<nodes[i+1].x)
            return lerp(nodes[i].y,nodes[i+1].y,saturate((distance-nodes[i].x)/max(1e-7,nodes[i+1].x-nodes[i].x)));
    return nodes[7].y;
}
uint SampleHash(uint v) { v^=v>>16; v*=0x7feb352d; v^=v>>15; v*=0x846ca68b; return v^(v>>16); }
float SampleRandom(uint v) { return ((SampleHash(v)>>9)+.5)/8388608.0; }
float3 MaterialLightTransmission(int light, float3 origin, float3 w, float distance, float3 extinction, uint seed)
{
    if (!any(extinction>0)) return 1;
    float entry,exit;
    if (!FourierInterval(origin,w,entry,exit) || distance<=entry) return 1;
    if (Quality.ExactShadows) return exp(-extinction*FourierColumn(origin,w,0,distance));
    int face; float2 uv; CubeAddress(w,face,uv);
    uint width,height,slices; FourierDeep0.GetDimensions(width,height,slices);
    float2 at=uv*width-.5;
    int2 base=(int2)floor(at); float2 f=frac(at);
    if (!Quality.FourShadowTaps)
    {
        // One stochastic bilinear transmission tap per camera sample. Independent
        // fixed spatial hashes avoid time flicker; the volume integral averages
        // taps across its slices. Four-tap filtering remains an inspection mode.
        int2 offset=int2(SampleRandom(seed)<f.x,SampleRandom(seed^0x9e3779b9)<f.y);
        int adjacent; float2 tap;
        CubeAddress(CubeDirection(face,(base+offset+.5)/width),adjacent,tap);
        return exp(-extinction*DeepColumn(clamp((int2)(tap*width),0,(int)width-1),6*light+adjacent,distance));
    }
    float3 transmission=0;
    [unroll] for (int y=0;y<2;++y) [unroll] for (int x=0;x<2;++x)
    {
        int adjacent; float2 tap;
        CubeAddress(CubeDirection(face,(base+int2(x,y)+.5)/width),adjacent,tap);
        int2 pixel=clamp((int2)(tap*width),0,(int)width-1);
        float h=DeepColumn(pixel,6*light+adjacent,distance);
        transmission+=(x ? f.x : 1-f.x)*(y ? f.y : 1-f.y)*exp(-extinction*h);
    }
    return transmission;
}
void Camera(float2 uv, out float3 origin, out float3 direction)
{
    float s,c; sincos(T*.25*View.CameraSpeed,s,c);
    float3 forward=normalize(float3(-9*c-13.5*s,-6,13.5*c-9*s));
    float3 right=normalize(cross(forward,float3(0,1,0))),up=cross(right,forward);
    float distance=40*View.CameraDistance;
    origin=CloudSpace(-distance*forward+(2*View.HorizontalOffset-1)*distance*right);
    float2 screen=(2*uv-1)*float2(max(1,AspectRatio),-max(1,1/AspectRatio));
    direction=CloudSpace(normalize(2*forward+screen.x*right+screen.y*up));
}
bool AirInterval(float3 o, float3 d, out float lo, out float hi)
{
    float b=dot(o,d),disc=b*b-dot(o,o)+AirRadius*AirRadius;
    if (disc<=0) { lo=hi=0; return false; }
    float root=sqrt(disc); lo=max(0,-b-root); hi=-b+root; return hi>lo;
}
float4 Display(float3 radiance)
{
    float3 mapped=1-exp(-max(0,radiance)*exp2(6*(View.Exposure-.5)));
    return float4(lerp(12.92*mapped,1.055*pow(mapped,1/2.4)-.055,step(.0031308,mapped)),1);
}
struct Source { float3 Position; float3 Intensity; float RadiusSquared; };
float4 PS(PSin input) : SV_Target
{
    if (View.ShowDensitySlice)
    {
        float3 p=float3((2*input.UV-1)*FourierBounds.x*float2(AspectRatio,1),(2*View.Slice-1)*FourierBounds.x);
        return Display(2*FourierDensity(p).xxx);
    }
    float concentration=Material.Enabled ? 4*Material.Density*Material.Density : 0;
    float3 extinction=concentration*(Material.Scattering+Material.Absorption);
    if (View.ShowTransport)
    {
        float2 tiled=input.UV*float2(3,2); int2 cell=(int2)tiled; int face=cell.x+3*cell.y;
        int light=(int)round(3*View.TransportLight);
        uint w,h,layers; FourierDeep0.GetDimensions(w,h,layers);
        float column=DeepColumn(clamp((int2)(frac(tiled)*w),0,(int)w-1),6*light+face,32*View.TransportDepth);
        return float4(1-exp(-extinction*column),1);
    }
    float3 scatter=concentration*Material.Scattering;
    float3 airScatter=.12*Air.Density*Air.Density*Air.Scattering;
    float3 emission=Material.Enabled && Emission.Enabled ? 4*Emission.Strength*Emission.Strength*Emission.Color : 0;
    if (!any(scatter>0) && !any(airScatter>0) && !any(emission>0)) return float4(0,0,0,1);
    Source lights[4]; bool anyLight=false;
    [unroll] for (int l=0;l<4;++l)
    {
        LightSettings setting=LightAt(l); Source source=(Source)0;
        if (setting.Enabled)
        {
            source.Position=LightPosition(l);
            source.Intensity=3000*setting.Intensity*setting.Intensity*setting.Color;
            float radius=.01+.49*setting.Radius*setting.Radius;
            source.RadiusSquared=radius*radius;
        }
        lights[l]=source; anyLight=anyLight || any(source.Intensity>0);
    }
    if (!anyLight && !any(emission>0)) return float4(0,0,0,1);
    float3 origin,direction; Camera(input.UV,origin,direction);
    float lo,hi; if (!AirInterval(origin,direction,lo,hi)) return float4(0,0,0,1);
    float materialLo,materialHi; bool crosses=FourierInterval(origin,direction,materialLo,materialHi);
    bool materialActive=crosses && (concentration>0 || any(emission>0));
    int samples=1+(int)round(127*Quality.Samples);
    float dt=(hi-lo)/samples;
    float4 airChord=EllipsoidChord(origin,direction,0,1/AirRadius,1);
    // Per-ray sinc factors integrate every complete camera cell analytically.
    // Only its illumination/phase is sampled. Axis phase recurrence avoids
    // calling sincos for every Fourier mode at every camera sample.
    float weights[63];
    FourierPhase phase=(FourierPhase)0;
    float3 sine=0,cosine=1;
    if (materialActive)
    {
        [unroll] for (int m=0;m<63;++m)
            weights[m]=dt*FourierSinc(.5*FourierBounds.y*dot(float3(ModeFrequency(m)),direction)*dt);
        phase=MakeFourierPhase(origin+(lo+.5*dt)*direction);
        sincos(FourierBounds.y*dt*direction,sine,cosine);
    }
    float3 radiance=0,transmission=1;
    [loop] for (int sample=0;sample<samples;++sample)
    {
        float begin=lo+sample*dt,end=begin+dt;
        float materialMass=0;
        if (materialActive)
        {
            if (begin>=materialLo && end<=materialHi)
            {
                [unroll] for (int k=0;k<63;++k)
                    materialMass+=weights[k]*dot(FourierModes[k].xy,ModePhase(phase,ModeFrequency(k)));
                materialMass=max(0,materialMass);
            }
            else if (end>materialLo && begin<materialHi)
                materialMass=FourierColumnUnclipped(origin,direction,max(begin,materialLo),min(end,materialHi));
        }
        float airMass=BlobColumn(airChord,begin,end);
        float3 p=origin+(begin+.5*dt)*direction;
        float3 incoming=0;
        if (any(scatter*materialMass+airScatter*airMass>0))
        {
            [loop] for (int light=0;light<4;++light)
            {
                Source source=lights[light]; if (!any(source.Intensity>0)) continue;
                float3 delta=source.Position-p;
                float r2=dot(delta,delta),r=sqrt(r2); if (r<=1e-6) continue;
                float3 w=delta/r;
                uint2 pixel=(uint2)input.Position.xy;
                uint seed=SampleHash(pixel.x^SampleHash(pixel.y+1)^SampleHash((uint)(sample*4+light+1)));
                float3 t=MaterialLightTransmission(light,source.Position,-w,r,extinction,seed);
                float4 chord=EllipsoidChord(p,w,0,1/AirRadius,1);
                t*=exp(-airScatter*BlobColumn(chord,0,r));
                float mu=dot(w,direction),phaseValue=3*(1+mu*mu)/(16*Pi);
                incoming+=source.Intensity/(r2+source.RadiusSquared)*t*phaseValue;
            }
        }
        float3 tau=extinction*materialMass+airScatter*airMass;
        float3 cellTransmission=exp(-tau);
        float3 factor=float3(tau.x<.001 ? 1-tau.x*.5+tau.x*tau.x/6 : (1-cellTransmission.x)/max(tau.x,1e-20),
            tau.y<.001 ? 1-tau.y*.5+tau.y*tau.y/6 : (1-cellTransmission.y)/max(tau.y,1e-20),
            tau.z<.001 ? 1-tau.z*.5+tau.z*tau.z/6 : (1-cellTransmission.z)/max(tau.z,1e-20));
        radiance+=transmission*factor*((scatter*materialMass+airScatter*airMass)*incoming+emission*materialMass);
        transmission*=cellTransmission;
        if (materialActive) AdvanceFourierPhase(phase,cosine,sine);
    }
    return Display(radiance);
}
