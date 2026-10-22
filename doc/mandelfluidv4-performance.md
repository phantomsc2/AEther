# MandelFluidv4 performance and latency

## Current Arc target and smoothness (2026-10-22)

Arc is the global device for the application, GPU tests and profiling tools. Scene changes
and monitor moves do not select another adapter or recreate audio/graphics resources.
Only machines without Arc use the normal hardware fallback; Arc initialization errors
are visible rather than hidden by switching to the 4070.

V4 now transports pigment/age on every displayed frame while velocity advection and the
multigrid pressure solve remain at 60 Hz. At 165 Hz the intermediate frames perform two
material draws using the last projected velocity field; lighting still samples live T
on every render. Inlet refresh is scaled by elapsed time to preserve the 60 Hz response.
This adds no audio buffering and retains the 512x512 grid, projection accuracy and donor
clamping. Large stalls retain bounded catchup and natural signal decay.

The display timeline also rejects isolated repeated/two-frame compositor predictions.
Histogram uses fractional pixel coverage over discrete history intervals so its motion
does not snap to whole pixels. Missing/unwritten intervals remain black.

Repeat the real WinUI presentation run on the built-in panel (verify the monitor name
on the current machine; Windows primary may be the external monitor):

```powershell
dotnet run --project tools/AEther.Smoke -c Release -- --presentation-profile '--monitor=\\.\DISPLAY1' --scene=MandelFluidv4 --fast --width=3200 --height=2000 --fullscreen --seconds=15 --output=artifacts/fluid-smooth/arc-default-native
```

`--fast` uses EmissionVelocity 1, VelocityGain .75, Curl .75. The profiler runs two seconds
of warmup, then records CSV/summary timings. It uses synthetic 1000 Hz timestamped input
six milliseconds behind host time, the actual SwapChainPanel/compositor path, and FP16
output. These measurements establish submission cadence, not speaker-to-photon latency.
There is no adapter override. Monitor selection changes the destination display only.

With the global policy, a 15-second Release run on Intel Arc, the built-in
3200x2000 165 Hz HDR panel, full-screen FP16 output and the fast preset recorded
2,469 render intervals: mean 6.0606 ms, p95 6.1941 ms, maximum 7.7509 ms, and zero
intervals above 9 ms. The final timing window reported GPU frame mean/p95
5.879/7.358 ms, material/velocity/pressure combined 1.484/3.925 ms, and Present
wait 0.028/0.070 ms. GPU timings exclude Present and may overlap CPU submission;
these numbers do not prove every submitted image reached scanout.
The trace is `artifacts/fluid-smooth/arc-global-native/pipeline-20261022-025936-064.csv`.

The offscreen solver tool additionally accepts `--hz=165`, `--width=3200`, `--height=2000`
and `--fast`; it records GPU cost independently of live presentation.

## Historical 4070 solver optimization (2026-10-21)

Measured 2026-10-21 in Release, 1280x720 output on an NVIDIA GeForce RTX 4070 Laptop GPU.
The simulation retains its 512x512 grid (128x128 only when LowQuality is explicitly selected).

## Changes

- Keep three pressure V-cycles and the existing fine-grid smoothing. Reduce only the 4x4
  coarsest relaxation from 64 to 16 iterations. Default work falls from 387 to 243 draws per
  step. A two-cycle trial was rejected: residual divergence rose from 0.77% to 3.49%.
- Reduce initial velocity-only warmup from eight steps to two. The first material update
  follows immediately when the fixed-step clock is due. Cold startup still includes resource
  creation and driver work; it is not representative of steady-state rendering.
- Tolerate the elapsed clock's 100 ns rounding in the 60 Hz accumulator. Regular 60 Hz input
  now gives one simulation step every frame instead of occasional zero-step/two-step pairs.
  Genuine stalls still use the existing bounded catchup and material-age handling.
- Copy reflected scene parameters into each pass only when the source revision changes.
  Edits, reset, restored settings and shader replacement invalidate the cache. This removes
  repeated cloning and reflected field matching from every pressure draw.
- Preserve corrected velocity advection, donor clamping, FP32 material/age state, spectral
  locality, boundary modes, and the absence of explicit dye diffusion.

## Measurements

Each case uses deterministic stereo spectral rows at 1000 Hz, a logical 60 Hz display clock,
24 warmup frames, and 48 measured frames. GPU timestamp/disjoint queries enclose actual work;
the offline tool waits to resolve each frame. CPU submission excludes readback/query resolution.
Results are median / p95, milliseconds unless indicated.

| Default 512x512 case | Baseline | Final |
| --- | ---: | ---: |
| GPU frame | 1.369 / 3.350 | 1.134 / 1.146 |
| GPU fluid | 1.299 / 3.280 | 1.064 / 1.071 |
| GPU pressure | 1.154 / 2.996 | 0.920 / 0.925 |
| CPU submission | 1.318 / 4.898 | 0.621 / 0.809 |
| CPU allocations per active frame, median | 681,864 bytes | 1,352 bytes |

Steady-state GPU frame median improves about 17%; p95 improves about 66%. The p95 change
includes eliminating artificial step batching. These short offscreen runs do not establish
displayed FPS, speaker-to-photon latency, or performance on another GPU. Baseline and final
include the profiling markers; the live asynchronous timestamp path is tested separately.

The independent pressure fixture excludes prescribed inlet forcing. With periodic boundaries,
residual RMS divergence is 0.772633% before and 0.772746% after; with walls it is 0.621531%
before and 0.621564% after. Relative curl error remains below 0.00012%. The inlet intentionally
adds a prescribed velocity after projection, so measuring divergence including inlet forcing
would not isolate pressure-solver accuracy.

## Reproduce and inspect

```powershell
dotnet run --project tools/AEther.MandelLightProfile --configuration Release -- --fluid-v4 --output=artifacts/fluid-v4/current
dotnet run --project tools/AEther.MandelLightProfile --configuration Release -- --fluid-v4 --filaments --case=default --frames=240 --output=artifacts/fluid-v4/filaments
```

The tool writes per-frame GPU timings, CPU timings, allocations, step/draw counts, projection
metrics, and summary quantiles to `profile.json`, plus final PNG and raw RGBA images. It profiles
default, viscous, wall-boundary and low-quality cases; `--case` selects one. `--filaments` raises
velocity gain and curl to exercise thin moving structures without lowering the grid resolution.

GPU regressions cover regular 30/60/120 Hz scheduling, live parameter edits/reset, 99% divergence
removal and curl preservation, one-cell material filaments without added diffusion, and finite
bounded material under maximum emission/curl/viscosity with million-amplitude input. Existing
production-scene tests continue to require exact black after a generous silence decay period.
