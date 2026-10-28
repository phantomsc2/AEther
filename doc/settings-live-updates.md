# Settings updates and startup costs

Measured on 2026-10-26, Release / .NET 10.0.13, Intel Arc Graphics,
FP16 scRGB, 1280x720, 48 kHz stereo, default 97 frequency bins,
1,000 analysis ticks/second and two seconds of GPU history.

## Current update path

`AudioPanel` builds its analyzer editors from `AnalyzerOptions` property
metadata. Each committed edit raises `SettingsChanged`; the host pauses rendering,
stops capture, drains analysis, and constructs a fresh capture source and analyzer.
`Session.RestartAnalysisAsync` resets the graphics audio handoff while retaining
the renderer, device, swap chain and compiled shaders. F5 explicitly rebuilds the
whole session. The session gate serializes this work; a version check skips
obsolete queued requests. There is no per-property update dispatcher or dependency
graph. Invalid options are rejected before the active analyzer is stopped.

Pointer sliders now preview their numeric value during the gesture and commit
once on release (also on cancellation/capture loss). A gesture ending at its
original value does not commit. The same helper handles analyzer, graphics, and shader
sliders. Keyboard/automation changes and typed numbers still apply immediately.

Each analyzer now copies its input options at construction. Previously the UI and
running analyzer shared the same mutable object: most settings were copied into
fields, but `MaxInputAgeMilliseconds` was read from that object during processing,
and timing UI could describe settings newer than the running pipeline. Session
construction now uses the analyzer's copy for graphics bin meaning and publication rate; history duration belongs to graphics.

GPU authoring has a separate existing path: scene selection compiles the selected
scene if needed; shader parameter edits update its parameter block; file changes
reload shaders after a 250 ms debounce. They preserve capture and GPU history.
`ShaderWindow` saves scene parameter values across full session restarts. This
implementation preserves that behavior. Shader parameters remain live GPU edits.

## Graphics rebuild contract

`AnalysisOutput` is the immutable analysis-to-graphics DTO, resolved by
`AnalyzerOptions.GetAnalysisOutput(format)` from the running analyzer's settings
copy. It extends the original `Domain` concept with the **actual** publication
rate. Graphics retention is configured separately:

| Contract field | Why graphics needs it | Rebuild rule |
| --- | --- | --- |
| `Domain` | Bin count and meaning (minimum frequency and bins per octave) | Reset history on a new generation; reallocate textures when band count changes |
| `AnalysisRate` | Map rows to time; size history and handoff buffers | Reset history; reallocate textures if the computed row count changes |

`GraphicsOptions.HistorySeconds` belongs to `AEther.Vortice`, defaults to 2 seconds,
and accepts finite values from 0.01 to 60 seconds. Its control belongs to the generated
Graphics category in the right column below the scene selector and above shader parameters. Pointer slider
edits commit on release; numeric edits apply immediately.

`Renderer.SetGraphicsOptions` clears retained histogram data and reallocates only
histogram textures when their row count changes. Same-row-count edits still clear
history and update the visual time scale; an unchanged value does nothing. CPU/GPU
allocations are prepared before committing the new configuration. Failed edits
leave the active setting intact and restore the UI to its effective value.

Capture, analysis, mailboxes, source timing, live spectrum, feature envelopes,
musical clocks, fluid state, compiled shaders and swap chains survive a graphics
edit. The existing AudioHistory instance is retained. Analysis restarts preserve
the graphics duration. The main window keeps the selection across scene changes
and session recreation. Analyzer and graphics settings now persist through the
JSON store described in [Settings UI](settings-ui.md). The existing row cap
and history-dependent shader decay are unchanged.

The rate is `sampleRate / floor(sampleRate / requestedRate)`, rather than the
requested rate. Domain equality includes bin meaning, even when texture width is
unchanged. Channel count and sample encoding stay outside the contract because
the handoff already normalizes capture to two RGBA channels (mono is mirrored).

Backend selection, DFT window/SIMD, SWIFT tuning, Kalman implementation/mode/noise,
splitter windows, gain and input queue policy do **not** change the graphics
contract. Their output layout and units stay compatible. Every accepted analysis edit still
restarts all CPU analysis and clears history; this avoids mixing results produced
with different algorithms/settings without introducing per-filter lifecycle rules.

`Renderer.RestartAudio` consumes only `AnalysisOutput`, not the analyzer settings.
It resets row counters, pending packets, spectral envelopes and CPU history,
uploads zeros to the audio textures, and swaps in a new profiling destination.
Only band/row dimensions trigger texture reallocation. The renderer clock keeps
its original epoch so retained simulations do not freeze after a restart. Fluid,
feedback and scene parameter state survive and decay under the normal silence
rules. No contract change causes shader source reads, compilation or swap-chain
attachment. Newly selected scenes remain lazily compiled.

## Stage boundaries

| Stage | Existing owner | Settings / dependencies |
| --- | --- | --- |
| DFT | `ISpectrumProcessor` (SWIFT or finite DFT), per channel | Frequency domain, sample rate, backend/window, SIMD, SWIFT tuning |
| Smoother | `SpectrumSmoother`, per channel | Linear Kalman filters; actual backend carrier frequencies, tick interval, process and measurement noise |
| Splitter | `Splitter`, per channel | Domain, actual tick rate, frequency and time support |
| GPU | `Renderer` and `AudioHistory` | Band count, tick rate, history duration, output size, scene and shader parameters |

The settings UI uses these four category names. Input gain/capture and bounded
queue controls retain their Input/Pipeline categories. `Analyzer` remains the
sequential orchestrator; `Session` owns capture/analysis/render lifetime. Existing
stage classes already provide meaningful processing boundaries, so this pass
adds measurement scopes there without introducing another set of wrappers.

## Measurements

Run from the repository root:

```powershell
dotnet run --project tools/AEther.StartupProfile -c Release -- artifacts/startup-profile.csv 11
```

There are 11 fresh analyzer/renderer instances per case, in one process. The table
uses the upper median of the ten subsequent runs, excluding the first run of
each case. CPU stage times include **both channels**. They are constructor wall
times, not steady-state processing costs or filter settling times.

| Warm stage (ms) | SWIFT / Mandelbox | SWIFT / MandelFluidv4 | DFT / Mandelbox | DFT / MandelFluidv4 |
| --- | ---: | ---: | ---: | ---: |
| Spectrum setup | 0.518 | 0.389 | 0.863 | 0.509 |
| Smoother setup (both filters) | 0.022 | 0.016 | 0.017 | 0.009 |
| Splitter setup | 0.105 | 0.257 | 0.082 | 0.019 |
| Whole analyzer setup | 0.996 | 0.736 | 1.389 | 0.587 |
| Whole GPU setup | 247.559 | 349.634 | 243.155 | 353.431 |
| GPU device creation (nested) | 44.825 | 41.598 | 40.739 | 41.672 |
| GPU output creation (nested) | 3.480 | 2.309 | 2.291 | 2.436 |
| Shader discovery/compilation (nested) | 191.133 | 292.523 | 185.469 | 296.866 |
| First draw CPU submission | 2.853 | 7.463 | 2.522 | 7.850 |
| Start to first GPU-complete readback | 277.450 | 396.641 | 267.319 | 403.401 |
| Stop/drain/dispose | 18.604 | 21.475 | 14.910 | 21.368 |

The first process run (SWIFT / Mandelbox) took 23.084 ms for analyzer construction:
12.828 ms spectrum, 1.721 ms smoother and 0.518 ms splitter. GPU construction took
408.586 ms, and the first completed readback arrived at 1,208.517 ms from the
start. First-run disposal took 440.499 ms. Only this first case includes initial
process/JIT/device warmup; subsequent first-of-case values are not cold starts.

Warm p95 GPU construction was 392.489 / 359.419 / 253.234 / 377.409 ms in table
order. These are small samples; p95 selects the maximum of ten warm observations.
GPU totals include more than their listed nested substeps, and medians of nested
steps must not be added to reconstruct the total.

The profiler uses a disposable HWND and the real constructors, with no audio
capture or WinUI composition attachment. `Render(false)` includes lazy scene
resources and the initial history upload; readback synchronizes GPU completion.
The scene is silent. Capture-device startup, first audio processing/JIT, audible
filter warmup, WinUI attachment and display latency are not measured. Readback
latency is not time to a visible frame. Raw observations are written to the CSV.
Live session startup scopes are also visible in Settings > Timing.

## Resulting restart policy

Keep one generic restart of every CPU stage for committed analyzer settings.
All CPU stages together initialize in roughly a millisecond after warmup;
selective smoother/splitter rebuilding adds lifetime complexity for little benefit.
Committing only on release removes redundant restarts. Graphics handles its own
contract and dimension-dependent allocations, retaining compiled shader programs.
The table above measures fresh construction, not the new CPU-only restart path.

Validation: 34 analyzer/session regressions passed, including GPU lifetime and
settings snapshot coverage. The SWIFT settings smoke check passed. A real mouse
drag changed the diagnostic commit counter from 2 to 3. The WinUI window smoke
check passed on retry; its first run failed the synthetic-history visibility
assertion before reaching restart validation, so that check is timing-sensitive.
Use `dotnet run --project tools/AEther.Smoke -c Release -- --slider-probe` for a
manual commit-count window (F5/F6/F11 exit).

Additional automated coverage exercises graphics-contract equality, integer batch
rate rounding, retained clock origin, rejected settings/source ownership, history
uploads, and repeated analysis restarts with changed gain, backend, SIMD/SWIFT,
smoother, splitter, queue, domain, rate, retention and sample format. The restart
regression invalidates shader source after initial compilation and asserts that
both selected and previously compiled scene programs survive; unselected scenes
stay lazy. `ShaderSilenceTests.AnalysisRestartPreservesSilenceDecayOfRetainedGraphics`
checks retained histogram, Mandelbox and periodic fluid state after active audio,
allowing the existing generous decay period before checking all RGB channels over
multiple final frames. These tests use hidden test HWNDs; no UI automation is needed.
