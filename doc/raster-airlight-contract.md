# Raster surfaces and homogeneous airlight

`Common/RasterAirlight.hlsl` and `RasterLightTransport` provide the shared
surface/shadow/airlight pipeline used by MandelLight v4. The integral is a
renderer feature independent of how the scene constructs its
[procedural geometry](geometry-contract.md).

The shared HLSL contains Lambertian surface lighting, point/spot shadow lookup,
infinite homogeneous single scattering, the unshadowed-light LUT path, filament
integration, and depth-guided airlight reconstruction. The scene supplies its
geometry callbacks, camera, parameters, and final `PS` composition/display
mapping. The shared airlight include supplies `GeometryProject`,
`RasterSurfacePS`, and `RasterAirlightPS`; include `RasterGeometry.hlsl` first.

```hlsl
ProjectionCamera SceneCamera();
```

Return the shader-owned camera from the [shared projection contract](projection-contract.md).
Defaults are vertical focal scale two, near distance 0.025, and far distance 128.
The same camera defines raster projection, rays, and surface-depth reconstruction.
Background airlight still integrates to infinity, independently of the far plane.

## Settings contract

The scene declares these groups inside `cbuffer Parameters`. Group/member names
are reserved by this backend; struct type names are free. Floats are normalized
controls in `[0,1]`, matching the existing parameter UI. No additional validation
or alternate transport path is introduced here.

| Group | Required members |
| --- | --- |
| `Air` | `float Density; float3 Scattering; float3 Absorption;` |
| `Lights` | `bool Enabled; float Count; float Gain; float RingRadius; bool Directional; float HalfAngle; bool Shadows; float Spread;` |
| `TonalLights` | `bool Enabled; float Gain; bool Directional; float HalfAngle; bool Shadows;` |
| `RingLight` | `float Intensity;` |
| `Quality` | `float Samples; float ShadowResolution; float ShadowBias; bool HardwarePCF; bool HalfResolutionAirlight;` |
| `Sampling` | `float OpticalDepthWeight; bool Jitter; bool RandomLight;` |
| `Filter` | `bool Enabled;` |
| `View` | `bool ShowDepth; bool ShowShadows; float ShadowLight;` |

The scene can add members, such as camera and exposure controls. There are no
required geometry settings. In particular, `Shape` has no host meaning.

The fixed homogeneous-medium mapping is:

```text
s = 0.3 * Air.Density^2 * Air.Scattering
a = 0.3 * Air.Density^2 * Air.Absorption
e = s + a
```

The GPU integrator and CPU LUT builder use the same coefficients. The LUT is
built/cached by the host, with the existing quadrature path during a pending
build. Medium coefficients therefore remain host-visible settings in this
version; arbitrary GPU-computed medium coefficients are outside this contract.

## Light setup and resources

The current host light adapter remains the spectral-ring implementation:

- Up to 96 transient and 96 tonal point/spot lights, with independent shadows.
- One unshadowed filament ring.
- Live CPU spectra determine radiance and compact active-light lists each frame.
- Both point-light rings and the filament share radius
  `(2 + 10 * Lights.Spread) * Lights.RingRadius`. This is independent of geometry
  spread; the default remains 5.7 world units.

The adapter uploads `RasterLightConstants` at `b3`; the shared include defines
the packed positions, intensities, active lists, cones, shadow projections, and
filament records. Moving light generation into scene HLSL is a separate change;
this geometry contract does not claim GPU ownership of light setup.

The host supplies `RasterPassConstants` at `b2` for camera/shadow selection and
`RasterAirlightConstants` at `b5` for LUT readiness/layout. It allocates and binds:

| Resource | Meaning |
| --- | --- |
| `RasterShadows` | Guarded spot/cube depth-array slices |
| `RasterSurface` | Full-resolution unattenuated surface radiance, RGBA32F |
| `RasterDepth` | Full-resolution projected camera depth, cleared to one |
| `RasterAirlight` | Full/half-resolution scattering RGB and depth guide |
| `RasterAirlightLut` | Host-built unshadowed point-light integral table |
| `RasterShadowSampler`, `RasterLutSampler` | Comparison and LUT sampling |

The final `PS` opts into this combined raster backend by sampling
`RasterSurface`. It also composes `RasterAirlight` as needed. Each frame the host
prepares geometry once, renders required active/preview shadow views, renders
surfaces, integrates airlight, then runs the scene's final `PS`. Depth/shadow
diagnostic modes skip airlight integration. Shadow geometry refreshes each frame;
there is no scene-specific geometry cache key.

This separates the reusable integral from MandelLight's cube construction while
retaining the existing light adapter and normalized medium controls as explicit
parts of the initial airlight contract.
