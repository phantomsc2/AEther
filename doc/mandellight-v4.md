# MandelLightv4: raster geometry and infinite homogeneous air

Restart after building, then select **MandelLightv4**. This audio-reactive prototype
now uses live spectral transients for all illumination. Without live T it renders
black, including after active input; it participates in the GPU silence regression.
It intentionally does not respond to a B-only/global-background fixture.

The default preset favors dense, strongly scattering air: Air.Density is 0.65,
Scattering is (1,1,1), and the low absorption remains (0.03,0.05,0.08). Half-resolution
airlight and one-light-per-sample sampling are enabled, with 32 distance samples,
hardware PCF and 512-pixel shadow faces. The scene retains 128 cube instances,
with a static 20-light spectral ring inside the cloud.
After reloading, use **Reset defaults** to apply the new preset to an existing session.

A 640x360 RTX 4070 Laptop capture with a synthetic live stereo-T fixture measured
approximately 1.54 ms total: 0.99 ms for 20 shadow cubemaps, 0.10 ms surfaces,
0.27 ms airlight and 0.10 ms reconstruction/composition. All lights were active
and the cubes were rotating, requiring shadow-map refreshes. Capture and timings
are in `artifacts/mandellight-v4/audio-ring-final`. Other timing tables below
describe the earlier four-light prototypes unless stated otherwise.

## Live spectral light ring

Lights.Count selects 1..32 point lights, default 20. Their positions form a fixed
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
becomes active receives a valid shadow map before it is used. Intensity changes
do not invalidate maps; geometry motion, ring placement/count, geometry edits,
resolution changes and reloads do.

The random-light mode constructs the angular proposal only for the selected
nonzero light at each distance sample. The all-light comparison path retains only
one PDF scalar per active light instead of a full array of ray/light descriptions.
With 32 samples and all 20 lights active, each ray evaluates each light once or
twice; expect more selection noise than the earlier four-light scene. There is
still no temporal accumulation.

## Rendering

The scene draws 1..4096 procedural cube instances with 36 vertices each. Seeded
positions, sizes, orientations and albedo variation are calculated by the vertex
shader. Increasing Count preserves previous instances. There are no Fourier modes,
surface distance estimates, material interiors or object translucency.

Each active point light rasterizes six perspective depth views into a scalar
R32 depth array. Only the nearest surface is needed for opaque visibility.
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

Every face also renders a one-texel guard on each side: a 512 face is stored as
514x514 with slightly expanded projection coverage. Lookup coordinates address the
central 90-degree region. Thus the full 2x2 filter footprint remains inside valid
rendered data even at cube edges and corners, without manual cross-face taps.

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

With Sampling.RandomLight disabled, all lights share camera sample positions.
There are two proposal families:

* **Optical depth:** truncated exponential distances for surface rays, ordinary
  exponential distances for infinite background rays. Its scalar rate follows the
  slowest attenuating RGB channel that scatters, to preserve a useful tail.
* **Equiangular:** one proposal per active light, covering the same camera-ray
  interval. It follows the softened inverse-square illumination peak.

Sampling.OpticalDepthWeight supplies the optical proposal's probability; the
remaining probability is divided equally among active light proposals. Stratified
samples select a component and a distance through its inverse CDF. The estimator
uses balance-heuristic multiple importance sampling through the full mixture PDF:

```text
q(t) = w * q_optical(t) + (1-w)/light_count * sum q_angular_light(t)
sample_contribution = full_scattering_integrand(t) / (sample_count * q(t))
```

Every position evaluates illumination from all active lights. Neither proposal's
weight is a brightness control. Slider 0 gives pure equiangular sampling; 1 gives
pure optical-depth sampling; default 0.5 combines both. No third proposal is
needed for full support. Floating-point inverse-CDF endpoints are clamped just
inside their domains to avoid infinite/undefined sample coordinates.

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
MIS weights, four-light evaluation and sample counts retain the same mathematical
model; only floating-point evaluation order changes.

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
| Lights.Enabled | Enable all ring lights; default on |
| Lights.Count | 1..32 fixed ring positions; default 20 |
| Lights.Gain | Live-T intensity multiplier `2^(12*value-6)`; default 4x |
| Lights.Radius | Soft inverse-square radius `0.01+0.49*value^2`; default slider 0.25 |
| Lights.RingRadius | Ring radius as fraction of cloud half extent; default 0.6 |
| Quality.Samples | 1..128 distance samples per integrated ray; default 32 |
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

Moving geometry or changing ring placement rebuilds the affected shadow cubemaps.
With Shape.Speed=0 the maps survive changing audio, camera motion, air/material
coefficient changes and exposure changes. Camera surfaces render each frame.
Shadow storage scales with Lights.Count: 20 lights require approximately 121 MiB
at 512 useful pixels per face including guards, or 30.5 MiB at 256. Camera color, depth and
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
