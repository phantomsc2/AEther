# MandelLightv4: point, cone and line GPU costs at 720p

Historical measurements: the infinite vertical line has since been replaced by
an analytic filament ring. The table below retains the original line results;
it does not measure the new ring. The current `--light-types` profiler captures
a `ring-unshadowed` case instead of `line-unshadowed`.

Measured on NVIDIA GeForce RTX 4070 Laptop GPU, using the radius-free
production renderer and RGBA32F HDR targets. One active source per row, 128 static
cube instances, fixed default camera, default air coefficients and 30-degree cone
half-angle. Shadowed integration uses 32 samples and hardware PCF; shadow views
have 512 useful texels per side. No full-resolution spatial denoising filter.

Both columns render a 1280x720 frame. Full airlight integrates every pixel;
half airlight uses 640x360 integration plus the existing depth-aware 720p
reconstruction and full-resolution fallback. Half airlight is the scene default.

## Median GPU frame time

| Source | Shadows | Full 720p airlight, ms | Default half airlight, ms |
| --- | --- | ---: | ---: |
| No lights: common frame baseline | Off | 0.197 | 0.180 |
| Point, 1 source | Off: LUT | 0.213 | 0.186 |
| Cone, 1 source | Off: clipped LUT | 0.204 | 0.191 |
| Infinite line, 1 source | Off: numerical quadrature | 0.346 | 0.228 |
| Point, 1 source | On: regenerate 6 cube faces | 2.230 | 0.929 |
| Cone, 1 source | On: regenerate 1 perspective view | 0.593 | 0.460 |
| Point, 1 source | On: cached maps | 2.189 | 0.886 |
| Cone, 1 source | On: cached map | 0.584 | 0.455 |
| Infinite line | On | Unsupported | Unsupported |

These are whole-frame GPU timeline measurements, not incremental per-light costs.
The no-light baseline retains the same geometry, depth and composition passes.
Small differences between cheap cases are sensitive to scheduling and clock
variation. Cone cost also benefits from its restricted illuminated region; the
comparison does not isolate shadow projection from cone support.

## Full-resolution pass breakdown

| Source | Shadow generation, ms | Surface, ms | Airlight, ms | Composite, ms | Total, ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| No lights | 0 | 0.048 | 0.052 | 0.039 | 0.197 |
| Point, unshadowed | 0 | 0.050 | 0.065 | 0.040 | 0.213 |
| Cone, unshadowed | 0 | 0.050 | 0.057 | 0.039 | 0.204 |
| Line, unshadowed | 0 | 0.047 | 0.198 | 0.043 | 0.346 |
| Point, refreshed shadows | 0.046 | 0.048 | 2.023 | 0.044 | 2.230 |
| Cone, refreshed shadows | 0.017 | 0.048 | 0.422 | 0.042 | 0.593 |

Total includes additional renderer work outside the named passes. Independently
computed medians need not add to the total median. With half airlight, the
composite also includes full-resolution integration fallback at unmatched depths.

## Protocol and reproduction

```powershell
dotnet run -c Release --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-v4/light-types/full-720p --light-types
dotnet run -c Release --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-v4/light-types/half-airlight-720p --light-types --half-airlight
```

- 39 measured GPU timestamp frames per case: three rounds of 13, reversing case
  order in the middle round, with at least eight warmup frames per case/round.
- The same independent compute primer runs immediately before every measured
  frame, outside timing, to reduce laptop GPU idle-clock variation.
- LUT generation/upload, shader compilation, allocation and readback are excluded.
- Refresh cases invalidate CPU shadow cache keys before each render. This redraws
  the same frozen geometry and maps, allowing direct comparison with cached cases.
- Both inputs are uniform live S/T=0.025. The unused tonal ring is disabled. Point
  and cone use the same single strobe-ring position and default gain; the line is
  the existing infinite Y axis through the origin at default intensity 0.5.
- The LUT is confirmed ready before measuring unshadowed point/cone cases. The
  harness checks shadow-refresh counts, black baseline and nonblack active cases.
- Both runs captured identical scene-source SHA-256 hashes. Raw per-frame timings,
  p10/p90 totals, source snapshots and PNGs are under their output directories.

Full-resolution p10/p90 totals are 0.211/0.217 ms for the unshadowed point,
0.202/0.207 ms for the unshadowed cone, 0.343/0.354 ms for the line,
2.220/2.232 ms for refreshed point shadows, and 0.583/0.606 ms for refreshed cone
shadows. Cached cone cases showed occasional scheduling outliers; use the medians
for the comparison, rather than treating them as frame-time guarantees.

The single-source results cannot be multiplied directly by 60 or 96: fixed passes
are shared, unshadowed lights are batched, and the shadowed sampler shares a fixed
sample budget across participating lights. Line shadows have not been implemented
and no estimate is substituted for a measurement.
