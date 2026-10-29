# Spectral IFS flight - concept and research outline

## 1. Intended experience

- Incoming sound creates visible, already intricate structure ahead of the camera.
- We travel through it at high speed: older structure approaches, expands in perspective and passes the camera.
- A central emitter is one possible projection; an emitting surface at a distance is the broader model. The visual reference is an Audiosurf-like ride through trippy scenery.
- Affine maps, folds, inversions and flame variations supply geometric vocabulary.
- Animation changes how new material is born and how existing material evolves.
- Primary target: audio-driven images. Audio resynthesis is a separate possible extension.

Working description: **a continuous mapping of spectral history into fractal flight scenery, with fluid afterglow**.
This is a proposed design, assembled from the references below; it is not a claim that a single established algorithm provides this exact result.

## 2. The connection

- MandelPond: repeatedly compose folds, inversion, scale and an offset; render a field associated with the resulting orbits.
- Flame: randomly compose maps and render the accumulated visitation density, with a palette and nonlinear exposure.
- Spectral tunnel: continuously inject an audio-derived distribution, transform it, transport it outward and let it fade.
- Common abstraction: iteration acts on points; collectively, those points define a distribution or image.
- Important distinction: a MandelPond field and a flame visitation density are different observables of iterative dynamics. Reusing their transformations does not make their renderers interchangeable.

### Why an initial seed alone is insufficient

For a conventional contractive IFS with a unique attracting invariant measure, long iteration forgets the initial distribution. An audio image used only as the initial particle positions will therefore lose its identity.

Preserve the music through continuous injection, finite particle lifetimes, retained spectral labels, and/or finite iteration depth. Audio can also change map parameters and probabilities, but that serves a different role from supplying material.

## 3. Three independent roles for audio

The latest intended mapping is more specific than the initial examples below:

- **Noise is clouds:** it supplies diffuse material.
- **Tones are slices:** wing-like movement cuts through existing clouds, leaving advected wakes.
- **Rhythm is localized fractal flashes:** attacks create immediate fractal events at particular flight locations, with a brief afterglow.
- **Dynamics is camera travel through the fluid:** the image-space carrier approximates forward movement, turning and roll.

`FluidFlight` sketches this mapping. `PlaneFlight` adds exact camera projection onto a world plane. The other three scenes retain deliberately different visual options.

| Role | Meaning | Example |
|---|---|---|
| Source | What enters the image | Spectral noise supplies cloud density |
| Dynamics | How material changes | Tonal wing cuts remove cloud material and create wakes |
| Observation | How state becomes visible | Rhythm lights fractal features at mapped history locations |

- Preserve frequency identity in source coordinates before experimenting with extensive mixing.
- Give tonal, transient and noise descriptors separate channels or labels rather than collapsing everything into one loudness value.
- Begin with fixed geometry and an audio source; add parameter animation after the source-to-image relationship is legible.

## 4. Coordinates: frequency, age, local fractal state

### Forward travel through an emitting surface

Let a cohort be created a distance $z_{\mathrm{far}}$ ahead of the camera, and approach at relative speed $v$:

$$
z(a)=z_{\mathrm{far}}-va,\qquad
p_{\mathrm{screen}}=f_{\mathrm{camera}}\frac{p_{\perp}}{z(a)}.
$$

- A small faraway structure expands in perspective as its age increases.
- Its apparent center depends on the emitter, camera orientation and flight path; it need not be the screen center.
- This gives a tunnel, road, cloud field or corridor through the same age-based source model.
- Perspective expansion accelerates near the camera, unlike the log-radius model below. Clip/fade near the camera to avoid the perspective singularity.
- Source frequency can choose a road lane, position on a distant surface or a direction around a corridor.

### Concrete shader sketches

| Scene | Main visual | Useful controls |
|---|---|---|
| `FoldFlight` | Twisted box-shaped gates with recursive fold lace | `FoldDetail`, `Twist`, `Bend` |
| `FilamentFlight` | Branching curved filaments surrounding an open flight path | `Branching`, `Curl`, `Aperture` |
| `SpectrumRoad` | Spectral road, roadside columns and folded overhead scenery | `Bend`, `TowerHeight`, `Backdrop` |
| `FluidFlight` | Persistent noise clouds, tonal wing cuts and localized rhythmic fractal flashes | `Fluid.Flow`, `Flight.Depth`, `Flight.Turn`, `Flight.Roll`, `Flight.WingAngle`, `Flight.SliceStrength`, `Flight.CloudCover`, `Flight.FlashDistance` |
| `PlaneFlight` | The same spectral/fractal material on a world plane, viewed through a 3D camera | `Camera.Height`, `Camera.Pitch`, `Camera.FieldOfView`, `Camera.FractalScale`, `Flight.Steering` |

- The first three expose `FlightSpeed`, `AudioGain`, `Glow`, `Background` and `Steering`. The two fluid scenes group controls under `Flight`, `Fluid`, `Filtering` and `Reaction`. `PlaneFlight` also exposes `Camera`.
- Select them in the existing Scene selector. F6 shows their reflected controls.
- `Steering` shifts the view; it does not add keyboard controls or a flight simulation.
- These are finite procedural fold/orbit sketches. `FluidFlight` additionally stores a persistent fluid afterglow; none accumulates a flame chaos-game histogram.
- The first three use world-space slices retaining birth-time parameters. `FluidFlight` uses a continuous per-pixel history map without event planes, particles or spatial event bins.
- They interpret normalized retained history as one second, matching the default history duration. A nondefault history duration would need an explicit seconds constant to match physical age exactly.
- The first three have slight `Background` illumination while choosing a direction; set it to zero for audio-only illumination.

### Exact 3D projection of a 2D plane

`PlaneFlight` puts the fractal material on the world plane $Y=0$. A pinhole camera has world position $C$ and orthonormal right/up/forward vectors $R,U,F$. For aspect-correct screen coordinates $(x,y)$ with positive screen $y$ pointing downward, its pixel ray and plane intersection are

$$
d=F+\frac{xR-yU}{f},\qquad
\tau=-\frac{C_y}{d_y},\qquad P=C+\tau d.
$$

Only rays pointing toward the ground are shaded. $f$ comes from the vertical field of view. The IFS samples a bounded, continuous chart of world coordinates $(P_x,P_z)$, and camera depth supplies history age.

- This is an actual perspective projection of a world plane, equivalent to perspective-correct sampling of a rasterized textured plane.
- Analytic ray/plane intersection fits the current fullscreen shader pipeline. It needs one finite fractal evaluation per visible pixel, with no distance-field ray march.
- A polygon mesh becomes useful for displaced terrain, geometric silhouettes, multiple occluding surfaces, or depth-buffer integration. It is unnecessary for this flat surface.
- The fractal chart and cloud texture use world positions. Camera translation, yaw, pitch, roll, height and field of view affect the projected plane.
- The afterglow remains a 2D fluid. Its translation flow uses the plane depth and camera basis; musical acceleration uses the available capped velocity proxy. Abrupt camera-control changes do not reproject an entire stored world-space volume.
- The world plane is flat. Shading and fractal relief do not create geometric height or self-occlusion.

### Stronger audio response

Transients now act as **multiplicative lightning through the rings**. A contour-weighted gain multiplies existing foreground and retained material light. Transients contribute neither density nor an independent emission layer. The illuminated source color feeds the fluid afterglow, while its density remains unchanged. `Reaction.Attacks` controls the lightning strength.

The current flight look uses **one combined material**. Cloud texture softly modulates fractal density; tones carve and illuminate its existing rounded contours; rhythmic energy excites the same density. Fresh response and fluid memory share one palette and lighting model. Circular fold contours remain prominent rather than being covered by a separate cloud or flash layer.

Tonal wing cuts follow the circular contour field inside a broad directional wing envelope, leaving shaped wakes instead of one solid horizontal gap. Foreground evaluation reuses its existing contour result when computing the cut.

- Higher source sensitivity gives quiet cloud/noise components and tonal cuts more visible influence.
- `Reaction.Attacks` separately boosts localized transient flashes.
- `Reaction.Strength` controls foreground response, while `Reaction.Afterglow` controls the persistent layer, allowing quick changes to remain visible over older light.
- `Reaction.TonalCuts` strengthens the cuts and wakes. `Reaction.CameraMotion` adds musical travel to the projected camera or motion to the tunnel carrier.
- Temporal filtering is shorter by default so onset response stays sharper; `Fluid.Diffusion` and `Fluid.HalfLife` still control soft trails.

### Preferred architecture: map the histogram directly into the IFS

Let $H(q,a,t)$ be the retained stereo tone/transient/noise histogram, and let a finite composition of animated folds and variations produce a sampling map:

$$
\Phi_t(p)=\bigl(q_t(p),a_t(p),g_t(p)\bigr).
$$

Here $q$ is frequency, $a$ is history age, and $g$ supplies fractal relief. The instantaneous foreground is

$$
L_0(p,t)=\mathcal S\!\left(H(q_t(p),a_t(p),t),g_t(p)\right).
$$

- This is pullback sampling: each image pixel asks which histogram location supplies its material and light.
- The history texture is sampled continuously with interpolation. No conversion to note objects, onset events or point clouds is required.
- The IFS can fold frequency and age erratically. Exact chronology and invertibility are optional artistic constraints, not requirements of the renderer.
- Smoothing includes bilateral filtering of the spectral history, unresolved-detail averaging in the procedural field, and diffusion of the fluid afterglow.
- Pullback sampling paints image values; it is not automatically the mass-conserving pushforward used in invariant-measure theory.
- Diffusion provides a soft temporal layer. It cannot repair arbitrary aliasing in the sharp foreground, so the foreground also needs filtering.
- Strict stability claims for a contractive IFS are unnecessary for finite, bounded evaluation. Guard singular inversions, keep history reads within their valid range, and bound injected light.

The fluid stores an afterglow $A$, rather than defining where new fractal geometry must be born:

$$
\partial_t A+v_{\mathrm{image}}\cdot\nabla A
=D\nabla^2A-\lambda A+\eta L_0,
\qquad L=L_0+\beta A.
$$

The implementation uses positive, finite-capacity deposition and optionally attenuates the afterglow along tonal wing cuts.

### Projective motion in the 2D fluid

For screen position $p$, vanishing point $p_0$, camera forward speed $V_z$, lateral velocity $V_\perp$, and an estimated depth $Z(p)$, a useful optical-flow approximation is

$$
v_{\mathrm{image}}(p)=\frac{V_z}{Z(p)}(p-p_0)
-\frac{V_\perp}{Z(p)}+v_{\mathrm{rotation}}(p)+v_{\mathrm{turbulence}}(p).
$$

- `FluidFlight` chooses inverse depth as a positive function of distance from the vanishing point. This is a visual depth assumption, not recovered scene geometry.
- The camera carrier bypasses incompressibility projection. Pressure projection acts on the turbulent perturbation; projecting the whole field would suppress the intended expansion.
- Noise supplies spatially textured cloud material through the continuous IFS/history map.
- Tonal energy controls an oriented wing-shaped removal mask and a small wake force. Cuts remain in the advected state.
- Rhythm samples the transient histogram through the same map and lights fractal relief at the corresponding flight location. The fluid records and diffuses the light as afterglow.
- The procedural history map and fluid velocity remain related visual approximations, not one exact 3D transport solution or a shared geometric depth buffer.
- One screen-space layer cannot supply true parallax between overlapping depths, reveal hidden surfaces after a turn, or guarantee correct occlusion. Layered depth/density buffers would be the next step if those cues become essential.

A schematic afterglow update is

$$
A_{n+1}(p)=e^{-\lambda\Delta t}
\operatorname{AdvectDiffuse}(A_n,v_{\mathrm{image}})(p)
e^{-\Delta t\,K_{\mathrm{tone}}(p)}
+\Delta t\,\eta L_0(p,t).
$$

The implementation also has bounded deposition and edge outflow, and applies the cut after deposition. Clouds and flashes contribute immediately to the foreground and feed its fluid afterglow.

### Spectral coordinate

For positive frequencies in the selected range, use normalized log frequency:

$$
q(f)=\frac{\log(f/f_{\min})}{\log(f_{\max}/f_{\min})},\qquad q\in[0,1].
$$

- Equal musical intervals then have equal coordinate spacing.
- Reuse the analyzer's frequency mapping if it already supplies suitable bands; do not logarithmically remap twice.
- A natural tunnel embedding puts frequency around the angle and history along the radius. Stereo can occupy separate semicircles, as in the current MandelPond.
- Frequency endpoints are not intrinsically periodic. Handle their angular seam explicitly.
- If interpreting values as energy, aggregate power into bands; a display magnitude is a legitimate artistic source but is not automatically an energy density.

### Age becomes distance from the center

For a finite source radius and constant fractional expansion rate:

$$
a=t-t_{\mathrm{birth}},\qquad
r(a)=r_0e^{\kappa a},\qquad
u=\log(r/r_0)=\kappa a.
$$

- Translation in log radius becomes dilation on screen: this is a useful tunnel model.
- The maximum visible age is $a_{\max}=\log(r_{\max}/r_0)/\kappa$.
- Use a small disk or annulus, not a mathematical point: an outward velocity proportional to radius leaves the exact origin stationary.
- Linear radial travel is another choice. The current MandelPond instead uses a saturating outward projection. These produce different acceleration and history spacing.
- Log frequency and log radius solve different problems: musical interval spacing versus multiplicative visual growth.

### A useful lifted state

$$
\xi=(a,q,c,z),
$$

where $c$ carries channel/material identity and $z$ is a local fractal coordinate.

- All branches advance age together.
- Apply folds and variations primarily to $z$, with controlled changes to spectral placement.
- Embed the state into the image through a map $\Psi(a,q,c,z)$.
- Optional: preserve outward age ordering when a readable chronology is wanted. Unrestricted folds can deliberately mix ages and bend temporal fronts.
- A radial warp needs a monotonicity check only if strict center-to-edge ordering becomes a requirement.

## 5. Mathematical framework: driven transfer operators

For maps $F_{i,t}$ chosen with constant-in-space probabilities $p_i(t)$, define an operator on measures:

$$
\mathcal P_t\mu=\sum_i p_i(t)(F_{i,t})_\#\mu,
\qquad p_i(t)\geq0,\quad\sum_i p_i(t)=1.
$$

Here pushforward means moving all the material through the map, including adding contributions when branches overlap. This accommodates folds that have no unique inverse. Spatially dependent branch probabilities require weighting the source measure before each pushforward.

Add retention, a fresh source rate $S_t$, and a timestep:

$$
\mu_{n+1}=\gamma\mathcal P_{t_n}\mu_n+\Delta t\,S_{t_n},
\qquad \gamma=e^{-\lambda\Delta t}<1.
$$

For a fixed operator and source, the stationary response is

$$
\mu_*=\Delta t\sum_{j=0}^{\infty}\gamma^j\mathcal P^jS.
$$

Interpretation: the image contains fresh sound plus transformed, fading copies of earlier sound. Each power of the operator is another generation.

- This is an inhomogeneous, dissipative evolution of distributions; animated maps make it nonautonomous.
- With a mass-preserving or mass-losing operator, retention bounds total mass for bounded source mass. It does not by itself bound continuum point density at singular concentrations.
- On a raster, conservative weights, finite footprints and filtering control concentrations. Tone mapping belongs after state evolution.
- Outward screen maps can be expansive. The classic theorem for contractive IFS attractors is not the justification for this open, source-driven renderer.

### Texture feedback is a related, distinct implementation

For a brightness/tracer image, a practical pullback update is

$$
C_{n+1}(x)=\gamma\sum_iw_iC_n(B_{i,t_n}(x))+\Delta t\,J_{t_n}(x).
$$

- $B_i$ reads an upstream position; it contracts the sampling coordinates when the visible image expands.
- Nonnegative weights summing to at most one, bounded sampling and bounded injection provide a simple discrete brightness bound.
- This transports image values. It is not generally a mass-conserving discretization of the measure equation above.
- For a density, account for area change/Jacobians or use conservative splatting. For folds, include all relevant preimages rather than choosing one arbitrary inverse.
- Repeated texture resampling blurs detail. Particles, stored coordinates or procedural reevaluation can retain finer structure.

## 6. Continuous model for the tunnel

An artistic tracer field can satisfy

$$
\partial_t C+v\cdot\nabla C=-\lambda C+J.
$$

A conserved density instead satisfies

$$
\partial_t\rho+\nabla\cdot(\rho v)=-\lambda\rho+J.
$$

For radial expansion $v(x)=\kappa x$ in two dimensions, the density has an additional dilution term $2\kappa\rho$. Choose whether the desired quantity is brightness, pigment concentration or total energy.

For radial expansion plus constant angular rotation, with boundary source $B$ at $r_0$, the tracer solution outside the source is

$$
C(r,\theta,t)=e^{-\lambda a}
B(\theta-\omega a,t-a),\qquad
a=\frac{\log(r/r_0)}{\kappa}.
$$

This explains why history lookup can produce a coherent tunnel without storing previous rendered images: each pixel looks up its emission time along a characteristic. More complex branching or mixing needs richer state or a more elaborate procedural solution.

## 7. Immediate complexity versus evolution over time

- Fractal iteration depth and elapsed age are separate variables.
- A new cohort can receive many local fractal iterations at birth, so its visible central shape is intricate immediately.
- Physical updates then move that cohort outward and optionally transform it further.
- One map application per display frame ties geometric detail to frame rate and makes young material comparatively simple. Use an explicit simulation clock and iteration budget.
- Retain a spectral label even when local coordinates undergo substantial iteration, so local mixing does not erase frequency identity.
- Decide whether a cohort retains its birth parameters or responds to current parameters. Birth parameters preserve its visual identity; current parameters allow the whole field to morph.
- A hybrid can retain birth geometry while applying a slowly changing global deformation. Reconstructing historical geometry requires parameter history or a deterministic time-addressable schedule.

## 8. Implemented experiment and next directions

1. `FluidFlight` projects a depth proxy into continuous history age.
2. Seven finite fold/inversion iterations warp frequency, age and relief.
3. The filtered histogram supplies cloud, slice and flash channels directly.
4. The same mapped light feeds the existing fluid; perspective flow, turbulence, diffusion and decay produce the afterglow.
5. Compare the other three sketches to steer the visual character.
6. Explore more erratic variation compositions and stronger spectral/time filtering.
7. If foreground and afterglow need tighter attachment, derive both their history-age map and optical flow from the same characteristics.
8. Introduce layered depth only if turning, occlusion and parallax become essential.

### Current project anchors

- `AEther.Vortice/Shaders/Common/MandelPond.hlsl` already maps history age to outward, fractal-warped fronts. It samples current spectra near the core.
- That shader reevaluates geometry with current parameters; its travelling illumination does not preserve historical geometry by itself.
- `doc/mandelpond-distance.md` explains the history mapping and its distinction from material transport; some passages document older revisions.
- `doc/mandelfluid.md` describes persistent pigment and an outward carrier, a useful neighboring experiment for material transport.
- `doc/shader-authoring.md` documents spectrum/history inputs. Arbitrary simulation pass schedules are not exposed by the current scene contract, so general texture feedback needs host support.

### Questions to judge visually

- Does a short event flash at a readable flight location and leave an appropriate fluid afterglow?
- Does a sustained note create a stable family of structures?
- Does a chord change remain readable as a new generation?
- Can transform animation preserve existing cohorts when desired?
- Does silence let old structure exit and fade?
- Does adding an IFS branch make interesting structure without erasing the sound's role?

The sketches are compiled and rendered on D3D11 with synthetic audio during development. These are visual evaluation criteria for subsequent real-audio tuning, not claims of a finished effect or a completed performance benchmark.

## 9. Reading and viewing order

1. [James P. Crutchfield — Space-time Dynamics in Video Feedback (1984)](https://csc.ucdavis.edu/~cmg/compmech/pubs/sptmdvfb.htm). Closest conceptual starting point: an entire image evolves under an iterated functional equation. Read it for recursive zoom, rotation and spatial dynamics.
2. [Scott Draves and Erik Reckase — The Fractal Flame Algorithm](https://flam3.com/flame_draves.pdf). Transformation vocabulary, density rendering and structural coloring. Separate these rendering ideas from the tunnel's chronological transport.
3. [Karl Sims — Fluid Flow Tutorial](https://karlsims.com/fluid-flow.html). Concrete explanation of pushing particles versus pulling textures, plus repeated injection. Borrow advection; outward expansion does not require an incompressible fluid solve.
4. [John Hutchinson — Fractals and Self-Similarity (1981)](https://doi.org/10.1512/iumj.1981.30.30055). Foundations for invariant sets and measures; useful for understanding precisely when contractive IFS results apply.
5. [Barnsley, Harding and Igudesman — How to Transform and Filter Images using Iterated Function Systems (2011)](https://arxiv.org/abs/1102.3199). A bridge from IFS geometry to transforming existing images.
6. [Gregg Helt — Extending MandelPond Fractals with Shape Inversions (2018)](https://arxiv.org/abs/1809.01720). Inspiration for generalizing the fold/inversion vocabulary beyond spheres.
7. [Karl Sims — Seven Experiments in Procedural Animation](https://www.karlsims.com/seven.html). Watch for continuity, emerging detail and how parameter animation changes the character of a field.

### Optional path toward sound synthesis

- [MetaSynth manual: Image Synth and Image Filter](https://uisoftware.com/metasynth-manual/). Inspiration for treating time-frequency images as editable material.
- [Henderson and Solomon — Audio Transport: A Generalized Portamento via Optimal Transport (2019)](https://arxiv.org/abs/1906.06763). A useful distinction between moving spectral peaks and crossfading their amplitudes.
- [Valdivia et al. — Audio signal interpolation using optimal transportation of spectrograms (2025)](https://arxiv.org/abs/2502.15430). Spectrogram transport followed by phase reconstruction and inversion.
- A visually valid magnitude image is not automatically a consistent complex time-frequency representation. Resynthesis requires a phase model and synthesis procedure; it adds constraints absent from this visual tunnel.
