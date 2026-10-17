# Full-volume loopback and one input gain

Status: implemented. The sections below record the input contract, changes and
validation criteria.

## Input contract and default

Recommend source/player volume at 100%, with listening volume adjusted downstream
of loopback capture. AEther assumes the arriving samples already have the intended
source level. Do not infer or compensate for player attenuation, Windows endpoint
volume, or historical loudness. Our USB Audio CODEC measurement found endpoint
volume independent of loopback level; this is an observation for that device/path,
not a guarantee for every device.

Use normalized sample amplitude as the reference: float magnitude 1 is nominal
full scale; signed PCM16 is decoded by division by 32768, giving [-1, 1).
Float samples above nominal full scale remain valid and must not be clipped just
to enforce a display ceiling.

Add AnalyzerOptions.InputGainDb, default 0.0 dB, with multiplier
10^(InputGainDb / 20). Unity is the correct default for preserving calibrated
full-scale input units. It does not promise a particular brightness for every
recording. Use a finite supported range of -60 to +60 dB, enforced by validation
and reflected in the settings slider. This is a manual sensitivity adjustment,
not automatic gain control.

## Peak versus RMS

Gain operates on every decoded sample; neither peak nor RMS controls it.
DFTProcessor.Output already uses twice the normalized complex magnitude, and the
windowed filters divide by window coherent gain. A steady sine aligned to an
analysis bin therefore reports approximately its peak amplitude. Actual alignment
uses the filter's rounded window length, and off-bin tones can read lower.
Keep this normalization; do not introduce another peak/RMS conversion.

For optional diagnostics, label meters explicitly:

- Sample peak dBFS = 20 log10(max absolute sample).
- RMS dBFS = 20 log10(sqrt(channel-mean sample power)).
- A full-scale sine has sample peak 0 dBFS and RMS about -3.01 dBFS.
- Silence reads minus infinity. No sine-referenced RMS offset is added.

There is no universal STB ceiling derived from sample full scale. Spectral
distribution, overlapping windows, local-fit effects and enabled slopes matter.
Keep analysis values in floating point; any bounded color mapping belongs in the
renderer and must preserve zero input as black.

## Implemented changes

1. In AnalyzerOptions.cs, replace SineGain, TransientGain and BackgroundGain with
   InputGainDb and its range validation. Describe 0 dB as normal full-volume input.
2. In Analyzer.cs, snapshot the gain multiplier when constructing the analysis
   session. Multiply each decoded sample in AnalyzeBatch before transient
   detection, waveform features and DFT processing. All downstream consumers must
   observe the same gained signal. Sanitize nonfinite decoded samples to zero
   before multiplication. Apply gain exactly once; do not alter the format reader
   or capture buffer.
3. In EvidenceOutput.cs, remove the options dependency and gain fields. Emit
   [max(S,0), max(T,0), B, A]. Update Splitter construction and comments accordingly.
   Signed evidence, residual RMS and raw spectral amplitude now all refer to the
   same input-gained units. Residual-standardized S/T remain gain invariant when
   residual scale is nonzero.
4. In SettingsWindow.cs, replace the three gain controls/range cases with Input
   gain (dB), range -60 to +60. Follow existing session restart behavior when this
   option changes, so one fit history never mixes different gain settings.
   Include the source-volume recommendation in its description.
5. Remove obsolete gain initializers from SpectrumStabilityTests.cs and
   tools/AEther.Smoke/Program.cs; unity is now inherent to splitter output. Update
   doc/splitter-design.md and relevant output-contract documentation. The current
   settings window constructs options in memory and has no saved-settings
   serialization to migrate. Old RGB gains are not mapped into input gain because
   that would also change alpha, waveform features and power.

## Expected behavior and review criteria

Removing the default RGB gains of 10 reduces rendered STB evidence by a factor of
10 at input gain 0 dB. Alpha A and waveform features retain their prior unity-input
levels. Setting input gain to +20 dB restores that RGB scale but also multiplies A,
waveform amplitudes and residual RMS by 10, and power/time integrals by 100. Thus it
is not an equivalent migration of the old display settings. Review scene exposure
with representative full-volume music; any required scene tuning must be explicit
and must not reintroduce analyzer component gains. Full scale defines units, not
perceptual brightness or a guaranteed maximum brightness.

Regression coverage verifies:

- PCM16 endpoints: -32768 -> -1; 0 -> 0; 32767 -> 32767/32768.
- A settled on-bin sine at input gain 0 dB produces the expected peak-equivalent
  DFT amplitude, including scalar and SIMD paths.
- +6.0206 dB input gain doubles amplitude, S/T/B, A and residual RMS; waveform
  power grows fourfold, while valid residual-standardized scores stay unchanged.
- Identical mono/stereo signals retain channel-mean RMS; gain never sums channels.
- Invalid gain settings are rejected, float overrange is retained, and nonfinite
  samples do not poison analysis state.
- Preserve AEther.Tests/ShaderSilenceTests.cs: exercise silence after active audio
  at unity and maximum supported input gain. Allow at least 1200 frames (20 seconds)
  of natural decay, retaining the 10000-frame upper bound for slow simulations.
  Once black, check every RGB channel across 60 consecutive final frames. Do not
  add a positive visualization floor or adaptation that makes silence glow.

The diagnostic loopback measurements motivate the input contract but are not a
portable device-volume guarantee. A player attenuated internally remains quieter
by design; the user can restore its volume or adjust Input gain manually.
