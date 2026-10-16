#include "../Common/Flight.hlsl"

cbuffer Parameters
{
    float FlightSpeed = .4;
    float Branching = .65;
    float Curl = .5;
    float Twist = .65;
    float Aperture = .45;
    float AudioGain = .55;
    float Glow = .45;
    float Background = .12;
    float2 Steering = float2(.5, .5);
};

// Flame-inspired finite orbit filaments on depth slices. There is no accumulated
// chaos-game histogram here: these sketches reevaluate detail without feedback blur.
float4 PS(PSin input) : SV_Target
{
    float2 screen = FlightScreen(input.UV, Steering);
    float speed = 10 + 22 * FlightSpeed;
    float camera = T * speed;
    float spacing = 1.4;
    float first = floor(camera / spacing) + 1;
    float background = Background * VisualBreath;
    float3 light = float3(.004, .007, .016) * background;
    [loop] for (int layer = 0; layer < 23; ++layer)
    {
        float world = (first + layer) * spacing;
        float depth = world - camera;
        if (depth > speed) break;
        float age = saturate((speed - depth) / speed);
        float birth = T - age;
        float2 p = screen * depth / 1.6;
        p += .3 * float2(sin(world * .21), cos(world * .17));
        p = FlightRotate(p, (Twist * 2 - 1) * world * .2);
        float radius = length(p);
        float angle = atan2(p.y, p.x);
        float frequency = atan2(abs(p.x), -p.y) / 3.14159265;
        float4 audio = FlightAudio(frequency, age, smoothstep(-.15, .15, p.x));
        float level = FlightLevel(audio, AudioGain);
        float noise = 1 - exp(-8 * audio.b);

        float2 z = p * .55;
        float scale = 1;
        float filament = 20;
        [unroll] for (int iteration = 0; iteration < 7; ++iteration)
        {
            // Symmetric branches followed by a curved variation. Birth time fixes
            // each cohort's shape while successive cohorts explore different maps.
            z = abs(z);
            z = FlightRotate(z, -.35 - .65 * Branching + .12 * sin(birth * .3));
            z = z * 1.65 - float2(1.1, .35);
            z.y += Curl * .32 * sin(2.4 * z.x + birth * .25);
            scale *= 1.65;
            filament = min(filament, abs(z.y) / scale);
        }
        float aperture = .65 + 1.8 * Aperture;
        float veil = smoothstep(aperture - .3, aperture + .2, radius)
            * (1 - smoothstep(3.8, 5.6, radius));
        float strand = FlightLine(filament, .005 + .007 * noise);
        float halo = exp(-45 * filament);
        float visibility = smoothstep(0, 1, depth) * (1 - smoothstep(.75, 1, depth / speed));
        float3 color = FlightPalette(frequency + .12 * sin(3 * angle), birth);
        light += color * veil * visibility * (strand + .18 * halo)
            * (.05 * background + .65 * level);
        // Onsets outline a sparse membrane between the branches.
        float membrane = FlightLine(radius - aperture - .18 * sin(5 * angle + birth), .012);
        light += color * membrane * visibility * (1 - exp(-12 * audio.g)) * .5;
    }
    return FlightOutput(light, screen, Glow);
}
