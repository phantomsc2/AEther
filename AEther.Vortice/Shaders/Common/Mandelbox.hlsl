#include "MandelboxField.hlsl"
float3 ShadeSliceAt(float2 uv, float sampleWidth, float movementTime, float3 reaction, float rayFold, bool anchoredView, float sourceRayFrequency, out float tonalRelief, out float rayFrequency)
{
    float2 screen = 2 * uv - 1;
    screen.x *= AspectRatio;
    screen.y = -screen.y;
    // Musical time traverses another dimension of the fixed 3D field.
    // All transforms follow the same inertial clock: audio builds momentum,
    // then the slice coasts smoothly to rest as that momentum relaxes.
    float viewTime = anchoredView ? .1 * movementTime : movementTime;
    float zoom = anchoredView ? 1.9 : 1.9 + .22 * sin(.73 * viewTime);
    // Cover the viewport with the interior of the box, including wide windows.
    // Cropping the slice avoids sampling empty space outside its outer bounds.
    // Rotate offsets around the screen-center sample, then translate the pivot.
    // Rotation/zoom have no displacement at screen == 0; depth drifts ten times
    // slower so the center remains an anchor while the surrounding field turns.
    float3 p = float3(screen * (zoom / max(1, AspectRatio)), 0);
    p = mul(Rz(.32 * viewTime + .18 * sin(1.3 * viewTime)), p);
    p = mul(Rx(.48 * sin(.83 * viewTime)), p);
    p = mul(Ry(.55 * sin(1.13 * viewTime)), p);
    p.z += (Geometry.SliceDepth * 4 - 2) + .85 * sin(.17 * viewTime) + .3 * sin(.31 * viewTime);
    if (anchoredView)
    {
        // Small, sparse deformations excite individual pockets. The camera
        // scale and orientation never respond to an onset or class envelope.
        float pocket = pow(.5 + .5 * sin(2.7 * p.x + .13 * movementTime)
            * sin(3.1 * p.y - .11 * movementTime), 4);
        float3 bend = sin(2.1 * p.yzx + float3(0, 2, 4));
        p += pocket * (.025 * reaction.x + .06 * reaction.y + .12 * reaction.z) * bend;
    }
    float radius = length(screen);
    float outer = smoothstep(.5, 2.4, radius);
    float2 distances = max(1e-8, BoxDistanceAt(p, movementTime));
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
    float angularFrequency = atan2(abs(screen.x), -screen.y) / 3.14159265;
    // Keep frequency angular so a tone remains a center-out ray. The folded
    // field bends that angular route without replacing it with distance slices.
    float tonalFrequency = saturate(angularFrequency + .18 * rayFold
        * sin(3.14159265 * angularFrequency) * (2 * foldCoordinate - 1)
        * smoothstep(.04, .65, radius));
    if (sourceRayFrequency >= 0)
    {
        // The object field supplies the roots; the viewport-filling background
        // supplies branching throughout the wake. This displaces the angular
        // ray coordinate rather than selecting static distance slices.
        float fineFold = smoothstep(-13, -5, log2(distances.x / distances.y));
        float backgroundBend = .2 * (2 * foldCoordinate - 1)
            + .12 * (2 * fineFold - 1) + .12 * (2 * frequency - 1);
        float2 edge = abs(2 * uv - 1);
        float anchor = 1 - smoothstep(.72, 1, max(edge.x, edge.y));
        float strength = (.45 + 1.4 * reaction.z) * (.6 + .4 * saturate(DistortionMomentum));
        float phase = sourceRayFrequency + anchor * strength
            * sin(3.14159265 * angularFrequency) * backgroundBend;
        // Reflect over bass/treble poles instead of clamping entire regions to
        // one frequency when a strong fold crosses an angular boundary.
        float wrapped = phase - 2 * floor(phase / 2);
        tonalFrequency = min(wrapped, 2 - wrapped);
    }
    rayFrequency = tonalFrequency;
    float footprint = max(fwidth(frequency) * sampleWidth, .028 * outer);
    float rayFootprint = sourceRayFrequency >= 0 ? min(.025, fwidth(tonalFrequency) * sampleWidth) : 0;
    float timeFootprint = min(.025, fwidth(age) * sampleWidth);
    float4 hull = 2 * History(frequency, age, right);
    float4 ray = 2 * History(tonalFrequency, age, right);
    [unroll] for (int tap = -1; tap <= 1; tap += 2)
    {
        float offset = tap / 2.0;
        hull += History(frequency + offset * footprint, age + offset * timeFootprint, right);
        ray += History(tonalFrequency + offset * rayFootprint, age + offset * timeFootprint, right);
    }
    hull *= .25;
    ray *= .25;
    float historyEdge = sourceRayFrequency >= 0 ? ShockEdgeAttenuation(uv, age) : 1;
    hull *= historyEdge;
    ray *= historyEdge;
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
    float attackFrequency = saturate(angularFrequency + .08 * sin(3.14159265 * angularFrequency)
        * (2 * foldCoordinate - 1) * smoothstep(.05, .3, radius));
    float attackAge = age * lerp(1.14, .88, angularFrequency);
    float attackWidth = max(.022 + .025 * dust, min(.06, fwidth(attackFrequency) * sampleWidth));
    float currentAttack = .5 * CurrentSpectrum(attackFrequency, right).g
        + .25 * (CurrentSpectrum(attackFrequency - attackWidth, right).g
                 + CurrentSpectrum(attackFrequency + attackWidth, right).g);
    float attackEnvelope = lerp(TransientHistory(attackFrequency, attackAge, right, attackWidth), currentAttack, center);
    float transientEdge = sourceRayFrequency >= 0 ? ShockEdgeAttenuation(uv, attackAge) : 1;
    attackEnvelope *= transientEdge;
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
    tonalRelief = lerp(.5, relief, saturate(.2 / reliefWidth));
    // Keep a continuous tone visible through fold valleys. Near-zero valley
    // gain turns the relief into apparent missing-history wedges and disks,
    // especially when shallow iteration depths expose broad, simple folds.
    // Retain shell contrast without using the field as an extinction mask.
    float sustain = 1.9 * max(0, ray.r) * trail * lerp(.65, 1.25, tonalRelief * tonalRelief);
    // Where a tone's angular route meets its matching distance-field shell,
    // illuminate that shell as well. Angular support keeps mids at the sides.
    float tonalSupport = max(0, ray.r) / (.04 + max(0, ray.r));
    float sustainedHull = 2.2 * max(0, hull.r) * tonalSupport * trail;
    sustainedHull *= 1 - rayFold;
    float3 sustainedHullPalette = .5 + .5 * cos(6.2831853 * (frequency * .9 + palettePhase + float3(.04, .38, .62)));
    // Attacks are crisp near the core, then widen and disperse into a faint
    // coarse glow near the old-history horizon.
    float attack = 6.2 * max(0, attackEnvelope) * trail * 1.2;
    float dispersed = TransientHistory(attackFrequency, attackAge, right, attackWidth + .07 * dust) * transientEdge;
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


float3 ShadeSlice(float2 uv, float sampleWidth, out float tonalRelief)
{
    float rayFrequency;
    return ShadeSliceAt(uv, sampleWidth, MusicalMovementTime * Quality.MovementSpeed * 10, 0, 0, false, -1, tonalRelief, rayFrequency);
}

#ifndef MANDELBOX_CUSTOM_PS
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
            float relief;
            color += ShadeSlice(IN.UV + uvX * offset.x + uvY * offset.y, sampleWidth, relief);
        }
    color *= sampleWidth * sampleWidth;
    return float4(pow(saturate(color), 1 / 2.2), 1);
}

#endif
