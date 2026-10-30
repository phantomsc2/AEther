# Froxel3

The current preset follows the October 30 screenshot: 256 depth slices, 4096
objects, slower/tighter clouds, and the selected camera/albedo values. Actual
g defaults to .6 following the subsequent request. See
[performance review](froxel3-performance.md) for the current preset measurements
and a proposed path toward 100 rings; older measurements below are historical.

Three homogeneous ring lights in the XY, YZ and ZX planes illuminate procedural
heterogeneous fog and 4,096 slowly drifting octahedra. Select `Froxel3` or launch
with `--scene=Froxel3`. The scene is independent of audio.

Each ring has a fixed linear RGB color: orange (1,.18,.04), cyan (.04,.7,1),
and violet (.45,.08,1). Squared sine intensity cycles have an eight-second
period and phase offsets of one-third turn. `Ring.Speed` defaults to .5;
zero freezes the cycles and one doubles their speed. Shared radius and softening
default to 4.5 and .05 units. Gain defaults to about 6.6 intensity units per unit length
at each ring's peak, with mapping `20*2^(16*(Gain-.5))`.

## Lighting

Uniform emission removes all spectrum textures, prefix sums, adaptive arcs and
lighting-resolution controls. Each occupied froxel evaluates three closed forms:

```text
rho = length(position.xy) in ring-local coordinates
n = (R-rho)^2 + z^2 + a^2
f = (R+rho)^2 + z^2 + a^2
W = 2*pi*R / sqrt(n*f)
source = scattering * sum(intensityRGB * W) / (4*pi)
```

This is the exact uniform-ring integral for the softened inverse-square model
and isotropic phase. It does not include light-path attenuation or shadows.
`Medium.Anisotropy` exposes the full HG range using the app's normalized control:
0 maps to g=-1, .5 to g=0, and 1 to g=+1. The default is .8 (g=.6). Positive g favors forward
light propagation, negative g backward scattering. Endpoints evaluate at
g=+/-0.9999 because exact +/-1 are delta distributions.

For nonzero g, phase is averaged over the same eight inverse-square-weighted
ring directions used by surface lighting. Each local segment evaluates this
factor near closest approach and multiplies its analytic distance-peak integral.
The HG factor is normalized relative to isotropic scattering; the existing
1/(4*pi) remains applied once. g=0 bypasses angular evaluation. Surface Lambert
lighting does not depend on medium anisotropy. Large |g| intentionally exposes
the limits of this approximation: eight directions can miss narrow lobes or
produce sharp brightness changes, and phase may vary across a local segment.
The existing FP16 radiance clamp still applies to extreme peaks.
Camera extinction still follows the sampled heterogeneous density.
The medium has no spherical boundary or radial fade. Rendering still integrates
only the camera grid's forward-depth range of 0..64 world units.

Lighting is now integrated across each camera segment. A local tangent to each
ring isolates the narrow `1/sqrt(distance^2+a^2)` term, whose segment integral
uses an asinh difference. The far-distance factor is evaluated near closest
approach. Curvature triggers 1..8 local pieces; this is a bounded approximation,
not exact curved-ring ray integration. Scattering density is sampled once per
froxel and held constant, as is extinction; the averaged lighting is multiplied
by the existing exponential segment weight. Correlation between attenuation
and lighting within a segment remains approximate. Raster composition evaluates
the same integral over the visible partial segment. The source volume now
stores material coefficients rather than pre-multiplied point lighting for V3.

This removes depth-jitter variation from lighting in a constant medium. Animated
noise in heterogeneous density and the 8x screen grid can still produce grain.
Softening is unchanged. New defaults use density .36 (mean extinction .07776),
RGB albedo (.888,.840,.716), camera distance .232 (10.816 units), yaw .523 and pitch .561. Ring.Gain
is .40: lower light gain balances the denser fog to preserve facet contrast.

Surfaces approximate the Lambert cosine with eight equal-geometric-weight
samples per ring. Inverse-CDF directions use precomputed tangent constants and
rational identities; no runtime trigonometry is needed in that sample loop.
This remains an approximation near horizons or sharp directional variations.
There are no specular reflections, cast shadows, or visible emitter cores.

## Geometry and controls

The octahedra are distributed equally among three toroidal clouds around the
rings. Their default orbit speeds are .08..16 radians/second and independent
spins .15..4 radians/second. Full-resolution raster color/depth is composited
with fog up to each surface, including the partial final volume segment.

`Froxel3Options` now contains only DepthSlices (8..256, default 256) and Instances
(0..65535, default 4096). Screen resolution is reduced eightfold per axis for
fog. The medium retains procedural noise and animated depth
jitter without history. Cloud.Speed, Cloud.Spread and Cloud.Size retain their
0..1 controls. Defaults are Speed=.057, Spread=.174 (tube radius .522), Size=.5 (sizes .055..13).

## Validation and profiling

GPU tests compare the scalar ring integral with independent numerical
integration, surface lighting with Lambert quadrature, and check intensity phase
offsets, frozen lighting, audio independence, resize, invalid-option rollback,
hot reload and deactivation. V1/V2 remain available as references.

```powershell
dotnet build AEther.sln --no-restore
dotnet run --project tools/AEther.MandelLightProfile -- --froxel3 AEther.Vortice/Shaders artifacts/froxel3-uniform
```

The profiler varies depth (32/86/128) and instances (0/1024/4096) at 1280x720.
It uses eight warmup frames and medians of 21 GPU timestamp samples, excludes
Present/readback and includes output composition. Initial Intel Arc default
measurement was 1.87 ms total, with .70 ms injection, .57 ms integration and
.28 ms rasterization. Clock variation was substantial across the matrix;
these measurements do not establish a speedup over the former one-ring scene.
With integrated peaks and close-up defaults, a later 720p run measured 1.62 ms total (.39 ms injection, .63 ms integration, .26 ms raster). Clock variation prevents direct speed comparisons. Lighting costs up to eight local pieces per ring and cell, independent of audio width;
integration scales with froxel count and raster work with shaded fragments.
