# STN+R decomposition

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
`MedianLength` seconds (default 0.2), converted to an odd count at the actual analysis
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

The fuzzy stage first produces linear class magnitudes S0 = S*A, T0 = T*A,
N0 = N*A. A shared bilateral filter then produces S1, T1, N1 and the signed
linear residual R = A - S1 - T1 - N1.

Each bin is packed as five doubles:
`[SineGain*S1, TransientGain*T1, NoiseGain*N1, A, R]`.
`Splitter.OutputStride` defines this layout. R and A are never gained.
At unity gains S1 + T1 + N1 + R = A; with other gains the identity applies
before those gains. Sine and transient gains default to 3.0; noise gain defaults
to 1.0. Gains apply after bilateral smoothing. Zero mutes that class; one gives its unscaled baseline.
Magnitude (alpha) is always unscaled. Gains do not change evidence or masks and
do not clip values at one. RGB plus residual reconstruct alpha only before gain (or with all three gains at one). This is spectral
magnitude allocation, not a reconstruction of three time-domain audio signals.
No inverse transform is involved. The same real masks could partition complex
coefficients if a reconstruction path is added later.

There is no floor subtraction, residual discard or adaptive normalization.
Noise membership remains complementary at classification time. The smoothed noise
field is a structural estimate, and R contains the signed mismatch of all three
smoothed fields. Positive R is removed detail; negative R is amplitude introduced
by averaging. R must not be clamped or interpreted as nonnegative magnitude.
Exact zero input anchors all components and R to zero even with active history.

Nonfinite input and overflow during linear conversion are treated as zero. Finite
quiet input is preserved: the existing DFT's numerical floor of 1e-10 magnitude
remains a tiny nonzero output. Values above nominal full scale are preserved.

The renderer receives linear magnitudes. Alpha is also linear; visual feature
extraction squares it directly for its power proxy. The GPU handoff preserves values
above one (within float range). Final display saturation and existing artistic response
envelopes remain rendering behavior, outside the splitter. Spectrum still takes
component maxima across rows received in one render frame, so its peak overview need
not be additive; individual history rows and splitter output reconstruct before gains when signed R is included.

Old branch-order, independent S/T/N window, jitter, separation-floor, and quantile
settings have been removed. The former SpectralRange normalizer has been removed.

## Core bilateral smoothing

All three ungained class fields use the same nonseparable time/frequency bilateral
average. Axis specific S/T/N variants are deferred. Filtering is in the analyzer,
independent of render rate, scene choice, and visualization history duration.
Only current and prior classified rows are used; filtered output never feeds back.
Startup uses available rows and frequency edges truncate and renormalize support.

- `SmoothingAmount` defaults to 0.5, ranges from 0 to 1, and zero bypasses filtering.
- Past time support is amount * MedianLength seconds, rounded up at the actual rate.
- Frequency radius is amount * MedianWidth / 2 octaves, rounded up to bins.
- Gaussian spatial sigma is half the rounded support radius, with a half-sample minimum.
- `SmoothingEdgeDb` defaults to 6 dB. Each class independently compares
  20*log10 of linear class magnitude; the range Gaussian uses this tolerance as sigma.
- The similarity calculation has a 1e-300 floor solely to make log(0) finite.
  This does not add any amplitude or normalize output.

The GPU receives STN and original A in the existing RGBA textures. Separate
`ResidualHistogram0/1` and `ResidualSpectrum0/1` textures carry signed ungained
R in red. Original A continues to drive energy features. The spectrum overview
selects the residual of largest absolute magnitude in each render batch and
therefore is not an additive reconstruction; individual history rows are.
Histogram's ResidualView shows positive detail in red and negative detail in blue.
Legacy four-value renderer input is accepted with zero residual.

## Verification

Tests cover the raised-cosine transition and plateaus for both S and T, flat-spectrum
noise, sustained ridges, a broad attack settling at the actual analysis rate, immediate
zero output, additive conservation through mixed history including frequency edges,
gain equivariance, invalid inputs and bounds, quiet input, and downstream linear
magnitude interpretation. A steady waveform tone also exercises the real DFT and splitter.
