# Latency and profiling

## Current source-timed pipeline (2026-10-20)

The WinUI session prefers the core `ITimedSampleSource` capability. Its borrowed-span callback
adds `AudioPacketTiming`: first-frame position, first-sample monotonic timestamp, quality,
and silent/discontinuity/timestamp-error flags. Existing byte callbacks and NAudio's raw
native packet callback remain available; subscribe to one ingestion path to avoid duplicates.
WASAPI QPC positions are expressed in 100 ns units and are converted once into `Stopwatch`
ticks. Wall-clock `DateTime` values do not drive synchronization.

Analysis retains its sample-count cadence (`sampleRate / floor(sampleRate / TimeResolution)`).
Rows carry their completed source interval `[SampleStartTimestamp, SampleEndTimestamp)`,
timestamp quality and discontinuity generation; `SampleIndex` still identifies the batch start.
Packets can contain many rows, and a row can cross packet boundaries. Arrival/completion times
are separate from represented sample time. Invalid timestamps extrapolate from contiguous
sample history; absent history uses callback time minus packet duration and is marked estimated.
After an untimestamped capture pause, a new arrival anchor is established rather than keeping
resumed packets permanently beyond the live age budget. Trustworthy device timestamps also
identify short source-time gaps even if the device frame counter resumes consecutively.

Capture does not wait for analysis or graphics. Queue overflow drops the **oldest** queued
packet. `BufferCapacity` bounds packet count; `MaxInputAgeMilliseconds` defaults to 50 ms and
also trims old prefixes of oversized live packets between batches. Offline byte-only ingestion
does not use this wall-time age limit. Age is tested at completed analysis boundaries: samples
needed by a fresh tick are retained even when its duration exceeds the age budget. Offline ingestion
remains sample-count based. Known missing samples and stream discontinuities reset
DFT rings/state, statistical fits and waveform filters; they are not concatenated or silently
invented as zeros. Explicitly silent packets override their buffer contents with zeros.

The renderer follows the newest completed source interval at or before its presentation target.
The default added presentation delay is **zero**. Explicit delay applies to all audio fields.
Live spectrum and global fields use a duration-weighted arithmetic mean over the same audio
interval; missing intervals contribute zero and partial intersections receive fractional weight.
Individual historical rows retain their original values. The global transient remains signed;
support counts/ranks describe the last contributing row rather than an averaged statistical fit.

No new packet does not mean a silent frame. The last aggregate can survive up to 50 ms of source
age, then live values clear and history continues aging. A discontinuity cancels retention.
The live handoff holds at most 100 ms of rows plus any explicitly requested presentation reserve;
shader history duration is independent. After stalls only the latest 50 ms can excite current
envelopes/motion. Stale rows cannot create a new flash merely because they were dequeued now.
Untimed offline/fixture rows retain a separate bounded history-seeding path; use explicit source
timestamps and an injected `TimeProvider` for synchronization tests.

F9 records CSV stage timings and metadata. The status panel reports source quality, discarded
audio duration, discontinuities and visual drops. Useful stages are:

| Stage | Meaning |
| --- | --- |
| `capture.packet_ms` | Packet's represented audio duration |
| `capture.queue_ms` | Callback arrival to analyzer dequeue |
| `capture.dropped_audio_ms` | Discarded/missing audio duration per loss operation |
| `analysis.source_age_ms` | Completed source boundary to publication |
| `analysis.total_ms` | Batch CPU/wait time including synchronous subscribers |
| `render.presented_audio_age_ms` | Frame preparation time minus selected source cursor |
| `render.source_to_submission_ms` | Latest consumed source boundary to CPU draw submission |
| `render.capture_to_submission_ms` | Callback arrival to CPU draw submission; excludes earlier packet age |

Timing scopes are nested; do not add them all together. Mean/p95 describe the latest 256 values;
saved profiles retain individual measurements. Metadata records timestamp quality and discontinuities.
Neither CPU submission nor `Present` completion establishes visible-pixel or speaker arrival time.
The DFT window's frequency-dependent response is unchanged. Physical audio/display calibration and
GPU history-upload optimization are separate work. Rendering performance comparisons must use 720p
or higher; low-resolution GPU silence tests are correctness checks, not timing benchmarks.

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
