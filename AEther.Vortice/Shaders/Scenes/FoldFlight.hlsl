#include "../Common/Flight.hlsl"

cbuffer Parameters
{
    float FlightSpeed = .55;
    float FoldDetail = .7;
    float Twist = .4;
    float Bend = .3;
    float AudioGain = .55;
    float Glow = .4;
    float Background = .15;
    float2 Steering = float2(.5, .5);
};

// Box-fold portals: geometry belongs to world-space cohorts, not the current frame.
// This is a finite fold construction, not a Mandelbox distance estimator.
float4 PS(PSin input) : SV_Target
{
    float2 screen = FlightScreen(input.UV, Steering);
    float speed = 12 + 24 * FlightSpeed;
    float farPlane = speed;
    float camera = T * speed;
    float spacing = 1.55;
    float first = floor(camera / spacing) + 1;
    float background = Background * VisualBreath;
    float3 light = float3(.006, .008, .018) * background;
    [loop] for (int layer = 0; layer < 24; ++layer)
    {
        float world = (first + layer) * spacing;
        float depth = world - camera;
        if (depth > farPlane) break;
        float age = saturate((farPlane - depth) / speed);
        float birth = T - age;
        float2 p = screen * depth / 1.5;
        p -= Bend * float2(1.1 * sin(world * .15) - 1.1 * sin(camera * .15),
            .7 * cos(world * .11) - .7 * cos(camera * .11));
        p = FlightRotate(p, (Twist * 2 - 1) * world * .14);

        float squareRadius = lerp(length(p), max(abs(p.x), abs(p.y)), .8);
        float rim = FlightLine(squareRadius - 2.25, .025);
        float2 z = p * .65;
        float scale = 1;
        float filaments = 20;
        [unroll] for (int iteration = 0; iteration < 6; ++iteration)
        {
            z = FlightRotate(abs(z), .42 + .14 * sin(birth * .23));
            z = 1.75 * z - float2(1.15, .7);
            scale *= 1.75;
            filaments = min(filaments, abs(length(z) - .48) / scale);
        }
        float lace = FlightLine(filaments, .006 + .004 * FoldDetail)
            * smoothstep(1.1, 1.6, squareRadius) * (1 - smoothstep(2.25, 2.9, squareRadius));
        float frequency = atan2(abs(p.x), -p.y) / 3.14159265;
        float4 audio = FlightAudio(frequency, age, smoothstep(-.1, .1, p.x));
        float level = FlightLevel(audio, AudioGain);
        float attack = 1 - exp(-12 * audio.g);
        float visibility = smoothstep(0, .8, depth) * (1 - smoothstep(.8, 1, depth / farPlane));
        float3 color = FlightPalette(frequency, birth);
        light += color * visibility * (rim + lace * FoldDetail)
            * (.08 * background + 1.25 * level + attack);
        light += color * visibility * exp(-24 * abs(squareRadius - 2.25))
            * (.012 * background + .07 * level);
    }
    return FlightOutput(light, screen, Glow);
}
