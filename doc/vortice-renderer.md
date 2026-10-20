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

The dedicated render thread waits on DXGI's frame-latency waitable object before each
frame, including the first. On Windows 11 it also waits for the compositor clock and
uses `Present(0)` with no tearing flags, submitting the latest frame for desktop
composition. This avoids both the XAML `CompositionTarget.Rendering` gate and the
composition swap chain's `Present(1)` gate, which followed the 60 Hz primary display
even with the window on the 165 Hz secondary display in the mixed-monitor probe.
The compositor heartbeat can follow the fastest active display, so rendering on a
slower display may submit more frames than that display scans out. Windows 10 keeps
the DXGI wait plus `Present(1)` fallback. See Microsoft's
[compositor-clock guidance](https://learn.microsoft.com/en-us/windows/win32/directcomp/compositor-clock/compositor-clock).
The swap chain allows one queued frame; its waitable flag is preserved on resize.
Both waits are interruptible for shutdown, and the handle is closed after the worker
stops. Timing shows frame-pacing wait separately from draw CPU and Present wait;
the FPS value measures render submissions, not physical scanout.
Resize, shader reload, readback and
frame rendering are serialized around the D3D immediate context. Minimizing pauses
rendering, and restore consumes recent audio from the bounded mailbox. Settings
edits rebuild the complete session.
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
