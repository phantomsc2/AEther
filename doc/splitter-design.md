# Local Gaussian spectral evidence

## Default configuration

- EvidenceMode: LocalJointFit, the rolling Gaussian model with both directional slopes.
- MedianLength: 0.1 seconds (100 ms), displayed as Time window (seconds).
- MedianWidth: 3 octaves, displayed as Frequency window (octaves).
- DFT window: Blackman, including direct DFTProcessor callers that omit a window.
- FrequencyResolution: 12 bins/octave; TimeResolution: 256 requested updates/second.
- S/T/N display gains: 10 each.

Local Gaussian is listed first in settings. Exact median is retained as a legacy
alternative; per-sample rolling sign tracking is experimental. Their specialized
controls are grouped under Alternative evidence modes. Changing their controls
does not affect local Gaussian fitting. The API property names and enum numeric
values remain unchanged.

See Rolling Gaussian local joint fit below for the current fitting formulas.

## Alternative exact-median decomposition

The exact splitter works in linear amplitude. It estimates a rectangular background
first, subtracts it without clipping, and then filters the signed deviations:

```text
A = linear DFT amplitude
N = median_rectangle(A)
D = A - N
S = median_time(D)
T = median_frequency(D)
RGB = [SineGain * max(S, 0), TransientGain * max(T, 0), NoiseGain * N]
Raw = A
```

N uses every raw amplitude in the full causal time-frequency rectangle. It is not
a median of medians. Each frequency bin maintains an exact indexed-heap median;
each new row inserts its entire frequency neighborhood and expires the oldest one.
The current row is included. Startup uses available rows, frequency support is
truncated at domain boundaries, and even populations use the lower median.

Every bin's current deviation is formed before frequency filtering starts. The
time median stores the signed deviation computed when each row arrived; old rows
are not reinterpreted using today's background. Negative values remain in both
median populations and are clamped only when writing RGB. Positive S/T indicate
directional structure above the local background; negative estimates describe
deficits below it. The current RGBA output exposes only their positive projection.

N is a typical amplitude background, not a noise probability or an isolated
physical source. A sufficiently broad and persistent chord can contribute to it.
The background neighborhood determines what is removed from directional evidence.

## Controls

- `SubtractNoiseBackground`: enabled by default. Replaces the former
  `NormalizeEvidence` mode and its noise-coverage thresholds. Disable to estimate
  S and T directly from raw amplitude, retaining N unchanged.
- `EvidenceMode`: `LocalJointFit` by default; legacy `ExactMedian` and experimental
  `PerSampleAverageSign` remain selectable.
- `MedianLength`: causal fit/filter time extent, default 0.1 seconds, rounded to
  an odd number of actual analysis rows and capped at 65,535.
- `MedianWidth`: centered frequency extent, default 3 octaves, rounded to an odd
  number of bins and clipped to the domain. Shared by N and T.
- `SineGain`, `TransientGain`, `NoiseGain`: display gains, default 10 each.
  Applied after estimation and positive projection; never used for subtraction.
- `TimeResolution`: observation/publication rate in exact mode. Only publication
  rate in per-sample mode.

Default frequency resolution is 12 bins per octave. There are no S/T share ratios,
noise-coverage gates, amplitude rescaling of N, or reconstruction constraint.
S and T can overlap; RGB need not sum to A even at unity gains.

## Experimental per-sample mode

The experimental backend packs frequency bins into portable SIMD vectors, shares
one audio-history ring per channel, and processes short 32-sample tiles. Spectra
feed trackers in chronological order without per-sample events or log conversions.
When SIMD is unavailable or disabled, a scalar DFT supplies linear amplitudes.
All supported window functions preserve their component weights and normalization.

N tracks the raw-amplitude background through the average of neighborhood signs
against one common estimate. Once N is updated, S tracks signed A-N when background
subtraction is enabled, or A otherwise. T is the instantaneous frequency median of
the same directional input, evaluated at publication because it has no history.

`SignSlewRate`, default 1 linear amplitude unit per second, determines the step
`SignSlewRate / sampleRate`. The N update averages signs over valid neighbors;
SIMD padding contributes nothing. Steps stop at observations to reduce chatter.
N remains nonnegative; S retains signed state. This is an approximate online
quantile tracker with no exact finite temporal window; MedianLength does not
control its history. It remains experimental following noisy visual results.

## Output and silence

`Splitter.OutputStride` remains four, mapped to RGBA spectrum/history textures.
Alpha remains raw amplitude for energy features and Histogram's CombinedLevel.
No residual texture or signed output channel is added. Directional evidence can
outlast input briefly. Exact filters clear their finite history naturally; sign
trackers slew toward zero. DFT numerical residue remains at its tiny original scale.
Every shader must eventually render black after music, including after active audio.

Processing uses preallocated storage. The new exact path adds two arrays per
channel for background and signed deviations, without per-row allocations.
The incremental rectangle retains its original exact population and complexity.

## Validation status

Release solution compilation succeeds with zero warnings and errors. Tests,
including the existing GPU silence regression in
`AEther.Tests/ShaderSilenceTests.cs`, were not run for this change. Earlier
independent-evidence assertions and throughput measurements describe the prior
behavior; direct-evidence comparisons must disable SubtractNoiseBackground.

## Rolling Gaussian local joint fit

Select **Splitter > EvidenceMode > LocalJointFit**. This now means Gaussian only;
the Laplace/Huber selector, transition, sweep count, and tolerance controls are
removed. LocalJointFit is now the default. The local fit uses the same causal
MedianLength and centered/truncated MedianWidth support at the actual analysis
rate. It includes available startup rows, without invented past observations.

With both directional slope options disabled, each local rectangle uses:

```text
m[f] = mean_time(A[f,t])
q[t] = mean_frequency(A[f,t])
B    = mean_rectangle(A)
S[f] = m[f] - B
T[t] = q[t] - B
R[f,t] = A[f,t] - m[f] - q[t] + B
```

This is the direct unweighted Gaussian least-squares solution with **zero-mean**
profiles. The old median recentering is removed: the fitted cell values are the
same Gaussian projection, but B and the individual signed effects now use the
ordinary mean convention. B is the grand mean, not a robust background median.
The center frequency's S and the current row's T are emitted for each bin.

### Directional slope terms (enabled by default)

Two boolean controls apply only to LocalJointFit:

- JointTimeSlopeEnabled: each frequency's S component may vary linearly through time.
- JointFrequencySlopeEnabled: each frame's T component may vary linearly across log frequency.

There is no learning rate, iteration budget, extrapolation horizon, or new window
extent. The existing causal time and centered frequency supports define the fit.
Both toggles are enabled by default; disabling both restores the flat model above.

Use centered coordinates u and x spanning [-1,1] across available time rows and
log-frequency bins. Domain bins are uniformly spaced in log frequency. Define:

```text
a[f] = time-regression coefficient of A[f,t] against u[t]
d[t] = frequency-regression coefficient of A[f,t] against x[f]
aBar = mean_frequency(a)
dBar = mean_time(d)
c = frequency-regression coefficient of a[f] against x[f]

S[f,t] = m[f] - B + (a[f] - aBar - 0.5*c*x[f])*u[t]
T[f,t] = q[t] - B + (d[t] - dBar - 0.5*c*u[t])*x[f]
```

A disabled slope has zero coefficient, and c is zero unless both slopes are
active. Constant B, static spectral tilt in S, and a frequency-wide time ramp in
T have explicit ownership. The remaining shared bilinear x*u term is split
half to S and half to T. This fixes the ambiguity in assigning that term while
preserving zero frequency-average S and zero time-average T. Their sum is the
direct Gaussian projection onto the combined directional linear model, without
iterative fitting or component competition.

Values are evaluated at the actual current frame (u=1 when multiple rows exist)
and output frequency. Truncated boundary windows use their own centered coordinate,
not an assumption that the query is always the midpoint. In symmetric interior
frequency windows x=0 at the output bin, so a pure T tilt contributes zero there;
it still changes the modeled neighborhood and residual scale. Time slopes affect
current values because the causal query lies at the time-window endpoint.

Residual RMS accounts for the additional fitted directions:

```text
residualVariance = flatResidualVariance
                 - mean(u^2)*var_frequency(a)
                 - mean(x^2)*var_time(d)
                 + c^2*mean(x^2)*mean(u^2)
```

The overlap is restored once to avoid subtracting shared explained variance twice.
Chronological time cross-moments update as the window slides. Frequency slopes
use shared weighted neighborhood sums; row-tilt moments retain their finite time
history. This adds one history ring when frequency slopes are enabled, and remains
O(bins + historyRows) in the ordinary update. Periodic rebasing and exceptional
numerical fallbacks retain the prior behavior. Signed slope histories are not
clipped, including negative spectral tilts.

JointEvidence additionally exposes SineTimeSlope (linear amplitude per second)
and TransientFrequencySlope (linear amplitude per octave). Its basis ranks describe
the active polynomial subspaces. Residual degrees of freedom are
(frequencySamples-frequencyBasisRank)*(timeSamples-timeBasisRank); a dimension with
only one observation drops its slope, and zero residual degrees of freedom gives
zero residual scale. Scores keep the existing explicit zero-scale policy.

### Rolling sufficient statistics and residual RMS

No rectangles are rebuilt. For each input row, maintain:

- Per-frequency temporal means and centered second moments, replacing the
  outgoing observation in the raw-history ring.
- Current frequency-neighborhood means, using one shared sliding frequency sum.
- Temporal means/centered moments of each neighborhood mean, with a second ring
  holding those incoming row means.
- Shared frequency sums of column means and column centered moments.

With slopes disabled, the residual variance follows the Gaussian ANOVA identity:

```text
mean(R?) = mean_frequency(var_time(A[f,t])) - var_time(q[t])
scale = ResidualRms = sqrt(max(0, mean(R?)))
zS = S / scale
zT = T / scale
```

Variances use population divisors, not degrees-of-freedom correction. Under the
independent Gaussian working model this is the fitted residual RMS/MLE scale;
real overlapping DFT spectra are correlated. Scores are signed, unbounded effect
sizes, not confidence intervals or classification probabilities.

The steady update costs O(bins + historyRows), independent of frequency-neighborhood
area: rolling moments replace expired observations, and frequency-window sums share
work between adjacent bins. One frequency history and its neighborhood mean/tilt histories
are rebuilt per row to limit accumulated downdate drift, adding O(historyRows).
Storage is O(bins * historyRows). Processing allocates no memory after construction.

Compensated sums and centered moment updates avoid subtracting squared large
background levels. Roundoff-sized cancellation in the residual variance is treated
as zero; a negative variance exceeding its numerical tolerance triggers a direct
residual calculation for that patch. Exceptional amplitude-range changes rescale
and rebase the shared history before squaring. These exceptional operations can
revisit history; the ordinary update does not. Exact expiry of the final nonzero
observation clears moment state so active input cannot leave permanent lighting.

### Evidence and rendering

JointEvidence now contains signed S/T, B, **ResidualRms**, raw amplitude, and support
dimensions. Its Scale property aliases ResidualRms. ResidualMad and Iterations are
removed. With slopes disabled, ResidualDegreesOfFreedom is (frequencySamples-1)*(timeSamples-1), describing
the complete-rectangle additive model; it is not a correction for DFT correlation.
Single-row or single-frequency rectangles have no residual degrees of freedom.
Zero effect / zero scale returns zero; nonzero effect / zero scale returns signed
infinity. HasResidualScale makes that case explicit to classifier consumers.

Statistics remain available from LocalJointSplitter.Estimates and the existing
Analyzer.JointEvidenceAvailable callback (memory valid only during the callback).
RGB shows [max(S,0), max(T,0), B] with display gains; alpha remains A. No logistic
activation or inter-component share normalization is added. SubtractNoiseBackground
continues to apply to the exact/sign paths, not the joint model.

### Profile and validation status

A same-process Release profile at 48 kHz stereo, 98 bins, 51 history rows, 37-bin
neighborhoods measured DFT + rolling Gaussian at **0.129 ms median / 0.169 ms p95**,
versus **10.506 / 12.356 ms** for the frozen previous Gaussian-plus-MAD implementation.
The evidence portion alone measured **0.0087 ms median**, with zero steady-state
allocated bytes. These are synthetic-workload CPU timings, not live device/GPU
latency guarantees. See [evidence-profile.md](evidence-profile.md) for raw artifacts.

Release solution compilation passes. Numerical regression and GPU silence tests
were not run for this implementation change.

### Directional slope profile

A same-process profile at default support measured stereo DFT + both slope terms
at **0.1443 ms median / 0.1904 ms p95**, versus **0.1397 / 0.1708 ms** for the frozen
previous flat implementation. Evidence alone measured **0.0142 ms**, versus
**0.0092 ms**. All measured paths allocated zero bytes per update. These are
synthetic-workload CPU timings across 768 measured calls each. Numerical and GPU
regression tests were not run for this change. Results are retained in
`tools/AEther.EvidenceProfile/results/gaussian-slopes.csv`.
