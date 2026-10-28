# Procedural raster geometry

`RasterGeometry` executes one non-indexed, instanced triangle-list draw. HLSL
owns both counts and all vertex construction. There is no CPU geometry count,
vertex buffer, index buffer, count readback, or reserved `Shape` parameter group.
Consumers are the shared [raster airlight transport](raster-airlight-contract.md)
and the separate [procedural point-light pass](mandellight-v5.md).

Include `Common/RasterGeometry.hlsl` and implement:

```hlsl
uint2 GeometryCount(); // (vertices per instance, instances)
GeometryVertex EvaluateGeometry(uint vertex, uint instance);
float4 GeometryProject(float3 world); // homogeneous clip position
```

Both airlight consumers project through the [shared shader-owned camera](projection-contract.md).

`GeometryVertex` contains `float3 World`, `Normal`, and `Albedo`. Normals are
world-space unit vectors. The shared vertex wrapper outputs world position and
flat, noninterpolated normal/albedo for the surface shader. It does not interpret
object identity or connectivity. A scene may construct unrelated triangles,
several solids, patches, or repeated objects from the two IDs.

The include provides two entry points:

- `PrepareGeometry`, a one-thread compute shader, writes all four uints of the
  16-byte `GeometryDrawArguments` raw UAV at `u0`: vertex count per instance,
  instance count, start vertex (zero), start instance (zero).
- `RasterGeometryVS` evaluates each vertex and projects it through the consumer's
  `GeometryProject` callback.

The host dispatches `(1,1,1)` once per frame, unbinds the UAV, and calls
`DrawInstancedIndirect` for each requested view using the same argument buffer.
Zero instances or zero vertices draw nothing. For triangle lists, supply a
multiple of three vertices. All instances in this draw have the same vertex
count; scenes can flatten variable geometry into one instance when appropriate.
No mesh storage or arbitrary working-buffer allocation is introduced by this contract.

Each compute, vertex, and pixel entry point has its own reflected parameter and
resource bindings. Geometry callbacks may use `Parameters`, `FrameConstants`,
and the existing spectrum/history textures and samplers. Counts can depend on
frame or audio inputs just like positions. All views in a frame must evaluate
the same world geometry; projection is the view-dependent operation.

MandelLight v4 implements `(36, 1 + round(4095 * Shape.Count))`, or zero
instances when disabled. Its seed, cube topology, transforms and material
variation are entirely scene HLSL. Changing to three tetrahedrons per instance,
or changing the vertex count, requires only editing the scene shader.

The airlight consumer supplies camera/light projection, surface shading, depth
targets, and shadow views. Its enabled shadow maps refresh each frame; it does
not infer static geometry or cache validity from scene parameter names.
