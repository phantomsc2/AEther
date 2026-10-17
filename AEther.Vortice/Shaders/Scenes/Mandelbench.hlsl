#include "../Common/Inputs.hlsl"

// Deliberately independent of audio and time: an interactive IFS workbench.
struct IterationSettings
{
    float Scale;
    float Iterations;
    float Step;
    bool BoxFold;
    bool SphereFold;
    float MinimumRadius;
    bool Juliabox;
    float3 JuliaConstant;
};
struct ViewSettings
{
    bool Compare;
    float Zoom;
    float2 Pan;
    float SliceDepth;
    float2 Rotation;
    float GridDensity;
    float CoordinateColor;
    bool DistanceContours;
    float DistanceLevel;
    bool CoordinateGrid;
    float BandSpacing;
    float LineWidth;
    bool Antialiasing;
};
struct JacobianSettings
{
    bool Enabled;
    bool MatrixEntries;
    bool BoundRatio;
    float LogRange;
};
struct SearchSettings
{
    float EscapeRadius;
    bool ShowFailures;
};
cbuffer Parameters
{
    IterationSettings IFS = { .3125, .33333333, 1, true, true, .47368421, false, float3(.675, .55, .5) };
    ViewSettings View = { false, .5, float2(.5, .5), .5, float2(.5, .5), .33333333, .65, false, .66666667, false, .625, .35, false };
    JacobianSettings Jacobian = { false, false, false, .5 };
    SearchSettings Search = { .2, false };
};

Texture2D<float4> DistanceSeeds;
Texture2D<float2> DistanceField;
SamplerState DistanceSampler;
cbuffer DistanceConstants { float4 DistanceDimensions; };

static const float3x3 Identity = float3x3(1,0,0, 0,1,0, 0,0,1);

float3 Orient(float3 p)
{
    return mul(Ry((View.Rotation.x - .5) * 6.2831853),
        mul(Rx((View.Rotation.y - .5) * 6.2831853), p));
}

// z starts at the sampled point in both modes. Mandelbox adds that point
// each iteration; Juliabox adds the same user-selected c everywhere.
// Step exposes the NEXT iteration: box fold, sphere fold, scale + translate.
float3 OrbitTrace(float3 p, out float derivative, out float3x3 jacobian,
    out float boundary, out float3 boundaryGradient)
{
    float3 z = p;
    float3 c = IFS.Juliabox ? 4 * IFS.JuliaConstant - 2 : p;
    float scale = 8 * IFS.Scale - 4;
    float minimumRadius = .05 + .95 * IFS.MinimumRadius;
    int completed = (int)round(12 * IFS.Iterations);
    derivative = 1;
    jacobian = Identity;
    float radius = length(p);
    float maximumRadius = radius;
    float activeBound = 1;
    float3 activeGradient = radius > 1e-20 ? p / radius : 0;
    [loop] for (int i = 0; i < 13; ++i)
    {
        if (i > completed) break;
        float stage = i < completed ? 3 : 3 * IFS.Step;
        float boxBlend = IFS.BoxFold ? saturate(stage) : 0;
        // Rows are output components; columns are input coordinates.
        // At a crease use the interior branch (the derivative is undefined).
        float3 boxSlope = 1 - 2 * boxBlend * float3(
            abs(z.x) > 1, abs(z.y) > 1, abs(z.z) > 1);
        jacobian[0] *= boxSlope.x;
        jacobian[1] *= boxSlope.y;
        jacobian[2] *= boxSlope.z;
        z = lerp(z, 2 * clamp(z, -1, 1) - z, boxBlend);
        float r2 = dot(z, z);
        float factor = 1 / clamp(r2, minimumRadius * minimumRadius, 1);
        float sphereBlend = IFS.SphereFold ? saturate(stage - 1) : 0;
        float dilation = lerp(1, factor, sphereBlend);
        float3x3 sphereDerivative = dilation * Identity;
        if (r2 > minimumRadius * minimumRadius && r2 < 1)
        {
            float3x3 outerProduct = float3x3(z.x * z, z.y * z, z.z * z);
            sphereDerivative -= (2 * sphereBlend * factor / r2) * outerProduct;
        }
        jacobian = mul(sphereDerivative, jacobian);
        z *= dilation;
        derivative *= dilation;
        float affineBlend = saturate(stage - 2);
        jacobian = lerp(1, scale, affineBlend) * jacobian
            + (IFS.Juliabox ? 0 : affineBlend) * Identity;
        z = lerp(z, scale * z + c, affineBlend);
        // Triangle inequality also bounds partial folds and partial affine steps.
        derivative = lerp(1, abs(scale), affineBlend) * derivative
            + (IFS.Juliabox ? 0 : affineBlend);
        radius = length(z);
        if (radius > maximumRadius)
        {
            maximumRadius = radius;
            activeBound = max(derivative, 1e-20);
            activeGradient = mul(transpose(jacobian / activeBound), z / max(radius, 1e-20));
        }
        // Prevent overflow for escaping orbits, preserving the escape direction.
        if (dot(z, z) > 1e12) break;
    }
    // Finite survivor set: ALL visited orbit positions must stay in the
    // escape ball. The zero set is a boundary, not a collection of orbit roots.
    // Divide f and its derivative by the same frozen local scale for stability.
    boundary = (maximumRadius - (2 + 30 * Search.EscapeRadius)) / activeBound;
    boundaryGradient = activeGradient;
    return z;
}

float3 OrbitJacobian(float3 p, out float derivative, out float3x3 jacobian)
{
    float unused;
    float3 unusedGradient;
    return OrbitTrace(p, derivative, jacobian, unused, unusedGradient);
}

float3 Orbit(float3 p, out float derivative)
{
    float3x3 unused;
    return OrbitJacobian(p, derivative, unused);
}

// Largest singular value: solve the symmetric 3x3 eigenproblem for J^T J.
// Normalize first to avoid squaring large entries. Unlike power iteration,
// this does not depend on a starting direction or a convergence threshold.
float LargestStretch(float3x3 jacobian)
{
    float magnitude = max(max(max(abs(jacobian[0].x), abs(jacobian[0].y)), abs(jacobian[0].z)),
        max(max(max(abs(jacobian[1].x), abs(jacobian[1].y)), abs(jacobian[1].z)),
            max(max(abs(jacobian[2].x), abs(jacobian[2].y)), abs(jacobian[2].z))));
    if (magnitude < 1e-30) return 0;
    float3x3 normalized = jacobian / magnitude;
    float3x3 gram = mul(transpose(normalized), normalized);
    float mean = (gram[0][0] + gram[1][1] + gram[2][2]) / 3;
    float3x3 centered = gram - mean * Identity;
    float spread = sqrt((dot(centered[0], centered[0])
        + dot(centered[1], centered[1]) + dot(centered[2], centered[2])) / 6);
    if (spread < 1e-7) return magnitude * sqrt(max(0, mean));
    float angle = acos(clamp(determinant(centered / spread) * .5, -1, 1)) / 3;
    return magnitude * sqrt(max(0, mean + 2 * spread * cos(angle)));
}

float3 StretchColor(float stretch)
{
    float range = 1 + 15 * Jacobian.LogRange;
    float value = saturate(.5 + log2(max(stretch, 1e-20)) / (2 * range));
    return value < .5 ? lerp(float3(.02, .04, .25), float3(.12, .65, .6), 2 * value)
        : lerp(float3(.12, .65, .6), float3(1, .3, .04), 2 * value - 1);
}

float GridLine(float coordinate)
{
    float width = max(fwidth(coordinate), 1e-5);
    float distance = abs(frac(coordinate + .5) - .5);
    // Fade to the integrated line coverage when individual cells are subpixel.
    float coverage = 1 - smoothstep(.65 * width, 1.65 * width, distance);
    return lerp(coverage, .16, smoothstep(.2, 1, width));
}

float3 Grid(float3 p)
{
    float density = exp2(6 * View.GridDensity - 1);
    float3 q = p * density;
    float2 width = max(fwidth(q.xy), 1e-5);
    float2 wave = sin(3.14159265 * q.xy);
    float checker = .5 + .5 * wave.x * wave.y
        * saturate(1 - width.x) * saturate(1 - width.y);
    // R and G encode the folded x/y coordinates; blue encodes slice z.
    float3 coordinates = .5 + .5 * p / (1 + abs(p));
    float3 color = lerp(float3(.08, .11, .16), coordinates, View.CoordinateColor)
        * lerp(.45, .85, checker);
    color = lerp(color, float3(.1, .8, 1), .85 * GridLine(q.x));
    color = lerp(color, float3(1, .46, .16), .85 * GridLine(q.y));
    // The two colored zero axes retain their identity through all folds.
    float2 axis = 1 - smoothstep(width, 2 * width, abs(q.xy));
    color = lerp(color, float3(.3, 1, .6), axis.x);
    return lerp(color, float3(1, .3, .5), axis.y);
}

float Boundary(float3 p, out float3 gradient)
{
    float bound, field;
    float3x3 jacobian;
    OrbitTrace(p, bound, jacobian, field, gradient);
    return field;
}

float2 DomainExtent()
{
    float zoom = 2 * exp2(6 * (View.Zoom - .5));
    return 1.5 * zoom * float2(AspectRatio * (View.Compare ? .5 : 1), 1);
}

float3 DomainPoint(float2 uv)
{
    return Orient(float3((2 * uv - 1) * DomainExtent(), 0))
        + float3(8 * (View.Pan - .5), 4 * View.SliceDepth - 2);
}

float DomainBoundary(float2 uv)
{
    float3 unused;
    return Boundary(DomainPoint(uv), unused);
}

// First sample the actual inside/outside geometry. Refine edge crossings rather
// than normalizing orbit radii into an alleged distance field.
float4 DistanceSeedPS(PSin input) : SV_Target
{
    float2 uv = input.UV;
    float field = DomainBoundary(uv);
    float best = 1e30;
    float4 result = 0;
    [loop] for (int neighbor = 0; neighbor < 4; ++neighbor)
    {
        float2 direction = neighbor < 2 ? float2(1,0) : float2(0,1);
        direction *= (neighbor & 1) == 0 ? 1 : -1;
        float2 other = uv + direction / DistanceDimensions.xy;
        float otherField = DomainBoundary(other);
        if ((field < 0) == (otherField < 0)) continue;
        float2 a = uv, b = other;
        [loop] for (int refine = 0; refine < 9; ++refine)
        {
            float2 middle = .5 * (a + b);
            if ((DomainBoundary(middle) < 0) == (field < 0)) a = middle;
            else b = middle;
        }
        float2 crossing = .5 * (a + b);
        float2 delta = (crossing - uv) * DomainExtent();
        float squared = dot(delta, delta);
        if (squared < best) { best = squared; result = float4(crossing, 1, 0); }
    }
    return result;
}

float4 DistanceJumpPS(PSin input) : SV_Target
{
    int2 pixel = (int2)input.Position.xy;
    int jump = (int)DistanceDimensions.z;
    float4 result = 0;
    float best = 1e30;
    [unroll] for (int y = -1; y <= 1; ++y)
        [unroll] for (int x = -1; x <= 1; ++x)
        {
            int2 position = pixel + int2(x,y) * jump;
            if (any(position < 0) || any(position >= (int2)DistanceDimensions.xy)) continue;
            float4 seed = DistanceSeeds.Load(int3(position, 0));
            if (seed.z < .5) continue;
            float2 delta = (seed.xy - input.UV) * DomainExtent();
            float squared = dot(delta, delta);
            if (squared < best) { best = squared; result = seed; }
        }
    return result;
}

float4 DistanceResolvePS(PSin input) : SV_Target
{
    float4 seed = DistanceSeeds.Load(int3((int2)input.Position.xy, 0));
    float radius = length((seed.xy - input.UV) * 2 * DomainExtent());
    float distance = (DomainBoundary(input.UV) < 0 ? -1 : 1) * radius;
    return float4(distance, seed.z, 0, 1);
}

float PulseIntegral(float x, float duty)
{
    return floor(x) * duty + min(frac(x), duty);
}

float3 DistanceContour(float distance, float pixelSize, bool valid)
{
    float spacing = exp2(-8 + 8 * View.BandSpacing);
    float halfLine = (.35 + 1.3 * View.LineWidth) * pixelSize * .5;
    // Geometric pixel footprint, independent of solver jumps. fwidth(distance)
    // would turn failed projections and branch changes into persistent dust.
    float halfFilter = pixelSize * .5;
    float radius = abs(distance);
    float coverage;
    if (View.DistanceContours)
    {
        float level = exp2(-16 + 18 * View.DistanceLevel);
        float overlap = max(0, min(radius + halfFilter, level + halfLine)
            - max(radius - halfFilter, level - halfLine));
        coverage = saturate(overlap / (2 * halfFilter));
    }
    else
    {
        float duty = min(.45, 2 * halfLine / spacing);
        float footprint = max(1e-5, 2 * halfFilter / spacing);
        float phase = radius / spacing + .5 * duty;
        coverage = saturate((PulseIntegral(phase + .5 * footprint, duty)
            - PulseIntegral(phase - .5 * footprint, duty)) / footprint);
    }
    float3 background = distance < 0 ? float3(.018, .055, .075) : float3(.055, .027, .018);
    float3 ink = distance < 0 ? float3(.2, .8, 1) : float3(1, .75, .24);
    if (!valid) return Search.ShowFailures ? float3(.6, .04, .3) : background;
    float edge = View.DistanceContours ? 0 : 1 - smoothstep(halfLine, halfLine + pixelSize, radius);
    return lerp(lerp(background, ink, coverage), float3(.85, .94, 1), edge);
}

float3 ShadeAt(float2 inputUV, float pixelSize)
{
    float2 uv = inputUV;
    bool matrixView = Jacobian.Enabled && Jacobian.MatrixEntries;
    bool ratioView = Jacobian.Enabled && !matrixView && Jacobian.BoundRatio;
    bool compare = View.Compare && !matrixView && !ratioView;
    bool original = compare && uv.x < .5;
    int2 tile = (int2)min(floor(uv * 3), 2);
    if (matrixView) uv = frac(3 * uv);
    else if (compare) uv.x = frac(2 * uv.x);
    float aspect = AspectRatio * (compare ? .5 : 1);
    float2 screen = (2 * uv - 1) * float2(aspect, -1);
    float zoom = 2 * exp2(6 * (View.Zoom - .5));
    float3 p = Orient(float3(screen * zoom, 0))
        + float3(8 * (View.Pan - .5), 4 * View.SliceDepth - 2);
    float derivative;
    float3x3 jacobian;
    float3 folded = OrbitJacobian(p, derivative, jacobian);
    // Pull back a Cartesian grid through F^n: curved contours are the points
    // whose transformed x or y lands on an integer grid coordinate.
    float3 color = View.CoordinateGrid ? Grid(original ? p : folded) : 0;
    float stretch = Jacobian.Enabled ? LargestStretch(jacobian) : derivative;
    if (Jacobian.Enabled)
    {
        // Compare the SAME orbit on both sides, rather than identity vs orbit.
        color = StretchColor(original ? derivative : stretch);
        if (ratioView)
        {
            float excess = max(0, log2(max(derivative, 1e-20) / max(stretch, 1e-20)));
            float amount = saturate(excess / (1 + 15 * Jacobian.LogRange));
            color = lerp(float3(.04, .2, .3), float3(1, .25, .03), amount);
        }
        if (matrixView)
        {
            float entry = jacobian[tile.y][tile.x];
            float amount = saturate(log2(1 + abs(entry)) / (1 + 15 * Jacobian.LogRange));
            color = float3(amount, amount, amount);
        }
    }
    if ((!Jacobian.Enabled && !View.CoordinateGrid || View.DistanceContours) && !matrixView && !ratioView)
    {
        bool valid = true;
        float distance;
        if (original)
        {
            // Compare a local scalar estimate with distance to the sampled
            // boundary of the same finite survivor set on the viewing plane.
            float3 gradient;
            distance = Boundary(p, gradient);
        }
        else
        {
            // Bilinear interpolation of a shared scalar field is continuous.
            // Never interpolate closest-point positions across Voronoi cells.
            float2 sample = DistanceField.SampleLevel(DistanceSampler, .5 + (uv - .5) / 1.5, 0);
            distance = sample.x;
            valid = sample.y > .99;
        }
        color = DistanceContour(distance, pixelSize, valid);
    }
    if (compare && abs(inputUV.x - .5) * Resolution.x < 1)
        color = float3(.7, .78, .9);
    if (matrixView && (min(uv.x, 1 - uv.x) * Resolution.x / 3 < 1
        || min(uv.y, 1 - uv.y) * Resolution.y / 3 < 1)) color = .6;
    return saturate(color);
}

float4 PS(PSin input) : SV_Target
{
    int samples = View.Antialiasing ? 2 : 1;
    float zoom = 2 * exp2(6 * (View.Zoom - .5));
    float pixelSize = 2 * zoom / max(1, Resolution.y) / samples;
    float3 color = 0;
    [loop] for (int y = 0; y < samples; ++y)
        [loop] for (int x = 0; x < samples; ++x)
        {
            float2 offset = ((float2(x, y) + .5) / samples - .5) / Resolution;
            color += ShadeAt(input.UV + offset, pixelSize);
        }
    return float4(color / (samples * samples), 1);
}
