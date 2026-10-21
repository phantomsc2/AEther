// All positions use the solver's viewport-height coordinates (positive Y down).
// The display and simulation share this projection, boundary and emitter.
float CometTime() { return S * Quality.MovementSpeed * 10; }
float CometShapeTime()
{
    return ShapeTime * Quality.MovementSpeed * 20;
}
float CometNoiseResponse(float amplitude)
{
    // Suppress low detector leakage, then span a wide distortion range rather
    // than reaching full strength from a faint noise floor.
    return pow(saturate(((1 - exp(-5 * max(0, amplitude))) - .04) / .96), 1.5);
}
float CometNoiseDrive(float frequency, float age, float right)
{
    float2 uv = float2(saturate(frequency), .5);
    float live = lerp(Spectrum0.SampleLevel(Linear, uv, 0).b,
        Spectrum1.SampleLevel(Linear, uv, 0).b, right);
    // Follow the material's recorded age, averaging an additional short tail
    // so local distortion has inertia as noisy bands enter and leave it.
    float recorded = .25 * max(0, History(frequency, age, right).b)
        + .35 * max(0, History(frequency, age + .12 / HistorySeconds, right).b)
        + .2 * max(0, History(frequency, age + .4 / HistorySeconds, right).b);
    float localNoise = CometNoiseResponse(.2 * max(0, live) + recorded);
    return saturate(.7 * localNoise + .3 * DistortionMomentum);
}
float3 CometReaction()
{
    return 1 - exp(-float3(3 * max(0, PoseX), 4 * max(0, VisualFlash), 3 * max(0, PoseZ)));
}
float2 CometHead()
{
    float t = .12 * CometShapeTime();
    // Follow from within the wake. Modest lateral lag keeps the head visible
    // ahead while the camera stays inside its expanding cone of gas.
    return float2(min(1, AspectRatio) * (.1 + .06 * sin(.73 * t)),
        -.06 + .045 * sin(.61 * t + .6));
}
float CometScale() { return 12 / ((.7 + .8 * Comet.Size) * min(1, AspectRatio)); }
float CometBoundary()
{
    return .003 + .003 * saturate(PoseX);
}
float CometSpectralImpact(float frequency, float right)
{
    float2 uv = float2(saturate(frequency), .5);
    float attack = lerp(Spectrum0.SampleLevel(Linear, uv, 0).g,
        Spectrum1.SampleLevel(Linear, uv, 0).g, right);
    // A short spectral release retains each band's own location on the edge.
    attack = max(attack, .55 * History(frequency, .025 / HistorySeconds, right).g);
    attack = max(attack, .25 * History(frequency, .05 / HistorySeconds, right).g);
    return pow(1 - exp(-4 * max(0, attack)), 2);
}
float2 CometDistances(float2 p)
{
    float t = CometShapeTime();
    float3 reaction = CometReaction();
    float2 offset = p - CometHead();
    float local = 1 - smoothstep(.65, 1.2, length(offset) / min(1, AspectRatio));
    float3 q = float3(offset * CometScale(), 0);
    q = mul(Rz(.52 * t + .22 * sin(1.7 * t)), q);
    q = mul(Rx(.65 * sin(.91 * t)), q);
    q = mul(Ry(.7 * cos(.73 * t)), q);
    q.z += Geometry.SliceDepth * 4 - 2 + Comet.Morph
        * (.9 * sin(1.1 * t) + .4 * sin(1.93 * t));
    q.z += .12 * reaction.x + .1 * reaction.z;
    float depth = max(1, (1 + round(Quality.MaximumIterations * 29))
        * lerp(.3, 1, saturate(PoseX + PoseZ)));
    float2 distances = max(1e-8, BoxDistanceAtDepth(q, t, depth));
    // Outside the local deformation region, skip spectral/history sampling.
    [branch] if (local <= 0) return distances;
    float radius = length(offset);
    float angle = atan2(abs(offset.x), offset.y) / 3.14159265;
    float hull = saturate((log2(distances.x) + 17) / 17);
    float frequency = lerp(angle, hull, .35);
    float right = .5 + .5 * offset.x / max(.001, radius);
    float impact = CometSpectralImpact(frequency, right) * local;
    float noisyEdge = CometNoiseDrive(frequency, 0, right) * local;
    float deformation = .16 * impact + .12 * noisyEdge;
    if (deformation <= 1e-5) return distances;
    // Small spectral bulges and indentations ride on the tumbling slice.
    // They displace local coordinates, never its orbit time or overall scale.
    float pockets = .35 + .65 * sin(3.1 * q.x) * cos(2.7 * q.y);
    q.xy /= 1 + deformation * pockets;
    return max(1e-8, BoxDistanceAtDepth(q, t, depth));
}
float CometRayEscape(float2 offset)
{
    return smoothstep(.55, 2.25, length(offset) / min(1, AspectRatio));
}
float CometRayAttachment(float2 offset, float2 distances, float noiseDrive)
{
    return (1 - CometRayEscape(offset))
        * exp(-lerp(12, 2, noiseDrive) * max(0, distances.x - CometBoundary()));
}
float CometRayFrequency(float2 offset, float2 distances, out float noiseDrive)
{
    float escape = CometRayEscape(offset);
    float headAngle = atan2(abs(offset.x), offset.y) / 3.14159265;
    float2 screen = offset + CometHead();
    float screenAngle = atan2(abs(screen.x), screen.y) / 3.14159265;
    float angle = lerp(headAngle, screenAngle, escape);
    float right = .5 + .5 * offset.x / max(.001, length(offset));
    float age = log(1 + 35 * max(0, distances.x - CometBoundary())
        / max(1, length(float2(AspectRatio, 1)) * .65)) / 6;
    noiseDrive = CometNoiseDrive(angle, age, right);
    float fold = smoothstep(-7, -.3, log2(distances.x / distances.y));
    float hull = saturate((log2(distances.x) + 17) / 17);
    // Close to the head, ray paths cling to the actual cutout's folds. Their
    // angular distortion vanishes outside the near field, leaving each tone
    // anchored to its frequency-coded screen angle as the lightning escapes.
    float fineFold = smoothstep(-13, -5, log2(distances.x / distances.y));
    float bend = .58 * (2 * fold - 1) + .16 * (2 * fineFold - 1) + .14 * (2 * hull - 1);
    float attachment = CometRayAttachment(offset, distances, noiseDrive);
    float distortion = .25 + 1.9 * noiseDrive;
    return saturate(angle + distortion * attachment * sin(3.14159265 * angle) * bend);
}
float3 CometPalette(float frequency, float phase)
{
    return .5 + .5 * cos(6.2831853 * (frequency * .85 + phase + float3(.04, .38, .62)));
}

#ifdef MANDEL_COMET
float4 CometSource(float2 p, float sourceAge)
{
    float2 q = p - CometHead();
    float frequency = atan2(abs(q.x), q.y) / 3.14159265;
    float age = max(sourceAge, HistoryNewestAge);
    uint bands, rows; Histogram0.GetDimensions(bands, rows);
    if (HistoryNewestAge > sourceAge + 2.0 / rows || age > HistoryAvailable || HistoryAvailable <= 0) return 0;
    float2 uv = float2(frequency, HistogramShift - age);
    float stereo = .5 + .5 * q.x / max(.001, length(q));
    return max(0, lerp(Histogram0.SampleLevel(Linear, uv, 0), Histogram1.SampleLevel(Linear, uv, 0), stereo));
}
float CometActivity() { return smoothstep(.005, .5, PoseX + PoseZ + .6 * PoseY); }
float2 CometDirection()
{
    float t = CometTime();
    return normalize(float2(-1, .23 + .18 * sin(.43 * t)));
}
float2 CometCarrier(float2 p)
{
    // Expansion is kept outside pressure projection and carries the wake past
    // every screen edge. Audio never kicks the whole image's travel speed.
    return CometActivity() * Fluid.Flow * 3 * (1.25 * (p - CometHead()) + .14 * CometDirection());
}
float CometDilution(float2 p) { return CometActivity() * Fluid.Flow * 7.5; }
float2 CometTurbulence(float2 p, float4 source)
{
    float2 q = p - CometHead(), direction = CometDirection();
    float2 side = float2(-direction.y, direction.x);
    float along = dot(q, direction), across = dot(q, side), t = CometTime();
    float eddies = sin(13 * across + 3 * sin(4 * along - 1.7 * t))
        + .5 * sin(23 * along + 9 * across + 2.3 * t);
    float strength = .2 * (1 - exp(-2 * source.r))
        + 7 * Fluid.Background * CometNoiseResponse(source.b) * (.35 + 1.1 * DistortionMomentum)
        + 4 * (1 - exp(-2 * source.g));
    return (side * eddies + .3 * direction * sin(17 * across - t))
        * strength * exp(-1.2 * across * across - .35 * along * along) * CometActivity();
}
float4 CometPigment(float2 p, float4 source)
{
    float2 distances = CometDistances(p);
    float surface = distances.x - CometBoundary();
    float band = max(.0015, 1.5 * fwidth(surface));
    float emitter = exp(-abs(surface) / band);
    float density = emitter * ((1 - exp(-2 * source.r)) + .6 * (1 - exp(-2 * source.b))
        + .2 * (1 - exp(-2 * source.g)));
    float frequency = saturate((log2(distances.x) + 17) / 17);
    return float4(CometPalette(frequency, -.05 * CometTime()) * density, density);
}
#endif
