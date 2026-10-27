# MandelFluid

MandelFluid combines the Mandelbox slice, spectral rays and transient fronts with
transported pigment. Its geometry, antialiasing and movement controls remain in
`Geometry` and `Quality`.

All four MandelFluid scenes use the [shared v4 ring fluid](ring-fluid.md), including
the same emitter, FP32 material, birth/frequency metadata, pressure solve and
update cadence. Local S emits material, B controls launch velocity and curl, and
T supplies scene lighting. The older flow, half-life, diffusion and background
controls have been replaced by v4's `Emitter` and `Fluid` controls. Material fades
on the audio history time scale.

Scene shading remains distinct: this version lights the Mandelbox folds with
transported pigment. Every production scene must return to black after music
stops; `ShaderSilenceTests` checks both stopped capture and explicit silent rows.
