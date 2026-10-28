# Current scene dependencies and plain HLSL contracts

Historical assessment before the fluid extraction. The resulting unified fluid
implementation is documented in [the current fluid contract](fluid-contract.md).
In particular, the two fluid profiles and host-owned dye/age meanings described
below have since been replaced by one solver and shader-defined material outputs.

Assessment of the source tree on 2026-10-28. This is a source review, not a
runtime or performance measurement. No renderer behavior changes are proposed as
part of this assessment. Contract names below are illustrative unless explicitly
identified as existing names.

## Findings

There are 16 discovered scene files: nine use the shared fullscreen interface,
six additionally use the fluid backend, and one uses raster/airlight transport.
The renderer generally selects capabilities by reflected resources, not scene
filenames. Most dependencies are already implicit contracts; the main work is
separating reusable backend requirements from artistic assumptions.

Plain HLSL is sufficient for the next steps. Resource names can request a backend;
entry-point names can supply its passes; reserved parameter fields can configure
host work. A separate manifest or arbitrary pass graph is not necessary for the
current scenes.

## Scene inventory

"Baseline" means the engine supplies the fullscreen triangle, frame/audio/history
inputs, reflected editable parameters, output conversion and scene lifecycle.
It does not mean the scene is independent of host-computed musical state.

| Scene | Work beyond baseline | Existing dependency | Contract extraction difficulty |
| --- | --- | --- | --- |
| Debug | Secondary output when embedded in the UI | UI explicitly selects `Debug`; shader itself is ordinary fullscreen HLSL | Low; keep the UI integration separate |
| Spectrum | None | Baseline | Already covered |
| Histogram | None | Baseline, including compatibility histogram aliases | Already covered |
| Mandelbox | None | Baseline and shared fractal HLSL | Already covered |
| FilamentFlight | None | Baseline and shared flight HLSL | Already covered |
| FoldFlight | None | Baseline and shared flight HLSL | Already covered |
| SpectrumRoad | None | Baseline and shared flight HLSL | Already covered |
| MandelLightv2 | None | Volume integration entirely inside `PS`; audio-independent prototype | Already covered |
| MandelVolume | None | Volume integration entirely inside `PS`; audio-independent prototype | Already covered |
| FluidFlight | Fluid simulation | `FluidDye`, legacy flight-fluid profile, flight source/carrier/force functions | Low to medium |
| PlaneFlight | Fluid simulation | Same backend as FluidFlight; projection differs in HLSL | Low to medium |
| MandelFluid | Fluid simulation | `FluidDye`, ring-fluid profile | Low to medium |
| MandelFluidv2 | Fluid simulation | Same ring-fluid backend; scene-specific display HLSL | Low to medium |
| MandelFluidv3 | Fluid simulation | Same ring-fluid backend; scene-specific display HLSL | Low to medium |
| MandelFluidv4 | Fluid simulation and transported metadata | `FluidDye`, `FluidAge`, same ring-fluid backend | Low to medium |
| MandelLightv4 | Geometry/shadow draws, CPU lights, LUT preparation, airlight target/pass | `RasterSurface` and fixed raster entry points; many parameter and slot assumptions | Medium for drawing; high for fully shader-defined lights with existing optimizations |

All four MandelFluid versions include `RingFluidParameters.hlsl`, which defines
`RING_FLUID`. Their names do not select historical solver versions. All four
currently receive the ring backend's FP32 material, extra metadata, corrected
advection and scheduling, whether or not their final pixel shader samples age.

Includes in `Shaders/Common` are another dependency of exchanged presets. A
single top-level file is not necessarily a self-contained file today. Preserve
documented engine HLSL includes as a supported interface; privately edited
includes would still need to travel with a preset or be flattened on export.

## Shared interface: mostly reusable already

Sources: [Inputs.hlsl](../AEther.Vortice/Shaders/Common/Inputs.hlsl),
[ShaderParameters.cs](../AEther.Vortice/ShaderParameters.cs),
[ShaderProgram.cs](../AEther.Vortice/ShaderProgram.cs),
[Renderer.cs](../AEther.Vortice/Renderer.cs).

- `PS(PSin)` is the required final pixel entry point. The host supplies a
  fullscreen triangle and the presentation path.
- `FrameConstants`, `Spectrum0/1`, `Histogram0/1` and `Linear` are shared bindings.
- `Parameters` provides defaults, UI controls and persistent settings through
  reflection. Supported values are normalized floats, float vectors, bools and
  nested structs. Integers, matrices and arrays are not supported in this UI
  buffer. This restriction does not apply to all engine-owned buffers.
- `FilteredHistogram0/1` are compatibility names for the same raw history views;
  there is no separate filtering pipeline behind them.
- Musical clocks and envelopes are calculated on the host. `ShapeTime`,
  `DistortionMomentum`, `MusicalMovementTime`, `S`, `Pose*`, `Visual*` and
  `FractalDepth` carry behavior beyond raw frame timing/audio transport.
- In particular, `MusicalMovementTime` is integrated differently when the active
  scene has fluid support. This is a hidden coupling between a rendering
  capability and artistic motion, even for the otherwise ordinary Mandelbox
  and flight shaders.

First document these semantics and retain compatibility. Moving every musical
clock into per-scene GPU state would be a separate behavior change, not a
prerequisite for extracting the rendering contracts.

The final shader currently uses display-encoded RGB; presentation decodes it for
scRGB. Any later separation of lighting passes must specify intermediate color
encoding rather than silently changing this existing interface.

## Fluid: a small public contract around substantial reusable work

Original sources: `MandelFluidSimulation.cs`, `FluidSimulation.hlsl` and
`RingFluidSimulation.hlsl`. Their replacements are
[FluidSimulation.cs](../AEther.Vortice/FluidSimulation.cs),
[the shared numerical HLSL](../AEther.Vortice/Shaders/Common/FluidSimulation.hlsl)
and [the ring material HLSL](../AEther.Vortice/Shaders/Common/RingFluidMaterial.hlsl).

### Existing activation and host controls

The final pixel program's reflected use of `FluidDye` requests fluid support.
The host then appends one of two solver HLSL files to the scene source and
compiles a fixed family of pass entry points.

The only scene parameter paths read directly by fluid-related production C# are:

| Existing path | Host decision |
| --- | --- |
| `Fluid.LowQuality` | Ring grid is 128 square instead of 512 square |
| `Fluid.PeriodicBoundary` | Transport/pressure sampler addressing |
| `Fluid.Viscosity` | Whether to submit the two ring velocity diffusion passes |

Other artistic controls, including emitter radius, injection, pigment, forcing,
decay and illumination, are consumed in HLSL. They do not each require C# support.
The legacy solver already calls scene-provided `FlightFluidSource`,
`FlightCarrier` and `FlightTurbulence` functions: these are useful existing hook
contracts, although their names and parameter references are flight-specific.

### What should remain in the backend

Resource formats, allocation, multigrid levels and V-cycles, ping-pong, MRT
binding, a bounded fixed-step accumulator, material updates at display cadence,
warmup, stall handling and clears are legitimate engine responsibilities.
The implementation file is 513 lines including GPU validation helpers; that is
not 513 lines of scene-specific behavior that contracts would eliminate.

### What should become explicit

- Validate and bind the three reserved fields once when compiling the scene,
  rather than repeatedly searching parameter trees. The current reads have
  inconsistent missing-field behavior and do not centrally enforce types.
- Replace the conceptual `RingFluid` switch with explicit solver-profile
  requirements. Today one flag bundles numerical scheme, grid shape, material
  precision, metadata, coarse solve iterations, warmup and cadence. These are
  not intrinsic properties of a ring emitter.
- Stop detecting that switch with raw `source.Contains("#define RING_FLUID")`.
  This is source-text inspection, not HLSL preprocessing or reflection. Comments
  and inactive preprocessor branches can mislead it.
- Let scene HLSL include its solver entry-point implementation explicitly,
  after its hooks and parameters. The host should compile a documented pass
  family rather than choose and append a particular source file. Preserve both
  existing profiles until their numerical differences are addressed deliberately.
- Give the source/material/boundary operations neutral hook names when extracting
  the shared HLSL solver. Preserve the ring inlet's staggered-face and post-pressure
  behavior; it is not interchangeable with adding an arbitrary force.
- Move lifecycle and grid decisions out of `Renderer.DrawCanvas` into the fluid
  backend contract. Removing renderer branches is useful even where algorithm
  code stays unchanged.

This is low-to-medium difficulty because the passes, resources and shader-side
artistic behavior are already separated reasonably well. Generalizing both
numerical profiles into one solver is a larger and unnecessary first step.

## MandelLightv4: several different contracts are mixed together

Sources: [RasterLightTransport.cs](../AEther.Vortice/RasterLightTransport.cs),
[MandelLightv4.hlsl](../AEther.Vortice/Shaders/Scenes/MandelLightv4.hlsl),
[UnshadowedAirlightLookup.cs](../AEther.Vortice/UnshadowedAirlightLookup.cs).

`RasterSurface` in the final pixel program requests raster support. The host
compiles `RasterGeometryVS`, `RasterSurfacePS` and `RasterAirlightPS` from the
same scene. This is already a useful plain HLSL entry-point contract.

### Geometry submission: medium difficulty

The cubes are generated entirely in HLSL using `SV_VertexID` and
`SV_InstanceID`. No cube mesh is generated or uploaded by C#. The host hardcodes
triangle-list draws with 36 vertices and an instance count derived from
`Shape.Enabled` and `Shape.Count`.

A general procedural-draw contract needs vertex count, instance count, topology,
shader stages and target/depth policy. The current scene only needs a small
subset. Reserved normalized draw controls can work within the existing
parameter UI; actual integer counts would require separate configuration
reflection or extending the UI's supported types.

The host does need to allocate targets and issue draw calls. Imported meshes
would additionally require asset loading and vertex/index buffers, but those
are not prerequisites for the current procedural geometry.

Vertex binding also needs attention: the vertex shader is not independently
reflected. Pixel-reflected constants are copied to vertex slots, and frame/pass/
light buffers additionally rely on hardcoded slots 0, 2 and 3. The scene pins
registers accordingly. Reflecting the vertex stage independently would remove
these slot assumptions.

### Lights: the largest artistic dependency

`UpdateLights` implements two specific spectral rings and one white filament
ring in C#: placement planes, radius, frequency/stereo mapping, palette, S/T
selection, normalized gain mappings, enable controls and angular profiles.
It also packs active indices, shadow projection frames and texture-array slices.
The first group of operations describes the artwork; the second group is
rendering infrastructure.

The complete raster host reads 27 distinct parameter paths: six in `Shape`,
seven in `Lights`, five in `TonalLights`, one in `RingLight`, three in `Air`, two
in `Quality` and three in `View`. Merely documenting all 27 as mandatory names
would preserve the current scene design rather than provide a broadly reusable
raster contract.

A reusable spectral-ring parameter contract is a moderate first extraction if
the rings remain host-generated. Fully arbitrary `GetLight(index)` HLSL is a
larger step: the CPU currently needs light activity and geometry to omit draws,
allocate shadow slices and validate cached shadows. Reflection cannot evaluate
an HLSL function or its runtime expressions on the CPU. Moving this generation
to HLSL needs a GPU-resident light data path with conservative host scheduling,
or explicit readback and its synchronization cost. Renaming a function alone
does not solve this boundary.

### Shadow caching: medium difficulty

Cache keys name `Shape.Count/Seed/Size/Spread/Speed` and include wall time when
speed is nonzero, as well as the generated light position/cone/projection data.
A reusable contract needs geometry invalidation and dynamic/static policy.
Conservatively refreshing on parameter edits and every frame for dynamic
geometry is simpler; finer invalidation can be added explicitly. Preserve the
rule that an active shadowed light receives a valid map before use.

### Airlight/LUT: medium difficulty

Target allocation, half resolution, depth/shadow resources and lookup caching
are reusable. C# currently duplicates the shader's mapping from normalized
`Air.Density/Scattering/Absorption` to physical coefficients so it can prepare
the LUT. It also knows `View.ShowDepth/ShowShadows` to skip integration.

The LUT class is 137 lines and already takes physical extinction rather than
scene parameter names; it is a useful existing separation. Give its caller a
documented medium configuration and move diagnostic scheduling behind the
backend interface. Keep CPU/GPU coefficient mappings identical, or explicitly
change where the LUT is generated. HLSL getters alone cannot supply CPU keys.

The raster implementation is 425 lines overall. Extracting contracts should
remove its knowledge of this scene's artistic layout, not remove the resource
management and drawing that make multipass rendering possible.

## Practical limits of plain HLSL discovery

- Reflected resource usage is a suitable request for the existing single fluid
  or raster instance. Validate resource types and dimensions as well as names.
- Unused resources can disappear during compilation. A declaration alone must
  not promise execution; discovery must follow the documented compiled entry
  points and their dependencies.
- `ShaderScene.Parameters` currently comes only from the final `PS`. Secondary
  passes have their own reflected layouts and copy matching values from it.
  Controls used only in secondary stages need deliberate coverage; unioning and
  validating parameter schemas is a separate concern from binding registers.
- Host-only controls must have an explicitly reflected source. Plain `static
  const` declarations, arbitrary helper functions and macros do not become a
  general CPU configuration API through resource reflection.
- An HLSL include can package common functions and entry points without a .NET
  rebuild. It cannot introduce a new host scheduling algorithm by itself.
- The current renderer runs raster preparation/airlight before fluid, then the
  final pixel pass. These fixed contracts will not yet support arbitrary
  fluid-to-volume dependencies or multiple independent fluid instances.

## Suggested order

1. Formalize the existing fluid resource/parameter/pass contract. Centralize
   validation and lifecycle, preserve the two numerical profiles, and remove
   fluid-specific parameter reads from the renderer. No JSON or graph required.
2. Move solver inclusion into HLSL and replace raw source-text profile detection
   with a deliberate compiled contract. Then extract neutral HLSL source and
   boundary hooks without changing transport behavior.
3. Extract procedural geometry submission and independent vertex reflection.
   This removes cube draw-count/slot assumptions before tackling lighting.
4. Separate reusable spectral-ring generation, generic shadow management and
   homogeneous-medium configuration. Decide separately whether GPU-generated
   lights justify changing scheduling and cache behavior.
5. Introduce more general composition only when a concrete scene needs a
   dependency the fixed contracts cannot express.

Retain shader reload/value restoration, resize and audio-restart behavior.
Existing GPU regressions cover silence after active input, fluid projection and
curl, cadence, material/age transport, shadow invalidation and lookup agreement.
Keep strict silence checks after generous decay across multiple final frames;
only MandelVolume and MandelLightv2 have the documented audio-independent
prototype exception. Any performance comparison must use at least 1280x720.

The silence fixture also hardcodes the six fluid scene names to select boundary
variants. Once capability discovery is formalized, that classification can use
the contract while preserving the intentional prototype exceptions. No tests
were run for this assessment because no runtime source changed.
