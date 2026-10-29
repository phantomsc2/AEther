# Vortice renderer

The rendering backend hosts the common FluidSimulation, RasterGeometry, Projection
and RasterLight modules. Scene discovery and reflected controls are documented in
[shader authoring](shader-authoring.md).

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
updates follow their owning subsystem; shader and graphics edits do not restart
capture.

## Audio handoff

Capture and analysis publish borrowed sample events. AudioHistory copies retained
descriptors into owned stereo arrays and hands them to the render thread through
bounded queues. GPU uploads and history maintenance run on that thread.
DroppedRows reports discarded visual rows separately from analyzer input drops.

Timestamped source intervals drive history placement and the display-interval
spectrum aggregate. Mean/RMS/Max aggregation applies to every scene and the
analysis preview; stale live values expire while history continues aging.
See [latency profiling](latency-profiling.md) for source-clock and handoff details.

## Shaders

Common/Inputs.hlsl supplies frame/audio inputs and the fullscreen vertex shader.
Scenes supply PS and include the central modules they need: FluidSimulation,
RasterGeometry, Projection and RasterLight. GPU LUT construction is part of
RasterLight. Common/Postprocessing.hlsl and Present.hlsl handle final output.
Bindings and parameter layouts come from each compiled entry point's reflection;
capabilities are activated by the final PS's compiled resources.

Repository launches use the source shader tree; deployed apps use the copied
Shaders directory. Shader reload builds candidate programs before replacing
working programs, and failure preserves the working set. Scene selection is
explicit and nullable. See [shader authoring](shader-authoring.md) and the
[module contracts](shader-contract-assessment.md) for the supported interfaces.

## Output precision

Scene output is composed into an FP16 texture before presentation. Visualization
shaders return linear illumination through `LinearOutput`; diagnostic display colors
use `DisplayOutput` or the compatible `float4(rgb, 1)` convention. `Present.hlsl`
applies the shared exposure and tone curve to linear illumination and decodes
diagnostic colors with the sRGB transfer function. It writes linear FP16 scRGB
(`R16G16B16A16_FLOAT`, `RGB_FULL_G10_NONE_P709`) to the flip-model swap chain,
avoiding eight-bit quantization before Windows composition. See the
[output contract](shader-authoring.md#minimal-scene) for the helpers and curve.

On HDR displays, reference white follows `DISPLAYCONFIG_SDR_WHITE_LEVEL`, queried for
the containing output once per second. Composition swap chains do not support
`GetContainingOutput`, so the WinUI path matches the window's monitor to DXGI outputs
across adapters. Polling follows monitor moves, HDR mode changes
and the Windows SDR-brightness slider. Active HDR also uses the output's reported
peak luminance to set tone-map headroom, retaining highlights above reference white.
SDR displays use a scale and headroom of one, including with FP16 presentation.
An unavailable display query falls back to nominal 80-nit reference white and SDR
headroom; missing peak luminance also uses SDR headroom. See Microsoft's
[Advanced Color guidance](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range).

If FP16 scRGB presentation is unsupported, output falls back to RGBA8 SDR with stable,
decorrelated spatial dithering of half an eight-bit step per channel. Black and white
remain exact, and the pattern does not flicker over static images. FP16 output is not
dithered by the app; the compositor/driver manages conversion to the display signal.
Final panel precision and any downstream processing can still limit visible gradients.

`ReadPixels()` uses the presentation shader and a lazy export target to produce
dithered, display-encoded RGBA8 with SDR headroom and the last rendered frame's
exposure, independently of the monitor's white level. `ReadPixels(false)` disables
export dithering for diagnostics. Normal rendering still has one scene and one
presentation pass; export work runs only on readback.
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

The default smoke check exercises discovered scenes, reflected controls,
reload/rollback, resize and shader-window behavior using synthetic audio.
`--output-only` checks presentation and export; `--fluid-only` checks the
shared solver and ring materials. Captures go to the ignored artifacts directory.
The window checks cover selection, close/reopen, settings, restarts and shutdown.

Manual checks: real-audio response, settings close/reopen, repeated setting changes,
input failures, minimize/restore, display selection and F11 transitions on each monitor.

## Spatial antialiasing

Scenes own spatial sampling. Shared fractal helpers use a center sample or a
deterministic 2x2 pattern according to their reflected quality controls, averaging
linear illumination before shared postprocessing. Derivative-based shading remains
an approximation and can change with the selected sample footprint.
