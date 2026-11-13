# AEther

## Description

Audio analysis and audio-reactive DirectX visualization on Windows using NAudio.

The original STBR splitter is preceded by an independent Kalman smoother.
Choose log-amplitude smoothing (A) or complex-resonator smoothing (B), with
explicit acceleration-noise intensities and a fixed measurement-noise floor.
See [smoothing models](doc/kalman-smoothing.md).

The Splitter's **Frequency tilt** slider applies a power-law gain after decomposition,
preserving linear amplitude response: 0 = None, +3 dB/octave (default),
+6 dB/octave (max). Gain is unity at 1 kHz, attenuating lower frequencies
and boosting higher frequencies. All S/T/B/A outputs and residuals share this
weighting; raw and smoother comparison diagnostics stay unweighted.

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

### VS Code

Open the repository folder and install the recommended C# Dev Kit extension.
Press F5 to build and debug the WinUI app, or use **Run Build Task** to build it.
The **test AEther** task runs the NUnit suite. Add arguments such as
`--scene=Planet` to the `args` array in `.vscode/launch.json` when needed.
The launch configuration uses managed debugging; Rider's native debugging setting
is not carried over.

- **F11** or double-clicking the canvas toggles fullscreen on the canvas window's current display.
  Closing the canvas dismisses it; select a scene in the hub to reopen it. Closing the hub exits the application.

The resizable hub uses whole-window Audio and Video tabs. Input, Gain and the live
analysis view are on the Audio tab. Below the view, DFT, Pipeline, Smoother and
Splitter are always-expanded cards in a staggered layout inside one shared scroller.
Pipeline includes a graphics-owned **Peak emphasis** aggregation parameter:
0 = Mean, 0.5 = RMS, 1 = Max (default emphasis 0.172). Intermediate values use a power mean with
`p = 1 / (1 - a)`, weighted by each row's overlap with the display interval.
Missing intervals contribute zero; Max includes every row with positive overlap.
Scenes hold the aggregate between arrivals until it becomes stale after 50 ms.
Changing peak emphasis preserves history and analysis. Spectrum and Debug diagnostics
continue to show only blocks newly consumed in each display frame.
HistorySeconds, Peak emphasis and Exposure are graphics settings in the Video tab's
Settings group. Changing HistorySeconds clears the histogram without restarting audio.
The Scene Parameters group starts with only an empty Scene picker. Selection opens the
canvas and reveals dynamic shader parameters, Reload and Reset defaults below it.
Only the Audio settings and shader settings scroll, independently inside their
panels. The overall window and analysis view never scroll. The bottom status strip
expands into a compact TableView with aligned Time, Severity, Source and Message
columns. Severity is shown as an icon with a tooltip. The status strip shows an
icon and count for each severity present in the retained event history. Counts
follow that history, including across pipeline restarts.

Serilog writes Information and above to compact structured JSON (`.clef`) in
`%LocalAppData%\AEther\logs`, asynchronously, rolling daily or at 10 MiB with
14 files retained. The UI keeps the latest 2,000 events for this application run.
Startup timings and configuration belong in these logs; continuous frame time,
GPU time and estimated audio age remain in the live graphics. Detailed GPU stages
remain available through explicit profiling and are not enabled by opening events.

Audio edits restart analysis while retaining graphics. Drag the canvas to choose
its display; monitor settings follow its location, and restarts leave it there.
Scene selection and parameter edits keep capture, analysis and history running. Closing a side
window hides it; closing the hub shuts down all windows and capture.
Closing the canvas clears the scene picker. Selecting the same scene again reopens
it with retained parameters and without restarting capture. Audio edits and shader
reloads leave the picker empty while the canvas is dismissed.

The control window is a whole-window standard tab view. The **Audio** tab places
the debug spectrum above the input and analyzer settings; the **Video** tab places
the combined frame-timing graph above the scene, graphics, and shader/theme
parameters. Each tab uses a single-column `GroupHost`: Audio has Spectrum and Settings;
Video has Timings, Settings and Scene Parameters. These native Expanders start expanded.
Collapsed groups occupy only their headers. Auto-sized groups fit their content;
Fill groups share the remaining height. Toolkit splitters resize consecutive expanded
groups, skipping collapsed groups, and allocations survive collapse/reopen. Content
cards use Toolkit StaggeredLayout independently inside each group. There are no
vertical splitters. The analysis view shares the renderer and history
while the canvas keeps its selected scene. It remains live with the canvas closed;
hiding or minimizing the hub stops its drawing work. An explicit `--scene=Name`
argument opts into launching a scene on startup.

Scenes are registered by short C# setup in `SceneDefinition.Defaults`, with HLSL
sources in `AEther.Vortice/Shaders/Scenes`. Shared code lives in `Shaders/Common`.
Save HLSL source files to reload without a .NET build. Adding a scene requires
a C# registration; deleting a registered source removes it from the selector. Failed reloads retain the
previous programs and controls, with diagnostics in the shader panel and event log. Published apps
use the `Shaders` tree beside the executable.

`Atmosphere` is a ground-level single-scattering sky with a Draine phase function,
Rayleigh-derived RGB coefficients, Transmittance
and Sky-View LUTs, sun/view controls, and raw LUT previews. It works without audio;
the final background pass uses a lookup with no integration. See the
[atmosphere scene](doc/atmosphere.md) for physical parameters and verification.

`Planet` uses the same atmosphere transport around an invisible opaque sphere, with
an orbit camera, a large diffuse atmosphere, foreground scattering, and an atmospheric limb. Its two LUTs
keep integration out of the final render pass. See [Planet](doc/planet.md).

`Flame` iterates a bounded affine fern IFS by inverse warping with equal branch
weights and Jacobian density correction. Shape controls act on continuous raw
feedback; no normalization or stabilization is applied. See [Flame](doc/flame.md).

Editable values are reflected from `cbuffer Parameters`; nested structs become groups,
bools become checkboxes, and floats/float vectors use linked [0, 1] sliders and numeric
inputs. Analyzer options are grouped by category using expanders, numeric inputs,
switches and enum selectors. Names are displayed
unchanged. Ordinary shader defaults live in HLSL; allocation defaults live in typed
C# effect options and use the shared settings panel generator. Values survive compatible reloads and session restarts
within the process; they are not saved between app launches. See
[shader authoring](doc/shader-authoring.md) for the input contract, examples, mappings,
and current limitations.

`FractalCut` draws parallel Z slabs back-to-front through a fixed Julia-box volume,
with a static frontal perspective camera. Each slab samples binary membership
at its ray-segment midpoint and integrates homogeneous emission/absorption with
`alpha = 1 - exp(-extinction * distance)`. Premultiplied hardware blending takes
place in linear floating-point RGBA before tone mapping. Segment distance includes
perspective and clipped boundary slabs. There is no lighting, antialiasing,
temporal history or audio input. Run `AEther.Smoke.exe . --fractal-cut-only` for
720p previews, analytic transport checks and a two-color draw-order check.
Its normalized Box controls map Scale to [-4,4], InnerFoldRadius to [0,1]
(with a numerical guard at zero), and Iterations to integers [1,32]. Defaults
are scale -2.152, guarded zero inner radius and 13 iterations (normalized
Box controls: 0.231, 0, 0.382).
JuliaSeed components map to [-2,2], with a static default of (-1.468,-0.684,0.756)
(normalized 0.133,0.329,0.689).
Extinction maps to [0,4] per fractal-coordinate unit, default 1.256 (normalized 0.314); zero is transparent.
CutScale controls slice spacing over the same six-unit volume: slice count is
`ceil(2^(8 - 4*CutScale))`, from 256 to 16, default 256 (normalized 0). Increasing spacing reduces
sampling detail without changing the volume extent or intentionally changing its
optical density. The exponential integral is exact within each sampled slab;
fractal boundaries still have spatial sampling error.

MandelPond and MandelFluid share fractal geometry. MandelFluid adds persistent pigment:
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
