# Global level and global STBR: design proposal

Status: numerical/event/GPU infrastructure and optional analysis diagnostics
implemented, 2026-10-18. The original proposal and mathematical discussion below
remain the design rationale. See [implementation and shader contract](global-stbr.md)
for the shipped interfaces, diagnostics, validation and remaining artistic rollout.

## Recommendation

Add a **global STBR view alongside the existing bin-local view**. Use the same
causal time support, but fit the entire frequency domain. Its background is the
originally requested scalar L. Also publish a signed broadband transient and a
sustained spectral contrast measure. The global residual is meaningful too, but
name and use it as an interaction measure; it is not a clean noise estimate.
These controls give shaders more than L alone, with explicit mathematical
meanings.

For this global view, first average **linear spectral magnitudes** across audio
channels. Keep the existing channel-specific local fits for stereo detail. This
is a shared visual control, not waveform downmixing or acoustic loudness.

The suggested shared shader vector is:

```text
GlobalSTBR = (s, t, l, r)

l = global fitted background / window mean
t = current global frequency mean minus l, signed
s = RMS across frequency of the current signed global S field
r = RMS of the global fit's residual (spectral-temporal interaction) over its
    complete rectangle

current global mean amplitude = l + t
```

Only the last identity is an additive scalar level decomposition. In particular,
`s` is a structural contrast measure: adding `s + t + l` does not reconstruct the
mean amplitude. Keep the full global S/T fields available to CPU diagnostics if
needed; their scalar summaries serve a different purpose.

Start with this parallel global view. If only one extra control is wanted, publish
`l` first. An octave-band view is a useful later alternative for broader spectral
structure. A scalar envelope by itself cannot identify all of S, T and B.

## 1. Current implementation and contracts

The relevant implementation is [Splitter.cs](../AEther/Splitter.cs),
[JointEvidence.cs](../AEther/JointEvidence.cs), and
[Analyzer.cs](../AEther/Analyzer.cs). Each audio channel has an independent local
splitter. Input to `Splitter.Process` is log10 magnitude, converted back to
linear amplitude before fitting.

For every output bin, the splitter fits a complete rectangle of retained time
rows and a clipped frequency neighborhood. Background B is its mean. S and T
are signed directional effects; the residual is the unexplained signed field.
`ResidualRms` describes the rectangle, not the current signed residual sample.

The rendered texture remains `[max(S,0), max(T,0), B, A]`. Alpha is raw spectral
amplitude. Clipped RGB does not reconstruct A and is unsuitable for deriving an
additional statistical fit. Use the signed estimates or the raw amplitudes.

Some existing documentation describes older defaults. The current
[AnalyzerOptions.cs](../AEther/AnalyzerOptions.cs) has:

| Setting | Current source default |
|---|---:|
| Requested analysis rate | 1000 Hz |
| Time window | 0.2 s |
| Frequency window | 2 octaves |
| Frequency resolution | 12 bins/octave |
| S time slope | Off |
| T frequency slope | On |
| Input gain | +15 dB |
| Analyzer DFT window | Hann |

The actual update rate is `sampleRate / floor(sampleRate / requestedRate)`.
`Odd(round(window * rate))` determines the retained row count. At 48 kHz with
these defaults, this is 201 rows and nominally 25 frequency bins per interior
local patch. The global fit should share this actual rate and row support.

Existing levels are different quantities:

| Existing value | Meaning |
|---|---|
| `AudioFeatures.Level` | RMS of gained waveform samples |
| `RetainedVisualFeatures.Level` | Spectral power proxy based on alpha; overlapping bins prevent a Parseval interpretation |
| `AudioHistory.Energy` | RMS of raw spectral magnitudes across rendered channels and bins |
| `VisualBreath` | Bounded, smoothed response derived from `AudioHistory.Energy` |
| Proposed `l` | Arithmetic mean of spectral magnitudes over channels, bins and retained rows |

Give the new values distinct names. None is interchangeable with perceptual
loudness, calibrated broadband RMS, or the existing musical movement clock.

## 2. Check of the local directional fit

### 2.1 Coordinates and projections

For one patch, let A be an F-by-N matrix: F frequency bins, N retained time
rows. Frequency is uniformly spaced in log frequency. Define centered
coordinates x and u:

```text
x[f] = (2*f - (F-1)) / (F-1),  f = 0..F-1
u[j] = (2*j - (N-1)) / (N-1),  j = 0..N-1

mean(x) = mean(u) = 0
vx = mean(x^2) = (F+1)/(3*(F-1))
vu = mean(u^2) = (N+1)/(3*(N-1))
```

Drop a coordinate and its slope when disabled or its axis has only one sample.
Use the actual clipped patch coordinates: its output bin need not be x=0.
The causal query is u=1 for N>1.

Let Pf project frequency vectors onto `span(1,x)` and Pt project time vectors
onto `span(1,u)`. With a slope disabled, its span contains only 1. Their ranks
are rf and rt, each 1 or 2. The fitted matrix and residual are:

```text
M = A*Pt + Pf*A - Pf*A*Pt
R = (I-Pf)*A*(I-Pt)
A = M + R
```

Left and right projection commute as operators on the matrix. Consequently,
`H(A)=M` is symmetric and idempotent under the Frobenius inner product. R is
orthogonal to every fitted direction. This proves that M is the unique least
squares fitted matrix, even if a redundant coefficient parameterization is used.

### 2.2 Component ownership

Define means and regression coefficients, with disabled coefficients zero:

```text
m[f] = mean_time(A[f,j])
q[j] = mean_frequency(A[f,j])
B    = mean_rectangle(A)
a[f] = mean_time(A[f,j]*u[j]) / vu
d[j] = mean_frequency(A[f,j]*x[f]) / vx
ab   = mean_frequency(a)
db   = mean_time(d)
c    = mean_frequency(a[f]*x[f]) / vx
     = mean_time(d[j]*u[j]) / vu

S[f,j] = m[f]-B + (a[f]-ab-0.5*c*x[f])*u[j]
T[f,j] = q[j]-B + (d[j]-db-0.5*c*u[j])*x[f]

M = B + S + T
mean_frequency(S[:,j]) = 0 for every j
mean_time(T[f,:]) = 0 for every f
```

Set c=0 unless both slopes exist. These are the formulas implemented by the
splitter. Static spectral tilt belongs to S; a frequency-wide time change belongs
to T. The shared bilinear `x*u` direction is split equally. Without that ownership
rule S and T are individually ambiguous, although their sum remains unique.

The half split is a convention, not a conclusion forced by Gaussian noise. S and
T are not mutually orthogonal when c is nonzero. Their rectangle inner product
is `mean(S*T) = c^2*vx*vu/4`; do not add their squared magnitudes as independent
component powers. The residual is orthogonal to the combined fitted space.

### 2.3 Residual variance and rank

All variances below use population divisors on the available observations:

```text
v0 = mean_frequency(var_time(A[f,:])) - var_time(q)
v  = v0 - vu*var_frequency(a) - vx*var_time(d) + c^2*vu*vx
SSE = F*N*v = sum_rectangle(R^2)

rank(H) = F*rt + N*rf - rf*rt
df      = F*N-rank(H) = (F-rf)*(N-rt)
ResidualRms = sqrt(SSE/(F*N))
```

The restored overlap term is necessary; omitting it subtracts the bilinear
projection twice. This checks the implementation's residual identity and its
`ResidualDegreesOfFreedom` formula. Dimensions of size one, and size two with
their slope enabled, correctly leave no residual degrees of freedom on that axis.

Keep compensated sums, centered moment updates, periodic rebasing, range
rescaling and direct residual fallback. Clamp only roundoff-sized cancellation
to zero; a materially negative variance requires investigation or direct
recalculation. A tiny positive result can also be cancellation, as the current
roundoff policy recognizes.

## 3. What makes the Gaussian statement rigorous

Specify the conditional working model explicitly:

```text
y = vec(A)
y = X*b + e
e ~ Normal(0, sigma^2*I)

negativeLogLikelihood = n/2*log(2*pi*sigma^2)
                      + ||y-X*b||^2/(2*sigma^2)
n = F*N
```

X spans the directional model above. Its fitted values are H*y. For positive
SSE, maximizing this likelihood gives least squares fitted values and
`sigmaML^2 = SSE/n`. Thus the existing residual RMS is the Gaussian MLE scale,
not an unbiased variance estimate. The Gaussian/least-squares equivalence is
described by [NIST](https://www.itl.nist.gov/div898/handbook/pmd/section4/pmd43.htm).

When df>0, `SSE/df` is an unbiased estimator of error variance under this model;
its square root is not an unbiased estimator of sigma. This differs from the
MLE divisor. [NIST's residual standard deviation](https://www.itl.nist.gov/div898/handbook/pmd/section4/pmd431.htm)
uses the degrees-of-freedom divisor. Preserve `ResidualRms` and optionally expose
a separately named corrected variance; do not silently change its definition.

When SSE=0, sigma=0 is a boundary limit: there is no finite likelihood maximum
over sigma>0. With df=0 the fit is saturated and supplies no residual information
for estimating noise. Returning zero RMS remains a computational convention,
not evidence of perfect certainty. Preserve support/rank metadata and the
explicit zero-scale behavior of `JointEvidence`.

There are three limits to the statistical interpretation:

1. **Magnitudes are nonnegative.** Gaussian error is a working approximation
   around a fitted mean. In the ideal circular complex Gaussian noise case,
   magnitude follows a Rice law, reducing to Rayleigh for zero signal; this
   follows from the Gaussian-vector magnitude construction documented by
   [SciPy](https://docs.scipy.org/doc/scipy/reference/generated/scipy.stats.rice.html).
   Actual windowed, overlapping analysis bins need not satisfy that ideal case.
   Exact least squares optimization does not establish Gaussian measurement noise.
2. **Observations overlap.** DFT time windows and frequency responses induce
   correlation and potentially unequal error variances. Even with independent
   model errors, fitted residuals have covariance `sigma^2*(I-H)`, not independent
   entries. Overlapping local patches are also correlated estimators, not
   independent data sets whose likelihoods can simply be multiplied.
3. **Scores are effect sizes.** `S/ResidualRms` and `T/ResidualRms` are not
   Gaussian probabilities, z tests, t tests, or significance levels. Under a
   correctly specified independent Gaussian model, a fitted contrast `w'*y`
   needs standard error `sqrt((SSE/df)*(w'*w))`. With covariance V, its variance
   is `w'*V*w`. Local RMS alone does not provide these quantities.

If calibrated inference becomes a requirement, estimate and validate covariance
from suitable recordings and use GLS, whose objective is
`(y-X*b)'*V^-1*(y-X*b)`. This is a different fit unless V is proportional to I.
[Statsmodels' GLS documentation](https://www.statsmodels.org/stable/generated/statsmodels.regression.linear_model.GLS.html)
describes covariance specification and whitening. Known diagonal unequal
variances lead to inverse-variance WLS, as described by
[NIST](https://www.itl.nist.gov/div898/handbook/pmd/section1/pmd143.htm).

No covariance model or calibrated confidence claim is needed for the proposed
visual controls. Call this an exact directional least squares fit with a
Gaussian working likelihood. Empirical Gaussian adequacy remains unverified.
In particular, the global residual RMS is first a measure of what the selected
additive directional model leaves out. It estimates Gaussian error scale only
if the model is adequate and the independent, equal-variance error assumptions
hold. Residual structure is evidence of model mismatch, as
[NIST's residual guidance](https://www.itl.nist.gov/div898/handbook/pmd/section4/pmd44.htm)
explains; it does not by itself reveal the physical source of that mismatch.

## 4. Option A: one additive global scalar

Let C be the number of input channels, K the total number of frequency bins,
and N the common retained row count. Define:

```text
Y[f,j] = mean_channel(A[channel,f,j])
l = mean_frequency,time(Y)
  = mean_channel,frequency,time(A)

b[channel,k] = B_local[channel,k] - l

A_current[channel,k] = l + b[channel,k]
                     + S_local[channel,k] + T_local[channel,k]
                     + R_current[channel,k]
```

Here R_current is the signed current residual, not `ResidualRms`. This rebases
the intercept and leaves the existing fitted values, S, T, residuals and local
degrees of freedom unchanged. `b` is signed and can be negative in silent bands
while other bands are active. Clip it only for an explicitly artistic mapping.

An unconstrained extra intercept would be unidentifiable:

```text
l -> l+h,  b[channel,k] -> b[channel,k]-h
```

Defining l from the full-domain observations fixes this ambiguity. It is a
specified global reference estimate plus local fits, not a new free parameter
in every patch. Do not subtract one additional degree of freedom from each local
residual.

There is a direct Gaussian reference fit for l. For global Y, fit
`Y[f,j] = l + g[f] + h[j] + e[f,j]`, with `sum(g)=sum(h)=0` and independent,
equal-variance Gaussian working errors. The intercept normal equation gives
`l=mean(Y)`. Adding centered directional terms keeps this result. This reference
fit and the local fits are overlapping views; they do not constitute one joint
independent likelihood.

Do not instead define l as an unweighted mean of local B values. Clipped,
overlapping neighborhoods weight source bins unevenly. For three bins with
three-bin nominal neighborhoods, averaging local B gives weights
`(5/18, 4/9, 5/18)`, rather than `(1/3,1/3,1/3)`. Consequently `mean_k(b[k])`
need not be zero. Imposing that extra constraint would choose a different l.

Also do not subtract a different l[j] from each stored row. That removes a
time-varying broadband contribution from T. For the intercept rebase, subtract
the current scalar uniformly from the entire current rectangle, or simply
derive `b=B-l` after fitting.

This l measures mean amplitude per log-frequency bin. Equal bin weights give
approximately equal weight per octave. It is sensitive to domain choice and
spectral sparsity, although it avoids the direct bin-count growth of a sum.
It is neither a power estimate nor gain invariant: positive input gain g
multiplies l and every signed linear component by g.

## 5. Option B: give the global view its own STBR fit

This is the recommended extension to Option A. Apply the projection in section 2
to Y over **all K bins**, with the same N rows and slope settings as the local
fits. Write its components as Sg, Tg, Bg and Rg:

```text
Y = Bg + Sg + Tg + Rg
Bg = mean_rectangle(Y) = l
q[j] = mean_frequency(Y[:,j])

mean_frequency(Sg[:,j]) = mean_frequency(Rg[:,j]) = 0
mean_frequency(Tg[:,j]) = q[j]-Bg

t = q[now]-Bg
s = sqrt(mean_frequency(Sg[:,now]^2))
r = sqrt(mean_rectangle(Rg^2))
```

The global residual rank is `(K-rf)*(N-rt)`, using K, not the local neighborhood
width. Because this fit uses channel-averaged magnitude, r describes the residual
of that average. It is not the mean of the channel residual RMS values. Averaging
channels may suppress channel-specific variation; local evidence preserves it.

Interpret r as a **model-relative interaction RMS**. With slopes off, the fit
removes the frequency mean and time mean, leaving the doubly centered
frequency-by-time interaction. With the directional slopes on, it also removes
the fitted linear structure on each axis and their shared bilinear term. The
residual is therefore useful for measuring evolving spectral shape or local
texture that the global S/T model does not explain.

For example, let magnitudes be `Y[f,j]=p[f]*g[j]`, a fixed spectral shape p whose
amplitude follows envelope g. With slopes off, the residual is exactly
`(p[f]-mean(p))*(g[j]-mean(g))`, so:

```text
r = populationSD_frequency(p) * populationSD_time(g)
```

An audible coherent swell in one narrow note can therefore have large r. This
is not measurement noise. With slopes on, the model also absorbs the supported
linear trends and their bilinear product, leaving higher-order shape changes.
So r can drive an interaction-sensitive visual detail, turbulence or diagnostic
display, but should not be called noise, confidence, instability or randomness
without further evidence. The fitted residual field is signed; its RMS alone
does not say where or with what sign the interaction occurs.

The scalar t is the constant-frequency part of the current global transient
field. With frequency slope enabled, Tg can also have a centered tilt term.
Optionally publish that signed coefficient, `dg[now]-mean(dg)-0.5*c*u[now]`,
as an additional amplitude control. Its physical slope is that coefficient
times `2 / frequencySpanInOctaves`.

s summarizes both positive and negative deviations in the sustained field.
It measures spectral structure relative to the global background. It does not
measure sustained waveform power or classify an instrument. If a more slowly
varying descriptor is wanted, use `sqrt(mean((m[f]-Bg)^2))` instead and name it
`MeanSpectralContrast`; this excludes the current S time-slope contribution.

Expected idealized examples, after fitting the stated support:

| Magnitude pattern | l / Bg | t at current row | s | r |
|---|---|---|---|---|
| Settled zero | 0 | 0 | 0 | 0 |
| Steady uniform amplitude a in every bin | a | 0 | 0 | 0 |
| Steady amplitude a in one bin, other bins zero | a/K | 0 | a*sqrt(K-1)/K | 0 |
| Uniform step from zero to a, one active row among N | a/N | a*(1-1/N) | 0 | 0 |
| Fixed non-flat spectrum with changing level (slopes off) | Mean magnitude | Signed broadband change | `mean(envelope) * SD_frequency(shape)` | `SD_frequency(shape) * SD_time(envelope)` |
| Higher-order time-frequency interaction | Mean magnitude | Signed global rise/fall | Structured contrast | RMS left by the directional fit |

A uniform step has no residual because the model gives T an arbitrary temporal
profile. At startup N=1, its transient is zero: the only observed level belongs
to the background. This is an explicit finite-support behavior, not a missing
attack detector. Keep the existing waveform transient detector for immediate
onsets and for frequency-local attacks whose global mean changes little.

For immediate global visibility, use `q[now]=l+t`. A rectangular window mean l
has a temporal centroid `(N-1)/(2*rate)` behind the current spectral row: about
100 ms for the current 201-row/1000-Hz example, before frequency-dependent DFT
window delays. Sharing row timestamps does not make different-frequency DFT
measurements equally delayed.

After establishing the same-support version, consider a separate global time
window of roughly 0.5-2 seconds for passage response. That changes the meaning
of the baseline and transient and requires its own retained history; it does
not change the local projection. `B_local=l+b_local` remains an exact rebase
for any specified l. The same-support residual argument below specifically
requires the same time samples and basis, and should not be carried over to
unequal windows. Treat this longer window as an optional musical design choice,
not a statistically determined optimum.

### Global and local components overlap

These are two views of the same observations. Do not add global S/T/B to the
existing local S/T/B as if they were independent sources or independent powers.
The exact local additive extension is only the rebase `B_local=l+b_local`.

Fitting the global residual locally is mathematically possible, but changes
component ownership substantially. In fact:

```text
Rg = (I-Pf_global)*Y*(I-Pt)
Rg*Pt = 0
```

Restriction to a frequency neighborhood preserves this time orthogonality.
Therefore a local fit of Rg has no sustained component under the same time
basis. A full-domain global S already includes every bin's time-persistent
structure, including narrow notes; it is not limited to broad spectral shape.
This is a reason to keep the views parallel, rather than quietly redefining
local S as a remainder after global fitting.

### Does a high global residual make the local estimates jump?

No additional coupling is proposed. The local fit reads A; the global fit reads
the channel-averaged Y. Global R is an output of its own projection and does not
feed the local fit. Computing or omitting the global view therefore gives the
same local S/T/B estimates for the same samples and settings.

With fixed support and basis, signed fitted effects are linear functions of
the observations. Global residual RMS is the norm of another linear function,
so both depend continuously on the observations. A sudden note change can
produce both a large R and a sharp local transient because both respond to the
input. A smooth swell can give nonzero R while the estimates change smoothly.
Sliding rectangular windows can also produce steps as an old event expires;
startup rank changes and directional endpoint overshoot already belong to the
existing fit. None is introduced by computing the parallel global residual.

If a consumer uses only `b_local=B_local-l`, remote spectral activity can change
that contrast through l. That is its intended relative meaning. Reconstructing
`l+b_local` with the same l gives the original B_local exactly. Feeding R back
into local window lengths, thresholds or gains would create a new coupling and
requires a separate design; it is not part of this proposal.

### What “turbulence control” means here

In the existing v3 fluid, `RadiatingTurbulence` constructs localized rotating
forces. An optional artistic mapping would multiply that already audio-driven
force by a bounded, smoothed global interaction response, for example:

```text
target = r / (h+r), where h > 0 is a renderer sensitivity
e <- e + (target-e)*(1-exp(-dt/tau))
force <- force * (1 + 0.5*e)
```

Here e is a dimensionless envelope, dt is elapsed seconds, and tau could start
around 0.1 seconds for visual review. This example permits a gain from 1 to 1.5.
It strengthens the existing swirls while retaining their spatial arrangement.
It does not change estimated S/T/B, invent a random signal, reset velocity, or
inject new pigment. A zero underlying local force remains zero. Continue the
normal dye and force decay policies in silence. A coherent swell can also
strengthen this control, so its visual interpretation is “more spectral-time
interaction makes stronger swirls,” not “the audio has become noisier.” Treat
this mapping as optional until listening and visual review establish its value.

## 6. Alternatives to a full global view

| Alternative | Meaning and use | Mathematical limitation |
|---|---|---|
| Scalar l only | Passage baseline and ambient visibility; smallest change | No separate global rise/fall or spectral structure descriptor |
| Full global STBR | Recommended shared baseline, transient, structure and residual controls | Broad support does not mean only broad spectral features; overlaps local evidence |
| Coarse band-by-time STBR | Octave bands or a few broad bands reveal bass/body/air balance and broad movement | Band weights, unequal widths, coordinates and noise variance must be explicit |
| Temporal level/trend fit | A sustained global envelope plus signed deviations, with very little state | No frequency axis; cannot identify spectral S |

For a coarse view, average linear magnitudes within each band before fitting.
Use band means rather than sums if amplitude units should survive regrouping.
Equal-width octave groups simplify the model. For unequal groups, decide whether
the target gives equal weight to bands or to original bins. Recovering the
original bin mean requires group-size weights. Those target weights and
inverse-noise-variance precision weights are different choices; do not treat them
as interchangeable. Recenter coordinates and derive weighted projections when
weights or spacing change. This option can replace the full global view later.

For a scalar temporal alternative, let q[j] be the global frequency mean and fit:

```text
q[j] = b + a*u[j] + e[j]
b = mean(q)
a = mean(q*u)/vu
levelTrend = b + a*u[now]
levelChange = e[now]
df = N - timeBasisRank
```

This is a valid Gaussian working regression, with residual RMS from its own SSE.
Its residual includes attacks, other departures from the trend and noise. It
does not independently identify a transient source and a noise source. In
contrast, applying the existing STBR model to an F=1 rectangle simply gives
`S=R=0`, `B=mean(q)` and `T=q-B`: the arbitrary temporal component saturates it.

A fast/slow envelope pair is also useful artistically, but its time constants
define the split. Calling those two filters a Gaussian STB decomposition would
overstate their interpretation. A true additive decomposition across several
spectral scales would need explicitly nested bases and ownership constraints;
that is a larger change than this proposal.

## 7. Implementation plan

### 7.1 Analyzer and numerical kernel

1. Share the linear-amplitude conversion/sanitization and projection algebra.
   Add an internal linear-input path so local processing and channel averaging
   do not repeatedly exponentiate and log the same data. Apply input gain once,
   in the existing waveform decode stage.
2. Add a full-domain support mode or a shared global kernel. Setting
   `FrequencyWindowOctaves` extremely large is insufficient: the current
   `First/End` construction still clips a centered window at boundary bins.
   Global support must explicitly be `[0,K)` for every output frequency.
3. Form Y by arithmetic averaging of gained linear channel magnitudes. Run one
   global fit per analysis row with the same causal support and slope settings.
   Never average log magnitudes or already clipped S/T outputs.
4. Publish an immutable `GlobalEvidence` value containing Bg, t, s, r and support
   metadata. Make current mean `Bg+t` a derived property. Keep signed t on the
   CPU and GPU. Derive local background contrast as `JointEvidence.Background-Bg`;
   retain the existing `Background` meaning.
5. Attach this value to the same `SampleEvent` that carries each local texture
   row, including the optional joint-evidence event. Carry it through
   `Session.OnAnalysis`, `Renderer.PostAudio` and the owned `AudioHistory` queue.
   This keeps one analysis row and its global estimates together.

The scalar-only version needs just compensated summation of the existing
per-column temporal means, then averaging across channels. It adds O(C*K) work
and no history ring.

Compute means and RMS summaries in bounded internal units, following the
splitter's range-rescaling policy, and restore amplitude units only at the
output. Do not sum or square unscaled exceptional magnitudes: a mathematically
finite mean or RMS can otherwise overflow in an intermediate calculation.

An optimized global fit needs the Y raw-history ring plus per-frequency moments,
and one shared history each for global q and d. This is O(K*N) storage and
O(K+N) work per ordinary update including amortized rebasing. A straightforward
reuse of the current splitter may duplicate q/d histories per bin; that is
correct but uses more memory. At K=97 and N=201, one double raw-history ring is
about 152 KiB. Benchmark the actual implementation before promising a latency.

All local/global supports must refer to the same accepted rows. Currently the
splitter has no row timestamps: its linear time coordinate assumes consecutive
uniformly spaced updates. `Analyzer` can skip analysis row numbers after input
drops. For an initial implementation, reset both fit histories on a known row
discontinuity and resume using available startup rows. Do not invent zero audio
in the gap. A later timestamp-aware fit would require actual centered time
moments in place of the equally spaced vu and rolling cross-moment formulas.

### 7.2 Renderer and shader contract

Keep the existing local four-channel texture format. Append one
`float4 GlobalSTBR` to `FrameConstants` in
[Inputs.hlsl](../AEther.Vortice/Shaders/Common/Inputs.hlsl). Append the matching
Vector4 to `Renderer.Frame` and grow the constant buffer from 96 to 112 bytes.
Update any reflected layout expectations together. Do not reuse the local alpha
channel or silently redefine `VisualEnergy`, `VisualBreath` or `VisualFlash`.

For live ambient controls, consume the newest complete global packet, then use
elapsed-time response filters when smoothing is desired. Under stopped capture,
zero its live target using the existing stale-audio policy; the last global
packet must not remain a permanent source of light.

`Spectrum0/1` currently contains component-wise maxima over packets drained in
one render frame. Its components need not come from one analysis row. A latest
global value cannot give exact contrasts or reconstructions for that texture.
Use row-aligned data for those operations.

If a shader needs historical global modulation or exact `B_local-Bg` at a
historical sample, store one global vector per retained row in a width-one
history texture. Register its reflected resource name and bind it to every
entry point that reads it. Apply the same row coordinates, interpolation,
availability masks, gap clearing and capture-expiry rules as the spectral
history. A current uniform is sufficient for live ambient response; it is not
a substitute for historical metadata.

### 7.3 Preserve silence, including the actual DFT path

There is an existing numerical issue relevant to any sensitive new control:
[DFTProcessor.Output](../AEther/DFT/DFTProcessor.cs) writes
`log10(max(1e-10, bin))`. Consequently a zero DFT becomes amplitude 1e-10 in the
splitter, not zero. Recursive DFT filters can also retain roundoff after their
nonzero samples have expired; the existing audit bounds this residue but does
not establish exact zero.

Do not normalize that floor into visible activity. For a clean production
contract, preserve true zero as log magnitude minus infinity (already accepted
by the splitter as zero), and clear recursive filter states when their actual
sample rings contain no nonzero samples. Track exact nonzero occupancy in both
scalar and SIMD filter paths, rather than guessing silence from a fitted score.
This is a proposed prerequisite for exact-zero analysis semantics, not an
already implemented guarantee. Retain extremely quiet nonzero input.

Then exact zero reaches the local and global fits and finite history expires
normally. Stopped capture is a separate case handled by renderer expiry.
Every new shader mapping must map zero to zero and every retained light source
must decay. Avoid a ratio to the residual scale or to a moving level as an
unconditional source of light.

## 8. Suggested shader use

Use global evidence for passage-wide response and local evidence for placement,
frequency identity and stereo. Example mappings, with illustrative renderer
sensitivities rather than analyzer calibration:

```hlsl
float BoundAmplitude(float x, float h)
{
    x = max(0, x);
    return x / (h + x); // h must be positive
}

float structure = BoundAmplitude(GlobalSTBR.r, .05);
float rise      = BoundAmplitude(GlobalSTBR.g, .02);
float bed       = BoundAmplitude(GlobalSTBR.b, .01);
float texture   = BoundAmplitude(GlobalSTBR.a, .02);

// Shared passage illumination: zero in settled silence.
float3 ambient = palette * bed * AmbientAmount;

// Local note placement remains supplied by the existing texture.
float3 note = notePalette * max(0, local.r);
note *= 1 + .4 * structure + .6 * rise;

// Texture modulates an existing source; it cannot inject light by itself here.
float injection = max(0, local.r) * (1 + .5 * texture);
float3 light = ambient + note;
```

These controls have different amplitude distributions, so one shared exposure
constant is not a mathematical requirement. Keep sensitivities in the rendering
layer. Bounded responses are artistic transfer functions, not probabilities.
For falling passages, use `BoundAmplitude(-GlobalSTBR.g, h)` as a separate release
or contraction demand. Integrate motion using elapsed time rather than adding
an unscaled amount per frame.

| Existing shader/path | Suggested global use | Local source to retain |
|---|---|---|
| `FoldFlight`, `FilamentFlight`, `SpectrumRoad` | Bg-driven ambient/background response where `VisualBreath` is currently used | Existing spectral/history placement |
| `FractalEdge` | Bg for the shared body/ambient contribution; s for slow material response; positive t for a passage-wide pulse | Local tone/noise and attack for face detail |
| `Mandelbox`, `MandelFluid` | Bg for passage illumination, s for deformation demand, t for a global pulse | Tonal rays, recorded attacks and local B |
| `MandelFluidv2` / `MandelComet` | Bg for broad wake visibility, s for slow morphology, t for an additional global rise response | Existing undelayed waveform/local attacks and spectral ray identities |
| `MandelFluidv3` / `RadiatingFluid` | s/Bg may modulate shared transport demand; r may modulate local turbulence | Local B must continue to inject/stir locally; S pigment and T lightning retain their current roles |
| `MandelGalaxy` | Bg for shared starfield and faint core illumination; s/t for bounded size or exposure modulation | Historical band tone for trails, channel-specific gas and note placement |
| `EdgeIFS` | t for an ink pulse, s for bounded branch-shape response | Local injection and decaying image feedback |
| `Spectrum`, `Histogram` | Optional diagnostic global strip showing s, signed t, Bg and r | Raw alpha view and current local RGB view |
| `Traces` | Optional passage exposure modulation | Age-aligned local sources, analytic shutter and trail falloff |

Do not replace every transient with global t: an attack in a small spectral
region can have a small global mean. Do not replace local B with Bg: v3's local
background-only emission and stereo behavior are intentional. Likewise, globally
gating an entire historical image by today's level would change the intended
trail release. Apply live global illumination to live layers; sample historical
global metadata when modulating an older source.

The repository instructions require eventual black for **every** scene. Some
current source/documentation explicitly exempts the audio-independent
`Mandelbench` study from the music-scene regression. This is an existing policy
mismatch, not an exception granted by this proposal. A literal all-scene rollout
must reconcile that study's visibility contract and regression discovery.

## 9. Validation and rollout

The mathematical check performed for this report used an independent dense QR
projection with twice reorthogonalized modified Gram-Schmidt. It covered 896
cases: F=1..7, N=1..8, all four slope combinations, and random, product-trend,
sinusoidal-interaction and exact bilinear input families. It compared fitted
values, component centering, the residual variance identity, ranks and the
`l + (B-l) + S + T + R` reconstruction.

| Quantity | Largest absolute discrepancy |
|---|---:|
| Fitted value versus dense projection | 3.56e-15 |
| Residual variance identity | 5.83e-16 |
| S/T centering constraints | 5.00e-16 |
| Rebased reconstruction | 4.45e-16 |
| Model rank / residual df | All matched exactly |

This checks the algebra, not execution of `Splitter.cs`, long-run rolling
numerics, empirical Gaussian adequacy or GPU behavior. No repository test suite
or GPU test was run for this documentation-only task.

Implementation acceptance should cover:

1. A direct QR/SVD oracle for the production local/global fit, including every
   slope combination, clipped edges, startup, saturation and exact bilinear data.
2. Direct complete-history means for Bg; a regression that distinguishes this
   from mean local B; exact signed reconstruction using current residual samples.
3. The idealized global fixtures above, stereo averaging, identical-mono
   duplication invariance, gain scaling, and a one-channel attack. Averaging
   magnitudes must not cancel opposite-phase waveforms.
4. Correct interaction RMS for channel-averaged Y, independent of averaging
   channel RMS values; support metadata and zero-scale cases; finite overrange
   handling. Confirm its response to a coherent single-note swell before using
   it to drive visual turbulence or texture.
5. Long streaming runs, ring wrap, rebasing, numerical range changes, dropped
   rows and synchronized local/global resets. Benchmark allocations and latency.
6. Packet ownership and row alignment, including multiple analysis packets per
   render frame, missing rows, stale capture and any global history texture.
7. The full waveform -> DFT -> local/global fit -> renderer silence path, so
   synthetic renderer packets cannot conceal an amplitude-floor issue.
8. Preserve and extend
   [ShaderSilenceTests.cs](../AEther.Tests/ShaderSilenceTests.cs). Excite each scene
   with music and nonzero global controls, then test both continuing silent
   packets and stopped capture. Keep at least 1200 decay frames, allow the
   existing 10000-frame bound, and require every RGB channel to be black across
   60 final frames. Include unity and maximum supported input gain. Allow natural
   trail/fluid decay, with no scene reset or forced simulation clear.

Roll out the numerical/global event contract first, then a diagnostic global
display, then individual shader mappings. Preserve local texture semantics
throughout. Review representative music for visual sensitivity; that review
does not replace the numerical and silence criteria.
