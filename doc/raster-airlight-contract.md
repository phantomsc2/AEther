# Raster geometry, point lights and airlight

`RasterEffect` owns the procedural raster system and its `RasterLight` backend.
MandelLight uses it. Geometry, camera and point-light construction belong to
scene HLSL; C# owns allocations, bindings, dispatches and draws.

Include `Common/RasterGeometry.hlsl` followed by `Common/RasterLight.hlsl`.
Implement the callbacks described in [geometry-contract.md](geometry-contract.md)
and [projection-contract.md](projection-contract.md), plus:

```hlsl
PointLight EvaluateLight(uint index);
// PointLight: float3 Position; float3 Intensity;
```

C# scene setup explicitly constructs the raster effect. Its final PS can sample
`RasterSurface`, `RasterDepth` and `RasterAirlight`. RasterOptions.PointLights is
the authoritative count, uploaded as `PointLightCount` in `RasterConstants`.
PreparePointLights writes 24-byte records in groups of 64. The CPU submits geometry
using RasterOptions.Vertices/Instances. Surface and integration entry points are
RasterSurfacePS and RasterAirlightPS. See [resource sizes](resource-sizes.md).

## Medium

Implement two callbacks in scene HLSL:

```hlsl
float3 AirScatter();    // Homogeneous scattering coefficients, nonnegative.
float3 AirExtinction(); // Scattering + absorption coefficients, nonnegative.
```

The callbacks can use arbitrary reflected parameters, frame/audio inputs or
constants. C# has no reserved medium parameter names and does not read coefficients.
MandelLight keeps its own Air controls and density mapping inside its callbacks.

## Rendering and resources

Each frame the backend prepares the medium LUT, computes lights, renders camera surface/depth, then integrates
airlight. Image dimensions default to canvas size and can be overridden. The final scene PS composes the two linear contributions
and returns `LinearOutput`. The surface contribution must be attenuated along the
camera ray using the same extinction and depth distance.

| Resource | Contents |
| --- | --- |
| `RasterSurface` | RGBA32F unattenuated surface radiance |
| `RasterDepth` | R32F camera depth, cleared to one |
| `RasterAirlight` | RGBA32F homogeneous single-scattering radiance |
| `PointLights` | GPU point records; count comes from C# options |
| `RasterAirlightLut`, `RasterLutSampler` | Shared RGB integral table and sampler |
| `RasterLookupState` | GPU coefficient key and rebuild flag |

Surface lighting is Lambertian: albedo/pi times cosine, inverse-square point
intensity and medium transmission from light to surface. There is no ambient
floor, shadow-map allocation, spot/filament lighting or reconstruction filter.

`AirlightLookup.hlsl` builds an FP32 RGB tail table (MandelLight defaults to 513x1025) on the GPU.
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
fail compilation. Shader reload preserves working programs on failure. Parameter layouts are merged across compiled stages.
