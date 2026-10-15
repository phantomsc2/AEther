# AEther

## Description

Audio analysis and audio-reactive DirectX visualization on Windows using NAudio.

## Project Layout

- **AEther**: Core library to read and analyze sample data
- **AEther.Benchmarks**: using [BenchmarkDotNet](https://benchmarkdotnet.org/)
- **AEther.NAudio**: audio input with [NAudio](https://github.com/naudio/NAudio)
- **AEther.Tests**: using [NUnit](https://nunit.org/)
- **AEther.WindowsForms**: outputs DirectX visualization with [Windows Forms](https://github.com/dotnet/winforms) and [SharpDX](http://sharpdx.org/)

- **tools/AEther.CaptureProbe**: measures NAudio loopback packet timing
- **tools/AEther.Smoke**: exercises analysis and rendering with synthetic audio

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
