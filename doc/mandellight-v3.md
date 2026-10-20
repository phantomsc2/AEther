# MandelLightv3: Fourier volume prototype

Restart the application after building, then select **MandelLightv3**. V3 adds host
resources and a light transport pass; subsequent shader edits use normal hot reload.
V1 and v2 remain available. This is an audio-independent prototype, explicitly
exempt from production silence/audio-response regressions. Production use must
add audio gating before removing that exemption.

## What this proves

Procedural source densities are aggregated on the CPU into **125 real Fourier
coefficients**. Camera rendering and light transport evaluate that fixed-size
field, with no per-object loops. Four lights each render a camera-independent deep
cubemap containing cumulative material along their rays. The camera samples these
curves to obtain light transmission and godrays.

The important compromise: 512 sources do **not** become 512 independently resolved
objects. They become one smooth, band-limited density field. The implementation
uses normalized source weights, so raising Count blends the field rather than
making it arbitrarily opaque. Many sources eventually converge to a smoother
shape. This is a representation experiment, not yet a general scene renderer.

## Positive analytical density

Each source is separable in the cloud's rotated coordinates. Per axis:

```text
h(x) = (1 + cos(x)) / 2
a(x, p) = h(x) * h(x-p) / cos(p/4)^4
source(x,y,z) = a(x,px) * a(y,py) * a(z,pz)
rho = weighted_average(source)
```

Here `p` is a seeded source phase reduced to [-pi,pi]. Each source is nonnegative
and normalized to peak one. The common `h(x)` factor makes density and its first
derivative vanish at the shared box boundary. Inside that box, each axis contains
frequencies -2 through 2, so the tensor basis has 5*5*5 = 125 real degrees of
freedom. Real symmetry allows storage as DC plus 62 conjugate pairs. Outside the
box, material density is zero; individual sources have no separate clipping.

Shape.Repetitions selects 1, 3 or 5 periods across the box. Default 3 produces
repeated cloudlets and more interesting shafts, but these repetitions are literal:
they do not add independent Fourier degrees of freedom. Source generation uses
four seeded clusters. Motion changes their phases; Speed rotates the whole cloud.

Along a ray, every mode has an analytical sine/cosine antiderivative. Thus segment
material mass is analytical up to floating-point error. The camera integrates
whole cells with sinc-weighted modes and advances phases by recurrence. Cells
cut by the box boundary use the direct analytical segment formula.

Air adds a radius-16 polynomial sphere using the same analytical kernel as v2.
RGB extinction, Rayleigh single scattering, softened point lights and local
emission remain. There are no surfaces, normals, BRDFs, fractal iterations,
distance estimates, G-buffer, epipolar coordinates or temporal accumulation.

## Deep cubemaps

For each light and cube texel:

1. Intersect the ray with the shared material box.
2. Evaluate analytical cumulative mass at 17 candidate depths.
3. Greedily retain eight depth/mass knots by maximum linear interpolation error.
4. Store these monotone curves in four RGBA16F texture arrays.

The camera reconstructs a piecewise linear cumulative column at its distance from
the light. RGB material extinction is applied afterward, so changing absorption,
scattering or density does not require rebuilding these maps. Air attenuation is
evaluated analytically outside the cache. Empty rays and receivers past the box
have cheaper paths. Cube face addressing also handles taps crossing a seam.

Default filtering picks one of the four angular neighbors stochastically with
bilinear weights, using a fixed screen-space hash per sample/light. This adds
grain, especially at high density. **FourShadowTaps** evaluates and filters all
four transmissions for a cleaner, more expensive image. Camera cells themselves
are uniform and deterministic; there is no camera-position jitter or history.

**ExactShadows** bypasses the maps and evaluates analytical light segments at every
camera sample. It is an expensive comparison mode. It does not make scattering
integration exact: illumination and the mixture of air/material sources are still
approximated within each camera cell. Point-light peaks can need more samples.

Maps rebuild when geometry, cloud rotation or light positions change. Camera
motion, exposure, light colors and material coefficients reuse them. Static
geometry also reuses CPU coefficients. At 64 pixels per face, all four lights use
3 MiB of deep-map storage; 128 pixels uses 12 MiB. Coefficients occupy 1 KiB.

## Controls

All numeric UI sliders are normalized to 0..1 unless noted.

| Control | Meaning |
| --- | --- |
| Shape.Count | 1..512 procedural sources; default 64 |
| Shape.Seed / Spread | Source pattern and phase spread |
| Shape.Size | Shared box half-width 4..8 |
| Shape.Repetitions | 1 / 3 / 5 spatial periods; default 3 |
| Shape.Motion / Speed | Source deformation / rigid rotation |
| Material.Density | Concentration multiplier `4*Density^2` |
| Material.Scattering / Absorption | RGB material coefficients |
| Air.Density / Scattering | Independent surrounding participating medium |
| Emission | Local material emission, independent of Material.Density |
| Light through Light4 | Four independent lights; Orbit sets radius 3..14 |
| Quality.Samples | 1..128 camera cells; default 64 |
| Quality.ShadowResolution | 32 / 64 / 128 pixels per cube face |
| Quality.FourShadowTaps | Cleaner angular filtering at higher camera cost |
| Quality.ExactShadows | Direct analytical reference, bypasses deep maps |
| View.ShowDensitySlice / Slice | Inspect density in a cloud-space cross-section |
| View.ShowTransport | Inspect six faces for TransportLight at TransportDepth |

Zero material density removes material scattering and extinction. Air and enabled
emission are separate controls; disable them too for an empty scene.

## Profiling and remaining limits

Run from the repository root on Windows with a D3D11 GPU:

```powershell
dotnet run --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-v3/current --v3
```

The tool writes PNG captures and timings.json. GPU medians cover 11 frames after
eight warmups, excluding compilation and image readback. CPU aggregation is the
last measured aggregation call, not a per-frame median; cached frames do no new
aggregation. Individual pass medians need not sum to the median total. See the
tool README for quick and direct-shadow reference modes.

First captures on Intel Arc Graphics, 640x360, 64 camera cells and four 64x64-face
light maps unless specified:

| Case | Camera GPU ms | Total GPU ms |
| --- | ---: | ---: |
| Default, 64 sources | 24.0 | 34.5 |
| 1 source | 15.9 | 23.6 |
| 128 sources | 15.2 | 23.0 |
| 512 sources | 15.8 | 23.4 |
| 512 deforming sources | 15.9 | 24.0 |
| Static maps, moving camera | 16.0 | 16.5 |
| One light | 7.7 | 9.2 |
| Four angular taps | 22.5 | 30.6 |
| 128 camera cells | 29.4 | 37.2 |
| 128x128 cube faces | 15.8 | 27.6 |
| 1280x720 | 60.6 | 68.6 |

Four moving light maps together usually cost about 7.1 ms. Their individual times
were effectively unchanged between 1 and 512 sources. Source count changes the
image, and first-case clocks differed, so these are exploratory timings rather
than controlled identical-image scaling measurements. The final 512-source
deformation aggregation call took 2.2 ms on the CPU; static source aggregation is
cached. At 320x180, the cached-curve pipeline took 11.2 ms total versus 61.5 ms for
direct analytical shadows at the same camera sample count.

Local captures: [default](../artifacts/mandellight-v3/final/default.png),
[higher density](../artifacts/mandellight-v3/final/dense.png),
[density slice](../artifacts/mandellight-v3/final/density-slice.png),
[deep cubemap](../artifacts/mandellight-v3/final/transport.png), and
[timings](../artifacts/mandellight-v3/final/timings.json).
The matched 320x180 [approximation](../artifacts/mandellight-v3/approximate/default.png)
and [direct-shadow reference](../artifacts/mandellight-v3/reference/default.png)
show the remaining cache error; both retain camera quadrature error. These
artifacts are generated locally and ignored by Git.

The camera pass still scales with pixels, camera cells and lights, and evaluates
63 stored mode pairs for material cells. Map construction scales with lights and
cube pixels, using fixed-size field integrals. Source count affects CPU aggregation,
not GPU loop bounds. Moving every source requires aggregation each frame.

This POC deliberately accepts periodic shapes, low-frequency smoothing, eight-knot
depth approximation, coarse angular shadows, half-precision storage and sampling
artifacts. There is no multiple scattering or general-object visibility buffer.
The main next question is whether this restricted density vocabulary earns its
fixed GPU cost compared with rasterized local primitives and richer light caches.
