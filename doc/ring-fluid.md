# Shared MandelFluid backend

MandelFluid, MandelFluidv2, MandelFluidv3 and MandelFluidv4 use the same simulation.
V4's numerical passes, emitter, defaults and lighting behavior are preserved;
the older variants retain their own scene shading and adopt v4's fluid behavior.

`Common/RingFluidParameters.hlsl` owns the emitter/fluid types, defaults and
`RING_FLUID` capability. Each scene declares these shared types in `Parameters`
and includes `Common/RingEmitter.hlsl`. The host selects
`Common/RingFluidSimulation.hlsl` by capability, with no MandelFluid scene-name
branches. FluidFlight and PlaneFlight retain their optical-flow backend in
`Common/FluidSimulation.hlsl`.

The ring backend uses a square 512x512 grid, or 128x128 with `Fluid.LowQuality`.
Velocity and pressure advance at 60 Hz with three multigrid V-cycles, two initial
velocity warmup steps on live input, and a 16-iteration coarse solve. Material
advances on every displayed frame; catch-up remains bounded to 50 ms. At a 4:1
viewport, the unchanged square-grid solve removes about 93-94% of the projection
fixture divergence; ordinary aspect ratios remove more than 99%. Viscosity
uses separable velocity diffusion. Dye has no artificial diffusion.

Material is FP32 RGB signal mass plus density. A second FP32 two-channel texture
stores density-weighted birth timestamp and frequency. Transport carries these
attributes together. Signal fades exponentially with the audio history duration;
density and coordinate moments remain available without keeping pixels lit.

- Local S emits frequency-tinted material through `Emitter.Rate`, `MaterialGain`
  and `Capacity`. `Radius`, `Width` and `EdgeSoftness` define the inlet.
- Local B controls `Fluid.EmissionVelocity`, `VelocityGain` and `Curl`.
- T contributes to scene lighting, never material injection or inlet motion.
- `Fluid.Viscosity` maps to `0.1 * value^3`; `PeriodicBoundary` wraps the domain.
- `Fluid.LowQuality` changes grid size and recreates simulation state.

The older half-life, diffusion, background and flow controls are removed from
MandelFluid v1-v3. They remain part of the separate flight backend.

GPU regressions compare the older variants against v4 with identical inputs,
including viscosity, curl and a 165 Hz display. Other checks cover component
separation, projection, periodic timestamp/frequency transport, v4 cadence and
filament preservation, and natural decay to black after music stops.
