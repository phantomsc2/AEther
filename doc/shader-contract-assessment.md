# Rendering module contracts

SceneDefinition registers explicit C# setup for all six scenes. Scenes own named
effect instances; each owns its programs, parameter values, allocation options,
bindings and persistent GPU resources. The renderer owns shared device/audio/
output infrastructure. Pass ordering is ordinary C# control flow.

| Area | Contract |
| --- | --- |
| Scene lifecycle | Explicit registration, lazy compilation, transactional reload |
| FluidEffect | Explicit formats and grid; HLSL boundary and material evolution |
| RasterEffect | CPU draw/count options; HLSL geometry, lights, camera and medium |
| ShaderProgram | Reflected ABI and parameter defaults; explicit local bindings |
| Projection | Shared HLSL camera, rays, depth and reconstruction |
| Output | Linear/display tags, FP16 intermediate, shared exposure and export |

Reflection validates configured interfaces; it does not select modules, formats,
resource ownership or update schedules. Independently compiled effects can reuse
HLSL names without sharing resources. Includes share algorithms, not instances.
There is no automatic scheduling graph or global compiled-program cache.

See [ownership and allocation](resource-sizes.md), [raster](raster-airlight-contract.md),
[geometry](geometry-contract.md), [fluid](fluid-contract.md) and
[shader authoring](shader-authoring.md).
