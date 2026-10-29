// A finite-iteration 3D MandelPond distance estimate, evaluated on a plane.
// Integrated audio moves the slice and slowly explores a family of orbit rules.
float2 BoxDistanceAtDepth(float3 p, float t, float depth)
{
    // These parameters are uniform in space: no omitted spatial derivatives.
    // Musical time drives the normalized movement-speed multiplier.
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

float2 BoxDistanceAt(float3 p, float t)
{
    float depth = max(1, FractalDepth * (1 + round(Quality.MaximumIterations * 29)) / 10);
    return BoxDistanceAtDepth(p, t, depth);
}

float2 BoxDistance(float3 p)
{
    return BoxDistanceAt(p, MusicalMovementTime * Quality.MovementSpeed * 10);
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

float ShockEdgeAttenuation(float2 uv, float age)
{
    // Quiet old fronts progressively across the outer viewport, with the
    // strongest suppression in corners. This attenuates history illumination,
    // leaving the live onset and the other material layers independently lit.
    float edge = length(2 * uv - 1);
    float falloff = pow(1 - smoothstep(.45, 1.15, edge), 3);
    return lerp(1, falloff, smoothstep(.025, .16, age * HistorySeconds));
}