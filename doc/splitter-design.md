# Fuzzy STN baseline

`Splitter` implements the single-stage enhanced fuzzy masks from Fierro and Välimäki,
[Enhanced Fuzzy Decomposition of Sound Into Sines, Transients, and Noise](https://arxiv.org/abs/2210.14041),
Eqs. (2)-(5), (8), and (23)-(24). This is a causal adaptation to AEther's existing
log-frequency DFT, not the paper's full two-stage STFT/ISTFT algorithm.

## Evidence

DFTProcessor supplies log10 magnitude. Convert it back to linear magnitude A:

```text
A = 10^input
H = temporal_median(A)
V = frequency_median(A)
Rs = H / (H + V)
Rt = 1 - Rs
```

The time median uses only the current and previous frames. Its window is
`MedianLength` seconds (default 0.3), converted to an odd count at the actual analysis
rate. At startup its history contains the first frame, then grows as frames arrive;
there is no lookahead or synthetic zero padding. Thus the first broadband frame has
no prior quiet context and is classified as noise.

The frequency median is centered, with truncated neighborhoods at the edges.
`MedianWidth` is measured in octaves (default 3), since the DFT bins are logarithmically
spaced. These are the only two evidence windows. If H and V are both zero, Rs = Rt = 0.5.
The implementation scales the ratio before division to avoid overflow.

## Classification

The same raised-cosine membership function is used for sines and transients:

```text
f(a) = 0                                          if a <= lower
       sin(pi/2 * (a-lower)/(upper-lower))^2        if lower < a < upper
       1                                          if a >= upper

S = f(Rs)
T = f(Rt)
N = 1 - S - T
```

`FuzzyLower` defaults to 0.7 and `FuzzyUpper` to 0.8, the paper's example single-stage
bounds. Validation requires 0.5 <= lower < upper <= 1, preventing overlapping S/T
masks and negative N. These are evidence ratios, not amplitude thresholds.

## Output

Each bin is packed as linear `[SineGain*S*A, TransientGain*T*A, NoiseGain*N*A, A]`
into the existing RGBA layout. The three class gains default to 3.0 and apply only
after classification. Zero mutes that class; one gives its unscaled baseline.
Magnitude (alpha) is always unscaled. Gains do not change evidence or masks and
do not clip values at one. RGB need not add up to alpha; the partition invariant
holds only before gain (or with all three gains at one). This is spectral
magnitude allocation, not a reconstruction of three time-domain audio signals.
No inverse transform is involved. The same real masks could partition complex
coefficients if a reconstruction path is added later.

There is no floor subtraction, residual discard, adaptive normalization, or
post-smoothing. Noise is the complementary class, not an independent filter.
Medians determine the masks only; they never supply output amplitude. Zero input
therefore produces zero output immediately, even while the time median retains history.

Nonfinite input and overflow during linear conversion are treated as zero. Finite
quiet input is preserved: the existing DFT's numerical floor of 1e-10 magnitude
remains a tiny nonzero output. Values above nominal full scale are preserved.

The renderer receives linear magnitudes. Alpha is also linear; visual feature
extraction squares it directly for its power proxy. The GPU handoff preserves values
above one (within float range). Final display saturation and existing artistic response
envelopes remain rendering behavior, outside the splitter. Spectrum still takes
component maxima across rows received in one render frame, so its peak overview need
not be additive; individual history rows and splitter output are additive.

Old branch-order, independent S/T/N window, jitter, separation-floor, and quantile
settings have been removed. The former SpectralRange normalizer has been removed.

## Verification

Tests cover the raised-cosine transition and plateaus for both S and T, flat-spectrum
noise, sustained ridges, a broad attack settling at the actual analysis rate, immediate
zero output, additive conservation through mixed history including frequency edges,
gain equivariance, invalid inputs and bounds, quiet input, and downstream linear
magnitude interpretation. A steady waveform tone also exercises the real DFT and splitter.
