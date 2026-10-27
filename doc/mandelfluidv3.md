# MandelFluidv3

V3 composites transported colored pigment behind a dark Mandelbox core. Musical
transients illuminate the cloud from the center. `Lighting.Strobe`,
`Lighting.Ambient` and `Lighting.SpatialFalloff` control this scene's light;
geometry, antialiasing and movement remain independent of the simulation.

The simulation is the [shared v4 ring fluid](ring-fluid.md). It no longer injects
pigment along the fractal silhouette or uses a separate expanding carrier and
vortex forcing. Local S emits pigment at the ring; local B supplies radial and
counter-rotating launch velocity. B and T cannot create material. V3 projects
this common fluid around its animated core for shading.

`Emitter` and `Fluid` have the same controls and defaults as v4. The former
`Flow`, `HalfLife`, `Diffusion` and `Background` controls are removed. Material
uses v4's FP32 signal/density layout and density-weighted birth timestamp and
frequency, rather than V3's residence-age/density layout. Pigment signal fades
on the history time scale, even in a closed periodic domain.

The GPU regressions compare V3's complete material, metadata and velocity state
against v4, check S/B/T separation, pressure projection, periodic transport and
silence, and verify that central strobes only change lighting.
