# Historical evidence profiles

These measurements predate the Gaussian-only STBR settlement. Mode names and
settings below describe the versions measured, not current selectable options.
The profiling tool now measures the production Gaussian path only.

# Evidence-mode quick profile

Date: 2026-10-17. Release .NET 10.0.13, four-double hardware SIMD,
Intel64 Family 6 Model 170 Stepping 4. Kernel thread pinned to CPU 0,
tiered compilation disabled. Deterministic synthetic stereo chords + noise +
broadband onsets; 48 kHz, Hann, 98 bands, 187 samples/block, 51 history rows,
37-bin frequency neighborhood. Block budget: 3.896 ms.

## Historical directional slope update

At the time of this measurement, time-linear S and log-frequency-linear T terms were enabled by default
in LocalJointFit and independently switchable. Both slope models have since been removed.
The following same-process comparison describes that historical implementation:

| Configuration | Stereo median / p95 ms | Evidence median ms |
|---|---:|---:|
| Frozen previous flat Gaussian | 0.1397 / 0.1708 | 0.0092 |
| New implementation, slopes disabled | 0.1421 / 0.1603 | 0.0113 |
| Time slope only | 0.1419 / 0.1551 | 0.0122 |
| Frequency slope only | 0.1439 / 0.1570 | 0.0129 |
| Both directional slopes | **0.1443 / 0.1904** | **0.0142** |

All cases use 768 measured calls after warmup, default supports, and zero per-call
kernel allocation. Raw artifacts: gaussian-slopes.csv and slopes-run.txt.
The data below describes the earlier flat implementation and remains historical.

## Gaussian replacement (previous flat implementation)

The three-model local fit has been replaced by rolling Gaussian means and residual
RMS. Robust-model settings and median profile centering are removed. The table below
is the current same-process comparison; the remaining sections are historical results
from before that replacement.

| Configuration | Stereo median / p95 ms | Evidence median ms |
|---|---:|---:|
| DFT only | 0.120 / 0.136 | ? |
| Exact background subtraction | 0.667 / 0.866 | 0.5313 |
| Rolling Gaussian | **0.129 / 0.169** | **0.0087** |
| Frozen old Gaussian + MAD | 10.506 / 12.356 | 10.3248 |
| Rolling Gaussian at ~516 Hz | 0.102 / 0.119 | 0.0104 |
| Rolling Gaussian, .5-second history | 0.141 / 0.199 | 0.0110 |
| Rolling Gaussian, 196 bins | 0.265 / 0.358 | 0.0169 |

New paths used 768 measured calls each; the expensive frozen baseline used 48.
All measured kernel processing loops allocated zero bytes per call. The fit now
uses zero-mean effects and RMS instead of median-centered effects and MAD, so this
comparison includes those intended semantic changes as well as scheduling changes.
A small unpaced Analyzer replay measured recent mean/p95 total CPU **0.189/0.256 ms**
for rolling Gaussian and **0.695/1.161 ms** for the exact path; these replay windows
include startup and exclude a renderer. The large input queue accepts a finite
backlog and is not a real-time pacing test.

Raw results: gaussian-rolling.csv, replay-gaussian.csv, gaussian-run.txt in the
profiling tool directory. Existing artifacts below are retained as historical context.

## Historical default kernel costs

Times include stereo DFT/readout plus evidence; setup and warmup excluded.
Median/p95 are individual call percentiles across 144 calls (768 for DFT-only).
These are quick Stopwatch measurements on a shared machine.

| Configuration | Median ms | p95 ms |
|---|---:|---:|
| DFT/readout only | 0.130 | 0.164 |
| Exact direct evidence | 0.817 | 1.145 |
| Exact background subtraction | 0.743 | 0.994 |
| Joint Gaussian | 10.691 | 12.487 |
| Joint Laplace, max 8 sweeps | 82.329 | 92.555 |
| Joint Huber, max 8 sweeps | 90.054 | 98.812 |
| Joint Huber, max 2 sweeps | 67.046 | 76.795 |

All kernel loops measured zero steady-state allocated bytes. Laplace averaged
6.34 sweeps, with 30.3% of patches reaching the 8-sweep budget. Huber averaged
4.30 main sweeps, with 0.3% reaching the cap; its two Laplace initialization
sweeps are additional. Reaching the cap does not establish failure to stabilize,
and stopping early is not proof of global optimality.

## Useful tuning options

The shorter follow-up used 24 timed calls after at least 0.7 seconds of audio
warmup and enough rows to fill the evidence history. Tail estimates are rough.

| Configuration | Median / p95 ms | Block budget ms |
|---|---:|---:|
| Huber at 32 Hz, 7 history rows | 13.040 / 14.290 | 31.250 |
| Huber at 64 Hz, 13 history rows | 23.729 / 27.502 | 15.625 |
| Gaussian at ~128 Hz, 27 history rows | 6.323 / 10.004 | 7.813 |
| Huber at ~256 Hz, .05 s and 1 octave | 8.829 / 10.523 | 3.896 |

Reducing the cadence changes both fit frequency and the number of observations
in a fixed-duration rectangle. The 32 Hz Huber case fits its kernel budget here;
it is not proof of end-to-end live performance. Gaussian at 128 Hz has little
headroom and exceeded its budget at p95.

## What is on the table

- **ExactMedian, direct:** independent robust summaries of raw amplitude.
- **ExactMedian, subtract background:** exact rectangular N; independent signed
  time/frequency medians of A-N. Fast current default.
- **LocalJointFit / Gaussian:** joint additive least-squares fit; fastest joint model.
- **LocalJointFit / Laplace:** iterative conditional medians fitting one shared
  B + S[f] + T[t] explanation; robust, but not the old exact median filters.
- **LocalJointFit / Huber:** robust joint fit with capped influence and an initial
  residual-scale estimate; extra initialization and conditional solving cost.
- **PerSampleAverageSign:** experimental per-audio-sample approximate trackers;
  visually noisy in the user's evaluation; separate workload and semantics.

ExactMedian remains the default. Selecting JointResidualModel alone does not
activate joint fitting; EvidenceMode must be LocalJointFit.

### Why Laplace is different

Old exact filters compute N once, then independent S and T medians. Their temporal
history contains deviations computed at each row's arrival. Local Laplace fitting
revisits a whole current raw-amplitude rectangle, repeatedly estimating each effect
after subtracting the others and revising B. Each conditional update is a median,
but the full estimator, background, support interpretation, and output differ.

## Next performance options

1. Use the exact path when full analysis cadence is required.
2. Run joint fitting at a lower cadence, initially ~32 Hz. Existing TimeResolution
   also lowers publication cadence; decoupling fit cadence from publication would
   be a separate implementation change.
3. For Gaussian, share running row/column sums between overlapping patches.
   Residual MAD still needs work even if the fit itself becomes cheap.
4. For robust models, cache/warm-start neighboring or successive fits, reduce
   repeated patch scans, or tile with explicit overlap policies. These alter
   scheduling and potentially finite-budget results and need quality comparisons.
5. Separate expensive residual-scale updates from location updates, accepting
   slower scale refresh. Not implemented.
6. SIMD/parallel fits are additional possibilities, not established speedups.

No production algorithms were changed. Source and raw results are in
[tools/AEther.EvidenceProfile](../tools/AEther.EvidenceProfile/README.md).
Analyzer replay results are retained separately; its large queue accepts a finite
unpaced backlog, so zero drops there is not evidence of real-time processing.
