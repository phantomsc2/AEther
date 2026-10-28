# Capture backend / engine-period experiment

Isolated Windows x64 experiment. It does not replace AEther's production capture adapter.
The question is whether a silent low-latency render stream makes any loopback implementation
supply fresh audio more frequently than the 10 ms observed with NAudio.

## Setup and reproduction

Run from the repository root. BASS native libraries are kept under ignored artifacts;
they are not redistributed by this project. The download script verifies the exact archives
used for the recorded experiment. Review upstream licensing before distributing BASS.

```powershell
powershell -File tools/AEther.LoopbackBackendProbe/Prepare-Native.ps1
powershell -File tools/AEther.LoopbackBackendProbe/Run-Matrix.ps1 -Seconds 5
```

One case:

```powershell
dotnet run --project tools/AEther.LoopbackBackendProbe -- miniaudio before 5 default manual
```

Arguments: backend (`naudio`, `cscore`, `bass`, `miniaudio`), mode (`baseline`, `before`,
`after`), measurement seconds, variant, output tag.

- `baseline`: ordinary silent shared playback supplies audio to loopback, with no low-period helper.
- `before`: start the minimum-period silent render helper before initializing capture.
- `after`: initialize/start capture first, then start the helper.
- All cases request 1 ms process timer resolution, restored on exit, so polling is not
  unnecessarily delayed by the default Windows timer resolution.
- The NAudio helper uses the exact selected endpoint and mix format, event sync, Pro Audio
  MMCSS, and `WithLowLatency(required: true)`. A failed minimum-period request fails the case.
- The engine's **actual current period** is read at initialization, helper start, capture
  start, and both measurement boundaries using `IAudioClient3.GetCurrentSharedModeEnginePeriod`.
- One second of warmup precedes recording. The default measurement interval is five seconds.
- Baseline and capture-first cases wait up to ten seconds for the engine to return to the
  tested 10 ms default. They fail explicitly if it has not, rather than mislabelling a 2 ms
  engine as a fresh baseline. Do not run cases concurrently.
- miniaudio is constrained to WASAPI, and its default Console endpoint is checked against
  the selected Multimedia endpoint. Other adapters select the endpoint by exact device ID.
- CSCore variants: `default` (1 ms polling request), `zero` (old adapter's zero-latency request),
  `event` (CSCore's generic capture class with event sync and loopback flags).
- BASS variants: `default` (1 ms buffer/period request, polling), `event`, `request2`
  (buffer=0, period=2 ms).
- miniaudio variants: `default` (1 ms request), `no-src` (disable automatic format conversion),
  `period2` (96 requested frames / two periods). Fixed-sized callback adaptation is disabled.

CSV and JSON outputs go to `artifacts/capture-backends/<tag>/`. Each callback records its
arrival time and frame count without copying samples or running the analyzer. JSON includes
callback sizes, gaps, callback rates, and bursts grouped by gaps below 0.25 ms. A smaller
callback size is **not** evidence of lower latency if several callbacks arrive together.

The BASS implementation calls BASSWASAPI's native C API directly; Bass.NET / ManagedBass
would wrap the same functions. The old repository BASS adapter used ordinary recording,
so it was not reused for this loopback comparison. CSCore uses the same low-level capture
API as the old adapter, with additional scheduling variants.

This probe measures data availability, not source-to-speaker or source-to-photon latency.
It generates silence, so it does not measure waveform age/correlation. See
[recorded findings](Results-2026-10-28.md).
