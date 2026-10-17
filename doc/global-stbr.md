# Global STBR implementation

Implemented 2026-10-18 from [the global STBR plan](global-stbr-plan.md).

## Analysis and event contract

`Analyzer` publishes `SampleEvent<T>.GlobalEvidence` alongside the corresponding
local texture row, including the optional `JointEvidenceAvailable` event. Input
gain is applied once in waveform decoding. The DFT emits linear magnitudes;
`GlobalSplitter` averages them across channels before using the same directional
projection as `Splitter`, with explicit full frequency support. Local texture
channels retain their existing meanings.

| CPU member | Meaning |
|---|---|
| `Background` | Arithmetic mean of the channel-averaged magnitude rectangle |
| `Transient` | Signed current frequency mean minus `Background` |
| `Structure` | Frequency RMS of the current signed global S field |
| `InteractionRms` | RMS residual over the complete rectangle; evolving spectral shape can produce it without noise |
| `CurrentMean` | Derived `Background + Transient` |
| `FrequencySamples`, `TimeSamples` | Actual available causal support, including startup |
| `FrequencyBasisRank`, `TimeBasisRank` | Enabled basis ranks, limited by available support |
| `ResidualDegreesOfFreedom` | `(FrequencySamples-FrequencyBasisRank)*(TimeSamples-TimeBasisRank)` |

These are amplitude controls. Adding structure, transient and background does not
reconstruct a scalar level. `JointEvidence.BackgroundContrast(global)` gives the
signed local `B - global.Background` when the estimates refer to the same row.
`GlobalSplitter.Estimates` exposes the complete signed current global fields to
CPU callers; that memory remains valid until its next update or reset.

Global and local fits use the actual analysis rate and the same accepted rows,
time window and slope settings. Known skipped analysis row numbers reset both
histories; gaps do not become zero observations. Exact zero reaches the fit:
logarithmic DFT output represents zero as negative infinity, and scalar/SIMD
filters clear recursion when their exact sample-ring nonzero count reaches zero.
Quiet nonzero samples are retained without a threshold.

The shared kernel preserves original linear observations alongside bounded
moment histories. A material range decrease after an extreme observation expires
rebuilds from those originals, recovering quiet observations that could not be
represented relative to the earlier range. Ordinary updates allocate no memory
and perform O(K+N) work; exceptional range changes rebuild in O(K*N). The initial
implementation deliberately reuses per-bin mean/tilt histories. At 97 bins and
201 rows with the frequency slope enabled, its four double history arrays use
about 609 KiB per fit, plus small per-bin/per-row arrays. Compact shared global
mean/tilt histories remain a possible optimization.

## Shader contract

`FrameConstants` is now 112 bytes. Its final member is:

```hlsl
float4 GlobalSTBR; // (s, t, b, r): structure, signed transient, background, interaction RMS
```

The renderer copies the newest complete packet into this uniform, independently
of the component maxima used for `Spectrum0/1`. It zeros the live value after
150 ms without consumed audio, following the existing stale capture policy.
There is no extra analyzer normalization or renderer smoothing.

`GlobalHistogram` is a width-one float RGBA texture, one vector per retained audio
row. It shares spectral history wrapping, gap clearing, presentation delay,
interpolation and availability masks. `GlobalSTBRAtAge(seconds)` samples it using
the same history coordinates. Unavailable ages return zero. Under stopped capture,
the advancing presentation clock expires the history without erasing natural
trails or simulation state.

Use the uniform for live passage response. Use historical metadata for an older
source or a local background contrast. A latest uniform cannot reconstruct a
texture assembled from component maxima across several rows.

```hlsl
// Illustrative artistic sensitivities, in gained amplitude units.
float bed = GlobalAmplitudeResponse(GlobalSTBR.b, .01);
float rise = GlobalAmplitudeResponse(GlobalSTBR.g, .02);
float fall = GlobalAmplitudeResponse(-GlobalSTBR.g, .02);
float interaction = GlobalAmplitudeResponse(GlobalSTBR.a, .02);

float4 olderGlobal = GlobalSTBRAtAge(sourceAgeSeconds);
float localBackgroundContrast = localAtSameAge.b - olderGlobal.b;
```

`GlobalAmplitudeResponse(x,h)` bounds positive amplitude and maps zero to zero;
`h` must be positive. Shader sensitivities are rendering choices. Global residual
ratios are not confidence or probability. Apply historical availability masks to
the associated local sample as usual. Existing artistic scenes have no new global
mapping in this implementation.

## Analysis views

Enable `ShowGlobalSTBR` in the existing shader parameter panel:

- **Spectrum:** a live strip along the bottom, four sections from left to right.
- **Histogram:** four horizontal strips along the bottom, using row-aligned history.

The section/strip order is structure (green), signed transient (orange for rise,
cyan for fall), background (blue), interaction RMS (magenta). Black means zero.
Both switches default to off; raw-alpha and local-RGB options remain available.

## Validation

The affected numerical/analyzer/DFT regression selection passed **81 tests**,
including dense QR comparisons of production local and global fits, every slope
combination, clipped frequency edges, startup/saturated fits, bilinear inputs,
ring wrapping, 20,000-row range/rebase streaming, idealized global examples,
stereo magnitude averaging, mono duplication, gain scaling, exact silence,
quiet input, overrange input, discontinuity resets and owned metadata history.
Two existing test fixtures were updated for current defaults: the window oracle
applies the configured input gain, and the settled spectral-contrast fixture
waits for the configured support to expire.

GPU validation passed the signed live/history probe and the existing scene silence
regression for Spectrum, Histogram and SpectrumRoad at 0 and +60 dB (**7 tests**).
The optional analysis strips were enabled for Spectrum and Histogram. Each silence
case excites local and global fields, checks both continuing silent packets and
stopped capture, allows 1200 to 10000 decay frames, then checks every RGB channel
across 60 final frames. A further **4 GPU tests** passed the actual waveform ->
DFT -> global fit -> analysis view path for Spectrum/Histogram at 0 and +60 dB,
including 1200 decay frames and 60 final black frames. Renderer construction also
compiles all scene entry points against the extended common inputs.

A Release measurement of 20,000 default global-fit updates (97 bins, 201 retained
rows, stereo input) measured **22.60 microseconds per row and zero managed bytes**
in the timed loop on this machine. This is kernel timing, not end-to-end capture
or rendering latency, and exceptional range rebuilds were outside that steady
fixture.

Validation used a separate output directory because an existing testhost held
the normal test output. The full unrelated suite and every artistic scene's GPU
decay were not rerun. The pre-existing audio-independent Mandelbench visibility
contract still conflicts with the repository's all-scene silence instruction;
its artistic behavior and dedicated tests were outside this analysis-only change.
Listening-based sensitivity choices and individual artistic shader mappings
remain the next rollout stage.
