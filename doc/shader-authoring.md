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
    float level = MusicBody(GlobalSTBR);
    return float4(Appearance.Enabled ? Appearance.Tint * Appearance.Brightness * level : 0, 1);
}
```

`PS` is the required pixel entry point. `PSin` contains `Position : SV_POSITION`
and `UV : TEXCOORDS`. The host supplies the fullscreen triangle vertex shader.
Scene output follows the existing display-encoded RGB convention; presentation
decodes it for scRGB output. Alpha should be one.

Production scenes must eventually turn black without music, including Mandelbench.
Explicit audio-independent prototypes, such as MandelVolume and MandelLight, are exceptions.
Its study geometry stays static while the global controls drive exposure.
Retained history, trails
and fluid may decay naturally; procedural backgrounds and ambient illumination
must also lose their light. `MusicBody(GlobalSTBR)` provides a bounded global
background/current-level response for passage illumination. Structure, signed
rise/fall and interaction controls are described in [global STBR](global-stbr.md).
Historical sources must use controls sampled at their own age.

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
sample counts show grain; raising quality costs GPU time. The current blanket
scene discovery in `ShaderSilenceTests` includes this prototype and its silence
assertions do not apply to it. No unit-test changes accompany this prototype.

## MandelLight prototype

`MandelLight` is a separate audio-independent surface/airlight prototype, leaving
`MandelVolume` available for comparison. It uses an orbiting camera, up to four
RGB point lights and RGB scattering through a shared radial density profile.
The default enables only the original white light with equal RGB coefficients.
No ambient illumination, volume emission,
image history or audio response is present. It requires rebuilding/restarting
the application once for the new precalculation passes; subsequent shader edits reload.

### Surface and air

The Mandelbox observable `q` and its full analytic Jacobian are the same as in
MandelVolume. Geometry is the closed level set, clipped to the radius-10.5 sphere:

```text
c = 0.5 * 2^(12 * (Fractal.Potential - 1))
f(p) = max(q(p) - c, length(p) - 10.5)
solid: f < 0
surface: f = 0
```

The normal follows `gradient(q)`, or the sphere normal where the clipping sphere
defines the boundary. The field is not a distance estimator. Camera and shadow
rays use 128–1024 uniform search steps through their solid bounding interval,
then ten bisections of the first detected outside/inside crossing. Features
narrower than a search step, and tangential contacts, can be missed. Increasing
`Quality.SearchSteps` helps; this is not a certified intersection algorithm.

Potential is logarithmic across twelve octaves, from 0.0001220703125 to 0.5.
The default slider value 1 retains the original level 0.5; zero now selects the
smallest positive level rather than a degenerate zero level. Search setup computes
the potential threshold once per ray and clips via the bounding interval,
avoiding a redundant sphere-distance calculation at every step.
The normal evaluation also returns q, avoiding a second orbit at surface hits.

The opaque surface uses isotropic GGX, Smith masking and Schlick Fresnel, with
scalar normal-incidence reflectance. There is no diffuse component, environment
light, indirect reflection or multiple scattering. `Surface.Roughness` maps to
`max(0.04, value)` and GGX alpha is its square. `Surface.Reflectance` is F0, not
a diffuse albedo or a metalness blend. This is an approximate conductor appearance,
without wavelength-dependent optical constants.

Air is a prescribed isothermal, constant-gravity spherical atmosphere, independent
of the fractal potential. It uses pure scattering (sigma_s = sigma_t) and the
normalized unpolarized Rayleigh phase function:

```text
H = 0.5 + 2 * Air.ScaleHeight
sigma(r) = 0.08 * Air.Density * exp((6 - r) / H)
sigma_s_rgb(r) = sigma_t_rgb(r) = sigma(r) * Air.Scattering
phase(mu) = 3 * (1 + mu*mu) / (16*pi)
```

`mu` is the cosine between incoming and outgoing photon propagation directions.
The finite domain ends at radius 16. This truncates an exponential tail, especially
noticeable at large scale heights; this is not a hydrostatic solution in the actual
fractal gravity field. The first opaque surface ends the camera air integral.
Light visibility removes contributions whose light path crosses the solid. Air
can scatter in empty cavities; there is no fractal-specific scattering branch.

`Air.Scattering` supplies independent RGB coefficients in [0,1], default (1,1,1).
The shared density still controls the number of particles; coefficients control
per-channel scattering and out-scattering. There is no absorption. Zero in one
channel removes that channel's airlight and atmospheric attenuation while allowing
surface light to pass unchanged in that channel. RGB is a three-channel transport
approximation, not full spectral rendering. Light colors are linear RGB; vector
controls use x=red, y=green and z=blue.

### Precalculation

`View.CameraSpeed` sets a constant camera orbit about world Y: 0 to 0.25 radians
per second, default 0.05 (slider 0.2). Radius and height stay fixed, and the camera
always looks at the origin. Zero freezes it at the original `(9,6,-13.5)` pose.
Camera motion rebuilds both the surface cache and the epipolar light map each frame,
including when the light itself is frozen; the atmosphere lookup remains cached.

`SurfacePS` builds a minimal G-buffer (`SurfaceCache`): camera first-hit depth and
analytic normal in an RGBA32F image, shared by every light. It is generated
at full render resolution. It rebuilds for camera motion, geometry, search-quality,
viewport, split-view or shader changes. Lighting, air and scattering sample edits
reuse it when the camera is frozen.
The camera pass reads this image instead of tracing and differentiating the same
static surface every frame. This preserves full normal/depth precision and the
existing search budget. It costs 16 bytes per display pixel; there is no temporal
accumulation or approximation from lowering cache resolution. The full-window
atmosphere view defers camera geometry work until it is needed again.

`AtmospherePS` preprocesses unit-density optical depth in a 512-by-256 R32F
lookup, using 1024 midpoint integration steps per texel. It rebuilds on shader
reload or `Air.ScaleHeight` changes, and is reused as the light moves or density,
material, geometry, RGB coefficients and quality controls change. Density and RGB
scattering coefficients multiply cached depths at runtime. The table remains scalar;
it stores outward paths from radius r to the outer sphere,
its coordinates are radius/16 and sqrt(mu), where mu is outward zenith cosine.
The square-root warp provides more resolution near grazing directions. Exact
endpoint texels and bilinear lookup avoid extrapolation at the sphere boundary.
Inward optical depths follow from full chord symmetry minus the near outward tail.

Each light's shadow map caches forward and reverse light-to-exit unit optical depths
along each light direction. A scattering or surface point requires one remaining
exit-depth lookup. Subtracting the two depths gives optical depth between light
and point. The direction with thinner exit tails is chosen to reduce cancellation
of nearly equal large depths. Shadow comparisons and smooth optical depths are
filtered separately. Near the epipolar axis, the same lookup supplies the light
endpoint while surface visibility uses its existing direct intersection fallback.
No atmospheric integration loop runs per scattering sample or surface point.

Since the camera orbit stays outside the atmosphere, camera-side optical depth is
simply the reverse exit depth at the point. The lookup uses hardware bilinear
filtering with explicit texel-center coordinates. No per-camera-ray bin table is
built. The LUT still has finite interpolation and quadrature error.

`View.ShowAtmosphereLookup` replaces all output with the raw unit-density lookup,
using logarithmic grayscale (zero depth black, maximum depth white). Radius runs
left to right; sqrt(outward zenith cosine) runs top to bottom. The table is fitted
as a 2:1 image. This view takes priority over `View.ShowShadowMap` and remains
visible with density or light intensity zero, to inspect the preprocessed data.

### Angular shadows and integration

The renderer follows three stages: shared atmosphere/G-buffer preprocessing, then
independent light-source transport and camera sampling for each active light.
Each light gets a 512-by-512 RGBA32F shadow target on demand. `ShadowPS` runs when
that light's transport inputs change; `LightPS` then integrates only that light.
The scene declares `Texture2D<float4> EpipolarShadow`;
referencing it requests this prepass, with the same reflected Parameters and
FrameConstants. A moving light or camera rebuilds it each frame. When both are
frozen it is reused until fractal geometry, search quality, scale height, light
speed, orbit phase, camera speed or shader changes.
Density, RGB coefficients, light color/intensity, material, sample count and display size do not
invalidate this unit-density light cache. The full-window atmosphere debug view
skips the light stage. It has no min–max hierarchy or temporal accumulation.

`View.ShowShadowMap` optionally places the render in the left 70% of the window
and a square shadow-map preview in the right panel. Gamma runs horizontally and
epipolar slice alpha vertically. Closer first hits appear brighter; misses are
black. The main camera aspect is adjusted for its panel. The debug view can remain
visible with light intensity zero because it displays geometry, not radiance.
`View.ShadowLight` selects the light shown: slider values 0, 1/3, 2/3 and 1
correspond to Light, Light2, Light3 and Light4. A selected disabled light still
gets a map for inspection, without contributing illumination.

The mapping follows Chen et al., [Real-Time Volumetric Shadows using 1D Min-Max
Mipmaps](https://cs.dartmouth.edu/~wjarosz/publications/chen11realtime.html): each
row is an epipolar plane containing camera and light, and columns vary the light
ray's angle gamma to the light-to-camera baseline. We generate rays directly in
these coordinates and store first-hit radial distance plus forward/reverse
light-to-exit optical depth, rather than resampling a conventional shadow map or
storing the paper's beta occluder angle. Visibility
compares sample/light distance against the cached first hit. Four comparison
results are bilinearly filtered, with periodic slice wrapping. Shadow bias is
0.002 world units for air and 0.025 for the surface. Near either baseline pole
(sine of gamma below 0.02), a direct level-set shadow query avoids the undefined
epipolar plane. Finite map resolution and bias can cause leaks, acne and temporal
aliasing around small features. Filtering is numerical antialiasing, not an area
light or physically resolved soft shadow.

Each independent light draw reads the same G-buffer, then combines two proposals
with equal-count balance MIS: equiangular
and an endpoint-fitted exponential. The latter fits the change in log density
minus representative camera optical depth (using the mean RGB coefficient) across
the ray, and can concentrate samples at either
endpoint. The exponent is limited to [-20,20] for numerical stability; near zero
it falls back to uniform sampling. Its exact normalized PDF corrects this proposal
approximation. There is no density pilot, CDF, bin search or third MIS strategy.
The full RGB integrand is evaluated at the same sampled depths. A scalar proposal
remains valid for RGB; strongly different channel coefficients can increase variance.
Each light has its own equiangular proposal and deterministic sample seed. Samples
are not shared between lights, and no all-light cross-PDF evaluations are required.
Camera and light transmission use the preprocessed atmosphere lookup; there is no
per-sample atmosphere march. All proposals
and PDFs use the camera interval truncated at the first surface. The geometric
equiangular angle theta differs from the map angle gamma by a constant per ray:

```text
gamma = theta + pi/2 - beta
```

The angular proposal uses the true perpendicular distance; near the axis it
falls back to uniform distance sampling. `Light.Radius` only softens inverse-square
falloff (`0.01 + 0.49 * value^2`), not the angular coordinates. It does not create
a visible emitter or an area light. The light orbits at horizontal radius 12,
with vertical excursion 3, always outside the bounded solid. Its initial phase
lights the camera-facing side. Speed is `0.5 * Light.Speed` radians/second;
zero freezes it. Intensity maps to `3000 * Light.Intensity^2` and is multiplied by
that light's `Color`. Each of `Light`, `Light2`, `Light3`, `Light4` has `Enabled`,
`Intensity`, `Speed`, `Radius`, `Phase` and `Color`. Phase adds 0 to 2*pi to the
orbit. Extra lights default disabled, with phase offsets and example colors.
Disabled lights, zero-intensity lights and black lights skip transport/integration
unless their map is selected for debugging. Allocated maps are retained for reuse.

Each light contributes airlight plus camera-attenuated direct surface reflection
to a shared RGBA32F radiance target using additive blending. It is cleared every
frame; there is no lighting history. `PS` reads `LightRadiance`, applies the
exponential tone map once to the total RGB radiance, then encodes for display.
FP32 accumulation avoids overflow of sharp HDR highlights before tone mapping.
It costs another 16 bytes per display pixel; each shadow map costs 4 MiB.
The host supplies a 16-byte `LightConstants` buffer selecting the light index
for `ShadowPS` and `LightPS`. These passes use one ordinary texture binding, not
resource arrays. Four light groups fit the existing parameter authoring contract.
A vacuum removes airlight but retains the lit surface; disabling all lights or
setting all intensities/colors to zero clears the image. Large optical
depths would require multiple scattering for quantitative physical accuracy.
`Quality.Samples` selects 1–48 samples per MIS strategy; default 12, preserving
24 default evaluations and a maximum of 96 per enabled light. Search defaults
to 512 steps. `Quality.TransmissionSteps` was removed after switching to cached
optical depth. Mandelbox scale and iteration controls retain MandelVolume's
mappings. The prototype is exempt from the silence
invariant; the blanket silence regression still discovers it and must not be used
as its acceptance criterion. No unit tests were added or changed.

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

Fluid scenes expose `Fluid.PeriodicBoundary` as a boolean. The host selects the
matching wrap samplers, while the simulation wraps pressure, velocity, pigment
and residence-age stencils. Periodic mode uses a translating music-driven
carrier and keeps HalfLife decay, with no boundary sink or expansion dilution.
Changing the checkbox preserves simulation state. Every fluid parameter struct
used with the shared simulation must include this boolean; source defaults are
false in the bundled scenes.

## Shared inputs and capabilities

`FrameConstants` supplies wall time `T`, frame delta `DT`, musical time `S`,
`Resolution`, `AspectRatio`, history coordinates, the existing audio feature values
and the unscaled musical movement clock `MusicalMovementTime`.
`Spectrum0/1` and `Histogram0/1` supply positive sustained (S), transient (T), and background (B) evidence in RGB and raw linear magnitude in alpha; `Linear`
is the shared sampler. See `Common/Inputs.hlsl` for the complete declarations.

Resources are bound by reflected name independently for every entry point. Unknown
resources, resource arrays and unsupported parameter types fail reload before any
working program is replaced. Engine inputs use reserved names and must retain their
declared types and layouts.

Referencing `DistanceField` requests Mandelbench's cached 2D boundary-distance
passes (`DistanceSeedPS`, `DistanceJumpPS`, `DistanceResolvePS`). These use the
scene's `Parameters`, `DistanceSeeds` and `DistanceConstants`; the final scene
reads `Texture2D<float2> DistanceField` with `DistanceSampler`. The existing
Mandelbench geometry/view controls are the cache contract. Geometry edits,
resize and reload rebuild the field; contour appearance edits reuse it.
This renderer capability requires one application rebuild/restart.

The Gaussian STBR fit estimates sustained spectral structure, broadband temporal
structure, local background, and an unexplained residual. RGB contains
positive S/T and background B; alpha is spectral amplitude A, not residual R. All four channels use input-gained amplitude units, with no component display gains.
Signed effects and residual RMS are exposed separately on the CPU. Components
are not normalized against one another. `FilteredHistogram0/1` remain aliases
for `Histogram0/1`; Histogram's `CombinedLevel` shows raw amplitude.
`TimeWindowSeconds` and `FrequencyWindowOctaves` control the fit neighborhood.

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
visible musical excitation, then allows 1200 to 10,000 decay frames at 60 Hz
(20 to 166.7 seconds of simulated silence). It tests both stopped
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
