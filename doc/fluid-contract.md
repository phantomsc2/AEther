# Fluid scenes in plain HLSL

All six fluid scenes use one host backend, `FluidSimulation`, and the shared
velocity/pressure passes in `Shaders/Common/FluidSimulation.hlsl`. The host does
not select a solver by scene name, macro or source-text search. No manifest is
required.

The final `PS` requests fluid support by using `FluidVelocity` or one of
`FluidMaterial0` through `FluidMaterial7`. These are compiled resource bindings:
an unused declaration that the compiler removes does not activate simulation.
Scenes include the shared solver explicitly after declaring their parameters.

## Host settings

Declare a struct named `Fluid` inside `cbuffer Parameters`, containing:

```hlsl
struct FluidSettings
{
    bool PeriodicBoundary;
    bool LowQuality;
    float Viscosity;
};
cbuffer Parameters
{
    FluidSettings Fluid = { true, false, 0 };
};
#include "../Common/FluidSimulation.hlsl"
```

The variable name and these three member names/types are reserved; the struct
type name is free. Additional members are scene-defined. The host validates and
binds the required fields during compilation. It uses the final pixel shader's
parameter buffer, or a fluid pass's reflected buffer when the final shader does
not use `Parameters`.

- `PeriodicBoundary` selects periodic addressing instead of the existing wall/
  open-material-edge behavior. Changing it preserves simulation state.
- `LowQuality` selects a 128x128 rather than 512x512 grid. Changing grid size
  recreates simulation state. Both grids are square, independent of aspect ratio.
- `Viscosity` is normalized; shared HLSL maps it to `0.1 * value^3`. Zero skips
  the two diffusion draws.

Velocity and pressure advance at 60 Hz with three multigrid V-cycles and 16
coarsest-grid iterations. Two initial velocity steps establish flow on the first
advance after initialization or clearing, independently of audio. Catchup is bounded to 50 ms; material normally
updates once per displayed frame. These are the existing v4 policies, now shared
by every fluid scene.

## Scene-defined velocity boundary

The shared solver calls:

```hlsl
float2 FluidApplyBoundary(int2 cell, float2 projectedVelocity);
```

Implement this function in scene HLSL. It runs after the pressure correction and
returns the velocity to store. Velocity components live on the right/bottom
faces of each cell. `World((cell + .5) / GridSize)` gives the cell center;
offset by half `CellSize.x/y` to evaluate its component faces.

`ProjectedVelocity(cell)` can sample neighboring pressure-corrected velocities.
The ring implementation uses it to preserve tangential/outward flow while
removing inward reentry around its inlet. Returning `projectedVelocity` leaves
the shared solver's flow unchanged.

There is no separate force or optical-flow carrier contract. A flow-only scene
can sample `Texture2D<float2> FluidVelocity` with `FluidSampler` and omit material
passes entirely.

## Shader-defined material

When the final shader samples material, implement two pixel entry points:

```hlsl
struct Material
{
    float4 Color : SV_Target0;
    float2 Extra : SV_Target1;
};

Material PredictMaterial(PSin input);
Material UpdateMaterial(PSin input);
```

The field and struct names have no host meaning. Both functions must expose the
same contiguous floating-point outputs `SV_Target0..N`, with one to eight
attachments. Scalar and float2 outputs allocate R32F and RG32F; float3/float4
allocate RGBA32F, with float3 padded to four storage channels. Prediction and
update output widths must match. All storage is FP32, preserving v4 precision.

The host allocates current, predicted and next textures from this reflected
signature. Each display update executes prediction, then update, then swaps the
current and next textures. It does not clamp, fade, mix or otherwise interpret
the material channels.

Bindings are positional by output semantic:

| Binding | Meaning |
| --- | --- |
| `FluidMaterialN` | Current material attachment N; also available to final `PS` |
| `FluidPredictedN` | Prediction output N, available to `UpdateMaterial` |
| `FluidVelocity` | Current velocity field |
| `FluidSampler` | Published material/velocity sampler respecting boundary mode |
| `ClampFluid` | Same transport sampler inside simulation passes |

Declare these textures with the appropriate HLSL component types. Prediction
must not read `FluidPredictedN`, since it is writing those textures. Final `PS`
cannot access private prediction/pressure resources.

Shared HLSL supplies `Backtrace(uv)` and `Trace(uv, dt)`, using midpoint tracing
and reconstruction of the staggered velocity field. A scene can use these to
transport all payload fields together, or implement another material update.
`StepSeconds` is the material timestep, `CatchupSeconds` reports elapsed time
beyond the bounded simulation step, and `SourceAge` is the historical substep
offset in normalized history-texture coordinates. These values carry no
material lifetime policy.

For example, a simple scene could sample its current attachments at
`Backtrace(input.UV)` in `PredictMaterial`, then apply its own emission and
exponential fading to the predicted values in `UpdateMaterial`. Another could
transport signed scalar concentrations with no decay. Velocity never reads the
material attachments in the shared numerical solver.

## Existing scenes

`Common/RingFluidMaterial.hlsl` supplies the ring's boundary and its own material
struct/passes for MandelFluid and v2/v3/v4. It preserves v4's pigment/density,
density-weighted birth/frequency, capacity, inlet refresh and history-based decay.
These are shader implementation details, not engine contracts.

`Common/FluidFlight.hlsl` supplies one RGBA material attachment and a boundary
that leaves velocity unchanged. It retains the procedural foreground and local
fading afterglow. The old camera-flow carrier, distributed turbulence,
vorticity confinement, material diffusion/dilution, material-cutting sink and
edge sink are removed. `Flight.HalfLife` controls its shader-defined decay;
`Flight.Flow` retains its role in procedural projection. Flight defaults to the
128 grid and can select 512 with the same `Fluid.LowQuality` control.

Material lifetime and display illumination are independent. The ring scenes
retain v4's decay; other payloads can persist and choose their own lighting.
The contract fixtures demonstrate persistent signed material with an
audio-gated display, without assigning a lifetime rule to its stored channels.

## Validation

GPU contract tests cover all six scenes, differing material attachment counts,
signed scalar/vector payloads with shader-defined evolution, flow without
material, and failed layout reloads retaining the working scene. Existing
regressions cover pressure/curl, boundary wrapping, material attributes, cadence,
viscosity, extreme input, reload/reset and silence.

The 1280x720 deterministic smoke fixture produced identical v4 pigment,
birth/frequency and velocity hashes and an identical PNG before and after the
extraction. Baseline artifacts are under `artifacts/fluid-unification-baseline`;
`dotnet run --project tools/AEther.Smoke -c Release -- . --fluid-only` produces
fresh state hashes and captures under `tools/AEther.Smoke/artifacts`.

Before the concurrent test-fixture cleanup, the full Release shader fixture
completed with 169/171 passing checks. Its two
high-gain active-fixture failures in the non-fluid FilamentFlight and FoldFlight
scenes reproduce in the original code and are intentionally left unchanged.
All fluid silence/numerical/contract checks passed; the final focused run also
passed all 18 checks. Results are under `artifacts/fluid-unification-tests`.
