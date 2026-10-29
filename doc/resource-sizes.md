# Scene resource sizes

Scenes declare defaults in a struct named `Sizes` inside `cbuffer Parameters`.
C# owns live values, allocation, validation and workload submission.

```hlsl
struct ResourceSizes { uint PointLights; uint2 Geometry; uint2 RasterAirlightLut; };
cbuffer Parameters
{
    ResourceSizes Sizes = { 180, uint2(36,128), uint2(513,1025) };
    float Brightness = .5;
};
```

The struct type name is free. Nested members can share names with global
resources: `Sizes.PointLights` and `PointLights` are distinct identifiers.
Direct cbuffer members have global scope and cannot reuse resource names.

| Field | Meaning | When omitted |
| --- | --- | --- |
| `uint PointLights` | Total light records, shared by read/write views | Required for raster lighting |
| `uint2 Geometry` | Vertices per instance, instance count | Required for raster geometry |
| `uint2 FluidVelocity` | Shared grid for velocity, materials and pressure hierarchy | Canvas dimensions |
| `uint2 RasterSurface` | Surface and matching depth dimensions | Canvas dimensions |
| `uint2 RasterAirlight` | Airlight image dimensions | Canvas dimensions |
| `uint2 RasterAirlightLut` | Integral table dimensions | Canvas dimensions |

MandelFluid explicitly declares a 512x512 FluidVelocity grid. Explicit fluid axes
must be independent powers of two from 16 through 2048. MandelLight defaults to
180 lights, geometry (36,128), and LUT (513,1025). Zero light or geometry counts
disable the corresponding work. A zero-light allocation has one internal dummy
element; its logical count remains zero. The backend supports up to 64*65535
lights, bounded by one preparation dispatch, and a uint total geometry vertex
count. Texture axes must be 1..16384, or 2..16384 for the LUT. Allocation can
still fail when memory is unavailable; values are never silently clamped.

Reflection merges parameter paths and defaults across compiled stages; conflicts
are errors. UInt controls belong in Sizes. Resource bindings and formats remain
those of the existing modules; arbitrary resource names are not allocated.
Unused Sizes members survive while their Parameters cbuffer is used by a stage;
a completely unused cbuffer is optimized away.

The generated UI uses integer controls, with power-of-two fluid sliders. Numeric
entry can extend initial slider ranges. Pointer edits commit on release. Reset,
reload and scene changes follow existing parameter default/restoration rules.

Each frame uses one snapshot for CPU allocation and shader bindings. Changes
recreate only the affected light buffer, fluid simulation, LUT, surface/depth
pair, or airlight target. Geometry changes only update direct CPU draw counts.
Ordinary parameters preserve storage. Canvas resizing affects implicit targets;
explicit fluid storage survives unless the physical domain aspect changes.
Replacements are allocated before working resources are released. Invalid/failed
edits restore the last working sizes and report a renderer error. Audio inputs,
fixed internal scratch layouts and pass ordering remain backend-owned.
