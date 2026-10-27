# Frame-time chart probe

Build with `dotnet build tools/AEther.ChartProfile -c Release -p:Platform=x64`.
Run `tools/AEther.ChartProfile/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/AEther.ChartProfile.exe` from the repository root.

The probe opens a 1280×720 client area, preloads 7 seconds of timestamped synthetic
frame intervals, GPU durations and total audio ages at 240 samples/second, and continues adding samples while the chart
scrolls. It measures for ten seconds after a three-second warmup. Results (CSV,
summary text and a PNG of the XAML tree) go into `artifacts/chart-profile`.

- Default: plot 7 seconds (1,680 points per series), refresh at the production rate of 1 Hz.
- `--fast`: request 60 Hz chart refresh to stress LiveCharts independently of production settings.
- `--layout`: exercise the full audio/video and views/controls layout instead of the isolated chart.

All modes retain individual samples with no averaging, markers or animations. The chart
uses LiveCharts' default curve smoothing and antialiasing.
The source ring is bounded at 32,768 samples. The probe checks for overwritten samples,
missing chart updates and incorrect quadrant placement. Synthetic GPU results arrive
out of order to exercise sorting by the original frame timestamp. `--layout` leaves audio analysis
unattached; it measures the chart and XAML layout, not the GPU spectrum or an active shader.

`chart.frame_time_update_ms` measures wall time from LiveCharts' `Measuring` event to
`UpdateFinished`, including drawing and scheduling; it is not GPU execution time.
The summary also records completed chart updates/second, process CPU core equivalents
and private memory. Performance results should be compared in Release at 720p or higher.

## Actual main-window comparison

Run with `--hub --scene=MandelFluidv4 --tag=my-run` to exercise the production
`MainWindow`, timestamped synthetic stereo capture, analyzer and scene renderer.
It keeps the scene at 1280×720 and the hub client at 1280×720, then samples these
configurations for eight seconds each after warmup:

1. Scene with hub hidden.
2. Scene with chart and controls.
3. Scene with spectrum and controls.
4. Scene with the complete hub.
5. Scene with hub hidden again, to expose workload/clock drift.

Results go into `artifacts/hub-profile/<tag>`. Each phase has a pipeline CSV and
summary; `process.csv` includes process/UI-thread CPU, allocations and chart draws.
The spectrum uses its normal preview resolution, recorded in each summary; the
main scene remains 720p throughout. CSV profiling enables detailed GPU queries for
measurement. In normal operation, the hub requests only the two GPU timestamps
needed for frame time.

See [main-window performance notes](../../doc/main-window-performance.md) for the
measured changes and the decision to retain full-resolution chart samples.

## Initial results (before reducing production refresh to 1 Hz)

Release, LiveCharts2 2.0.4, default CPU renderer, 1280×720 chart/client pixels,
10 Hz requested refresh, 240 synthetic intervals/second, with curve smoothing and
antialiasing disabled (2026-10-27):

| Visible history | Points | Completed updates/s | Update median / p95 | CPU core equivalents |
| --- | ---: | ---: | ---: | ---: |
| 30 s | 7,201 | 9.08 | 12.5 / 19.7 ms | 0.215 |
| 120 s | 28,801 | 9.10 | 55.8 / 69.4 ms | 0.603 |

Both runs retained all samples. These are isolated chart measurements, not scene or
spectrum performance measurements. The former 120-second window exposed the cost of
feeding tens of thousands of raw points to LiveCharts; its CPU cost was substantial.
