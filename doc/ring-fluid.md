# Shared MandelFluid backend

MandelFluid uses the [plain HLSL fluid contract](fluid-contract.md)
and one host solver. `Common/RingFluidParameters.hlsl` owns the ring controls and
defaults. Each MandelFluid scene includes `Common/RingEmitter.hlsl` and
`Common/RingFluidMaterial.hlsl`, which defines its boundary and material passes
and explicitly includes the shared `Common/FluidSimulation.hlsl` numerical code.
There is no `RING_FLUID` capability or host source-file selection.

The scene defaults to a 512x512 grid. FluidOptions Width/Height exposes independent
power-of-two axes; see [resource sizes](resource-sizes.md).
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
- FluidOptions Width/Height changes grid dimensions and recreates simulation state.

The older half-life, diffusion, background and flow controls are removed from
MandelFluid v1-v3. Flight scenes use the same numerical backend with a single
RGBA material attachment, no optical-flow carrier/forcing, and their own
`Flight.HalfLife` decay in HLSL. Ring age/frequency is not a host requirement.

GPU regressions compare the older variants against v4 with identical inputs,
including viscosity, curl and a 165 Hz display. Other checks cover component
separation, projection, periodic timestamp/frequency transport, v4 cadence and
filament preservation, and natural decay to black after music stops.
