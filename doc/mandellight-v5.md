# MandelLightv5: shader-defined point lights

Select **MandelLightv5** after building the host. The scene retains v4's atmosphere
and procedural cube cloud, with three fixed perpendicular rings of point lights:
S in XZ, T in XY, and B in YZ. Each ring samples its corresponding live
`Spectrum.rgb` component, using the angular frequency palette and stereo blend.
The shader samples row zero at UV.y=.25 in the two-row spectrum textures,
keeping diagnostic data out of the light intensity.
Intensity is linear in the component, with no additional history or ambient floor.
Silence renders black. The rings stay fixed while the camera and cubes can move.

Defaults are 128 cubes, 60 lights per ring, radius 5.7, and shared gain 4x.
`Lights.Count` maps to 1..96 lights per ring, `Radius` to 0..10 world units,
and `Gain` to `2^(12*value-6)`. `Enabled` removes all lights. Geometry, atmosphere,
camera, exposure, and depth preview retain their v4 meanings.

`Lights.SampleAirlight` is the single sampling toggle, enabled by default. It
builds a GPU suggestion grid, with **M=2** stratified light samples per node.
Nodes are spaced at most 16 pixels apart, including the image edges. A compute
shader reads the current GPU light records and weights them by intensity,
inverse distance to the infinite camera ray, and scalar atmospheric attenuation.
Selection still uses infinite proposal rays. The baseline and final integral
end at the corresponding ray's surface, or infinity for background rays.

Each node stores two `(uint index, float coefficient)` records in a structured
buffer. The coefficient is `1/(M*q)`, where `q` is the sampled light's probability
at that node. Each full-resolution pixel gathers four neighboring nodes, applies
their depth-aware bilinear weights, and evaluates their eight suggestions using the complete
RGB point integral on its own ray. Indices are loaded discretely. Duplicate
indices are valid and retain their separate estimator weights.

The coarse pass also caches one RGB baseline contribution per active light and
node, plus their RGB sum. The pixel starts from the interpolated sum and samples
only the difference between actual and interpolated per-light contributions:

```text
F(i) = full RGB contribution of light i on the pixel's ray
a(c) = normalized depth-compatible bilinear corner weight; sum(a) = 1
b(i) = sum over corners c: a(c) * cachedBaseline(c,i)
B = sum over corners c: a(c) * cachedSum(c) = sum over lights i: b(i)
estimate = B + sum over corners c and samples m:
               a(c) * [F(i[c,m]) - b(i[c,m])] / (M*q[c,m])
```

Each cached RGB sum also stores the node's ray endpoint in its fourth float.
For finite endpoints, compatibility is `max(0, 1-abs(t-nodeT)/(.05*max(t,nodeT)))^2`,
with a numerical denominator guard. Background nodes match only background pixels.
The four bilinear weights are multiplied by compatibility and renormalized. The
same weights are used for the RGB sum, per-light baseline, and corner estimators.
No new shader parameters or rendering passes are added; M remains 2.

If compatible weight totals at most 1e-6, the pixel instead uses zero baseline
and the original bilinear corner sampling weights. This is ordinary compensated
light sampling: no incorrect layer is interpolated, and the full light sum is
still preserved in expectation. It can be noisy on thin objects that none of the
four nodes see. Relative depth can also reject valid samples on sloping surfaces;
this simple version deliberately uses no normals or additional depth layers.

Every corner residual estimator has the same expected value, the full light sum
minus B. Adding B preserves the full sum in expectation. A baseline that exactly
reconstructs each light removes sampling variance entirely. Residuals, including
negative estimates, remain signed in the floating-point airlight target until
the scene's final display mapping. All light-count-dependent
weight scans and selection run at grid resolution; the sampled full-resolution
pass evaluates at most eight integrals regardless of light count. Switching the
toggle off sums every light as a noise-free reference. Surface lighting always
sums every light.

Sampling uses a fixed spatial pattern, so a stationary frame remains stable.
The baseline removes most broad patches in the current fixture. Residual variance
remains near depth discontinuities and sharp light centers. There is no temporal
accumulation or image filtering. Configured counts
of eight or fewer lights use the exhaustive sum. A small uniform probability
mixture preserves support for lights whose
contribution the approximation underestimates, including colored attenuation.
Proposal distance/brightness guards do not change the physical point-light kernel.
Selected exact on-ray sources retain their singular radiance. Sampling cannot
guarantee selecting every singular source; the exhaustive toggle remains the
reference for those cases.

Only the approximation's core is bounded: its perpendicular distance is at least
the light's distance times the coarse node's angular footprint. This keeps a node
on a point singularity from creating a huge interpolated baseline across a cell.
Cached values are capped at 1e20 to keep baseline sums and subtractions finite.
Both changes are compensated by the signed residual; the actual point-light
kernel is unchanged. Current node depths are used after the surface pass. A depth
edge is handled by rejecting incompatible endpoints before interpolation, without
changing the estimator's expected value before final display clamping.

## Point-light contract

Include `Common/RasterGeometry.hlsl`, `Common/PointLights.hlsl`, and
`Common/PointRasterAirlight.hlsl`. Implement the geometry callbacks,
`ProjectionCamera SceneCamera()` using the [shared projection layer](projection-contract.md), and:

```hlsl
uint LightCount();
PointLight EvaluateLight(uint index);
bool UsePointLightSampling(); // Scene callback controlling airlight source selection.
// PointLight contains only float3 Position and float3 Intensity (RGB).
```

`PreparePointLights` runs once per frame and writes a GPU count and a structured
buffer with 24-byte records. The host allocates capacity for 512 lights and
dispatches eight groups of 64 threads; HLSL clamps the count to this capacity.
Records below the current count are overwritten every frame. A smaller or zero
count immediately excludes previous records. Zero-intensity records are skipped
by the lighting shaders. The CPU never generates positions, intensities, counts,
or active lists, and never reads them back during rendering. Callbacks can use
frame constants, parameters, and spectrum/history textures.

`PreparePointLightSuggestions` follows light generation and the surface/depth draw,
with 8x8 compute groups, one thread per grid node. It uses two light scans at each
node: baseline integration plus total proposal weight, then stratified CDF
selection with actual interval widths for compensation. The shared CPU-generated
LUT is available to this compute pass as well as the full-resolution pixel pass.
At 1080p the grid is 121x69 nodes, versus 2,073,600 output pixels. Suggestions
use about 130 KiB, and the RGB sum plus endpoint another 130 KiB. The per-light RGB buffer reserves
all 512 GPU-defined light slots: about 51 MB at 1080p and 90 MB at 1440p. Only active
records are written and sampled. The host allocates/resizes buffers and schedules
the dispatch; it does not read back the active count to size the cache. The
existing boolean controls shader work; no additional user parameters are added.

The distinct `PointSurface` resource selects `ProceduralPointLightTransport`.
It prepares geometry and lights, draws Lambertian surfaces/depth with the existing
indirect geometry draw, integrates homogeneous Rayleigh airlight, and runs the
scene's final composition. `PointSurface`, `PointDepth`, and `PointAirlight` are
all full resolution. There are no shadow maps, cone lights, filaments, filtering,
temporal accumulation, or half-resolution rendering in this backend.

The host recognizes only the existing normalized `Air.Density/Scattering/Absorption`
medium controls and `View.ShowDepth`. The scene owns the names and meaning of its
geometry and light controls. The shared CPU-generated `UnshadowedAirlightLookup`
is renderer-owned and reused across v4/v5, including its coefficient cache and
quadrature fallback while a table is pending. Point-integral math is shared with
v4; v4 retains its shadow and other existing features.

## Validation and profiling

The GPU tests exercise STB isolation/stereo, shader counts and positions, capacity
clamping, silence, LUT quadrature agreement, edits, resize, reload, and scene switching.
Sampling checks compare averages in linear radiance against exhaustive finite and
infinite integrals, exercise identical lights and strongly chromatic attenuation,
and verify that the toggle leaves surface radiance unchanged. A GPU readback
test checks every pixel of a synthetic contribution against an independent
baseline-plus-residual estimator, including negative residuals, cell boundaries,
odd sizes, 1x1 rendering, and
clearing stale records after lights are disabled.
Reference cases also cover zero-variance reconstruction, deliberately mismatched
depth endpoints, and a sharp source inside the approximation's bounded core.
The interpolation oracle covers background-only, mixed foreground/background,
partially compatible sloping depths, and an all-corners-rejected fallback.
Run the profiling tool with `--point-lights` for 1280x720 individual-ring and full
scene captures, per-pass GPU medians, and a v4 point-only cost comparison:

```powershell
dotnet run --project tools/AEther.MandelLightProfile -- --point-lights
```

Results and shader snapshots are written to `artifacts/mandellight-v5`. The v4
comparison uses two 90-light rings versus v5's three 60-light rings, both at full
resolution with 128 cubes. It compares cost rather than image equality.
The `stb`/`stb-exhaustive` and `stb-288`/`stb-288-exhaustive` captures compare
sampling on/off at 180 and 288 lights with the same geometry and audio fixture.
Use `--sampling-only --1080p` or `--sampling-only --1440p` for the paired grid
and exhaustive cases at higher resolutions. Grid preparation has its own GPU marker.

The depth-aware version's 2026-10-29 Intel Arc 2560x1440 capture measured these
medians over 41 warmed frames with the same M=2 and stationary STB fixture:

| Lights | Baseline + suggestions | Depth-aware residual pass | Combined | Frame total |
| --- | ---: | ---: | ---: | ---: |
| 180 | 3.033 ms | 2.459 ms | 5.492 ms | 20.014 ms |
| 288 | 3.978 ms | 2.637 ms | 6.615 ms | 29.664 ms |

The full-resolution pass was 0.13/0.22 ms slower than the prior captures below.
Unchanged surface timings also varied between runs, so these differences are
indicative rather than an isolated measurement of the compatibility arithmetic.
The endpoint adds four bytes per coarse node (about 59 KB at 1440p), four cached
endpoint reads, compatibility calculations, and weight normalization; there is
no additional pass, light scan, or full-resolution integral budget.

Against the same exhaustive PNGs, display RGB RMSE fell from 0.01231 to 0.00360
at 180 lights and from 0.01282 to 0.00327 at 288 lights, about 3.4x/3.9x lower.
Captures, shader snapshots, timings, and the comparison script/JSON are in
`artifacts/mandellight-v5/depth-aware`. Thin or steeply sloping geometry can still
reject all corners and show ordinary sampling noise locally.

Before depth-aware interpolation, the baseline-plus-residual experiment's
2026-10-29 Intel Arc 2560x1440 capture
measured these medians over 41 frames after LUT warmup, with the same stationary
scene, M=2, and STB fixture:

| Lights | Baseline + suggestions | Residual pass | Combined | Frame total |
| --- | ---: | ---: | ---: | ---: |
| 180 | 2.862 ms | 2.328 ms | 5.190 ms | 18.607 ms |
| 288 | 4.052 ms | 2.422 ms | 6.474 ms | 28.020 ms |

The new captures are in `artifacts/mandellight-v5/residual/1440p`. Broad colored
patches are greatly reduced, with remaining errors concentrated near geometry
silhouettes and light centers. The `Point light suggestions` marker now includes
the baseline computation and the transition from surface depth to compute reads.

Comparing the saved PNGs against the exhaustive reference gives these display-RGB
RMSE values on a 0..1 scale (not linear-radiance errors):

| Lights | Previous sampler | Baseline + residual | Previous outer region | Residual outer region |
| --- | ---: | ---: | ---: | ---: |
| 180 | 0.09179 | 0.01231 | 0.08681 | 0.00347 |
| 288 | 0.09738 | 0.01282 | 0.09556 | 0.00376 |

The outer region is the union of the outermost 20% on each image side. Error is
about 7.5x lower overall and 25x lower in that region. Old and new exhaustive
reference PNGs are identical. `artifacts/mandellight-v5/residual/quality.json`
and `compare-quality.ps1` record the comparison and its calculation.

Before the baseline experiment, the 2026-10-29 Intel Arc 2560x1440 capture measured
these medians, using
41 frames after LUT warmup with the same stationary scene and STB fixture:

| Lights | Grid preparation | Sampled airlight | Exhaustive airlight | Sampled total | Exhaustive total |
| --- | ---: | ---: | ---: | ---: | ---: |
| 180 | 0.115 ms | 2.614 ms | 38.002 ms | 16.835 ms | 52.555 ms |
| 288 | 0.174 ms | 3.739 ms | 59.069 ms | 24.853 ms | 81.215 ms |

Including grid preparation, sampled airlight is about 14x and 15x faster than
the exhaustive reference in these captures. Surface lighting now dominates:
11.790 ms at 180 lights and 18.592 ms at 288 lights. These timings do not imply
equal image quality: M=2 produces conspicuous colored patches in the paired
captures. Shader snapshots, PNGs, and exact timings are in
`artifacts/mandellight-v5/grid/1440p`.

Before the suggestion grid, the 2026-10-29 Intel Arc 720p capture measured these airlight-pass medians
(41 frames after warmup, same camera, medium, and synthetic STB fixture):

| Lights | Eight source samples | Exhaustive sum |
| --- | ---: | ---: |
| 180 | 7.92 ms | 9.28 ms |
| 288 | 11.20 ms | 14.79 ms |

Those reduced airlight time by about 15% and 24%, respectively, with visible
sampling grain. The previous per-pixel weight scans still scaled with light
count. Exact results and paired images are in `artifacts/mandellight-v5/sampling`.

Before source sampling, the 2026-10-28 full-resolution 720p Intel Arc capture measured
these GPU medians over 41 frames after LUT warmup:

| Pass | v5, 3x60 points | v4, 2x90 points |
| --- | ---: | ---: |
| Light generation | 0.010 ms | CPU-generated |
| Geometry preparation | 0.002 ms | 0.004 ms |
| Surfaces | 3.172 ms | 6.040 ms |
| Airlight | 9.491 ms | 14.859 ms |
| Composition | 1.451 ms | 0.503 ms |
| Total including output | 14.508 ms | 21.701 ms |

These are different ring layouts and input mixtures, so the measurements are
an illustrative cost comparison. Captures and exact settings are in
`artifacts/mandellight-v5/timings.json`; the combined capture is `stb.png`.
