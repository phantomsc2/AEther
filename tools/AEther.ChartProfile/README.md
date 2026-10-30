# Timing chart probes

Build with dotnet build tools/AEther.ChartProfile -c Release -p:Platform=x64.
Run the executable under bin/x64/Release/net10.0-windows10.0.19041.0/win-x64
from the repository root.

The production charts use direct CPU rasterization into transparent WriteableBitmap
surfaces. Every raw sample becomes an opaque center with four quarter-opacity physical-pixel neighbors at its original time
and logarithmic duration. There is no binning, averaging, animation, smoothing,
per-point UI element, or LiveCharts dependency. The grid and current-time marker
are translucent; labels and legends use XAML.

Default: 1280x720 isolated charts, seven-second history, 10 Hz maximum refresh, 60 Hz
synthetic frame metrics and 1000 Hz analysis/handoff with periodic spikes.
A three-second warmup precedes ten seconds of measurement. The probe checks raw
sample counts, lost samples, updates, axis ranges, reset, and layout. It saves
pipeline CSV, summaries, and a rendered XAML capture.

Options:
- --layout uses the production hub's chart/control layout inside the 720p window.
- --fast requests the maximum supported 10 Hz chart refresh.
- --tag=NAME writes isolated results under artifacts/chart-profile/NAME.
- --hub --scene=MandelFluid --tag=NAME runs the actual hub, analyzer and renderer
  at 1280x720 through scene, charts, spectrum, full hub, and scene-repeat phases.
  Each phase samples eight seconds after warmup.
- --chart-hz=N overrides refresh rate in hub mode.

Metrics:
- chart.processing_update_ms and chart.scheduling_update_ms measure synchronous
  input copying, history maintenance, rasterization, bitmap-buffer write and
  Invalidate submission. They exclude later XAML layout, composition, and scanout.
- *_prepare_ms and *_raster_upload_ms split that work into two stages.
- chart.frame_time_update_ms combines the two per-chart sample streams.
- Completed chart updates count bitmap submissions, not display presentations.
- Whole-process CPU includes UI work outside the timed chart functions.

Always compare rendering performance in Release at 720p or higher. Earlier
LiveCharts bin-sweep results are historical and timed asynchronous chart updates;
their wall-time boundaries differ from the direct raster renderer.

See ../../doc/raster-chart-performance.md for measurements.

Incremental caching draws only arriving samples and removes expired samples using per-pixel per-trace reference counts. Full history replay occurs on resize, DPI change, axis expansion or profile replacement. The source histories remain available for those rebuilds. Refresh requests are capped at 10 Hz; hidden views suspend updates. Fully empty idle charts skip bitmap submission.
