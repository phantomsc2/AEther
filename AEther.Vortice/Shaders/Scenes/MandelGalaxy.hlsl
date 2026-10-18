#define GEOMETRY_VERTEX_COUNT 54
#define GEOMETRY_INSTANCE_COUNT 65536
#define GEOMETRY_ADDITIVE
#include "../Common/Inputs.hlsl"

struct GalaxyLayout { float Population; float Radius; float Spacing; float Arms; float Winding; float Thickness; float Inclination; float Eccentricity; };
struct GalaxyOrbit { float Speed; float Differential; float Phase; float AudioGain; float StereoWidth; };
struct GalaxyCamera { float Distance; float Tilt; float Heading; float Speed; bool Orbit; };
struct GalaxyLayers { bool Cores; bool Gas; bool NoteRays; bool Mandelstars; bool HistogramTrails; bool Shells; bool Starfield; };
struct GalaxyLook { float Size; float Gas; float Rays; float Fractal; float Exposure; };
struct GalaxyTrails { float Length; float Width; float Falloff; float AetherSpeed; float AetherHeading; float AetherElevation; };
struct GalaxySpectrum { float Radial; float Arm; float Orbit; float Scatter; };
cbuffer Parameters : register(b1)
{
    GalaxyLayout Galaxy = { .5, .65, .45, .35, .55, .15, .15, .15 };
    GalaxyOrbit Orbits = { .25, .6, .1, .55, 1 };
    GalaxyCamera Camera = { .2, .45, .1, .15, true };
    GalaxyLayers Layers = { true, true, true, true, true, true, true };
    GalaxyLook Appearance = { .4, .5, .6, .65, .4 };
    GalaxyTrails Trails = { 1, .4, .35, .25, .65, .45 };
    GalaxySpectrum Spectrum = { .65, .27, .08, .035 };
};
float Hash(float n) { return frac(sin(n * 127.1 + 311.7) * 43758.5453); }
float Noise(float2 p)
{
    float2 i = floor(p), f = frac(p); f = f*f*(3-2*f);
    float n = i.x + i.y*157;
    return lerp(lerp(Hash(n), Hash(n+1), f.x), lerp(Hash(n+157), Hash(n+158), f.x), f.y);
}
float3 Palette(float f) { return .5 + .5*cos(6.2831853*(f*.8 + float3(.04,.35,.67))); }
float4 AudioAt(float frequency, float age, float pan, out float bandFrequency)
{
    uint bands, rows;
    Histogram0.GetDimensions(bands, rows);
    // Select one frequency column. Never filter across neighboring notes.
    int band = min((int)(saturate(frequency)*bands), (int)bands-1);
    bandFrequency = (band+.5)/bands;
    float4 now = lerp(Spectrum0.Load(int3(band,0,0)), Spectrum1.Load(int3(band,0,0)),pan);
    float offset = age * HistorySpan;
    float4 past = 0;
    if (offset >= HistoryNewestAge && offset <= HistoryAvailable && age < 1)
    {
        float row = frac(HistogramShift-offset)*rows-.5;
        int low = (int)floor(row);
        int y0 = low < 0 ? (int)rows-1 : low;
        int y1 = y0+1 >= (int)rows ? 0 : y0+1;
        float4 left = lerp(Histogram0.Load(int3(band,y0,0)),Histogram0.Load(int3(band,y1,0)),frac(row));
        float4 right = lerp(Histogram1.Load(int3(band,y0,0)),Histogram1.Load(int3(band,y1,0)),frac(row));
        past = lerp(left,right,pan);
    }
    return 1-exp(-max(0,lerp(past,now,1-smoothstep(0,.025,age)))*(2+22*Orbits.AudioGain));
}
void View(float time, out float3 eye, out float3 right, out float3 up, out float3 forward)
{
    float heading = Camera.Heading*6.2831853 + (Camera.Orbit ? time*.025*Camera.Speed : 0);
    float tilt = .03 + Camera.Tilt*1.45;
    float distance = 25 + 900*Camera.Distance*Camera.Distance;
    eye = distance*float3(sin(heading)*cos(tilt),sin(tilt),cos(heading)*cos(tilt));
    forward = -normalize(eye); right = normalize(cross(float3(0,1,0),forward)); up = cross(forward,right);
}
struct StarVertex
{
    float4 Position : SV_POSITION;
    float2 UV : TEXCOORD0;
    float Frequency : TEXCOORD1;
    nointerpolation float Seed : TEXCOORD2;
    nointerpolation uint Trail : TEXCOORD3;
    nointerpolation float Lod : TEXCOORD4;
};
float OrbitFrequency(uint index) { return pow((index+.5)/65536.0,.4+1.4*Galaxy.Spacing); }
float OrbitRadius(uint index) { return (8+650*Galaxy.Radius)*(.035+.965*Hash(index+73)); }
float ArmDistance(float radius)
{
    // Simpson integration of arc length along the logarithmic spiral:
    // theta(r) = 12 * Winding * log(1 + .08*r).
    float2 q=.08*radius*float2(.5,1);
    float2 slope=12*Galaxy.Winding*q/(1+q);
    float2 speed=sqrt(1+slope*slope);
    return radius*(1+4*speed.x+speed.y)/6;
}
float SampleFrequency(uint index,float3 center,float2 coordinates)
{
    // World-space shells stay centered on the galaxy as the camera moves.
    // Include the companion orbits so the highest bands reach the outer rim.
    float extent = (8+650*Galaxy.Radius)+1.36*(2+30*Galaxy.Radius);
    float radial=saturate(length(center)/extent);
    float weight=Spectrum.Radial+Spectrum.Arm+Spectrum.Orbit;
    float frequency=weight>1e-5
        ? dot(float3(radial,coordinates),float3(Spectrum.Radial,Spectrum.Arm,Spectrum.Orbit))/max(weight,1e-5)
        : radial;
    frequency+=Spectrum.Scatter*(Hash(index+211)-.5);
    return pow(saturate(frequency),.4+1.4*Galaxy.Spacing);
}
float3 OrbitPoint(uint index,float time,out float angle)
{
    float seed=Hash(index+1), f=OrbitFrequency(index);
    // Seeded orbital roles preserve the stellar and companion hierarchy.
    float radius=OrbitRadius(index);
    float arms=1+floor(Galaxy.Arms*11);
    angle=fmod(index,arms)*6.2831853/arms+(seed-.5)*.42
        +Galaxy.Winding*12*log(1+radius*.08);
    angle+=time*(.003+.15*Orbits.Speed)*pow(max(.08,f),-1.5*Orbits.Differential)+Orbits.Phase*6.2831853;
    float e=.7*Galaxy.Eccentricity;
    float3 p=float3(radius*cos(angle),(Hash(index+29)-.5)*radius*Galaxy.Thickness*.35,
        radius*sin(angle)*sqrt(1-e*e));
    return mul(Rx((seed-.5)*Galaxy.Inclination*1.5),p);
}
float3 StarPoint(uint spectralIndex,float time,out float2 coordinates)
{
    float seed=Hash(spectralIndex+1), frequency=OrbitFrequency(spectralIndex);
    float angle;
    float armRadius=OrbitRadius(spectralIndex);
    float3 center = OrbitPoint(spectralIndex,time,angle);
    if (frequency >= .12)
    {
        uint root = spectralIndex%512u;
        armRadius=OrbitRadius(root);
        float parentAngle;
        float3 anchor = OrbitPoint(root,time,parentAngle);
        float localAngle = seed*6.2831853 + time*(.02+.4*Orbits.Speed)*(1+3*frequency);
        float localRadius = (2+30*Galaxy.Radius)*(.2+Hash(spectralIndex+41));
        if (frequency >= .55)
        {
            // Shared analytic planet orbit, then a smaller moon/dust orbit.
            uint planet = 8192u + ((spectralIndex/512u)%16u)*512u + root;
            float planetSeed = Hash(planet+1);
            float planetAngle = planetSeed*6.2831853 + time*(.02+.4*Orbits.Speed)*(1+3*OrbitFrequency(planet));
            float planetRadius = (2+30*Galaxy.Radius)*(.2+Hash(planet+41));
            anchor += float3(cos(planetAngle),.15*sin(planetAngle*2),sin(planetAngle))*planetRadius;
            localRadius *= frequency >= .85 ? .025 : .12;
        }
        center = anchor + mul(Rx((seed-.5)*Galaxy.Inclination*2),
            float3(cos(localAngle),.1*sin(localAngle*2),sin(localAngle))*localRadius);
        angle = localAngle;
    }
    coordinates=float2(ArmDistance(armRadius)/ArmDistance(8+650*Galaxy.Radius),.5+.5*sin(angle));
    return center;
}
float3 TrailPoint(uint index,float age,float3 eye,float3 right,float3 up,float3 forward,out float frequency)
{
    // Reconstruct the star's previous SCREEN position, including camera motion.
    // Keep the old depth so these remain perspective ribbons with parallax.
    float seconds=age*max(.001,HistorySeconds > 0 ? HistorySeconds : .3);
    float pastTime=T-seconds;
    float3 oldEye,oldRight,oldUp,oldForward;
    View(pastTime,oldEye,oldRight,oldUp,oldForward);
    float2 coordinates;
    float3 center=StarPoint(index,pastTime,coordinates);
    frequency=SampleFrequency(index,center,coordinates);
    float3 past=center-oldEye;
    float3 world=eye+right*dot(past,oldRight)+up*dot(past,oldUp)+forward*dot(past,oldForward);
    // One world-space aether velocity for the entire swarm, independent of
    // frequency, size, parent orbit or stereo coordinate. Zero at the head.
    float heading=Trails.AetherHeading*6.2831853;
    float elevation=(Trails.AetherElevation-.5)*3.14159265;
    float3 flow=float3(cos(heading)*cos(elevation),sin(elevation),sin(heading)*cos(elevation));
    return world+flow*(120*Trails.AetherSpeed*seconds);
}
StarVertex GeometryVS(uint id : SV_VertexID, uint instance : SV_InstanceID)
{
    // No particle buffers, uploads, integration or pairwise forces. Stable IDs.
    uint spectralIndex = (instance*40503u)&65535u;
    float seed = Hash(spectralIndex+1);
    float orbitFrequency = OrbitFrequency(spectralIndex);
    float2 coordinates;
    float3 center = StarPoint(spectralIndex,T,coordinates);
    float frequency = SampleFrequency(spectralIndex,center,coordinates);
    float3 eye,right,up,forward; View(T,eye,right,up,forward);
    float size = (.08+2.4*Appearance.Size*Appearance.Size)*lerp(.7,1.2,Hash(instance+83))
        * (.025+3*pow(saturate(1-orbitFrequency),3.5));
    uint quad = id/6, corner = id%6;
    float2 uv = corner==0 ? float2(0,0) : corner==1 ? float2(0,1) : corner==2 || corner==3 ? float2(1,0) : corner==4 ? float2(0,1) : float2(1,1);
    float3 world;
    if (quad==0) world = center + ((uv.x*2-1)*right+(1-uv.y*2)*up)*size*3;
    else
    {
        // Eight connected segments trace the actual hierarchical orbit and view.
        float age=(quad-1+uv.y)/8.0*(.05+.95*Trails.Length);
        world=TrailPoint(spectralIndex,age,eye,right,up,forward,frequency);
        float nearbyFrequency;
        float3 nearby=TrailPoint(spectralIndex,age+.002,eye,right,up,forward,nearbyFrequency);
        float3 direction=nearby-world;
        float3 side=cross(direction,forward);
        side=dot(side,side)>1e-10 ? normalize(side) : right;
        world+=side*(uv.x*2-1)*size*(.1+Trails.Width);
        uv.y=age;

    }
    float3 relative = world-eye;
    float3 v = float3(dot(relative,right),dot(relative,up),dot(relative,forward));
    StarVertex o;
    o.Position = float4(v.x*1.7/AspectRatio,v.y*1.7,4000.0/3999.9*v.z-.1000025,v.z);
    float centerDepth = dot(center-eye,forward);
    float pixels = size*Resolution.y*.85/max(.1,centerDepth);
    bool visible = instance < (uint)(1024+64512*Galaxy.Population) && centerDepth>.1;
    visible = visible && (quad==0 || (Layers.HistogramTrails && pixels>.3));
    // Entire instance pieces collapse outside the view; expensive shading is skipped.
    if (!visible) o.Position=float4(2,2,2,1);
    o.UV = quad==0 ? (uv*2-1)*3 : uv;
    o.Frequency=frequency; o.Seed=seed; o.Trail=quad>0; o.Lod=pixels;
    return o;
}
float Slice(float2 p,float seed)
{
    float3 c=float3(p*.6,.25+.2*sin(seed*6.28+S*.025)), z=c; float d=1;
    [unroll] for(int i=0;i<6;i++)
    {
        z=2*clamp(z,-1,1)-z; float fold=1/clamp(dot(z,z),.25,1);
        z=-1.65*fold*z+c; d=1.65*fold*d+1;
    }
    float phase=3.3*log2(max(1e-7,length(z)/d));
    return lerp(exp(-9*abs(sin(phase))),.12,smoothstep(.15,.65,fwidth(phase)));
}
float4 PS(StarVertex input) : SV_Target
{
    float pan = .5 + (Hash(input.Seed*113+7)-.5)*Orbits.StereoWidth;
    float bandFrequency;
    float4 audio=AudioAt(input.Frequency,input.Trail ? input.UV.y : 0,pan,bandFrequency);
    float4 global=MusicAtAge(input.Trail ? input.UV.y : 0,.025);
    float bed=MusicBody(global), structure=MusicStructure(global);
    float rise=MusicRise(global), interaction=MusicInteraction(global);
    float3 bandColor=Palette(bandFrequency);
    // A band's tone/noise deepens its hue; its transients add a pale flash.
    // All modulation uses the same mapped band and the same history age.
    float saturation=.45+.55*saturate(audio.x+.5*audio.z);
    float3 color=lerp(dot(bandColor,float3(.2126,.7152,.0722)).xxx,bandColor,saturation);
    color=lerp(color,float3(1,1,1),.3*max(audio.y,.5*rise));
    if(input.Trail)
    {
        float x=input.UV.x*2-1, age=input.UV.y/(.05+.95*Trails.Length);
        float profile=exp(-18*x*x)*(1-smoothstep(.85,1,age))*exp(-age*(.5+6*Trails.Falloff));
        // Follow the historical mapped band's tone. Noise and transients can
        // tint an existing trail, but cannot illuminate a silent tonal band.
        float light=audio.x*(1+.6*bed+.35*structure);
        return float4(color*profile*light*.45,0);
    }
    float2 p=input.UV; float r=length(p); clip(2.95-r);
    float cloud=Noise(p*2+float2(input.Seed*71,MusicalMovementTime*.09));
    float gas=Layers.Gas ? max(audio.z,.15*bed)*cloud*exp(-r*r*.65)*Appearance.Gas*(1+.5*interaction) : 0;
    float rays=pow(saturate(.5+.5*cos(atan2(p.y,p.x)*(6+floor(input.Frequency*18))+input.Seed*17)),18);
    if(Layers.NoteRays) gas*=1+audio.x*rays*(3+24*Appearance.Rays);
    float3 light=color*gas;
    float core=1-smoothstep(.7+.1*structure,1+.16*structure,r);
    if(Layers.Cores) light+=color*core*(.045*bed+.65*audio.x)*(1+.4*rise);
    if(Layers.Shells) light+=color*exp(-25*abs(r-1.15))*(.035*bed+.4*audio.x);
    // Tiny stars use an integrated glow; resolve folds only when they cover pixels.
    if(Layers.Mandelstars && input.Lod>1.5)
    {
        float fractal=Slice(p,input.Seed)*(1-smoothstep(1.6,2.7,r));
        light*=1+fractal*(.3+6*audio.y)*Appearance.Fractal;
        light+=color*fractal*(.04*bed+.8*max(audio.y,.4*rise))*Appearance.Fractal;
    }
    else if(Layers.Mandelstars) light+=color*exp(-r*r*2)*(.04*bed+.2*max(audio.y,.4*rise));
    return float4(light,0);
}
Texture2D<float4> SceneImage;
float4 AfterglowPS(PSin input) : SV_Target
{
    float3 light=SceneImage.SampleLevel(Linear,input.UV,0).rgb;
    if(Layers.Starfield)
    {
        float2 p=input.UV*float2(AspectRatio,1)*150;
        float seed=Hash(dot(floor(p),float2(1,157)));
        light+=float3(.25,.35,.5)*exp(-180*dot(frac(p)-.5,frac(p)-.5))*step(.996,seed)*MusicBody(GlobalSTBR);
    }
    return float4(pow(saturate(1-exp(-max(0,light)*(1+3*Appearance.Exposure))),1/2.2),1);
}


