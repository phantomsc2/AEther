# Rendering module contracts

The central modules are FluidSimulation, RasterGeometry, Projection and RasterLight.
AirlightLookup and PointAirlightIntegral support RasterLight; Inputs and
Postprocessing/Present define frame/audio inputs and output presentation.

| Area | Current contract | Remaining boundary |
| --- | --- | --- |
| Scene lifecycle | Discovered HLSL PS, transactional reload, reflected controls | Fixed capability discovery and pass order |
| FluidSimulation | One solver; shader boundary and reflected material outputs | One instance, fixed grid/timestep/multigrid policy |
| RasterGeometry | GPU counts, indirect instanced triangles, stage reflection | One procedural draw, no mesh asset loader |
| Projection | Shader camera, rays, depth and reconstruction | Shared conventions must be used consistently |
| RasterLight | GPU point lights, camera surface/depth, Lambertian BRDF, integral LUT | 512-light limit; shader-defined medium callbacks |
| Output | Linear/display tags, shared exposure/tone curve/export | Fixed FP16 intermediate and presentation contract |

Concrete scenes can define geometry, camera, point-light counts/properties,
fluid payloads and final composition in HLSL without rebuilding the host.
Resource management and pass scheduling remain host responsibilities.

The final PS's compiled resource bindings activate the known backend pass families.
Schedules and fixed buffer layouts are common-module contracts. Secondary-stage
parameter unions and cross-module dependency discovery are not implemented.
Medium coefficients, LUT construction and invalidation are shader-owned; C# only
allocates resources and submits passes. No scene is selected implicitly.

See [raster contract](raster-airlight-contract.md), [geometry](geometry-contract.md),
[fluid](fluid-contract.md), [projection](projection-contract.md) and
[shader authoring](shader-authoring.md).
