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

Procedural triangle scenes can use the [geometry contract](geometry-contract.md):
HLSL supplies vertex/instance counts and evaluates vertices, and the host executes
one indirect instanced draw. The [RasterLight contract](raster-airlight-contract.md)
supplies point-light surfaces and precalculated homogeneous single-scattering
airlight, with shader-emitted position/intensity records. MandelLightv5 uses this
single raster backend.

Perspective scenes share the [HLSL projection contract](projection-contract.md)
for shader-owned cameras, vertical field of view, near/far depth mapping, rays,
and world-position reconstruction. Infinite homogeneous airlight remains separate
from finite camera clipping.

## Debug analysis scene

`Debug` combines the presented analysis block and its stereo history on shared
frequency rows (low at the bottom, high at the top). The central 30% contains
mirrored live bars: left channel grows left, right channel grows right. Each
outer 35% shows that channel's full configured history duration, scrolling outward
from the live/history seam. Horizontal distance means amplitude inside the live
region and elapsed time outside it; the two regions have separate scales.

Live S/T/B components use the existing red/green/blue convention with overlapping
bar lengths. Total amplitude (the analysis alpha channel) fills a quarter-intensity grey
bar from the center, blending behind the component bars instead of a white edge cap.
The fixed linear scale runs from zero at screen center to amplitude one at each
live/history seam; larger values clip there. `CombinedLevel`, off by default,
switches the live bars to grey total amplitude alone and history to monochrome total amplitude. These are
analysis values, not calibrated dB or acoustic loudness. There is no automatic
gain, peak hold, additional frequency smoothing, or age fading.

History integrates piecewise-constant observations over each pixel's time
footprint, preserving smooth subpixel scrolling without inventing intermediate
observations. Missing rows remain black. Live bars use the same display-interval
aggregate as every scene, retaining it until stale-data expiry.
The main hub embeds this view below Gain in its left column, exposing the
audio-to-scene dataflow beside the scene controls in the equal-width right column.
Debug contains the separate timing and status text. The Audio settings stay aligned
to the bottom of the left column, and the analysis view fills the space above them,
with thinner bands when the available height is small.
Neither the analysis view nor the overall hub scrolls. Only the expanded Audio
settings and shader settings scroll independently inside their own bounded panels.
It shares the canvas renderer's device, audio textures, history, and rendering
thread; it does not start another capture or analyzer. The selected canvas scene
and its parameters stay unchanged. The graph continues updating when the canvas is
hidden, while a hidden or minimized hub performs no analysis-view drawing.
Both outputs use their own resolution, DPI, and monitor white level. Resize,
shader reloads, and session restarts reconnect the view automatically. The
Debug scene remains available in the scene picker. The analysis preview compiles
a private Debug program using source defaults; preview parameters have no UI or
persistence and do not share canvas scene edits.
With stopped or silent audio, all scene RGB eventually turns
black as history scrolls out. Residual and smoother comparisons remain in `Spectrum`.

GPU timestamp comparison at 1280x720 on Intel Arc Graphics (128 bands, 60 analysis
rows/second, two seconds of history; synthetic stereo tones and periodic attacks):

| Scene | Mean scene time | p95 scene time |
| --- | ---: | ---: |
| Debug | 0.2204 ms | 0.2282 ms |
| Spectrum | 0.1308 ms | 0.1399 ms |
| Histogram | 0.2364 ms | 0.2399 ms |

These measure the scene GPU pass, excluding presentation and readback duration.
`AnalysisSceneGpuTimingAt720p` reproduces the comparison and saves RGBA captures
under `artifacts/debug-scene`. Its recent timing window excludes initial warmup.

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
    float4 local = .5 * (Spectrum0.SampleLevel(Linear, float2(input.UV.x, .5), 0)
        + Spectrum1.SampleLevel(Linear, float2(input.UV.x, .5), 0));
    float level = AmplitudeResponse(local.b, .01);
    return LinearOutput(Appearance.Enabled ? Appearance.Tint * Appearance.Brightness * level : 0);
}
```

`PS` is the required pixel entry point. `PSin` contains `Position : SV_POSITION`
and `UV : TEXCOORDS`. The host supplies the fullscreen triangle vertex shader.
Use `LinearOutput(illumination)` to opt into shared postprocessing. Return linear
RGB illumination, including values above one, with no final tone curve, gamma
encoding, or display clipping. Average antialiasing samples in linear illumination.
The renderer applies global **Exposure** from Graphics settings, in stops from
-12 to +12 (default zero); +1 doubles illumination and -1 halves it. Shaders do
not declare an Exposure parameter.

Use `DisplayOutput(encodedColor)` for diagnostic palettes and inline depth/shadow
views that must bypass exposure and tone mapping. Existing `float4(rgb, 1)` returns
keep this display-encoded behavior. Debug, Spectrum, and Histogram use this path.
Internally, scene-buffer alpha marks the contract: zero for linear illumination,
one for display colors. It is not transparency; final output and exports are opaque.

`Common/Postprocessing.hlsl` owns tone mapping and the sRGB transfer functions.
The existing presentation pass uses the exponential curve
`h * (1 - exp(-max(illumination, 0) * exp2(Exposure) / h))`. SDR headroom `h` is
one. Active HDR uses at least one, derived from the display peak luminance divided
by Windows reference white, preserving highlights above white. FP16 surface support
alone does not enable HDR headroom. FP16 output stays linear scRGB; SDR output is
sRGB encoded and spatially dithered. Legacy display colors are decoded for scRGB.

RGBA8 readback uses the same shader with SDR headroom and the last rendered frame's
exposure, independently of the monitor. `ReadPixels(false)` disables export dither.
Changing Exposure leaves audio history and fluid simulation intact. Former scene
exposure defaults are fixed lighting constants, with no overlapping UI controls.

Sample local S/T/B and raw
amplitude from `Spectrum0/1`, or `Histogram0/1` for historical sources at their
own age. The texture layout is `[max(S,0), max(T,0), B, A]`; alpha is not residual.
`AmplitudeResponse(x,h)` bounds a positive amplitude with sensitivity h and maps
zero to zero. There is no separate global fit or global evidence transport.

## MandelVolume prototype

`MandelVolume` is an audio-independent single-scattering experiment. Its camera
and Mandelbox stay fixed; one white point light orbits inside the
fractal using wall time `T`. A thin ambient medium fills the same bounding sphere
as the fractal density, producing a halo through single scattering around the
light. The background outside that sphere is black, with no emission,
fluid, image history, or temporal accumulation. It intentionally remains visible
without music, under the prototype exception in `AGENTS.md`.

The Mandelbox iteration multiplier and maximum iteration count are editable.
Defaults retain scale two and four iterations. The terminal orbit radius is
normalized by `max(1,abs(scale))^N`, staying finite at scale zero. Fractal density
follows one continuous Gaussian profile, rather than a periodic stack of shells:

```text
scale = 8 * Fractal.Scale - 4
N = 1 + round(15 * Fractal.MaximumIterations)
q = length(z_N) / max(1, abs(scale))^N
c = 0.5 * Density.Center
w = 0.002 + 0.198 * Density.Width^2
sigma_t = 0.2 * Density.Ambient + 8 * Density.Strength * exp(-0.5 * ((q - c) / w)^2)
sigma_s = Density.Albedo * sigma_t
```

`Center` selects a radius in the normalized fractal orbit coordinates. `Width`
is the standard deviation in the same field units; neither is an exact distance
from the Mandelbox surface or the world origin. A smooth spatial envelope fades
extinction between world radii 9.5 and 10.5. Bounds remain fixed during scale and
iteration edits; some variants can meet that spatial envelope. This is a finite
procedural density field, not a surface DE. Changing iterations can require
retuning the density center and width to select the desired field values.
Both ambient and fractal extinction contribute to camera and light transmittance.
Setting `Density.Strength` to zero isolates the ambient medium; setting both
`Strength` and `Ambient` to zero removes all scattering.

The light's horizontal orbit has radius 2.5, with vertical excursions up to 1.5.
Its inverse-square falloff is softened as `1 / (r*r + radius*radius)` to keep
illumination finite within the medium. The equiangular proposal uses the same
radius, including for rays aimed directly through the source. Small radii and
high intensities concentrate illumination and expose outward shafts as fractal
density occludes light reaching the ambient medium. These shafts are computed
through light transmittance. The light has no visible source geometry.

### Optional phase response

Phase defaults preserve the isotropic rendering: `Phase.Anisotropy = 0.5` maps
to HG asymmetry `g = 0`, and `Phase.Alignment = 0` disables oriented scattering.
Raise Alignment to blend the fractal medium toward a gradient-oriented HG lobe.
Ambient scattering retains the ordinary HG response. Both material contributions
add before applying camera and light transmittance:

```text
g = 1.6 * (Phase.Anisotropy - 0.5)
r = 0.05 + 0.95 * Phase.Roughness
n = normalize(gradient(q))
p_ambient = HG(dot(i, o), g)
p_fractal = lerp(p_ambient, HG(dot(reflect(i, n), o), 1-r), alignment)
source = albedo * (ambient_extinction * p_ambient + fractal_extinction * p_fractal)
```

Here `i` points from the light toward the sample and `o` points from the sample
toward the camera. Each lobe is normalized; Alignment redistributes the fractal
scattering over directions. At zero Roughness the actual width parameter is
still 0.05, keeping the sharpest lobe finite. Weak gradients fade Alignment back
to zero. Gradient orientation is two-sided: reversing `n` leaves reflection
unchanged.

The field gradient uses the full analytic 3x3 Jacobian of the finite Mandelbox
orbit. Its three columns propagate through box-fold reflections, the sphere
inversion Jacobian, scale, and the `+p` derivative `+I`. The final gradient is
`max(1,abs(scale))^(-N) * transpose(J) * z / length(z)`. Zero terminal radius
uses a zero gradient. No surface intersections or finite differences are used
by the rendering path. Jacobian evaluation runs only for oriented fractal
scattering; density, shadow transmittance, and the disabled phase path remain
scalar. The gradient is piecewise analytic; box/sphere fold creases can have
discontinuous gradients, which remain visible at sharp phase settings.

This is a phenomenological phase model with scalar extinction. A complete
microflake material would also make extinction direction dependent. Sharp lobes
introduce additional peaks along camera rays and may need more scattering
samples; the existing MIS proposals remain valid.

The integrator combines equiangular, density-guided, and reference free-flight
sampling with balance MIS. A 64-bin pilot table supplies both a normalized
density proposal and approximate camera optical depth. Shadow transmittance uses
midpoint integration through the same density field. These finite integrations
can miss narrow features; MIS does not remove that quadrature error.

All exposed values follow the usual [0,1] parameter contract:

| Control | Mapping |
|---|---|
| `Fractal.Scale` | Iteration multiplier -4 to 4; default 2 (slider 0.75) |
| `Fractal.MaximumIterations` | Nearest integer from 1 to 16; default 4 |
| `Density.Center` | 0 to 0.5 field units; default 0.5 |
| `Density.Width` | 0.002 to 0.2 field units, quadratic; default about 0.0568 |
| `Density.Strength` | Fractal peak extinction 0 to 8 per world unit; default 2 |
| `Density.Ambient` | Ambient extinction 0 to 0.2 per world unit, before the spatial envelope; default 0.0838 |
| `Density.Albedo` | Scattering fraction; default 1 |
| `Light.Intensity` | Point intensity 0 to 3000, quadratic; default 907.5 |
| `Light.Speed` | Orbit angular speed 0 to 0.5 radians/second; zero freezes the light |
| `Light.Radius` | Softening radius 0.01 to 0.5 world units, quadratic; default 0.0296 |
| `Phase.Anisotropy` | HG asymmetry -0.8 to 0.8; slider 0.5 is isotropic (default) |
| `Phase.Alignment` | Fractal orientation blend 0 to 1; default 0 (disabled) |
| `Phase.Roughness` | Oriented lobe width 0.05 to 1; default 0.3 |
| `Quality.Samples` | 1 to 32 samples per strategy, 3 to 96 total; default 16 per strategy |
| `Quality.TransmissionSteps` | 8 to 128 shadow steps; default 48 |

The density defaults match the supplied reference image: Center 1, Width 0.526,
Strength 0.25, Ambient 0.419, and Albedo 1. Raising the shadow step budget helps
resolve thinner occluding features. Higher iterations and narrow profiles may
need more transmission steps and scattering samples. Compatible edited values
survive hot reload; use Reset defaults to load the complete new reference preset.

Sampling is stratified with a stable per-pixel seed. Narrow profiles and low
sample counts show grain; raising quality costs GPU time.

## MandelLightv5

The scene uses GPU-generated point lights in three perpendicular STB rings,
procedural cube geometry, Lambertian surfaces and the shared airlight LUT.
See the [scene controls](mandellight-v5.md) and
[RasterGeometry/RasterLight contract](raster-airlight-contract.md).

## MandelLightv2 prototype

`MandelLightv2` is an audio-independent experiment with procedural ellipsoidal
density blobs and analytical camera/light attenuation. It uses a single pixel
shader and shares its integration samples across lights and emission, with no
transport caches or surface shading. It remains visible without music under the
prototype exception. See [MandelLightv2](mandellight-v2.md) for the model, controls
and profiling instructions.

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

Fluid scenes use the [plain HLSL fluid contract](fluid-contract.md): reserved
`Fluid.PeriodicBoundary`, `Fluid.LowQuality` and `Fluid.Viscosity` controls,
a scene-defined velocity boundary, and optional material structs reflected from
matching `PredictMaterial`/`UpdateMaterial` output signatures. All six scenes
share one flow solver. Material meanings, emission and decay belong to HLSL.
The four MandelFluid variants retain the [v4 ring material](ring-fluid.md);
flight scenes use one material attachment and their own half-life rule.

## Shared inputs and capabilities

`FrameConstants` supplies wall time `T`, frame delta `DT`, musical time `S`,
`Resolution`, `AspectRatio`, history coordinates, the existing audio feature values
and the unscaled musical movement clock `MusicalMovementTime`.
`Spectrum0/1` and `Histogram0/1` supply [positive S, positive T, background B,
splitter input amplitude] in RGBA, in linear amplitude units. The splitter input
is the selected Kalman smoother's output. `Linear` is the shared sampler.
See [smoother models and comparison](kalman-smoothing.md),
[splitter semantics](splitter-design.md), and `Common/Inputs.hlsl` for details.

Resources are bound by reflected name independently for every entry point. Unknown
resources, resource arrays and unsupported parameter types fail reload before any
working program is replaced. Engine inputs use reserved names and must retain their
declared types and layouts.

The preprocessing stage compares log-amplitude and complex-resonator Kalman
models with explicit acceleration-noise intensities and fixed Cartesian noise
RMS. Both return linear magnitudes; neither exports velocity or phase. The
original splitter neighborhood settings are active. Spectrum's `CompareSmoothers` view uses diagnostic row 1:
[residual RMS, raw magnitude, mode A magnitude, mode B magnitude], aggregated
over the same display interval using the same Mean/RMS/Max setting.
It displays A left/red and B right/cyan using the same stereo average and linear
scale. `FilteredHistogram0/1` remain aliases for `Histogram0/1`.

Using `FluidVelocity` or `FluidMaterial0..7` in the final program requests the
shared fluid backend; `FluidSampler` samples the published textures. Material
attachments come from the reflected HLSL output struct, with no engine-defined
dye/age channels. Ring emission, capacity and weighted birth/frequency live in
`Common/RingFluidMaterial.hlsl`. See [the fluid contract](fluid-contract.md).

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
| `Fluid.EmissionVelocity` | 0 to 20, scaled by local B |
| `Emitter.Rate` | 0 to 200 density units per second, scaled by local S |
| `Fluid.Viscosity` | `0.1 * value^3`, no dye diffusion |
| `Fluid.Curl` | 0 to 20, scaled by local B and angular variation |

Other float controls use their normalized value directly. Movement speed currently
scales the shared musical phase in HLSL: changing it can shift the pose, and zero
selects a stationary phase. This differs from the previous CPU integration that
preserved pose when changing speed.

## Mapping normalized controls

Float controls stay in [0, 1], including defaults and vector components. Keep
physical bounds and response curves in the scene HLSL so each scene remains
self contained. There is no separate bounds metadata.

```hlsl
float speed = lerp(-4.0, 4.0, Motion.Speed);
float radius = exp2(lerp(log2(0.01), log2(10.0), Light.Radius));
float iterations = 1.0 + round(15.0 * Quality.Iterations);
```

Use linear mapping for signed ranges, logarithmic mapping for positive values
spanning orders of magnitude, and rounding for discrete choices.

## Reload and UI behavior

The application opens a resizable hub with two equal-width columns. Input, Gain
(dB), and Analysis are pinned on the left. DFT, Pipeline, Smoother and Splitter
appear below as independently expandable blocks, all closed initially, inside one
shared bounded scroller. HistorySeconds is in the right column below the scene
selector and updates graphics history without restarting analysis; the GPU category and outer
Audio expander are removed. The unlabeled
Scene picker is pinned above HistorySeconds on the right and starts empty. Selecting a scene opens the
canvas and shows Reload, Reset defaults, the parameter tree and shader diagnostics
below the picker. Shader settings scroll independently of Audio settings; the hub
and Analysis view do not scroll. Debug remains a separate status/timing window.
Closing the hub stops the session and exits the application. An explicit
`--scene=Name` argument opts into selecting and opening a scene at startup.

F6 activates the hub and focuses the Scene picker. Escape dismisses the canvas or
hides the focused Debug window. Expanding or collapsing categories affects only layout.
Scene selection and controls operate on the existing renderer without restarting audio.
Audio edits restart analysis while retaining graphics. The untitled canvas has a
normal title bar and resizable border. Move it to the desired monitor; F11 explicitly
toggles fullscreen on that monitor. Restarts preserve its placement. Space in the
canvas activates the hub. Closing the canvas or pressing Escape clears the scene
picker. Selecting a scene, including the same one, reopens the canvas with retained
parameters and without restarting capture. Audio edits and shader reloads preserve
the empty picker while the canvas is dismissed. Minimizing a window keeps its selection.
Monitor-specific white level and DPI follow the canvas window automatically.

A reload builds a complete candidate set before swapping programs, parameter layouts,
bindings and UI. Failure preserves the previous set. Removing the active scene clears selection; an empty scene directory is supported.
Startup and reload never choose a scene implicitly.
Compatible fluid state survives reloads; scene changes and resize reset it.

Compilation currently runs on the window thread and rebuilds all discovered scenes,
so saving can briefly pause drawing. Capture and analysis continue independently.
Includes are expanded from quoted include directives, rather than implementing a
full preprocessor-aware include resolver; avoid missing includes in inactive branches.

## Verification

```powershell
dotnet build AEther.sln --no-restore
dotnet run --project tools/AEther.Smoke -- . --shader-authoring-only
dotnet run --project tools/AEther.Smoke -- . --windows-watcher-only
```

The focused smoke check renders all discovered scenes with synthetic audio, checks reflected
struct/vector/bool defaults and uploads, changes slots and declaration order, exercises
add/remove/reset and failed-reload rollback, and verifies watcher discovery and shader
window controls without restarting capture. PNGs go to the ignored smoke artifacts folder.
Historical specialized smoke modes still contain fixtures for the previous shader
layout and require migration before use with this authoring system.
