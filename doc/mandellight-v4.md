# MandelLightv4: raster geometry and infinite homogeneous air

Restart after building, then select **MandelLightv4**. This audio-reactive prototype
uses live spectral transients for its strobe ring and tonal structure for a
horizontal ring. Each has an independent shadow toggle, default off. Without live S/T it renders
black, including after active input; it participates in the GPU silence regression.
It intentionally does not respond to a B-only/global-background fixture.

The default preset favors dense, strongly scattering air: Air.Density is 0.65,
Scattering is (1,1,1), and the low absorption remains (0.03,0.05,0.08). Half-resolution
airlight is enabled. Unshadowed lights use the LUT; when shadows are enabled,
the sampler uses 32 distance samples, hardware PCF and 512-pixel shadow faces. The scene retains 128 cube instances,
with two static 60-light spectral rings inside the cloud (120 lights by default).
After reloading, use **Reset defaults** to apply the new preset to an existing session.

A historical 640x360 RTX 4070 Laptop capture with a synthetic live stereo-T fixture measured
approximately 1.54 ms total: 0.99 ms for 20 shadow cubemaps, 0.10 ms surfaces,
0.27 ms airlight and 0.10 ms reconstruction/composition. All lights were active
and the cubes were rotating, requiring shadow-map refreshes. Capture and timings
are in `artifacts/mandellight-v4/audio-ring-final`. Other timing tables below
describe the earlier four-light prototypes unless stated otherwise.

## Live spectral light ring

Lights.Count selects 1..96 lights per ring, default 60. Their positions form a fixed
world-space ring facing the initial camera, with radius
`(2+10*Shape.Spread)*Lights.RingRadius`, default 5.7 inside the cube cloud's
9.5-unit half extent. Camera motion does not move the ring. Positions do not
respond to music. Cubes retain their independent rotation control.

The ring uses the same polar frequency/stereo mapping and frequency palette as
`Common/RingEmitter.hlsl`. For unit ring coordinates `p=(sin(a),cos(a))`:

```text
f = atan2(abs(p.x),-p.y)/pi
right = 0.5 + 0.5*p.x*abs(p.x)
T = lerp(Spectrum0(f,0.5).g, Spectrum1(f,0.5).g, right)
intensity_rgb = 3000 * 2^(12*Lights.Gain-6) * T * palette(f)
```

Bass is at the bottom, treble at the top. The sign of Y converts world-space up
to the downward-positive UV convention used by RingEmitter. Left and right each traverse the
frequency axis, with mono blending at the two poles and maximum stereo separation
at the sides. Frequency follows the analyzer's existing log-frequency coordinate.
The gain defaults to 4x. Intensity is linear in the live positive transient
amplitude: no squaring, threshold, smoothing, history, S/B contribution or ambient
floor. The existing spectrum upload represents the maximum of packets consumed
for the current render frame; it contains no persistent light envelope.

The host samples the already available CPU spectra once per light, matching the
other shaders' clamped linear texture sampling at `(f,.5)`, then uploads positions,
RGB intensities and nonzero-light indices in a small constant buffer. This avoids
reading/interpolating spectrum textures at every airlight distance sample. Silent
lights are omitted from surface/airlight loops and shadow refreshes. A light that
becomes active with shadows enabled receives a valid shadow map before it is used. Intensity changes
do not invalidate maps; geometry motion, ring placement/count, geometry edits,
resolution changes and reloads do.

The random-light mode caches only indices and cone intervals for lights that
intersect each ray, constructing a full proposal only for the selected light.
Distance samples are stratified separately within each light. With 32 samples
and 20 intersecting lights, each gets one or two samples; cone misses are omitted.
There is still no temporal accumulation.

## Shared hard spotlights

Both rings support the same hard angular emission profile within their existing
surface and airlight passes. The transient strobes default to inward-facing
spotlights with a **30-degree half-angle** (60-degree full opening). The tonal
ring remains omnidirectional by default. `Lights.Directional` and
`TonalLights.Directional` select spot versus full-sphere emission independently.
Both axes point from each ring position toward the world origin, including when
the ring radius is zero, where the ring's angular coordinates define the axis.

Each group's `HalfAngle` slider maps to `90*value` degrees, clamped to 5..60;
the default value 1/3 gives 30 degrees. Wider openings and soft angular
edges are unsupported. Intensity remains the on-axis intensity: narrowing the
cone reduces emitted power rather than concentrating the same power.

Host-uploaded unit axes and cosine thresholds share one representation; a negative
cosine marks omnidirectional emission and bypasses cone calculations. Surface
lighting rejects points outside the forward cone before querying shadows.
Shadowed airlight conditions both sampling proposals and their PDFs on the cone
interval. Lights with empty intervals are excluded from that ray's selection.
The angular span and shifted exponential normalization use the same entry/exit
bounds; samples outside other lights' intervals have zero PDF for those lights.

For unshadowed lights, the camera ray is intersected analytically with the convex
forward cone and clipped to the opaque surface endpoint. The shared LUT evaluates
the resulting interval with one tail lookup for an infinite exit or two for a
finite exit; empty intervals require no lookups. The quadrature fallback uses the
same clipped interval. Point lights bypass clipping. Orientation and aperture
changes do not rebuild the LUT. With shadows enabled, they invalidate the
light's cached shadow view without reallocating its depth slice.

Directional lights use one perspective shadow view aligned with the cone axis.
The full field of view is twice the half-angle, 60 degrees by default. The host
uploads an orthonormal right/up frame, tangent of the half-angle, and array-slice
base for each light. Omnidirectional lights retain six fixed 90-degree views.
Both projection types pack into the same depth texture array, with one slice per
spotlight and six per point light. Surface lighting and airlight remain combined;
visibility selects the light's projection. The receiver-plane corrections and
hardware PCF use that same projection and its one-texel guard.

## Horizontal tonal ring and combined unshadowed fast path

`Lights.Shadows` and `TonalLights.Shadows` independently select shadow transport
for their rings. Both default to **false**. With a toggle off, its active lights
share the existing unshadowed surface loop and LUT airlight loop. With it on, they
use shadowed surface lighting and the same cone-bounded MIS airlight sampler.
Any combination is supported, including all 192 lights shadowed. Neither toggle
changes intensity, direction, spectrum input, or the other ring's settings.

Shadow maps are allocated for the configured shadow groups and refreshed only
for active lights. With both toggles off, a tiny placeholder SRV replaces the
full-resolution arrays and no shadow-map draws occur. `View.ShowShadows` explicitly
builds the selected diagnostic map even for inactive lights. It selects from
strobe maps followed by tonal maps when both are enabled; with neither enabled,
it previews the strobe positions without enabling light visibility. Spotlights
show one complete depth view; point lights show their six faces in a 3x2 grid.

The second ring has the same count, radius, angular spacing, soft source radius,
palette, frequency coordinates and stereo blend as the strobe ring. It lies in
the world XZ plane at Y=0, sharing the strobe ring's horizontal stereo axis.
Treble is on its far side along the initial horizontal view direction; bass is
on its near side. Camera motion does not move either ring.

`TonalLights.Enabled/Gain` independently enable and attenuate this ring.
`Lights.Enabled/Gain` control only the strobes. Its input is live `Spectrum.r` (S), with no added history,
envelope, global background, or ambient floor. Its intensity is:

```text
S = lerp(Spectrum0(f,0.5).r, Spectrum1(f,0.5).r, right)
tonal_rgb = 0.75 * 3000 * 2^(12*TonalLights.Gain-6) * S * palette(f)
```

Both gain sliders default to 2/3: strobes have 4x gain, tonal lights 3x including
their fixed 0.75 factor. This preserves the prior default brightness while making
the gains independent. Equal slider values and S/T amplitudes give a 75% tonal
intensity. Existing saved presets may need **Reset defaults** to apply the new settings.

Both rings illuminate the Lambert surfaces in the existing geometry draw. The
unshadowed loop makes no visibility queries. The fullscreen airlight pass reconstructs
one camera ray and depth endpoint, sums all active lights with shadows disabled
using a shared cumulative-integral texture, then adds the sampled shadowed contribution.
The rings are combined in linear HDR before reconstruction and tone mapping.
Lights with shadows disabled require no per-light draws or shadow cubemaps. Lights have no distance
cutoff; optional spot cones restrict their angular support.

The 513x1025 RGBA32F lookup occupies 8.02 MiB and stores a scaled Rayleigh tail
integral for the current RGB extinction and soft radius. Infinite rays require
one lookup per unshadowed light; finite intervals require two. Camera motion,
light positions, spectrum, gain and object changes do not invalidate it. Medium
coefficient or radius changes trigger a CPU-only background build; GPU upload
stays on the rendering thread. Up to four recent coefficient sets are cached.
While the matching table is unavailable, a deterministic 16-point equiangular
quadrature evaluates the same unshadowed integral. A stale table is never used.
Rays outside the table's perpendicular-distance domain also use quadrature.
See [the LUT study](mandellight-lut-study.md) for its derivation and accuracy limits.

Historical production-path RTX 4070 Laptop measurements before directional strobes
(both rings omnidirectional) at **1280x720**, default half-resolution
airlight, 128 rotating cubes, stationary camera, median of 41 frames after warmup:

| Active lights | Surface ms | Airlight ms | Composite ms | Total GPU ms |
| --- | ---: | ---: | ---: | ---: |
| 20 strobes | 0.294 | 0.726 | 0.183 | 2.257 |
| 1 tonal | 0.022 | 0.010 | 0.034 | 0.109 |
| 20 tonal | 0.133 | 0.094 | 0.083 | 0.361 |
| 32 tonal | 0.318 | 0.225 | 0.183 | 0.794 |
| 20 strobes + 20 tonal | 0.431 | 0.732 | 0.242 | 2.392 |

The LUT is ready before measurement; builds, compilation and readback are excluded.
Strobe and combined cases include refreshing all strobe maps. Tonal-only cases
assert that no shadow-map draws occurred. GPU clocks vary, so the difference
between total medians should not be read as an exact incremental cost. Captures
and raw timings are in `artifacts/mandellight-v4/tonal-ring`; reproduce with the
profiling tool's `--tonal-ring` mode now captures 60-light rings and the 96-light
capacity case; the table above retains its historical 20/32-light measurements.

## Rendering

The scene draws 1..4096 procedural cube instances with 36 vertices each. Seeded
positions, sizes, orientations and albedo variation are calculated by the vertex
shader. Increasing Count preserves previous instances. There are no Fourier modes,
surface distance estimates, material interiors or object translucency.

Each active shadowed cone light rasterizes one perspective depth view; each
omnidirectional light rasterizes six. Both use a scalar R32 depth array. Only the
nearest surface is needed for opaque visibility.
The camera rasterizes the same instances into a linear HDR surface-color target
and a sampleable scalar depth target. Vertex normals provide Lambert diffuse
lighting directly; a normal/material G-buffer is unnecessary for this POC.

Surface lighting includes light-to-surface air extinction and filtered shadow queries.
A fullscreen pass samples air scattering up to the visible surface, or all the
way to infinity for background rays, into an HDR texture. A second pass optionally
filters that airlight and composites:

```text
pixel_radiance = airlight + exp(-sigma_t * surface_distance) * surface_radiance
```

This is single scattering with a normalized Rayleigh phase function. There is no
ambient/environment source or multiple scattering. Lights.Radius regularizes the
inverse-square falloff; it does not turn the light into an area emitter.

## Shadow filtering

Shadow faces default to **512x512** useful texels. Quality.HardwarePCF defaults to
true and uses one SampleCmpLevelZero instruction with a linear comparison sampler
for each shadow query, both on surfaces and in airlight. The hardware compares four
depth texels and bilinearly filters their visibility results. It does not interpolate
depth and then compare. Disabling the toggle restores a nearest-texel comparison.

Every shadow view also renders a one-texel guard on each side: a 512 view is stored as
514x514 with slightly expanded projection coverage. Lookup coordinates address the
central useful region. Cubes cover 90 degrees per face; spots cover twice their
half-angle. Thus the full 2x2 filter footprint remains inside rendered data even
at cube edges/corners and cone edges, without manual cross-face taps.

Surface receiver-plane depth is affine in projected face coordinates. PCF uses a
conservative reference depth over its four texel centers to prevent slope acne,
plus Quality.ShadowBias. The point-sampled comparison retains the exact receiver
plane at its texel center. As with other bias schemes, a conservative tolerance can
slightly shrink contact shadows at grazing angles. The filter softens shadow edges
by about one map texel; material silhouettes remain hard raster geometry.

Before the integral arithmetic optimization below: RTX 4070 Laptop GPU, 640x360,
four moving lights/cubes and 32 airlight samples,
same camera/time sequence, medians of 11 frames after eight warmups:

| Shadow mode | Four maps total, approximately ms | Surface ms | Airlight ms | Total ms |
| --- | ---: | ---: | ---: | ---: |
| Point, 256 | 0.10 | 0.023 | 1.62 | 1.81 |
| Hardware PCF, 256 | 0.10 | 0.023 | 1.82 | 2.01 |
| Point, 512 | 0.18 | 0.023 | 1.92 | 2.20 |
| Hardware PCF, 512 | 0.18 | 0.023 | 2.25 | 2.53 |

With air disabled, point/PCF surface costs were nearly identical in this scene.
The repeated airlight queries account for most of the filtering cost. Local
captures: [point 256](../artifacts/mandellight-v4/shadows/point256-no-air.png),
[PCF 512](../artifacts/mandellight-v4/shadows/pcf512-no-air.png),
[PCF with air](../artifacts/mandellight-v4/shadows/pcf512.png), and
[measurements](../artifacts/mandellight-v4/shadows/shadows.json).

## Atmosphere

Air has constant RGB scattering and absorption everywhere:

```text
sigma_s = 0.3 * Air.Density^2 * Air.Scattering
sigma_a = 0.3 * Air.Density^2 * Air.Absorption
sigma_t = sigma_s + sigma_a
T(d) = exp(-sigma_t * d)
```

There is no bounding sphere, density falloff or fixed air march distance. Finite
lights illuminate the infinite medium; the background has no separate color.
Surface camera depth terminates the air integral. Geometry uses a near plane of
0.025 and far plane of 128, sufficient for the bounded procedural cube scene; that
projection limit does not truncate background air. Air between overlapping solid
objects is never integrated along a camera ray beyond its first visible surface.

The ellipsoid column integral is unnecessary for constant density: attenuation
reduces to an exponential of distance. V2's equiangular sampling idea, phase
function and light falloff remain useful.

## Sampling and MIS

For each shadowed light, intersect the visible camera ray with its forward cone.
Full-sphere lights use the full visible interval. Empty intervals are excluded
from that ray's participating light count M. There are two proposal families:

* **Optical depth:** shifted exponential distances starting at each cone entry,
  normalized over that light's interval. Its scalar rate follows the
  slowest attenuating RGB channel that scatters, to preserve a useful tail.
* **Equiangular:** one proposal per participating light, covering exactly its cone
  interval. It follows the softened inverse-square illumination peak.

Sampling.OpticalDepthWeight supplies the optical proposal's probability w.
Both proposals are zero outside the light's interval. For interval [a_i,b_i]:

```text
mass_i = 1-exp(-rate*(b_i-a_i))     # 1 if b_i is infinite
e_i(t) = rate*exp(-rate*(t-a_i))/mass_i
p_i(t) = w*e_i(t) + (1-w)*angular_i(t)
```

The default random-light mode cycles through the M participating lights with a
random starting offset. For N>=M, each light's n_i distance samples cover its own
full CDF in n_i strata; its sum is divided by n_i*p_i(t). This avoids the previous
coupling between a light's index and a narrow global distance stratum. For N<M,
a uniform cyclic subset is sampled and weighted by M/(N*p_i(t)).

With Sampling.RandomLight disabled, draw from q(t)=sum_i p_i(t)/M and evaluate the
full illumination sum at each shared distance, dividing by N*q(t). The PDF sum
includes only proposals whose clipped intervals contain that distance.

Neither proposal's weight is a brightness control. Slider 0 gives pure equiangular
sampling; 1 gives pure optical-depth sampling; default 0.5 combines both.
Inverse-CDF endpoints are clamped inside their domains. Small optical intervals
use stable exp/log series to avoid cancellation.

At 1280x720, 20 strobes, 128 frozen cubes, default 32 samples and half-resolution
airlight, a matched before/after capture reduced display-RGB RMSE against a
1001-sample reference from 18.881 to 1.614 (8-bit channel units), about 91.5%.
The comparison includes pixels whose reference RGB sum is at least 15. The
reference retains the same shadow maps and reconstruction and is not an exact
transport solution. RTX 4070 Laptop median airlight time was 0.608 ms before and
0.561 ms after; total 1.232 vs 1.164 ms, with cached shadow maps and 41 measured
frames. Clock variation limits small timing differences. Captures, source
snapshots and measurements are in `artifacts/mandellight-v4/spotlight-sampling`.

The inner loop shares its ray/light quadratic between the equiangular PDF,
inverse-square falloff and Rayleigh phase. With `c` the light projection along the
camera ray, `b2` its squared perpendicular distance, `a2` the softened radius
squared, and `t` the sample distance:

```text
x = t-c
r2 = x*x+b2
k = 1/(r2+a2)
angular_pdf = height/span * k
phase = 3/(16*pi) * (2-b2/r2)
```

The PDF and illumination sums are accumulated in one light loop, sharing `r2`
and `k`. This replaces the repeated 3D distance/normalized-direction calculation.
Camera and light attenuation are combined as `exp(-sigma_t*(t+sqrt(r2)))` inside
the light contribution, replacing their separate exponentials. Ray positions,
The conditioned sampling changes the proposal distribution, not the physical
scattering integrand or the number of configured distance samples.

At the earlier low-density, full-resolution defaults on RTX 4070 Laptop GPU,
640x360, a repeat baseline measured
2.212 ms airlight / 2.490 ms total, versus 1.125 ms airlight / 1.395 ms total after
the change. Captured PNGs differed in one color channel by one 8-bit step
(display-RGB RMSE 0.00000472). Independent quadrature and all six focused GPU
regressions passed. Local artifacts are in `artifacts/mandellight-v4/integral-before`,
`integral-baseline-repeat`, and `integral-after`. Earlier timing tables describe
the pre-optimization implementation.

Jitter uses fixed screen-space hashes, with no temporal accumulation or time
jitter. Disabling it replaces the random offset within each stratum by its
midpoint. Shadows use hardware PCF by default, as described above. ShadowBias is
an additional small depth tolerance. Sampling grain, residual angular shadow
aliasing and shadow bias error remain.
Hard material edges are preserved; all physical haze is surrounding airlight.

### Optional one-light sampling

Sampling.RandomLight defaults to true. When enabled, each distance-sample slot
chooses one active light, then samples distance from that light's own two-component
optical/equiangular mixture. It evaluates only that light's shadow and illumination:

```text
p(light) = 1/light_count
g_light(t) = w*q_optical(t) + (1-w)*q_angular_light(t)
sample_contribution = light_count * integrand_light(t) / (sample_count*g_light(t))
```

The factor `light_count` compensates for selection probability, preserving the
intended brightness. There is no sum of other lights' angular PDFs in this mode.
Light choices cycle through the active lights with a hash-randomized starting
offset per pixel, independently of the distance jitter. Each sample slot has a
uniform light choice, and light sample counts differ by at most one. A 32-sample
ray with 20 active lights gets one or two evaluations per light instead of 32; a ray with
one enabled light uses the same estimator as the all-light path. Disabling Jitter
uses a fixed cycle and distance-stratum midpoints.

Selection happens per distance sample, not once for the entire pixel/ray. It adds
light-selection noise, particularly visible as colored grain when lights differ
strongly. Sampling.OpticalDepthWeight still controls the optical/angular split.

## Spatial airlight filter

Filter.Enabled defaults to true. It applies one depth-aware 3x3 weighted average
to linear HDR airlight before surface composition and tone mapping. Surface color
and analytical camera transmission are evaluated at the original pixel. The
existing hash jitter and MIS estimator are unchanged; there is no image history.

The spatial weights are the outer product of `[1,2,1]`. Sky/geometry neighbors
never mix. Finite depths are weighted by:

```text
d = abs(neighbor_depth - center_depth) / max(0.05, 0.02*center_depth)
depth_weight = exp2(-2*d*d)
```

Weights are renormalized over the neighborhood. Linear camera depth is packed
into the airlight texture's alpha channel, with zero for sky, so the filter needs
nine HDR texture reads rather than additional depth texture reads. There is no
median or color threshold. A one-pixel radius can still soften thin godrays in
free air, where camera depth supplies no edge information. Disabling Filter.Enabled
provides an immediate comparison with the unfiltered estimator.

### Optional half-resolution airlight

Quality.HalfResolutionAirlight defaults to true. When enabled the HDR airlight
target has `ceil(width/2)` by `ceil(height/2)` pixels; camera surfaces, camera depth,
shadow maps and final composition remain at their original resolutions. Each
2x2 block integrates the full-resolution ray at its bottom-right pixel (clamped
at odd-sized viewport edges), using that pixel's exact raster depth as endpoint.
The guide in alpha refers to the same ray, with zero still meaning sky.

Final composition reconstructs from four neighboring coarse airlight samples.
Bilinear spatial weights are multiplied by the same depth weights as the 3x3
filter, then renormalized using the full-resolution pixel's depth as guide.
Sky and geometry never mix. If no compatible coarse neighbor contributes enough
weight, the composite evaluates the original full-resolution integral for that
pixel. This protects thin cubes/discontinuities that the coarse rays missed,
at some extra cost near silhouettes and strong depth changes.

This reconstruction replaces the 3x3 filter; Filter.Enabled affects only the
full-resolution mode. There is no extra blur pass, temporal accumulation or
frame-varying jitter. Finite depths within the tolerance can mix; shafts in open
air have no depth boundary to protect and can soften over the coarser sampling
grid. The mode reduces the main integration pass to approximately one quarter
of the rays, with additional reconstruction and exceptional full-resolution rays.

Both modes can be enabled independently. A first RTX 4070 Laptop capture at
640x360, 128 moving cubes, four moving lights, 32 samples and 512 hardware-PCF
shadow faces gave these medians of 11 frames after eight warmups:

| Mode | Airlight ms | Composite/reconstruction ms | Total ms |
| --- | ---: | ---: | ---: |
| Existing full resolution, all lights, 3x3 filter | 1.154 | 0.031 | 1.441 |
| One light per sample, full resolution | 0.427 | 0.031 | 0.715 |
| Half resolution, all lights | 0.438 | 0.196 | 0.896 |
| Both | 0.155 | 0.081 | 0.493 |

The composite includes the full-resolution fallback rays. Both enabled reduced
total GPU time by about 66% in this scene, with more visible grain than the
existing filtered mode. These are exploratory timings and quality depends on
geometry, depth discontinuities, sample budget and lighting. Captures and timings
are in `artifacts/mandellight-v4/economy-modes`. Use `--v4 --modes` with the profiling
tool to compare the four combinations.

On RTX 4070 Laptop GPU, a frozen 640x360 scene with 16 samples measured 0.949 ms
unfiltered and 0.964 ms filtered. The composite pass itself increased from about
0.014 to 0.020 ms. Against an unfiltered 512-sample reference, display-RGB RMSE
fell from 0.022884 to 0.008818 (2.6x lower). This includes filter bias, reference
sampling error and 8-bit export quantization; it is one scene comparison rather
than a universal quality or timing guarantee.

[Unfiltered](../artifacts/mandellight-v4/filter/unfiltered.png),
[filtered](../artifacts/mandellight-v4/filter/filtered.png),
[512-sample reference](../artifacts/mandellight-v4/filter/reference.png), and
[measurements](../artifacts/mandellight-v4/filter/filter.json) are local ignored
artifacts.

## Controls

| Control | Meaning |
| --- | --- |
| Shape.Enabled / Count | Enable cubes; 1..4096 instances, default 128 |
| Shape.Seed | Procedural arrangement |
| Shape.Size | Cube base half-size 0.2..1.5, with seeded variation |
| Shape.Spread | Position range per axis, half-width 2..12 |
| Shape.Speed | Individual cube rotation speed; zero freezes geometry |
| Shape.Albedo | RGB diffuse reflectance |
| Air.Density | Density multiplier, applied as `0.3*value^2`; default 0.65 |
| Air.Scattering / Absorption | Constant RGB coefficients; defaults (1,1,1) / (0.03,0.05,0.08) |
| Lights.Enabled | Enable transient strobes independently; default on |
| Lights.Shadows | Enable strobe shadow maps and sampled visibility; default off |
| Lights.Count | 1..96 fixed positions per ring; default 60 strobes + 60 tonal |
| Lights.Gain | Strobe-only intensity multiplier `2^(12*value-6)`; default 4x |
| Lights.Radius | Soft inverse-square radius `0.01+0.49*value^2`; default slider 0.25 |
| Lights.RingRadius | Ring radius as fraction of cloud half extent; default 0.6 |
| Lights.Directional | Hard strobe cones pointing toward origin; default on |
| Lights.HalfAngle | Half-angle `90*value` degrees, clamped to 5..60; default 1/3 = 30 degrees |
| TonalLights.Enabled | Enable horizontal S-driven ring independently; default on |
| TonalLights.Shadows | Enable tonal shadow maps and sampled visibility; default off |
| TonalLights.Gain | Independent tonal multiplier `0.75*2^(12*value-6)`; default 2/3 gives 3x |
| TonalLights.Directional | Hard tonal cones pointing toward origin; default off (full sphere) |
| TonalLights.HalfAngle | Same half-angle mapping as Lights.HalfAngle; default 1/3 |
| Quality.Samples | 1..128 shadowed distance samples per integrated ray; default 32; unshadowed LUT is independent |
| Quality.ShadowResolution | 128 / 256 / 512 useful pixels per cube face; default 512 |
| Quality.ShadowBias | Depth tolerance 0.0005..0.0205 world units along cube-face depth |
| Quality.HardwarePCF | Hardware 2x2 comparison filtering; enabled by default |
| Quality.HalfResolutionAirlight | Half width/height airlight with depth-aware reconstruction; default on |
| Sampling.OpticalDepthWeight | MIS mixture: 0 angular, 1 optical, default 0.5 |
| Sampling.Jitter | Stratified sampling noise; disable for midpoint inspection |
| Sampling.RandomLight | Evaluate one uniformly selected light per distance sample; default on |
| Filter.Enabled | Depth-aware 3x3 airlight filter at full resolution; enabled by default, bypassed in half-resolution mode |
| View.ShowDepth | Inspect camera distance |
| View.ShowShadows / ShadowLight | Inspect six shadow faces, including disabled lights |

Material has no density or edge-softness parameter. Air.Density=0 gives plain
opaque surfaces in vacuum. Shape.Enabled=false gives unoccluded atmospheric
scattering. Set both to zero/off for black regardless of enabled lights.

## Costs and captures

Moving geometry or changing ring placement, direction, aperture or projection type
rebuilds the affected shadow maps. Packing changes also invalidate moved slices.
With Shape.Speed=0 the maps survive changing audio, camera motion, air/material
coefficient changes and exposure changes. Camera surfaces render each frame.
Shadow storage scales with Lights.Count and the enabled shadow groups: each
20-light cone ring requires approximately 20.2 MiB at 512 useful pixels per view
including guards, or 5.1 MiB at 256. A 20-light omnidirectional ring needs six times
that storage (approximately 121 MiB or 30.5 MiB). These are allocation counts,
not performance measurements. With both shadow toggles off, only the tiny
placeholder shadow texture is allocated. Camera color, depth and
airlight targets add 20 bytes per pixel at full resolution, or approximately 14
with half-resolution airlight. There is no per-object loop in the airlight shader. Raster geometry
work still scales with instances, shadow views, coverage and overdraw. This POC
submits all instances to every active cube face and relies on GPU clipping/depth.

Earlier point-shadow measurements on Intel Arc Graphics, 640x360, four lights,
32 shared samples and 256-pixel faces unless stated; medians of 11 frames after
eight warmups. The current PCF default is measured in the shadow section above:

| Case | Shadow maps total, approximately ms | Surface ms | Airlight ms | Total ms |
| --- | ---: | ---: | ---: | ---: |
| Default 128 cubes | 0.25 | 0.11 | 4.05 | 4.48 |
| 512 cubes | 0.30 | 0.18 | 2.86 | 3.43 |
| 4096 cubes | 0.84 | 0.36 | 2.82 | 4.10 |
| One light | 0.07 | 0.06 | 1.05 | 1.23 |
| Static maps, moving camera | cached | 0.13 | 2.93 | 3.12 |
| 128 samples | 0.24 | 0.11 | 10.59 | 11.04 |
| 512-pixel faces | 0.34 | 0.11 | 3.21 | 3.76 |
| No air | 0.27 | 0.12 | 0.04 | 0.48 |
| 1280x720 | 0.26 | 0.26 | 12.07 | 13.08 |

First-case GPU clocks varied: a separate default capture took 3.35 ms total.
Different object counts also change geometry and camera integration intervals, so
these are exploratory figures, not identical-image scaling measurements.

After changing renderer startup to request DXGI's high-performance adapter, the
same default capture selected **NVIDIA GeForce RTX 4070 Laptop GPU** and took
1.80 ms total: about 0.10 ms for all four cubemaps, 0.03 ms for surfaces and
1.62 ms for airlight. Its local capture/timings are under
`artifacts/mandellight-v4/high-performance`. The settings timing panel now reports
the selected GPU; the Arc measurements above describe the earlier default-adapter
path.

Run from the repository root:

```powershell
dotnet run --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-v4/current --v4
dotnet run --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-v4/filter --v4 --filter
dotnet run --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-v4/shadows --v4 --shadows
dotnet test AEther.Tests/AEther.Tests.csproj --filter FullyQualifiedName~MandelLightRasterTests
```

Use --quick for default only and --small for 320x180 captures. Focused GPU tests
cover cache invalidation, resize, reload, lights-off behavior and all three
optical/angular mixture settings against independent numerical quadrature through
an infinite homogeneous medium. A synthetic HDR impulse/depth-boundary regression
checks the 3x3 weights, foreground/sky rejection, finite-depth rejection and filter
toggle behavior. A hardware-PCF probe compares against explicit four-depth
comparisons at 128, 256 and 512 face sizes, including cube-seam directions and
fractional shadow coverage. The unoccluded reference does not measure shadow
map accuracy or shadowed sampling convergence.

Local ignored artifacts: [default](../artifacts/mandellight-v4/final/default.png),
[surfaces without air](../artifacts/mandellight-v4/final/no-air.png),
[128 samples](../artifacts/mandellight-v4/final/samples128.png),
[4096 cubes](../artifacts/mandellight-v4/final/objects4096.png),
[shadow faces](../artifacts/mandellight-v4/final/shadow-cube.png),
and [timings](../artifacts/mandellight-v4/final/timings.json).

Reference for exponential homogeneous-medium sampling:
[PBRT, Sampling Volume Scattering](https://www.pbr-book.org/3ed-2018/Light_Transport_II_Volume_Rendering/Sampling_Volume_Scattering).
Depth resource/view formats follow
[Direct3D depth-stencil guidance](https://learn.microsoft.com/en-us/windows/win32/direct3d11/d3d10-graphics-programming-guide-depth-stencil).
