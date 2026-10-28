#define PROJECTIVE_FLIGHT
#define FLIGHT_SMOOTH_HISTORY
#include "FractalTypes.hlsl"
#include "Flight.hlsl"

struct FluidParameters
{
    float Viscosity;
    bool PeriodicBoundary;
    bool LowQuality;
};
struct FlightParameters
{
    float Flow;
    float HalfLife;
    float2 Steering;
    float Turn;
    float Roll;
    float SourceDistance;
    float Warp;
    float AudioGain;
    float Glow;
    float WingAngle;
    float WingSpan;
    float SliceStrength;
    float FlashDistance;
    float CloudCover;
};
struct ReactionParameters
{
    float Strength;
    float Attacks;
    float TonalCuts;
    float CameraMotion;
    float Afterglow;
};
#ifdef FLIGHT_PLANE_PROJECTION
struct CameraParameters
{
    float Height;
    float Pitch;
    float FieldOfView;
    float FractalScale;
};
#endif
cbuffer Parameters
{
    FluidParameters Fluid = { 0, false, true };
    FlightParameters Flight = { .45, .24, float2(.5, .5), .5, .5, .45, .7, .75, .3, .5, .65, .75, .35, .45 };
    ReactionParameters Reaction = { .7, .75, .75, .35, .4 };
#ifdef FLIGHT_PLANE_PROJECTION
    CameraParameters Camera = { .35, .72, .5, .45 };
#endif
};

#include "FluidSimulation.hlsl"
struct FluidMaterial { float4 Color : SV_Target0; };
Texture2D<float4> FluidMaterial0;
Texture2D<float4> FluidPredicted0;
SamplerState FluidSampler;

// Coordinates share the fluid grid's viewport-height units (positive Y down).
float2 FlightVanishingPoint()
{
    return (Flight.Steering * 2 - 1) * float2(.7 * AspectRatio, .55);
}

#ifdef FLIGHT_PLANE_PROJECTION
// A pinhole camera above the world plane Y=0, looking along positive Z.
void FlightCameraBasis(out float3 right, out float3 up, out float3 forward)
{
    float yaw = (Flight.Steering.x - .5) * 1.1 + (Flight.Turn - .5) * .8;
    float pitch = (Camera.Pitch - .5) * 1.1 + (Flight.Steering.y - .5) * .6;
    forward = float3(sin(yaw) * cos(pitch), -sin(pitch), cos(yaw) * cos(pitch));
    right = float3(cos(yaw), 0, -sin(yaw));
    up = cross(forward, right);
    float roll = (Flight.Roll - .5) * 1.8;
    float3 originalRight = right;
    right = cos(roll) * right + sin(roll) * up;
    up = -sin(roll) * originalRight + cos(roll) * up;
}

float FlightFocalLength()
{
    // Vertical field of view spans 40..100 degrees.
    return 1 / tan((40 + 60 * Camera.FieldOfView) * .00872664626);
}

float FlightForwardSpeed() { return 8 + 20 * Flight.Flow; }

float3 FlightCameraPosition()
{
    return float3(0, .6 + 2.4 * Camera.Height,
        T * FlightForwardSpeed() + S * 2 * Reaction.CameraMotion);
}

float4 FlightPlaneHit(float2 p)
{
    float3 right, up, forward;
    FlightCameraBasis(right, up, forward);
    float3 ray = forward + (p.x * right - p.y * up) / FlightFocalLength();
    float3 camera = FlightCameraPosition();
    if (ray.y >= -.0001) return float4(0, 0, 0, -1);
    float distance = -camera.y / ray.y;
    return float4(camera + distance * ray, distance);
}
#endif

// Frequency, continuous history age, fractal relief and birth time. No event
// planes, particles or spatial bins are used to place the foreground geometry.
float4 FlightMap(float2 p)
{
    float2 q = p - FlightVanishingPoint();
#ifdef FLIGHT_PLANE_PROJECTION
    float4 hit = FlightPlaneHit(p);
    if (hit.w <= 0) return float4(0, -2, 0, 0);
    float speed = FlightForwardSpeed();
    float farPlane = speed * (.55 + .4 * Flight.FlashDistance);
    float depth = hit.w;
    float age = (farPlane - depth) / speed;
    float birth = T - age;
    // Continuous bounded chart on the infinite plane, anchored in world space.
    float2 chart = hit.xz * (.12 + .5 * Camera.FractalScale);
    float3 z = float3(3 * sin(chart.x), 3 * sin(chart.y), .7 * cos(chart.x * .6 + chart.y * .4));
#else
    float speed = 12 + 24 * Flight.Flow;
    float farPlane = speed * (.3 + .6 * Flight.FlashDistance);
    float depth = 1.5 * (.8 + .6 * Flight.SourceDistance) / max(.025, length(q));
    float age = (farPlane - depth) / speed;
    float birth = T - age;
    float3 z = float3(q * depth / 1.5, .14 * depth + .35 * sin(birth * .3));
#endif
    float3 offset = z * .3;
    float derivative = 1;
    [unroll] for (int iteration = 0; iteration < 7; ++iteration)
    {
        z = 2 * clamp(z, -1, 1) - z;
        float fold = 1 / clamp(dot(z, z), .2, 1);
        z *= fold;
        z.xy = FlightRotate(z.xy, .3 + .35 * Flight.Warp * sin(birth * .17));
        z = -1.65 * z + offset;
        derivative = 1.65 * fold * derivative + .3;
    }
    float coordinate = log2(max(1e-7, length(z) / derivative));
    float phase = 3.2 * coordinate;
    // Average unresolved folds instead of letting distant detail sparkle.
    float relief = lerp(exp(-9 * abs(sin(phase))), .1, saturate(fwidth(phase) / .8));
#ifdef FLIGHT_PLANE_PROJECTION
    float frequency = .5 + .5 * sin(hit.x * .3);
#else
    float frequency = atan2(abs(q.x), q.y) / 3.14159265;
#endif
    frequency += .13 * Flight.Warp * sin(coordinate * 2) * sin(3.14159265 * frequency);
    age += .075 * Flight.Warp * sin(coordinate * 1.7);
    return float4(saturate(frequency), age, relief, birth);
}

float4 FlightFluidSource(float2 p, float substepOffset)
{
    float4 mapping = FlightMap(p);
    return FlightAudio(mapping.x, mapping.y + substepOffset / max(.001, HistorySpan),
        step(0, p.x - FlightVanishingPoint().x));
}

float FlightSliceContour(float2 p, float4 source, float contour)
{
    float2 wing = FlightRotate(p - FlightVanishingPoint(), (Flight.WingAngle - .5) * 3.14159265);
    float span = .3 + AspectRatio * .7 * Flight.WingSpan;
    float extent = smoothstep(.08, .2, abs(wing.x)) * (1 - smoothstep(span, span + .15, abs(wing.x)));
    // The wing exposes the round contours already present in the folded field.
    // Cut those contours into the material instead of opening one solid stripe.
    float blade = exp(-pow(wing.y / .14, 2)) * extent * (.08 + .92 * contour);
    float tone = 1 - exp(-(4 + 60 * Flight.AudioGain) * source.r);
    return (30 + 90 * Reaction.TonalCuts) * Flight.SliceStrength * blade * tone;
}

float FlightCloudNoise(float2 p)
{
    float2 cell = floor(p), f = frac(p);
    f = f * f * (3 - 2 * f);
    float a = frac(sin(dot(cell, float2(127.1, 311.7))) * 43758.5453);
    float b = frac(sin(dot(cell + float2(1, 0), float2(127.1, 311.7))) * 43758.5453);
    float c = frac(sin(dot(cell + float2(0, 1), float2(127.1, 311.7))) * 43758.5453);
    float d = frac(sin(dot(cell + 1, float2(127.1, 311.7))) * 43758.5453);
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float FlightLightning(float4 source, float4 mapping)
{
    float gain = 4 + 60 * Flight.AudioGain;
    float attack = 1 - exp(-gain * (1 + 3 * Reaction.Attacks) * source.g);
    // Conduct the pulse along the existing round contours. This only scales
    // material light: it cannot create cloud density or illuminate empty space.
    float conductor = pow(saturate(mapping.z), .7);
    return 1 + (4 + 20 * Reaction.Attacks) * attack * conductor;
}

float4 FlightLightSource(float2 p, float4 source, float4 mapping)
{
    float2 q = p - FlightVanishingPoint();
    float gain = 4 + 60 * Flight.AudioGain;
    float noise = 1 - exp(-gain * source.b);
    float tone = 1 - exp(-gain * source.r);
#ifdef FLIGHT_PLANE_PROJECTION
    float4 hit = FlightPlaneHit(p);
    if (hit.w <= 0) return 0;
    float2 cloudPosition = hit.xz * 1.8;
#else
    float2 cloudPosition = q * 12 + float2(.4, -.7) * mapping.w;
#endif
    float cloud = .57 * FlightCloudNoise(cloudPosition)
        + .28 * FlightCloudNoise(cloudPosition * 2.03)
        + .15 * FlightCloudNoise(cloudPosition * 4.11);
    // One continuous material: cloud structure modulates the folded surface,
    // tones reveal/cut its circular contours, attacks excite that same body.
    // A broad texture transition avoids a separate layer of hard cloud blocks.
    cloud = smoothstep(.2 - .12 * Flight.CloudCover, .85 - .2 * Flight.CloudCover, cloud);
    float occupancy = 1 - exp(-gain * (source.b + .04 * source.r));
    float relief = mapping.z;
    float density = occupancy * (.15 + .85 * cloud) * (.18 + .82 * relief);
    density *= exp(-.045 * FlightSliceContour(p, source, mapping.z));
    float excitation = .25 + .75 * noise + tone * (.15 + 1.5 * relief);
    float3 palette = lerp(float3(.45, .62, .7), FlightPalette(mapping.x, mapping.w), .65);
    return float4(.5 * palette * density * excitation, density);
}

float4 FlightPigment(float2 p, float4 source, float substepOffset)
{
    // Capture the same continuously mapped foreground in the lower-resolution
    // fluid. The fluid carries its diffuse, persistent afterglow.
    float4 mapping = FlightMap(p);
    mapping.y += substepOffset / max(.001, HistorySpan);
    float4 material = FlightLightSource(p, source, mapping);
    // Retain the illuminated color as afterglow, leaving density unchanged.
    material.rgb *= FlightLightning(source, mapping);
    return .65 * material;
}

// Flight retains its procedural foreground and fading afterglow. The previous
// optical-flow carrier, distributed forcing and dye diffusion are intentionally
// removed; its material uses the same flow solver as every other fluid scene.
float2 FluidApplyBoundary(int2 cell, float2 projectedVelocity)
{
    return projectedVelocity;
}
FluidMaterial PredictMaterial(PSin input)
{
    FluidMaterial output;
    output.Color = FluidMaterial0.SampleLevel(ClampFluid, Backtrace(input.UV), 0);
    return output;
}
FluidMaterial UpdateMaterial(PSin input)
{
    float2 p = World(input.UV);
    float4 dye = FluidPredicted0.Load(int3(int2(input.Position.xy), 0));
    // This scene chooses to fade all four channels. Ring material instead
    // retains density and weighted coordinates while fading pigment alone.
    dye *= exp(-(.69314718 / (.1 + Flight.HalfLife * 9.9)) * (StepSeconds + CatchupSeconds));
    float4 source = FlightFluidSource(p, SourceAge);
    float4 dose = StepSeconds * 6 * FlightPigment(p, source, SourceAge);
    FluidMaterial output;
    output.Color = min(dye + (12 - dye) * (1 - exp(-dose / 12)), 12);
    return output;
}
float4 PS(PSin input) : SV_Target
{
    float4 dye = FluidMaterial0.Sample(FluidSampler, input.UV);
    uint width, height; FluidMaterial0.GetDimensions(width, height);
    float2 tap = 1.5 / float2(width, height);
    float4 left = FluidMaterial0.Sample(FluidSampler, input.UV - float2(tap.x, 0));
    float4 right = FluidMaterial0.Sample(FluidSampler, input.UV + float2(tap.x, 0));
    float4 up = FluidMaterial0.Sample(FluidSampler, input.UV - float2(0, tap.y));
    float4 down = FluidMaterial0.Sample(FluidSampler, input.UV + float2(0, tap.y));
    float3 normal = normalize(float3(2 * (left.a - right.a), 2 * (up.a - down.a), .3));
    float shading = .55 + .65 * saturate(dot(normal, normalize(float3(-.4, -.6, 1))));
    float2 screen = float2((2 * input.UV.x - 1) * AspectRatio, 1 - 2 * input.UV.y);
    float2 p = float2(screen.x, -screen.y);
    float4 mapping = FlightMap(p);
    float4 source = FlightAudio(mapping.x, mapping.y,
        step(0, p.x - FlightVanishingPoint().x));
    // Shade the fresh response and retained response together. They are current
    // excitation and memory of one material, sharing its palette and lighting.
    float3 material = (2 + 4 * Reaction.Strength) * FlightLightSource(p, source, mapping).rgb
        + (.4 + 2 * Reaction.Afterglow) * dye.rgb;
    float3 light = material * shading
        + .12 * Reaction.Afterglow * (left.rgb + right.rgb + up.rgb + down.rgb);
    light *= FlightLightning(source, mapping);
    return FlightOutput(light, screen, Flight.Glow);
}
