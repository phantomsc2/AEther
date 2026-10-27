# AEther

## Description

Audio analysis and audio-reactive DirectX visualization on Windows using NAudio.

The original STBR splitter is preceded by an independent Kalman smoother.
Choose log-amplitude smoothing (A) or complex-resonator smoothing (B), with
explicit acceleration-noise intensities and a fixed measurement-noise floor.
Spectrum's `CompareSmoothers` view shows both on the same input, A left/red and
B right/cyan, with raw amplitude marked in white. Normal S/T/B/R controls retain
their original meaning. See [models and comparison](doc/kalman-smoothing.md).

## Project Layout

- **AEther**: Core library to read and analyze sample data
- **AEther.Benchmarks**: benchmarks for the active STBR splitter using [BenchmarkDotNet](https://benchmarkdotnet.org/)
- **AEther.NAudio**: audio input with [NAudio](https://github.com/naudio/NAudio)
- **AEther.Tests**: using [NUnit](https://nunit.org/)
- **AEther.Vortice**: D3D11 fullscreen shaders, audio textures and runtime shader compilation
- **AEther.WinUI**: primary settings hub, separate render canvas and tool windows, application lifecycle

- **tools/AEther.CaptureProbe**: measures NAudio loopback packet timing
- **tools/AEther.Smoke**: exercises analysis and rendering with synthetic audio

## Running

Install the .NET 10 SDK and Windows SDK/build tools, then run
`dotnet run --project AEther.WinUI` on Windows 10 (1809+) or Windows 11, x64.
The app is unpackaged and includes its Windows App SDK runtime, so no MSIX
installation or separate Windows App Runtime installation is needed. The .NET 10
desktop runtime is required when running a build without the SDK.
The app starts with the settings hub, captures the default system audio output,
and runs its analysis view with no scene selected. Selecting a scene opens an
untitled, resizable canvas window with a standard title bar. Its content is a
Direct3D `SwapChainPanel`. Settings and shader
properties use standard WinUI controls with the system's default appearance.

- **F11** or double-clicking the canvas toggles fullscreen on the canvas window's current display.
  Closing the canvas dismisses it; select a scene in the hub to reopen it. Closing the hub exits the application.

The resizable hub has two equal-width columns. Input, Gain and the live analysis
view are pinned on the left. Below them, DFT, Pipeline, Smoother and Splitter are
separate expandable blocks, all closed initially, inside one shared scroller.
HistorySeconds is a graphics setting in the right column, below the scene selector
and above shader controls. Changing it clears the histogram without restarting audio.
There is no outer Audio expander.
The Scene picker starts empty above HistorySeconds. Selection opens the
canvas and reveals dynamic shader parameters, Reload and Reset defaults below it.
Only the Audio settings and shader settings scroll, independently inside their
panels. The overall window and analysis view never scroll. Debug remains a separate
window for status and timing text.

Audio edits restart analysis while retaining graphics. Drag the canvas to choose
its display; monitor settings follow its location, and restarts leave it there.
Scene selection and parameter edits keep capture, analysis and history running. Closing a side
window hides it; closing the hub shuts down all windows and capture.
Closing the canvas clears the scene picker. Selecting the same scene again reopens
it with retained parameters and without restarting capture. Audio edits and shader
reloads leave the picker empty while the canvas is dismissed.

The analysis view shares the renderer and history while the canvas keeps its
selected scene. It fills the available space above the bottom-aligned Audio settings,
with thinner bands when the window is too short. It remains live with the
canvas closed; hiding or minimizing the hub stops its drawing work. An explicit
`--scene=Name` argument opts into launching a scene on startup.

Scenes are the top-level files in `AEther.Vortice/Shaders/Scenes/*.hlsl`. Shared code
lives in `Shaders/Common`. Save source files in the IDE to reload without a .NET build;
adding or deleting a scene updates the shader selector. Failed reloads retain the
previous programs and controls, with diagnostics in the shader panel and Debug window. Published apps
use the `Shaders` tree beside the executable.

Editable values are reflected from `cbuffer Parameters`; nested structs become groups,
bools become checkboxes, and floats/float vectors use linked [0, 1] sliders and numeric
inputs. Analyzer options are grouped by category using expanders, numeric inputs,
switches and enum selectors. Names are displayed
unchanged. Defaults live in HLSL. Values survive compatible reloads and session restarts
within the process; they are not saved between app launches. See
[shader authoring](doc/shader-authoring.md) for the input contract, examples, mappings,
and current limitations.

Mandelbox and MandelFluid share fractal geometry. MandelFluid adds persistent pigment:
tones supply body material, background drives turbulence and rhythmic attacks highlight the
field. Histogram displays one second of history per stereo side, with now in the center.
Mono is mirrored; multichannel inputs use the first two channels.
Presentation uses a 16-bit floating-point scRGB swap chain when supported, preserving
smooth dark gradients on HDR displays. Windows' SDR reference-white setting controls
the brightness on HDR monitors. An 8-bit fallback and PNG exports use stable spatial
dithering. The settings timing panel reports the active output format. This improves
precision while retaining the scenes' existing tone mapping and color range.
See [renderer notes](doc/vortice-renderer.md) for the pipeline and verification commands.

Intel Arc is the global rendering and performance target for every scene, test,
and profiling tool. DXGI enumeration identifies Arc hardware by Intel vendor ID
and adapter description; adapter indices and the window's monitor do not select
the GPU. An Arc initialization failure is reported instead of silently switching
to a faster device. Machines without Arc use the default hardware adapter.
Scene changes and monitor moves keep the existing renderer and audio history.
The settings timing panel shows the GPU actually used. Restart after rebuilding
to apply this policy to an existing session.

## Audio capture

NAudio is pinned to the stable 3.1.0 release. `Loopback` uses
`WasapiRecorderBuilder` with the endpoint mix format, a 1 ms requested buffer,
event synchronization on Windows 10 1703+, and MMCSS "Pro Audio" priority.
Older Windows versions use polling. Windows rounds the buffer to its supported
shared-engine duration; a 1 ms request does not guarantee 1 ms packets.
`WithLowLatency()` uses IAudioClient3 and does not support loopback.

`SampleSource.DataAvailable` supplies a `ReadOnlySpan<byte>` valid only during
the callback. Consumers must copy data before returning if they retain it.
`Analyzer.PostSampleSpan` copies directly into pooled storage for its bounded
queue; `PostSamples(ReadOnlyMemory<byte>)` remains available for existing callers.

`AEther.NAudio.Input.PacketAvailable` optionally exposes the original WASAPI
packet flags, device frame position and QPC position (in 100-nanosecond units).
Its buffer has the same callback-only lifetime. Positions marked `TimestampError`
must not be used for timing. Subscribe to either this event or `DataAvailable`
when feeding an analyzer, so each packet is processed once. The analyzer still
uses estimated arrival-based timing; packet metadata does not yet reset DSP state
on discontinuities or replace those timestamps.

Run `dotnet run --project tools/AEther.CaptureProbe` while audio is playing to
compare 1, 2, 5 and 10 ms requests. The CSV contains timing metadata only.
