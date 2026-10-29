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

## Medium

Implement two callbacks in scene HLSL:

```hlsl
float3 AirScatter();    // Homogeneous scattering coefficients, nonnegative.
float3 AirExtinction(); // Scattering + absorption coefficients, nonnegative.
```

The callbacks can use arbitrary reflected parameters, frame/audio inputs or
constants. C# has no reserved medium parameter names and does not read coefficients.
MandelLightv5 keeps its own Air controls and density mapping inside its callbacks.

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
| `RasterLookupState` | GPU coefficient key and rebuild flag |

Surface lighting is Lambertian: albedo/pi times cosine, inverse-square point
intensity and medium transmission from light to surface. There is no ambient
floor, shadow-map allocation, spot/filament lighting or reconstruction filter.

`AirlightLookup.hlsl` builds a 513x1025 FP32 RGB tail table on the GPU.
A one-thread preparation pass compares requested extinction with the completed
table's key. An 8x8-thread compute pass integrates each texel independently using
64 panels of four-point Gauss quadrature, then a one-thread pass commits the key.
Unchanged coefficients skip texel integration. Construction completes before
shading in the same command stream; no CPU task, readback, cache or upload is
needed. Shader reload invalidates the key.

`PointAirlightIntegral.hlsl` shares coordinate definitions with the builder and
evaluates finite/infinite intervals, with collinear handling and direct quadrature
outside the table domain. The atmosphere extends to infinity for missing geometry;
camera clipping does not truncate it. Other volume prototypes retain their own
heterogeneous sampling.

Resources bind by per-stage reflection, not fixed scene slots. Unknown resources
fail compilation. Shader reload preserves working programs on failure. Settings
used only in secondary stages still need exposure through the scene parameter
layout; general cross-stage parameter schema merging is not provided.
