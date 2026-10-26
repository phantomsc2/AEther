# Kalman spectrum preprocessing

The original Gaussian splitter is active again. Both independent Kalman smoothers
run on the same coherent complex spectrum, after input gain. A mode selector
chooses which linear magnitude is passed to `Splitter.ProcessLinear`.
The splitter's original S/T/B/amplitude output, signed momentary residual,
residual RMS, settings, and `Analyzer.JointEvidenceAvailable` callback are restored.
Velocity is internal to the smoother and is not forwarded to the splitter.

## Parameters

The Kalman smoother group contains an enabled flag, mode selector and model noise parameters:

| Setting | Default | Units / interpretation |
|---|---:|---|
| Enabled | true | Off bypasses both filters and feeds observed magnitudes directly to the splitter |
| Splitter input | A: log amplitude | Select A or B for production decomposition |
| Log acceleration intensity | 0.001 | qA, log-amplitude squared / seconds cubed |
| Complex acceleration intensity | 0.00001 | qB, amplitude squared / seconds cubed, per real/imaginary component |
| Measurement noise RMS | 0.001 | sigma, amplitude units per Cartesian component, after input gain |

Increasing q allows faster state changes; increasing sigma reduces measurement
trust. q=0 is allowed and means deterministic motion. There is no response-time
mapping, reference-level normalization, update-rate retuning, velocity damping,
phase estimation, phase rejection, spatial coupling, or adaptive noise fitting.
The two q defaults approximately match the former temporal observer's intensities
at 1 kHz updates and amplitude 0.1, but the q values are now used directly.
Their units differ; equal numerical values do not imply equal smoothing.
The original splitter support settings are separate: 0.2 seconds and 2 octaves.
When disabled, both comparison outputs equal the raw observation and neither
filter runs. The timing panel shows dedicated smoother CPU mean/p95 per channel
for A+B combined (`analysis.smooth_channel_ms`); bypass records zero.

## Mode A: log amplitude

For each bin and channel:

```text
x = [a, v]
a' = v
v' = white acceleration noise, intensity qA

y = log(|observation|)
R = (sigma / |observation|)^2

F = [[1, h], [0, 1]]
Q = qA * [[h^3/3, h^2/2], [h^2/2, h]]
output = exp(a)
```

The transformed observation variance is a first-order approximation to fixed
isotropic Cartesian noise. It is not an exact Gaussian log-noise model at low
SNR. It is evaluated at observed magnitude, so a weak prediction cannot prevent
recovery when strong observations return. No phase information enters this mode.
Exact zero has no logarithm: it clears this bin and outputs zero, without an
amplitude threshold. Natural nonzero backend tails are still observed.

## Mode B: complex resonator

State and measurements remain complex Cartesian values. The known bin-center
rotation belongs to the linear transition; no demodulation, phase extraction,
unwrap, estimated frequency, or magnitude-dependent measurement variance is used.

```text
x = [z, u]                         both complex
omega = 2*pi*actual bin frequency
z' = i*omega*z + u
u' = i*omega*u + white acceleration noise

rotation = exp(i*omega*h)
F = rotation * [[1, h], [0, 1]]
Q = qB * [[h^3/3, h^2/2], [h^2/2, h]]
R = sigma^2
measurement = observed complex resonator value
output = |z|
```

Each covariance entry represents an identity block over real/imaginary parts.
Isotropic noise, a real initial amplitude/velocity cross covariance, and the
shared rotation preserve this structure exactly. Only three real covariance
scalars are needed alongside the two complex states. The backend supplies actual
carrier centers, including finite-DFT quantization. u is the derivative relative
to the known carrier motion; it is not an exported amplitude derivative.
Zero is a valid complex observation: this mode continues its normal corrections
and naturally decays, including possible overshoot before taking absolute value.

Mode B can attenuate detuned oscillations or produce magnitude dips during phase
changes. Its objective is coherent complex-state estimation. Mode A instead
smooths magnitude independently of phase. These differences are intentionally
visible in the comparison.

## Update, initialization and numerical safety

Both modes use ordinary scalar Kalman gains for position and velocity, exact
discretized CWNA covariance prediction, and a Joseph covariance correction.
First observation: position equals the measurement, position variance equals R,
velocity is zero, and velocity variance/cross covariance are zero. This is an
explicit start-at-rest prior, not an unbounded-uncertainty initialization.
Acceleration noise subsequently creates velocity uncertainty. h is actual audio
batch duration, never render-frame or wall-clock duration.

No per-bin cross covariance or dense matrices are stored. Work/storage are O(n),
with no allocations inside `SpectrumSmoother.Process`.

Nonfinite observations clear the corresponding bin. Nonfinite state/output
arithmetic reacquires from the current finite observation. Mode A's propagated
measurement variance is bounded to [1e-300,1e300] only to protect arithmetic;
there are no musical amplitude/slope clamps. Roundoff guards keep local covariance
nonnegative/positive semidefinite. Stream discontinuities reset both smoothers,
both backend state and the splitter's history. Missing packets are not zero data.
Overlapping filterbank measurements remain temporally/spatially correlated; the
noise model is an explicit working approximation, not calibrated confidence.

## Same-input A/B comparison

Enable `CompareSmoothers` in Spectrum's shader properties:

- Left/red: A's output.
- Right/cyan: B's output.
- White marker: raw observed magnitude.

Both sides show the arithmetic mean of the same two audio channels (mono is
mirrored), on the same linear amplitude scale, from the same analysis tick.
This avoids confusing stereo differences with model differences. Input gain
controls their common amplitude scale; no diagnostic squash or normalization
changes the model output. Comparison overrides Spectrum's decomposition toggles
only while enabled. The selected mode still drives the original splitter and
other scenes. Normal Spectrum mode restores the original stereo S/T/B/residual
view and raw fallback (the splitter input, now smoothed).

`SampleEvent` exposes callback-lifetime linear arrays `ObservedAmplitude`,
`LogSmoothedAmplitude`, and `ComplexSmoothedAmplitude`. Renderer diagnostic row 1
contains `[signed residual, observed, A, B]`. Production row 0 remains
`[positive S, positive T, background B, splitter input amplitude]`.

Tests compare mode B with an independent dense four-real-state Kalman filter,
check analytic mode A/B updates, known carrier motion, phase reversal behavior,
noise reduction, log-amplitude motion, invalid/extreme inputs, silence/reset,
stereo/gain handling, restored splitter output/residuals, comparison pixels and
the end-to-end GPU silence regression through both backends and both modes.
