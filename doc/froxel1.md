# Froxel1

Audio-independent DX11 participating-media POC. Select `Froxel1` in the scene
picker, or launch with `--scene=Froxel1`. Its noise field and colored point lights
animate using wall time. Lights blink with phase-offset squared sine cycles,
reaching zero intensity between pulses. There is no temporal history.

## Grid and passes

The grid is `ceil(canvasWidth/8) x ceil(canvasHeight/8) x DepthSlices`.
DepthSlices defaults to 64 and accepts integers 8..256. At 1280x720 the default
grid is 160x90x64: 921,600 cells, approximately one cell per canvas pixel in total.
Changing depth count leaves horizontal and vertical resolution unchanged.
PointLights defaults to four and accepts 0..64. Instances defaults to 1024 and accepts 0..65535; zero disables geometry. These controls live under Sizes.

1. **Froxel lights:** evaluate procedural position and intensity once per light.
2. **Froxel injection:** one thread per cell evaluates the medium and all lights.
   RGB stores the local scattering source per unit length; alpha stores extinction.
3. **Froxel integration:** one thread per screen-grid column loops over depth,
   writing accumulated radiance and transmittance at each slice boundary.
4. **Froxel raster:** draw 1024 small procedural octahedra (24 vertices each) into full-resolution HDR color and depth. They orbit rapidly in an XY torus of major radius 4.5, tube radius 0.95, with independent spins. Surfaces receive Lambertian lighting from the same blinking lights.
5. **Scene:** compose fog at the surface depth as C + T * surface. Reconstruct the final partial fog segment analytically; background pixels read the last integrated slice. Geometry occludes camera rays but does not cast light-space shadows.

Injection and integration use RGBA16F 3D textures with SRV/UAV views. Together
they use approximately 14.1 MiB at default 720p settings, excluding views and
the light buffer. The full-resolution RGBA16F color and D32 depth targets add about 10.5 MiB at 720p. Resize replaces the volume pair only when rounded grid
dimensions change; light count changes preserve the volumes. Deactivation and
successful shader reload release storage. Edited controls survive compatible reloads.

Shifted logarithmic slice boundaries cover forward depth zero through 64:

```text
z(k) = 65^(k / N) - 1
```

`N` is depth count. Physical segment lengths include the perspective ray's
angle to camera forward. A stable hash selects one depth sample within each
cell. Material and lighting use that same position. The open-interval hash
pattern follows the historical MandelVolume sampler; no frame index enters it.
Equiangular/MIS camera sampling is not used: the shared grid must retain fixed
slice boundaries for integration and reuse across all lights.

For constant source `q`, extinction `kt`, and segment length `h`:

```text
a = exp(-kt * h)
C += T * q * (1 - a) / kt
T *= a
```

The zero-extinction limit of the weight is `h`; a short series prevents
cancellation for small optical depths. Integration accumulates in float32 before
storing float16 prefixes. RGB writes saturate at the float16 maximum (65504)
to avoid infinities at extreme intensity/radius settings. There is no early
termination or temporal reprojection.

## Material and controls

All shader controls use the existing normalized 0..1 parameter UI.

| Control | Mapping/default |
| --- | --- |
| Medium.Density | Mean extinction `0.6 * Density^2`; default 0.15 per world unit |
| Medium.Albedo | RGB scattering fraction; default (0.95,0.95,0.95) |
| Medium.NoiseScale | 0.1..1.5 noise cycles per world unit; default 0.66 |
| Medium.Contrast | Density multiplier `1 + Contrast * noise`; default 0.8 |
| Medium.Speed | Noise advection multiplier; default 0.3; zero freezes it |
| Medium.Anisotropy | HG `g = 1.6 * (value - 0.5)`; default g=0.5 |
| Lights.Intensity | Peak intensity `5000 * value^2`, times RGB color and pulse; default 0.4 |
| Lights.Speed | Orbit and blinking speed multiplier; default 0.3; zero freezes both |
| Lights.Radius | Falloff softening radius `0.05 + 0.95 * value^2`; default 0.1355 |
| Camera.Distance | 2..40 world units from origin; default 18 |
| Camera.Yaw | One full turn; default 0.5 |
| Camera.Pitch | -80..80 degrees; default 0.57 (11.2 degrees) |

The medium uses one octave of smooth gradient noise and fades between sphere
radii six and eight. Scattering is albedo times extinction. HG uses incoming
light propagation and outgoing direction toward the camera, so positive g is
forward scattering. Light falloff uses `1/(distance^2 + radius^2)`.

Camera-path extinction integrates the sampled noise. Light-path attenuation
uses mean extinction times the light segment's length inside the radius-eight
sphere, ignoring noise and the boundary fade. This is deliberately approximate:
there are no opaque shadows, heterogeneous self-shadows, or multiple scattering.
Low depth counts, small light radii, or high anisotropy can expose spatial grain
and moving sampling artifacts; the scene has no temporal filter to conceal them.

## Verification and profiling

`FroxelTests` checks dimension/count controls, zero density/lights, independent
animation, camera edits, resource lifetime, resize, reload rollback, finite
volume data, and monotonic transmittance. A GPU probe checks homogeneous
integration at 8/64/256 slices against analytic radiance and transmittance,
including zero/tiny/high extinction and off-axis rays.

```powershell
dotnet build AEther.sln --no-restore
dotnet test AEther.Tests --filter FullyQualifiedName~Froxel
dotnet run --project tools/AEther.MandelLightProfile -- --froxel AEther.Vortice/Shaders artifacts/froxel1
```

The profiling tool renders at 1280x720, captures the default, animated, and
inside-volume views, and measures 16/64/128 slices with 1/4/16/64 lights.
It records medians of 31 GPU timestamp samples after 16 warmup frames, with
fixed scene time for the workload matrix. `timings.json` identifies the adapter
and separates light preparation, injection, integration, composition and output.
Total excludes Present and readback but includes audio upload and output.

Historical fog-only Intel Arc Graphics measurements with four lights (before the raster pass):

| Slices | Injection | Integration | Total GPU frame |
| --- | ---: | ---: | ---: |
| 16 | 0.135 ms | 0.062 ms | 0.471 ms |
| 64 | 0.518 ms | 0.398 ms | 1.189 ms |
| 128 | 1.027 ms | 0.796 ms | 2.093 ms |

At 64 slices, increasing light count from one to 64 changed injection from
0.504 to 0.782 ms. The full matrix is saved in `artifacts/froxel1/timings.json`.
GPU clock/power variation affects cross-workload comparisons; these are
observations, not targets.
