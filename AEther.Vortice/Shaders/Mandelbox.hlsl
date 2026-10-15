// A finite-iteration 3D Mandelbox distance estimate, evaluated on a plane.
// Integrated audio moves the slice; the box/sphere folds and scale stay fixed.
float2 BoxDistance(float3 p)
{
    float3 z = p;
    float derivative = 1;
    float coarse = 0;
    float depth = clamp(FractalDepth, 3, 10);
    int lower = (int)floor(depth);
    int upper = (int)ceil(depth);
    float lowLog = 0, highLog = 0;
    // One orbit supplies both adjacent estimates. Four iterations are always
    // evaluated so the coarse reference stays fixed even at depth three.
    [loop] for (int i = 1; i <= max(4, upper); ++i)
    {
        z = 2 * clamp(z, -1, 1) - z;
        float fold = 1 / clamp(dot(z, z), .25, 1);
        z *= fold;
        derivative *= fold;
        z = -1.5 * z + p;
        derivative = 1.5 * derivative + 1;
        float distance = max(1e-8, length(z) / derivative);
        if (i == 4) coarse = distance;
        if (i == lower) lowLog = log2(distance);
        if (i == upper) highLog = log2(distance);
    }
    // Spectral UVs use log distance, so reveal each new level in that space.
    // Smoothstep also removes the slope change when crossing integer depths.
    float blend = smoothstep(0, 1, frac(depth));
    return float2(exp2(lerp(lowLog, highLog, blend)), coarse);
}

float4 CurrentSpectrum(float frequency, float right)
{
    float2 uv = float2(saturate(frequency), .5);
    return lerp(Spectrum0.Sample(Linear, uv), Spectrum1.Sample(Linear, uv), right);
}

float4 History(float frequency, float age, float right)
{
    if (age < 0 || age >= 1 || age * HistorySpan > HistoryAvailable
        || age * HistorySpan < HistoryNewestAge) return 0;
    float2 uv = float2(saturate(frequency), HistogramShift - age * HistorySpan);
    return lerp(Histogram0.Sample(HullHistory, uv), Histogram1.Sample(HullHistory, uv), right);
}

float4 PS(const PSDefaultin IN) : SV_Target
{
    float2 screen = 2 * IN.UV - 1;
    screen.x *= AspectRatio;
    screen.y = -screen.y;
    // Musical time traverses another dimension of the fixed 3D field.
    // All transforms follow the same inertial clock: audio builds momentum,
    // then the slice coasts smoothly to rest as that momentum relaxes.
    float zoom = 1.9 + .22 * sin(.73 * S);
    // Cover the viewport with the interior of the box, including wide windows.
    // Cropping the slice avoids sampling empty space outside its outer bounds.
    float3 p = float3(screen * (zoom / max(1, AspectRatio)),
        SliceDepth + .85 * sin(1.7 * S) + .3 * sin(3.1 * S));
    p = mul(Rz(.32 * S + .18 * sin(1.3 * S)), p);
    p = mul(Rx(.48 * sin(.83 * S)), p);
    p = mul(Ry(.55 * sin(1.13 * S)), p);
    float radius = length(screen);
    float outer = smoothstep(.5, 2.4, radius);
    float2 distances = max(1e-8, BoxDistance(p));
    // Equal ratios of distance become nested hulls. A steady tone selects a
    // fixed shell in this field, rather than displacing the fractal surface.
    float deepFrequency = (log2(distances.x) + 17) / 17;
    float coarseFrequency = (log2(distances.y) + 10) / 10;
    float frequency = saturate(lerp(deepFrequency, coarseFrequency, outer));
    // Project time as r(t) = horizon * (1 - exp(-k*t)). A new row therefore
    // covers a large central area; its outward speed falls exponentially.
    // Warp the projected radius with the fold field so equal-age fronts hug
    // the fractal while retaining a center-to-edge order.
    float foldCoordinate = smoothstep(-7, -.3, log2(distances.x / distances.y));
    // Euclidean distance gives circular flashes even in a wide viewport.
    // Put the horizon at the corners so the field still fills the whole canvas.
    float viewRadius = radius / length(float2(AspectRatio, 1));
    // Fold the circles into the fractal, fading the warp at the horizon.
    float warpedRadius = viewRadius * (1 + (1 - viewRadius) * saturate(HistoryWarp)
        * lerp(.72, .46, outer) * (2 * foldCoordinate - 1));
    float age = -log(max(.001, 1 - warpedRadius / 1.02)) / TimeCompression;
    // Time and frequency occupy separate dimensions of the history texture.
    // Angle places a note on a ray; the distance field places an attack on a hull.
    // Each stereo channel occupies its own semicircle. Both run from bass
    // at the bottom to treble at the top, meeting along the vertical axis.
    float right = step(0, screen.x);
    float rayFrequency = atan2(abs(screen.x), -screen.y) / 3.14159265;
    // Pull tonal routes into the same folded UV field as the percussion.
    // They still retain an angular identity, but now reveal far more box detail.
    // Keep the bottom/top frequency endpoints anchored while folding the sides.
    float angularBlend = 4 * rayFrequency * (1 - rayFrequency);
    float tonalFrequency = lerp(rayFrequency, frequency, .68 * saturate(HistoryWarp) * angularBlend);
    float footprint = max(fwidth(frequency), .028 * outer);
    float timeFootprint = min(.025, fwidth(age));
    float4 hull = History(frequency, age, right);
    float4 ray = History(tonalFrequency, age, right);
    [unroll] for (int tap = -1; tap <= 1; ++tap)
    {
        float offset = tap / 2.0;
        hull += History(frequency + offset * footprint, age + offset * timeFootprint, right);
        ray += History(tonalFrequency, age + offset * timeFootprint, right);
    }
    hull *= .25;
    ray *= .25;
    float4 currentHull = CurrentSpectrum(frequency, right);
    float4 currentRay = CurrentSpectrum(tonalFrequency, right);
    float center = exp(-12 * radius);
    hull = lerp(hull, currentHull, center);
    ray = lerp(ray, currentRay, center);
    float dust = smoothstep(.28, .85, age);
    float trail = exp(-2.3 * age);
    // The rays inherit nested box detail rather than looking like flat cones.
    // Suppress the subpixel part of this modulation to keep the image stable.
    float foldPhase = 14 * log2(distances.x);
    float detail = .5 + .5 * cos(foldPhase) * saturate(.8 / max(.001, fwidth(foldPhase)));
    float palettePhase = .11 * S;
    float3 rayPalette = .5 + .5 * cos(6.2831853 * (tonalFrequency * .9 + palettePhase + float3(.04, .38, .62)));
    float3 hullPalette = .5 + .5 * cos(6.2831853 * (frequency * 1.3 - palettePhase + float3(.04, .38, .62)));
    float sustain = 2.8 * max(0, ray.r) * trail * (.18 + 1.6 * detail);
    // Attacks are crisp near the core, then widen and disperse into a faint
    // coarse glow near the old-history horizon.
    float attack = 6.2 * max(0, hull.g) * trail * (.5 + 1.4 * detail);
    float4 dispersed = .5 * (History(frequency - .07 * dust, age, right)
                            + History(frequency + .07 * dust, age, right));
    float broad = 2.0 * max(0, dispersed.g) * dust * trail;
    float3 light = rayPalette * sustain
        + hullPalette * attack * (1 - .7 * dust)
        + float3(.36, .53, .67) * broad;
    light += hullPalette * .4 * max(0, hull.b) * trail * detail;
    // Bound the exposure response so a loud hit retains contour contrast.
    float impact = 1 - exp(-3 * VisualFlash);
    light *= 1 + 1.2 * impact;
    // The undelayed onset ignites the core immediately; the recorded event
    // subsequently propagates through the history field without repeated triggers.
    float coreFlash = (1 - exp(-3 * max(0, currentHull.g))) * exp(-7 * radius);
    light += float3(.6, .78, 1) * coreFlash * (1 + detail);
    float vignette = 1 - .38 * smoothstep(.25, 2.5, length(screen));
    float3 color = 1 - exp(-light * vignette);
    return float4(pow(saturate(color), 1 / 2.2), 1);
}

