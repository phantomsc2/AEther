# MandelLightv5: shader-defined point lights

Select **MandelLightv5** after building the host. The scene retains v4's atmosphere
and procedural cube cloud, with three fixed perpendicular rings of point lights:
S in XZ, T in XY, and B in YZ. Each ring samples its corresponding live
`Spectrum.rgb` component, using the angular frequency palette and stereo blend.
The shader samples row zero at UV.y=.25 in the two-row spectrum textures,
keeping diagnostic data out of the light intensity.
Intensity is linear in the component, with no additional history or ambient floor.
Silence renders black. The rings stay fixed while the camera and cubes can move.

Defaults are 128 cubes, 60 lights per ring, radius 5.7, and shared gain 4x.
`Lights.Count` maps to 1..96 lights per ring, `Radius` to 0..10 world units,
and `Gain` to `2^(12*value-6)`. `Enabled` removes all lights. Geometry, atmosphere,
camera, exposure, and depth preview retain their v4 meanings.

## Point-light contract

Include `Common/RasterGeometry.hlsl`, `Common/PointLights.hlsl`, and
`Common/PointRasterAirlight.hlsl`. Implement the geometry callbacks,
`CameraFrame`, and:

```hlsl
uint LightCount();
PointLight EvaluateLight(uint index);
// PointLight contains only float3 Position and float3 Intensity (RGB).
```

`PreparePointLights` runs once per frame and writes a GPU count and a structured
buffer with 24-byte records. The host allocates capacity for 512 lights and
dispatches eight groups of 64 threads; HLSL clamps the count to this capacity.
Records below the current count are overwritten every frame. A smaller or zero
count immediately excludes previous records. Zero-intensity records are skipped
by the lighting shaders. The CPU never generates positions, intensities, counts,
or active lists, and never reads them back during rendering. Callbacks can use
frame constants, parameters, and spectrum/history textures.

The distinct `PointSurface` resource selects `ProceduralPointLightTransport`.
It prepares geometry and lights, draws Lambertian surfaces/depth with the existing
indirect geometry draw, integrates homogeneous Rayleigh airlight, and runs the
scene's final composition. `PointSurface`, `PointDepth`, and `PointAirlight` are
all full resolution. There are no shadow maps, cone lights, filaments, filtering,
temporal accumulation, or half-resolution rendering in this backend.

The host recognizes only the existing normalized `Air.Density/Scattering/Absorption`
medium controls and `View.ShowDepth`. The scene owns the names and meaning of its
geometry and light controls. The shared CPU-generated `UnshadowedAirlightLookup`
is renderer-owned and reused across v4/v5, including its coefficient cache and
quadrature fallback while a table is pending. Point-integral math is shared with
v4; v4 retains its shadow and other existing features.

## Validation and profiling

The GPU tests exercise STB isolation/stereo, shader counts and positions, capacity
clamping, silence, LUT quadrature agreement, edits, resize, reload, and scene switching.
Run the profiling tool with `--point-lights` for 1280x720 individual-ring and full
scene captures, per-pass GPU medians, and a v4 point-only cost comparison:

```powershell
dotnet run --project tools/AEther.MandelLightProfile -- --point-lights
```

Results and shader snapshots are written to `artifacts/mandellight-v5`. The v4
comparison uses two 90-light rings versus v5's three 60-light rings, both at full
resolution with 128 cubes. It compares cost rather than image equality.

On Intel Arc Graphics, the 2026-10-28 full-resolution 720p capture measured
these GPU medians over 41 frames after LUT warmup:

| Pass | v5, 3x60 points | v4, 2x90 points |
| --- | ---: | ---: |
| Light generation | 0.010 ms | CPU-generated |
| Geometry preparation | 0.002 ms | 0.004 ms |
| Surfaces | 3.172 ms | 6.040 ms |
| Airlight | 9.491 ms | 14.859 ms |
| Composition | 1.451 ms | 0.503 ms |
| Total including output | 14.508 ms | 21.701 ms |

These are different ring layouts and input mixtures, so the measurements are
an illustrative cost comparison. Captures and exact settings are in
`artifacts/mandellight-v5/timings.json`; the combined capture is `stb.png`.
