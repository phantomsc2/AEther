# Current shader authoring

Scene discovery, reflected controls and the shader window are described in [shader authoring](shader-authoring.md).
The milestone notes below describe the earlier fixed-scene renderer; its fixed enum, positional scene constants, property grid and scene-restart behavior have been superseded.

# Vortice renderer milestone

The current application has four D3D11 scenes: Spectrum, Histogram, Mandelbox and
MandelFluid. The SharpDX Effects renderer was removed; MandelFluid migrates its
fluid/multigrid approach to native Vortice fullscreen passes. Earlier renderer
documents describe historical work.

## Projects and ownership

- AEther: analysis and shared signal types.
- AEther.NAudio: Windows audio capture.
- AEther.Vortice: native graphics resources, shader compilation, audio texture history.
- AEther.WinUI: application startup, full-window SwapChainPanel, owned settings and shader windows,
  capture/session lifecycle, keyboard shortcuts and borderless fullscreen.

The host passes its native window handle, physical pixel dimensions and a swap-chain
attachment callback to Renderer. WinUI attaches the composition swap chain through
`ISwapChainPanelNative`; the rendering library has no WinUI dependency. Composition
uses FlipSequential, opaque alpha and an inverse composition-scale transform. Pixel
dimensions and the transform follow layout and DPI changes. The HWND presentation
path remains available for renderer probes.

On Windows 11 the dedicated render thread waits for the compositor clock, then
for swap-chain readiness before sampling audio, and uses `Present(0)` with no tearing flags, submitting the latest frame for desktop
composition. This avoids both the XAML `CompositionTarget.Rendering` gate and the
composition swap chain's `Present(1)` gate, which followed the 60 Hz primary display
even with the window on the 165 Hz secondary display in the mixed-monitor probe.
Waiting for DXGI and then for a fresh compositor tick can skip alternate refreshes
at native resolution. The order is instead compositor tick first, DXGI readiness
second: if backpressure clears just after the tick, render immediately without
requiring another tick. A compositor tick alone is not permission to write this
swap chain's back buffer. Sampling audio before readiness can move the wait into
the GPU output pass and Present, making the frame's input older before display.
The compositor heartbeat can follow the fastest active display, so rendering on a
slower display may submit more frames than that display scans out. Windows 10 keeps
the DXGI wait plus `Present(1)` fallback. See Microsoft's
[compositor-clock guidance](https://learn.microsoft.com/en-us/windows/win32/directcomp/compositor-clock/compositor-clock).
The swap chain allows one queued frame; its waitable flag is preserved on resize.
See Microsoft's [wait-before-rendering guidance](https://learn.microsoft.com/en-us/windows/uwp/gaming/reduce-latency-with-dxgi-1-3-swap-chains).
Either pacing wait is interruptible for shutdown, and the handle is closed after the worker
stops. Timing shows frame-pacing wait separately from draw CPU and Present wait;
the FPS value measures render submissions, not physical scanout. GPU timing
delivery reports CPU elapsed time between recording the end queries and reading
their results; it includes GPU queueing, driver availability and polling cadence,
and is not a direct scanout measurement. Skipped timing samples indicate a busy
query pool, not skipped audio texture uploads or skipped render submissions.
Resize, shader reload, readback and
frame rendering are serialized around the D3D immediate context. Minimizing pauses
rendering, and restore consumes recent audio from the bounded mailbox. Settings
edits rebuild the complete session.
Spectrum selects the hardware adapter connected to the window's monitor, avoiding
native FP16 frame copies between GPUs on hybrid laptops. Other scenes use DXGI's
high-performance adapter preference. Changing between these policies, or moving
Spectrum to another monitor, replaces graphics resources on the UI thread while
preserving capture, analysis, audio history and shader parameter values. The new
textures receive the complete retained history before incremental uploads resume.
Closing a tool window hides it; closing the render window awaits audio/analysis
shutdown, detaches the swap chain, releases graphics resources and closes both tools.

### Mixed-refresh verification, 2026-10-20

The local WinUI probe used Spectrum at 1280x720, FP16 scRGB, an RTX 4070 Laptop
render device, and two Intel Arc-connected displays: a 2560x1440 primary at
approximately 60 Hz and a 3200x2000 secondary at 165 Hz. Quiet-input presentation comparisons
on the secondary measured 60.0 render submissions/s with `Present(1)` for either
one or two queued frames. `Present(0)` with either that display's vblank or the
compositor clock measured 165.1 submissions/s. Unpaced `Present(0)` exceeded the
display rate, so it is not used by itself.

The updated production worker, with continuous synthetic 48 kHz stereo audio and
1000 Hz analysis, measured 165.0 submissions/s at 1280x720: 5.22 ms mean pacing
wait, 0.76 ms draw CPU and 0.08 ms Present. Minimizing paused rendering; after
restore and resize to 1366x768 it measured 163.7 submissions/s. These are short
local Release runs after warmup, not GPU execution timings or a measurement of
physical scanout. The one-frame-queue comparison used quiet input; it establishes
the presentation ceiling rather than a controlled audio-processing speedup.

### Back-buffer readiness verification, 2026-10-26

A live report showed fresh audio at CPU submission (about 6 ms source age), but
an output GPU interval near 16.5 ms and delayed visual response. The Windows 11
worker was waiting on the compositor heartbeat while bypassing swap-chain
readiness. It now waits for the compositor tick first and swap-chain readiness
second, before reading audio or recording draw commands. A late readiness signal
does not require another compositor tick. The user reports the latency is gone
with the current build; a Spotify-specific pause/resume trigger was not isolated.

Serial 1280x720 Spectrum probes used a real WinUI SwapChainPanel, synthetic
timestamped audio, two seconds of warmup and three seconds of recorded timings.
The reference mode reproduced the former compositor-only worker by bypassing
the readiness gate. Means from the recorded CSVs were:

| Display / path | Output GPU interval | GPU timing delivery | Submission interval |
|---|---:|---:|---:|
| 60 Hz primary, old | 15.79 ms | 349.97 ms | 16.68 ms |
| 60 Hz primary, readiness gate | 0.20 ms | 5.82 ms | 6.06 ms |
| 165 Hz secondary, old | 5.84 ms | 39.30 ms | 6.07 ms |
| 165 Hz secondary, readiness gate | 0.19 ms | 5.90 ms | 6.06 ms |

Reports are under `artifacts/pacing-ab`. The compositor heartbeat can run at the
fastest display rate; 6.06 ms submissions on the 60 Hz monitor do not mean 165 Hz
physical scanout. Query delivery is not an audio-to-photon measurement. These
short diagnostics establish that the readiness gate removes the observed
output-stage stalls without halving the submission rate on the 165 Hz display.
OS-controlled occlusion/activation behavior can still occur; do not infer that
all hide/show transitions have been exhaustively verified from this comparison.

Reproduce with `AEther.Smoke --presentation-profile --scene=Spectrum
--width=1280 --height=720 --seconds=3 --monitor=primary` (or `secondary`). Add
`--bypass-swap-ready` only for the old-path reference comparison. The application
always enables the readiness gate. Regression tests cover both readiness/tick
orders, shutdown during either wait, and swap-chain readiness across resize.

### Native-resolution verification, 2026-10-21

At 3200x2000, serial DXGI/compositor waits reproduced approximately 82.5 submissions/s
despite sub-millisecond shader work. Removing the extra gate improved throughput;
the NVIDIA-to-Intel presentation path still incurred stalls at native resolution.
Spectrum now uses the Intel Arc adapter driving this 165 Hz panel.

The final Release production-worker run used native 3200x2000 FP16 scRGB output,
continuous synthetic 48 kHz stereo audio and 1000 Hz analysis. After two seconds of
warmup, an eight-second trace contained 1,323 frame intervals:

| Measurement | Result |
| --- | ---: |
| Render submissions | 165.00/s |
| Mean frame interval | 6.061 ms |
| p95 frame interval | 6.179 ms |
| Recent mean pacing wait | 5.93 ms |
| Recent mean draw CPU | 0.06 ms |
| Recent mean Present wait | 0.04 ms |

Minimizing paused rendering; restore and resize to 1366x768 returned to 165.0
submissions/s. Frame-interval statistics cover the full trace; the CPU/wait rows
use the timing panel's last 256 measurements. These are submission measurements,
not physical scanout timings. Resolution, FP16 output and shader appearance are
preserved. The local trace is
`artifacts/native-spectrum-profile/pipeline-20261021-221557-188.csv`.

Focused scheduler, swap-chain and timed-session regressions passed (12 tests),
including retained-history readback across adapter changes. Spectrum silence
checks passed on the display-adapter path (8 tests), retaining the generous decay
period and exact RGB checks across final frames. WinUI smoke checks passed for
scene changes without capture restart, parameter persistence, shader reload,
resize, fullscreen, session restart and shutdown.

## Audio handoff

Capture copies into the analyzer's existing bounded queue. The sequential analysis worker
publishes borrowed sample events. AudioHistory copies the retained RGBA descriptors into
owned stereo arrays and posts them to a bounded channel, dropping oldest queued rows on
overload. The render thread drains the channel, builds history and uploads textures.
No graphics operation holds an audio/analysis lock. DroppedRows reports discarded visual
updates separately from Analyzer.DroppedInputCount. This is a live display pipeline, not
a lossless offline consumer. Spectrum takes component maxima between rendered frames.

History uses timestamped slots, with a continuous monotonic playhead and inferred capture
times mapped to it at session creation. Rows expire even if loopback sends no silence
packets. The default one-second history occupies each half of Histogram. Frequency stays
discrete while time is interpolated. The current timing still inherits the analyzer's
estimated capture timestamps; hardware QPC timestamps are not used yet.

## Shaders

Common.hlsl contains the fullscreen triangle vertex shader, frame constants at b0, scene
constants at b1, four audio textures at t0–t3 and a sampler at s0. Scene source is appended
to this common header and compiled with D3DCompiler into Shader Model 5 vertex/pixel
programs. There are no Effects11 techniques or native Effects DLLs.

Mandelbox carries forward the finite-iteration box/sphere folds and history illumination
from mandelbox-slice.fx. Retained spectral descriptors drive its illumination and musical
time. The desktop does not need model meshes or a 3D scene graph for these fractals.

During development source shaders are found relative to the repository; deployed apps use
the copied Shaders directory. The host debounces file changes. Reload constructs all new
GPU shaders before replacing any working program. Failed reload preserves the old programs.

## Output precision

Scene colors are composed into an FP16 texture before presentation. `Present.hlsl`
decodes the existing display-encoded colors with the sRGB transfer function and writes
linear FP16 scRGB (`R16G16B16A16_FLOAT`, `RGB_FULL_G10_NONE_P709`) to the flip-model
swap chain. This avoids rounding soft gradients to eight bits before Windows composes
the image. The tone mapping and palette remain as authored; this change does not
increase highlight brightness or expand the scene gamut.

On HDR displays, reference white follows `DISPLAYCONFIG_SDR_WHITE_LEVEL`, queried for
the containing output once per second. Composition swap chains do not support
`GetContainingOutput`, so the WinUI path matches the window's monitor to DXGI outputs
across adapters. Polling follows monitor moves, HDR mode changes
and the Windows SDR-brightness slider. SDR displays use a scale of one. An unavailable
display query falls back to the nominal 80-nit scRGB reference white. See Microsoft's
[Advanced Color guidance](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range).

If FP16 scRGB presentation is unsupported, output falls back to RGBA8 SDR with stable,
decorrelated spatial dithering of half an eight-bit step per channel. Black and white
remain exact, and the pattern does not flicker over static images. FP16 output is not
dithered by the app; the compositor/driver manages conversion to the display signal.
Final panel precision and any downstream processing can still limit visible gradients.

`ReadPixels()` exports the scene as dithered, display-encoded RGBA8, independently of
the monitor's white level. `ReadPixels(false)` disables export dithering for diagnostics.
The GPU output check reads the actual swap chain, separately from the eight-bit export:
`dotnet run --project tools/AEther.Smoke -- . --output-only`. It checks dark-gradient
precision, the sRGB transfer function, exact black, stable unbiased fallback dithering,
resize and failed presentation-shader reload. The test's dark ramp retains more than
1,000 FP16 output levels versus at most 23 in the forced eight-bit path.

## Verification

Run:

```powershell
dotnet build AEther.sln -c Release
dotnet test AEther.Tests
dotnet run --project tools/AEther.Smoke -- .
```

The smoke tool uses synthetic stereo descriptors, without recording system audio. It
checks all four scenes render visible, distinct images, recreates the renderer, resizes,
reloads valid shaders, rejects an invalid shader while retaining working programs, and
checks history expires during silence. PNGs go into its ignored artifacts directory.
It also exercises invisible host windows with a synthetic source: settings ownership,
close/reopen, Space, F11 entry/restore, scene selection with full restart, and shutdown.
CPU tests check mailbox bounds, borrowed-memory copying, mono mirroring and finite values.

Manual checks: real-audio response, settings close/reopen, repeated setting changes,
input failures, minimize/restore, display selection and F11 transitions on each monitor.

## Spatial antialiasing

Distance estimation, log-distance depth interpolation, frequency/history coordinates and
exposure follow HEAD. AA is a checkbox: disabled uses one center sample, enabled uses deterministic
2×2 sampling, averaging linear output colors before gamma encoding. Both modes
uses the same field and depth; there is no AA-dependent iteration suppression. Existing
screen-derivative shading heuristics use each subpixel's footprint. They remain approximate,
so quality changes can affect those filters as well as resolve additional spatial detail.

The maximum iteration count accepts any positive integer, defaulting to HEAD's 10.
Musical drive controls requested depth as before; at the default limit the original depth
response is preserved. Quality and maximum depth apply live without restarting capture.
The experimental linear propagation-distance setting, tracer and material injection are removed.

GPU checks compare None directly against HEAD at a fixed fractional depth, check integer
depth continuity, and compare spatial AA against averaged larger None renders with quad
shading derivatives disabled in that reference fixture. Existing rendering, stereo,
reload/resize, history expiry and settings lifecycle checks remain active.

Very high requested depths retain the last finite estimate if the orbit or derivative
approaches float overflow. This is a numerical-range guard, not a fixed iteration cap.

Mandelbox settings now occupy a separate, always-visible property grid. Antialiasing,
maximum iterations and movement speed update live without capture restart. Movement
speed defaults to 1 (original), accepts nonnegative finite multipliers, and 0 freezes
movement. The renderer integrates musical-time increments multiplied by the current
speed into a separate movement clock, preserving pose when speed changes. Only slice,
rotation and zoom use that clock; depth, palette and history timing remain independent.
