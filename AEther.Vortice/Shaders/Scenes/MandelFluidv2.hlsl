#include "../Common/Inputs.hlsl"
#include "../Common/FractalTypes.hlsl"
struct FluidParameters
{
    float Flow;
    float HalfLife;
    float Diffusion;
    float Noise;
    float Curl;
};
struct CometParameters
{
    float Size;
    float Morph;
    float Tail;
    float Shockwaves;
    float Background;
};
cbuffer Parameters
{
    GeometryParameters Geometry = { 0.3125, 0.45, 0.5, 0.44, 0.5 };
    QualityParameters Quality = { false, 0.37931034, 0.45 };
    FluidParameters Fluid = { 0.544, 0.385, 0.609, 0.45, 0.986 };
    CometParameters Comet = { 1, 0.726, 0.679, 1, 0.472 };
};
// The shared orbit supplies both the foreground silhouette and the original
// direct slice, projected separately as the fine background structure.
#define MANDELBOX_CUSTOM_PS
#include "../Common/Mandelbox.hlsl"
#define MANDEL_COMET
#include "../Common/MandelComet.hlsl"
Texture2D<float4> FluidDye;
SamplerState FluidSampler;
float3 CometShade(float2 uv, float sampleWidth)
{
    float2 p = (2 * uv - 1) * float2(AspectRatio, 1);
    float2 q = p - CometHead();
    float radius = length(q);
    float2 distances = CometDistances(p);
    float surface = distances.x - CometBoundary();
    float pixel = max(1e-5, fwidth(surface) * sampleWidth);
    float outside = smoothstep(-pixel, pixel, surface);
    float relief = smoothstep(-7, -.3, log2(distances.x / distances.y));
    float frequency = saturate((log2(distances.x) + 17) / 17);
    float angleFrequency = atan2(abs(q.x), q.y) / 3.14159265;
    float stereo = .5 + .5 * q.x / max(radius, 1e-5);
    float4 current = CurrentSpectrum(angleFrequency, stereo);
    float rayNoise;
    float sourceToneFrequency = CometRayFrequency(q, distances, rayNoise);
    // Flash has a 45 ms release; cubing its bounded response makes an onset
    // illuminate the entire wake briefly, then relinquish it to advancing fronts.
    float flash = pow(1 - exp(-4 * max(0, VisualFlash)), 3);
    float t = CometTime();

    // The untouched Mandelbox slice supplies fine spectral structure behind
    // the object. Compose in light space to retain contrast during loud hits.
    // A single advected sheet supplies broad rolling motion. Three cheap dye
    // taps replace the old five-depth procedural volume and its noise field.
    uint dyeWidth, dyeHeight; FluidDye.GetDimensions(dyeWidth, dyeHeight);
    float2 dyeTap = 1.5 / float2(dyeWidth, dyeHeight);
    float4 wake = FluidDye.Sample(FluidSampler, uv);
    float2 slope = float2(
        FluidDye.Sample(FluidSampler, uv + float2(dyeTap.x, 0)).a - wake.a,
        FluidDye.Sample(FluidSampler, uv + float2(0, dyeTap.y)).a - wake.a);
    float wakeBody = 1 - exp(-.65 * max(0, wake.a));
    float2 wakeBend = .022 * slope / (1 + length(slope));
    wakeBend *= smoothstep(.2, .65, radius) * (1 - smoothstep(.65, 1, max(abs(2 * uv.x - 1), abs(2 * uv.y - 1))))
        * (.25 + .75 * rayNoise);
    float sliceRelief;
    float toneFrequency;
    float3 sliceReaction = CometReaction();
    sliceReaction.z = rayNoise;
    float3 slice = ShadeSliceAt(uv + wakeBend, sampleWidth, t, sliceReaction, .7, true, sourceToneFrequency, sliceRelief, toneFrequency);
    float3 background = -log(max(1e-5, 1 - slice)) * Comet.Background;
    // Every tonal layer uses the same background-folded ray coordinate: sharp
    // lightning and the direct slice travel together.
    float4 currentTone = CurrentSpectrum(toneFrequency, stereo);
    float4 currentHull = CurrentSpectrum(frequency, stereo);
    float tone = pow(1 - exp(-4 * max(0, currentTone.r)), 2);
    float noise = pow(1 - exp(-4 * max(0, max(currentTone.b, currentHull.b))), 2);

    // Use the slice's distance hulls as the wave coordinate. Near the surface,
    // each onset traces the actual fractal boundary, then expands and disperses.
    // History age, rather than a beat oscillator, keeps separate hits separate.
    float travel = max(0, surface);
    // The fine background folds refract the outgoing front even after it has
    // left the small object's immediate boundary, retaining fractal flashes.
    travel *= exp2(.38 * (2 * sliceRelief - 1) * smoothstep(.05, .5, radius));
    // Place the retained-history horizon beyond the corners, including portrait
    // and ultrawide views, so fronts leave the screen while still expanding.
    float extent = length(float2(AspectRatio, 1));
    float age = log(1 + 35 * travel / max(1, extent * .65)) / 6;
    float attackFrequency = saturate(angleFrequency + .09 * sin(3.14159265 * angleFrequency)
        * (2 * relief - 1));
    float attackAge = age * lerp(1.12, .88, angleFrequency);
    float width = max(.022, min(.065, fwidth(attackFrequency) * sampleWidth));
    float timeWidth = min(.018, fwidth(attackAge) * sampleWidth);
    float attack = .5 * TransientHistory(attackFrequency, attackAge, stereo, width)
        + .25 * TransientHistory(attackFrequency, attackAge - timeWidth, stereo, width)
        + .25 * TransientHistory(attackFrequency, attackAge + timeWidth, stereo, width);
    float currentAttack = .5 * current.g + .25 * (CurrentSpectrum(attackFrequency - width, stereo).g
        + CurrentSpectrum(attackFrequency + width, stereo).g);
    // The live edge flashes immediately, before its row enters delayed history.
    float rim = exp(-max(0, surface) / max(.0015, 1.5 * pixel));
    float front = pow(1 - exp(-4 * max(0, attack)), 2) * exp(-2.8 * age);
    front *= ShockEdgeAttenuation(uv, attackAge);
    float3 flashPalette = lerp(CometPalette(attackFrequency, -.07 * t), float3(.7, .88, 1), .3);
    float excitation = Comet.Shockwaves * (7 * flash + 5 * front);
    float3 light = background * (.8 * pow(CometReaction().x, 2) + .45 * noise + .6 * excitation);
    // Pigment supplies soft material between sharp rays. Existing orbit relief
    // adds fine wisps without any additional fractal or 3D-noise evaluations.
    float3 wakeColor = wake.rgb / max(.001, wake.a);
    float wakeRelief = lerp(.4, 1.2, sliceRelief) * (1 + .3 * saturate(length(slope)));
    float edge = 1 - .7 * smoothstep(.6, 1.2, length(2 * uv - 1));
    light += (1 + 3 * Comet.Tail) * wakeColor * wakeBody * wakeRelief * edge
        * (.16 * tone + .24 * noise + .7 * excitation);
    // Tones pick fine distance hulls; noise reveals broader folded texture.
    // Every term follows a spectral component, without persistent field lighting.
    light += CometPalette(frequency, -.05 * t) * Comet.Background
        * (1.5 * tone * pow(sliceRelief, 3) + .55 * noise * sliceRelief);
    // A narrow luminous core traces the same route as the direct slice beams.
    // Near the cutout it branches through fine folds; outside it becomes the
    // stable angular ray selected by the note, with no independent animation.
    float filament = pow(1 - exp(-6 * max(0, currentTone.r)), 14);
    float attachment = CometRayAttachment(q, distances, rayNoise);
    light += CometPalette(toneFrequency, .03 * t) * filament
        * (1 + 3 * Comet.Background) * (.15 + 5.5 * attachment) * (.45 + .55 * relief);
    // Finer orbit structure splits off secondary arcs at the emission surface.
    // Both routes converge back to the same angular note as they escape.
    float fineFold = smoothstep(-13, -5, log2(distances.x / distances.y));
    float branchFrequency = saturate(toneFrequency + .09 * attachment * (.15 + 1.85 * rayNoise)
        * sin(3.14159265 * toneFrequency) * (2 * fineFold - 1));
    float branchTone = max(0, CurrentSpectrum(branchFrequency, stereo).r);
    float branch = pow(1 - exp(-6 * branchTone), 18) * attachment * attachment * (.2 + .8 * rayNoise);
    float3 branchPalette = lerp(CometPalette(branchFrequency, .03 * t), float3(.8, .93, 1), .4);
    light += branchPalette * branch * (2 + 4 * Comet.Background);
    light += float3(.8, .93, 1) * filament * attachment * attachment * 1.1;
    light += (1 + 8 * Comet.Shockwaves) * flashPalette * front * (.55 + .7 * relief);
    light += CometPalette(frequency, .03 * t) * rim
        * (.08 * tone + .035 * noise
            + (2 + 7 * Comet.Shockwaves) * pow(1 - exp(-4 * max(0, currentAttack)), 2));

    // Occlude every layer, including the shock flash: the moving body is black.
    light *= outside;
    return 1 - exp(-max(0, light));
}

float4 PS(const PSin input) : SV_Target
{
    int samples = Quality.Antialiasing ? 2 : 1;
    float sampleWidth = 1.0 / samples;
    float2 uvX = ddx(input.UV), uvY = ddy(input.UV);
    float3 color = 0;
    [loop] for (int y = 0; y < samples; ++y)
        [loop] for (int x = 0; x < samples; ++x)
        {
            float2 offset = (float2(x, y) + .5) * sampleWidth - .5;
            color += CometShade(input.UV + uvX * offset.x + uvY * offset.y, sampleWidth);
        }
    return float4(pow(saturate(color * sampleWidth * sampleWidth), 1 / 2.2), 1);
}
