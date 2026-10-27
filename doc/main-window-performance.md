# Main-window performance tightening

Measured on 2026-10-27 in Release on Intel Arc Graphics, using the actual WinUI
hub and a 1280×720 MandelFluidv4 scene. The hub client was also 1280×720. Each
phase had warmup followed by eight seconds of timestamped synthetic stereo input.
The capture/analyzer continued running in all phases. Results are under
`artifacts/hub-profile/{baseline,tightened,envelope}`; the probe is documented in
`tools/AEther.ChartProfile/README.md`.

## Retained changes

- The spectrum preview renders at most 640 pixels wide and one pixel per analysis
  band vertically, capped at 256. At the measured layout this was 620×97 instead
  of about 620×240. The compositor stretches it to the panel size. Temporal row
  coverage/integration in Debug.hlsl is unchanged; short history events are not
  discarded. The spectrum continues at the scene/render-worker cadence.
- The preview uses a direct 8-bit SDR surface. This removes its FP16 intermediate
  image, second fullscreen color-conversion draw and monitor-white-level queries.
  The main scene retains the original FP16/scRGB path and resolution.
- LiveCharts receives only visible logarithmic ticks, instead of tick visuals for
  sixteen decades. Default font antialiasing and curve smoothing are retained.
- When diagnostics are collapsed, GPU profiling uses only the frame's start/end
  queries. Opening diagnostics or recording a CSV enables the detailed stage queries.
- At the time of these measurements, the chart retained every raw sample in its ten-second window and refreshed at 1 Hz. It now uses a seven-second window and refreshes at 10 Hz.

## Measurements

Full hub, same scene/client resolutions, with detailed GPU queries enabled for both
runs:

| Metric | Before | After preview/tick changes |
| --- | ---: | ---: |
| Spectrum GPU median | 0.178 ms/frame | 0.175 ms/frame |
| Spectrum GPU p95 | 0.463 ms/frame | 0.223 ms/frame |
| Frame interval median | 16.680 ms | 16.679 ms |
| Frame interval p95 | 16.920 ms | 16.950 ms |
| Chart update median | 7.604 ms/update | 9.602 ms/update |

Typical GPU savings were small on this adapter; the new preview chiefly bounds
pixel/bandwidth/memory costs as the hub grows or moves to a high-DPI screen.
The measured spectrum was about 3.4% of the full GPU frame time (0.175 / 5.155 ms).
The plot did about 8–10 ms of wall-clock work once per second. Process/UI CPU varied
between runs, so the measurements do not support claiming a total application CPU
speedup. The nearly identical frame-interval p95 also should not be presented as an
FPS gain. Results include scheduling/composition effects and a stateful fluid scene.

The final, unbucketed implementation was also checked with the default Mandelbox
scene at 1280×720 (`artifacts/hub-profile/final-mandelbox`). Full-hub GPU frame
median was 5.807 ms, with 0.234 ms in the spectrum output (about 4%). Chart update
median was 7.369 ms once per second. Frame interval median/p95 was 16.682/16.987 ms.
The scene-only repeat measured 16.680/16.897 ms. These runs demonstrate low preview
cost for both tested scenes, rather than a guaranteed budget for every shader or GPU.

## Rejected chart bucketing

A min/max envelope reduced approximately 1,800 plotted points to about 773 while
retaining peaks and dips. In the full hub its chart update median changed from
7.604 to 6.884 ms: only about 0.7 ms saved once per second. This was not enough to
justify sacrificing time detail, so the envelope and its implementation were
removed. The final chart draws every retained sample.

## Validation

The embedded-debug GPU regression covers both the original and direct-SDR paths,
with and without scene drawing: shared audio, matching colors within one 8-bit
step for SDR, unchanged main-scene pixels, resize, reload, and exact black after
the existing generous silence decay period. A GPU test also verifies that reduced
profiling still supplies frame time and CSV recording restores stage timings.

The focused run passed 76 tests across those GPU checks, timing histories,
audio histories, timestamp handling, timed-session lifecycle and render-worker
scheduling. The broader shader sweep was stopped after finding two pre-existing
fixture failures: FilamentFlight and FoldFlight at +60 dB never produced the
required initial visible light. An isolated reference build using the original
ColorOutput.cs, Renderer.Debug.cs and ShaderSilenceTests.cs reproduced both failures
(two failures and two passes across those scenes' gain cases). No shader was changed
as part of this work.
