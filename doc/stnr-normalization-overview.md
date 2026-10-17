> Historical discussion before the Gaussian-only STBR settlement. See [current design](splitter-design.md) for current names and options.

# Current STN+R decomposition and normalization

The current system estimates **local spectral structure in amplitude units**. It does not yet turn those estimates into consistently bounded controls. For the goal of bounded outputs without sliders, I would keep that fit and add automatic display normalization after it.

The key distinction is between **how much of a component is present**, **how clearly it stands above unexplained variation**, and **how bright it should look**. Those need different scales.

## What we currently estimate

The default is `LocalJointFit`, with both directional slopes enabled. It fits a causal time–frequency neighborhood: roughly 100 ms × 3 octaves, at 12 bins/octave.

Start with the simpler version, without slopes:

$$
A_{f,t}=B+S_f+T_t+R_{f,t}
$$

| Quantity | Meaning | Units / range |
|---|---|---|
| A | Current DFT magnitude | Linear digital amplitude, nonnegative |
| B, exposed as N | Mean amplitude across the local rectangle | Same amplitude units, nonnegative |
| S | Each frequency’s temporal mean minus B | Same units, signed |
| T | Each frame’s frequency mean minus B | Same units, signed |
| R | Cell value left unexplained by the fit | Same units, signed |
| Residual RMS | RMS of residuals across the rectangle | Same units, nonnegative |
| Standardized S/T | Directional effects divided by residual RMS | Dimensionless, unbounded |

For this flat model:

$$
B=\operatorname{mean}_{f,t}A_{f,t}
$$

$$
S_f=\operatorname{mean}_t A_{f,t}-B
$$

$$
T_t=\operatorname{mean}_f A_{f,t}-B
$$

S captures persistent frequency structure, T captures changes shared across frequencies, and N is the local background level.

**N is not an isolated noise source.** A broad sustained chord contributes to it. Likewise, T is broadband temporal structure, which can include smooth volume changes as well as attacks. R contains whatever the model cannot explain; it is not synonymous with noise either.

With the default slopes enabled:

- S can rise or fall linearly through the time window.
- T can tilt linearly across log frequency.
- Their shared frequency × time term is split equally between them.
- The outputs are evaluated at the current time and frequency.

The exposed slopes have units of **amplitude/second** and **amplitude/octave**. Details are in [LocalJointSplitter.cs](../AEther/LocalJointSplitter.cs) and [the design notes](splitter-design.md).

## What “amplitude” means here

Input audio is ordinarily around −1 to 1, relative to digital full scale. The DFT normalization makes a steady matching sinusoid’s magnitude approximately its peak amplitude. That is neither acoustic loudness nor power.

The DFT passes the following to the splitter:

$$
\log_{10}(A)
$$

The splitter immediately converts it back to linear amplitude. The fit therefore happens in **linear amplitude**, not logarithms or decibels. The DFT also floors the magnitude at:

$$
A_{\min}=10^{-10}
$$

A further subtlety: this is a constant-Q-style filter bank with longer windows at low frequencies. Tonal amplitude calibration does not imply equal broadband-noise levels across frequencies, and summing squared bins does not directly give waveform power.

## What normalization currently does

There are two separate paths.

[JointEvidence.cs](../AEther/JointEvidence.cs) exposes:

$$
z_S=\frac{S}{\sigma_R},
\qquad
z_T=\frac{T}{\sigma_R}
$$

These are effect sizes, not probabilities or confidence scores. A tiny clean tone can have a huge score. Zero residual with a nonzero effect produces infinity.

[EvidenceOutput.cs](../AEther/EvidenceOutput.cs) sends this to rendering, using the default gains:

$$
RGB=
\left[
10\max(S,0),\quad
10\max(T,0),\quad
10B
\right]
$$

$$
\alpha=A
$$

Those gains are configurable, and the outputs are **not bounded**. The standardized scores do not drive this RGB path.

Also, the displayed channels do not form a partition: clipping negative S/T removes the reconstruction identity. RGB need not sum to A, and alpha is raw amplitude, not R.

## How I would get bounded outputs without sliders

Use positive component amplitudes:

$$
x_S=\max(S,0),
\qquad
x_T=\max(T,0),
\qquad
x_N=B
$$

Then give each component an automatically estimated reference scale and apply:

$$
y_i=\frac{x_i}{x_i+q_i}
$$

This has useful, explicit behavior:

| Component amplitude | Bounded output |
|---|---:|
| Zero | 0 |
| Reference scale | 0.5 |
| Three times reference | 0.75 |
| Nine times reference | 0.9 |

The function is easy; **estimating the reference scale is the actual normalization problem**.

My starting design would be:

- Estimate a recent upper percentile of each component during meaningful audio activity.
- Pool across frequency and both stereo channels initially, preserving spectral shape and stereo balance.
- Let the reference rise quickly and fall slowly, avoiding long saturation after a loud entrance.
- Give references a fixed minimum and prevent them from chasing silence downward.
- Preserve an absolute activity envelope so numerical residue or device noise cannot become full brightness.

This replaces the three gain sliders with automatic reference estimates. It still requires internal choices—percentile, adaptation times, and minimum scale—but those can be product defaults.

The main tradeoff is that independent normalization makes each component visually usable even when it is relatively weak. **Equal displayed S and N would mean comparable strength relative to their own recent behavior, not equal amplitude.** Keep the original estimates available for consumers that need amplitude comparisons.

## Alternatives and what they mean

| Approach | What the bounded output means | Main tradeoff |
|---|---|---|
| Fixed amplitude reference + soft compression | Strength relative to digital full scale | Stable interpretation; quiet recordings remain quiet |
| One adaptive reference shared by S/T/N | Relative amplitude under common automatic gain | Preserves component balance; dominant N or S can suppress weaker components |
| Separate adaptive references | Strength relative to each component’s recent activity | Good channel utilization; loses direct amplitude comparison |
| Compress residual-standardized S/T | Structure relative to unexplained variation | Clean tiny signals saturate; N needs another definition |
| Normalize components to sum to one | Relative component shares | Loses overall intensity; unstable near silence; current signed fit is not a physical partition |
| Running percentile rank | How unusual the current value is | Uses the range well; weak noise can appear strong |

You could also change the estimator: robust medians reduce sensitivity to isolated peaks; fitting log magnitude emphasizes relative contrast; a nonnegative source model makes component shares more meaningful. Each changes what S/T/N mean. None by itself solves display normalization.

For the stated goal, my preference is **the existing amplitude fit → separate automatic component references → zero-preserving bounded compression**, with a shared absolute activity envelope to ensure everything fades to black. Residual-standardized scores can remain available as a separate measure of structural clarity.

The implementation was inspected for this explanation; no implementation files were changed or tests run.
