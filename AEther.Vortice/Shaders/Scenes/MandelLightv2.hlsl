#include "../Common/Projection.hlsl"
#include "../Common/AnalyticVolumes.hlsl"

// Audio-independent prototype. Additive ellipsoid densities, exact segment
// attenuation, sampled single scattering. No surfaces, moments, raymarches,
// image history or transport caches. Silence exemption ends at production use.
struct ShapeSettings { float Count; float Seed; float Size; float Spread; float Speed; };
struct MaterialSettings { bool Enabled; float Density; float3 Scattering; float3 Absorption; };
struct AirSettings { float Density; float3 Scattering; };
struct EmissionSettings { bool Enabled; float Strength; float3 Color; };
struct LightSettings { bool Enabled; float Intensity; float Speed; float Radius; float Phase; float3 Color; };
struct QualitySettings { float Samples; };
struct ViewSettings { float CameraDistance; float CameraSpeed; float HorizontalOffset; float Exposure; };
cbuffer Parameters
{
    ShapeSettings Shape = { .46666667, .25, .5, .6, .15 };
    MaterialSettings Material = { true, .55, float3(.8,.85,.9), float3(.04,.12,.2) };
    AirSettings Air = { .2, float3(.8,.9,1) };
    EmissionSettings Emission = { false, .12, float3(.15,.5,1) };
    LightSettings Light = { true, .65, .35, .2, 0, float3(1,.72,.45) };
    LightSettings Light2 = { true, .55, .25, .2, .3, float3(.2,.55,1) };
    LightSettings Light3 = { false, .4, .35, .2, .55, float3(1,.2,.08) };
    LightSettings Light4 = { false, .4, .35, .2, .8, float3(.2,1,.45) };
    QualitySettings Quality = { .49206349 };
    ViewSettings View = { .4324711, .15, .5, .5 };
};
static const float Pi=3.14159265359;
static const float AirRadius=16;
static const int MaxBlobs=17; // One spherical air component plus up to 16 blobs.

uint Hash(uint x)
{
    x^=x>>16; x*=0x7feb352d; x^=x>>15; x*=0x846ca68b; return x^(x>>16);
}
float Random(uint x) { return ((Hash(x)>>9)+.5)/8388608.0; }
float Mean(float3 x) { return dot(x,float3(1,1,1)/3); }

ProjectionCamera SceneCamera()
{
    float3 origin;
    float sine,cosine; sincos(T*.25*View.CameraSpeed,sine,cosine);
    float3 forward=normalize(float3(-9*cosine-13.5*sine,-6,13.5*cosine-9*sine));
    float3 right=normalize(cross(forward,float3(0,1,0)));
    float3 up=cross(right,forward);
    float distance=40*View.CameraDistance;
    origin=-distance*forward+(2*View.HorizontalOffset-1)*distance*right;
    return MakeProjectionCamera(origin,forward,right,up);
}
float3 CloudSpace(float3 p, float sine, float cosine)
{
    // One shared rigid orientation: ellipsoids stay axis-aligned in this space.
    // Directions and points get the same rotation, preserving all ray lengths.
    p=float3(cosine*p.x+sine*p.z,p.y,-sine*p.x+cosine*p.z);
    return float3(p.x,.95533649*p.y-.29552021*p.z,.29552021*p.y+.95533649*p.z);
}
bool AirInterval(float3 o, float3 d, out float lo, out float hi)
{
    float b=dot(o,d), c=dot(o,o)-AirRadius*AirRadius;
    float disc=b*b-c;
    if (disc<=0) { lo=hi=0; return false; }
    float root=sqrt(disc);
    lo=max(0,-b-root); hi=-b+root;
    return hi>lo+1e-5;
}

struct Blob
{
    float3 Center;
    float3 InverseRadii;
    float Strength;
};
struct Beam
{
    float4 Chord;
    // Truncated tent CDF at near, CDF span, near-side warp, selection weight.
    float4 Guide;
};
Blob MakeBlob(int index)
{
    Blob b=(Blob)0;
    if (index==0)
    {
        b.InverseRadii=1/AirRadius;
        b.Strength=1;
        return b;
    }
    // A prefix-stable sequence: changing Count does not move existing blobs.
    uint seed=Hash((uint)round(65535*Shape.Seed)^((uint)index*0x9e3779b9));
    float3 r=float3(Random(seed),Random(seed+1),Random(seed+2));
    float3 v=2*r-1;
    float3 axis=v*rsqrt(max(dot(v,v),1e-8));
    b.Center=axis*(5.5*Shape.Spread*pow(Random(seed+3),1.0/3.0));
    float size=.7+3.3*Shape.Size;
    float3 radii=size*(.65+.9*float3(Random(seed+4),Random(seed+5),Random(seed+6)));
    b.Strength=.65+.7*Random(seed+7);
    if (index==1) { b.Center*=.3; radii*=1.15; }
    b.InverseRadii=1/radii;
    return b;
}
float4 MakeGuide(float4 chord, float lo, float hi, float extinction, float source)
{
    float mass=BlobColumn(chord,lo,hi);
    if (mass<=1e-10 || source<=0) return 0;
    float first=BlobTentCdf((lo-chord.x)*chord.y);
    float span=BlobTentCdf((hi-chord.x)*chord.y)-first;
    if (span<=1e-7) return 0;
    float depth=extinction*mass;
    // Optical depth approximately limits a component's visible contribution.
    // The warp is a proposal only; its exact PDF appears in the estimator.
    return float4(first,span,1+min(64,depth),source*mass/(1+depth));
}
float SampleBlob(Beam b, float u)
{
    float warp=b.Guide.z;
    float v=u/(warp-(warp-1)*u);
    float z=BlobTentInverse(saturate(b.Guide.x+b.Guide.y*v));
    return b.Chord.x+z/b.Chord.y;
}
float BlobPdf(Beam b, float t)
{
    if (b.Guide.w<=0) return 0;
    float z=(t-b.Chord.x)*b.Chord.y;
    float v=(BlobTentCdf(z)-b.Guide.x)/b.Guide.y;
    if (v<0 || v>1 || abs(z)>=1) return 0;
    float denominator=1+(b.Guide.z-1)*v;
    return (1-abs(z))*b.Chord.y/b.Guide.y*b.Guide.z/(denominator*denominator);
}
LightSettings Settings(int i)
{
    if (i==1) return Light2;
    if (i==2) return Light3;
    if (i==3) return Light4;
    return Light;
}
struct Source
{
    float3 Position;
    float3 Intensity;
    float RadiusSquared;
    // Camera projection, softened perpendicular distance, near angle, angle span.
    float4 Angular;
    float Weight;
};
Source MakeSource(int i, float3 origin, float3 direction, float lo, float hi, float sine, float cosine)
{
    Source s=(Source)0;
    LightSettings light=Settings(i);
    if (!light.Enabled || light.Intensity<=0 || !any(light.Color>0)) return s;
    float angle=T*.5*light.Speed-.8+2*Pi*light.Phase;
    s.Position=CloudSpace(float3(12*cos(angle),3*sin(.7*angle+.3),12*sin(angle)),sine,cosine);
    s.Intensity=3000*light.Intensity*light.Intensity*light.Color;
    float radius=.01+.49*light.Radius*light.Radius;
    s.RadiusSquared=radius*radius;
    float3 delta=s.Position-origin;
    float projection=dot(delta,direction);
    float3 perpendicular=delta-projection*direction;
    float h=sqrt(dot(perpendicular,perpendicular)+s.RadiusSquared);
    float nearAngle=atan2(lo-projection,h);
    float span=atan2(hi-projection,h)-nearAngle;
    s.Angular=float4(projection,h,nearAngle,span);
    s.Weight=Mean(s.Intensity)/max(1,dot(perpendicular,perpendicular));
    // Very distant, almost axial intervals can lose angular span in FP32.
    if (span<=1e-6) s.Weight=0;
    return s;
}
float AngularPdf(Source s, float t)
{
    float offset=t-s.Angular.x;
    return s.Angular.y/(s.Angular.w*(s.Angular.y*s.Angular.y+offset*offset));
}
float Phase(float mu) { return 3*(1+mu*mu)/(16*Pi); }
bool SegmentHitsBox(float3 p, float3 w, float distance, float3 lo, float3 hi)
{
    float3 inverse=(2*step(0,w)-1)/max(abs(w),1e-20);
    float3 a=(lo-p)*inverse, b=(hi-p)*inverse;
    float3 first=min(a,b), last=max(a,b);
    return max(0,max(first.x,max(first.y,first.z)))<min(distance,min(last.x,min(last.y,last.z)));
}

float4 PS(PSin input) : SV_Target
{
    ProjectionCamera camera=SceneCamera();
    float3 origin=camera.Origin,direction=ProjectionDirection(camera,input.UV);
    float sine,cosine; sincos(T*.2*Shape.Speed,sine,cosine);
    origin=CloudSpace(origin,sine,cosine); direction=CloudSpace(direction,sine,cosine);
    float nearT,farT;
    if (!AirInterval(origin,direction,nearT,farT) || !ProjectionClipInterval(camera,ProjectionDirection(camera,input.UV),nearT,farT)) return float4(0,0,0,1);

    float concentration=Material.Enabled ? 4*Material.Density*Material.Density : 0;
    float3 scatter=concentration*Material.Scattering;
    float3 extinction=concentration*(Material.Scattering+Material.Absorption);
    float3 airScatter=.12*Air.Density*Air.Density*Air.Scattering;
    float3 emission=Material.Enabled && Emission.Enabled ? 4*Emission.Strength*Emission.Strength*Emission.Color : 0;
    if (!any(scatter>0) && !any(airScatter>0) && !any(emission>0)) return float4(0,0,0,1);

    int count=1+(int)round(15*Shape.Count);
    if (!Material.Enabled || (concentration<=0 && !any(emission>0))) count=0;
    int components=count+1;
    Blob blobs[MaxBlobs];
    // Keep camera data compact and separate from world geometry. Only intersected
    // blobs enter the per-sample camera/PDF loop; every blob can still cast shadows.
    Beam beams[MaxBlobs];
    int beamCount=0;
    float3 boxLo=0,boxHi=0;
    float guideTotal=0;
    [loop] for (int i=0;i<components;++i)
    {
        Blob b=MakeBlob(i);
        Beam beam;
        beam.Chord=EllipsoidChord(origin,direction,b.Center,b.InverseRadii,b.Strength);
        beam.Guide=MakeGuide(beam.Chord,nearT,farT,i==0 ? Mean(airScatter) : Mean(extinction),
            i==0 ? Mean(airScatter) : Mean(scatter+emission));
        guideTotal+=beam.Guide.w;
        blobs[i]=b;
        bool intersects=beam.Chord.w>0 && (nearT-beam.Chord.x)*beam.Chord.y<1
            && (farT-beam.Chord.x)*beam.Chord.y>-1;
        if (i==0 || intersects) beams[beamCount++]=beam;
        if (i>0)
        {
            float3 radius=1/b.InverseRadii+1e-4;
            boxLo=min(boxLo,b.Center-radius); boxHi=max(boxHi,b.Center+radius);
        }
    }
    Source lights[4];
    float lightTotal=0;
    bool anyLight=false;
    [unroll] for (int l=0;l<4;++l)
    {
        lights[l]=MakeSource(l,origin,direction,nearT,farT,sine,cosine);
        lightTotal+=lights[l].Weight;
        anyLight=anyLight || any(lights[l].Intensity>0);
    }
    if (!anyLight && !any(emission>0)) return float4(0,0,0,1);

    // One normalized mixture, one shared sample set for every light and emission.
    // Density tents favor actual support; equiangular proposals catch light peaks.
    // A small uniform floor covers numerical degeneracies and all other intervals.
    float angularMix=lightTotal>0 ? .3 : 0;
    float densityMix=guideTotal>0 ? .95-angularMix : 0;
    float uniformMix=1-densityMix-angularMix;
    float rayLength=farT-nearT;
    uint2 pixel=(uint2)input.Position.xy;
    uint seed=Hash(pixel.x^Hash(pixel.y+0x9e3779b9));
    int samples=1+(int)round(63*Quality.Samples);
    float3 radiance=0;
    [loop] for (int sampleIndex=0;sampleIndex<samples;++sampleIndex)
    {
        float jitter=Random(seed^Hash((uint)sampleIndex+1));
        float u=min((sampleIndex+jitter)/samples,.99999994);
        float t;
        if (u<densityMix)
        {
            float pick=u/densityMix*guideTotal;
            t=nearT+u/densityMix*rayLength;
            [loop] for (int b=0;b<beamCount;++b)
            {
                if (pick<beams[b].Guide.w)
                {
                    t=SampleBlob(beams[b],saturate(pick/beams[b].Guide.w));
                    break;
                }
                pick-=beams[b].Guide.w;
            }
        }
        else if (u<densityMix+angularMix)
        {
            float pick=(u-densityMix)/angularMix*lightTotal;
            t=nearT+(u-densityMix)/angularMix*rayLength;
            [unroll] for (int a=0;a<4;++a)
            {
                if (pick<lights[a].Weight)
                {
                    float v=saturate(pick/lights[a].Weight);
                    t=lights[a].Angular.x+lights[a].Angular.y*tan(lights[a].Angular.z+v*lights[a].Angular.w);
                    break;
                }
                pick-=lights[a].Weight;
            }
        }
        else t=nearT+(u-densityMix-angularMix)/uniformMix*rayLength;
        t=clamp(t,nearT,farT);

        float pdf=uniformMix/rayLength;
        float materialDensity=0, materialColumn=0;
        [loop] for (int j=0;j<beamCount;++j)
        {
            if (guideTotal>0) pdf+=densityMix*beams[j].Guide.w/guideTotal*BlobPdf(beams[j],t);
            if (j>0)
            {
                materialDensity+=BlobDensity(beams[j].Chord,t);
                materialColumn+=BlobColumn(beams[j].Chord,nearT,t);
            }
        }
        [unroll] for (int k=0;k<4;++k)
            if (lights[k].Weight>0) pdf+=angularMix*lights[k].Weight/lightTotal*AngularPdf(lights[k],t);
        float airDensity=BlobDensity(beams[0].Chord,t);
        float airColumn=BlobColumn(beams[0].Chord,nearT,t);
        float3 cameraTransmission=exp(-extinction*materialColumn-airScatter*airColumn);
        float3 localScatter=scatter*materialDensity+airScatter*airDensity;
        float3 incoming=0;
        float3 p=origin+t*direction;
        if (any(localScatter>0))
        {
            [unroll] for (int lightIndex=0;lightIndex<4;++lightIndex)
            {
                Source source=lights[lightIndex];
                if (!any(source.Intensity>0)) continue;
                float3 delta=source.Position-p;
                float distanceSquared=dot(delta,delta);
                float distance=sqrt(distanceSquared);
                if (distance<=1e-6) continue;
                float3 w=delta/distance;
                float column=0;
                if (count>0 && any(extinction>0) && SegmentHitsBox(p,w,distance,boxLo,boxHi))
                {
                    [loop] for (int shadow=1;shadow<components;++shadow)
                    {
                        Blob volume=blobs[shadow];
                        float4 chord=EllipsoidChord(p,w,volume.Center,volume.InverseRadii,volume.Strength);
                        column+=BlobColumn(chord,0,distance);
                    }
                }
                float4 airChord=EllipsoidChord(p,w,0,1/AirRadius,1);
                float airDepth=BlobColumn(airChord,0,distance);
                float3 transmission=exp(-extinction*column-airScatter*airDepth);
                incoming+=source.Intensity/(distanceSquared+source.RadiusSquared)
                    *transmission*Phase(dot(w,direction));
            }
        }
        radiance+=cameraTransmission*(emission*materialDensity+localScatter*incoming)/max(pdf,1e-20);
    }
    radiance*=exp2(6*(View.Exposure-.5))/samples;
    float3 mapped=1-exp(-max(0,radiance));
    float3 encoded=lerp(12.92*mapped,1.055*pow(mapped,1/2.4)-.055,step(.0031308,mapped));
    return float4(encoded,1);
}
