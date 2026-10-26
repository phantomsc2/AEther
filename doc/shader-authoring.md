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
observations. Missing rows remain black. Live bars use the newest block consumed
this display frame and are black when no new block became due, matching `Spectrum`.
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
Debug scene remains available in the scene picker, with shared shader parameters.
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
    return float4(Appearance.Enabled ? Appearance.Tint * Appearance.Brightness * level : 0, 1);
}
```

`PS` is the required pixel entry point. `PSin` contains `Position : SV_POSITION`
and `UV : TEXCOORDS`. The host supplies the fullscreen triangle vertex shader.
Scene output follows the existing display-encoded RGB convention; presentation
decodes it for scRGB output. Alpha should be one.

Production scenes must eventually turn black without music, including Mandelbench.
Explicit audio-independent prototypes, such as MandelVolume and MandelLight, are exceptions.
Mandelbench's study geometry stays static while raw spectral energy drives exposure.
Retained history, trails and fluid may decay naturally; procedural backgrounds
and ambient illumination must also lose their light. Sample local S/T/B and raw
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
sample counts show grain; raising quality costs GPU time. Audio-independent volume
prototypes are excluded from the production silence and audio-response scene lists
in `ShaderSilenceTests` under the prototype exception in `AGENTS.md`.

## MandelLightv4 prototype

`MandelLightv4` rasterizes opaque instanced cubes into camera color/depth and
optional scalar shadow maps. Cone lights use one axis-aligned perspective view
with a 5..60 degree half-angle; omnidirectional lights use six cube faces. Both
pack into one depth-array resource and share surface/airlight visibility code.
`Lights.Shadows` and `TonalLights.Shadows`
independently enable shadows, both defaulting off. Unshadowed lights use the
shared LUT path; shadowed lights share the visibility sampler, with capacity for
both rings (192 lights). Full-size shadow arrays are omitted when neither group
needs them. A fullscreen pass integrates single scattering through
infinite homogeneous air, combining equiangular and optical-depth proposals with
balance-heuristic MIS. Material edges are hard; shadow queries default to hardware
2x2 PCF on 512-pixel faces with rendered guard pixels. Sampling jitter supplies
the integration noise. A fixed world-space ring defaults to 60 lights (1..96),
using the RingEmitter polar frequency/stereo mapping. Current `Spectrum0/1.g`
transient amplitude supplies linear light intensity, with a strobe gain and no
history, smoothing or ambient floor. The host uploads sampled light data once per
frame; zero-intensity lights are skipped.
A second ring uses live `Spectrum0/1.r` structure, shares the count/radius/palette
and stereo coordinates, and lies horizontally in XZ. Its default intensity is
75% of the strobe intensity for equal S/T. All unshadowed lights are batched into
the same surface and airlight draws. A shared scaled RGB tail LUT integrates
their airlight with no visibility queries. Extinction changes rebuild the
table in the background; deterministic angular quadrature supplies the correct
current coefficients until upload. `TonalLights.Enabled/Gain` control this ring
independently of `Lights.Enabled/Gain`, which control only the transient strobes.
Strobes default to hard 30-degree inward cones. Their optical and equiangular
proposals are conditioned on each ray/cone interval, with matching normalized
PDFs, per-light distance stratification, and ray-local light selection.
An independent circular filament at the origin has one `RingLight.Intensity`
control, default 0.5. Live mean tonal S supplies its energy, preserving silence.
It matches the point-light rings' radius and is orthogonal to both planes.
It uses local-line `pi/r` falloff, nearest-point Lambert/phase shading,
and deterministic hyperbolic camera-ray quadrature without shadows or a LUT.
Analytic slope breakpoints bracket the ring-distance extrema; safeguarded root
solving splits the ray into monotonic-distance intervals, each with transformed
Gauss quadrature. The source directions and extinction use the nearest ring point
rather than integration around the emitter. Quadrature has a 20-optical-depth
numerical tail limit. The ring shares the existing surface and airlight draws.
Point lights use `1/r^2` and the filament uses `pi/r`, with no source radii or spatial
regularization. The point LUT stores `b*tail` and factors out `1/b` during lookup.
Only floating-point denominator/degeneracy safeguards remain; exact singularities
use finite float32 saturation. Both surface and airlight targets are RGBA32F.
The dense default preset enables one-light-per-distance sampling and half-resolution
airlight with depth-aware reconstruction and full-resolution fallback at unmatched
depth boundaries. Scattering defaults to (1,1,1) and density to 0.65.
When half-resolution airlight is disabled, an optional depth-aware 3x3 filter
denoises HDR airlight before surface composition. Surface shading and analytical
transmission retain their original pixel values.
This audio-reactive prototype participates in the GPU silence regression. It is
excluded from B-only response checks because its lights intentionally use only S/T.
Restart after building the host changes.
See [MandelLightv4](mandellight-v4.md) for controls, sampling and measurements.

## MandelLightv3 prototype

`MandelLightv3` aggregates procedural positive densities into a 125-real-coefficient
Fourier field, then builds adaptive eight-knot deep cubemaps for up to four lights.
GPU lighting has no per-object loop. Camera cells use analytical material mass,
with sampled illumination and cached light transmission. This audio-independent
prototype is exempt from the production silence requirement. It adds host render
resources, so restart after building before selecting the scene. See
[MandelLightv3](mandellight-v3.md) for controls, representation limits and profiling.

## MandelLightv2 prototype

`MandelLightv2` is an audio-independent experiment with procedural ellipsoidal
density blobs and analytical camera/light attenuation. It uses a single pixel
shader and shares its integration samples across lights and emission, with no
transport caches or surface shading. It remains visible without music under the
prototype exception. See [MandelLightv2](mandellight-v2.md) for the model, controls
and profiling instructions. `MandelLight` remains the fractal-volume reference.

## MandelLight prototype

`MandelLight` is an audio-independent participating-medium prototype. Up to four
RGB point lights illuminate radial air and a bounded Mandelbox density field.
Camera and light rays store moment representations of cumulative occupied length
and displaced air. Local scattering and emission evaluate the density field at
integration samples. The scene uses single scattering and has no temporal image
history or audio response. It is exempt from the silence invariant; production
audio reactivity must gate both emission and lights and preserve
`ShaderSilenceTests`.

Restart after updating the renderer for the new transport passes and resources.
Subsequent shader edits use normal hot reload.

### Interior and transport controls

| Control | Behavior |
| --- | --- |
| `Interior.Enabled` | Enables the material density field. Disabling it leaves only the radial air. |
| `Interior.Density` | Maps to `d = 4 * value^2`, multiplying interior scattering and absorption. Air displacement scales by `min(d,1)`, so zero density leaves the surrounding air intact. Default 0.25 gives d=0.25. |
| `Interior.EdgeSoftness` | Width of the smooth density transition in normalized potential, from `q/v = 1-value` to `q/v = 1`. It softens the density near the threshold without changing the fully occupied core. Default 0.2; zero approaches a hard density transition. This is not a world-space distance or overall opacity control. |
| `Interior.Scattering` | RGB scattering coefficients; default (0.7,0.7,0.7). Also contributes to extinction. |
| `Interior.Absorption` | RGB absorption coefficients; default (0.15,0.3,0.45). |
| `Transport.Trigonometric` | False: four power moments, the default baseline. True: two complex trigonometric moments, using the same four real coefficient channels. Applies to both camera and light transport. |
| `Transport.OrbitRejection` | Default true. For more than four iterations, terminate a density query when a conservative orbit bound proves its final potential cannot contain material. Does not skip positions along a ray. |
| `Emission.Enabled` | Integrates local volume emission independently of the lights. |
| `Emission.Strength` | Maps to `8 * value^2` emitted radiance per world unit of occupied material. Default 0.2. |
| `Emission.Color` | Linear RGB emission color; default (1,0.35,0.1). |
| `Quality.TransportSteps` | Selects 128-1024 midpoint samples in multiples of 16 over each camera/light ray's complete interior support chord; default 512. |
| `Quality.Samples` | Selects 1-48 camera integration samples per proposal; default 12. |

Vectors use x=red, y=green and z=blue. Every exposed float remains in [0,1].
Emission is independent of density, so zero density permits a transparent emitter.
Disable emission to make zero-density material visually indistinguishable from air.
Emission does not illuminate neighboring material. With all lights disabled and
emission off, the rendered radiance is exactly black. Debug views intentionally
visualize their caches independently of illumination.

### Density and moment transport

The finite Mandelbox field q retains MandelVolume's scale and iteration mappings:
scale is `8 * Fractal.Scale - 4`, and iterations are
`1 + round(15 * Fractal.MaximumIterations)` (four by default).
The potential slider spans twelve octaves:

```text
v = 0.5 * 2^(12 * (Fractal.Potential - 1))
w = max(0.00001, Interior.EdgeSoftness)
m(p) = 1 - smoothstep(1 - w, 1, q(p) / v)
k = d * (Interior.Scattering + Interior.Absorption)
```

Occupancy m is zero outside radius 10.5 or when the interior is disabled. The
bounding sphere defines the integration support. Every occupied interval along a
ray contributes to attenuation; gaps contribute only air. Camera integration
continues to the radius-16 atmosphere exit, including cameras inside material.

For a ray `p(s) = o + s*d` with a support interval [a,b], moments accumulate two
positive measures: occupied length m*ds and displaced unit-density air
m*sigma_air*ds. Both use the same normalized depth `x = 2*(s-a)/(b-a)-1`.

```text
f(x) = (x, x^2, x^3, x^4)                         // power mode
f(x) = (cos(t), sin(t), cos(2*t), sin(2*t))        // trigonometric mode
t = 0.95*pi*(x+1)

h = integral[a,b] m(p(s)) ds
j = integral[a,b] m(p(s))*sigma_air(p(s)) ds
B = integral[a,b] m(p(s))*f(x(s)) ds / h
C = integral[a,b] m(p(s))*sigma_air(p(s))*f(x(s)) ds / j

h(s) = h * cumulative_fraction(B, x(s))
j(s) = j * cumulative_fraction(C, x(s))
f = min(d,1)
T_rgb(s) = exp(-k*h(s) - Air.Density*Air.Scattering*max(0, air_depth(s)-f*j(s)))
```

Empty measures contribute zero and endpoints return exact zero/total mass.
Density and RGB coefficients are applied after reconstruction. This retains RGB
absorption differences without separate per-channel moment caches. Subtracting
displaced air makes extinction consistent with scattering: material replaces air
in proportion to `m*min(d,1)`, while cavities retain the radial atmosphere. This
displacement fades continuously as density approaches zero; cached occupancy and
displaced-air moments remain independent of the material density setting.

`Common/TransportMoments.hlsl` adapts the CC0 reconstruction from
[Moment-Based Order-Independent Transparency](https://momentsingraphics.de/I3D2018.html).
Power reconstruction uses a Hankel factorization, trigonometric reconstruction a
complex Toeplitz factorization. Both use a small moment bias (2e-5) and the midpoint
of the reconstructed cumulative bounds. Trigonometric depths leave a pi/10
wrapping zone. Reconstruction returns a cumulative mass fraction, followed by
RGB Beer-Lambert attenuation. This is a low-order approximation: intricate
occupancy can be smoothed or misplaced, and finite transport sampling can miss
features narrower than one step.

### Scattering and integration

Air retains its radial unit-density extinction and normalized Rayleigh phase:

```text
H = 0.5 + 2 * Air.ScaleHeight
sigma_air(p) = 0.08 * exp((6 - length(p)) / H)
sigma_s_rgb(p) = Air.Density*sigma_air(p)*Air.Scattering*(1-min(d,1)*m(p))
                 + d*Interior.Scattering*m(p)
phase(mu) = 3 * (1 + mu*mu) / (16*pi)
```

Both media use the same camera and light transmittance. Each light uses equal-count
balance MIS with equiangular and endpoint-fitted exponential proposals over the
atmosphere interval. Rays crossing the interior's support add a density-guided
proposal within that support. Camera transport supplies 16 occupancy bins from its
existing density evaluations. Approximate camera attenuation weights these bins;
at least 10% uniform sampling retains coverage of features missed by the cache.
For light integration the guide fades continuously to uniform as density approaches
zero, including when material is disabled. The bounded proposal remains active for
air to avoid an abrupt change in grain at zero density. Emission uses the same
guide in an independent stratified integration, once per frame.

The histogram changes sample placement, not the represented material or the moment
reconstruction. Actual density is evaluated at every sample, and every proposal
PDF uses world-space distance with the same histogram used for sampling and MIS.
Default sampling performs 24 evaluations per light, or 36 when the ray crosses
the interior's support. No potential gradient or distance estimate is evaluated.
Samples use deterministic spatial jitter without time jitter. Low sample counts
can show grain or miss small occupied intervals. This model provides direct
single scattering; diffuse transport through repeated scattering is not included.

### Cache layout and invalidation

`CameraTransportPS` produces three full-resolution RGBA32F images and one
RGBA32UInt image, 64 bytes per pixel. Each enabled point light uses `LightTransportPS` to produce three
512-by-512 RGBA32F angular images, 12 MiB per light. Their channels contain:

| Resource suffix | Content |
| --- | --- |
| `Moments` | Four normalized occupied-length coefficients. |
| `AirMoments` | Four normalized displaced-air coefficients. |
| `Transport` | Total occupied length, total displaced air depth, support chord entry and exit distance. |
| `Importance` (camera only) | Sixteen mean occupancy bins, packed at eight bits per bin into four uint channels. Used only for sample placement. |

Camera resources use the `Camera` prefix, angular resources the `Light` prefix.
The full-resolution camera data is shared by every light and the emission draw.
Angular coordinates are inclination gamma/pi and azimuth alpha/(2*pi), following
the mapping used by [Chen et al.](https://cs.dartmouth.edu/~wjarosz/publications/chen11realtime.html).
The polar axis runs from the light toward the camera: positions along any camera
ray retain their azimuth and therefore stay in one epipolar slice. This does not
implement Chen's hierarchical traversal or reuse an integrated result across pixels.
At lookup, each neighboring angular ray reconstructs at the receiver distance
using its own support chord. RGB transmissions are filtered after exponentiation.
A deterministic azimuth handles the exact poles without a secondary light march.

Geometry, enabled interior, edge softness, moment basis, orbit rejection, transport quality,
scale height, camera motion, and shader changes invalidate affected transport
caches. Light motion invalidates only that light's angular cache. Camera resizing
reallocates its transport images. Density, RGB material coefficients, emission,
light intensity and color, and integration sample count reuse transport data.
The cache build samples the whole support chord, so cost grows with transport
quality, camera resolution, enabled lights, and fractal iteration count.

`AtmospherePS` retains its 512-by-256 R32F table with 1024 midpoint samples per
texel. It stores outward unit-density optical depth by radius and square-root
zenith cosine and rebuilds only for shader or scale-height changes. Inward paths
use chord symmetry. Camera attenuation subtracts the reverse exit tail at an
inside-atmosphere camera. Light attenuation evaluates the source and receiver
exit tails once per sample, shared by all four angular-cache taps. Cached support
intervals avoid recomputing angular directions and sphere intersections per tap.

Independent light and emission draws add into one RGBA32F radiance image, 16 bytes
per pixel, cleared each frame. `PS` applies the exponential tone map after
summation and encodes for display. Host `LightConstants` selects lights 0-3 or
the emission-only draw 4.

See [MandelLight performance](mandellight-performance.md) for measured pass costs,
noise comparisons, remaining limitations, and the profiling command.

### Camera, lights and inspection

`View.CameraSpeed` maps to 0-0.25 radians/second around world Y (default 0.05).
Orientation follows this orbit independently of translation, including at the
origin. `View.CameraDistance` maps to `40 * value` world units, default 0.4324711.
`View.HorizontalOffset` translates along camera right by
`(2 * value - 1) * distance`, with 0.5 centered.

`Light` through `Light4` expose Enabled, Intensity, Speed, Radius, Phase, and RGB
Color. Lights orbit at horizontal radius 12 with vertical excursion 3. Speed maps
to `0.5 * value` radians/second; phase adds `2*pi*value`. Intensity maps to
`3000 * value^2`. Radius softens inverse-square falloff by
`0.01 + 0.49 * value^2`. Only the original white light is enabled by default.
Disabled, black, or zero-intensity lights skip transport and integration unless
selected for inspection.

`View.ShowLightTransport` uses the left 70% for rendering and the right panel for
RGB material opacity reconstructed from the angular moments. `View.TransportDepth`
selects radial distance 0-32 (default 32, beyond the entire interior).
`View.TransportLight` selects Light through Light4 at values 0, 1/3, 2/3, 1.
A selected disabled light still builds a valid cache without adding illumination.
`View.ShowAtmosphereLookup` takes priority and displays logarithmic unit-density
optical depth with radius horizontal and square-root zenith cosine vertical.
Atmosphere inspection skips camera and light integration.

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
`Spectrum0/1` and `Histogram0/1` supply [positive S, positive T, background B,
splitter input amplitude] in RGBA, in linear amplitude units. The splitter input
is the selected Kalman smoother's output. `Linear` is the shared sampler.
See [smoother models and comparison](kalman-smoothing.md),
[splitter semantics](splitter-design.md), and `Common/Inputs.hlsl` for details.

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

The preprocessing stage compares log-amplitude and complex-resonator Kalman
models with explicit acceleration-noise intensities and fixed Cartesian noise
RMS. Both return linear magnitudes; neither exports velocity or phase. The
original splitter neighborhood settings and `Analyzer.JointEvidenceAvailable`
callback are restored. Spectrum's `CompareSmoothers` view uses diagnostic row 1:
[signed momentary residual, raw magnitude, mode A magnitude, mode B magnitude].
It displays A left/red and B right/cyan using the same stereo average and linear
scale. `FilteredHistogram0/1` remain aliases for `Histogram0/1`.

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
shared bounded scroller. HistorySeconds is in Pipeline; the GPU category and outer
Audio expander are removed. The unlabeled
Scene picker is pinned on the right and starts empty. Selecting a scene opens the
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
dotnet run --project tools/AEther.Smoke -- . --windows-watcher-only
```

The focused smoke check renders all discovered scenes with synthetic audio, checks reflected
struct/vector/bool defaults and uploads, changes slots and declaration order, exercises
add/remove/reset and failed-reload rollback, and verifies watcher discovery and shader
window controls without restarting capture. PNGs go to the ignored smoke artifacts folder.
Historical specialized smoke modes still contain fixtures for the previous shader
layout and require migration before use with this authoring system.
