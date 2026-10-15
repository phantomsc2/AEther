# AEther

## Description

Audio analysis and audio-reactive DirectX visualization on Windows using NAudio.

## Project Layout

- **AEther**: Core library to read and analyze sample data
- **AEther.Benchmarks**: using [BenchmarkDotNet](https://benchmarkdotnet.org/)
- **AEther.NAudio**: audio input with [NAudio](https://github.com/naudio/NAudio)
- **AEther.Tests**: using [NUnit](https://nunit.org/)
- **AEther.Vortice**: D3D11 fullscreen shaders, audio textures and runtime shader compilation
- **AEther.WindowsForms**: render window, separate settings window and application lifecycle

- **tools/AEther.CaptureProbe**: measures NAudio loopback packet timing
- **tools/AEther.Smoke**: exercises analysis and rendering with synthetic audio

## Running

Install the .NET 10 SDK, then run `dotnet run --project AEther.WindowsForms`.
The app starts windowed, captures the default system audio output and displays Mandelbox.

- **Space**: show or hide settings. Closing settings keeps the render window running.
- **F11**: toggle borderless fullscreen on the selected display.
- **F5**: rebuild the capture/analysis/render session.
- **R**: toggle combined level view in Spectrum and Histogram.
- **I**: toggle Spectrum intensity view.
- **[ / ]**: move the Mandelbox slice.
- **Escape**: close the render window (or hide settings when settings has focus).

Settings select audio input, scene, display and analyzer options. These changes restart
the session. A separate Mandelbox property grid stays visible for every scene. Its
antialiasing, maximum iterations and movement speed apply live without restarting
audio: None uses one sample, Medium (default) uses 2×2, and High uses 4×4. AA averages
linear colors and does not change the distance field or depth. The maximum depth defaults
to HEAD's 10 iterations; any positive integer is accepted. Musical drive controls depth using the
original response, with the original log-distance interpolation and history mapping.
Defaults live in code; settings are not persisted.
Movement speed defaults to 1 (the original speed); 3 triples it and 0 freezes the pose.
Scale is adjustable in the same grid and defaults to -1.5. It changes the signed
scale inside every iteration; the distance bound uses its absolute value.
Speed changes preserve the current position and affect slice, rotation and zoom only.
Depth travel, palette timing and audio-history propagation keep their existing rates.
Histogram shows one second of history per stereo side by default, with now in the center.
Mono is mirrored; for multichannel inputs the first two channels are displayed.

Edit `AEther.Vortice/Shaders/*.hlsl` while running from the repository to reload shaders.
Compilation errors appear in settings and the last working shaders remain active.
Published builds load shaders from the `Shaders` folder beside the executable.
See [renderer notes](doc/vortice-renderer.md) for the pipeline and verification commands.

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
