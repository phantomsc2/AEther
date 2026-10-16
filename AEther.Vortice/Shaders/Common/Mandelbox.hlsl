// A finite-iteration 3D Mandelbox distance estimate, evaluated on a plane.
// Integrated audio moves the slice and slowly explores a family of orbit rules.
float2 BoxDistance(float3 p)
{
    // These parameters are uniform in space: no omitted spatial derivatives.
    // Musical time drives the normalized movement-speed multiplier.
    float t = (MusicalMovementTime * Quality.MovementSpeed * 10);
    float beta = 1 - Geometry.JuliaBlend * (.55 + .45 * sin(.11 * t + .4));
    float3 julia = Geometry.JuliaBlend * float3(.65 * sin(.13 * t + .7),
        .5 * sin(.17 * t - 1.1), .6 * cos(.09 * t + .2));
    float3 offset = beta * p + julia;
    float3x3 rotation = mul(Rz(Geometry.FoldRotation * sin(.19 * t + .6)),
        mul(Ry(Geometry.FoldRotation * sin(.13 * t - .4)), Rx(Geometry.FoldRotation * cos(.17 * t + .2))));
    // Log stretches sum to zero: change proportions while preserving volume.
    // Their largest stretch, rather than determinant, bounds radial expansion.
    float3 stretch = exp((Geometry.Anisotropy * 0.5) * cos(.14 * t + float3(0, 2.0943951, 4.1887902)));
    float expansion = abs((Geometry.Scale * 8 - 4)) * max(stretch.x, max(stretch.y, stretch.z));
    float3 z = p;
    float derivative = 1;
    float coarse = 0;
    float depth = max(1, FractalDepth * (1 + round(Quality.MaximumIterations * 29)) / 10);
    float lower = floor(depth);
    float upper = ceil(depth);
    float lowLog = 0, highLog = 0;
    // One orbit supplies both adjacent estimates. Four iterations are always
    // evaluated so the coarse reference stays fixed even at depth three.
    [loop] for (int i = 1; i <= max(4, upper); ++i)
    {
        float3 previousZ = z;
        float previousDerivative = derivative;
        z = 2 * clamp(z, -1, 1) - z;
        float fold = 1 / clamp(dot(z, z), .25, 1);
        z *= fold;
        derivative *= fold;
        z = (Geometry.Scale * 8 - 4) * stretch * mul(rotation, z) + offset;
        // For J_next = scale*D*R*J_sphere*J_box*J + beta*I,
        // rotations/reflections have norm one and the sphere-fold norm is fold.
        derivative = expansion * derivative + abs(beta);
        float distance = max(1e-8, length(z) / derivative);
        if (i == 4) coarse = distance;
        if (i == lower) lowLog = log2(distance);
        if (i == upper) highLog = log2(distance);
        // There is no iteration-count cap. Stop only before the orbit or its
        // expansion bound would exhaust float range, retaining the last finite
        // estimate for requested depths beyond that numerical horizon.
        if (i >= 4 && (derivative >= 1e30 || dot(z, z) >= 1e30
            || (all(z == previousZ) && derivative == previousDerivative)))
        {
            if (i < lower) lowLog = log2(distance);
            if (i < upper) highLog = log2(distance);
            break;
        }
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
    return lerp(FilteredHistogram0.Sample(HullHistory, uv), FilteredHistogram1.Sample(HullHistory, uv), right);
}

float TransientHistory(float frequency, float age, float right, float width)
{
    // A smooth spectral envelope, rather than separate hard frequency wedges.
    return .4 * History(frequency, age, right).g
        + .24 * (History(frequency - width, age, right).g + History(frequency + width, age, right).g)
        + .06 * (History(frequency - 2 * width, age, right).g + History(frequency + 2 * width, age, right).g);
}

float3 Shade(float2 uv, float sampleWidth)
{
    float2 screen = 2 * uv - 1;
    screen.x *= AspectRatio;
    screen.y = -screen.y;
    // Musical time traverses another dimension of the fixed 3D field.
    // All transforms follow the same inertial clock: audio builds momentum,
    // then the slice coasts smoothly to rest as that momentum relaxes.
    float movementTime = (MusicalMovementTime * Quality.MovementSpeed * 10);
    float zoom = 1.9 + .22 * sin(.73 * movementTime);
    // Cover the viewport with the interior of the box, including wide windows.
    // Cropping the slice avoids sampling empty space outside its outer bounds.
    // Rotate offsets around the screen-center sample, then translate the pivot.
    // Rotation/zoom have no displacement at screen == 0; depth drifts ten times
    // slower so the center remains an anchor while the surrounding field turns.
    float3 p = float3(screen * (zoom / max(1, AspectRatio)), 0);
    p = mul(Rz(.32 * movementTime + .18 * sin(1.3 * movementTime)), p);
    p = mul(Rx(.48 * sin(.83 * movementTime)), p);
    p = mul(Ry(.55 * sin(1.13 * movementTime)), p);
    p.z += (Geometry.SliceDepth * 4 - 2) + .85 * sin(.17 * movementTime) + .3 * sin(.31 * movementTime);
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
    float warpedRadius = viewRadius * (1 + (1 - viewRadius) * 0.95
        * lerp(.72, .46, outer) * (2 * foldCoordinate - 1));
    float age = -log(max(.001, 1 - warpedRadius / 1.02)) / 4.0;
    // Time and frequency occupy separate dimensions of the history texture.
    // Angle places spectral content around a front; the distance field folds it.
    // Blend to mono at the bass/treble poles so the channels meet smoothly
    // along the vertical axis. Squared angular width keeps stereo separation
    // concentrated around the mids at either side, independent of radius.
    float stereoPosition = screen.x / max(radius, 1e-5);
    float right = .5 + .5 * stereoPosition * abs(stereoPosition);
    // Frequency keeps a fixed angle matching the histogram's vertical order.
    // Tone frequency stays anchored; transients get a small bounded fold below.
    float tonalFrequency = atan2(abs(screen.x), -screen.y) / 3.14159265;
    float footprint = max(fwidth(frequency) * sampleWidth, .028 * outer);
    float timeFootprint = min(.025, fwidth(age) * sampleWidth);
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
    // Put the transient spectrum around each advancing front: bass below,
    // treble above, with the folds bending the spectral route between them.
    // Mild dispersion lets higher frequencies lead instead of sharing one ring.
    float attackFrequency = saturate(tonalFrequency + .08 * sin(3.14159265 * tonalFrequency)
        * (2 * foldCoordinate - 1) * smoothstep(.05, .3, radius));
    float attackAge = age * lerp(1.14, .88, tonalFrequency);
    float attackWidth = max(.022 + .025 * dust, min(.06, fwidth(attackFrequency) * sampleWidth));
    float currentAttack = .5 * CurrentSpectrum(attackFrequency, right).g
        + .25 * (CurrentSpectrum(attackFrequency - attackWidth, right).g
                 + CurrentSpectrum(attackFrequency + attackWidth, right).g);
    float attackEnvelope = lerp(TransientHistory(attackFrequency, attackAge, right, attackWidth), currentAttack, center);
#ifdef MANDEL_FLUID
    float palettePhase = .11 * (MusicalMovementTime * Quality.MovementSpeed * 10);
#else
    float palettePhase = .11 * S;
#endif
    float3 rayPalette = .5 + .5 * cos(6.2831853 * (tonalFrequency * .9 + palettePhase + float3(.04, .38, .62)));
    float3 hullPalette = .5 + .5 * cos(6.2831853 * (frequency * 1.3 - palettePhase + float3(.04, .38, .62)));
    float3 attackPalette = .5 + .5 * cos(6.2831853 * (attackFrequency * .9 - palettePhase + float3(.04, .38, .62)));
    // Let real fold depth carve the beams, fading unresolved relief toward its
    // midpoint. This adds structure without periodic contour bands or changing
    // the angle that identifies a tone.
    float reliefWidth = max(.04, fwidth(foldCoordinate) * sampleWidth);
    float relief = smoothstep(.35 - reliefWidth, .65 + reliefWidth, foldCoordinate);
    float tonalRelief = lerp(.5, relief, saturate(.2 / reliefWidth));
    // Let the folds interrupt a tone's path instead of leaving a continuous
    // bright spoke through the valleys. More of its light lives on the shells.
    float sustain = 1.9 * max(0, ray.r) * trail * lerp(.04, 1.25, tonalRelief * tonalRelief);
    // Where a tone's angular route meets its matching distance-field shell,
    // illuminate that shell as well. Angular support keeps mids at the sides.
    float tonalSupport = max(0, ray.r) / (.04 + max(0, ray.r));
    float sustainedHull = 2.2 * max(0, hull.r) * tonalSupport * trail;
    float3 sustainedHullPalette = .5 + .5 * cos(6.2831853 * (frequency * .9 + palettePhase + float3(.04, .38, .62)));
    // Attacks are crisp near the core, then widen and disperse into a faint
    // coarse glow near the old-history horizon.
    float attack = 6.2 * max(0, attackEnvelope) * trail * 1.2;
    float dispersed = TransientHistory(attackFrequency, attackAge, right, attackWidth + .07 * dust);
    float broad = 2.0 * max(0, dispersed) * dust * trail;
#ifdef MANDEL_FLUID
    // Energy reveals the underlying field throughout a passage. The material
    // supplies body; notes retain a directional ray near the source, then spread.
    float3 drives = saturate(float3(PoseX, PoseY, PoseZ)); // tone, transient, noise
    float together = pow(max(0, drives.x * drives.y * drives.z), 1.0 / 3.0);
    float interaction = .35 + .65 * together;
    float energy = saturate(VisualBreath);
    float3 field = hullPalette * energy * (.07 + .52 * tonalRelief);
    float plumeBlend = smoothstep(.08, .85, radius);
    float3 body = FluidLight(uv, tonalRelief) * lerp(.4, 1.0, plumeBlend);
    float3 beams = rayPalette * sustain * lerp(.8, .24, plumeBlend)
        + sustainedHullPalette * sustainedHull * .25;
    float noiseAir = 1 - exp(-2 * max(0, hull.b));
    float3 light = field + (body + beams) * interaction;
    // Recorded onsets remain crisp and ordered by history age. They illuminate
    // and expose the field rather than accumulating as long-lived pigment.
    float front = 1 - exp(-4 * max(0, attackEnvelope) * trail);
    light *= 1 + front * (2.5 + 2 * together) + .18 * drives.z * noiseAir;
    light += attackPalette * attack * (1 - .7 * dust) * (.65 + .35 * together)
        + float3(.36, .53, .67) * broad * .5;
    float impact = 1 - exp(-3 * max(0, VisualFlash));
    light *= 1 + 1.2 * impact;
    float coreFlash = (1 - exp(-3 * max(0, currentAttack))) * exp(-7 * radius);
    light += float3(.6, .78, 1) * coreFlash * 1.5;
#else
    float3 light = rayPalette * sustain
        + sustainedHullPalette * sustainedHull
        + attackPalette * attack * (1 - .7 * dust)
        + float3(.36, .53, .67) * broad;
    light += hullPalette * .2 * max(0, hull.b) * trail;
#endif
#ifndef MANDEL_FLUID
    // Bound the exposure response so a loud hit retains contour contrast.
    float impact = 1 - exp(-3 * VisualFlash);
    light *= 1 + 1.2 * impact;
    // The undelayed onset ignites the core immediately; the recorded event
    // subsequently propagates through the history field without repeated triggers.
    float coreFlash = (1 - exp(-3 * max(0, currentAttack))) * exp(-7 * radius);
    light += float3(.6, .78, 1) * coreFlash * 1.5;
#endif
    float vignette = 1 - .38 * smoothstep(.25, 2.5, length(screen));
    float3 color = 1 - exp(-light * vignette);
    return saturate(color);
}


float4 PS(const PSin IN) : SV_Target
{
    // AA integrates HEAD's shading without changing its distance field or depth.
    int samples = Quality.Antialiasing ? 2 : 1;
    float sampleWidth = 1.0 / samples;
    float2 uvX = ddx(IN.UV), uvY = ddy(IN.UV);
    float3 color = 0;
    [loop] for (int y = 0; y < samples; ++y)
        [loop] for (int x = 0; x < samples; ++x)
        {
            float2 offset = (float2(x, y) + .5) * sampleWidth - .5;
            color += Shade(IN.UV + uvX * offset.x + uvY * offset.y, sampleWidth);
        }
    color *= sampleWidth * sampleWidth;
    return float4(pow(saturate(color), 1 / 2.2), 1);
}
