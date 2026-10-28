# Latency and profiling

## Device-position and display-time pipeline (2026-10-21)

The WinUI session prefers the core `ITimedSampleSource` capability. Its borrowed-span callback
adds `AudioPacketTiming`: first-frame position, first-sample monotonic timestamp, quality,
and silent/discontinuity/timestamp-error flags. Existing byte callbacks and NAudio's raw
native packet callback remain available; subscribe to one ingestion path to avoid duplicates.
WASAPI QPC positions are expressed in 100 ns units and are converted once into `Stopwatch`
ticks. Wall-clock `DateTime` values do not drive synchronization.

Valid WASAPI device positions are authoritative. For a packet starting at frame `p`,
its frame offset `k` represents stream time `(p + k) / sampleRate`. Analysis aligns
complete batches to those driver frame indices; `SampleIndex` is the actual batch's
starting device frame and `AnalysisRow` is that index divided by batch size. Counted
bytes provide positions only for offline/legacy sources and invalid-position packets.
QPC differences never insert frames, shift row indices, or reset contiguous signal processing.

Analysis retains its sample-count cadence (`sampleRate / floor(sampleRate / TimeResolution)`).
Rows carry their completed source interval `[SampleStartTimestamp, SampleEndTimestamp)`,
timestamp quality and discontinuity generation; `SampleIndex` still identifies the batch start.
With a stream correlation, both interval boundaries are computed from the driver's frame
indices and sample rate through that fixed correlation. Per-packet QPC noise therefore
cannot change observation durations, temporal averaging or envelopes. Raw packet QPC stamps
remain available in the capture callback/probe and determine input age separately.
Packets can contain many rows, and a row can cross packet boundaries. Arrival/completion times
are separate from represented sample time. Invalid timestamps extrapolate from contiguous
sample history; absent history uses callback time minus packet duration and is marked estimated.
After an untimestamped capture pause, a new arrival anchor is established rather than keeping
resumed packets permanently beyond the live age budget. A driver discontinuity or backward
device-position reset establishes a new `AudioStreamClock` correlation from the next valid
device-frame/QPC pair. It does not fabricate missing samples from elapsed host time.
Forward device-position gaps preserve their exact absent frame indices. Timestamp errors
are explicitly estimated using known packet lengths until valid device positions return.

Capture does not wait for analysis or graphics. Queue overflow drops the **oldest** queued
packet. `BufferCapacity` bounds packet count; `MaxInputAgeMilliseconds` defaults to 50 ms and
also trims old prefixes of oversized live packets between batches. Offline byte-only ingestion
does not use this wall-time age limit. Age is tested at completed analysis boundaries: samples
needed by a fresh tick are retained even when its duration exceeds the age budget. Offline ingestion
remains sample-count based. Known missing samples and stream discontinuities reset
DFT rings/state, statistical fits and waveform filters; they are not concatenated or silently
invented as zeros. Explicitly silent packets override their buffer contents with zeros.

The histogram is the exchange point between the two clocks. A fixed `AudioStreamClock`
pair converts presentation QPC time to a fractional analysis-row coordinate. Stored rows
remain at driver-derived ring slots; packet arrival and QPC jitter cannot reanchor scrolling.
A new stream clock epoch clears the old history mapping. This uses the nominal sample rate;
it does not implement long-term device/host clock drift calibration or speaker latency calibration.

Incoming history uploads occur immediately, independently of presentation. Late rows fill
their assigned historical slots; ahead-of-cursor rows can already be in GPU memory. The shader
samples only the history behind its display cursor, masking unwritten history rather than
slowing down or catching up. The renderer's live spectrum/envelopes select completed source
intervals at or before the presentation target separately from history storage.
The Histogram scene loads discrete rows over their actual analysis intervals without
interpolating amplitudes between neighboring blocks. Missing rows and the unwritten strip
remain black. Its sharp edges move on the physical pixel grid.
The default added presentation delay is **zero**. Explicit delay applies to all audio fields.
Live spectral fields use the Pipeline panel's **Peak emphasis** aggregation parameter:
0 = arithmetic mean, 0.5 = RMS (default), 1 = maximum. Intermediate values use the
power mean with `p = 1 / (1 - a)`. Each row is weighted by its fractional overlap
with the audio interval; missing intervals contribute zero without renormalization.
Max includes every row with positive overlap. Normalizing each component by its
maximum before exponentiation prevents overflow as the parameter approaches 1.
Individual historical rows retain their original values.

The Spectrum scene displays the latest new analysis block consumed in each display frame,
including faint solid bars for the absolute momentary residual, without averaging blocks
or holding the previous frame. S/T/B/R visibility controls fall back to raw amplitude
when all four layers are disabled.
ShowSum replaces intensity display with one white bar whose length is the sum of
the selected layer amplitudes, including the full absolute residual amplitude.
With no selected layers it continues to show raw amplitude. The I shortcut toggles ShowSum.
No newly consumed block means black. When multiple blocks arrive before one refresh, Spectrum
shows the newest eligible block and Histogram retains each at its original position; they
are never played back across later refreshes to catch up. Blocks older than the existing
50 ms live-envelope freshness limit remain available only in history.

For the other scenes, no new packet does not mean a silent frame. The last aggregate can survive up to 50 ms of source
age, then live values clear and history continues aging. A discontinuity cancels retention.
The live handoff holds at most 100 ms of rows plus any explicitly requested presentation reserve;
shader history duration is independent. After stalls only the latest 50 ms can excite current
envelopes/motion. Stale rows cannot create a new flash merely because they were dequeued now.
Untimed offline/fixture rows retain a separate bounded history-seeding path; use explicit source
timestamps and an injected `TimeProvider` for synchronization tests.

WinUI continues pacing its render thread with the compositor heartbeat (`Present(0)`),
or the DXGI frame-ready signal (`Present(1)`) on older systems. For visible rendering,
`IDCompositionDevice::GetFrameStatistics().nextEstimatedFrameTime` supplies the estimated
display timestamp and composition rate. The first estimate anchors a continuous
timeline at that rate; isolated repeated/two-frame estimates cannot freeze and then
jump the histogram or simulation. Actual missed refreshes still advance elapsed time.
Refresh changes and pauses longer than 500 ms establish a fresh anchor. Both scene
time and the histogram cursor use this timeline. Missing compositor statistics fall back to host QPC.
Offline rendering uses the injected clock or an explicit presentation timestamp.
The timing panel identifies the active frame clock and reports display-target intervals.
These are display estimates, not proof that each submitted frame actually reached scanout.
Intel Arc is the global target for all scenes, tests and profiling tools. Selection is
independent of scene and monitor; an available Arc that cannot initialize is an error.
Only machines without Arc fall back to normal hardware creation. Scene changes and
monitor moves do not replace the renderer or restart capture. Monitor queries remain
for HDR/color management. On the development laptop Arc owns the built-in display,
avoiding full-frame transfers from the 4070 for every scene.

F9 records CSV stage timings and metadata. The status panel reports source quality, discarded
audio duration, discontinuities and visual drops. Useful stages are:

| Stage | Meaning |
| --- | --- |
| `capture.packet_ms` | Packet's represented audio duration |
| `capture.queue_ms` | Callback arrival to analyzer dequeue |
| `capture.dropped_audio_ms` | Discarded/missing audio duration per loss operation |
| `analysis.source_age_ms` | Completed source boundary to publication |
| `analysis.total_ms` | Batch CPU/wait time including synchronous subscribers |
| `render.audio_upload_ms` | CPU time submitting current spectrum and dirty history uploads |
| `render.display_interval_ms` | Difference between compositor display-target timestamps |
| `render.raw_display_interval_ms` | Unstabilized compositor estimates; exposes repeated/skipped predictions |
| `render.display_lead_ms` | Estimated next display time minus CPU frame start |
| `gpu.frame_ms` | Delayed GPU elapsed time across frame commands, excluding Present |
| `gpu.fluid_ms` | Fluid work including all simulation steps in that frame |
| `gpu.fluid_pressure_ms` | Pressure divergence, multigrid cycles and projection, accumulated across steps |
| `gpu.fluid_velocity_ms` / `gpu.fluid_material_ms` | Velocity transport/viscosity and pigment/age transport |
| `gpu.scene_ms` / `gpu.output_ms` | Final scene draw and display conversion |
| `render.presented_audio_age_ms` | Frame preparation time minus selected source cursor |
| `render.source_to_submission_ms` | Latest consumed source boundary to CPU draw submission |
| `render.capture_to_submission_ms` | Callback arrival to CPU draw submission; excludes earlier packet age |

Timing scopes are nested; do not add them all together. Mean/p95 describe the latest 256 values;
saved profiles retain individual measurements. Metadata records timestamp quality and discontinuities.
Neither CPU submission nor `Present` completion establishes visible-pixel or speaker arrival time.
The UI labels its source-cursor age "Audio age at frame start"; the historical CSV key remains
`render.presented_audio_age_ms`. It is neither the mean age of the aggregated spectrum nor scanout age.
The DFT window's frequency-dependent response is unchanged. Physical audio/display calibration
remains separate work. Rendering performance comparisons must use 720p
or higher; low-resolution GPU silence tests are correctness checks, not timing benchmarks.

### Positioning validation

`AudioTimingTests` covers nonzero and unaligned driver positions, packet fragmentation,
QPC steps without frame insertion, invalid-position recovery, missing frames and new epochs.
`DisplayTimingTests` checks the actual compositor API and uses the global Arc default at 1280x720
to verify histogram impulse displacement at explicit display times despite irregular CPU
timing, followed by strict zero checks on every RGB channel across 60 final silent frames.
The capture probe additionally writes `.positions.csv` timing metadata with driver frame
positions and QPC stamps; it never saves audio samples.

### Live GPU diagnostics and RenderDoc

The timing panel now includes GPU frame, fluid, pressure, velocity/material, scene and output
mean/p95 timings, plus the current fluid grid, velocity-step count (including startup warmup),
and draw count. GPU stages nest: pressure is inside fluid, and fluid is inside frame. Do not sum
these values. The timestamp intervals can include GPU idle gaps while CPU commands arrive;
they are elapsed command intervals, not shader occupancy measurements.

Eight reusable timestamp-query sets are read on later frames with `DoNotFlush`. If the GPU has
not completed a set, rendering continues without waiting; a saturated pool skips timing samples.
Disjoint timestamps and samples exceeding the bounded event capacity are discarded. The panel
shows skipped/invalid sample counts. Valid results feed the same F9 CSV profiler as CPU timings.
No GPU/CPU clock correlation or physical audio/display synchronization is implied.

RenderDoc captures show named frame, audio upload, simulation, pressure, material, scene and output
events. Individual fluid draws include their pass name and grid dimensions. Shader entry points
and spectrum/histogram resources are also named. Annotation calls run only while a graphics tool
reports an active capture/debugging connection; live GPU timing does not require RenderDoc.

See [MandelFluidv4 measurements](mandelfluidv4-performance.md) for the deterministic 720p profiling
command, solver tradeoffs, and before/after timings.

### Incremental histogram uploads

The CPU and GPU histories use the same physical ring slots. The render thread uploads only
changed rows, including cleared gaps, after consuming analysis events. Adjacent rows are batched;
a wrap needs at most two region updates per stereo channel. Startup and complete history clears
upload the full ring once. Frames without history changes upload no histogram data. The two
small current-spectrum textures still upload every frame, using the selected power mean
(RMS by default).
Direct edits to `AudioHistory.History` must call `InvalidateHistory()` on the render thread before
rendering. Analysis events invalidate their own rows automatically.

`render.audio_upload_ms` measures CPU time for unbinding audio shader resources and submitting
current-spectrum and histogram updates. It excludes GPU completion and physical presentation.
The `HistoryUploadTests.CompareFullAndIncrementalUploadsAt720p` comparison uses identical
deterministic input, a lightweight history-sampling scene, and 1280x720 output. GPU readback bounds
outstanding work outside the CPU timing scopes; frame submission timings are not display cadence
or GPU execution times. It checks identical final pixels and reports histogram payload separately.

Measured on 2026-10-21 in Release on an NVIDIA GeForce RTX 4070 Laptop GPU, with 97 bins,
1000 analysis rows/second and 2002 history slots. Each mode ran three blocks with 16 warmup
frames and 96 measured frames per block; mode order alternated. Logical audio advanced at 60 Hz;
the benchmark rendered offscreen without Present or vsync. All compared final pixels matched.

| Measurement | Full uploads | Incremental uploads |
| --- | ---: | ---: |
| Audio upload CPU median / p95 | 0.7100 / 1.2243 ms | 0.0418 / 0.0891 ms |
| Frame submission CPU median / p95 | 0.8217 / 1.3978 ms | 0.1462 / 0.3196 ms |
| Histogram payload per measured frame | 6,214,208 bytes | 51,733 bytes |

Histogram payload decreased about 120 times. These results isolate transport in the lightweight
test scene; they do not establish an equivalent frame-rate or audio-to-photon improvement in
expensive production scenes. Run the comparison again with:

```powershell
dotnet test AEther.Tests/AEther.Tests.csproj --configuration Release --filter 'FullyQualifiedName~CompareFullAndIncrementalUploadsAt720p' --verbosity normal
```

## Historical profiling notes

The following observations describe earlier implementations and recordings. Their buffering,
locking, defaults and stage interpretations are superseded by the current description above.

Press F9 while running to start recording; play representative music for 15–30 seconds, then press F9 again. CSV and a percentile summary are saved under `profiles` beside the executable. Restarting the session resets the recording. Capture is bounded to 200,000 stage records.

The window's **Analysis age max** is the maximum estimated first-sample age at analysis publication over the last second. Capture timestamps are inferred by subtracting packet duration from callback time. This includes packet accumulation, worker queueing, batch assembly and analysis; it is not an audio-device timestamp or audio-to-photon measurement. **Frame interval** includes presentation pacing, rather than measuring GPU execution.

Interpret the profile:

- `capture.packet_ms`: duration of audio delivered in each callback. Large packets already imply stale early samples before analysis starts.
- `capture.queue_ms`: callback entry to worker dequeue, including copying and scheduling. Growing values suggest backlog or worker starvation.
- `analysis.total_ms`: whole batch CPU/wait cost including subscribers. At 48 kHz and resolution 256 the batch budget is about 3.90 ms; sustained costs above this cannot keep up.
- Decode, waveform features, DFT and split stages explain analysis cost. DFT/split records are per channel, so account for both stereo channels. Waveform features are currently calculated even though the visual session uses separated spectral controls.
- `analysis.subscribers_ms`: synchronous downstream processing, including graphics. Graphics lock wait reveals contention with texture uploads on the render thread.
- Frame audio-state/upload, draw submission and Present wait explain render-side CPU blocking. Draw submission is not GPU execution; Present wait can include pacing and GPU backlog. These measurements do not establish scanout latency.

Stages are nested: do not sum all summary rows. Warmup, pauses and scene changes can affect maxima. Compare median and p95 as well as max.

Additional signal delays are absent from the title metric: a Hann DFT window has frequency-dependent temporal response (roughly 400 ms window at 27.5 Hz, 25 ms at 440 Hz); a 100 ms trailing median takes roughly half a window to follow a sustained step. The transient positive-difference branch does not wait for that median step. History presentation delay now defaults to zero (the original trace used the earlier 40 ms default). Render pacing and display add further delay. These effects overlap and depend on the particular branch; they are not a single fixed sum.

The render thread currently holds the audio lock during texture uploads. Present uses vsync and the swapchain has three buffers. These are candidates for follow-up changes after profiling; neither alone proves the cause of a reported 72 ms analysis age.

## Capture diagnosis, 2026-10-14

The 15.57-second trace `pipeline-20261014-232403-363` contained 249 capture callbacks: 184 carried 60 ms of audio and 65 carried 70 ms. Analysis age was 58.923 ms median / 70.933 ms p95. Worker queue wait was 0.079 ms median / 0.701 ms p95 (19.826 ms p99). Whole analysis batches cost 0.736 ms median / 1.155 ms p95. Graphics lock wait was effectively zero at median and p95; state/uploads cost 0.831 ms median. Present blocked for 15.649 ms median on an approximately 60 Hz render loop.

This strongly identifies capture batching as the dominant routine delay. The sample rate and batch size were absent from this first trace, but callback audio duration divided by batch count implies approximately 0.96 ms per analysis batch, so DSP still consumes substantial budget at this configuration. New profiles include actual sample rate, channels, batch budget, frequency bins and SIMD setting.

[NAudio 2.2.1 WasapiLoopbackCapture](https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Wasapi/WasapiLoopbackCapture.cs) exposes only default/device constructors. Its [WasapiCapture base](https://github.com/naudio/NAudio/blob/v2.2.1/NAudio.Wasapi/WasapiCapture.cs) defaults to a 100 ms requested buffer, sleeps for half the actual allocated buffer duration, then coalesces all available packets into DataAvailable callbacks. This explains the observed large deliveries without implying that WASAPI itself requires such a delay.

Loopback now derives from WasapiCapture, retains its loopback and conversion flags, requests 10 ms, and enables event synchronization on Windows 10 1703 or newer. Older Windows uses the same smaller request with polling. [Microsoft documents event-driven loopback support](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording) from that Windows version onward. The driver/audio engine determines actual timing; 10 ms is a request, not a guaranteed callback interval. Rebuild/restart and repeat the F9 trace to measure the improvement on this device.

## Minimum buffer sweep

The subsequent capture-only probe tested 1, 2, 5 and 10 ms requests, each for three seconds while audio played. All delivered 10 ms packets and approximately 10 ms callback intervals (p95 intervals 10.086–10.111 ms). The minimal metadata handler took 2.4–3.2 microseconds on average; this excludes library copying and analysis costs. The default request is now 1 ms, with event synchronization; on this endpoint the engine grants the same 10 ms cadence without increasing callback overhead.

The former CSCore path, run in a separate process, delivered callbacks at 15.645 ms median / 16.209 ms p95. This historical comparison concerns the configurations measured then, not a universal library ranking. Both used WASAPI. The CSCore adapter has since been removed.

Measure the current NAudio adapter with `dotnet run --project tools/AEther.CaptureProbe -- <output.csv>`. Only packet size/timing metadata is saved. Keep music playing throughout each sweep.

True sub-10-ms shared audio periods require supported engine/driver periods and a different initialization path. [IAudioClient3 period initialization](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient3-initializesharedaudiostream) supports specific period constraints and lists only EVENTCALLBACK as a supported stream flag. [NAudio's newer capture documentation](https://github.com/naudio/NAudio/blob/main/Docs/WasapiRecorder.md) explicitly excludes loopback from its IAudioClient3 low-latency mode. Merely requesting a smaller legacy loopback buffer cannot establish a 1 ms engine cadence. A future exploration could use a separate compatible render client to lower the shared engine period, with measurements; that affects the shared endpoint and is outside this buffer-setting change.
