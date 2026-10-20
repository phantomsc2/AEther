# MandelLight GPU profile

For opaque raster cubes and homogeneous atmospheric scattering, add `--v4`:

```powershell
dotnet run --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-v4/current --v4
```

This records the four scalar shadow cubemaps, camera surface pass and airlight
pass separately. Cases cover 1..4096 cubes, one/four lights, static caches, sampling
and shadow resolution, pure angular/pure optical/MIS sampling, diagnostics,
zero air, no objects, lights off and 1280x720. Lights-off output is checked for
zero RGB after active lighting. PNGs and timings.json use the v3 timing protocol
below. --quick captures only default; --small uses 320x180. See the
[v4 model and controls](../../doc/mandellight-v4.md).

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
