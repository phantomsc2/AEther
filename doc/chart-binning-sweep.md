# Timing-chart bin sweep

Release, 1280x720 hub layout, both charts together without scene/audio pipeline.
Synthetic 60 Hz frame metrics and 1000 Hz analysis/handoff, seven-second history,
5 Hz refresh. Three-second warmup and ten-second sample per process (47 updates
per chart). Values are wall time including scheduling; chart timings overlap.

| X bins | Time/bin ms | Points | Processing median/p95 ms | Scheduling median/p95 ms | CPU cores |
| --- | ---: | ---: | ---: | ---: | ---: |
| Full | native | 16086 | 31.773 / 51.242 | 35.259 / 43.911 | 0.271 |
| 256 | 27.3 | 3124 | 8.488 / 13.800 | 9.302 / 13.509 | 0.136 |
| 128 | 54.7 | 1792 | 7.047 / 8.654 | 7.161 / 16.906 | 0.116 |
| 64 | 109.4 | 896 | 6.543 / 11.382 | 6.432 / 8.287 | 0.098 |
| 32 | 218.8 | 448 | 6.439 / 7.128 | 6.388 / 7.132 | 0.094 |
| 16 | 437.5 | 224 | 6.151 / 6.769 | 6.050 / 6.803 | 0.105 |
| 8 | 875 | 112 | 6.207 / 6.658 | 6.045 / 6.801 | 0.086 |
| 4 | 1750 | 56 | 6.049 / 6.420 | 5.589 / 6.407 | 0.100 |

The curve flattens near 64 bins. Removing 94% more plotted points (64 to 4 bins)
only saves 0.5-0.8 ms. Remaining cost is consistent with chart update/draw/scheduling
overhead; this does not isolate which part dominates. CPU is whole-process core
equivalents without baseline subtraction. Single short runs establish the broad
curve, not precise differences between adjacent settings. This workload differs
from earlier individual-chart production-scene runs.

All runs retained raw history and passed update, spike-inclusive axis-range,
layout, and reset checks. Extrema retain original timestamps and values, without
averaging. Four-bin captures show severe loss of temporal detail.

Raw profiles and captures: artifacts/chart-profile/bin-sweep-N/, where N is
0, 256, 128, 64, 32, 16, 8, or 4. Zero renders all raw samples.
Build tools/AEther.ChartProfile in Release with Platform=x64; run the executable
with --layout --bins=64 --tag=bin-sweep-64 (substitute the desired count).

At the time of this sweep production used 256 bins. The direct-raster renderer now supersedes this approach; see raster-chart-performance.md. Substantially faster rendering calls for bypassing
per-point chart entities, such as batching raw points onto a raster surface.
That alternative has not been implemented or measured by this sweep.