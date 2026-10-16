# Robust spectral background and directional deviations

## Default decomposition

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
- `EvidenceMode`: `ExactMedian` by default. The experimental
  `PerSampleAverageSign` tracker remains selectable.
- `MedianLength`: exact-mode causal time extent, default 0.2 seconds, rounded to
  an odd number of actual analysis rows and capped at 65,535. Shared by N and S.
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
