# SWIFT quality and CPU study

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet run --project tools/AEther.SwiftStudy -c Release -- --quality
$env:DOTNET_TieredCompilation = '0'
dotnet run --project tools/AEther.SwiftStudy -c Release -- --bench --pin
python tools/AEther.SwiftStudy/plot_results.py
```

`--quality` writes deterministic transfer/response results and production STBR traces for
synthetic signals and the first 12 seconds of `TestFiles/test_input.wav`. `--bench` compares
scalar SWIFT, SIMD SWIFT, and existing SIMD Hann, with/without both local fits.
Traces and scenario summaries use the selected frequency in the first channel.
`--pin` pins the measurement thread to logical CPU 0 on Windows.

Run quality, tests, and benchmark sequentially so other checks do not compete with timing.
Plots require NumPy and Matplotlib. Results overwrite the corresponding files under
`doc/swift-study`; quality and benchmark runs save separate machine metadata. The study
uses the production implementations and requires no extra .NET package dependencies.
Regenerate both quality and benchmark data before plotting: archived results use
the removed global-fit schema and workload.

Benchmark cases: stereo 44.1/48 kHz; 12/24 bins per octave over 30–8000 Hz; current 1 kHz
publication blocks (44/48 samples), 187 samples, and 1,024 samples. Warmup lasts at least
200 ms and fills the Hann and STBR histories. Each of five rounds targets about 200 ms,
with 100–25,000 iterations per round selected after warmup. Median/p95/max describe
individual timed calls; the round mean range is recorded separately. Timing includes
Stopwatch overhead and excludes construction, waveform generation, and file writing.
Construction allocation includes temporary tuning arrays and is not retained memory.
There is no renderer in this CPU study and no claim about audio-to-visible-pixel latency.

The report is [doc/swift-study/report.md](../../doc/swift-study/report.md).

To regenerate validation counts in the report, run these before the plot script:

```powershell
dotnet build AEther.WinUI -c Release -p:Platform=x64
dotnet test AEther.Tests -c Release --filter 'TestCategory!=GPU' --logger 'trx;LogFileName=swift-nongpu.trx'
dotnet test AEther.Tests -c Release --no-build --filter 'FullyQualifiedName~SwiftAndStbrOutputEventuallyRendersBlack' --logger 'trx;LogFileName=swift-gpu.trx'
dotnet test AEther.Tests -c Release --no-build --filter 'FullyQualifiedName~EverySceneEventuallyTurnsBlackWithoutMusic' --logger 'trx;LogFileName=swift-existing-gpu.trx'
```

The report script can run without local test result files; it marks those suites as unrecorded.
