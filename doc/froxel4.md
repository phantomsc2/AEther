# Froxel4

Select `Froxel4` or launch with `--scene=Froxel4`. A tube of infinite straight
line lights illuminates heterogeneous fog and a full spherical shell of 4096
slowly moving octahedra. Lines are parallel to Z, with radius 2.5 by default.
There are no emitter endpoints or tube-length control.

## Spectrum and lighting

The cross-section is `p(t)=R*(sin(t),-cos(t))`: negative angles read the left
channel, positive angles the right. Low frequencies meet at the bottom and high
frequencies at the top. Each audio bin creates one infinite line at its sector
midpoint, so light count is twice the graphics spectrum width. Current row-zero
RGB S/T/B is read directly; alpha is ignored. Each line's color is uniform along
its entire axis. No palette, longitudinal history or extra smoothing is added.
Geometry moves independently of audio; silence is black.

Intensity per unit length is `max(0, spectrumRGB) * gain * pi / bands`. Angular
weighting keeps aggregate per-length emission stable when a uniform spectrum
is resampled to different bin counts. Infinite lines have no finite total power.

For softened perpendicular distance b, point illumination is exactly `pi/b`:

```text
q = position - line.origin
q = q - line.axis * dot(q, line.axis)
b = sqrt(dot(q,q) + softening^2)
weight = pi / b
```

Camera-segment illumination is integrated analytically using an asinh difference
and a constant-distance limit for parallel rays. This retains the sharp core
without endpoint splitting, closest-point endpoint corrections, or atan2 calls.
Moving the origin along its normalized axis does not change the light. The
far field continues to fall as 1/distance from the axis; illumination extends
indefinitely along it. This intentionally replaces the former finite-line model.

Fog uses compile-time isotropic phase `1/(4*pi)`, applied once after summing
line contributions. There are no directional phase samples, outgoing-camera
calculations, or anisotropy control in Froxel4. Froxel3 retains HG for experiments.
Surfaces still use eight Lambert directions. Their infinite-line angular bounds
are constant, removing per-light endpoint trigonometry.

The light core is analytic, while scattering/extinction remain sampled once
per froxel. Correlation between varying illumination and attenuation inside a
cell is still approximate. There are no light-path attenuation, cast shadows,
specular reflections, multiple scattering, or separately visible emitter cores.

## Pipeline and controls

1. Prepare one 48-byte structured-buffer entry per stereo bin on the GPU:
   origin, normalized axis, softening, RGB intensity, padding. Allocation follows
   audio width; values update every frame.
2. Inject material coefficients into a 3D texture.
3. Evaluate all active lines' segment-averaged illumination in parallel across
   the froxel grid into a separate lighting/extinction volume.
4. Integrate camera columns using only the radiance/transmittance recurrence.
5. Rasterize and light the octahedron shell at full resolution.
6. Composite fog up to surfaces, reevaluating the visible partial final segment.

`Froxel4Options` exposes DepthSlices (8..256, default 256) and Instances
(0..65535, default 4096). The grid is `ceil(width/8) x ceil(height/8) x slices`.
Three RGBA16F volumes occupy about 88.5 MB total at 720p / 256 slices. Surfaces
use full-resolution HDR color and depth targets.

| Normalized shader control | Physical mapping / default |
| --- | --- |
| Tube.Radius | 1..8; default 2.5 |
| Tube.Softening | `.01+.49*value^2`; default .05 |
| Tube.Gain | `1000*2^(16*(value-.5))`; default value .4, gain about 330 |
| Cloud.Distance | 1..16; default 8 |
| Cloud.Spread | 0..4; default 1.5 |
| Cloud.Size | 0..2 times the .055..13 size range; default value .5 |
| Cloud.Speed | 0..2 times orbit/spin speed; default value .057 |

The full shell has uniform solid-angle placement and uniform volume density
between radii `max(0,distance-spread)` and `distance+spread`, default 6.5..9.5.
The infinite tube crosses both sides of this shell. Medium/camera defaults
retain density .36, albedo (.888,.840,.716), noise scale .720, contrast .796,
speed .730, camera distance/yaw/pitch .232/.523/.561. Fog has no spherical
boundary; camera integration still covers forward depth 0..64. Global exposure
is unchanged.

## Validation and profiling

GPU tests compare point illumination with quadrature over the whole line,
camera averages with dense ray integration, and Lambert response with numerical
integration. They cover parallel/zero-length camera intervals, axial translation
invariance, stereo positions and colors, intensity linearity, silence, pass
ordering, zero density/instances, motion, audio resize, canvas resize, rollback,
hot reload and deactivation. Tests also verify obsolete length/anisotropy controls
are absent. V1-3 regressions cover shared pipeline compatibility.

```powershell
dotnet build AEther.sln --no-restore
dotnet run --project tools/AEther.MandelLightProfile -- --froxel4 AEther.Vortice/Shaders artifacts/froxel4-production
```

The probe measures 8/32/96 active lines at 720p, 64/128/256 depth slices and
0/4096 objects. It records medians of 11 GPU samples after eight warmup frames;
captures use exposure -1.488. Total excludes Present/readback. Light preparation
is small; all-light evaluation and surface sampling still grow linearly with
active count. There is no clustering, far-field aggregation or phase LUT.

For matched comparison against saved finite and experimental infinite shaders:

```powershell
dotnet run --project tools/AEther.MandelLightProfile -- --froxel4-infinite-compare AEther.Vortice/Shaders artifacts/froxel4-infinite-production artifacts/froxel4-finite-reference/Shaders artifacts/froxel4-infinite/Shaders
```

That harness uses 32 active lines, 4096 objects, frozen time, 720p and exposure
-1.488; it measures medians of 21 samples after 32 warmup frames. It checks full,
surface-only and fog-only production images against the experimental infinite
variant. Changing from finite to infinite lines intentionally changes the image.
Timing variation remains possible; raw pass results and image comparisons are
saved in comparison.json.

The production comparison on Intel Arc measured 3.54..3.56 ms at 64 slices
and 8.30..8.39 ms at 256 slices, versus 6.09 and 13.11 ms for finite lines,
with 32 active lights and 4096 objects. Production images were byte-for-byte
identical to the experimental infinite variant for all six full/surface/fog
comparisons. Timings vary with GPU clocks and workload.
