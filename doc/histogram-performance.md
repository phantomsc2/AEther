# Histogram at native resolution

Measured with `DisplayTimingTests.MeasureHistogramGpuCostByResolutionAndAdapter`:
16 warmup frames, then 96 measured frames, 97 audio bands, 160 analysis rows/second,
two seconds of history and FP16 scRGB output. GPU timestamps include the scene and
output conversion. Small histogram readbacks outside the timings bound queued work.

| Resolution | GPU | GPU frame mean | GPU frame p95 |
| --- | --- | ---: | ---: |
| 1280x720 | RTX 4070 Laptop | 0.063 ms | 0.065 ms |
| 1280x720 | Intel Arc | 0.199 ms | 0.277 ms |
| 3200x2000 | RTX 4070 Laptop | 0.353 ms | 0.356 ms |
| 3200x2000 | Intel Arc | 1.038 ms | 1.240 ms |

These isolate rendering. They do not measure live compositor scheduling, transfers
between GPUs, or physical scanout, and cannot establish the cause of an observed hitch.
Raw measurements and test results are under `artifacts/fullscreen-histogram/`.

The display-owner selection resolved to Intel Arc on this machine. Histogram previously
selected the high-performance adapter, requiring its animated FP16 surface to reach the
display through another adapter. A 3200x2000 FP16 RGBA surface is 51.2 MB; at 165 updates
per second its uncompressed payload alone is 8.45 GB/s. This is a transfer-size estimate,
not a measured transfer bandwidth.

The WinUI session now chooses the display GPU for both Histogram and Spectrum. This
avoids that adapter crossing while keeping native-resolution rendering, discrete audio
events, and the original audio clock. Heavy scenes continue selecting the performance GPU.
Adapter-change tests preserve the existing audio history across graphics recreation.
Fullscreen presentation smoothness must still be evaluated in the running application.
