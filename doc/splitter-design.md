# Gaussian STBR decomposition

The sliding DFT and Gaussian local fit are the single production analysis path.
Exact-median and per-sample sign splitters have been removed; historical versions
remain in git. STBR means sustained spectral structure, broadband temporal
structure, local background, and unexplained residual.

## Settings

- TimeWindowSeconds: causal time extent, default 0.1 seconds.
- FrequencyWindowOctaves: centered frequency extent, default 3 octaves.
- JointTimeSlopeEnabled / JointFrequencySlopeEnabled: both enabled by default.
- TimeResolution: requested publication and fit update rate, default 256 Hz.
- FrequencyResolution: 12 bins per octave.
- Analyzer DFT window: Hann. The existing direct DFTProcessor default is unchanged.
- InputGainDb: gain before all analysis, default 0 dB (unity), range -60 to +60 dB.

There is no mode selector, background-subtraction checkbox, or tracker slew rate.
The fit is joint least squares, not a background-first reduction.

## Flat model

With both slopes disabled:

```text
m[f] = mean_time(A[f,t])
q[t] = mean_frequency(A[f,t])
B = mean_rectangle(A)
S[f] = m[f] - B
T[t] = q[t] - B
R[f,t] = A[f,t] - B - S[f] - T[t]
```

A, B, S, T, R and residual RMS are linear digital amplitude quantities. B owns
the patch mean; S, T and R are centered signed deviations. Background is not an
isolated physical noise source. Available startup rows and truncated frequency
neighborhoods define the fit, without invented observations.

## Output contract

Splitter.OutputStride is four. Published/rendered rows are
[max(S,0), max(T,0), B, A]. Alpha is not R.
All components use the same input-gained amplitude units. Signed estimates and neighborhood residual RMS are available through
Splitter.Estimates and Analyzer.JointEvidenceAvailable. Residual RMS is not the
signed current-cell residual. There are no component display gains; automatic
bounded display normalization is not implemented in this settlement.

Every scene must eventually render black after audio stops. Finite fit history
expires naturally; AEther.Tests/ShaderSilenceTests.cs retains the GPU regression.

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

Statistics remain available from Splitter.Estimates and the existing
Analyzer.JointEvidenceAvailable callback (memory valid only during the callback).
RGB shows [max(S,0), max(T,0), B] without component gains; alpha remains A. No logistic
activation or inter-component share normalization is added.
