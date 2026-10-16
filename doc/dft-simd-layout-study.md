# Sliding DFT and evidence SIMD layout study

Date: 2026-10-17. Scope: AEther's existing finite-window sliding DFT, higher-order cosine windows, current exact median evidence, and proposed per-sample sign trackers.

## Production implementation update

The near-term coefficient SIMD backend is now implemented in `WindowedDFTFilterSIMD`. It uses portable `Vector<double>` groups for every window size, specializes one to three groups with state and coefficients local across the input block, and uses a group-at-a-time vector loop for larger kernels. Tail lanes are excluded from output, and scalar accumulation order and coherent-gain normalization are retained. Processing allocates no per-block scratch storage. `DFTProcessor` selects this backend whenever SIMD is requested and hardware accelerated; rectangular windows retain their existing specialized filter.

The exact median evidence filters and their observation rate are unchanged. Release compilation succeeded with zero warnings and errors. Tests and integrated throughput measurements were not run for this implementation change; the measurements below remain results from the earlier isolated prototypes. References below to the old production fallback describe the pre-change baseline.

## Recommendation

Support both evidence workloads, and choose their scheduling independently of their mathematics.

1. **Near term: extend coefficient SIMD to multiple vectors and keep recurrence state in local variables across a block.** This makes Blackman, Nuttall and FlatTop usable with SIMD without changing their windows, frequency resolution, time response, or the current exact-window filters. It was the best measured DFT-only layout for Nuttall and FlatTop in the primary run.
2. **For the per-sample goal: use cross-bin SIMD with shared audio history and short time tiles.** Compute vector magnitudes directly and fuse the temporal sign updates. Once a tile contains all frequencies, apply joint average-of-signs updates in chronological sample order. Specialized unrolled kernels were strongest for Hann/Blackman; high-order windows benefit from alternatives that limit register pressure.
3. **Keep both scalar packing and hardware-gather options during implementation experiments.** Gather was a secondary tuning factor here, with no universal win. Do not make it the architectural prerequisite.
4. **Keep exact medians as an evidence mode.** It consumes a spectrum at its configured observation rate and can use either DFT backend. A faster transform does not require changing the evidence definition.

The measured per-sample prototype, including temporal and joint sign updates, costs about **0.56`0.73 ms per 187-sample stereo block**, depending on window. The current exact-evidence workload with the coefficient prototype costs about **0.68`0.76 ms**. Both are below the 3.896 ms block duration in these kernel measurements. They exclude capture, decoding, waveform features, renderer handoff, and scheduling; they are not a live-app latency or drop-rate guarantee.

This is the best measured design among the variants below, not proof of a globally optimal implementation. The study adds an isolated executable and this report; it does not switch the production DFT or evidence mode.

## What the existing implementation does

The relevant source is [DFTProcessor.cs](../AEther/DFT/DFTProcessor.cs), [WindowedDFTFilterSIMD.cs](../AEther/DFT/WindowedDFTFilterSIMD.cs), and [WindowedDFTFilter.cs](../AEther/DFT/WindowedDFTFilter.cs).

Each displayed band owns a finite audio history and one or more complex recurrence states. At the new default resolution, M = round(1/(2^(1/12)-1)) = 17. Each band uses N = round(M*48000/f). Each window component j rotates at integer bin M - floor(K/2) + j within that band's own N-sample transform:

```text
S_j[n] = a_j*S_j[n-1] + x[n] - x[n-N]
Z[n]   = sum_j(windowWeight[j]*S_j[n]) / (N*centerWeight)
A[n]   = 2*abs(Z[n])
```

The positive rotation uses sample ages. Preserve this convention and coherent-gain normalization. The outgoing term belongs to the DFT, not the sign tracker. [Jacobsen's SDFT derivation](https://www.dsprelated.com/showarticle/776/Understanding-and-Implementing-the-Sliding-DFT.php).

On this machine, Vector<double> has four lanes. Production currently puts window components in those lanes and falls back to scalar when they do not fit:

| Window | Complex recurrence states | Coefficient vectors needed at width 4 | Current SIMD selection |
|---|---:|---:|---|
| Rectangular | 1 | 1 | Scalar special case |
| Hann/Hamming | 3 | 1 | SIMD |
| Blackman | 5 | 2 | Scalar fallback |
| Nuttall | 7 | 2 | Scalar fallback |
| FlatTop | 9 | 3 | Scalar fallback |

The finite cosine windows are short frequency-domain convolutions. A larger window kernel means more recurrence states; it does not require one SIMD vector to hold the entire kernel. These are neighboring integer bins of the **same N**, not adjacent logarithmically spaced display bands, which have different N. [Smith's frequency-domain window construction](https://www.dsprelated.com/freebooks/SASP/Frequency_Domain_Implementation_Blackman_Harris_Family.html).

## Candidate layouts and loop order

### Multiple coefficient vectors per band

For one band, maintain one, two, or three vectors of real states and imaginary states. Pad unused components with zero window weight. Read the outgoing sample once, broadcast the difference, and reuse it for every component vector.

Keep state and rotation vectors in local variables through the input block, then store them once. This avoids repeatedly reading and writing object fields in the inner recurrence. The prototype combines this scheduling improvement with multiple vectors: its speedup is not attributable solely to changing vector width or lane occupancy.

This is a small conceptual extension of the current implementation and a strong fit for sparse spectrum readout plus exact medians. Its lanes are less useful when every sample needs a magnitude: reducing window components and then making scalar tracker updates adds substantial work in the tested implementation.

### Cross-bin vectors

A lane represents a displayed frequency band. Use separate real/imaginary state planes for each window component:

```text
                         SIMD lanes
                       f0 f1 f2 f3
component 0 real/imag   [.........]
component 1 real/imag   [.........]
...
```

There are 25 four-lane groups for 98 bands. Only the last two lanes are padding. Window accumulation produces four complex band outputs directly, followed by a vector square root and vector sign updates. No horizontal reduction across window lanes is necessary.

Two loop orders were tested:

- **Tap outer:** group -> component -> samples in tile. A recurrence keeps only one complex vector state live through its time loop. Stage the outgoing differences once and reuse them across components. For per-sample output, accumulate weighted complex results into reusable tile scratch.
- **Sample outer:** group -> sample -> components. A generic array-based version repeatedly loads/stores state planes. Specialized unrolled versions retain each component's state locally. This is fast for small kernels, but keeping 7`9 complex vector states live increases register pressure on AVX2. The observed high-order slowdown is consistent with that explanation; no JIT disassembly or hardware spill counters were collected, so spills are not established as its sole cause.

Short time tiles allow both chronological evidence updates and useful locality. The sweep tested 4, 8, 16, 32, 64, and 187 samples. A tile is a scheduling unit, not evidence downsampling: every sample is still observed.

### Shared audio history and scattered reads

The current defaults give 98 bands, delays from 109 to 29,673 samples, and about 4.02 MiB of duplicated audio histories per channel. A shared history needs at least 231.8 KiB; the prototype uses a 32,768-double, 256 KiB power-of-two ring per channel.

A stageable shared ring must be larger than **maximum delay + maximum staged input block**, not merely maximum delay. Otherwise copying a whole incoming block can overwrite an outgoing sample before a later band reads it. The study reserves for blocks up to 1,024 samples; production must split larger blocks internally. Read outgoing samples at (absoluteSampleIndex - N_lane) & mask.

There are about 9.41 million outgoing reads per second in stereo, or 75.3 MB/s of useful double payload before cache-line and instruction overhead. These reads are scattered across bands but advance sequentially within each band's delay stream. One difference vector serves every window component. Gather is not multiplied by the number of window states.

The study compares four scalar loads packed into Vector256<double> against AVX2 GatherVector256 with four 32-bit indices and an 8-byte scale. The official API exposes that overload. [Microsoft documentation](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics.x86.avx2.gathervector256`view=net-10.0).

## Measurement conditions

- Intel Core Ultra 9 185H; 22 logical CPUs visible; AVX2 and FMA supported.
- .NET runtime 10.0.13, SDK 10.0.402, Windows 10.0.26200 version string.
- Release builds, FP64, tiered compilation disabled for repeatability.
- Measurement thread pinned to logical CPU 0, identified by CPUID as performance-core type 0x40. This does not isolate the core from other applications or fix its clock.
- 48 kHz stereo, 98 bands, 12 bins/octave; primary block 187 samples, duration 3.896 ms.
- Deterministic changing input; roughly 37,400 samples of warmup per case. Three timed rounds per case, approximately 60 ms each, with at least 20 calls per round.
- Reported value is the **median of three mean stereo call times**, not a per-block p95. Raw CSV also records the smallest/largest round means. Early unpinned runs showed substantial migration/noise; only the final pinned runs are tabulated here.
- All timed processing paths reported zero managed allocation per call after setup. Construction, history storage and scratch are allocated upfront.

Workloads:

| Name | Included |
|---|---|
| DFT | Recurrence plus one normalized complex spectrum readout per block |
| Exact | DFT, log-magnitude output, current Splitter exact medians and gains at block rate |
| Time | Every-sample magnitude, temporal sign tracker, final complex readout |
| Joint | Time workload plus a centered 3-octave average-of-signs tracker at every sample |

The evidence workloads are cost prototypes: a fixed step of 1/48000 amplitude units per observation, not a calibrated replacement for MedianLength. They omit an additional per-sample frequency-only tracker, renderer publication and full distribution/MAD estimation. All candidate joint measurements include the same joint comparison kernel and valid-edge normalization. Current-API per-sample results include frequent scalar GetOutput/magnitude extraction; those rows expose the cost of using today's API at that cadence, not a fundamental lower bound on coefficient SIMD.

## Results: 187 samples, milliseconds per stereo call

### DFT and normalized complex readout

| Window | Current auto | Multi-vector coefficients | Unrolled bins, FMA, tile 16 | Bins, tap outer, tile 8 |
|---|---:|---:|---:|---:|
| Hann | 0.184 | 0.073 | 0.068 | 0.071 |
| Blackman | 0.258 | 0.093 | 0.093 | 0.093 |
| Nuttall | 0.324 | 0.088 | 0.149 | 0.110 |
| FlatTop | 0.386 | 0.088 | 0.145 | 0.134 |

Hann's best variants are close enough that a small timing difference should not determine the architecture. The coefficient prototype gives the clearest block-rate win for Nuttall and FlatTop; the latter is about 4.4x faster than current auto in this run. The seven- and nine-state cases remain fully SIMD-enabled.

### DFT plus the existing exact filters

| Window | Current auto + exact | Multi-vector + exact | Unrolled bins + exact |
|---|---:|---:|---:|
| Hann | 0.892 | 0.681 | 0.663 |
| Blackman | 0.855 | 0.691 | 0.691 |
| Nuttall | 0.935 | 0.732 | 0.740 |
| FlatTop | 1.073 | 0.755 | 0.833 |

The exact medians dominate much of this workload, so transform-only gains shrink in the combined result. Small differences between the two new backends are not consistent enough across runs to justify removing either evidence mode or hard-coding a backend from this table alone.

### Every-sample magnitude and temporal sign updates

| Window | Current per-sample API | Unrolled bins, no FMA | Unrolled bins, FMA |
|---|---:|---:|---:|
| Hann | 5.367 | 0.120 | 0.124 |
| Blackman | 2.052 | 0.157 | 0.152 |
| Nuttall | 2.459 | 0.251 | 0.203 |
| FlatTop | 2.709 | 0.288 | 0.234 |

Cross-bin output is a particularly good fit here: magnitudes and independent temporal tracker states already occupy corresponding lanes. The large improvement over the current API includes fusion, reduced extraction overhead, and scheduling`not simply SIMD arithmetic in isolation.

### Every-sample temporal and joint evidence

| Window | Unrolled bins, FMA, tile 16 | Bins, tap outer, tile 64 | Same, hardware gather |
|---|---:|---:|---:|
| Hann | 0.557 | 0.633 | 0.628 |
| Blackman | 0.625 | 0.671 | 0.647 |
| Nuttall | 0.686 | 0.691 | 0.682 |
| FlatTop | 0.726 | 0.732 | 0.763 |

Joint neighborhood comparisons become a substantial part of the cost. Gather versus scalar packing varies with window and schedule; there is no broad hardware-gather advantage in this complete workload. FMA sometimes helps the unrolled high-order kernel, but should be treated as a separately validated arithmetic specialization.

### Larger input blocks: 1,024 samples

The block duration is 21.333 ms. These are the same every-sample temporal-plus-joint workloads, not a reduction in evidence cadence:

| Window | Unrolled bins, FMA, tile 16 | Bins, tap outer, tile 64 |
|---|---:|---:|
| Hann | 3.234 | 3.295 |
| Blackman | 3.333 | 3.666 |
| Nuttall | 3.949 | 3.881 |
| FlatTop | 3.868 | 4.489 |

The larger-block run supports the same broad conclusion, while also showing occasional large timing outliers in the raw data. Tiles can stay short inside a large capture block. Larger capture blocks should not be selected just to improve benchmark throughput; they also change delivery latency.

## Numerical and behavioral checks

The isolated harness compares every backend to the scalar transform and compares selected bins to an independent direct time-domain windowed sum. It checks all six windows, irregular 1/19/73/187/1,024-sample chunks, startup, non-multiple-of-four domains, actual shared-ring wrap, observed versus unobserved execution, and retained state after zero input.

The final full-domain check used Hann and FlatTop at all 98 default bands, with three maximum window lengths of nonzero input before checking the direct sum. It recorded:

| Check | Largest observed absolute error |
|---|---:|
| Backend normalized complex output versus scalar | 5.31e-15 |
| Direct sum versus scalar | 1.07e-15 |
| Every-sample linear magnitude versus scalar extraction | 1.08e-14 |
| Individual temporal/joint tracker state versus reference, full-domain run | 0 |

A separate million-sample check at 441.37 Hz covered Hann, Blackman, Nuttall and FlatTop for every backend. Maximum complex disagreement was 1.26e-16; largest retained normalized magnitude after 4,096 zeros was 4.63e-16.

The smaller all-window check showed an aggregate tracker checksum difference of 1.04e-4 despite magnitude differences around 1e-16. Sign decisions are discontinuous at equality, so near-ties can turn tiny arithmetic differences into several fixed steps. The later full-domain check explicitly compares each estimator state, not only a checksum. Production acceptance should use tolerances and defined tie behavior, not bitwise tracker equality.

These checks do not establish indefinite recursive stability. SIMD and FMA do not remove the existing unit-circle SDFT drift question. This study did not run a multi-hour soak or switch to a different transform. It also did not run the GPU suite because production shader/analysis behavior was not changed.

## Proposed implementation boundaries

Expose a spectrum-bank interface with two consumers:

```text
Audio samples -> finite-window DFT bank
                  |-> block-rate linear amplitude -> exact-window evidence
                  |-> sample-time linear amplitude -> approximate sign evidence
Both consumers -> timestamped evidence/history rows -> batched renderer handoff
```

For the sample-time backend:

1. Append a bounded input chunk to the shared ring, reserving enough history to stage it safely.
2. Process a short tile across band groups, with SoA real/imaginary state and rotation planes.
3. Choose an unrolled small-window kernel or a register-bounded component/time kernel. Measure the crossover rather than assuming one fits every order.
4. Compute linear magnitudes in vectors and update temporal trackers in place.
5. After all bands in a tile are available, update joint evidence for each sample in order. Include only valid frequency neighbors and exclude padded lanes from averages.
6. Publish rows at the chosen history/display cadence. Publication frequency does not define the evidence accumulation cadence.

For the exact mode, keep its existing observation interval and complete rectangular medians. It should not pay for per-sample magnitudes or sign trackers. Preserve the public log-magnitude path for compatibility, but add an internal linear-amplitude path so new consumers avoid log10 followed by pow10.

Keep algorithm mode separate from hardware dispatch: scalar/reference fallback, portable coefficient SIMD, and a cross-bin AVX2 specialization can share window definitions and validation. Do not expose CPU-layout choices as musical controls. Current study code requires AVX2/FMA and uses unsafe gather only in the isolated executable; production needs explicit supported-ISA dispatch and safe tail handling.

The study's generated unrolled functions are experimental. A production implementation should keep a readable generic reference and only retain specialized variants that demonstrate a benefit.

## Gates before adopting the sign evidence mode

- Define response in seconds and amplitude scale; a sign tracker's step is not a hard time window.
- Define equality, initialization, nonfinite input, positivity, and any near-target smoothing.
- Demonstrate quiet-input and silence behavior. A constant nonzero step can chatter around a tiny DFT residual and create much larger artificial evidence. Decay/step limiting or an absolute silence policy needs explicit design and the existing GPU regression.
- Confirm the frequency-only evidence cadence and behavior. The cost study measures temporal and joint trackers; it does not settle that third field's update rule.
- Preserve independent evidence: no cross-channel allocation or S+T+N constraint is introduced by the layout.
- Run the existing DFT audit and direct-reference checks on the production backend, add all-domain/window/block parity coverage, then run ShaderSilenceTests.cs after wiring it into analysis.
- Measure the actual WinUI Debug and Release paths, including capture backlog, analysis p95, drops, and render handoff. Kernel averages are insufficient for a realtime acceptance decision.

Further candidates, not established by this study: SIMD across time via unrolling the linear recurrence/prefix scans; hybrid lane packing; float state; AVX-512/ARM paths; alternative stable SDFT formulations; and neighborhood histogram/rank methods. Time unrolling can exploit DFT linearity, but does not independently parallelize the nonlinear sign recurrence. None should replace the measured FP64 paths without a new correctness and throughput comparison.

## Reproduction and artifacts

See [the study README](../tools/AEther.DftStudy/README.md), [primary results](../tools/AEther.DftStudy/results.csv), [large-block results](../tools/AEther.DftStudy/results-large.csv), [primary run log](../tools/AEther.DftStudy/run-final.txt), and [full-domain verification](../tools/AEther.DftStudy/verification-wide.txt). Timing evidence is local to the machine and environment described above.
