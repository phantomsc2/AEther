#undef Histogram0
#undef Histogram1
// Optical-flow fluid/multigrid passes for the flight scenes. Coordinates and
// velocities are in viewport-height units, independent of the grid resolution.
cbuffer FluidConstants { float2 GridSize, CellSize; float StepSeconds, SourceAge, UnusedDecay, UnusedDiffusion; float AgeCatchupSeconds; float3 UnusedForcing; };
Texture2D<float2> Velocity;
Texture2D<float4> Dye;
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
    // The expanding carrier and pigment have separate, open outflow boundaries.
    return Pressure.Load(int3(Fluid.PeriodicBoundary ? WrapCell(p) : clamp(p, 0, int2(GridSize) - 1), 0));
}
float Laplacian(int2 p)
{
    float2 coefficient = 1 / (CellSize * CellSize);
    return coefficient.x * (ReadPressure(p + int2(1, 0)) + ReadPressure(p - int2(1, 0)))
        + coefficient.y * (ReadPressure(p + int2(0, 1)) + ReadPressure(p - int2(0, 1)))
        - 2 * (coefficient.x + coefficient.y) * ReadPressure(p);
}
float4 Source(float2 p)
{
    // The flight scene supplies a continuous IFS-to-history map. SourceAge is
    // only the solver's substep offset, not a discretized event index.
    return FlightFluidSource(p, SourceAge);
}
float CurlAt(int2 p)
{
    return (ReadVelocity(p + int2(1, 0)).y - ReadVelocity(p - int2(1, 0)).y) / (2 * CellSize.x)
        - (ReadVelocity(p + int2(0, 1)).x - ReadVelocity(p - int2(0, 1)).x) / (2 * CellSize.y);
}
float2 SampleTransportVelocity(float2 uv)
{
    return Velocity.SampleLevel(ClampFluid, uv, 0);
}
float2 Transport(float2 p)
{
    if (Fluid.PeriodicBoundary)
    {
        // A torus has no net expansion/outflow. A shared translational current
        // lets old plumes return continuously, instead of colliding radial jets
        // at the seam. Turbulent velocity remains fully pressure projected.
        float activity = saturate(PoseX + PoseZ + .2 * PoseY);
        float2 direction = normalize(float2(-1, .2 * sin(.12 * MusicalMovementTime)));
        return SampleTransportVelocity(ToUV(p))
            + activity * Fluid.Flow * 3 * direction;
    }
    // Camera optical flow is compressible in image space. Keep it outside the
    // pressure projection, which applies only to the turbulent perturbation.
    return Velocity.SampleLevel(ClampFluid, ToUV(p), 0) + FlightCarrier(p);
}
float2 Trace(float2 uv, float dt)
{
    float2 p = World(uv);
    float2 midpoint = p - .5 * dt * Transport(p);
    return ToUV(p - dt * Transport(midpoint));
}
float2 Backtrace(float2 uv) { return Trace(uv, StepSeconds); }

float2 AdvectVelocity(PSin input) : SV_Target
{
    float2 p = World(input.UV);
    float2 v = Velocity.SampleLevel(ClampFluid, Backtrace(input.UV), 0) * exp(-.65 * StepSeconds);
    float4 source = Source(p);
    float2 force = FlightTurbulence(p, source);
    // Vorticity confinement restores small curls lost to semi-Lagrangian advection.
    int2 q = int2(input.Position.xy);
    float2 gradient = float2(abs(CurlAt(q + int2(1, 0))) - abs(CurlAt(q - int2(1, 0))),
        abs(CurlAt(q + int2(0, 1))) - abs(CurlAt(q - int2(0, 1)))) / (2 * CellSize);
    gradient /= max(1e-4, length(gradient));
    force += (Fluid.Curl * 2) * min(CellSize.x, CellSize.y) * CurlAt(q) * float2(gradient.y, -gradient.x);
    v += StepSeconds * force;
    v *= min(1, 2.0 / max(1e-5, length(v)));
    if (!Fluid.PeriodicBoundary)
    {
        if (q.x == GridSize.x - 1) v.x = 0;
        if (q.y == GridSize.y - 1) v.y = 0;
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
float2 Project(PSin input) : SV_Target
{
    int2 p = int2(input.Position.xy);
    float center = ReadPressure(p);
    return ReadVelocity(p) - float2(ReadPressure(p + int2(1, 0)) - center,
        ReadPressure(p + int2(0, 1)) - center) / CellSize;
}
float4 AdvectDye(PSin input) : SV_Target
{
    float2 uv = Backtrace(input.UV);
    float4 dye = Dye.SampleLevel(ClampFluid, uv, 0);
    float4 neighbors = .25 * (Dye.SampleLevel(ClampFluid, uv + float2(1 / GridSize.x, 0), 0)
        + Dye.SampleLevel(ClampFluid, uv - float2(1 / GridSize.x, 0), 0)
        + Dye.SampleLevel(ClampFluid, uv + float2(0, 1 / GridSize.y), 0)
        + Dye.SampleLevel(ClampFluid, uv - float2(0, 1 / GridSize.y), 0));
    float2 p = World(input.UV);
    float4 source = Source(p);
    float noise = 1 - exp(-2 * source.b);
    float diffusion = saturate(StepSeconds * (Fluid.Diffusion * 0.01) * 4
        * smoothstep(.02, .35, max(noise, PoseZ))
        / (CellSize.x * CellSize.y));
    // Conserve pigment as the carrier expands. Without dilution a fading
    // ambient plume gains total light merely by occupying more screen area.
    float expansion = FlightDilution(p);
    if (Fluid.PeriodicBoundary) expansion = 0;
    dye = lerp(dye, neighbors, diffusion) * exp(-((0.69314718 / (0.1 + Fluid.HalfLife * 9.9)) + expansion) * StepSeconds);
    // FP16 subnormals can round an exponential loss back to the same stored
    // value forever, especially on a closed periodic domain. Remove one minimum
    // representable quantum per fixed step only in that subnormal range.
    dye = max(0, dye - (dye < .00006103515625 ? 5.96046448e-8 : 0));
    // Capture the continuously mapped IFS foreground for a fluid afterglow.
    float4 dose = StepSeconds * 6 * FlightPigment(p, source, SourceAge);
    // Smooth finite-capacity accumulation: approximately additive for faint
    // material, asymptotic near capacity. Convex advection/diffusion and exact
    // exponential losses keep every update positive and bounded for any input.
    dye += (12 - dye) * (1 - exp(-dose / 12));
    // A directional tonal blade removes existing cloud material. Its wake is
    // retained in the advected state, so the cut travels with the cloud.
    dye *= exp(-StepSeconds * FlightSlice(p, source));
    // A soft sink prevents edge buildup and clamped streaks on resize/aspect extremes.
    float edge = min(min(input.UV.x, 1 - input.UV.x), min(input.UV.y, 1 - input.UV.y));
    float edgeDecay = Fluid.PeriodicBoundary ? 1 : exp(-StepSeconds * 12 * (1 - smoothstep(0, .04, edge)));
    return min(dye * edgeDecay, 12);
}
