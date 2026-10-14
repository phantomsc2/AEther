# Four-way moving-filter experiment

Defaults are one second of history, zero history presentation delay, independent windows: sinuoids 0.3 s / 0.3 octaves, transients 0.03 s / 3 octaves, noise 0.1 s / 1 octave. Frequency weighting has been removed. The PropertyGrid exposes `SinuoidLength`, `SinuoidWidth`, `TransientLength`, `TransientWidth`, `NoiseLength`, `NoiseWidth` and `SplitOrder`. Length is seconds; width is octaves.

Input log amplitude is converted to linear power above the absolute separation floor. Subtraction in dB used to make noise consume the full floor-to-bed interval while ridges and attacks received only a dB contrast. This gave blue a floor-dependent advantage. Power differences instead measure excess energy:

```text
P_floor = 10^(SeparationFloorDb / 10)
L = max(10^(raw_dB / 10) - P_floor, 0)
spike(input, filtered) = max(input - filtered, 0)
pass(input, filtered) = filtered
```

`Mt` is a causal temporal median. `Mf` is a centered frequency median on the logarithmic frequency axis. Each branch uses its own time and frequency window. Temporal sample counts are rounded to odd sizes using the actual analysis rate; at 256 Hz the nominal 100 ms window has 27 samples. Frequency edges use truncated neighborhoods without wrapping.

For each branch, use its corresponding Mt/Mf windows. Define Dt(X) = max(X - Mt(X), 0), and Df(X) = max(X - Mf(X), 0).

```text
Time then frequency:
  sinuoid   = Df_sinuoid(Mt_sinuoid(L))
  transient = Mf_transient(Dt_transient(L))
  noise     = Mf_noise(Mt_noise(L))

Frequency then time:
  sinuoid   = Mt_sinuoid(Df_sinuoid(L))
  transient = Dt_transient(Mf_transient(L))
  noise     = Mt_noise(Mf_noise(L))

residual = max(L - sinuoid - transient - noise, 0)
```

Independent windows can overlap or leave uncovered energy; the residual is a discarded remainder, not a fourth independently filtered classifier. Pass branches keep their filtered signal and memory after falls.
Medians are nonlinear, so these orders are not equivalent. Time first is the default because it establishes sustained structure before finding ridges. Frequency first is available for direct comparison with broad attacks. Pass branches use the filtered signal itself, including their short memory after the source falls. The four values are nonnegative descriptors rather than an exact additive audio reconstruction. "Noise" means smooth in both dimensions, not a probabilistic label or a separated instrument.

## Output and residual isolation

After median separation, all three power components use a shared `JitterLength` EMA time constant, default 0.01 seconds. Zero bypasses the post-filter. It uses the actual analysis rate, not the requested resolution:

```text
a = exp(-1 / (actual_analysis_rate * JitterLength))
H_out = a*H_previous + (1-a)*sinuoid
N_out = a*N_previous + (1-a)*noise
T_out = max(transient, a*T_previous)
```

Sinuoid and noise state start at the first separated value. Transients retain instant attack and exponential release. Residual is computed before this stage; post-filter tails do not change separation or leak residual into visuals. Common brightness, alpha and retained visual controls use the post-filtered power. Structural median windows remain independent of this shared 10 ms relaxation.

Red carries sinuoids, green transients, blue noise. The working values are linear power. Residual is calculated and exposed through `Splitter.ResidualDb` as `10*log10(1 + residual/P_floor)` (diagnostic dB above the floor), but is never packed into textures. Alpha holds the combined retained absolute level; `R` in Spectrum and Histogram shows this level in grayscale. It therefore excludes residual as well.

The shared .01/.99 quantile normalization is retained, with 60-second exponentially weighted history, slowly published bounds and a minimum 24 dB span. It learns only from retained levels. Silent input freezes its estimates. All components use the same brightness mapping and physical amplitude shares:

```text
P = sinuoid + transient + noise
retained_dB = 10*log10(P_floor + P)
span = max(24, Q99 - Q01, Q99 - SeparationFloorDb)
brightness = clamp((retained_dB - SeparationFloorDb) / span, 0, 1)
RGB = brightness * sqrt([sinuoid, transient, noise] / P)
alpha = P > 0 ? clamp(1 + retained_dB/100, 0, 1) : 0
```

RGB is zero when P is zero. Fixed normalization uses a 100 dB span. Squared RGB proportions equal the retained component power proportions, even when common brightness saturates. A 20 dB stronger tone or attack contributes approximately 99% of local power; a purely steady broadband passage stays blue. The quantile floor is not subtracted from the noise branch, which would hide a noise-only passage after adaptation. There are no per-component gain trackers, boosts or normalization windows. This remains a structural power decomposition, not calibrated instrument separation; median pass branches can retain energy after input falls.

Raw waveform onset and RMS events remain available from the analyzer for measurement, but the visual session no longer subscribes to them. Otherwise a discarded residual could still trigger lightning, travel or musical-time animation through that bypass. The visual envelopes and `S` clock now derive from retained spectral level and transient channels. Spectral power is an artistic energy proxy because the DFT bands overlap; it is not advertised as calibrated waveform RMS.

## Verification

Tests cover sustained ridges, broad attacks, smooth broadband noise, isolated residuals, falling levels, silence, numerical input, quantile adaptation, both filter orders, and exclusion of residual from the visual energy clock and lightning. The renderer smoke tool sends one second of synthesized chords and broadband attacks through the actual DFT and splitter, then renders the histogram and fractal. The combined retained grayscale view is captured for comparison; no visual displays residual.
