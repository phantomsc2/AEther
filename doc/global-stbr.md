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
the associated local sample as usual.

## Artistic mappings

The artistic scenes now consume the shared controls directly. `MusicBed`,
`MusicBody`, `MusicStructure`, `MusicRise`, `MusicFall` and `MusicInteraction`
in `Inputs.hlsl` provide bounded rendering responses. `MusicBody` includes the
current mean so startup response does not wait for the fitted bed to settle.
The sensitivities are .01 for B, .015 for current mean, .035 for S contrast and
.02 for signed T and interaction. These functions preserve zero.

| Path | Global response |
|---|---|
| MandelFluid family | B drives transport, curl and illumination; S strengthens existing pigment and shape evolution; T pulses light and expansion; interaction strengthens vortices and optional mixing |
| MandelFluidv3 | Local S/B supply clouds; global B drives flow/curl and restrained ambient visibility; local attacks and positive global T strobe existing clouds from the dark core, regardless of pigment age or color |
| Mandelbox / comet slice | Historical B supplies a field bed, S strengthens note beams, age-aligned T adds fronts; live controls act near the source |
| FoldFlight / FilamentFlight / SpectrumRoad | B supplies broad illumination and cohort intensity; S/interaction bend filaments, T excites outlines; travel uses integrated musical time |
| FluidFlight / PlaneFlight / FractalEdge | B supplies cloud/body visibility and stirring; S strengthens contour response; T lights existing material; interaction adds turbulent detail |
| EdgeIFS | B supplies roots without requiring local S/T; S changes branch spread and T pulses ink |
| MandelGalaxy / Traces | B contributes to body/exposure; age-aligned global controls strengthen historical sources while local notes preserve frequency identity |
| Mandelbench | Global level lights the static study; S/T modulate exposure. It now participates in the same all-scene silence regression |

Renderer shape and distortion clocks also include global B/S/interaction, and
fluid movement mobility includes the passage bed so it cannot stall solely
because local directional contrasts are small. Existing analytic wall-time
camera/shutter paths in Galaxy, Traces and PlaneFlight remain continuous; their
visible material and effects follow music. Live passage modulation never gates
the entire stored image. Retained dye/trails preserve their own decay, and
historical sources sample global metadata at the corresponding source age.

Fluid scenes also expose **Fluid.PeriodicBoundary** (default off). It wraps all
transported fields and pressure stencils consistently, replaces expanding
outflow with a translating music-driven current, and disables edge sinks and
expansion dilution. Normal pigment decay remains active. This lets the viewer
move through recirculating earlier material without retaining permanent light.
Subnormal half-float pigment loses at least one representable quantum per fixed
step, preventing exponential rounding from preserving a faint floor forever.

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

The measurements above describe the initial infrastructure rollout. Artistic
validation is recorded below. Listening-based sensitivity choices can be adjusted
in the rendering layer without changing the analyzer's amplitude contract.

### Artistic response and periodic transport checks (2026-10-18)

GPU fixtures hold local S/T at zero, supply a uniform local B/raw magnitude of
.025 and compare the same scene with global B disabled versus B=.025. Every
artistic scene gains visible intensity from the global bed. Mean RGB over the
last six sampled frames of a 120-frame fixture, in RGBA8 channel units:

| Scene | Local B only | With global B |
|---|---:|---:|
| MandelFluid | 71.04 | 99.83 |
| MandelFluidv2 | 10.41 | 28.90 |
| MandelFluidv3 (before cloud consolidation) | 1.42 | 4.95 |

The same B-only fixture increases actual velocity RMS in all three MandelFluid
variants, with finite velocity and bounded pigment. These are controlled GPU
comparisons, not perceptual calibration or listening-based tuning.

Periodic and normal projection pass across landscape, portrait, ultrawide and
square grids: residual divergence stays below 1% of the fixture's initial RMS
and relative curl error below 1.2e-6. Seeded pigment crosses the periodic seam
in all three MandelFluid variants, retaining roughly 82% of its initial density
after one second with the test's configured decay. V3's density-weighted residence
age remains aligned. Toggling the boundary live preserves the dye state.

All **44 scene silence fixtures** pass: every scene at 0 and +60 dB, plus the
periodic option at both gains for each of the six fluid scenes. Each fixture
checks silent startup, excites local/global controls, tests both stopped capture
and continuing silent packets, permits 1200 to 10000 decay frames, and requires
every RGB channel to be exactly black across 60 final frames. Simulations remain
alive throughout their decay. Mandelbench participates without an exception.

Focused checks also pass for transient activation of old pigment, empty-space
darkness, reversible spectral deformation, static study geometry, and decay
through the half-float rounding plateau. Local B retains its original exponential
response to preserve stereo contrast; the added passage drive is separate.
Final v3 reruns pass stereo emission (right/left density ratio 9.59), global B
intensity/motion, and periodic silence at both input gains.

V3 was subsequently consolidated around the fluid-cloud reference. Its Comet
group and age-based lighting FadeTime were removed. Lighting now exposes only
Strobe and Ambient, with a small ambient default of .008. Density gradients and
central distance falloff shape each flash; old and recirculated clouds remain
fully responsive to new attacks. Fluid defaults now follow the reference
(Flow .089, HalfLife .145, Background .324, Curl .243, Diffusion 0), with
Geometry.Anisotropy .526. See [mandelfluidv3.md](mandelfluidv3.md).
