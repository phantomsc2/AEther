# MandelLight GPU profile

For the production combined strobe/tonal path at 1280x720:

```powershell
dotnet run -c Release --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-v4/tonal-ring --tonal-ring
```

This captures transient-only, tonal-only (1/60/96 lights), and combined 60+60-light
cases with live stereo S/T fixtures. This comparison explicitly enables strobe
shadows and disables tonal shadows; the scene preset defaults both shadows off.
It waits for the matching unshadowed LUT,
asserts that tonal-only frames issue no shadow-map draws, and records 41-frame
GPU medians with rotating cubes. Tables are cached and ready for the measured
frames. Shader compilation, background generation and readback are excluded.

For the isolated unshadowed airlight LUT study, run from the repository root:

```powershell
dotnet run -c Release --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-v4/lut-study --airlight-lut
```

This mode always renders at 1280x720. It compares the existing sampled integral,
deterministic equiangular quadrature, and two-dimensional cumulative-integral
textures, both without visibility and with cubemap PCF visibility. It substitutes
experimental shaders at profiling pass markers; production shaders and transport
are unchanged. Use `--airlight-lut --cpu-only` to run just the numerical validation.

The output includes `cpu-validation.json`, `gpu-results.json`, and PNG captures.
CPU validation uses independent adaptive quadrature, including infinite rays,
short segments, collinear rays, lights behind the camera, and parameter changes.
GPU references use 1024 samples. Timings include separate airlight and composite
passes, with full-resolution fallback included in the latter for half-resolution
airlight. Each measured frame follows an untimed substantial GPU workload to keep
laptop idle clock changes from distorting comparisons. LUT construction and
compilation are excluded from per-frame GPU timings and reported separately.
See [the LUT study](../../doc/mandellight-lut-study.md) for results and limitations.

For opaque raster cubes and homogeneous atmospheric scattering, add `--v4`:

```powershell
dotnet run --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-v4/current --v4
```

This records the spectral ring's scalar shadow cubemaps, camera surface pass and airlight
pass separately. V4 now receives a deterministic synthetic live stereo-T spectrum
before each frame; there is no fallback illumination in the production shader.
Cases cover 1..4096 cubes, one/20 lights, static caches, sampling
and shadow resolution, pure angular/pure optical/MIS sampling, diagnostics,
zero air, no objects, lights off and 1280x720. Lights-off output is checked for
zero RGB after active lighting. PNGs and timings.json use the v3 timing protocol
below. --quick captures only default; --small uses 320x180. See the
[v4 model and controls](../../doc/mandellight-v4.md).

For a frozen filter comparison use `--v4 --filter`. It captures filtered and
unfiltered 16-sample images plus a copied shader at 512 samples as a reference.
It writes `filter.json` with per-pass GPU timings, adapter name and display-RGB
RMSE against the reference. The copied shader's sample bound is modified in the
artifact directory only. All scene, geometry and light motion is frozen for the
comparison. `--small` uses 320x180 instead of 640x360.

`--v4 --shadows` compares point sampling and hardware PCF at 256 and 512 useful
texels per cube face, with atmosphere and surface-only variants. Camera and time
sequences match across cases; cubes move so map rebuild costs are included.
It saves PNGs plus `shadows.json` with the actual adapter and per-pass medians.

`--v4 --modes` explicitly captures full resolution with all lights,
one-light-per-sample, half-resolution airlight and combined modes at the same
sample budget and time sequence, regardless of the preset's toggle defaults. It saves four
PNGs and `timings.json`, including separate integration and reconstruction costs.
The half-resolution composite timings include full-resolution fallback integrals
where the depth-aware reconstruction has no compatible coarse sample.
The `--filter` comparison explicitly disables both speed modes to isolate the
full-resolution 3x3 filter. Ordinary `--quick` captures use the current preset,
including its enabled speed modes.

For the Fourier/deep-cubemap prototype, add `--v3`:

```powershell
dotnet run --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-v3/current --v3
```

This captures the default four-light scene, source counts through 512, moving
sources, cached maps with a moving camera, filtering, sampling and cube-resolution
variants, inside views, density and transport diagnostics, emission and air-only
views. Each case writes a PNG. GPU timestamps report individual light cubemaps,
camera and total, as medians of 11 frames after eight warmups. CPU aggregation is
the last measured call, retained across cache hits, rather than a per-frame median.
`--quick` captures only default. `--small` uses 320 by 180. `--reference` renders
only default at 320 by 180 with direct analytical light attenuation, bypassing the
maps but retaining the same sampled camera scattering integral. See the
[v3 model and controls](../../doc/mandellight-v3.md).

For the analytical ellipsoid prototype, add `--v2`:

```powershell
dotnet run --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-v2/current --v2
```

This selects and copies MandelLightv2, timestamps its complete single-pass render,
and captures default, count, quality, lighting and inside-camera variants. `--quick`
runs only its default. `--reference` renders a copied v2 shader at 256 shared
samples and runs only the default. V2 does not use the old per-pass markers or
the MandelLight-specific `--noise` comparison. See the
[v2 model and controls](../../doc/mandellight-v2.md).

Windows and a D3D11 GPU are required. Run from the repository root:

For a reproducible 720p spotlight-sampler comparison, use
`--spotlight-sampling` instead of `--tonal-ring`. It captures only the 60-strobe
case, freezes the cubes and camera, and times 41 frames with cached shadow maps.
Use separate source roots/output directories to compare sampler revisions.
The checked report in `doc/mandellight-v4.md` also compares the 32-sample captures
against a copied shader with a 1001-sample output; that reference is untimed for
the reported production comparison.

```powershell
dotnet run --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-profile/current
dotnet run --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-profile/noise --noise
```

The first argument is a shader root containing `Common` and `Scenes/MandelLight.hlsl`.
The second is an output directory. The tool copies only MandelLight and its common
includes into that output, so other scenes do not affect compilation. Use distinct
output directories for comparisons. Source shaders are not modified.

The timing run measures a default moving camera/light, frozen caches, high samples,
high transport steps, high iterations, four lights, trigonometric moments, an inside
camera, and 1280 by 720. When the shader exposes `Transport.OrbitRejection`, it also
measures 16 iterations with rejection disabled. `--quick` runs only the first two
cases. GPU timestamps surround draws and the whole render, excluding shader
compilation and pixel readback. Queries are allocated before each timed interval.
Each result is the median of 21 frames after 20 warmups; output is `timings.json`
and image captures. Individual medians need not sum to the median total. Avoid
other GPU workloads while collecting comparisons; clock and scheduling variation
can still affect the measurements.

`--noise` freezes the default view and light at 320 by 180. It captures light,
emission and combined output with 12 samples per proposal, then compiles a copied
shader with 256 samples per proposal as a reference. It writes PNG pairs and
`noise.json` with display-RGB RMSE and means. The reference retains the same moment
and transport-step approximations, so it measures integration sampling error,
not total error against an exact volume solution.

The regular renderer has only a nullable internal pass callback; all timestamp
queries and readback belong to this tool. See the
[measurement report](../../doc/mandellight-performance.md) for the first trimming pass.
