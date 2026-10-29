# Procedural raster geometry

RasterGeometry submits one non-indexed triangle-list DrawInstanced call.
The scene declares default (vertices per instance, instance count) in
`uint2 Parameters.Sizes.Geometry`; the backend owns the live counts.
See [resource sizes](resource-sizes.md).

Include Common/RasterGeometry.hlsl and implement:

```hlsl
GeometryVertex EvaluateGeometry(uint vertex, uint instance);
float4 GeometryProject(float3 world);
```

RasterLight supplies GeometryProject through the shared shader camera.
GeometryVertex contains float3 World, Normal and Albedo. The vertex wrapper
projects each vertex and forwards flat normal/albedo values. Positions, normals,
colors and animation remain shader-defined.

There is no count preparation dispatch, indirect argument buffer, count readback,
vertex buffer or index buffer. Zero vertices or instances draw nothing. Supply a
multiple of three vertices for complete triangles. MandelLight defaults to
36 vertices per cube and 128 instances.

Each stage binds resources independently through reflection. Parameter layouts
merge by path across stages; conflicting types/defaults are errors. Counts are
user-controlled setup values; vertex behavior can still use frame/audio inputs.
