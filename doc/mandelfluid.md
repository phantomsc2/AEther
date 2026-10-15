# MandelFluid

Select MandelFluid in settings. The scene shares Mandelbox's finite-iteration
field, stereo frequency angles, movement and spatial AA. Audio roles are distinct:

- Aggregate energy keeps the underlying fractal visible throughout a passage.
- Tones form directional rays near the center, blending into body material farther
  out. Pure sinusoid mixtures settle into static, crystalline structure.
- Transients retain the original histogram-age shockwaves from center to edge.
  They sharply modulate exposure and highlight folds; they do not deposit pigment.
- Noise/distortion controls overall movement and mixing, adds broader mist, and
  enables diffusion. A small transient contribution can agitate the carrier.

The geometric mean of bounded tone/transient/noise envelopes strengthens the
interaction when all three are present. It has a nonzero floor: solo components
and aggregate energy remain visible, and a missing component cannot black out the
scene. Class envelopes and field-energy demand advance per audio row, independently
of display batching, and decay during capture silence.

Noise primarily drives slice/rotation/zoom movement and the palette clock. The
same activity controls outward transport and stirring. Below a small noise/attack
threshold these clocks and forcing stop; existing velocity and material relax.
Diffusion is explicitly noise-dependent: the live diffusion setting controls its
maximum, rather than blurring pure notes at all times. Detector leakage below the
threshold does not keep an otherwise tonal field moving.

## Migration and numerical changes

Reference: `AEther.WindowsForms/Multigrid.cs`, `States/Fluid.cs`, and the
`fluid-*.fx`, `mg-residual.fx`, `poisson-sor.fx` shaders before commit `de13cee`.
The recursive V-cycle is retained: pre-smooth, compute residual, restrict it,
recursively solve a zero-initialized correction, interpolate/add, post-smooth.
Vortice owns all textures and shader programs; Effects11 and SharpDX are unnecessary.

The pressure solve uses float32 surfaces, weighted Jacobi relaxation, explicit
2x2 restriction and bilinear prolongation. Levels continue to a four-cell short
axis instead of stopping near 32 cells. Two V-cycles run per simulation step.
Backward divergence and forward pressure gradient compose the exact five-point
Laplacian used by the solver, replacing the old quad-derivative projection.
Homogeneous Neumann pressure boundaries match zero normal turbulent velocity;
the zero-mean divergence makes the pressure nullspace harmless.

Projected turbulence is separate from the outward carrier: projecting a center
source into a divergence-free field would suppress the intended expansion.
Pigment uses the combined flow, midpoint backtracing, bounded noise-dependent
diffusion, expansion dilution and exponential decay. Vorticity confinement restores
curls lost during advection. Dye exits through border-zero sampling and an edge sink.

Deposition uses `Cnew = C + (12 - C) * (1 - exp(-dose / 12))`: approximately additive
at low density, smoothly approaching finite capacity under repeated input.
Nonnegative doses, convex advection/diffusion and exponential losses preserve
[0,12]. Velocity forcing is bounded before pressure projection. Transient-only
input can stir or illuminate existing material, but cannot create any.

The simulation advances at 60 fixed steps per second on a power-of-two grid with
a maximum axis of 256 cells (minimum 32), with physical cell spacing adjusted to
the viewport aspect. Frame catch-up is bounded to 50 ms. Scene re-entry, resize and
stalls longer than half a second reset the field. Resources are allocated only
when MandelFluid is selected. Source sampling bridges at most two missing analysis
rows, then stops injecting when capture stops; material continues dissipating.

All solver shaders participate in transactional hot reload. Compilation errors
preserve previous programs and simulation textures. The experimental full-resolution
transient pigment buffers, deposition pass and transient half-life control are removed.

## Controls and checks

The shared Mandelbox/MandelFluid grid provides live flow (0 to 3), pigment half-life
(0.1 to 10 seconds), maximum noise diffusion (0 to 0.01), noise (0 to 3) and curl
(0 to 2). Defaults are 1, 1.4 seconds, 0.001, 1 and 0.45. Changes do not restart capture.

Run `dotnet run --project tools/AEther.Smoke -- . --fluid-only` for GPU checks of
projection, persistent energy illumination, stationary pure-tone velocity, zero
noise diffusion, noise-driven movement/mixing, transient-only zero pigment,
center-to-edge shockwave travel, release, silent capture, fixed-step agreement at
30/60/120 display FPS, extreme-input stability, scene re-entry, failed solver reload
and odd/portrait/tiny resizes. PNGs go into `tools/AEther.Smoke/artifacts` (ignored).
The full suite also checks settings and session restarts. Real-audio aesthetic
tuning remains a manual listening/visual check.
