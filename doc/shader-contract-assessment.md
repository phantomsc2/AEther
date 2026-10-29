# Scene contracts after raster consolidation

The renderer now has one fluid backend and one procedural raster/light backend.
MandelLightv4 and its CPU light adapter, shadow maps, spot/filament transport,
half-resolution reconstruction and specialized profiling paths have been removed.

| Area | Current contract | Remaining boundary |
| --- | --- | --- |
| Scene lifecycle | Discovered HLSL PS, transactional reload, reflected controls | Fixed capability discovery and pass order |
| Fluid | One solver; shader boundary and reflected material outputs | One instance, fixed grid/timestep/multigrid policy |
| Geometry | GPU counts, indirect instanced triangles, stage reflection | One procedural draw, no mesh asset loader |
| Projection | Shader camera, rays, depth and reconstruction | Shared conventions must be used consistently |
| RasterLight | GPU point lights, camera surface/depth, Lambertian BRDF, integral LUT | 512-light limit; validated host-visible Air and depth-diagnostic settings |
| Output | Linear/display tags, shared exposure/tone curve/export | Fixed FP16 intermediate and presentation contract |

The fluid extraction (6500394), procedural geometry extraction (83673dd),
GPU point-light scene (6b4cab1), shared projection (eb210a8), and shared
postprocessing (34f4867) established the reusable contracts. The light sampling
experiment was reverted (c2681bb). Raster consolidation keeps the v5 feature set
under RasterGeometry/RasterLight and removes the competing v4 pipeline.

Concrete scenes can define geometry, camera, point-light counts/properties,
fluid payloads and final composition in HLSL without rebuilding the host.
Resource management and pass scheduling remain host responsibilities.

Next boundaries worth addressing are parameter schemas across shader stages,
artistic motion clocks that still depend on fluid capability, and capability
composition when a concrete scene requires dependencies beyond the fixed raster,
fluid, final-PS and presentation order. Medium/LUT setup now lives in RasterLight;
Renderer no longer reads Air fields. CPU/GPU coefficient mapping remains an
explicit shared contract because CPU-built lookup tables need coefficient keys.

See [raster contract](raster-airlight-contract.md), [geometry](geometry-contract.md),
[fluid](fluid-contract.md), [projection](projection-contract.md) and
[shader authoring](shader-authoring.md).
