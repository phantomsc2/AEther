# MandelLight volume performance

MandelLight remains an audio-independent volumetric prototype. These measurements
cover the moment-transport implementation and the first trimming pass, not the
older surface approximation. No surface intersections or proxy lobes are restored.

## Measurements

Measured on Intel(R) Arc(TM) Graphics, Windows/D3D11. Values are milliseconds,
median of 21 GPU timestamped frames after 20 warmups. Compilation and pixel
readback are outside the timed interval. GPU clock changes and other GPU users
can affect these numbers; treat the comparisons as rough pass costs, not a
portable performance guarantee. Individual pass medians need not sum to the
median total. The total includes rendering and submission gaps, without vsync.

Unless specified: 640 by 360, one moving light, moving camera, power moments,
four fractal iterations, 512 transport steps, 12 samples per proposal, emission on.
The parameter sets are the profiler defaults, not a reconstruction of the user's
multilight screenshot.

| Case | Before | After | Reduction |
| --- | ---: | ---: | ---: |
| Default | 29.5 | 25.6 | 13% |
| 48 samples per proposal | 62.5 | 57.4 | 8% |
| 1024 transport steps | 29.2 | 27.8 | 5% |
| 16 fractal iterations | 39.7 | 36.4 | 8% |
| Four lights | 78.0 | 69.5 | 11% |
| 1280 by 720 | 81.1 | 73.5 | 9% |
| Camera inside the volume | 30.8 | 31.0 | -1% |

This is a modest speed improvement. The guide also consumes additional camera
cache bandwidth, so improvement is not uniform across camera positions.

### Most expensive work

At 1280 by 720 after trimming:

| Draw | GPU ms | Main work |
| --- | ---: | --- |
| Light integration | 44.9 | Local density, camera attenuation, four angular-cache taps, moment CDF reconstruction and RGB attenuation at every candidate |
| Camera transport | 19.1 | Up to 512 fractal evaluations per pixel, two moment measures, occupancy bins |
| Emission | 4.2 | Guided integration and camera attenuation |
| Light transport | 1.7 | Fractal march over a fixed 512 by 512 angular cache |

`Quality.Samples` multiplies camera integration work: 12 means up to 36 candidates
per light, 48 means up to 144. A contributing candidate can reconstruct two measures
for the camera plus two measures at each of four angular taps: up to ten moment
CDF evaluations. Each active light repeats this integration. Increasing screen
resolution increases both integration and camera-cache work; light-cache resolution
is fixed.

`Quality.TransportSteps` multiplies camera and light cache construction, rather
than the later shading sample count. `Fractal.MaximumIterations` increases density
evaluation cost in both cache construction and local shading. Camera motion also
changes the camera-aligned angular grids, invalidating the light caches. When all
motion stops, transport caches are reused; the measured frozen total fell from
16.0 to 14.0 ms.

The atmosphere LUT is expensive to construct (512 by 256 texels, 1024 integration
steps each), but only rebuilds on shader or air-scale-height changes. Warm-frame
measurements exclude its construction. Normal rendering does not allocate timestamp
queries; the profiling hook is null.

## Changes

* Cache support chord entry/exit distances. Stop recomputing spherical intersections
  and angular directions for each of the four light-cache taps at every candidate.
* Compute radial-air source/receiver depth once per candidate, shared by the four
  taps. The atmosphere LUT still supplies the air integral.
* Skip moment reconstruction for empty measures and zero material density.
* Add 16 density bins to camera transport, using the fractal evaluations already
  required for the moment cache. Guide emission and the bounded MIS proposal with
  occupancy times approximate camera attenuation. Retain at least 10% uniform
  coverage and evaluate the actual density and matching PDFs at each sample.
* Add conservative orbit rejection for more than four iterations and expanding
  fractal scales. `Transport.OrbitRejection` defaults true and permits comparison
  with the full orbit. This exits an individual density query only when a lower
  bound proves the final normalized potential is unoccupied; it does not advance
  or skip the camera ray. At 16 iterations, its toggle reduced camera transport
  from 20.4 to 12.8 ms and light transport from 7.2 to 4.4 ms in the same run.
  Four-iteration defaults do not use this shortcut.

The occupancy guide costs one extra RGBA32UInt image: camera caches grow from
48 to 64 bytes per pixel, adding about 14.1 MiB at 1280 by 720. Each light cache
remains 12 MiB. The 8-bit occupancy quantization affects only sample placement;
moment accumulation and radiance remain FP32.

### Orbit bound

For maximum absolute coordinate `r`, expansion `a = abs(scale) > 1`, and
`c = 2*a + max(abs(p))`, the box fold removes at most two per coordinate and the
sphere fold never shrinks a vector. Therefore:

```text
r_next >= a*r - c
R = c / (a-1)
r_final >= R + a^k * (r-R)       // k remaining iterations
```

If this lower bound exceeds the final unnormalized occupancy threshold, the
query contributes zero material. The implementation uses a numerical margin and
does not apply the bound near unit expansion. The GPU regression compares the
occupied density with rejection enabled/disabled across scales, iteration counts,
potential thresholds and edge widths.

## Noise and epipolar coordinates

The earlier uniform support proposal spends many samples in cavities and misses
small occupied intervals. Sharp spatial density combined with point-light falloff
and attenuation produces a much less uniform integrand than a surface proxy.
Some variance is expected at a small sample count, but the earlier amount of
grain was not unavoidable.

A frozen 320 by 180 default scene was compared with its own 256-sample-per-proposal
reference. RMSE below is over display RGB in [0,1], after tone mapping, without
dither. Both comparisons retain 512 transport steps and four moments, so this
measures integration sampling error, not error against exact volume transport.

| Contribution | Before RMSE | Guided RMSE | Reduction |
| --- | ---: | ---: | ---: |
| Light only | 0.03808 | 0.02668 | 30% |
| Emission only | 0.06586 | 0.02422 | 63% |
| Combined | 0.04202 | 0.02182 | 48% |

The angular map still uses inclination and azimuth about the light-to-camera
axis. Samples on one camera ray share an azimuth, so the epipolar geometry is
preserved. A GPU regression checks several sample distances, camera positions,
offsets and all four lights. This implementation does not include hierarchical
epipolar traversal or reuse an integrated lighting result between pixels.

Remaining effects have different causes:

* Fine grain: sparse quadrature of actual density and illumination; the guide
  reduces it. Jitter is spatial and deterministic. There is no temporal accumulation.
* Thin features missing from transport: insufficient transport march resolution;
  a sampling guide cannot repair attenuation that was never measured.
* Smooth leakage or misplaced attenuation: four-moment reconstruction error.
* Angular aliasing: fixed 512 by 512 light caches and interpolation; epipolar
  alignment does not remove that resolution limit.

A potential-gradient distance estimate could provide another sampling proposal,
but is not a conservative empty-space distance for this finite density field.
Using it to skip whole intervals can erase disconnected material folds. The
existing march's density is a direct guide without extra derivative work. A next
larger optimization would adapt transport construction itself or factor moment
reconstruction out of the candidate loop; neither is included in this rough pass.

## Reproduction

See [profiler instructions](../tools/AEther.MandelLightProfile/README.md).
Local ignored artifacts from this run are under
`artifacts/mandellight-profile/{before-steady,after-final,noise-before,noise-after}`.
Those directories contain JSON timings, RGBA captures and noise comparison PNGs.
The pre-trim shader snapshot is under `artifacts/mandellight-profile/baseline`.
