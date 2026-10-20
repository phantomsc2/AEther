# MandelLightv4: raster geometry and infinite homogeneous air

Restart after building, then select **MandelLightv4**. This audio-independent
prototype is exempt from the production silence/audio-response scene lists.
Turning all lights off still immediately produces black. Production use needs
audio gating before removing the prototype exemption.

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

Surface lighting includes light-to-surface air extinction and hard shadow queries.
A fullscreen pass samples air scattering up to the visible surface, or all the
way to infinity for background rays, into an HDR texture. A second pass optionally
filters that airlight and composites:

```text
pixel_radiance = airlight + exp(-sigma_t * surface_distance) * surface_radiance
```

This is single scattering with a normalized Rayleigh phase function. There is no
ambient/environment source or multiple scattering. Light Radius regularizes the
inverse-square falloff; it does not turn the light into an area emitter.

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

All lights share the camera sample positions. There are two proposal families:

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

Jitter uses fixed screen-space hashes, with no temporal accumulation or time
jitter. Disabling it replaces the random offset within each stratum by its
midpoint. Shadows use nearest texel lookup, with no PCF, blur or soft silhouette
filter. Surface comparisons evaluate the receiver plane along the actual shadow
texel's center ray to reduce slope acne. ShadowBias is an additional small depth
tolerance. Sampling grain, angular shadow aliasing and shadow bias error remain.
Hard material edges are preserved; all physical haze is surrounding airlight.

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
| Air.Density | Density multiplier, applied as `0.3*value^2` |
| Air.Scattering / Absorption | Constant RGB coefficients |
| Light through Light4 | Four lights with color, intensity, orbit and motion |
| Quality.Samples | 1..128 shared camera samples; default 32 |
| Quality.ShadowResolution | 128 / 256 / 512 pixels per cube face; default 256 |
| Quality.ShadowBias | Depth tolerance 0.0005..0.0205 world units along cube-face depth |
| Sampling.OpticalDepthWeight | MIS mixture: 0 angular, 1 optical, default 0.5 |
| Sampling.Jitter | Stratified sampling noise; disable for midpoint inspection |
| Filter.Enabled | Depth-aware 3x3 airlight filter; enabled by default |
| View.ShowDepth | Inspect camera distance |
| View.ShowShadows / ShadowLight | Inspect six shadow faces, including disabled lights |

Material has no density or edge-softness parameter. Air.Density=0 gives plain
opaque surfaces in vacuum. Shape.Enabled=false gives unoccluded atmospheric
scattering. Set both to zero/off for black regardless of enabled lights.

## Costs and captures

Moving geometry or lights rebuilds the affected shadow cubemaps. Stationary light
maps survive camera motion, air/material coefficient changes and exposure changes.
Camera surfaces render each frame. The light caches contain 6 MiB of depth at
256 pixels per face for four lights, or 24 MiB at 512. Camera color, depth and
airlight targets add 20 bytes per pixel. There is no per-object loop in the airlight shader. Raster geometry
work still scales with instances, shadow views, coverage and overdraw. This POC
submits all instances to every active cube face and relies on GPU clipping/depth.

Intel Arc Graphics, 640x360, four lights, 32 shared samples and 256-pixel faces,
unless stated; medians of 11 frames after eight warmups:

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
dotnet test AEther.Tests/AEther.Tests.csproj --filter FullyQualifiedName~MandelLightRasterTests
```

Use --quick for default only and --small for 320x180 captures. Focused GPU tests
cover cache invalidation, resize, reload, lights-off behavior and all three
optical/angular mixture settings against independent numerical quadrature through
an infinite homogeneous medium. A synthetic HDR impulse/depth-boundary regression
checks the 3x3 weights, foreground/sky rejection, finite-depth rejection and filter
toggle behavior. The unoccluded reference does not measure shadow
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
