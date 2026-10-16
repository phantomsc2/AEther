#undef Histogram0
#undef Histogram1
// Vortice migration of the SharpDX fluid/multigrid passes. Coordinates and
// velocities are in viewport-height units, independent of the grid resolution.
cbuffer FluidConstants { float2 GridSize, CellSize; float StepSeconds, SourceAge, UnusedDecay, UnusedDiffusion; float4 UnusedForcing; };
Texture2D<float2> Velocity;
Texture2D<float4> Dye;
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
}
float CurlAt(int2 p)
{
    return (ReadVelocity(p + int2(1, 0)).y - ReadVelocity(p - int2(1, 0)).y) / (2 * CellSize.x)
        - (ReadVelocity(p + int2(0, 1)).x - ReadVelocity(p - int2(0, 1)).x) / (2 * CellSize.y);
}
float Mobility() { return smoothstep(.02, .55, PoseZ + .2 * PoseY); }
float2 Transport(float2 p)
{
    // A center source expands the carrier. Project only the turbulent velocity:
    // projecting this source as well would cancel the intended explosion.
    float r = length(p);
    return Velocity.SampleLevel(ClampFluid, ToUV(p), 0)
        + Mobility() * (Fluid.Flow * 3) * .65 * p / max(.08, r) / (1 + .65 * r);
}
float2 Backtrace(float2 uv)
{
    float2 p = World(uv);
    float2 midpoint = p - .5 * StepSeconds * Transport(p);
    return ToUV(p - StepSeconds * Transport(midpoint));
}

float2 AdvectVelocity(PSin input) : SV_Target
{
    float2 p = World(input.UV);
    float2 v = Velocity.SampleLevel(ClampFluid, Backtrace(input.UV), 0) * exp(-.65 * StepSeconds);
    float4 source = Source(p);
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
        * (1.8 * tone + 3 * (Fluid.Noise * 3) * noise + .7 * attack) * envelope * Mobility();
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
    return center - .8 * residual / (2 * (coefficient.x + coefficient.y));
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
    float diffusion = saturate(StepSeconds * (Fluid.Diffusion * 0.01) * 4 * smoothstep(.02, .35, max(noise, PoseZ))
        / (CellSize.x * CellSize.y));
    // Conserve pigment as the carrier expands. Without dilution a fading
    // ambient plume gains total light merely by occupying more screen area.
    float r = length(p);
    float expansion = Mobility() * .65 * (Fluid.Flow * 3) / (max(.08, r) * pow(1 + .65 * r, 2));
    dye = lerp(dye, neighbors, diffusion) * exp(-((0.69314718 / (0.1 + Fluid.HalfLife * 9.9)) + expansion) * StepSeconds);
    float frequency = atan2(abs(p.x), p.y) / 3.14159265;
    float3 palette = .5 + .5 * cos(6.2831853 * (frequency * .9 + .11 * (MusicalMovementTime * Quality.MovementSpeed * 10) + float3(.04, .38, .62)));
    // Tones supply narrow central plumes. Noise feeds a broader, quieter body
    // across the interior, rather than turning every sound into the same jet.
    float radius = length(p);
    float emitter = exp(-pow(radius / .32, 4)) * smoothstep(.015, .09, radius);
    float tone = 1 - exp(-2 * source.r);
    float mist = .16 * exp(-1.3 * radius * radius) * (Fluid.Noise * 3) * noise;
    float3 pigment = palette * tone * emitter + lerp(palette, float3(.45, .62, .72), .65) * mist;
    float4 dose = StepSeconds * 6 * float4(pigment, tone * emitter + mist);
    // Smooth finite-capacity accumulation: approximately additive for faint
    // material, asymptotic near capacity. Convex advection/diffusion and exact
    // exponential losses keep every update positive and bounded for any input.
    dye += (12 - dye) * (1 - exp(-dose / 12));
    // A soft sink prevents edge buildup and clamped streaks on resize/aspect extremes.
    float edge = min(min(input.UV.x, 1 - input.UV.x), min(input.UV.y, 1 - input.UV.y));
    return min(dye * exp(-StepSeconds * 12 * (1 - smoothstep(0, .04, edge))), 12);
}
