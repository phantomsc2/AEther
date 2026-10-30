# Froxel2

One continuous stereo-spectrum ring illuminates an animated heterogeneous medium.
Select `Froxel2`, or launch with `--scene=Froxel2`. There are no surfaces,
rasterization passes, point-light buffers, or directly visible emitter geometry.
Only scattered illumination is shown; nearby bands naturally mix in the fog.

## Source model

The stationary ring lies in the XY plane:

```text
p(t) = R * (sin(t), -cos(t), 0), -pi <= t <= pi
frequency coordinate = abs(t) / pi
```

Low frequencies meet at the bottom; high frequencies meet at the top. The left
semicircle reads `Spectrum0`, the right reads `Spectrum1`. Each live STB bin is
one constant-intensity arc on each semicircle. S, T and B supply red, green and
blue intensity directly, with a common linear gain. No palette, channel blending,
extra smoothing, or nonlinear amplitude response is applied. Existing upstream
audio processing and graphics spectrum aggregation still apply.

The emission is radiant intensity per unit length. Arc contributions include
their length, so subdivision does not change power. Changing ring radius at
fixed intensity changes total emitted power proportionally to circumference.

`RingArcWeight` analytically integrates the softened inverse-square model:

```text
W = R * integral[begin, begin+span] dt / (A - B*cos(t-phi))
A = R² + rho² + z² + a²
B = 2*R*rho
rho = length(sample.xy)
phi = atan2(sample.x, -sample.y)
```

Each arc spans at most pi. The implementation uses an `atan2` angle difference
that stays positive across the angular branch cut. It forms near/far terms
directly rather than subtracting nearly equal squared distances; on the ring
axis it uses `R*span/A`. Positive softening radius `a` keeps the model finite.

At each froxel, sum arc intensity times W and multiply by local RGB scattering
and the isotropic phase `1/(4*pi)`. This is exact illumination for the specified
piecewise-constant, softened source model, up to floating-point precision.
It deliberately omits light-path medium attenuation, shadows and multiple
scattering. Camera-path extinction is integrated through the sampled noise.
There is no anisotropy control because nonconstant phase would invalidate this
arc integral.

## Grid, animation and controls

The shared froxel pipeline uses `ceil(width/8) x ceil(height/8)` columns and
8..256 depth slices, default 86. Slice boundaries cover forward depth 0..64
with shifted logarithmic spacing. Source/extinction and integrated
radiance/transmittance use two RGBA16F volumes. No lighting cache is present.

Each cell gets one stratified depth sample. A low-discrepancy rotation indexed by
`floor(T*60)` animates its jitter without a previous-frame image or persistent
sampling state. Fixed time is deterministic. Material and illumination are
evaluated at the same sample position. This changes fixed grain to animated
grain; it does not eliminate spatial undersampling or guarantee smoother frames.

Controls follow the existing normalized 0..1 shader convention:

| Control | Mapping/default |
| --- | --- |
| Ring.Radius | 1..8 world units; default 4.5 |
| Ring.Softening | `0.01 + 0.49*value²`; default 0.05 |
| Ring.Gain | `1000*2^(16*(value-0.5))`; default 1000 |
| Medium.Density | Mean extinction `0.6*value²`; default 0.159 (about 0.0152 per unit) |
| Medium.Albedo | RGB scattering fraction; default 0.95 per component |
| Medium.NoiseScale | 0.1..1.5 cycles per unit; default 0.720 |
| Medium.Contrast | Noise modulation strength; default 0.796 |
| Medium.Speed | Noise advection multiplier; default 0.730 |
| Camera.Distance | 2..40 world units; default 0.253 |
| Camera.Yaw | Full turn; default 0.574 |
| Camera.Pitch | -80..80 degrees; default 0.572 |

Noise fades between sphere radii six and eight. Medium movement continues in
silence, but without audio the source is zero and the output is black. Global
exposure remains the existing graphics setting and is not changed by the scene.

## Accuracy checks and performance

GPU tests compare arc weights against independent double-precision world-space
Simpson quadrature, including near-ring samples, axis samples, angular wrapping,
arc subdivision, and the closed-form uniform full ring. Other tests verify
individual stereo bins and STB colors at multiple spectrum widths, amplitude
linearity, silence, audio restart, time jitter, resize and reload rollback.
Froxel1 tests cover the shared integration and original raster/point-light path.

```powershell
dotnet build AEther.sln --no-restore
dotnet run --project tools/AEther.MandelLightProfile -- --froxel2 AEther.Vortice/Shaders artifacts/froxel2
```

The probe measures 16/64/128 bands per channel and 32/86/128 slices at 1280x720.
It captures synthetic stereo STB patterns and saves GPU medians plus adapter
details in `timings.json`. This reference implementation does two analytic arc
evaluations per active stereo band at each occupied froxel. Lighting work scales
with spectrum width times occupied cell count; integration scales with cell
count. Exact arc integration does not remove the all-arcs loop.

An initial 720p run measured 86-slice total GPU times of about 1.93 ms for 16
bands/channel, 2.34 ms for 64, and 2.77 ms for 128. These include audio upload,
composition and output, but exclude Present/readback. GPU clock variation means
these are observations rather than fixed scaling guarantees.
