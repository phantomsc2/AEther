# MandelLight: shader-defined point lights

Select **MandelLight** after building the host. The scene combines a homogeneous atmosphere and procedural cube cloud with three fixed perpendicular rings of point lights:
S in XZ, T in XY, and B in YZ. Each ring samples its corresponding live
`Spectrum.rgb` component, using the angular frequency palette and stereo blend.
The shader samples row zero at UV.y=.25 in the two-row spectrum textures,
keeping diagnostic data out of the light intensity.
Intensity is linear in the component, with no additional history or ambient floor.
Silence renders black. The rings stay fixed while the camera and cubes can move.

Defaults are 128 cubes, 60 lights per ring, radius 5.7, and shared gain 4x.
Sizes.PointLights is the total count, default 180; the shader balances it across
the three rings. Sizes.Geometry defaults to (36,128). Zero counts disable lights
or geometry. Lights.Radius maps to 0..10 world units and Lights.Gain to
2^(12*value-6). Geometry, atmosphere and camera remain shader-defined.
See [resource sizes](resource-sizes.md). Exposure is the shared Graphics
setting in stops; the former default -0.6-stop scene gain is a fixed shader constant.

## Point-light contract

See the [RasterGeometry/RasterLight contract](raster-airlight-contract.md).
