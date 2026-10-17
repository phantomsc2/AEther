#undef Histogram0
#undef Histogram1
// Vortice migration of the SharpDX fluid/multigrid passes. Coordinates and
// velocities are in viewport-height units, independent of the grid resolution.
cbuffer FluidConstants { float2 GridSize, CellSize; float StepSeconds, SourceAge, UnusedDecay, UnusedDiffusion; float AgeCatchupSeconds; float3 UnusedForcing; };
Texture2D<float2> Velocity;
Texture2D<float4> Dye;
Texture2D<float4> Advected;
#ifdef MANDEL_FLUID_V3
// Density-weighted age in seconds and matching density, transported together.
Texture2D<float2> DyeAge;
Texture2D<float2> AdvectedAge;
struct DyeOutput { float4 Color : SV_Target0; float2 Age : SV_Target1; };
float2 AgeBounds(float2 uv, bool upper)
{
    int2 p = int2(floor(uv * GridSize - .5));
    float2 bound = upper ? -1e30 : 1e30;
    [unroll] for (int y = 0; y < 2; ++y)
        [unroll] for (int x = 0; x < 2; ++x)
        {
            int2 q = p + int2(x, y);
            float2 v = 0;
            if (all(q >= 0) && all(q < int2(GridSize))) v = DyeAge.Load(int3(q, 0));
            bound = upper ? max(bound, v) : min(bound, v);
        }
    return bound;
}
#endif
Texture2D<float> Pressure;
Texture2D<float> Rhs;
Texture2D<float> Correction;
SamplerState ClampFluid;
SamplerState PressureSampler;

float2 World(float2 uv) { return (2 * uv - 1) * float2(AspectRatio, 1); }
float2 ToUV(float2 p) { return .5 + .5 * p / float2(AspectRatio, 1); }
float2 ReadVelocity(int2 p) { return Velocity.Load(int3(clamp(p, 0, int2(GridSize) - 1), 0)); }
float ReadPressure(int2 p)
{
    // Neumann pressure boundary, paired with zero normal turbulent velocity.
    // The expanding carrier and pigment have separate, open outflow boundaries.
    return Pressure.Load(int3(clamp(p, 0, int2(GridSize) - 1), 0));
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
#ifdef MANDEL_FLUID_V3
    return RadiatingSource(p, SourceAge);
#elif defined(MANDEL_COMET)
    return CometSource(p, SourceAge);
#elif defined(PROJECTIVE_FLIGHT)
    // The flight scene supplies a continuous IFS-to-history map. SourceAge is
    // only the solver's substep offset, not a discretized event index.
    return FlightFluidSource(p, SourceAge);
#else
    float frequency = atan2(abs(p.x), p.y) / 3.14159265;
    float age = max(SourceAge, HistoryNewestAge);
    uint bands, rows; Histogram0.GetDimensions(bands, rows);
    // Bridge at most two analysis rows. A paused loopback must never hold its
    // last nonzero row as an endlessly replenished fluid source.
    if (HistoryNewestAge > SourceAge + 2.0 / rows) return 0;
    if (age > HistoryAvailable || HistoryAvailable <= 0) return 0;
    float2 uv = float2(frequency, HistogramShift - age);
    return max(0, lerp(Histogram0.SampleLevel(Linear, uv, 0),
        Histogram1.SampleLevel(Linear, uv, 0), step(0, p.x)));
#endif
}
float CurlAt(int2 p)
{
    return (ReadVelocity(p + int2(1, 0)).y - ReadVelocity(p - int2(1, 0)).y) / (2 * CellSize.x)
        - (ReadVelocity(p + int2(0, 1)).x - ReadVelocity(p - int2(0, 1)).x) / (2 * CellSize.y);
}
float Mobility() { return smoothstep(.02, .55, PoseZ + .2 * PoseY); }
float2 SampleTransportVelocity(float2 uv)
{
#ifdef MANDEL_FLUID_V3
    // Pressure is cell centered; x/y velocities live on the right/bottom
    // faces. Reconstruct both components at the same physical point.
    return float2(Velocity.SampleLevel(ClampFluid, uv - float2(.5 / GridSize.x, 0), 0).x,
        Velocity.SampleLevel(ClampFluid, uv - float2(0, .5 / GridSize.y), 0).y);
#else
    return Velocity.SampleLevel(ClampFluid, uv, 0);
#endif
}
float2 Transport(float2 p)
{
#ifdef MANDEL_FLUID_V3
    return SampleTransportVelocity(ToUV(p)) + RadiatingCarrier(p);
#elif defined(MANDEL_COMET)
    return Velocity.SampleLevel(ClampFluid, ToUV(p), 0) + CometCarrier(p);
#elif defined(PROJECTIVE_FLIGHT)
    // Camera optical flow is compressible in image space. Keep it outside the
    // pressure projection, which applies only to the turbulent perturbation.
    return Velocity.SampleLevel(ClampFluid, ToUV(p), 0) + FlightCarrier(p);
#else
    // A center source expands the carrier. Project only the turbulent velocity:
    // projecting this source as well would cancel the intended explosion.
    float r = length(p);
    return Velocity.SampleLevel(ClampFluid, ToUV(p), 0)
        + Mobility() * (Fluid.Flow * 3) * .65 * p / max(.08, r) / (1 + .65 * r);
#endif
}
float2 Trace(float2 uv, float dt)
{
    float2 p = World(uv);
    float2 midpoint = p - .5 * dt * Transport(p);
    return ToUV(p - dt * Transport(midpoint));
}
float2 Backtrace(float2 uv) { return Trace(uv, StepSeconds); }

// MacCormack correction uses the predicted field transported backward in
// time. Clamp to the four original donors: no new extrema or negative dye.
float4 DonorBounds(float2 uv, bool upper, bool velocityField)
{
    int2 p = int2(floor(uv * GridSize - .5));
    float4 bound = upper ? -1e30 : 1e30;
    [unroll] for (int y = 0; y < 2; ++y)
        [unroll] for (int x = 0; x < 2; ++x)
        {
            int2 q = p + int2(x, y);
            float4 v = 0;
            if (all(q >= 0) && all(q < int2(GridSize)))
                v = velocityField ? float4(Velocity.Load(int3(q, 0)), 0, 0) : Dye.Load(int3(q, 0));
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
#ifdef MANDEL_FLUID_V3
DyeOutput PredictDye(PSin input)
{
    float2 uv = Backtrace(input.UV);
    DyeOutput output;
    output.Color = Dye.SampleLevel(ClampFluid, uv, 0);
    output.Age = DyeAge.SampleLevel(ClampFluid, uv, 0);
    return output;
}
#else
float4 PredictDye(PSin input) : SV_Target
{
    return Dye.SampleLevel(ClampFluid, Backtrace(input.UV), 0);
}
#endif

float2 AdvectVelocity(PSin input) : SV_Target
{
    float2 p = World(input.UV);
#ifdef MANDEL_FLUID_V3
    float2 x = float2(.5 / GridSize.x, 0), y = float2(0, .5 / GridSize.y);
    float2 backX = Backtrace(input.UV + x) - x, backY = Backtrace(input.UV + y) - y;
    float2 reverse = float2(Advected.SampleLevel(ClampFluid, Trace(input.UV + x, -StepSeconds) - x, 0).x,
        Advected.SampleLevel(ClampFluid, Trace(input.UV + y, -StepSeconds) - y, 0).y);
    float2 corrected = Advected.Load(int3(int2(input.Position.xy), 0)).xy
        + .5 * (Velocity.Load(int3(int2(input.Position.xy), 0)) - reverse);
    float2 lower = float2(DonorBounds(backX, false, true).x, DonorBounds(backY, false, true).y);
    float2 upper = float2(DonorBounds(backX, true, true).x, DonorBounds(backY, true, true).y);
    float2 v = clamp(corrected, lower, upper) * exp(-.25 * StepSeconds);
#else
    float2 v = Velocity.SampleLevel(ClampFluid, Backtrace(input.UV), 0) * exp(-.65 * StepSeconds);
#endif
    float4 source = Source(p);
#ifdef MANDEL_FLUID_V3
    float2 force = RadiatingTurbulence(p, source);
#elif defined(MANDEL_COMET)
    float2 force = CometTurbulence(p, source);
#elif defined(PROJECTIVE_FLIGHT)
    float2 force = FlightTurbulence(p, source);
#else
    // Saturating drive keeps very loud or broadband material numerically bounded.
    float tone = 1 - exp(-2 * source.r), noise = 1 - exp(-2 * source.b);
    float attack = 1 - exp(-2 * source.g);
    float radius = length(p);
    float envelope = exp(-2.2 * radius * radius);
    float2 direction = p / max(.08, radius);
    float angle = atan2(p.y, p.x);
    float eddies = sin(5 * angle + 7 * radius - .9 * (MusicalMovementTime * Quality.MovementSpeed * 10))
        + .5 * sin(9 * p.x + 7 * p.y + 1.3 * (MusicalMovementTime * Quality.MovementSpeed * 10));
    float2 force = float2(-direction.y, direction.x) * eddies
        * (1.8 * tone + 3 * (Fluid.Background * 3) * noise + .7 * attack) * envelope * Mobility();
#endif
    // Vorticity confinement restores small curls lost to semi-Lagrangian advection.
    int2 q = int2(input.Position.xy);
    float2 gradient = float2(abs(CurlAt(q + int2(1, 0))) - abs(CurlAt(q - int2(1, 0))),
        abs(CurlAt(q + int2(0, 1))) - abs(CurlAt(q - int2(0, 1)))) / (2 * CellSize);
    gradient /= max(1e-4, length(gradient));
    force += (Fluid.Curl * 2) * min(CellSize.x, CellSize.y) * CurlAt(q) * float2(gradient.y, -gradient.x);
    v += StepSeconds * force;
    v *= min(1, 2.0 / max(1e-5, length(v)));
    if (q.x == GridSize.x - 1) v.x = 0;
    if (q.y == GridSize.y - 1) v.y = 0;
    return v;
}

float Divergence(PSin input) : SV_Target
{
    int2 p = int2(input.Position.xy);
    // Backward divergence + forward pressure gradient compose the same
    // five-point Laplacian used by the solver (no quad ddx/ddy approximation).
    float2 v = ReadVelocity(p);
    float2 left = p.x > 0 ? ReadVelocity(p - int2(1, 0)) : 0;
    float2 up = p.y > 0 ? ReadVelocity(p - int2(0, 1)) : 0;
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
    float diagonal = coefficient.x * ((p.x > 0 ? 1 : 0) + (p.x < GridSize.x - 1 ? 1 : 0))
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
#ifdef MANDEL_FLUID_V3
DyeOutput AdvectDye(PSin input)
#else
float4 AdvectDye(PSin input) : SV_Target
#endif
{
    float2 uv = Backtrace(input.UV);
#ifdef MANDEL_FLUID_V3
    float4 reverse = Advected.SampleLevel(ClampFluid, Trace(input.UV, -StepSeconds), 0);
    float4 corrected = Advected.Load(int3(int2(input.Position.xy), 0))
        + .5 * (Dye.Load(int3(int2(input.Position.xy), 0)) - reverse);
    float4 dye = clamp(corrected, DonorBounds(uv, false, false), DonorBounds(uv, true, false));
    float2 ageReverse = AdvectedAge.SampleLevel(ClampFluid, Trace(input.UV, -StepSeconds), 0);
    float2 ageCorrected = AdvectedAge.Load(int3(int2(input.Position.xy), 0))
        + .5 * (DyeAge.Load(int3(int2(input.Position.xy), 0)) - ageReverse);
    float2 age = clamp(ageCorrected, AgeBounds(uv, false), AgeBounds(uv, true));
#else
    float4 dye = Dye.SampleLevel(ClampFluid, uv, 0);
#endif
    float4 neighbors = .25 * (Dye.SampleLevel(ClampFluid, uv + float2(1 / GridSize.x, 0), 0)
        + Dye.SampleLevel(ClampFluid, uv - float2(1 / GridSize.x, 0), 0)
        + Dye.SampleLevel(ClampFluid, uv + float2(0, 1 / GridSize.y), 0)
        + Dye.SampleLevel(ClampFluid, uv - float2(0, 1 / GridSize.y), 0));
    float2 p = World(input.UV);
    float4 source = Source(p);
    float noise = 1 - exp(-2 * source.b);
#ifdef GEOMETRY_AFTERGLOW
    float diffusion = saturate(StepSeconds * (Fluid.Diffusion * 0.01) * 4 / (CellSize.x * CellSize.y));
#else
    float diffusion = saturate(StepSeconds * (Fluid.Diffusion * 0.01) * 4 * smoothstep(.02, .35, max(noise, PoseZ))
        / (CellSize.x * CellSize.y));
#endif
    // Conserve pigment as the carrier expands. Without dilution a fading
    // ambient plume gains total light merely by occupying more screen area.
    float r = length(p);
#ifdef MANDEL_FLUID_V3
    float expansion = RadiatingDilution(p);
#elif defined(MANDEL_COMET)
    float expansion = CometDilution(p);
#elif defined(PROJECTIVE_FLIGHT)
    float expansion = FlightDilution(p);
#else
    float expansion = Mobility() * .65 * (Fluid.Flow * 3) / (max(.08, r) * pow(1 + .65 * r, 2));
#endif
    dye = lerp(dye, neighbors, diffusion) * exp(-((0.69314718 / (0.1 + Fluid.HalfLife * 9.9)) + expansion) * StepSeconds);
#ifdef MANDEL_FLUID_V3
    float2 ageNeighbors = .25 * (DyeAge.SampleLevel(ClampFluid, uv + float2(1 / GridSize.x, 0), 0)
        + DyeAge.SampleLevel(ClampFluid, uv - float2(1 / GridSize.x, 0), 0)
        + DyeAge.SampleLevel(ClampFluid, uv + float2(0, 1 / GridSize.y), 0)
        + DyeAge.SampleLevel(ClampFluid, uv - float2(0, 1 / GridSize.y), 0));
    age = lerp(age, ageNeighbors, diffusion);
    float meanAge = clamp(age.x / max(1e-12, age.y), 0, 120) + StepSeconds + AgeCatchupSeconds;
    float oldDensity = dye.a;
    age = float2(meanAge * oldDensity, oldDensity);
    float4 dose = StepSeconds * 6 * RadiatingPigment(p, source);
#elif defined(MANDEL_COMET)
    float4 dose = StepSeconds * 6 * CometPigment(p, source);
#elif defined(PROJECTIVE_FLIGHT)
    // Capture the continuously mapped IFS foreground for a fluid afterglow.
    float4 dose = StepSeconds * 6 * FlightPigment(p, source);
#else
    float frequency = atan2(abs(p.x), p.y) / 3.14159265;
    float3 palette = .5 + .5 * cos(6.2831853 * (frequency * .9 + .11 * (MusicalMovementTime * Quality.MovementSpeed * 10) + float3(.04, .38, .62)));
    // Tones supply narrow central plumes. Noise feeds a broader, quieter body
    // across the interior, rather than turning every sound into the same jet.
    float radius = length(p);
    float emitter = exp(-pow(radius / .32, 4)) * smoothstep(.015, .09, radius);
    float tone = 1 - exp(-2 * source.r);
    float mist = .16 * exp(-1.3 * radius * radius) * (Fluid.Background * 3) * noise;
    float3 pigment = palette * tone * emitter + lerp(palette, float3(.45, .62, .72), .65) * mist;
    float4 dose = StepSeconds * 6 * float4(pigment, tone * emitter + mist);
#endif
    // Smooth finite-capacity accumulation: approximately additive for faint
    // material, asymptotic near capacity. Convex advection/diffusion and exact
    // exponential losses keep every update positive and bounded for any input.
    dye += (12 - dye) * (1 - exp(-dose / 12));
#ifdef MANDEL_FLUID_V3
    // Fresh emission has zero residence age; only a late source row is older.
    float birthAge = max(0, HistoryNewestAge - SourceAge) * HistorySeconds / max(1e-6, HistorySpan);
    age += float2(birthAge, 1) * max(0, dye.a - oldDensity);
#endif
#ifdef PROJECTIVE_FLIGHT
    // A directional tonal blade removes existing cloud material. Its wake is
    // retained in the advected state, so the cut travels with the cloud.
    dye *= exp(-StepSeconds * FlightSlice(p, source));
#endif
    // A soft sink prevents edge buildup and clamped streaks on resize/aspect extremes.
    float edge = min(min(input.UV.x, 1 - input.UV.x), min(input.UV.y, 1 - input.UV.y));
    float edgeDecay = exp(-StepSeconds * 12 * (1 - smoothstep(0, .04, edge)));
#ifdef MANDEL_FLUID_V3
    DyeOutput output;
    output.Color = min(dye * edgeDecay, 12);
    output.Age = age * edgeDecay;
    return output;
#else
    return min(dye * edgeDecay, 12);
#endif
}
