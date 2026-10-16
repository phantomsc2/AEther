# DFT layout study executable

This is an isolated research executable. It references AEther's current transform and splitter, but does not replace the production DFT, change evidence mode, or install itself into the solution.

The report is [doc/dft-simd-layout-study.md](../../doc/dft-simd-layout-study.md).

## Requirements

Windows x64, .NET 10 SDK, AVX2 and FMA. The optional thread-pinning switch uses Windows APIs. The benchmark intentionally fixes Vector256<double> width to four lanes; it is not production CPU dispatch code.

## Run

From the repository root in PowerShell:

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet run --project tools/AEther.DftStudy -c Release -- --pin --verify --bench --extended
dotnet run --project tools/AEther.DftStudy -c Release -- --pin --verify --wide --long
dotnet run --project tools/AEther.DftStudy -c Release -- --pin --bench --large
```

- `--verify` checks all six windows on a 19-band domain, irregular chunks, nonzero shared-ring wrap, observed/unobserved execution, and retained DFT state after silence. Complex outputs are compared with scalar and direct-sum references.
- `--wide` changes verification to Hann and FlatTop at the current complete default domain.
- `--long` adds one million input samples per backend/window at 441.37 Hz followed by 4,096 zeros.
- `--bench` compares 187-sample stereo workloads: DFT/readout, DFT plus current exact medians, per-sample temporal evidence, and temporal plus joint average-of-signs evidence.
- `--extended` adds scalar-load/gather and tile-size sweeps.
- `--large` uses 1,024-sample blocks and a smaller candidate set. It omits the exact workload, since sampling its output once per larger block would change the observation rate.
- `--pin` pins only the measurement thread to logical CPU 0 and prints its hybrid core type. It does not isolate that core or control its clock.

The executable writes `results.csv` beside its built assembly. Save that file after each benchmark run; the next run overwrites it. Root-level CSVs and logs here are the captured study artifacts:

- `results.csv`: final 187-sample matrix.
- `results-large.csv`: 1,024-sample matrix.
- `run-final.txt` and `run-large.txt`: machine metadata, checks, and stdout.
- `verification-wide.txt`: full-domain and long-run validation.

Reported times are median/minimum/maximum of three timed-round means in microseconds per **stereo** call. They are not per-call latency percentiles. Process warmup and setup are excluded, and allocation is measured on the processing thread.

## Prototype limitations

`Backends.cs` implements existing scalar/auto references, multiple coefficient vectors per band, and shared-ring cross-bin schedules. `UnrolledSpectrum.cs` contains specialized experimental kernels for 1/3/5/7/9 recurrence states. The shared-ring implementation accepts at most 1,024 samples per call and reserves that entire staging margin; production must chunk larger inputs.

Every-sample evidence uses a fixed sign step for comparative cost measurement. It does not calibrate MedianLength, implement a complete frequency-only evidence mode, or guarantee silence behavior for a production sign tracker. Tiny magnitude differences can change sign decisions near equality; verification checks numerical tolerances rather than bitwise estimator identity. The report describes the remaining gates.

Timing depends on CPU scheduling, clocks, JIT choices and concurrent applications. Keep both raw ranges and metadata when comparing runs.
