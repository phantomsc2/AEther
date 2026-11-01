#ifndef AETHER_FLUID_SIMULATION
#define AETHER_FLUID_SIMULATION
// Shared velocity/multigrid passes. Coordinates and
// velocities are in viewport-height units, independent of the grid resolution.
cbuffer FluidConstants { float2 GridSize, CellSize; float StepSeconds, SourceAge, VC1Strength, VC2Strength; float CatchupSeconds; float3 UnusedForcing; };
Texture2D<float2> Velocity;
Texture2D<float4> Advected;
int2 WrapCell(int2 p) { int2 size = int2(GridSize); return (p % size + size) % size; }
Texture2D<float> Pressure;
Texture2D<float> Rhs;
Texture2D<float> Correction;
SamplerState ClampFluid;
SamplerState PressureSampler;

float2 World(float2 uv) { return (2 * uv - 1) * float2(AspectRatio, 1); }
float2 ToUV(float2 p) { return .5 + .5 * p / float2(AspectRatio, 1); }
float2 ReadVelocity(int2 p)
{
    return Velocity.Load(int3(Fluid.PeriodicBoundary ? WrapCell(p) : clamp(p, 0, int2(GridSize) - 1), 0));
}
float ReadPressure(int2 p)
{
    // Neumann pressure boundary, paired with zero normal turbulent velocity.
    // Scene velocity constraints are applied after pressure projection.
    return Pressure.Load(int3(Fluid.PeriodicBoundary ? WrapCell(p) : clamp(p, 0, int2(GridSize) - 1), 0));
}
float Laplacian(int2 p)
{
    float2 coefficient = 1 / (CellSize * CellSize);
    return coefficient.x * (ReadPressure(p + int2(1, 0)) + ReadPressure(p - int2(1, 0)))
        + coefficient.y * (ReadPressure(p + int2(0, 1)) + ReadPressure(p - int2(0, 1)))
        - 2 * (coefficient.x + coefficient.y) * ReadPressure(p);
}
float2 SampleTransportVelocity(float2 uv)
{
    // Pressure is cell centered; x/y velocities live on the right/bottom
    // faces. Reconstruct both components at the same physical point.
    return float2(Velocity.SampleLevel(ClampFluid, uv - float2(.5 / GridSize.x, 0), 0).x,
        Velocity.SampleLevel(ClampFluid, uv - float2(0, .5 / GridSize.y), 0).y);
}
float2 Transport(float2 p)
{
    return SampleTransportVelocity(ToUV(p));
}
float2 Trace(float2 uv, float dt)
{
    float2 p = World(uv);
    float2 midpoint = p - .5 * dt * Transport(p);
    return ToUV(p - dt * Transport(midpoint));
}
float2 Backtrace(float2 uv) { return Trace(uv, StepSeconds); }

// MacCormack correction uses the predicted field transported backward in
// time. Clamp to the four original donors: no new extrema.
float4 DonorBounds(float2 uv, bool upper)
{
    int2 p = int2(floor(uv * GridSize - .5));
    float4 bound = upper ? -1e30 : 1e30;
    [unroll] for (int y = 0; y < 2; ++y)
        [unroll] for (int x = 0; x < 2; ++x)
        {
            int2 q = p + int2(x, y);
            float4 v = 0;
            if (Fluid.PeriodicBoundary)
                v = float4(Velocity.Load(int3(WrapCell(q), 0)), 0, 0);
            else if (all(q >= 0) && all(q < int2(GridSize)))
                v = float4(Velocity.Load(int3(q, 0)), 0, 0);
            bound = upper ? max(bound, v) : min(bound, v);
        }
    return bound;
}
float2 PredictVelocity(PSin input) : SV_Target
{
    float2 x = float2(.5 / GridSize.x, 0), y = float2(0, .5 / GridSize.y);
    return float2(Velocity.SampleLevel(ClampFluid, Backtrace(input.UV + x) - x, 0).x,
        Velocity.SampleLevel(ClampFluid, Backtrace(input.UV + y) - y, 0).y);
}
float2 AdvectVelocity(PSin input) : SV_Target
{
    int2 q = int2(input.Position.xy);
    float2 x = float2(.5 / GridSize.x, 0), y = float2(0, .5 / GridSize.y);
    float2 backX = Backtrace(input.UV + x) - x, backY = Backtrace(input.UV + y) - y;
    float2 reverse = float2(Advected.SampleLevel(ClampFluid, Trace(input.UV + x, -StepSeconds) - x, 0).x,
        Advected.SampleLevel(ClampFluid, Trace(input.UV + y, -StepSeconds) - y, 0).y);
    float2 corrected = Advected.Load(int3(int2(input.Position.xy), 0)).xy
        + .5 * (Velocity.Load(int3(int2(input.Position.xy), 0)) - reverse);
    float2 lower = float2(DonorBounds(backX, false).x, DonorBounds(backY, false).y);
    float2 upper = float2(DonorBounds(backX, true).x, DonorBounds(backY, true).y);
    float2 v = clamp(corrected, lower, upper);
    if (!Fluid.PeriodicBoundary)
    {
        if (q.x == GridSize.x - 1) v.x = 0;
        if (q.y == GridSize.y - 1) v.y = 0;
    }
    return v;
}

// The heat equation's Gaussian kernel diffuses velocity without an explicit
// timestep stability cap. Two separable passes approximate its 2D convolution.
// Pressure projection and the full-strength inlet follow both passes.
float2 DiffuseVelocityAxis(float2 uv, float2 axis)
{
    float viscosity = .1 * pow(Fluid.Viscosity, 3);
    float sigma = sqrt(2 * viscosity * StepSeconds);
    float2 sum = 0;
    float weight = 0;
    [unroll] for (int i = -8; i <= 8; ++i)
    {
        float x = .5 * i;
        float w = exp(-.5 * x * x);
        float2 offset = axis * (x * sigma) / float2(2 * AspectRatio, 2);
        sum += w * Velocity.SampleLevel(PressureSampler, uv + offset, 0);
        weight += w;
    }
    return sum / weight;
}
float2 DiffuseVelocityX(PSin input) : SV_Target
{
    return DiffuseVelocityAxis(input.UV, float2(1, 0));
}
float2 DiffuseVelocityY(PSin input) : SV_Target
{
    return DiffuseVelocityAxis(input.UV, float2(0, 1));
}

// Corner-centered curl of face-centered velocity. A wall corner has no
// confinement flux, keeping normal velocity zero at the domain boundary.
float Vorticity(PSin input) : SV_Target
{
    int2 p = int2(input.Position.xy);
    if (!Fluid.PeriodicBoundary && (p.x == GridSize.x - 1 || p.y == GridSize.y - 1)) return 0;
    float2 v = ReadVelocity(p);
    return (ReadVelocity(p + int2(1, 0)).y - v.y) / CellSize.x
        - (ReadVelocity(p + int2(0, 1)).x - v.x) / CellSize.y;
}
float ReadCurl(int2 p)
{
    if (Fluid.PeriodicBoundary) return Rhs.Load(int3(WrapCell(p), 0));
    if (any(p < 0) || any(p >= int2(GridSize))) return 0;
    return Rhs.Load(int3(p, 0));
}
float2 VC1Force(int2 p)
{
    float w = ReadCurl(p);
    float nw = abs(ReadCurl(p + int2(-1, -1))), north = abs(ReadCurl(p + int2(0, -1))), ne = abs(ReadCurl(p + int2(1, -1)));
    float west = abs(ReadCurl(p + int2(-1, 0))), east = abs(ReadCurl(p + int2(1, 0)));
    float sw = abs(ReadCurl(p + int2(-1, 1))), south = abs(ReadCurl(p + int2(0, 1))), se = abs(ReadCurl(p + int2(1, 1)));
    // Scharr derivatives average across the transverse direction to reduce
    // grid-axis bias; retain physical derivatives for rectangular cells.
    float2 g = float2(3 * (ne - nw) + 10 * (east - west) + 3 * (se - sw),
                      3 * (sw - nw) + 10 * (south - north) + 3 * (se - ne)) / (32 * CellSize);
    // Fade weak gradients instead of normalizing grid noise to unit strength.
    // A local curl scale keeps this threshold independent of flow amplitude;
    // division by cell size expresses it in the same units as the gradient.
    float scale = (abs(w) + nw + north + ne + west + east + sw + south + se) / 9;
    float epsilon = max(.05 * scale / min(CellSize.x, CellSize.y), 1e-12);
    float2 n = g / sqrt(dot(g, g) + epsilon * epsilon);
    return (2 * VC1Strength) * min(CellSize.x, CellSize.y) * w * float2(n.y, -n.x);
}
float2 VC1(PSin input) : SV_Target
{
    int2 p = int2(input.Position.xy);
    float2 f = VC1Force(p);
    float2 a = float2(.5 * (f.x + VC1Force(p - int2(0, 1)).x),
                      .5 * (f.y + VC1Force(p - int2(1, 0)).y));
    float2 v = ReadVelocity(p) + StepSeconds * a;
    if (!Fluid.PeriodicBoundary)
    {
        if (p.x == GridSize.x - 1) v.x = 0;
        if (p.y == GridSize.y - 1) v.y = 0;
    }
    return v;
}
float VC2Potential(PSin input) : SV_Target
{
    int2 p = int2(input.Position.xy);
    float w = ReadCurl(p);
    float4 a = abs(float4(ReadCurl(p + int2(1, 0)), ReadCurl(p - int2(1, 0)),
                          ReadCurl(p + int2(0, 1)), ReadCurl(p - int2(0, 1))));
    float4 d = abs(float4(ReadCurl(p + int2(-1, -1)), ReadCurl(p + int2(1, -1)),
                          ReadCurl(p + int2(-1, 1)), ReadCurl(p + int2(1, 1))));
    float h = min(CellSize.x, CellSize.y);
    // Include diagonal curl with inverse physical-distance weights. Normalize
    // the weights so a constant neighborhood retains its curl magnitude.
    float2 axisWeight = h / CellSize;
    float diagonalWeight = h / length(CellSize);
    float4 weights = float4(axisWeight.x, axisWeight.x, axisWeight.y, axisWeight.y);
    float center = abs(w);
    float hmean = 0;
    // Preserve the zero-sample limit of the harmonic mean, including walls.
    if (center > 0 && all(a > 0) && all(d > 0))
        hmean = (1 + 2 * (axisWeight.x + axisWeight.y) + 4 * diagonalWeight)
            / (1 / center + dot(1 / a, weights) + diagonalWeight * dot(1 / d, float4(1, 1, 1, 1)));
    float eps = .2 * VC2Strength * h * h / StepSeconds;
    return eps * (.5 * w - sign(w) * hmean);
}
float ReadPotential(int2 p)
{
    if (Fluid.PeriodicBoundary) return Correction.Load(int3(WrapCell(p), 0));
    if (any(p < 0) || any(p >= int2(GridSize))) return 0;
    return Correction.Load(int3(p, 0));
}
float2 VC2(PSin input) : SV_Target
{
    int2 p = int2(input.Position.xy);
    float s = ReadPotential(p);
    float2 a = float2(-(s - ReadPotential(p - int2(0, 1))) / CellSize.y,
                        (s - ReadPotential(p - int2(1, 0))) / CellSize.x);
    float2 v = ReadVelocity(p) + StepSeconds * a;
    if (!Fluid.PeriodicBoundary)
    {
        if (p.x == GridSize.x - 1) v.x = 0;
        if (p.y == GridSize.y - 1) v.y = 0;
    }
    return v;
}
float Divergence(PSin input) : SV_Target
{
    int2 p = int2(input.Position.xy);
    // Backward divergence + forward pressure gradient compose the same
    // five-point Laplacian used by the solver (no quad ddx/ddy approximation).
    float2 v = ReadVelocity(p);
    float2 left = Fluid.PeriodicBoundary || p.x > 0 ? ReadVelocity(p - int2(1, 0)) : 0;
    float2 up = Fluid.PeriodicBoundary || p.y > 0 ? ReadVelocity(p - int2(0, 1)) : 0;
    return (v.x - left.x) / CellSize.x + (v.y - up.y) / CellSize.y;
}
float Relax(PSin input) : SV_Target
{
    int2 p = int2(input.Position.xy);
    float center = ReadPressure(p);
    float2 coefficient = 1 / (CellSize * CellSize);
    float residual = Rhs.Load(int3(p, 0)) - Laplacian(p);
    // Neumann walls remove neighbors from the diagonal as well as the
    // stencil. Using the interior diagonal under-relaxes boundary errors.
    float diagonal = Fluid.PeriodicBoundary ? 2 * (coefficient.x + coefficient.y)
        : coefficient.x * ((p.x > 0 ? 1 : 0) + (p.x < GridSize.x - 1 ? 1 : 0))
        + coefficient.y * ((p.y > 0 ? 1 : 0) + (p.y < GridSize.y - 1 ? 1 : 0));
    return center - .8 * residual / diagonal;
}
float Residual(PSin input) : SV_Target
{
    int2 p = int2(input.Position.xy);
    return Rhs.Load(int3(p, 0)) - Laplacian(p);
}
float Restrict(PSin input) : SV_Target
{
    // Explicit 2x2 volume average avoids sampler-dependent restriction.
    int2 p = 2 * int2(input.Position.xy);
    return .25 * (Rhs.Load(int3(p, 0)) + Rhs.Load(int3(p + int2(1, 0), 0))
        + Rhs.Load(int3(p + int2(0, 1), 0)) + Rhs.Load(int3(p + 1, 0)));
}
float Prolong(PSin input) : SV_Target
{
    // Clamp interpolation matches the homogeneous Neumann pressure boundary.
    return Pressure.Load(int3(int2(input.Position.xy), 0))
        + Correction.SampleLevel(PressureSampler, input.UV, 0);
}
float2 ProjectedVelocity(int2 cell)
{
    float center = ReadPressure(cell);
    return ReadVelocity(cell) - float2(ReadPressure(cell + int2(1, 0)) - center,
        ReadPressure(cell + int2(0, 1)) - center) / CellSize;
}
// Implemented in scene HLSL. The shared solver knows no emitter geometry.
float2 FluidApplyBoundary(int2 cell, float2 projectedVelocity);
float2 Project(PSin input) : SV_Target
{
    int2 cell = int2(input.Position.xy);
    return FluidApplyBoundary(cell, ProjectedVelocity(cell));
}
// Numerical regression entry point: isolate projection from scene constraints.
float2 ProjectUnforced(PSin input) : SV_Target
{
    return ProjectedVelocity(int2(input.Position.xy));
}
#endif
