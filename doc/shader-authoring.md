# Shader authoring

Each top-level `AEther.Vortice/Shaders/Scenes/*.hlsl` file is one scene. Its filename
without the extension is its identity and display name. Shared HLSL lives in
`Shaders/Common`; it is not discovered as scenes. Source includes use quoted paths
relative to the including file. `Common/Inputs.hlsl` is supplied automatically and
can also be explicitly included (it has an include guard).

The IDE source tree is preferred when launching in the repository. Deployed builds
copy the same directory structure beside the executable. The recursive watcher
debounces saves, creation, renames and deletion by 250 ms. No .NET build is needed
for shader edits, parameter changes or scene discovery.

## Minimal scene

```hlsl
struct AppearanceSettings
{
    float Brightness;
    float3 Tint;
    bool Enabled;
};
cbuffer Parameters
{
    AppearanceSettings Appearance = { 0.5, float3(0.2, 0.5, 1.0), true };
};

float4 PS(PSin input) : SV_TARGET
{
    return float4(Appearance.Enabled ? Appearance.Tint * Appearance.Brightness : 0, 1);
}
```

`PS` is the required pixel entry point. `PSin` contains `Position : SV_POSITION`
and `UV : TEXCOORDS`. The host supplies the fullscreen triangle vertex shader.
Scene output follows the existing display-encoded RGB convention; presentation
decodes it for scRGB output. Alpha should be one.

Every scene must eventually turn black without music. Retained history, trails
and fluid may decay naturally; procedural backgrounds and ambient illumination
must also lose their light. `VisualBreath` is a decaying audio envelope suitable
for background illumination that should remain visible during music.

## Geometry scenes and image afterglow

`FractalEdge` demonstrates the optional mesh path. A scene declares
`#define GEOMETRY_VERTEX_COUNT 48` and supplies `GeometryVS(uint id : SV_VertexID)`.
The count must be a positive multiple of three, at most 65535. The vertex shader
generates world-space triangles and returns clip coordinates plus the attributes
consumed by `PS`. Geometry is drawn into a linear FP16 scene image with a D32
depth buffer. `Parameters` must use `register(b1)` and the shared frame constants
use b0, so the vertex and pixel stages share the same bound constant buffers.

Geometry scenes also supply `AfterglowPS(PSin input)`. This fullscreen pass reads
the completed geometry through `SceneImage`; it owns tone mapping/output encoding.
Referencing `FluidDye` there requests the shared fluid passes. The geometry image
is rendered first, then becomes the fluid source, then the fullscreen composite
runs. `Common/GeometryAfterglow.hlsl` implements this image-only afterglow for
`FractalEdge`. Its fluid does not define geometry, normals or material lighting.

`#define GEOMETRY_ADDITIVE` selects additive light compositing without depth writes,
as used by `MandelGalaxy` for translucent gas and sphere-shaded billboards.
Add `#define GEOMETRY_INSTANCE_COUNT 65536` to use one instanced draw. Instance
counts range from 1 to 1048576. `GeometryVS` can consume `SV_InstanceID` to generate
each particle procedurally. `MandelGalaxy` uses 54 vertices per instance: one
Mandelstar billboard and eight connected histogram ribbon segments. No CPU physics
or per-particle attribute uploads are needed. Vertex shaders share constant buffers;
audio textures are sampled in the pixel shader.
`Effects.Amount = 0` shows the direct geometry image without its afterglow overlay.

The new mesh path requires rebuilding/restarting the application once. Subsequent
geometry shader edits, vertex-count changes and parameter changes use normal hot reload.

## Parameter contract

- Only `cbuffer Parameters` is exposed as editable state; it is optional.
- Scalars `float` and `bool`, vectors `float2/3/4`, and nested structs are supported.
- Every float, including defaults and vector components, has domain [0, 1]. Map it
  to physical units inside HLSL. Bools use checkboxes. Vector components have numeric
  controls; vectors imply no color or other special meaning.
- Integers, matrices and arrays in `Parameters` are rejected with diagnostics.
- Struct members become nested groups. Names are shown unchanged. Struct members
  are sorted alphabetically for display; declaration order carries no identity.
- HLSL initializers supply defaults. Missing defaults are zero. Reset restores the
  current source defaults; changing a default alone does not overwrite an edited value.
- Filename plus member path identifies a value. Compatible values survive reordered
  declarations and changed register assignments. Type/shape changes reset the affected
  subtree; new members receive their defaults. Renames create new identities.
- UI state survives capture-session restarts within the app, but is not persisted to disk.

No comments or annotations are parsed for metadata. Reflection supplies names, types,
defaults, offsets and bindings. Byte offsets are used only to serialize values into
the compiled layout. No author-assigned `register` or `packoffset` is required.

## Shared inputs and capabilities

`FrameConstants` supplies wall time `T`, frame delta `DT`, musical time `S`,
`Resolution`, `AspectRatio`, history coordinates, the existing audio feature values
and the unscaled musical movement clock `MusicalMovementTime`.
`Spectrum0/1` and `Histogram0/1` supply stereo smoothed STN in RGB and original linear magnitude in alpha; `Linear`
is the shared sampler. See `Common/Inputs.hlsl` for the complete declarations.

Resources are bound by reflected name independently for every entry point. Unknown
resources, resource arrays and unsupported parameter types fail reload before any
working program is replaced. Engine inputs use reserved names and must retain their
declared types and layouts.

Bilateral smoothing is part of the core splitter. `FilteredHistogram0/1` remain
compatibility aliases for `Histogram0/1` and do not request a GPU filter pass.
`ResidualHistogram0/1` and `ResidualSpectrum0/1` provide signed ungained linear
residual in red; the other channels are zero. At unity class gains, individual
history rows satisfy sum(STN) + R = original magnitude. Spectrum peak overviews
are not additive. Histogram's `ResidualView` shows positive R in red and negative R
in blue. Smoothing controls live in AnalyzerOptions, independently of scene controls.
Referencing `FluidDye` requests the existing fluid simulation;
`FluidSampler` samples it. The simulation's shared HLSL uses the `Fluid` and `Quality`
structures declared in `Scenes/MandelFluid.hlsl`. These are contracts of shared shader
code, not host interpretations of editable parameter names. Arbitrary simulation pass
schedules are not configurable in this version.

## Existing scene mappings

The HLSL source is authoritative. Current normalized controls map to these units:

| Parameter | Mapping |
|---|---|
| `Geometry.Scale` | -4 to 4; default -1.5 |
| `Geometry.Anisotropy` | 0 to 0.5 |
| `Geometry.SliceDepth` | -2 to 2 |
| `Quality.MaximumIterations` | nearest integer from 1 to 30; default 10 |
| `Quality.Antialiasing` | boolean checkbox: false uses one sample; true uses 2x2 |
| `Quality.MovementSpeed` | 0 to 10; default 3 |
| `Fluid.Flow`, `Noise` | 0 to 3 |
| `Fluid.HalfLife` | 0.1 to 10 seconds |
| `Fluid.Diffusion` | 0 to 0.01 |
| `Fluid.Curl` | 0 to 2 |

Other float controls use their normalized value directly. Movement speed currently
scales the shared musical phase in HLSL: changing it can shift the pose, and zero
selects a stationary phase. This differs from the previous CPU integration that
preserved pose when changing speed.

## Reload and UI behavior

The Scene selector in settings automatically opens the owned shader window when a scene
is selected. The shader window contains the current parameter tree, Reload, Reset defaults
and diagnostics. Closing hides it; F6 toggles it from settings or the render window
and hides it when the shader window has focus.
Scene selection and controls operate on the existing renderer without restarting audio.

A reload builds a complete candidate set before swapping programs, parameter layouts,
bindings and UI. Failure preserves the previous set. Removing the active scene selects
the alphabetically first remaining scene; removing every scene rejects the reload.
Compatible fluid state survives reloads; scene changes and resize reset it.

Compilation currently runs on the window thread and rebuilds all discovered scenes,
so saving can briefly pause drawing. Capture and analysis continue independently.
Includes are expanded from quoted include directives, rather than implementing a
full preprocessor-aware include resolver; avoid missing includes in inactive branches.

## Verification

The GPU silence regression discovers every scene, verifies silent startup and
visible musical excitation, then keeps rendering the same scene for 10,000
frames at 60 Hz (166.7 seconds of simulated silence). It tests both stopped
capture and continuing zero-valued audio packets. Each of the following 60
frames must have every RGB channel exactly zero in undithered RGBA8 output.
No scene changes, simulation clears or large time steps shortcut the decay.
The injected `TimeProvider` advances the production audio and rendering paths
without wall-clock sleeps. This test requires Windows and a D3D11 GPU.

```powershell
dotnet test AEther.Tests/AEther.Tests.csproj --filter FullyQualifiedName~ShaderSilenceTests
```

```powershell
dotnet build AEther.sln --no-restore
dotnet run --project tools/AEther.Smoke -- . --shader-authoring-only
```

The focused smoke check renders all four scenes with synthetic audio, checks reflected
struct/vector/bool defaults and uploads, changes slots and declaration order, exercises
add/remove/reset and failed-reload rollback, and verifies watcher discovery and shader
window controls without restarting capture. PNGs go to the ignored smoke artifacts folder.
Historical specialized smoke modes still contain fixtures for the previous shader
layout and require migration before use with this authoring system.
