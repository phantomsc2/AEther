#include "../Common/Flight.hlsl"

cbuffer Parameters
{
    float FlightSpeed = .55;
    float Bend = .35;
    float TowerHeight = .6;
    float Backdrop = .65;
    float AudioGain = .55;
    float Glow = .35;
    float Background = .18;
    float2 Steering = float2(.5, .5);
};

float RoadCurve(float world)
{
    return Bend * (1.4 * sin(.09 * world) + .55 * sin(.19 * world));
}

// A concrete forward-flight reference: a spectral road, roadside columns and
// recursive overhead arches. The road has no gameplay or collision state.
float4 PS(PSin input) : SV_Target
{
    float2 screen = FlightScreen(input.UV, Steering);
    screen.y -= .12;
    float speed = 12 + 24 * FlightSpeed;
    float camera = MusicalMovementTime * speed;
    float3 light = 0;

    // Ray intersection with a horizontal road below the camera.
    if (screen.y < -.001)
    {
        float depth = -1.5 / screen.y;
        float world = camera + depth;
        float x = screen.x * depth / 1.5 - (RoadCurve(world) - RoadCurve(camera));
        float age = saturate((speed - depth) / speed);
        float frequency = saturate((x + 2.2) / 4.4);
        float4 audio = FlightAudio(frequency, age, step(0, x));
        float level = FlightLevel(audio, AudioGain);
        float fade = (1 - smoothstep(speed * .75, speed, depth)) * smoothstep(0, .8, depth);
        float rails = FlightLine(abs(x) - 2.2, .025);
        float lane = FlightLine(abs(frac((x + 2.2) / .55) - .5) * .55, .012);
        float cross = FlightLine(abs(frac(world / 1.55) - .5) * 1.55, .018);
        float inside = 1 - smoothstep(2.15, 2.25, abs(x));
        float3 color = FlightPalette(frequency, MusicalMovementTime - age);
        float recordedBackground = Background * AmplitudeResponse(audio.b, .01);
        light += color * fade * (rails * (.15 * recordedBackground + level)
            + inside * (lane * (.035 * recordedBackground + .15 * level)
                + cross * (.08 * recordedBackground + .7 * level)));
        light += color * fade * inside * level * .045;
    }

    float first = floor(camera / 1.55) + 1;
    [loop] for (int layer = 0; layer < 24; ++layer)
    {
        float world = (first + layer) * 1.55;
        float depth = world - camera;
        if (depth > speed) break;
        float age = saturate((speed - depth) / speed);
        float birth = MusicalMovementTime - age;
        float2 p = screen * depth / 1.5;
        p.x -= RoadCurve(world) - RoadCurve(camera);
        float frequency = saturate((abs(p.x) - 2.5) / 3.0);
        float band = min(11, floor(frequency * 12));
        frequency = (band + .5) / 12;
        float4 audio = FlightAudio(frequency, age, step(0, p.x));
        float level = FlightLevel(audio, AudioGain);
        float recordedBackground = Background * AmplitudeResponse(audio.b, .01);
        float height = .12 + (1 + 4 * TowerHeight) * level;
        float localX = abs(p.x) - (2.5 + (band + .5) * .25);
        float vertical = smoothstep(-1.04, -.98, p.y) * (1 - smoothstep(height - 1, height - .94, p.y));
        float columns = FlightLine(localX, .07) * vertical;
        float top = FlightLine(p.y - (height - 1), .018) * (1 - smoothstep(.07, .1, abs(localX)));
        float visibility = smoothstep(0, .9, depth) * (1 - smoothstep(.8, 1, depth / speed));
        float3 color = FlightPalette(frequency, birth);
        light += color * visibility * (columns * (.025 * recordedBackground + .22 * level) + top * level);

        // Folded overhead scenery stays attached to a world-space slice.
        float2 z = float2(p.x, p.y + 1);
        float detail = 20, scale = 1;
        [unroll] for (int iteration = 0; iteration < 5; ++iteration)
        {
            z = abs(FlightRotate(z, .35 + .08 * sin(birth * .2))) * 1.7 - float2(1.6, 1.15);
            scale *= 1.7;
            detail = min(detail, abs(length(z) - .65) / scale);
        }
        float arch = FlightLine(detail, .007) * smoothstep(-.7, .4, p.y)
            * (1 - smoothstep(4, 7, length(p)));
        float4 skyAudio = FlightAudio(atan2(abs(p.x), max(.01, p.y)) / 1.5707963,
            age, step(0, p.x));
        light += FlightPalette(.65 + .15 * sin(p.x), birth) * arch * visibility * Backdrop
            * (.04 * Background * AmplitudeResponse(skyAudio.b, .01) + .5 * FlightLevel(skyAudio, AudioGain));
    }
    return FlightOutput(light, screen, Glow);
}
