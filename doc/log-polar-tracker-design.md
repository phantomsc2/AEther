# Log-polar tracker: first implementation

The active analyzer now uses `LogPolarTracker` in place of the Gaussian STBR
splitter. The older `Splitter` remains available for standalone studies, but its
settings are hidden from the application and do not affect the live analyzer.
Context: https://chatgpt.com/share/6adf55f8-3440-83ed-bd53-e8626534c185

## Model

One independent state per audio channel and frequency bin:

```text
x = [a, v, p, w]
z = exp(a + i*p)
v = a'                   log-amplitude slope, 1/s
w = p'                   carrier-relative angular frequency, rad/s

a' = v;  v' = white noise of intensity qa
p' = w;  w' = white noise of intensity qp
```

This is continuous white-noise acceleration (an integrated Wiener process).
There is no additional position random walk. Estimation uncertainty is retained;
the state is not assumed exactly known. For the actual audio interval h:

```text
F2 = [[1, h], [0, 1]]
Q2(q) = q * [[h^3/3, h^2/2],
             [h^2/2, h]]

F = blockdiag(F2, F2)
Q = blockdiag(Q2(qa), Q2(qp))
```

SWIFT and DFT provide coherent complex observations with the same factor-of-two
normalization as their linear magnitudes. Remove the bin's carrier rotation
using elapsed audio samples, not wall time. DFT exposes its actual quantized
center rather than the requested frequency. The observation is:

```text
y = exp(a + i*p) + complex noise
Cov([noise.real, noise.imag]) = sigma^2 * I
estimated frequency in Hz = actual bin center + w/(2*pi)
```

## Settings and time interpretation

The UI's Log-polar tracker group exposes:

- `AmplitudeTrackingSeconds`: default 0.1 seconds.
- `PhaseTrackingSeconds`: default 0.1 seconds.
- `TrackerNoiseFloor`: default 0.001, Cartesian RMS per real/imaginary component,
  in coherent amplitude units **after input gain**.

The response settings are characteristic dynamic scales, not hard windows or
95% settling times. They are referenced to amplitude 0.1 and Cartesian RMS
0.001. Louder signals track faster with this fixed absolute noise floor.
Increasing the floor reduces trust in measurements. Changing input gain also
changes signal/noise ratio; the floor does not scale automatically with gain.

For each coordinate, with response setting t:

```text
q = h * (0.001/0.1)^2 / t^4
```

The factor h compensates for observation density when tuning the observer's
response in seconds. This is a tuning convention: changing the analysis rate
retunes the continuous process intensity. It does not assert that overlapping
spectrum observations become independent white measurements. h is the actual
batch duration, which can differ from 1/TimeResolution.

## Polar observation update

Transform each complex observation to log magnitude and principal phase.
Propagate the fixed isotropic Cartesian noise into these coordinates using a
first-order approximation, evaluated at the **observed** magnitude:

```text
am = ln(|y|)
pm = arg(y)
vobs = (sigma/|y|)^2
```

With independent amplitude/phase acceleration, this working observation model
separates into two symmetric 2x2 covariance blocks: six scalars per bin. There
is no full 4x4 matrix, cross-bin matrix, or matrix inversion. Predict the blocks
with F2*P*F2' + Q2, then use scalar linear Kalman updates:

```text
a- = a + h*v
p- = p + h*w
ea = am - a-
ep = wrap(pm - p-)

For each predicted covariance block [c00,c01,c11]:
    s  = c00 + vobs
    k0 = c00/s
    k1 = c01/s
    position += k0*error
    velocity += k1*error
```

Use ea for [a,v], ep for [p,w]. Covariance correction uses scalar Joseph form,
with a roundoff guard preserving positive semidefiniteness. The transformed
noise is approximately Gaussian and independent at sufficient SNR, not exactly
Gaussian at low SNR. Anisotropic noise or coupled process noise would require a
different observation covariance.

The original Cartesian EKF linearized exp(a+i*p) at the predicted state. A phase
mismatch could then shrink amplitude; a very small predicted amplitude made
both gains vanish and left derivatives coasting indefinitely. This occurred
even after a strong signal returned. The polar update avoids both failure
mechanisms without phase-jump rejection, frequency snapping, or reset heuristics.

## Acquisition and numerical behavior

Initialize from the first finite nonzero complex observation, with v=w=0.
There is no phase magnitude threshold, outlier rejection, jump detection,
frequency snapping, branch reacquisition, or automatic reset after a phase jump.
Phase remains deliberately experimental and noisy.

Initial amplitude variance includes sigma^2/|y|^2, bounded numerically. Initial
phase variance is pi^2/3, the variance of a uniform principal angle; slope
variances are 1/t^2. Tiny startup magnitudes do not imply arbitrarily large
initial velocity variance.

Numerical safeguards:

- Bound measured and estimated log-amplitude to [-80,40]. The observation update
  no longer exponentiates the prediction or needs a capped amplitude correction.
- Bound sigma/|y| to [1e-12,1e12] before squaring, preventing overflow/underflow
  when forming observation variance. These are numerical bounds, not gates.
- Limit derivative state to +/-1e6 per second, far outside intended musical
  ranges. These are numerical bounds, not a pitch range.
- Reduce phase and carrier by whole turns. Phase is a local lifted coordinate
  with a periodically chosen whole-turn gauge, not an accumulated turn count.
- Nonfinite observations and exact complex zero clear the bin and publish four
  exact zeros. The spectrum backend already owns natural tail decay and exact
  tail expiry; no amplitude threshold is imposed on live observations.
- Existing input discontinuities reset both the backend and tracker, including
  carrier state. An absent packet is not synthesized as a zero observation.

## Diagnostic output and Spectrum

`Analyzer.TrackerStatesAvailable` publishes raw signed `LogPolarState` values,
including observed amplitude and observation count. Its pooled memory is valid
only during the callback. It replaces `Analyzer.JointEvidenceAvailable`.

For now the existing four-slot layout contains fixed display encodings:

```text
squash(x) = 0.5 + 0.5*tanh(x)
R = squash(a/4)                 log amplitude
G = squash(v*amplitudeSeconds)  log-amplitude slope
B = 0.5 + p/(2*pi)             principal carrier-relative phase
A = squash(w*phaseSeconds)     angular frequency offset
```

Zero slope and zero offset appear at 0.5, independent of observed amplitude.
All encodings are bounded in [0,1]. Phase has a seam at the principal-angle
branch cut. There is no amplitude envelope on these diagnostic channels: even
quiet nonzero observations show their state. For the amplitude channel,
squash(a/4) equals sqrt(exp(a))/(1+sqrt(exp(a))); an estimated amplitude of
0.001 produces about 0.031 instead of the earlier doubly attenuated 0.0003.
Exact backend zero still clears the bin and all four outputs.

The existing Spectrum controls can be used without changing shaders:

| Existing toggle | New coordinate |
|---|---|
| ShowS / red | a |
| ShowT / green | a' |
| ShowB / blue | phase |
| ShowR / dim gray | phase' |
| All toggles off / alpha fallback | phase' |

The fourth encoding is duplicated into `MomentaryResidual` for ShowR; it is no
longer a residual. `ResidualRms` is empty. No Gaussian residual degrees of
freedom or S/T/B evidence is invented for these states. ShowSum sums diagnostic
encodings and has no decomposition meaning. Other shaders and renderer-derived
music features still interpret the old slots; their behavior is provisional.
Waveform features and the direct transient detector are unchanged.

`Spectrum.CompareAmplitude` overlays an observed-amplitude white marker on the
inferred-amplitude red fill, using the same mapping for both. The observation
is transported in `SampleEvent.ObservedAmplitude` and the G component of the
second spectrum texture row, from the same analysis tick as the inferred state.
If the white marker leads the red edge, the difference is inside the tracker;
if both are late together, investigate the backend/capture/presentation path.
The active tracker response/floor values are also recorded in the pipeline profile.

A controlled 48 kHz on-center tone step from amplitude 0.02 to 0.2, after three
seconds of steady input, gave these audio-domain 90% step times with response
0.001 s and noise floor 0.000001 (12 bins/octave, SWIFT width 1, imbalance 0):

| Frequency | Observed SWIFT amplitude | Inferred amplitude |
|---|---|---|
| 1 kHz | 14 ms | 14 ms |
| 4 kHz | 4 ms | 4 ms |

This isolates analysis behavior; it does not measure live capture-to-screen delay.
An approximately 200 ms delay above 1 kHz is not explained by these fast settings
alone. Low-frequency SWIFT is intrinsically slower: at 30 Hz its onset reaches
50% around 199 ms and 90% around 461 ms with the same bandwidth configuration.

## Known limitations

- Cold backend transients, beating partials and noise can produce noisy phase
  derivatives. Carrier/sign tests now exercise cold startup on both backends;
  there is no special warmup, acquisition gate or jump detector.
- For observation interval h, residual frequency is ambiguous modulo 1/h Hz.
  Carrier removal removes nominal rotation, not aliasing of residual offsets.
- Below the configured measurement floor, corrections become very weak and the
  constant-velocity model coasts. Derivatives need not quickly return to zero
  in those bins; forcing them to zero would add an explicit damping assumption.
- At low SNR, transformed phase noise becomes nearly uniform and log magnitude
  is biased. The propagated Gaussian variance is a practical approximation.
- Multiple partials can cancel in a bin, causing spikes in both derivatives.
  These states describe the filtered mixture, not an isolated source pitch.
- Overlapping filterbank observations are correlated and may have anisotropic
  noise. This observer is a feature experiment, not calibrated Bayesian confidence.
- Constant log-amplitude slope predicts exponential growth/decay and can overshoot
  steps. Independent bin trackers do not retain STBR's cross-frequency distinction
  between broadband attacks and narrowband sustained structure.

## Verification

Tests cover known exponential amplitude trajectories, phase wraps, multiple
publication rates, real-tone carrier/sign checks for both scalar/SIMD backends,
noise-floor response, extreme finite inputs, invalid input, silence/reset, and
agreement with an independently implemented dense 4x4 log-polar Kalman filter.
Recovery tests reproduce the old Cartesian observer's failures on constant
magnitude with a phase jump and on a long nonzero decay followed by a new attack.
The latter also changes frequency offset and checks both derivatives recover.
Real filterbank tests check centered slopes in sufficiently observable bins.
A GPU pixel test verifies a tracker zero slope renders as a half-width green bar.
Analyzer tests cover stereo packing, raw-state callbacks, gain handling and the
fourth diagnostic plane. The tracker-specific ShaderSilenceTests cases are
disabled for this experimental iteration by explicit user request. Their
implementation drives both real backends through the new tracker and retains
the generous decay period and strict final RGB checks. Existing production-scene
silence regressions remain enabled. Numerical zero handling remains in the
tracker even while its GPU silence regression is disabled.

Rendering performance comparisons, if added, must run at 1280x720 or higher.
No rendering-performance claim is made for this iteration.
