# Sliding DFT audit

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

Both implementations have the same ring length, bins, rotations, sample difference, window weights, active-lane summation order and gain. `ComplexVector` performs the ordinary complex product independently in each lane. SIMD packs the neighboring window components, not time samples. Padding has zero weight and cannot affect output.

The processor always selects Hann, independently of vector width. If three window components do not fit, it falls back to scalar. Blackman and Nuttall require five and seven components respectively, so they use scalar on this host's four-double vectors.

`DFTAuditTests` uses an independent direct time-domain sum, covering startup, ring boundaries, irregular chunks, DC + tone + noise, frequencies from 27.5 to 23,000 Hz, and rectangular/Hann/Hamming/Blackman/Nuttall windows. On this host:

| Check | Result |
| --- | --- |
| Vector width / accelerated | 4 doubles / yes |
| Direct-reference combinations | 25 |
| SIMD-supported combinations | 15 |
| Maximum scalar/SIMD complex difference | 0 in tested cases |
| Maximum normalized direct-reference difference | 2.002086e-15 |
| 10 million constant samples, then 2N zeros | maximum residual 8.418188e-15 |

Existing tests also cover on-bin sine gain, a 40-band scalar/SIMD processor comparison and one million sine samples followed by silence. Equality on this host does not promise bitwise equality on every JIT/CPU; numerical tolerance is the portable contract.

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
