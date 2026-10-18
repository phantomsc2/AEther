// Eight code-generated quads: a corner, an isolated shelf and a focal cutout.
#define GEOMETRY_VERTEX_COUNT 48
#define PROJECTIVE_FLIGHT
#define GEOMETRY_AFTERGLOW
#define FLIGHT_SMOOTH_HISTORY
#include "../Common/FractalTypes.hlsl"
#include "../Common/Flight.hlsl"

struct EdgeCamera
{
    float Speed;
    float Height;
    float Clearance;
    float FieldOfView;
    float Roll;
    float2 Aim;
};
struct EdgeMaterial
{
    float Scale;
    float Detail;
    float Relief;
    float AudioGain;
    float Lightning;
    float FocalSize;
};
struct EdgeLighting { float Ambient; float Sun; float Headlight; };
struct EdgeEffects { float Amount; float Turbulence; float Exposure; };
struct FluidParameters { float Flow; float HalfLife; float Diffusion; float Background; float Curl; bool PeriodicBoundary; };
cbuffer Parameters : register(b1)
{
    EdgeCamera Camera = { .45, .4, .35, .5, .5, float2(.5, .5) };
    EdgeMaterial Material = { .45, .7, .35, .7, .7, .45 };
    EdgeLighting Lighting = { .2, .65, .6 };
    EdgeEffects Effects = { .2, .2, .35 };
    FluidParameters Fluid = { .1, .12, .3, .1, .1, false };
};

float EdgePath(float z) { return .3 * sin(z * .075) + .12 * sin(z * .19); }
float EdgeTravel() { return MusicalMovementTime * (1 + 12 * Camera.Speed) + .6 * S; }

void EdgeView(out float3 eye, out float3 right, out float3 up, out float3 forward)
{
    float travel = EdgeTravel();
    eye = float3(EdgePath(travel) - .55 + .25 + .65 * Camera.Clearance,
        -.8 + .5 + .8 * Camera.Height, travel);
    float3 target = float3(EdgePath(travel + 12) + .25, -.02, travel + 12);
    target.xy += (Camera.Aim * 2 - 1) * float2(2, 1.5);
    forward = normalize(target - eye);
    right = normalize(cross(float3(0, 1, 0), forward));
    up = cross(forward, right);
    float roll = (Camera.Roll - .5) * 1.6;
    float3 oldRight = right;
    right = cos(roll) * right + sin(roll) * up;
    up = -sin(roll) * oldRight + cos(roll) * up;
}

struct EdgeVertex
{
    float4 Position : SV_POSITION;
    float3 World : TEXCOORD0;
    float2 UV : TEXCOORD1;
    float3 Normal : TEXCOORD2;
    float Depth : TEXCOORD3;
    nointerpolation uint Face : TEXCOORD4;
};

EdgeVertex GeometryVS(uint id : SV_VertexID)
{
    uint face = id / 6, corner = id % 6;
    float2 uv = corner == 0 ? float2(0, 0) : corner == 1 ? float2(0, 1)
        : corner == 2 || corner == 3 ? float2(1, 0) : corner == 4 ? float2(0, 1) : float2(1, 1);
    float3 eye, right, up, forward;
    EdgeView(eye, right, up, forward);
    float3 world, normal;
    if (face < 6)
    {
        float z = EdgeTravel() - 1 + (face / 2 + uv.y) * 11;
        float edge = EdgePath(z) - .55;
        bool wall = (face % 2) != 0;
        world = wall ? float3(edge, lerp(-.8, 2.1, uv.x), z)
            : float3(edge + 2.7 * uv.x, -.8, z);
        normal = wall ? float3(1, 0, 0) : float3(0, 1, 0);
    }
    else if (face == 6)
    {
        // This shelf is physically separated from the corner by open space.
        world = float3(lerp(3.8, 6.5, uv.x), -.35, EdgeTravel() + 12 + 20 * uv.y);
        normal = float3(0, 1, 0);
    }
    else
    {
        float size = .45 + 1.4 * Material.FocalSize;
        world = eye + forward * 21 + ((uv.x * 2 - 1) * right + (1 - 2 * uv.y) * up) * size;
        normal = -forward;
    }
    float3 relative = world - eye;
    float3 view = float3(dot(relative, right), dot(relative, up), dot(relative, forward));
    float focal = 1 / tan((45 + 45 * Camera.FieldOfView) * .00872664626);
    EdgeVertex output;
    output.Position = float4(view.x * focal / AspectRatio, view.y * focal,
        120.0 / 119.95 * view.z - 6.0 / 119.95, view.z);
    output.World = world;
    output.UV = uv;
    output.Normal = normal;
    output.Depth = view.z;
    output.Face = face;
    return output;
}

// The same known neighborhood appears on every surface. World coordinates vary
// continuously; there is no per-frame depth proxy or cloud-grid geometry.
float2 EdgeFractal(float3 world, float2 uv, uint face)
{
    float3 p = 2.6 * sin(world * (.12 + .25 * Material.Scale));
    if (face == 7) p = float3((uv * 2 - 1) * .7, .28) + float3(-.42, .13, 0);
    float3 z = p;
    float derivative = 1;
    [unroll] for (int iteration = 0; iteration < 8; ++iteration)
    {
        z = 2 * clamp(z, -1, 1) - z;
        float fold = 1 / clamp(dot(z, z), .25, 1);
        z = -1.65 * fold * z + p;
        derivative = 1.65 * fold * derivative + 1;
    }
    float coordinate = log2(max(1e-7, length(z) / derivative));
    float phase = coordinate * (2 + 2 * Material.Detail);
    float footprint = fwidth(phase);
    float ring = lerp(exp(-8 * abs(sin(phase))), .1, smoothstep(.15, .65, footprint));
    return float2(coordinate, ring);
}

float4 PS(EdgeVertex input) : SV_Target
{
    float2 disk = input.UV * 2 - 1;
    if (input.Face == 7) clip(1 - dot(disk, disk));
    float2 field = EdgeFractal(input.World, input.UV, input.Face);
    float frequency = .5 + .5 * sin(input.World.x * .4 + input.World.y * .3);
    float age = (32 - input.Depth) / 32;
    if (input.Face == 7)
    {
        frequency = saturate(.5 + .45 * disk.x + .06 * sin(field.x));
        age = saturate(.35 + .28 * disk.y);
    }
    float4 audio = FlightAudio(frequency, age, step(0, input.World.x - EdgePath(input.World.z)));
    float gain = 4 + 60 * Material.AudioGain;
    float tone = 1 - exp(-gain * audio.r);
    float noise = 1 - exp(-gain * audio.b);
    float attack = 1 - exp(-gain * audio.g);
    float4 global = MusicAtAge(age);
    float bed = MusicBody(global);
    attack = max(attack, .55 * MusicRise(global));
    float3 eye, right, up, forward;
    EdgeView(eye, right, up, forward);
    float3 normal = input.Normal;
    float3 dpdx = ddx(input.World), dpdy = ddy(input.World);
    float3 r1 = cross(dpdy, normal), r2 = cross(normal, dpdx);
    float determinant = dot(dpdx, r1);
    float height = .03 * sin(field.x * 2);
    float3 gradient = (ddx(height) * r1 + ddy(height) * r2)
        * sign(determinant) / max(1e-5, abs(determinant));
    gradient *= min(1, 3 / max(.001, length(gradient)));
    // Unresolved bump gradients produce glitter even when contour color is
    // filtered. Fade the bump itself before distant detail becomes subpixel.
    float bumpVisibility = 1 - smoothstep(.08, .35, fwidth(field.x));
    normal = normalize(normal - Material.Relief * bumpVisibility * gradient);
    if (input.Face == 7)
        normal = normalize(right * disk.x - up * disk.y - forward * sqrt(saturate(1 - dot(disk, disk))));
    float3 lampPosition = eye + forward * 5 + up * 2;
    float3 lamp = lampPosition - input.World;
    float diffuse = .06 + Lighting.Ambient * .35
        + Lighting.Sun * saturate(dot(normal, normalize(float3(.45, .7, -.25))));
    diffuse += Lighting.Headlight * 4 * saturate(dot(normal, normalize(lamp))) / (1 + .07 * dot(lamp, lamp));
    float3 viewDirection = normalize(eye - input.World);
    float specular = pow(saturate(dot(normal, normalize(normalize(lamp) + viewDirection))), 32);
    float3 palette = lerp(float3(.32, .48, .55), FlightPalette(frequency, .03 * input.World.z), .65);
    float body = (.3 * bed + .65 * noise + .25 * tone) * (.25 + .75 * field.y)
        * (1 + .35 * MusicStructure(global));
    float lightning = 1 + (4 + 18 * Material.Lightning) * attack * pow(saturate(field.y), .7);
    float3 light = (palette * body * diffuse + .12 * specular * (noise + tone + .5 * bed)) * lightning;
    // Keep the small central patch readable as the place to look toward.
    if (input.Face == 7) light *= 1.5;
    light *= exp(-input.Depth * .012);
    return float4(light, saturate(input.Depth / 120));
}

#include "../Common/GeometryAfterglow.hlsl"
