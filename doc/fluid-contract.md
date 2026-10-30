# Fluid scenes in plain HLSL

A C# scene definition explicitly constructs a `FluidEffect` with grid defaults and
material attachment formats. Each instance owns a `FluidSimulation`, its programs,
parameters and storage. Shared numerical passes live in
`Shaders/Common/FluidSimulation.hlsl`; scenes include these after their parameters.
Referencing a fluid texture does not activate a backend automatically.

## Host settings

Declare a struct named `Fluid` inside `cbuffer Parameters`, containing:

```hlsl
struct FluidSettings
{
    bool PeriodicBoundary;
    float Viscosity;
};
cbuffer Parameters
{
    FluidSettings Fluid = { true, 0 };
};
#include "../Common/FluidSimulation.hlsl"
```

The variable name and these two member names/types are reserved; the struct
type name is free. Additional members are scene-defined. The host validates and
binds the required fields during compilation. It uses the final pixel shader's
parameter buffer, or a fluid pass's reflected buffer when the final shader does
not use `Parameters`.

- `PeriodicBoundary` selects periodic addressing instead of the existing wall/
  open-material-edge behavior. Changing it preserves simulation state.
- FluidOptions Width/Height selects independent power-of-two axes (16..2048).
  MandelFluid defaults to 512x512. Grid edits recreate simulation state.
  See [resource sizes](resource-sizes.md).
- `Viscosity` is normalized; shared HLSL maps it to `0.1 * value^3`. Zero skips
  the two diffusion draws.

Velocity and pressure advance at 60 Hz with three multigrid V-cycles and 16
coarsest-grid iterations. Two initial velocity steps establish flow on the first
advance after initialization or clearing, independently of audio. Catchup is bounded to 50 ms; material normally
updates once per displayed frame. These policies are shared by every fluid scene.

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

When C# setup declares material attachments, implement two pixel entry points:

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
attachments. C# declares R32F for scalar, RG32F for float2, and RGBA32F for
float3/float4, with float3 padded to four storage channels. Prediction and
update output widths must match. All storage is FP32, preserving v4 precision.

The host validates both shader signatures against explicit C# formats, then
allocates current, predicted and next textures from that configuration. Each display update executes prediction, then update, then swaps the
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
128 grid and can select 512 with the FluidOptions Width/Height controls.

Material lifetime and display illumination are independent. The ring scenes
retain v4's decay; other payloads can persist and choose their own lighting.
The contract fixtures demonstrate persistent signed material with an
audio-gated display, without assigning a lifetime rule to its stored channels.

## Validation

GPU contract tests cover retained scenes, differing material attachment counts,
signed scalar/vector payloads with shader-defined evolution, flow without
material, and failed layout reloads retaining the working scene. Existing
regressions cover pressure/curl, boundary wrapping, material attributes, cadence,
viscosity, extreme input, reload/reset and silence.

The 1280x720 smoke fixture checks shared material/velocity state, projection,
silence and resize for MandelFluid:
`dotnet run --project tools/AEther.Smoke -c Release -- . --fluid-only`.
State hashes and captures go to `tools/AEther.Smoke/artifacts`.
