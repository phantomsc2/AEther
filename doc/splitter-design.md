# Gaussian STBR decomposition

The live Analyzer uses this original splitter after
[Kalman magnitude preprocessing](kalman-smoothing.md). Its time/frequency support
settings and `Analyzer.JointEvidenceAvailable` callback are active. The splitter
receives the selected smoother's linear amplitude; velocity is not forwarded.

Spectrum analysis (SWIFT by default, optionally DFT) feeds the Gaussian local fit.
Exact-median and per-sample sign splitters have been removed; historical versions
remain in git. STBR means sustained spectral structure, broadband temporal
structure, local background, and unexplained residual.

## Settings

- TimeWindowSeconds: causal time extent, default 0.2 seconds.
- FrequencyWindowOctaves: centered frequency extent, default 2 octaves.
- TimeResolution: requested publication and fit update rate, default 1000 Hz.
- FrequencyResolution: 12 bins per octave.
- Analyzer DFT window: Hann. The existing direct DFTProcessor default is unchanged.
- InputGainDb: gain before all analysis, default +15 dB, range -60 to +60 dB.

There is no mode selector, background-subtraction checkbox, or tracker slew rate.
The fit is joint least squares, not a background-first reduction.
Each channel publishes its local fit only. There is no separate global fit,
global level rebase, or global evidence side channel.

## Flat model

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

### Rolling sufficient statistics and residual RMS

No rectangles are rebuilt. For each input row, maintain:

- Per-frequency temporal means and centered second moments, replacing the
  outgoing observation in the raw-history ring.
- Current frequency-neighborhood means, using one shared sliding frequency sum.
- Temporal means/centered moments of each neighborhood mean, with a second ring
  holding those incoming row means.
- Shared frequency sums of column means and column centered moments.

The residual variance follows the Gaussian ANOVA identity:

```text
v = mean_frequency(var_time(A[f,t])) - var_time(q[t])
scale = ResidualRms = sqrt(max(0, v))
zS = S / scale
zT = T / scale
```

Variances use population divisors, not degrees-of-freedom correction. Under the
independent Gaussian working model this is the fitted residual RMS/MLE scale;
real overlapping DFT spectra are correlated. Scores are signed, unbounded effect
sizes, not confidence intervals or classification probabilities.

The steady update costs O(bins + historyRows), independent of frequency-neighborhood
area: rolling moments replace expired observations, and frequency-window sums share
work between adjacent bins. One frequency history and its neighborhood mean history
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
removed. ResidualDegreesOfFreedom is (frequencySamples-1)*(timeSamples-1), describing
the complete-rectangle additive model; it is not a correction for DFT correlation.
Single-row or single-frequency rectangles have no residual degrees of freedom.
Zero effect / zero scale returns zero; nonzero effect / zero scale returns signed
infinity. HasResidualScale makes that case explicit to classifier consumers.

Statistics remain available from Splitter.Estimates and the existing
Analyzer.JointEvidenceAvailable callback (memory valid only during the callback).
RGB shows [max(S,0), max(T,0), B] without component gains; alpha remains A. No logistic
activation or inter-component share normalization is added.
