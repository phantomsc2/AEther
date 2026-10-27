# AEther revival — first technical baseline

## Artistic direction

This project expresses the creator's eyes-closed experience of music. Metal, rock,
ambient, trance and Goa share space, time and structure; instruments and voices
are building blocks. The scene is centered roughly between the eyes.

Reference: Carbon Based Lifeforms, Central Plains. Original notes are preserved in
`ideas/songs/Carbon Based Lifeforms - Central Plains`.

- Bass: warm air descending as clouds and monsoon rain onto a plain.
- Synth: rays and planes intersecting the atmosphere and sweeping around like thunder.
- Voices: distant upward welling, rising into the stratosphere and whirling around structures.
- Rhythm: dancing forward camera movement and head bobbing; rhythmic lighting also feels right.
- Transients: immediate impulses, then momentum, bending and decay. They may generate branches.
- Prefer simple, explicit behavior and trustworthy calculations. Avoid random jitter by default;
  glitch/breakcore could use a separate deliberately discontinuous mode.
- Atmospheric rendering is a strong existing direction. Let lighting reveal structure through space.

## Implemented baseline

- Removed the unused, missing tree-model dependency.
- Pinned the installed stable .NET 9 SDK to prevent accidental use of the .NET 11 RC.
  A .NET 10 migration remains a later, separately verified change; its SDK is not installed here.
- Replaced the timer/Dataflow/Pipe analysis pipeline with one bounded sequential worker.
  Capture callbacks copy device-owned memory, never wait for analysis, and report queue rejections
  through `DroppedInputCount`. Capacity counts capture packets, not milliseconds.
- Stop drains accepted full batches and discards an incomplete tail. Subscribers must copy pooled
  event memory if they retain it. Stop is terminal; create a new analyzer for a new session.
- Exact batches and samples split across callbacks are handled without waiting for another packet.
- Amplitude attacks take a separate path before tonal DFT/median analysis. This is an amplitude
  novelty detector, not beat tracking or instrument separation. Detection is still constrained by
  capture buffering, analysis batching (~3.9 ms by default), worker load and display refresh.
- Render-frame peak retention preserves attacks between frames. A critically damped spring
  provides continuous motion; rhythm illumination has immediate attack and 180 ms exponential decay.
- The existing 3D scene's light responds to attacks. Spring state is exposed in frame constants for
  future structure/camera work; the Central Plains world itself is not yet implemented.
- Monotonic frame timing, clamped integration delta, synchronized spectrum/history uploads before
  drawing, and vsync replace wall-clock animation and the unpaced presentation loop.
- Restarts are serialized, stopped sessions disposed, closing awaits analysis completion, and shader
  notifications are marshaled to the UI and debounced. Active shader variants compile before the old
  session is retired, preserving it on a shader compile error.
- Texture ownership, cube-slice views, fluid solvers, particles, models and mapped streams are released.

## Verification and commands

Run from the repository root:

```powershell
dotnet build AEther.sln --no-restore
dotnet test AEther.Tests/AEther.Tests.csproj --no-restore
dotnet run --project tools/AEther.Smoke/AEther.Smoke.csproj -- .
dotnet run --project AEther.WinUI/AEther.WinUI.csproj
```

The hidden renderer smoke tool uses synthetic spectral input, renders all six states, checks
Direct3D debug-layer errors, compiles active shader variants and recreates graphics modules.
It writes PNG review frames under `tools/AEther.Smoke/artifacts` (ignored by Git).
It does not prove live audio synchronization or long-session stability.

Controls: Space toggles the panel; F5 recreates the session; F11 toggles fullscreen;
Escape closes. Select SceneState for atmospheric lighting. Loopback is the default input.

## Audio and histogram review

Regression tests exposed and now cover the DFT normalization and indexing defects:

- Windowed scalar and SIMD DFT kernels were centered at M+1 rather than M. Corrected both.
- Window coherent gain was not normalized; an on-bin Hann-windowed sine read a quarter of
  its actual amplitude with the combined centering/gain defects. Corrected coherent gain.

SIMD now uses the same Hann window as scalar processing, with scalar fallback on narrow vectors.
Tests compare recursive output to a direct windowed DFT through startup and ring wrap, check sine
gain/phase stability and splitter silence/attack behavior.

Histogram history defaults to two seconds (`GraphicsOptions.HistorySeconds`), owned by graphics.
A continuous fractional playhead follows the analysis sample rate, independent of capture packet
bursts. History presentation delay defaults to zero. An optional positive
`HistoryPresentationDelayMilliseconds` supplies adjacent recorded rows for time interpolation when
capture packets arrive late; zero minimizes delay but can expose playhead stalls.
Frequency remains discrete; no motion blur or general amplitude smoothing was added. The ring's
oldest invalid region is excluded rather than wrapping newer audio into older history.
Live transient delivery and lighting have no added presentation buffer.

Colors now describe the four-way experiment: red is time-smooth frequency ridges, green is
frequency-smooth temporal spikes, and blue is smooth in both dimensions. Residual spikes in both
dimensions are calculated but excluded from every visual channel and visual control. `R` shows
combined retained level in grayscale. These filters are not instrument separation.

Frequency resolution still requires time: the lowest default bin (27.5 Hz, about eleven cycles) has
an approximately 400 ms DFT window. Hann weighting emphasizes its midpoint; the sustained median
adds roughly half its 100 ms window. Visual attacks now come from the retained transient branch. Display latency,
window duration and audio input drops must be distinguished when tuning responsiveness.
The title reports rejected input callbacks to expose overload rather than silently calling it jitter.

## Remaining work

- Tune input/attack sensitivity against real music; measure latency and frame pacing.
- Tune the first Central Plains scene against the creator's live listening feedback.
- The original fluid and feedback simulations still need stability and frame-rate audits;
  the analytic CPU spring is frame-rate independent, not a claim about every existing shader.
- Migrate SharpDX/Effects to modern bindings and explicit shader pipelines, one verified scene at a time.
- Test failed device initialization, device changes, resize/fullscreen, and full DJ-set duration.
