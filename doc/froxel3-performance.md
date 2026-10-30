# Froxel3 performance review

The screenshot preset uses 256 slices and 4096 octahedra. Camera normalized
Distance/Pitch/Yaw are .232/.561/.523; Cloud Size/Speed/Spread .5/.057/.174;
RGB albedo .888/.840/.716. Following the subsequent request, anisotropy defaults
to actual g=.6 (normalized .8), rather than the screenshot's normalized .939.
Controls hidden below the screenshot retain their existing defaults. The
review captures use the screenshot's global exposure -1.488; global application
exposure defaults are not changed for other scenes.

## Measurement

Intel Arc, 1280x720, 160x90x256 froxels, fixed scene time, 64 warmup frames,
31 GPU timestamp samples per scenario. Medians exclude Present and readback.
GPU clock/background-load variation remains significant: removing geometry or
density noise did not consistently reduce total time, so these are diagnostic
observations rather than equal-quality speedup promises. Every ablation changes
the image. Full pass results and images are in artifacts/froxel3-review.
An isolated baseline repeat measured 3.95 and 4.42 ms; its integration pass
varied from 2.59 to 3.03 ms. This confirms the need to avoid precise speedup
claims from small differences between these scenarios.

| Workload | Total ms | Material injection ms | Integration + fog lighting ms | Raster ms | Composition ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| New defaults, 3 rings | 3.96 | .66 | 2.59 | .31 | .22 |
| 128 slices | 2.40 | .34 | 1.32 | .32 | .23 |
| 64 slices | 1.51 | .17 | .67 | .31 | .21 |
| Isotropic | 3.44 | .78 | 1.86 | .39 | .19 |
| One ring | 3.03 | .75 | 1.80 | .17 | .15 |
| One phase direction, deliberately rough | 2.54 | .63 | 1.34 | .30 | .15 |
| Fog lighting disabled | 2.47 | .63 | 1.32 | .31 | .10 |
| Point-sampled light in parallel source pass | 3.74 | 1.95 | 1.23 | .32 | .11 |

The constant-medium and no-object scenarios exhibited particularly large
timing variation; inspect raw results before drawing conclusions from them.
Disabling fog lighting retains extinction integration and the surface pass.
The one-phase-direction experiment changes fog phase sampling only; Lambert
surface lighting still uses eight directions. The point-sampled reference
restores depth-jitter lighting errors and does not measure a parallel version
of the analytic segment integral.

Reproduce with:

```powershell
dotnet run --project tools/AEther.MandelLightProfile -- --froxel3-review AEther.Vortice/Shaders artifacts/froxel3-review
```

## Where work goes

There are 3,686,400 froxels. Every camera column serially walks its 256 slices.
Inside that loop, every ring computes tangent geometry, closest approach,
curvature subdivision, square roots and log/asinh terms. Each local piece also
evaluates eight HG directions. Three rings therefore require at least 11.1
million ring-segment evaluations and 88.5 million phase directions per frame;
subdivision increases those counts. Some degenerate branches bypass sampling.

Material injection is already parallel in all three dimensions, and contains
procedural gradient noise plus a volume write. It is independent of light count.
The marker still says 'Froxel ring injection', but in V3 it injects material.
The two RGBA16F volumes occupy about 59 MB at this resolution. Source write,
integration read and prefix write nominally move about 88 MB/frame before cache
effects and composition reads.

4096 octahedra generate 98,304 vertices / 32,768 triangles. Surface pixels also
evaluate all three rings with eight Lambert directions each. Composition
evaluates the partial last fog segment for pixels covered by geometry, repeating
ring integration there. Geometry generation and output presentation are smaller
costs in this view; both surface lighting and partial integration would still
grow with light count.

## Toward 100 rings

100 rings evaluated everywhere at 256 slices would mean 368.6 million
ring-segment evaluations and roughly 2.95 billion HG directions before curvature
subdivision. Extrapolating the measured lighting increment suggests tens of
milliseconds, not a viable extension of the present all-rings loop. This is a
work estimate, not a measured 100-ring benchmark.

1. Separate parallel segment-light evaluation from the sequential camera scan.
   Preserve the analytic peak average, write it per froxel, and let the column
   pass perform only radiance/transmittance recurrence. Keep material available
   for surface-depth reconstruction. This improves parallelism but does not
   remove the asymptotic light-count cost and adds volume traffic.
2. Assign rings to 3D clusters using expanded ring tubes and a contribution
   threshold. A conservative enclosing sphere is a broad-phase test, not a good
   final assignment shape for a large ring. Share the lists with raster shading
   and partial-segment evaluation. Ring interiors and distant tails still
   contribute: a tube list alone is an approximation, not exact culling.
3. Spend analytic peak integration and eight phase samples on a small number
   of locally important rings. Approximate far rings by point emitters when
   distance is large compared with radius, and aggregate sufficiently similar
   distant contributions in a hierarchy or coarse directional field. Smoothly
   blend tiers to avoid popping. Maintain sharp local distance falloff.
4. Approximate phase before softening the filament. Try one/few directions for
   distant lights, or a lookup for the local-line phase average. Strong g makes
   broad low-order directional caches inaccurate, so preserve selected strong
   forward lobes separately. Validate at .6 and .9.
5. Precompute per-ring transforms, radius/softening constants and animated RGB
   intensity once per frame in a structured buffer. This is small setup work;
   it avoids repeated trig and prepares for arbitrary ring positions/orientations.

The practical target is 100 scene rings with roughly 4..8 expensive local
contributions per cluster, not 100 expensive evaluations per cell. Highly
overlapping equally important rings remain a worst case: then fewer samples,
lower spatial/depth resolution, or stochastic selection with reconstruction is
necessary. Because peaks are integrated, reducing material depth to 128 is a
more promising quality tradeoff than blurring or enlarging the light core.
