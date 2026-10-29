# MandelLightv2: analytical volume prototype

Select **MandelLightv2** in the scene selector. Scene discovery and hot reload use
the existing shader authoring path; no new renderer resources or passes are needed.

This is an audio-independent proof of concept and intentionally remains visible
without music. It has no image history or simulation. Like MandelVolume, it is explicitly excluded from production silence/audio-response
regressions. Production audio reactivity must gate its lights and emission before
removing that exemption.

## Model

Up to sixteen procedural ellipsoids contribute additive material concentration:

```text
u = (p - c) / r                       // in the cloud's shared rotated space
rho(p) = strength * max(0, 1-dot(u,u))^3
material(p) = sum rho_i(p)
```

Here `c` is a blob center and `r` contains its three radii. The cloud rotates as a
rigid body; individual ellipsoids are aligned in cloud space. A seeded sequence
chooses positions, radii and strengths. Increasing Count adds blobs without moving
the existing ones. The first blob sits closer to the center to anchor the cluster.
Overlaps are denser, with no density clamp, threshold surface or surface shading.
All allowed blob shapes fit inside the radius-16 air support.

Air is one broad spherical instance of the same cubic kernel. It adds to material
extinction and scattering; material does not displace it. There is no
atmosphere lookup table in v2.

Material and air use RGB single scattering with the normalized Rayleigh phase
`3*(1+mu*mu)/(16*pi)`. Lights retain softened inverse-square falloff. Up to four
lights illuminate the same material. Emission adds radiance locally and does not
illuminate neighboring material.

## Exact attenuation, sampled illumination

`Common/AnalyticVolumes.hlsl` computes ellipsoid ray chords. Along an intersecting
ray, each density reduces to:

```text
rho(t) = peak * (1-z*z)^3,       -1 < z < 1
z = (t-middle)/half_length
full_column = peak * half_length * 32/35
```

Its normalized cumulative integral is a Beta(4,4) polynomial. The implementation
evaluates the smaller tail to reduce cancellation near chord endpoints. Complete
and empty intervals have direct shortcuts. Camera-to-sample and sample-to-light
column densities are evaluated analytically, including overlaps, cavities, inside
cameras and lights passing through several blobs. All component optical depths
are summed before one RGB exponential per segment:

```text
k = 4 * Material.Density^2 * (Material.Scattering + Material.Absorption)
a = 0.12 * Air.Density^2 * Air.Scattering
T = exp(-k*material_column - a*air_column)
```

Attenuation is exact for the defined density fields up to floating-point error.
Scattered illumination is still a sampled integral; exact attenuation does not
make the whole image an exact solution of volume transport. Multiple scattering
is not modeled.

One spatially stratified sequence samples a normalized mixture:

* Density tents over intersected camera chords. Selection weights use analytical
  column mass, source strength and approximate attenuation. A rational CDF warp
  favors the near side of dense components; inversion needs no iteration.
* A mixture of equiangular proposals for enabled lights, using their softened
  perpendicular distances. These proposals catch peaks near the point lights.
* A uniform floor of at least 5% across the ray interval.

The estimator evaluates the complete mixture PDF at every sample. The guide's
approximations change placement, not the density or the attenuation model. The
same samples evaluate camera density and transmission once, then accumulate all
lights and emission. Jitter is deterministic in screen space with no time jitter
or temporal accumulation. Low sample counts can show grain. Fixed midpoint
sampling was tried and rejected because it produced coherent contours.

## Cost and resource choices

The scene is one full-screen pixel shader. It uses no transport render targets,
moment reconstruction, angular cache, transport marches, epipolar lookup,
gradient evaluation, surface proxies or temporal feedback. Normal frame constants
and the reflected Parameters constant buffer are sufficient.

Camera chords are compacted per pixel: only intersected material blobs enter the
camera-density, camera-transmission and proposal loops. Shadow segments first
check a conservative cluster bounding box; misses avoid all material integrals.
All blobs still participate in shadowing when appropriate. These checks bound
density support and do not shade or occlude a proxy shape.

Cost grows with integration samples, active lights, blob count and projected
overlap. Sixteen blobs and inside cameras can be substantially more expensive
than the default. Count, Size and Spread all affect overlap and screen coverage.
The default eight-blob cluster is intended as a practical starting point.

### Rough GPU measurements

Intel(R) Arc(TM) Graphics, D3D11; median of 21 frames after 20 warmups. Compilation
and readback are excluded. Unless specified: 640 by 360, 8 blobs, 2 lights, 32
shared samples, moving camera/cloud/lights, emission off. GPU clocks and other GPU workloads can affect the timings.

| Case | GPU ms |
| --- | ---: |
| Default | 6.8 |
| One blob | 2.9 |
| Sixteen blobs | 37.9 |
| 64 samples | 12.3 |
| One light | 5.0 |
| Four lights | 12.6 |
| Camera inside | 26.1 |
| Maximum material density | 6.7 |
| Emission only | 2.1 |
| Air only | 2.8 |
| Default at 1280 by 720 | 27.7 |

Captures and raw timings are in the local ignored directory
`artifacts/mandellight-v2/release`. The first implementation used a single large
array for geometry and camera data, visiting all blobs in every camera sample;
it took roughly 29 ms at 640 by 360 with 24 samples and two lights. Separating
geometry, compacting camera intersections, and culling shadow segments brought
the 32-sample default down to the figures above. Fully unrolling the blob loops
was tried and rejected: it substantially worsened shader compilation and GPU cost.

## Controls

Every float control follows the normalized [0,1] contract.

| Control | Mapping / behavior |
| --- | --- |
| `Shape.Count` | Nearest integer 1–16; default 8 |
| `Shape.Seed` | Integer seed 0–65535; changes the procedural cluster |
| `Shape.Size` | Base radius 0.7–4; seeded aspect ratios and first-blob enlargement also apply |
| `Shape.Spread` | Center distribution radius 0–5.5; default 3.3 |
| `Shape.Speed` | Cloud rotation 0–0.2 radians/second; zero freezes it |
| `Material.Enabled` | Disables material scattering, absorption and emission; air remains |
| `Material.Density` | Concentration multiplier `4*value^2`; default 1.21 |
| `Material.Scattering`, `Absorption` | Linear RGB coefficients; both contribute to extinction |
| `Air.Density` | Peak air multiplier `0.12*value^2`; default 0.0048 |
| `Air.Scattering` | RGB scattering/extinction coefficients for the air kernel |
| `Emission.Enabled` | Independent local emission; default off |
| `Emission.Strength` | `4*value^2` per unit blob concentration; independent of Material.Density |
| `Emission.Color` | Linear RGB |
| `Light` through `Light4` | Enabled, RGB color, intensity, speed, phase and softening radius |
| `Light.Intensity` | `3000*value^2` point intensity |
| `Light.Speed` | Orbit speed 0–0.5 radians/second |
| `Light.Phase` | One full orbit |
| `Light.Radius` | Softening radius `0.01+0.49*value^2` |
| `Quality.Samples` | 1–64 shared samples per camera ray; default 32, independent of light count |
| `View.CameraDistance` | 0–40 world units; zero places the camera inside the cluster |
| `View.CameraSpeed` | Camera orbit 0–0.25 radians/second |
| `View.HorizontalOffset` | Translation from minus to plus one camera distance along camera-right |
| Graphics `Exposure` | Shared exposure in stops, -12 to +12; default 0 (unity) |

Zero material density restores the air-only result when emission is off. Emission
can deliberately remain visible at zero material density. With all lights disabled
and emission off the image is black. There are no ambient or procedural background
light sources. Air is visible only where a light illuminates it.

## Preview and profiling

```powershell
dotnet run --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-v2/current --v2
```

The tool saves PNGs and GPU timings for default, 1/16 blobs, 64 samples, 1/4 lights,
inside camera, high density, emission, air only and 1280 by 720. Use `--quick` for
the default only or `--reference` for a copied shader with 256 shared samples.
The latter is a sampling reference, not a multiple-scattering reference. Source
shaders are not modified. No regression tests accompany this prototype.
