# Raster geometry, point lights and airlight

`RasterGeometry` and `RasterLight` are the single procedural raster system.
MandelLightv5 uses it. Geometry, camera and point-light construction belong to
scene HLSL; C# owns allocations, bindings, dispatches and draws.

Include `Common/RasterGeometry.hlsl` followed by `Common/RasterLight.hlsl`.
Implement the callbacks described in [geometry-contract.md](geometry-contract.md)
and [projection-contract.md](projection-contract.md), plus:

```hlsl
uint LightCount();
PointLight EvaluateLight(uint index);
// PointLight: float3 Position; float3 Intensity;
```

The final PS requests the backend by sampling `RasterSurface`. It can also use
`RasterDepth` and `RasterAirlight`. No separate light backend or shadow capability
is selected. `PreparePointLights` writes up to 512 GPU-resident 24-byte records
and a count; the host dispatches eight groups of 64 with no count readback.
Geometry uses `PrepareGeometry` and `RasterGeometryVS`; camera surface and
integration entry points are `RasterSurfacePS` and `RasterAirlightPS`.

## Medium and diagnostics

Declare these fields inside `cbuffer Parameters` (struct type names are free):

```hlsl
struct AirSettings { float Density; float3 Scattering; float3 Absorption; };
struct ViewSettings { bool ShowDepth; };
```

Name the corresponding variables `Air` and `View`. Additional members are free.
RasterLight validates these names and types at compilation and binds their
locations once. `View.ShowDepth` skips the integration pass; the scene PS renders
its diagnostic using `DisplayOutput`. Medium coefficients are:

```text
s = 0.3 * Air.Density^2 * Air.Scattering
e = s + 0.3 * Air.Density^2 * Air.Absorption
```

## Rendering and resources

Each frame the backend prepares the medium LUT, computes lights and indirect
geometry arguments, renders full-resolution camera surface/depth, then integrates
full-resolution airlight. The final scene PS composes the two linear contributions
and returns `LinearOutput`. The surface contribution must be attenuated along the
camera ray using the same extinction and depth distance.

| Resource | Contents |
| --- | --- |
| `RasterSurface` | RGBA32F unattenuated surface radiance |
| `RasterDepth` | R32F camera depth, cleared to one |
| `RasterAirlight` | RGBA32F homogeneous single-scattering radiance |
| `PointLights`, `PointLightCount` | GPU point records and raw count |
| `RasterAirlightLut`, `RasterLutSampler` | Shared RGB integral table and sampler |
| `RasterAirlightConstants` | Table readiness and coordinate mapping |

Surface lighting is Lambertian: albedo/pi times cosine, inverse-square point
intensity and medium transmission from light to surface. There is no ambient
floor, shadow-map allocation, spot/filament lighting or reconstruction filter.

`UnshadowedAirlightLookup` builds a 513x1025 RGB tail table on the CPU, keyed by
extinction, with four cached tables. Upload remains on the render thread.
`PointAirlightIntegral.hlsl` evaluates finite/infinite ray intervals from it,
with collinear handling and quadrature while a matching table is unavailable or
outside its range. The atmosphere extends to infinity for missing geometry;
camera near/far clipping does not truncate it. This is one shared integral/LUT
implementation. Other volume prototypes retain their own heterogeneous sampling.

Resources bind by per-stage reflection, not fixed scene slots. Unknown resources
fail compilation. Shader reload preserves working programs on failure. Settings
used only in secondary stages still need exposure through the scene parameter
layout; general cross-stage parameter schema merging is not provided.
