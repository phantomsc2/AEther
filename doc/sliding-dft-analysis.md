# Sliding DFT audit

## Window configuration

`AnalyzerOptions.WindowFunction` selects a `DFTWindow` and defaults to `Hann`. In the Windows Forms settings, choose **DFT > Window function**; the existing settings-change handler restarts the session with the selected window. In code, use e.g. `new AnalyzerOptions { WindowFunction = DFTWindow.FlatTop }`. Direct processor callers can supply the optional fifth constructor argument. Unknown enum values are rejected before analysis starts. The selected window is included in profile metadata.

The choices are Hann, Rectangular, Hamming, Blackman, Nuttall and FlatTop. Hamming and Blackman use their exact rational coefficients; the rounded alternatives and separate Exact entries have been removed. Nuttall uses the correctly spelled name and retains Nuttall4b coefficients. FlatTop adds a distinct amplitude-measurement option: its flatter response reduces off-bin level error at the cost of a much wider peak and nine DFT states. Its five cosine coefficients follow [SciPy's flat-top implementation](https://github.com/scipy/scipy/blob/main/scipy/signal/windows/_windows.py). It uses the existing scalar fallback when it exceeds the SIMD width.

The current implementation is mathematically correct for valid inputs and the default Hann window. The SIMD implementation computes the same filter as `WindowedDFTFilter`, not the unwindowed `DFTFilter`. This audit concerns the current working tree, which already fixes the old window-bin offset and coherent-gain normalization.

## Recurrence and phase

For integer bin k, put a = exp(i 2πk/N). The state is the direct sum over sample ages:

    S[n] = Σ(r=0..N-1) x[n-r] a^r
    S[n] = a S[n-1] + x[n] - x[n-N]

The outgoing term is correct because a^N = 1 in exact arithmetic. The positive exponential is appropriate for sample ages; it is not a sign error. Relative to a conventional negative-exponential DFT over the oldest-to-newest buffer, the conventional coefficient is a*S. Magnitudes agree. Complex phase therefore needs this convention documented if it later drives visuals.

The ring buffer reads outgoing samples before overwriting them. Block processing and single-sample processing advance in the same order. Startup is a zero-padded history.

## Windowing and amplitude

The window array holds frequency-domain convolution coefficients, not time-domain sample weights. `CreateWindow([a0,a1,...])` assigns a0 to the center and (-1)^j*aj/2 to both neighboring bins. The filters use bins M - window.Length/2 + j.

For Hann this gives [-.25,.5,-.25] at bins [M-1,M,M+1], equivalent to the periodic time-domain window:

    w[r] = .5 - .5 cos(2πr/N)
    W[n] = Σ x[n-r] w[r] exp(i 2πMr/N)

Division by N*.5 corrects coherent gain. `DFTProcessor` then doubles the magnitude for a real, isolated, positive-frequency sinusoid. An on-bin sine of amplitude .7 produces .7 after warmup, without phase-dependent magnitude ripple in the normal frequency range.

This is a periodic Hann (denominator N), not the symmetric N-1 variant. Both are legitimate windows, but their reference sums differ. The processor returns log10(amplitude), not decibels; multiply by 20 for dBFS. Its amplitude floor 1e-10 corresponds to -200 dBFS.

## Scalar / SIMD equivalence

Both implementations have the same ring length, bins, rotations, sample difference, window weights, active-lane summation order and gain. The SIMD kernel computes complex products directly from real and imaginary vectors, independently in each lane. SIMD packs the neighboring window components, not time samples. Padding has zero weight and cannot affect output.

The processor defaults to Hann, independently of vector width. If the selected window's components do not fit, it falls back to scalar. Blackman, Nuttall and FlatTop require five, seven and nine components respectively, so they use scalar on this host's four-double vectors.

`DFTAuditTests` uses an independent direct time-domain sum, covering startup, ring boundaries, irregular chunks, DC + tone + noise, frequencies from 27.5 to 23,000 Hz, and all six selectable windows. On this host:

| Check | Result |
| --- | --- |
| Vector width / accelerated | 4 doubles / yes |
| Direct-reference combinations | 30 |
| SIMD-supported combinations | 15 |
| Maximum scalar/SIMD complex difference | 0 in tested cases |
| Maximum normalized direct-reference difference | 2.534240e-15 |
| 10 million constant samples, then 2N zeros | maximum residual 8.418188e-15 |

Tests also cover on-bin sine gain and a 40-band scalar/SIMD processor comparison for every choice, the selected window reaching both analyzer channels, reduced half-bin amplitude error for FlatTop, and one million Hann-windowed sine samples followed by silence. Equality on this host does not promise bitwise equality on every JIT/CPU; numerical tolerance is the portable contract.

## SIMD throughput and window selection (2026-10-15)

A temporary console probe referencing the Release AEther assembly measured the default 65-band Hann processor at 48 kHz, one channel, with parallelism disabled. Runtime: .NET 10.0.13; hardware-accelerated vectors: four doubles. Each pair received a one-second warmup; timings are medians of seven alternating-order rounds of 1,000 calls. These are local throughput measurements, not BenchmarkDotNet results or end-to-end capture/render timings.

| Input block | Scalar per call | SIMD per call | Speedup |
| --- | --- | --- | --- |
| 187 samples | 63.6 us | 47.9 us | 1.33x |
| 1024 samples | 343.8 us | 234.6 us | 1.47x |

SIMD is useful and does not change spectral quality. Its current single-vector design cannot accelerate five-state Blackman or seven-state Nuttall on this host. Multiple vector groups would allow both, at additional computation cost. Other candidates to benchmark are keeping state/coefficient vectors in local variables across each block, using explicit real/imaginary vectors in the hot loop, and vectorizing across filters. Cross-filter packing needs a plan for their different ring lengths. These are proposed experiments, not measured improvements. `Vector.IsHardwareAccelerated` should also be considered when selecting the SIMD path.

Before consolidation, the probe evaluated each finite periodic window's analytic frequency response for N=4096 at 0.001-bin intervals through 64 bins. The first minimum and the maximum response after it give the following historical leakage comparison. The Exact rows now correspond to the Hamming and Blackman choices; the rounded alternatives are no longer exposed. ENBW is computed from the cosine coefficients. The widths are DFT bins (sampleRate/N), not the log-spaced display bands.

| Window | States | Peak sidelobe | First minimum from center | ENBW |
| --- | --- | --- | --- | --- |
| Rectangular | 1 | -13.26 dB | 1 bin | 1.0000 |
| Hann | 3 | -31.47 dB | 2 bins | 1.5000 |
| Hamming | 3 | -42.67 dB | 2 bins | 1.3628 |
| Hamming Exact | 3 | -41.69 dB | 2 bins | 1.3528 |
| Blackman | 5 | -58.11 dB | 3 bins | 1.7268 |
| Blackman Exact | 5 | -68.24 dB | 3 bins | 1.6937 |
| Nuttall | 7 | -93.32 dB | 4 bins | 2.0212 |

All seven original arrays were valid cosine windows. The renamed `NuttallWindow` specifically represents Nuttall4b (continuous first derivative), not the Blackman-Nuttall/Nuttall4c variant returned by SciPy's `nuttall`. See the coefficient table in [Heinzel et al.](https://holometer.fnal.gov/GH_FFT.pdf). "Exact" is a coefficient variant, not a universal quality ranking. In particular, exact Blackman has slower far-sidelobe decay than ordinary Blackman; see [SciPy's Blackman notes](https://docs.scipy.org/doc/scipy/reference/generated/scipy.signal.windows.blackman.html).

Hann remains the general-purpose default. The window option allows Hamming at the same three-state cost, or Blackman/Nuttall for greater sidelobe rejection. Hamming's boundary discontinuity gives slower far-sidelobe decay than Hann. Wider main lobes can hide a nearby weak tone despite lower sidelobes; preserving main-lobe width with Blackman or Nuttall requires longer windows and more latency. Compare windows with two unequal tones over multiple separations, off-bin tones, and attacks, rather than sidelobe level alone. Noise-power comparisons additionally require ENBW normalization; the existing coherent-gain normalization is for sinusoidal amplitude.

## What the filters actually resolve

At eight bands per octave, M = round(1/(2^(1/8)-1)) = 11. Each requested frequency selects N = round(M*sampleRate/f), so its actual center is M*sampleRate/N. The default 65-band domain has a maximum tuning error of 0.325647% at 48 kHz: requested 3838.587219 Hz, actual 3826.086957 Hz, N=138.

Each band has a different time window. Hann suppresses the newest sample and emphasizes history centered approximately N/2 samples ago:

| Frequency | Window at 48 kHz | Approximate window centroid age |
| --- | --- | --- |
| 27.5 Hz | 400 ms | 200 ms |
| 110 Hz | 100 ms | 50 ms |
| 440 Hz | 25 ms | 12.5 ms |
| 7040 Hz | 1.5625 ms | .78125 ms |

Consequently this is a multi-resolution analysis, not a spectrum in which all frequencies describe the same instant. Hann's equivalent noise bandwidth is 1.5 bins, and its first zeros are two bins either side of the center. The nominal Q of 11 becomes approximately 7.33 measured against equivalent noise bandwidth. Adjacent bands overlap. The downstream temporal median adds its own history dependence.

These are real limits on separation and attack timing. Low-frequency Hann output cannot be instantaneous. Keep the separate fast onset detector for immediate light, while using the spectral bank for frequency-specific structure and sustain. Moving geometry directly with raw magnitude also exposes beating from multiple nearby tones even when the filter is correct.

## Remaining boundaries

1. **Long-run numerical stability:** this is the traditional unit-circle recursive form. Floating-point rotations do not exactly satisfy a^N=1, and cancellation leaves tiny residual states. The measured three-and-a-half-minute equivalent input is encouraging, but does not prove an indefinitely stable party-length run. A longer soak, periodic recomputation from the ring, or a guaranteed-stable SDFT formulation would address that separately. Merely damping the rotation changes the filter unless the outgoing term and gain are changed consistently. See [Rick Lyons' derivation of a guaranteed-stable SDFT](https://ftp.dsprelated.com/showarticle/1533.php).
2. **DC/Nyquist:** doubling magnitude is intended for isolated positive-frequency real sinusoids. Near Nyquist, a window can overlap the mirrored negative-frequency component; gain can then depend on phase even for an on-bin real tone. Default frequencies end at 7040 Hz and avoid this boundary at 48 kHz.
3. **Constructor contracts:** direct filter constructors do not validate frequency, rate, resolution or window shape/gain. Invalid inputs can yield N=0; nonempty block processing then cannot make progress. Windows must be odd, nonempty, have a usable center gain, and fit the SIMD lane count when calling its constructor directly. The processor's fallback prevents the lane-size failure during normal operation.
4. **Window ownership:** scalar retains the caller's array, whereas SIMD copies its values. Mutating that array after construction can make them differ. The current processor does not mutate it.

The current evidence does not implicate SIMD in the visible jitter. The more consequential visual issues are analysis timing, contrast interpretation, nearby-tone beating, and mapping instantaneous magnitude onto geometry.
