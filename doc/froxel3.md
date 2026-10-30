# Froxel3

Select `Froxel3` (or launch with `--scene=Froxel3`) for a stereo-spectrum ring
illuminating heterogeneous fog and 1,024 slowly drifting, flat-shaded octahedra.
The ring's emission mapping, medium, camera, and exposure conventions match
[Froxel2](froxel2.md). Silence produces black output. The emitter has no visible
core; the octahedra receive light and occlude camera rays, but cast no shadows.

## Bounded lighting work

The shader approximates the spectrum with a fixed number of adaptive arcs at
each shading position, rather than summing all audio bins there. Two GPU threads
build per-channel RGB prefix integrals once per frame in an RGBA32F texture of
size `(bands+1) x 2`. Each thread scans one channel. Prefix differences give the
exact angular average of the piecewise-constant spectrum over an arbitrary arc,
including partial bins and the stereo seam. A direct bin fetch avoids prefix
cancellation when an arc lies entirely inside one bin.

The initial equal-geometric-mass partition was rejected after GPU comparison
with V2: near a dark section of the ring, averaging a distant bright bin over a
broad outer arc greatly overestimated illumination. V3 instead uses symmetric
logarithmic angular spacing around the nearest ring point. This resolves both
the near peak and the distant tail while retaining the fixed arc budget.

```text
rho = length(position.xy)
n = (R-rho)^2 + z^2 + a^2
f = (R+rho)^2 + z^2 + a^2
k = sqrt(n/f)
s = 2*k
theta(u) = sign(2*u-1) * s * ((1+pi/s)^abs(2*u-1) - 1)
u = arc boundary index / arc count
```

Here R is ring radius and a is positive source softening. Add the nearest-point
angle to theta to obtain world ring angles. Each coarse arc uses V2's exact
softened inverse-square geometric integral, multiplied by its average emission.
Uniform emission remains exact for isotropic, unshadowed incident light (within
floating-point precision). Arbitrary spectra are approximate: the average loses
correlation between emission and distance within each coarse arc. Narrow bright
bins contribute even when they fall between representative samples; they may
still be spread or misweighted. Increasing LightingArcs improves resolution.

Surface lighting uses the same arcs, with one Lambert cosine per arc evaluated
at its geometric-mass midpoint. This additionally approximates angular variation
and horizon clipping within each arc. Neutral diffuse albedo varies from 0.35 to
0.7 across instances. No specular, light-path attenuation, cast shadows, or
multiple scattering is evaluated.

## Rendering and controls

The existing volume injection and camera-column integration operate at
`ceil(width/8) x ceil(height/8) x DepthSlices`. Material and lighting use the same
animated depth sample. There is no temporal history. Full-resolution HDR raster
color and depth are composited using integrated fog up to the surface, including
analytic reconstruction of the final partial segment. Background pixels use the
complete fog integral. The result is `fog + transmittance * surface`.

`Froxel3Effect.CloudOptions` / `Froxel3Options` expose:

| Control | Default / range |
| --- | --- |
| DepthSlices | 86 / 8..256 |
| Instances | 1024 / 0..65535 |
| LightingArcs | 8 / 4, 8, 16, 32 |

Cloud shader controls use normalized 0..1 values, all defaulting to 0.5:

| Control | Mapping |
| --- | --- |
| Cloud.Speed | 0..2 times default motion; zero freezes geometry |
| Cloud.Spread | Torus tube radius 0..3 world units, default 1.5 |
| Cloud.Size | 0..2 times default object size (0.055..0.13 units) |

The cloud follows the ring radius. Default orbital speeds are 0.08..0.16 rad/s,
tube motion 0.12..0.22 rad/s, and spin 0.15..0.4 rad/s. Camera, medium, and ring
controls retain V2's defaults. All motion is procedural, independent of audio
amplitude; audio determines illumination.

## Validation and timing

GPU tests compare adaptive illumination with V2 for uniform light, a single
narrow bin, alternating colors, and stereo imbalance, at 8 and 31 bands/channel.
They cover near-ring points, the axis, the angular seam, linear intensity,
convergence, and surface directions against independent Lambert quadrature.
Lifecycle tests cover silence, motion, zero density, zero instances, invalid
option rollback, audio resizing, canvas resizing, hot reload, and deactivation.
Existing V1/V2 tests exercise the shared integration and composition paths.

```powershell
dotnet build AEther.sln --no-restore
dotnet run --project tools/AEther.MandelLightProfile -- --froxel3 AEther.Vortice/Shaders artifacts/froxel3
```

Initial Intel Arc measurements at **1280x720**, eight warmup frames and medians
of 21 GPU timestamp samples:

| Configuration (otherwise 128 bands, 86 slices, 8 arcs, 1024 objects) | Total GPU ms |
| --- | ---: |
| Default | 1.65 |
| 32 depth slices | 1.09 |
| 128 depth slices | 2.06 |
| 4 lighting arcs | 1.36 |
| 16 lighting arcs | 2.02 |
| 32 lighting arcs | 2.85 |
| No objects | 1.30 |
| 4096 objects | 2.15 |
| 16 spectrum bands/channel | 1.82 |
| 512 spectrum bands/channel | 1.87 |

At the default, injection was about 0.46 ms, integration 0.54 ms, rasterization
0.34 ms, and prefix preparation 0.013 ms. Prefix preparation rose to 0.041 ms
at 512 bands. Totals include audio upload, composition, and output, excluding
Present/readback. Clock variation and different spectrum patterns affect these
measurements; they are not fixed performance guarantees.

Preparation scales with spectrum width. Lighting scales with froxels or shaded
fragments times LightingArcs, integration with froxel count, and geometry with
instance count. V1 and the exact V2 source remain available for comparison.
