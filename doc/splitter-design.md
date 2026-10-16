# Independent robust spectral evidence

The splitter emits evidence directly in linear amplitude. Time, frequency, and
joint time-frequency estimates may all be strong at the same bin. There are no
competing masks, membership ratios, probability normalization, or additive source
constraint. The former signed residual has been replaced by raw amplitude.

## Evidence

DFTProcessor supplies log10 magnitude. Convert it to linear A, treating nonfinite
input and conversion overflow as zero. Estimate:

```text
S = median of A across causal time history at this frequency
T = median of A across the current centered frequency neighborhood
N = median of A across the full time-frequency rectangle
Raw = A
```

These are robust location estimates. An isolated large sample cannot dominate a
median. S/T/N retain their historic channel names for rendering, but they mean
time, frequency, and 2D evidence rather than exclusive sine/transient/noise sources.
In particular, N is not a noise probability, and T can respond to sustained broad
spectral content. A broadband onset and an established chord can both retain
evidence without either being attenuated by a ratio against the other.

Each median is the lower 0.5 quantile for an even population. Time uses only
available past and current rows. Frequency support truncates at domain boundaries;
there is no synthetic padding. The 2D median uses every sample in its rectangle,
not a composition of one-dimensional medians. All estimates read original amplitude,
never filtered feedback. No Gaussian distribution assumption is needed.

## Controls

- `MedianLength`: causal time window in seconds (default 0.2), converted to an
  odd count at the actual analysis rate, bounded to 65,535 samples.
- `MedianWidth`: frequency window in octaves (default 3), converted to an odd
  bin count and clipped to the domain. The 2D estimate shares both extents.
- `SineGain`, `TransientGain`, `NoiseGain`: independent display gains applied
  after estimation (defaults 3, 3, 1). They do not affect evidence or raw amplitude.

Fuzzy thresholds and the extra bilateral smoothing controls have been removed.
There is no post-classification bilateral stage because there is no classification
stage. MAD, distribution uncertainty, and weighted quantiles are future extensions.

## Output and rendering

`Splitter.OutputStride` is four. Each bin is packed as:

```text
[SineGain*S, TransientGain*T, NoiseGain*N, Raw]
```

This maps directly to RGBA spectrum and history textures. RGB are overlapping
amplitude evidence, and alpha is raw magnitude. Their sum need not equal alpha,
even at unity gains. A constant field at unity gains appears equally in all four
channels. There is no subtraction residual and no separate residual texture.
Histogram's CombinedLevel shows raw amplitude; its RGB view shows the three estimates.
FilteredHistogram0/1 remain compatibility aliases for Histogram0/1.

Energy features use raw amplitude, so overlapping evidence is not counted repeatedly
as input energy. Scene response envelopes and display saturation remain rendering
behavior. Original finite quiet input is preserved without adaptive normalization.
The DFT's tiny numerical floor remains tiny. Time-dependent evidence may outlast
zero input briefly, but becomes exactly zero once silence fills its finite history.
Every scene must eventually render black after active audio or capture stopping.

## Verification

Regressions cover simultaneous chord and broadband onset evidence, isolated outlier
rejection, independent gain scaling, startup and frequency boundaries, lower medians,
nonseparable 2D estimation, quiet and invalid input, actual-rate finite history decay,
raw-amplitude energy extraction, and the existing GPU silence regression across scenes.
