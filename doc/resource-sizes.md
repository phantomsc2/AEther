# Effect resource sizes and ownership

Scene setup in `SceneDefinition.Defaults` constructs effects explicitly. Each effect
owns its compiled passes, reflected shader parameters, C# allocation options,
resource bindings and persistent GPU storage. Resource names are local to a pass;
two instances can bind different textures under the same HLSL name.

```csharp
new("MandelFluid", (device, context, source) =>
    [new FluidEffect("Fluid", device, context, source, "MandelFluid.hlsl",
        new FluidOptions(), Format.R32G32B32A32_Float, Format.R32G32_Float)])
```

The factory declares the material attachments and their order. Reflection validates
the compiled outputs; it does not select the effect or choose storage formats.
Current scenes contain one effect. Scene drawing executes the declared sequence in
order, with a local binding dictionary for each instance. Shared inputs are the
frame constant buffer and audio textures/sampler. Effect outputs do not enter the
renderer's shared input dictionary.

A scene definition can supply an ordinary C# draw callback to order updates and
draws explicitly, including drawing an effect with a different `EffectFrame.Target`.
The default callback draws its effects in declaration order. No dependency graph
or scheduling language is involved.

## Allocation options

Allocation defaults and live values are typed C# options, outside HLSL Parameters.
`FluidEffect.Options` accepts `FluidOptions`; `RasterEffect.Options` accepts
`RasterOptions`. Records are replaced atomically and read once per frame.

`FroxelEffect.Options` accepts `FroxelOptions`: DepthSlices is 8..256 (default 64),
PointLights is 0..64 (default four). Width and height always follow the canvas,
rounded upward after division by eight. See [Froxel1](froxel1.md).

`Froxel2Effect.DepthOptions` accepts `Froxel2Options` with only DepthSlices
(8..256, default 86). It shares the volume allocator but has no point-light or
surface resources. Spectrum width supplies the ring's arc count automatically.

`Froxel3Effect.CloudOptions` accepts `Froxel3Options`: DepthSlices 8..256 (default
86), Instances 0..65535 (default 1024), and LightingArcs 4/8/16/32 (default 8).
The grid follows the canvas at 8x reduction per axis; surface color and depth
follow full canvas resolution. Its two-row spectrum prefix texture follows
audio width automatically. See [Froxel3](froxel3.md).

| Option | Meaning/default |
| --- | --- |
| Fluid Width/Height | Velocity, material and pressure grid; 512x512 |
| Raster PointLights | Authoritative light record count; 180 |
| Raster Vertices/Instances | Direct DrawInstanced counts; 36 and 128 |
| Raster SurfaceWidth/Height | Color and matching depth; zero follows canvas |
| Raster AirWidth/Height | Airlight image; zero follows canvas |
| Raster LookupWidth/Height | Integral LUT; zero follows canvas |

MandelLight supplies explicit LUT defaults 513x1025. Explicit texture axes are
1..16384, or 2..16384 for the LUT. Fluid axes must be independent powers of two
from 16 through 2048. Zero light or geometry counts disable corresponding work.
Zero lights still allocate one internal dummy record. Lights are limited to
64*65535 by the preparation dispatch; the product of geometry counts must fit uint.

The shared C# options editor generates integer controls from type/range metadata,
with power-of-two fluid sliders and adaptive ranges for counts. Numeric entry can
extend slider ranges; pointer changes commit on release. Ordinary HLSL bool and
normalized float parameters retain reflected controls and source defaults.

## GPU interface and lifecycle

Raster HLSL reads `PointLightCount` from `RasterConstants`, supplied by the effect.
Geometry counts stay on the CPU. Shaders query bound texture dimensions. HLSL no
longer declares a Sizes struct or allocation defaults.

Each effect snapshots shader parameters at a frame boundary. Allocation changes
replace only its affected light buffer, fluid simulation, LUT, surface/depth pair,
or airlight target. Geometry edits change draw submission without a vertex buffer.
Ordinary parameters preserve storage. Implicit targets follow canvas resizing;
explicit fluid storage survives changes that preserve physical domain aspect.
Replacement allocation precedes disposal. Invalid or failed edits restore accepted
options and retain working resources, with diagnostics in the shader panel.

Deactivation releases simulation and raster storage while retaining options and
parameter values. Reactivation starts fresh simulation state. Successful reload
replaces programs and resets the affected runtime resources; failed compilation
preserves the working instance. Settings are retained by scene and effect name,
with shader values restored by path/type. They survive compatible reloads and
renderer restarts within the process, but are not persisted between app launches.
