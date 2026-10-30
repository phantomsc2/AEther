# Transparent full-resolution timing scatter

The production timing charts now rasterize every retained sample into a reusable
WriteableBitmap. There is no LiveCharts dependency, binning, averaging, curve fitting,
or per-sample UI object. Each sample is an opaque one-physical-pixel dot. Samples
that map to the same pixel overlap naturally; traces drawn later can cover earlier
traces. The background is transparent, with premultiplied translucent grid/cursor
pixels. Legends and axis labels remain XAML. The seven-second history, original
sample timestamps, logarithmic Y axis, aligned timelines and 5 Hz refresh remain.
Scheduling is retained because its measured cost is low.

CPU rasterization was chosen because the visible plots are small, opaque dots are
simple integer writes, and the measured cost met the target without GPU resource
management or another graphics dependency. All retained raw points are visited;
no temporal decimation is performed. New bitmap/storage allocations happen on
resize; the pixel array is reused during steady state.

## Measurements

Release on 2026-10-30. All windows at 1280x720 or greater. Isolated tests used the
production 620x320-DIP chart panel inside the 1280x720 hub layout, at raster scale 1.
Synthetic input was 60 Hz frame metrics plus 1000 Hz analysis/handoff with isolated
spikes. Three-second warmup, ten-second measurement, 47 updates per chart.

| Workload | Processing median / p95 | Scheduling median / p95 |
| --- | ---: | ---: |
| Isolated layout, 16,100 raw points | 0.359 / 0.501 ms | 0.294 / 0.469 ms |
| Actual analyzer + MandelFluid scene, charts enabled | 0.340 / 0.538 ms | 0.253 / 0.501 ms |
| Actual analyzer + MandelFluid scene + spectrum preview | 0.323 / 0.724 ms | 0.254 / 0.477 ms |

Actual-hub measurements used Intel Arc Graphics, timestamped synthetic stereo,
and 1280x720 scene/client windows, eight seconds per phase after warmup, 39 updates
per chart. Processing maximum in the full-hub phase was 0.764 ms. These are short
local measurements, not a guaranteed hard latency bound: Scheduling had an isolated
14.566 ms outlier in the charts-enabled phase and 9.984 ms in the isolated probe.

Timing boundaries: synchronous history copying/maintenance, range calculation,
rasterization, pixel-buffer write, and Invalidate submission. Deferred XAML layout,
composition, GPU presentation and scanout are excluded. This differs from the old
LiveCharts Measuring-to-UpdateFinished asynchronous wall-time metric; do not claim
a direct latency speedup ratio from those two metrics. Whole-process CPU in the
isolated layout fell from the historical full-resolution sweep's 0.271 cores to
0.027 cores under the same synthetic workload. Short runs and clock drift limit
precision; production phase CPU figures are not suitable for exact attribution.

Raw results:
- artifacts/chart-profile/raster-transparent (includes layout.png)
- artifacts/hub-profile/raster-production (per-phase CSV and summaries)

## Validation

The 720p UI probe passed checks for all native sample counts, loss, aligned charts,
axis ranges including isolated spikes, layout and reset. The XAML capture was
visually inspected. Three unit tests cover one-sample spikes and native sample
positions, wrap without cross-seam connecting lines, and invalid/out-of-range data.
The Release chart probe build succeeds; existing experimental XAML control warnings
remain. Resize and rasterization scale recreate the bitmap at physical pixel size.
## Incremental cache follow-up

The default and maximum refresh is now 10 Hz. New samples increment per-trace
pixel counts; expired samples decrement them, preserving overlapping dots and
trace precedence. Only affected pixels change in the raster cache. The cache is
copied for the time cursor and bitmap upload; existing points are not remapped.
Ranges only inspect incoming values. Axis expansion, resize/DPI or profile change
rebuild the cache from retained history. Empty idle charts skip bitmap submission;
hidden views defer history consumption. Sample callbacks do not touch UI objects.

At 1280x720 with the analyzer, MandelFluid and spectrum active, eight-second phase:
Processing median/p95 0.145/0.231 ms; Scheduling 0.091/0.122 ms. 74 updates per chart
in 8.067 seconds, 16,376 raw points retained. Compared with the earlier 10 Hz full
redraw run (0.200/0.421 and 0.153/0.239 ms), this is a modest further reduction.
Different short runs and clock variation limit exact attribution. Timing includes
cache maintenance, bitmap copy/upload and submission, but excludes composition.
Raw results: artifacts/hub-profile/incremental-10hz.

Five scatter tests pass, including incremental-vs-full-redraw equality after
expiration across wraps and preserving overlapped samples/trace layers.
## Soft five-point kernel

Each point now has an opaque center and four immediate physical-pixel neighbors
at alpha 64/255 (approximately 25%). Halos do not accumulate brightness. Centers
win over halos across traces; equal-strength overlaps use existing trace order.
Separate center/halo occupancy counts preserve correct expiration and reveal the
underlying halo or grid. Halos are premultiplied and composited over the cached
grid. Edges clip without wrapping the kernel across the timeline seam.

In the 1280x720 full hub with analyzer, MandelFluid and spectrum active:
Processing median/p95 0.190/0.293 ms; Scheduling 0.121/0.168 ms. The earlier
single-pixel incremental baseline was 0.145/0.231 and 0.091/0.122 ms respectively.
These are short separate runs with the same timing boundaries; the extra cost
is small and p95 remains comfortably below 1 ms. All 16,379 raw samples were retained.
The 10 Hz cap, incremental updates, transparency and history rebuilds remain.

Eight scatter tests passed, covering exact footprint/premultiplication, clipped
corners, center-over-halo priority, non-accumulating overlapping halos, grid
restoration, and incremental/full-redraw equivalence after expiration. The 720p UI
probe passed its retention/layout/reset checks, and its capture was visually
inspected. Raw results: artifacts/hub-profile/soft-cross and
artifacts/chart-profile/soft-cross (layout.png).