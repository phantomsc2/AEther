# MandelFluidv2

A tumbling, morphing Mandelbox slice cuts every light layer to black. Spectral
transients briefly deform localized parts of its boundary, then return to the
ongoing shape without advancing its morph clock or pulsing its overall scale.

The direct Mandelbox slice supplies the detailed background and bends angular
lightning rays. Near the object, rays cling to coarse and fine fractal folds and
split into bright secondary branches. The background continues the distortion
through the wider wake. At the screen boundary, rays recover their frequency-coded
angle. Separate transient fronts expand beyond the viewport, fading steeply at
the edges and especially the corners.

Live and recorded noise control distortion strength and reach. A passage envelope
combines song energy and noise, building over 0.85 seconds and relaxing over 3.2
seconds. This gives distortion inertia without moving or zooming the whole camera.

## Cost and controls

The expensive five-depth volumetric reconstruction has been replaced in
this scene by a single advected fluid sheet. Three dye samples supply soft pigment,
density relief and a small local background refraction. Existing fractal relief
adds wisps without new orbit or procedural noise evaluations. The fluid grid stays
at most 256 by 256, independent of window size. Tail and fluid controls are restored.
Local deformation skips
spectral/history reads outside the object's influence. The background filter
reuses its center sample instead of fetching it twice.

Defaults: size 1, morph 0.726, tail 0.679, shockwaves 1 and background 0.472. Antialiasing is
off by default; enabling it shades four samples per pixel.

- **Comet.Size** controls apparent object size.
- **Comet.Morph** controls depth traversal.
- **Comet.Tail** controls soft wake illumination.
- **Fluid** controls flow, half-life, diffusion, noise and curl.
- **Comet.Shockwaves** controls flashes and fronts.
- **Comet.Background** controls the direct slice and fine structure.
- **Geometry** retains scale, Julia blend, fold rotation, anisotropy and slice depth.
- **Quality** controls antialiasing, iteration depth and movement speed.

All lighting follows spectral evidence and decays to black without music. The
scene remains included in `ShaderSilenceTests`, which allows 10,000 decay frames
before checking every RGB channel across 60 final frames, for both missing packets
and explicit silent packets.
