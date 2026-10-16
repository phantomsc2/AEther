# Mandelbox distance and history coordinates

## Musical structure experiment

The live Mandelbox controls now include Julia blend (default .3), internal fold
rotation (.35 radians per axis), and anisotropy (.22 logarithmic stretch).
Setting all three to zero restores the original orbit. All three follow
`MovementTime`, so movement speed also controls their exploration and zero freezes
their pose. They inherit the existing fast attack / slow release musical clock;
there is no wall-time drift or separate transient geometry envelope.

Each iteration applies box fold, sphere fold, internal rotation, diagonal stretch
and signed scale, then adds `beta*p + juliaOffset`. Slowly differing musical
periods change the rotation axes, Julia offset and point-injection coefficient.
Julia blend is an exploration strength: `beta=1-blend*(.55+.45*sin(.11*t+.4))`,
rather than a fixed interpolation coefficient. The three stretch factors are
`exp(anisotropy*cos(.14*t + phase))` with phases spaced by 120 degrees, preserving
volume while changing proportions. These are spatially uniform parameters.

The corresponding full chain rule is
`J_next = scale*D*R*J_sphere*J_box*J + beta*I`, starting at `J=I`.
The shader retains a scalar expansion bound:
`L_next = abs(scale)*max(stretch)*sphereFactor*L + abs(beta)`.
The largest stretch is required even though the stretch determinant is one.
Distance remains the existing empirical `length(z)/L` field, with the same
fractional-depth log blend and four-iteration coarse reference. Both reference
and deep estimates use the same varied orbit; history projection, angular note
placement and transient illumination retain their existing formulas.

Tone shading now favors the matching fractal shells: the straight-ray gain is
1.9 (previously 2.8), with squared fold relief and a lower valley floor, while
sustained shell gain is 2.2 (previously 1.5). This interrupts continuous spokes
and gives more weight to the fractal structure without changing note placement.

Transient fronts now carry the recorded spectrum around each stereo semicircle
(bass below, mids at the sides, treble above), instead of selecting transient
frequency solely from fractal distance. A bounded fold-dependent angular warp
retains the frequency endpoints. A five-tap spectral envelope softens the bands;
older tails broaden further. Front age is multiplied by a smooth 1.14-to-.88
bass-to-treble ramp, letting treble lead slightly. Color follows that same smooth
spectral coordinate. The immediate core uses the corresponding current spectrum.
The existing fractal history warp still shapes the fronts; no fractal derivative
or audio-analysis changes are involved. `--transient-only` checks isolated
bass/mid/treble impulses in each channel and saves their GPU previews. The HEAD
mapping fixture now uses noise illumination to isolate the underlying geometry
and history mapping from both tone and transient shading changes.

`MandelboxDistanceTests` checks the expansion bound against independent finite
differences across individual/default/maximum controls, musical poses and signed
scales. The GPU smoke runner supports `--variation-only` for isolated control
comparisons, five frozen musical poses and maximum settings. Captures are saved
as `tools/AEther.Smoke/artifacts/variation-*.png`. The full smoke run also checks
the original geometry against HEAD with all three controls disabled, AA, depth
continuity, stereo, history expiry and live settings across session restarts.

Iteration depth follows smoothed musical velocity directly: `depth=3+7*clamp(velocity/1.5,0,1)`. The same fast attack and slow decay govern movement and detail, with no separate depth envelope. The shader captures distance estimates at floor/ceil depth in one orbit and smoothstep-blends their log distances. The coarse reference remains fixed at four iterations. This is a visual field interpolation, not a guaranteed geometric distance bound.

Stereo spectra occupy two semicircles: left audio on the left, right audio on the right, with bass meeting at the bottom and treble at the top. The angular coordinate is `atan2(abs(x), -y)/pi` in aspect-correct screen coordinates. Tonal UVs retain the fractal blend along the sides, fading that blend to zero at the bass/treble endpoints. All history and current-spectrum sampling selects the corresponding channel, including transient hulls and core flashes. Channel differences can produce a seam along the vertical axis. GPU smoke checks use isolated left/right bass and treble to verify placement and channel isolation.

The current renderer is `AEther.Vortice/Shaders/Mandelbox.hlsl`; the `.fx` implementations discussed below are historical. Its slice depth, rotation, zoom and palette advance with integrated musical time. Each analysis row contributes RMS magnitude plus power, with extra drive from transient RMS. Movement speed follows that drive through an EMA with separate attack (0.16 seconds) and decay (1.6 seconds): `alpha=1-exp(-dt/tau)`, selecting attack when the target exceeds current speed. Heavy passages build speed quickly; zero input lets existing momentum coast down by 95% in about 4.8 seconds. Velocity and its accumulated travel are integrated analytically per frame, with audio drive weighted by sample duration. There is no base drift from rest. The immediate transient flash releases in 45 ms, while recorded hits continue outward on the separate presentation clock, including when capture stops sending packets. Unwritten and expired history is masked instead of wrapping into old events.

The slice now covers the viewport by cropping the interior field to its aspect ratio. History uses Euclidean radius with its horizon at the viewport corners, giving circular flashes across a full canvas. Fractal warping falls to zero at that horizon. Tonal frequency coordinates blend 68% toward the fractal coordinate at full warp; the default warp is .95. Transient contours have stronger detail and contrast, with bounded flash exposure to preserve their shape. On resuming after a packet-free pause, packet arrival time realigns the presentation clock with sample rows and discards expired pre-pause history; musical time keeps its pose. The smoke renderer checks illumination on every edge, shader compilation/reload, silence expiry and pause/resume without recreating the renderer; `AudioHistoryTests` checks frame-independent musical travel and transient acceleration.

The active `mandelbox-slice.fx` uses the standard scalar Mandelbox distance estimator described by [Mikael Hvidtfeldt Christensen](https://blog.hvidtfeldts.net/index.php/2011/11/distance-estimated-3d-fractals-vi-the-mandelbox/). Each box fold reflects coordinates without changing length. The sphere fold at radii squared .25..1 scales local lengths by `a=1/clamp(r²,.25,1)`. The affine step has scale -1.5 and adds the original sample point. The shader tracks

    z_next = -1.5 * a * boxfold(z) + p
    dr_next = 1.5 * a * dr + 1
    DE = |z| / dr

starting with `z=p`, `dr=1`. The `+1` is necessary because `p` is reintroduced at every step; without it the derivative would describe a Julia-style orbit with constant offset. This scalar recurrence bounds the norm of the full Jacobian by the triangle inequality. It is conservative relative to a local derivative calculation, though `|z|/dr` is still an empirical Mandelbox distance estimator rather than a globally exact signed distance to the fractal set.

The old `mandelbox.fx` attempted to calculate a full 3×3 Jacobian. Its sphere inversion branch uses `z` after multiplying it by the inversion factor in `I - 2*a*outer_product(z,z)`, whereas the Jacobian needs the **pre-inversion** vector `q` and `I - 2*outer_product(q,q)/dot(q,q)`. Its Jacobian updates also multiply the local transform on the right even though the orbit uses `z -> local(z)`; with column vectors the chain rule needs `J_next=J_local*J_old`. Those mistakes can be hidden by plotting only `det(J)`, because determinant multiplication is commutative. The `+I` affine update makes the order matter again.

Taking `cuberoot(abs(det(J)))` is not a valid distance denominator in general. The determinant measures volume change, whereas the local distance calculation needs the largest change of radius: `|grad_p |z|| = |J^T z|/|z|` (or a conservative matrix norm). For example, a Jacobian with singular values 100, 1, 1 has cube-root determinant about 4.64 but induced norm 100. The old shader further multiplies by `log(|z|)`, which belongs to a different family of escape-time estimators and can make the field negative inside the unit sphere. It also sets `z0.z = sin(.3*T)` and immediately overwrites it with zero, so that slice was not moving in its third dimension.

An independent finite-difference check over 300 points found the running scalar derivative at least as large as the local radial derivative within the numerical tolerance of the check. At one actual nine-iteration Mandelbox point, the local radial derivative was 270.186 while `cuberoot(abs(det(J)))` was 57.088, a factor of 4.733 apart. The corresponding executable checks are in `AEther.Tests/MandelboxDistanceTests.cs`.

The active shader uses distance for the spectral contour coordinate, but its previous history age was solely `1-exp(-.85*screenRadius)`, explaining the visible circular transient rings. Now it computes a second history coordinate from the ratio of the nine-iteration and four-iteration distance estimates. `V` cycles screen-only, a mixed setting (the default), and the strongest fractal mapping. A radial component remains at the strongest setting so new events originate near the center and advance toward the outer field. Audio changes illumination, while the field and its UV mapping remain geometric.

This mapping makes equal-age fronts follow Mandelbox detail. It does not simulate material transport along a vector field: where a true path and speed along a surface matter, we would need to choose a potential, calculate its gradient, and integrate trajectories. The three rendered mapping variants in the smoke artifacts make this distinction visible.

The visual time projection now uses `r(age)=1.02*(1-exp(-k*age))`, with age normalized to the recorded history span and `k=4` by default. Its inverse in the pixel shader is `age=-log(1-r/1.02)/k`. Here `r` measures the fraction of the distance to the viewport's horizontal or vertical boundary, then is warped by the fold field. This keeps the oldest recorded light visible near the outer edge. At the default one-second history, an event moves outward rapidly during its first quarter-second, then approaches the image horizon asymptotically. Press `C` to cycle `k=4, 8, 2`; higher values allocate more screen area to the recent past. The fold field warps radius before this inverse map, so an equal-age attack front follows fractal contours. This is the characteristic solution of a transport-and-decay model for emitted light: the histogram is the source history, the inverse map selects the emission time, and an age-dependent decay fades old light. It is not a simulation of moving fractal geometry or fluid momentum.

Sustained red contrast is sampled by angular frequency, creating stable rays at note-specific angles; a chord change lights new angles at the center. Green transient contrast is sampled using fractal distance, creating propagating hulls. Older green light is broadened in frequency and faded into a diffuse tail. The retained transient branch lights the central region before the delayed histogram reaches it. The raw onset detector no longer bypasses the split.

When loopback capture stops emitting packets, the renderer now writes zero rows into the same history ring at the analysis rate. This lets the last rays and hulls travel out and disappear; restarting audio appends after the silent interval. A renderer smoke check captures a lit histogram row, waits through a short silent window, and verifies that the result becomes black.

The shared camera response retains its long release but now has zero base travel and roll speed. After a track ends, movement gradually settles instead of drifting forever. The fractal's musical time `S` also stops accumulating when retained spectral power is zero; wall time still advances the history and response decay.

A full Jacobian is useful if a later renderer needs accurate local normals or directional flow. The correct sphere fold, in its inversion interval and before changing `q`, has `J_sphere = a*(I - 2*q*q^T/dot(q,q))`. The complete iteration has `J_next = -1.5*J_sphere*J_box*J + I`. Even then, determinant alone would not describe propagation direction. The gradient of a chosen distance or potential field would be the relevant direction; its iso-contours would carry the advancing light.

## Spatial antialiasing

Distance estimation, log-distance depth interpolation, frequency/history coordinates and
exposure follow HEAD. AA is a checkbox: disabled uses one center sample, enabled uses deterministic
2×2 sampling, averaging linear output colors before gamma encoding. Both modes
uses the same field and depth; there is no AA-dependent iteration suppression. Existing
screen-derivative shading heuristics use each subpixel's footprint. They remain approximate,
so quality changes can affect those filters as well as resolve additional spatial detail.

The maximum iteration count accepts any positive integer, defaulting to HEAD's 10.
Musical drive controls requested depth as before; at the default limit the original depth
response is preserved. Quality and maximum depth apply live without restarting capture.
The experimental linear propagation-distance setting, tracer and material injection are removed.

GPU checks compare None directly against HEAD at a fixed fractional depth, check integer
depth continuity, and compare spatial AA against averaged larger None renders with quad
shading derivatives disabled in that reference fixture. Existing rendering, stereo,
reload/resize, history expiry and settings lifecycle checks remain active.

Very high requested depths retain the last finite estimate if the orbit or derivative
approaches float overflow. This is a numerical-range guard, not a fixed iteration cap.

Scale is a live Mandelbox property, default -1.5. The orbit uses the signed scale and
the expansion bound uses abs(scale). Negative, zero and positive values are supported;
fixed-point orbit/derivative stagnation ends evaluation at the last finite estimate.
Finite-difference tests cover eight scales and GPU checks verify stable, distinct images
and live scale updates preserved across a scene restart. The HEAD mapping fixture uses
fixed colors to compare geometry/history independently of subsequent palette edits.
