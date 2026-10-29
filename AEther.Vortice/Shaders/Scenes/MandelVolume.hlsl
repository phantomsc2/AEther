#include "../Common/Projection.hlsl"

// Audio-independent proof of concept: this scene deliberately stays visible in
// silence. Static fractal/camera, one moving light, no emission or image history.
struct FractalSettings
{
    float Scale;
    float MaximumIterations;
};
struct DensitySettings
{
    float Center;
    float Width;
    float Strength;
    float Ambient;
    float Albedo;
};
struct LightSettings
{
    float Intensity;
    float Speed;
    float Radius;
};
struct QualitySettings
{
    float Samples;
    float TransmissionSteps;
};
struct PhaseSettings
{
    float Anisotropy;
    float Alignment;
    float Roughness;
};
cbuffer Parameters
{
    FractalSettings Fractal = { .75, .2 };
    DensitySettings Density = { 1, .526, .25, .419, 1 };
    LightSettings Light = { .55, .552, .2 };
    QualitySettings Quality = { .483871, .3333333 };
    // Anisotropy .5 maps to g=0; alignment zero disables oriented scattering.
    PhaseSettings Phase = { .5, 0, .2631579 };
};

// Enclose the default scale-two Mandelbox, keeping the medium's domain fixed
// while artistic scale/iteration edits change the finite fractal field.
static const float VolumeRadius = 10.5;
static const int GuideBins = 64;

// Finite Mandelbox orbit. q = |z_N|/max(1,|scale|)^N is a normalized terminal
// radius, not a signed distance. The normalization stays finite at scale zero.
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
// Only the scattering phase requests this; shadow/pilot density stays scalar.
float3 FractalGradient(float3 p)
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
    if (radius < 1e-8) return 0;
    return pow(max(1, abs(scale)), -iterations)
        * float3(dot(z, dx), dot(z, dy), dot(z, dz)) / radius;
}

float AmbientExtinction(float3 p)
{
    return (1 - smoothstep(9.5, VolumeRadius, length(p))) * .2 * Density.Ambient;
}

float Extinction(float3 p)
{
    float envelope = 1 - smoothstep(9.5, VolumeRadius, length(p));
    if (envelope <= 0) return 0;
    if (Density.Strength <= 0) return envelope * .2 * Density.Ambient;
    float q = FractalField(p);
    // One smooth Gaussian field profile, without periodic shell bands.
    // Center: 0..0.5 field units; Width: .002..0.2 field units (one sigma).
    float center = .5 * Density.Center;
    float width = .002 + .198 * Density.Width * Density.Width;
    float u = (q - center) / width;
    return envelope * (.2 * Density.Ambient + 8 * Density.Strength * exp(-.5 * u * u));
}

// HG relative to isotropic scattering: actual phase = response / (4*pi).
// g=0 returns exactly one, preserving the existing isotropic integrand.
float HGResponse(float cosine, float g)
{
    float d = max(1e-6, 1 + g * g - 2 * g * clamp(cosine, -1, 1));
    return (1 - g * g) / (d * sqrt(d));
}

float PhaseScattering(float3 p, float totalExtinction, float3 lightDirection, float3 cameraDirection)
{
    float g = 1.6 * (Phase.Anisotropy - .5);
    float ordinary = HGResponse(dot(-lightDirection, cameraDirection), g);
    float scattering = Density.Albedo * totalExtinction;
    [branch] if (Phase.Alignment > 0 && Density.Strength > 0)
    {
        float ambient = AmbientExtinction(p);
        float fractal = max(0, totalExtinction - ambient);
        if (fractal > 1e-8)
        {
            float3 gradient = FractalGradient(p);
            float magnitude2 = dot(gradient, gradient);
            // Degenerate gradients contribute ordinary scattering. Orientation
            // follows q, not the Gaussian density, whose peak has zero slope.
            float confidence = smoothstep(1e-10, 1e-6, magnitude2);
            float3 normal = gradient * rsqrt(max(magnitude2, 1e-20));
            float roughness = .05 + .95 * Phase.Roughness;
            float3 reflected = reflect(-lightDirection, normal);
            float oriented = HGResponse(dot(reflected, cameraDirection), 1 - roughness);
            float fractalPhase = lerp(ordinary, oriented, Phase.Alignment * confidence);
            // Add the two material contributions, each with a normalized phase.
            return Density.Albedo * (ambient * ordinary + fractal * fractalPhase);
        }
    }
    return scattering * ordinary;
}

bool VolumeInterval(float3 origin, float3 direction, out float nearT, out float farT)
{
    float b = dot(origin, direction);
    float c = dot(origin, origin) - VolumeRadius * VolumeRadius;
    float discriminant = b * b - c;
    if (discriminant <= 0) { nearT = farT = 0; return false; }
    float root = sqrt(discriminant);
    nearT = max(0, -b - root);
    farT = -b + root;
    return farT > nearT + 1e-4;
}

float LightTransmission(float3 p, float3 direction, float distanceToLight)
{
    if (distanceToLight <= 1e-5) return 1;
    float nearT, farT;
    if (!VolumeInterval(p, direction, nearT, farT)) return 1;
    farT = min(farT, distanceToLight);
    int steps = 8 + (int)round(120 * Quality.TransmissionSteps);
    float stepLength = (farT - nearT) / steps;
    float opticalDepth = 0;
    [loop] for (int i = 0; i < steps; ++i)
        opticalDepth += Extinction(p + direction * (nearT + (i + .5) * stepLength)) * stepLength;
    return exp(-opticalDepth);
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

ProjectionCamera SceneCamera()
{
    float3 origin=float3(9,6,-13.5),forward=normalize(-origin);
    float3 right=normalize(cross(forward,float3(0,1,0))),up=cross(right,forward);
    return MakeProjectionCamera(origin,forward,right,up);
}
float4 PS(PSin input) : SV_Target
{
    if ((Density.Strength <= 0 && Density.Ambient <= 0) || Density.Albedo <= 0 || Light.Intensity <= 0)
        return float4(0, 0, 0, 1);

    ProjectionCamera camera=SceneCamera();
    float3 origin=camera.Origin,direction=ProjectionDirection(camera,input.UV);
    float nearT, farT;
    if (!VolumeInterval(origin, direction, nearT, farT) || !ProjectionClipInterval(camera,direction,nearT,farT)) return float4(0, 0, 0, 1);

    // Orbit within the fractal and its existing bounds. Speed zero freezes it.
    float angle = T * .5 * Light.Speed;
    float3 lightPosition = float3(2.5 * cos(angle), 1.5 * sin(.7 * angle + .3), 2.5 * sin(angle));
    // A small radius concentrates the physical in-scattering around the light;
    // use that same softened geometry in the equiangular proposal below.
    float lightRadius = .01 + .49 * Light.Radius * Light.Radius;
    float rayLength = farT - nearT;
    float binLength = rayLength / GuideBins;
    float sigma[GuideBins];
    float tau[GuideBins + 1];
    float cdf[GuideBins + 1];
    tau[0] = cdf[0] = 0;
    // One fixed proposal per ray, shared by ALL cross-PDF evaluations. The same
    // pilot table supplies a piecewise-constant camera optical-depth integral.
    [loop] for (int bin = 0; bin < GuideBins; ++bin)
    {
        sigma[bin] = Extinction(origin + direction * (nearT + (bin + .5) * binLength));
        tau[bin + 1] = tau[bin] + sigma[bin] * binLength;
        float weight = sigma[bin] * exp(-.5 * (tau[bin] + tau[bin + 1]));
        // Positive support even when the pilot misses a density feature.
        cdf[bin + 1] = cdf[bin] + weight + max(1e-6, .02 * (8 * Density.Strength + .2 * Density.Ambient));
    }
    float guideTotal = cdf[GuideBins];

    float3 toLight = lightPosition - origin;
    float projection = dot(toLight, direction);
    float3 perpendicularVector = toLight - projection * direction;
    // Match the softened inverse-square falloff in both the proposal and PDF;
    // this also remains well-defined for a ray straight through the light.
    float perpendicular = sqrt(dot(perpendicularVector, perpendicularVector) + lightRadius * lightRadius);
    float angleNear = atan2(nearT - projection, perpendicular);
    float angleFar = atan2(farT - projection, perpendicular);
    float angleSpan = angleFar - angleNear;
    bool useAngular = perpendicular > 1e-3 && angleSpan > 1e-5;
    float referenceDepth = tau[GuideBins];
    bool useExponential = referenceDepth > 1e-3;
    float referenceRate = referenceDepth / rayLength;
    float exponentialMass = 1 - exp(-referenceDepth);

    uint2 pixel = (uint2)input.Position.xy;
    uint seed = RandomHash(pixel.x ^ RandomHash(pixel.y + 0x9e3779b9));
    int samples = 1 + (int)round(31 * Quality.Samples);
    float radiance = 0;
    [loop] for (int sampleIndex = 0; sampleIndex < samples; ++sampleIndex)
    {
        [loop] for (int strategy = 0; strategy < 3; ++strategy)
        {
            float u = (sampleIndex + Random01(seed ^ RandomHash((uint)(sampleIndex * 3 + strategy + 1)))) / samples;
            // Adding the stratum index can round an open-interval value to 1.
            u = min(u, .99999994);
            float t;
            if (strategy == 0 && useAngular)
                t = projection + perpendicular * tan(lerp(angleNear, angleFar, u));
            else if (strategy == 1)
            {
                float target = u * guideTotal;
                int selected = GuideBins - 1;
                [loop] for (int j = 0; j < GuideBins; ++j)
                    if (target < cdf[j + 1]) { selected = j; break; }
                float fraction = (target - cdf[selected]) / (cdf[selected + 1] - cdf[selected]);
                t = nearT + (selected + fraction) * binLength;
            }
            else if (strategy == 2 && useExponential)
                t = nearT - log(1 - u * exponentialMass) / referenceRate;
            else
                t = nearT + u * rayLength;
            t = clamp(t, nearT, farT);

            int index = min(GuideBins - 1, (int)((t - nearT) / binLength));
            float offset = t - nearT - index * binLength;
            float cameraTransmission = exp(-tau[index] - sigma[index] * offset);
            float pdfGuide = (cdf[index + 1] - cdf[index]) / (guideTotal * binLength);
            float pdfAngular = 1 / rayLength;
            if (useAngular)
                pdfAngular = perpendicular / (angleSpan * (perpendicular * perpendicular + (t - projection) * (t - projection)));
            float pdfExponential = 1 / rayLength;
            if (useExponential)
                pdfExponential = referenceRate * exp(-referenceRate * (t - nearT)) / exponentialMass;

            float3 p = origin + t * direction;
            float extinction = Extinction(p);
            float scattering = Density.Albedo * extinction;
            if (scattering <= 1e-8) continue;
            float3 lightVector = lightPosition - p;
            float distanceSquared = dot(lightVector, lightVector);
            float distanceToLight = sqrt(distanceSquared);
            float3 lightDirection = lightVector / max(distanceToLight, 1e-6);
            float lightTransmission = LightTransmission(p, lightDirection, distanceToLight);
            [branch] if (Phase.Alignment > 0 || Phase.Anisotropy != .5)
                scattering = PhaseScattering(p, extinction, lightDirection, -direction);
            // Phase responses are relative to 1/(4*pi). Equal-count balance MIS:
            // sum F(t)/(pAngular+pGuide+pExponential), divided by samples per
            // strategy, NOT by the combined count of all three strategies.
            float integrand = cameraTransmission * scattering * lightTransmission
                * (3000 * Light.Intensity * Light.Intensity) / (12.5663706144 * (distanceSquared + lightRadius * lightRadius));
            radiance += integrand / (pdfAngular + pdfGuide + pdfExponential);
        }
    }
    return LinearOutput((radiance / samples).xxx);
}
