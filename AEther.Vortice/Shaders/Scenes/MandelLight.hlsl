#include "../Common/Inputs.hlsl"

// Audio-independent surface/airlight prototype; no ambient, history or medium emission.
struct FractalSettings { float Scale; float Potential; float MaximumIterations; };
struct AirSettings { float Density; float ScaleHeight; float3 Scattering; };
struct SurfaceSettings { float Roughness; float Reflectance; };
struct LightSettings { float Intensity; float Speed; float Radius; float Phase; float3 Color; bool Enabled; };
struct QualitySettings { float Samples; float SearchSteps; };
struct ViewSettings { bool ShowShadowMap; bool ShowAtmosphereLookup; float CameraSpeed; float ShadowLight; };
cbuffer Parameters
{
    FractalSettings Fractal = { .75, 1, .2 };
    AirSettings Air = { .3, .4, float3(1,1,1) };
    SurfaceSettings Surface = { .3, .85 };
    LightSettings Light = { .55, .35, .2, 0, float3(1,1,1), true };
    LightSettings Light2 = { .35, .35, .2, .25, float3(.2,.6,1), false };
    LightSettings Light3 = { .35, .35, .2, .5, float3(1,.3,.1), false };
    LightSettings Light4 = { .35, .35, .2, .75, float3(.2,1,.4), false };
    QualitySettings Quality = { .23404255, .42857143 };
    ViewSettings View = { false, false, .2, 0 };
};
// Host selects one light per transport/integration draw. Density and G-buffer
// are shared; shadow maps and MIS samples belong to that selected light.
cbuffer LightConstants { uint LightIndex; float3 LightPadding; };
LightSettings CurrentLight()
{
    if(LightIndex==1) return Light2;
    if(LightIndex==2) return Light3;
    if(LightIndex==3) return Light4;
    return Light;
}
Texture2D<float4> LightRadiance;
Texture2D<float4> EpipolarShadow;
Texture2D<float4> SurfaceCache;
Texture2D<float> AtmosphereOpticalDepth;
SamplerState AtmosphereSampler;
static const float Pi = 3.14159265359;
static const float SolidRadius = 10.5;
static const float AirRadius = 16;
// Constant angular speed about world Y; radius/elevation stay fixed.
float3 CameraPosition()
{
    float sine,cosine;
    sincos(T*.25*View.CameraSpeed,sine,cosine);
    return float3(9*cosine+13.5*sine,6,9*sine-13.5*cosine);
}

// Potential spans twelve octaves; slider 1 retains the original level 0.5.
float Potential() { return .5 * exp2(12 * (Fractal.Potential - 1)); }

float FractalField(float3 p)
{
    float scale = 8 * Fractal.Scale - 4;
    int iterations = 1 + (int)round(15 * Fractal.MaximumIterations);
    float3 z = p;
    [loop] for (int i = 0; i < iterations; ++i)
    {
        z = 2 * clamp(z, -1, 1) - z;
        float fold = 1 / clamp(dot(z, z), .25, 1);
        z *= fold;
        z = scale * z + p;
    }
    return length(z) * pow(max(1, abs(scale)), -iterations);
}

// Columns of dz/dp, propagated analytically through the same finite orbit.
// Used only at surface hits; the atmosphere has no fractal dependence.
float3 FractalGradient(float3 p, out float value)
{
    float scale = 8 * Fractal.Scale - 4;
    int iterations = 1 + (int)round(15 * Fractal.MaximumIterations);
    float3 z = p;
    float3 dx = float3(1, 0, 0), dy = float3(0, 1, 0), dz = float3(0, 0, 1);
    [loop] for (int i = 0; i < iterations; ++i)
    {
        float3 signs = 1 - 2 * step(1, abs(z));
        dx *= signs; dy *= signs; dz *= signs;
        z = 2 * clamp(z, -1, 1) - z;
        float r2 = dot(z, z);
        float fold = 1 / clamp(r2, .25, 1);
        if (r2 > .25 && r2 < 1)
        {
            // Sphere inversion: fold * (I - 2*z*z^T/r2).
            dx = fold * (dx - 2 * z * dot(z, dx) / r2);
            dy = fold * (dy - 2 * z * dot(z, dy) / r2);
            dz = fold * (dz - 2 * z * dot(z, dz) / r2);
        }
        else { dx *= fold; dy *= fold; dz *= fold; }
        z *= fold;
        z = scale * z + p;
        dx = scale * dx + float3(1, 0, 0);
        dy = scale * dy + float3(0, 1, 0);
        dz = scale * dz + float3(0, 0, 1);
    }
    float radius = length(z);
    float normalization = pow(max(1, abs(scale)), -iterations);
    value = radius * normalization;
    if (radius < 1e-8) return 0;
    return normalization * float3(dot(z, dx), dot(z, dy), dot(z, dz)) / radius;
}

bool Interval(float3 o, float3 d, float radius, out float a, out float b)
{
    float v = dot(o, d), c = dot(o, o) - radius * radius;
    float disc = v*v - c;
    if (disc <= 0) { a = b = 0; return false; }
    float root = sqrt(disc);
    a = max(0, -v - root); b = -v + root;
    return b > a;
}

// Uniform search is deliberate: q is not a safe distance estimator. Crossing
// refinement does not guarantee detecting features narrower than one search step.
bool SurfaceHit(float3 o, float3 d, float limit, out float hit)
{
    hit = limit;
    float a, b;
    if (!Interval(o, d, SolidRadius, a, b)) return false;
    b = min(b, limit);
    if (b <= a) return false;
    // Bounds already clip the level set to the sphere, avoiding a second
    // distance calculation at every search step.
    float threshold = Potential();
    float previous = a;
    if (FractalField(o+a*d) <= threshold) { hit = a; return true; }
    int steps = 128 + (int)round(896 * Quality.SearchSteps);
    [loop] for (int j = 1; j <= steps; ++j)
    {
        float t = lerp(a, b, (float)j / steps);
        float next = FractalField(o+t*d);
        if (next <= threshold)
        {
            float lo = previous, hi = t;
            [unroll] for (int k = 0; k < 10; ++k)
            {
                float mid = .5*(lo + hi);
                if (FractalField(o+mid*d) > threshold) lo = mid; else hi = mid;
            }
            hit = .5*(lo + hi); return true;
        }
        previous = t;
    }
    return false;
}

float3 CameraRay(float2 uv, float3 camera)
{
    float3 forward=normalize(-camera);
    float3 right=normalize(cross(forward,float3(0,1,0)));
    float3 up=cross(right,forward);
    float aspect=AspectRatio*(View.ShowShadowMap ? .7 : 1);
    float2 screen=(2*uv-1)*float2(max(1,aspect),-max(1,1/aspect));
    return normalize(2*forward+screen.x*right+screen.y*up);
}
float3 SurfaceNormal(float3 p)
{
    float value;
    float3 gradient=FractalGradient(p,value);
    if(length(p)-SolidRadius>=value-Potential()-.0001) gradient=p;
    return gradient/max(1e-10,length(gradient));
}
// 1. VIEW PRECALCULATION: camera first-hit depth and analytic normal, no lighting.
float4 SurfacePS(PSin input) : SV_Target
{
    if(View.ShowShadowMap)
    {
        if(input.UV.x>=.7) return float4(0,0,0,-1);
        input.UV.x/=.7;
    }
    float3 camera=CameraPosition();
    float3 direction=CameraRay(input.UV,camera);
    float hit;
    if(!SurfaceHit(camera,direction,64,hit)) return float4(0,0,0,-1);
    return float4(SurfaceNormal(camera+hit*direction),hit);
}
float3 LightPosition()
{
    LightSettings light=CurrentLight();
    float angle = T * .5 * light.Speed - .8 + 6.28318530718*light.Phase;
    // Outside the closed solid for all geometry edits; no light trapped in metal.
    return float3(12*cos(angle), 3*sin(.7*angle + .3), 12*sin(angle));
}
void LightBasis(float3 light, out float3 axis, out float3 u, out float3 v)
{
    axis = normalize(CameraPosition() - light);
    float3 helper = abs(axis.y) < .95 ? float3(0,1,0) : float3(1,0,0);
    u = normalize(cross(helper, axis)); v = cross(axis, u);
}

struct LightContext
{
    float3 Position;
    float3 Axis;
    float3 U;
    float3 V;
};
LightContext MakeLightContext()
{
    LightContext source;
    source.Position=LightPosition();
    LightBasis(source.Position,source.Axis,source.U,source.V);
    return source;
}
// 1. STATIC PRECALCULATION: cached until atmosphere shape or shader changes.
// HQ unit-density optical depth for outward rays. x is radius/16, y is sqrt(mu).
// Exact endpoint texels and extra grazing-angle resolution reduce interpolation
// error. Only scale height changes this cache; density is applied at lookup time.
float AtmospherePS(PSin input) : SV_Target
{
    float r = AirRadius * (input.Position.x - .5) / 511;
    float u = (input.Position.y - .5) / 255;
    float mu = u*u;
    float along = r*mu;
    float distance = max(0,sqrt(max(0,along*along + AirRadius*AirRadius-r*r))-along);
    float stepLength = distance/1024, tau=0;
    float h=.5+2*Air.ScaleHeight;
    [loop] for(int i=0;i<1024;++i)
    {
        float t=(i+.5)*stepLength;
        float radius=sqrt(max(0,r*r+t*t+2*along*t));
        tau += .08*exp((6-radius)/h)*stepLength;
    }
    return tau;
}
float OutwardOpticalDepth(float r, float mu)
{
    uint width,height; AtmosphereOpticalDepth.GetDimensions(width,height);
    float2 uv=(float2(saturate(r/AirRadius),sqrt(saturate(mu)))
        *float2(width-1,height-1)+.5)/float2(width,height);
    return AtmosphereOpticalDepth.SampleLevel(AtmosphereSampler,uv,0);
}
float OpticalDepthToExit(float3 p, float3 direction)
{
    float r=length(p);
    float mu=r>1e-6 ? clamp(dot(p,direction)/r,-1,1) : 0;
    if(mu>=0) return OutwardOpticalDepth(r,mu);
    float impact=r*sqrt(max(0,1-mu*mu));
    // Inward ray crosses the chord midpoint: full chord minus the near tail.
    return max(0,2*OutwardOpticalDepth(impact,0)-OutwardOpticalDepth(r,-mu));
}
float3 CameraTransmission(float3 p, float3 cameraDirection)
{
    // Camera is outside the sphere: the reverse exit path is exactly the camera
    // segment inside the atmosphere. No subtraction of nearly equal thick paths.
    return Air.Density>0 ? exp(-Air.Density*Air.Scattering*OpticalDepthToExit(p,-cameraDirection)) : 1;
}

// 2. LIGHT-SOURCE TRANSPORT: angular visibility and optical-depth cache.
// x = gamma/pi, y = alpha/(2*pi). Cache first-hit radial distance plus
// unit-density optical depth from light to atmosphere exit in both directions.
float4 ShadowPS(PSin input) : SV_Target
{
    float3 light = LightPosition(), axis, u, v;
    LightBasis(light, axis, u, v);
    float gamma = Pi * input.UV.x, alpha = 2*Pi * input.UV.y;
    float3 d = cos(gamma)*axis + sin(gamma)*(cos(alpha)*u + sin(alpha)*v);
    float hit;
    SurfaceHit(light, d, 64, hit);
    // The two exit directions share the same outward tail and full chord.
    float r=length(light), mu=clamp(dot(light,d)/r,-1,1);
    float tail=OutwardOpticalDepth(r,abs(mu));
    float opposite=max(0,2*OutwardOpticalDepth(r*sqrt(max(0,1-mu*mu)),0)-tail);
    return float4(hit,mu>=0 ? tail : opposite,mu>=0 ? opposite : tail,0);
}
float3 LightTransmission(float3 p, LightContext source, float bias)
{
    float3 light=source.Position;
    float3 delta = p - light;
    float r = length(delta);
    if (r <= bias) return 1;
    float3 w=delta/r, axis=source.Axis, u=source.U, v=source.V;
    float x = dot(w,u), y = dot(w,v);
    float perpendicular = sqrt(x*x + y*y);
    // Choose the direction with the thinner exit tails to avoid subtracting
    // almost equal, optically thick paths for short segments far from center.
    bool forward = dot(light+p,w)>=0;
    if (perpendicular < .02)
    {
        float hit;
        if(SurfaceHit(light, w, max(0, r - bias), hit)) return 0;
        if (Air.Density <= 0) return 1;
        float sampleTail = OpticalDepthToExit(p,forward ? w : -w);
        float lightTail = OpticalDepthToExit(light,forward ? w : -w);
        float tau=max(0,forward ? lightTail-sampleTail : sampleTail-lightTail);
        return exp(-Air.Density*Air.Scattering*tau);
    }
    float gamma = atan2(perpendicular, dot(w,axis));
    float alpha = atan2(y,x);
    float2 uv = float2(gamma/Pi, frac(alpha/(2*Pi)));
    uint width, height; EpipolarShadow.GetDimensions(width,height);
    float2 texel = uv * float2(width,height) - .5;
    int2 base = (int2)floor(texel);
    float2 f = frac(texel);
    float visible = 0, lightTail = 0;
    [unroll] for (int j=0; j<2; ++j)
        [unroll] for (int i=0; i<2; ++i)
        {
            int2 at = int2(clamp(base.x+i,0,(int)width-1), (base.y+j+(int)height)%(int)height);
            float3 cached = EpipolarShadow.Load(int3(at,0)).xyz;
            float weight = (i ? f.x : 1-f.x)*(j ? f.y : 1-f.y);
            // Compare distances first, filter visibility rather than geometry.
            visible += weight * (r <= cached.x + bias ? 1 : 0);
            // Optical depth is smooth and independent of the solid boundary.
            lightTail += weight * (forward ? cached.y : cached.z);
        }
    if (visible <= 0 || Air.Density <= 0) return visible;
    float sampleTail = OpticalDepthToExit(p,forward ? w : -w);
    float tau=max(0,forward ? lightTail-sampleTail : sampleTail-lightTail);
    return visible * exp(-Air.Density*Air.Scattering*tau);
}

float Extinction(float3 p)
{
    float r = length(p);
    if (r >= AirRadius) return 0;
    float h = .5 + 2*Air.ScaleHeight;
    return .08 * Air.Density * exp((6 - r)/h);
}
float Phase(float cosine) { return 3*(1+cosine*cosine)/(16*Pi); }
float3 Falloff(float r2)
{
    LightSettings light=CurrentLight();
    float radius = .01 + .49*light.Radius*light.Radius;
    return light.Color * (3000*light.Intensity*light.Intensity / (r2+radius*radius));
}
float SmithG1(float cosine, float a2)
{
    return 2*cosine / max(1e-6, cosine + sqrt(a2+(1-a2)*cosine*cosine));
}
float3 SurfaceLighting(float3 p, float3 eye, LightContext source, float3 n)
{
    float3 light=source.Position;
    float3 delta=light-p;
    float r=length(delta);
    float3 l=delta/max(r,1e-6), v=normalize(eye-p);
    float nl=saturate(dot(n,l)), nv=saturate(dot(n,v));
    if (nl <= 0 || nv <= 0) return 0;
    float3 halfVector=l+v;
    float3 h=halfVector/max(length(halfVector),1e-6);
    float nh=saturate(dot(n,h)), vh=saturate(dot(v,h));
    float roughness=max(.04,Surface.Roughness);
    float a=roughness*roughness, a2=a*a;
    float denom=nh*nh*(a2-1)+1;
    float D=a2/(Pi*denom*denom);
    float G=SmithG1(nl,a2)*SmithG1(nv,a2);
    float F=Surface.Reflectance+(1-Surface.Reflectance)*pow(1-vh,5);
    float brdf=D*G*F/max(1e-6,4*nl*nv);
    float3 transmission=LightTransmission(p,source,.025);
    return transmission*Falloff(r*r)*brdf*nl;
}
uint RandomHash(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352d;
    value ^= value >> 15;
    value *= 0x846ca68b;
    return value ^ (value >> 16);
}
float Random01(uint seed)
{
    // Open interval: inverse CDFs never receive either endpoint. No time jitter.
    return ((RandomHash(seed) >> 9) + .5) / 8388608.0;
}

// 3. CAMERA SAMPLING: cached surface, two-proposal MIS airlight, surface BRDF.
float3 RenderLight(PSin input)
{
    LightSettings light=CurrentLight();
    if (!light.Enabled || light.Intensity <= 0 || !any(light.Color>0)) return 0;
    float3 origin = CameraPosition();
    float3 direction=CameraRay(input.UV,origin);
    float nearT, farT;
    if (!Interval(origin, direction, AirRadius, nearT, farT)) return 0;

    float4 surface=SurfaceCache.Load(int3((int2)input.Position.xy,0));
    bool hasSurface=surface.w>=0;
    if (hasSurface) farT=min(farT,surface.w);
    LightContext lightSource=MakeLightContext();
    float3 lightPosition = lightSource.Position;
    float rayLength = farT - nearT;
    float3 toLight = lightPosition - origin;
    float projection = dot(toLight, direction);
    float3 perpendicularVector = toLight - projection * direction;
    // Geometric equiangular angle: gamma = theta + pi/2 - beta exactly.
    // The finite light-radius falloff remains in the integrand. Near the axis,
    // use uniform distance sampling; every cross-PDF uses the same fallback.
    float perpendicular = length(perpendicularVector);
    float angleNear = atan2(nearT - projection, perpendicular);
    float angleFar = atan2(farT - projection, perpendicular);
    float angleSpan = angleFar - angleNear;
    bool useAngular = perpendicular > 1e-3 && angleSpan > 1e-5;
    float referenceDepth = Air.Density*dot(Air.Scattering,float3(1,1,1)/3)
        *OpticalDepthToExit(origin+farT*direction,-direction);
    // Fit density * camera transmission at the two endpoints. Density's log
    // change is exact for this radial profile; its interpolation along the ray
    // is only a proposal approximation, corrected by the MIS PDF.
    float logDensityChange=(length(origin+nearT*direction)-length(origin+farT*direction))
        /(.5+2*Air.ScaleHeight);
    float slope=clamp(logDensityChange-referenceDepth,-20,20);
    bool increasing=slope>0;
    float depth=abs(slope);
    bool useExponential=depth>1e-3;
    float referenceRate=depth/rayLength;
    float exponentialMass=1-exp(-depth);

    uint2 pixel = (uint2)input.Position.xy;
    uint seed = RandomHash(pixel.x ^ RandomHash(pixel.y + 0x9e3779b9)
        ^ (LightIndex==0 ? 0 : RandomHash(LightIndex)));
    int samples = 1 + (int)round(47 * Quality.Samples);
    float3 radiance = 0;
    [loop] for (int sampleIndex = 0; sampleIndex < samples && Air.Density > 0 && any(Air.Scattering*light.Color>0); ++sampleIndex)
    {
        [loop] for (int strategy = 0; strategy < 2; ++strategy)
        {
            float u = (sampleIndex + Random01(seed ^ RandomHash((uint)(sampleIndex * 2 + strategy + 1)))) / samples;
            // Adding the stratum index can round an open-interval value to 1.
            u = min(u, .99999994);
            float t;
            if (strategy == 0 && useAngular)
                t = projection + perpendicular * tan(lerp(angleNear, angleFar, u));
            else if (strategy == 1 && useExponential)
            {
                float distance=-log(1-u*exponentialMass)/referenceRate;
                t=increasing ? farT-distance : nearT+distance;
            }
            else
                t = nearT + u * rayLength;
            t = clamp(t, nearT, farT);

            float pdfAngular = 1 / rayLength;
            if (useAngular)
                pdfAngular = perpendicular / (angleSpan * (perpendicular * perpendicular + (t - projection) * (t - projection)));
            float pdfExponential = 1 / rayLength;
            if (useExponential)
                pdfExponential = referenceRate * exp(-referenceRate * (increasing ? farT-t : t-nearT)) / exponentialMass;

            float3 p = origin + t * direction;
            float extinction = Extinction(p);
            float3 scattering = extinction*Air.Scattering;
            if (!any(scattering > 1e-8)) continue;
            float3 lightVector = lightPosition - p;
            float distanceSquared = dot(lightVector, lightVector);
            float distanceToLight = sqrt(distanceSquared);
            float3 lightDirection = lightVector / max(distanceToLight, 1e-6);
            float3 lightTransmission = LightTransmission(p, lightSource, .002);
            if (!any(lightTransmission > 0)) continue;
            float3 cameraTransmission = CameraTransmission(p,direction);
            float phase = Phase(dot(-lightDirection,-direction));
            // Equal-count balance MIS, PDFs measured per world-space distance.
            float3 integrand = cameraTransmission * scattering * lightTransmission
                * phase * Falloff(distanceSquared);
            radiance += integrand / (pdfAngular + pdfExponential);
        }
    }
    radiance /= samples;
    if (hasSurface)
        radiance += CameraTransmission(origin+farT*direction,direction) * SurfaceLighting(origin+farT*direction,origin,lightSource,surface.xyz);
    return max(0,radiance);
}
// One independent draw per light into a shared linear FP32 accumulation image.
float4 LightPS(PSin input) : SV_Target
{
    if(View.ShowShadowMap)
    {
        if(input.UV.x>=.7) return 0;
        input.UV.x/=.7;
    }
    return float4(RenderLight(input),0);
}
float4 DisplayRadiance(PSin input)
{
    float3 radiance=LightRadiance.Load(int3((int2)input.Position.xy,0)).rgb;
    float3 mapped=1-exp(-max(0,radiance));
    float3 encoded=lerp(12.92*mapped,1.055*pow(mapped,1/2.4)-.055,step(.0031308,mapped));
    return float4(encoded,1);
}
float4 PS(PSin input) : SV_Target
{
    if (View.ShowAtmosphereLookup)
    {
        // Full-window, log-scaled unit-density optical depth. Radius increases
        // left to right; sqrt(outward zenith cosine) increases top to bottom.
        float2 uv=.5+(input.UV-.5)*float2(max(1,AspectRatio/2),max(1,2/AspectRatio));
        if(any(uv<0)||any(uv>=1)) return float4(.025,.025,.025,1);
        uint w,h; AtmosphereOpticalDepth.GetDimensions(w,h);
        float depth=AtmosphereOpticalDepth.Load(int3((int2)(uv*float2(w,h)),0));
        float maximum=AtmosphereOpticalDepth.Load(int3(0,0,0));
        float value=saturate(log2(1+depth)/max(1e-6,log2(1+maximum)));
        return float4(value.xxx,1);
    }
    if (!View.ShowShadowMap || input.UV.x < .7) return DisplayRadiance(input);
    // Square map in the right-hand panel: gamma horizontal, slice alpha vertical.
    // Near occluders are bright; no hit (distance 64) is black. Raw distances
    // use nearest texels here so the preview exposes the actual cache resolution.
    float2 uv = float2((input.UV.x - .7)/.3, input.UV.y);
    float aspect = .3*AspectRatio;
    uv = .5 + (uv-.5)*float2(max(1,aspect),max(1,1/aspect));
    if (any(uv < 0) || any(uv >= 1)) return float4(.025,.025,.025,1);
    uint w,h; EpipolarShadow.GetDimensions(w,h);
    float depth = EpipolarShadow.Load(int3((int2)(uv*float2(w,h)),0)).x;
    float value = depth < 64 ? saturate(1-depth/32) : 0;
    return float4(value.xxx,1);
}
