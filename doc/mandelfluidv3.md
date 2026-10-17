# MandelFluidv3

Select **MandelFluidv3** in the scene picker. The center keeps v2's black
Mandelbox slice, with faster tumbling and depth morphing during music.

The sustained/transient roles are reversed:

- **Transients** illuminate all retained material of the matching frequency,
  wherever the fluid has carried it, with an exponential age envelope. The
  fixed dye color selects the current spectrum, with stereo blended so each
  transient reaches the whole matching color population. An 80% spectral
  response and 20% smooth strobe response multiply the dyed material's body
  and wet highlights. Illumination blends exponentially from the flash to a
  quiet ambient intensity as material ages. **Lighting.FadeTime** maps 0..1
  to a decay time of 0.05..4 seconds; default 0.1 gives 0.445 seconds, about
  4.5 times faster than before. **Lighting.Ambient** sets the background floor
  (default 0.04); zero restores a fade to darkness. Ambient light requires
  decaying dye, and transients cannot light empty space. The history buffer does
  not limit how old material can be when a new hit illuminates it.
  A soft density mask shapes wispy edges without diffusing the simulation.
  Emission runs from bass below to treble above. Left and right emission stays
  distinct at the sides and blends smoothly to mono at both vertical poles.
- **Sustained notes** inject colored liquid into a persistent fluid simulation.
  Separated tonal bands stir each other's streams more strongly than a lone note.
  Transients do not inject dye or directly force the simulation.
- **Background B** increases both local vortex forcing and the intensity of
  material emitted from the matching spectral/stereo region of the fractal.
  It can emit liquid on its own and strengthens sustained-note emission where
  they coincide. **Fluid.Background** controls the sensitivity of both effects.

The fluid emits from the actual Mandelbox boundary: injection evaluates the
same distance field, cutout threshold and inverse display projection as the
black silhouette. A filtered band resolves the emitter at the simulation's
resolution. The simulation carries its liquid outward; the silhouette
occludes material passing behind it.

V3 uses limited MacCormack advection for velocity and pigment, with correctly
positioned face velocity samples and reduced velocity damping. This reduces
the numerical blur that remained even with diffusion at zero. Counter-rotating
localized vortices roll streams into spirals, and separated tonal bands
strengthen the forcing. The Curl control adjusts both this forcing and
vorticity confinement. The bounded correction adds two transport passes per
step and keeps pigment nonnegative without introducing new extrema.

Density-weighted age travels alongside pigment through the same corrected
advection passes. New emission mixes in young material; existing material ages
in seconds, including time dropped from motion during short frame stalls.
Diffusion, dilution, decay and edge losses preserve a mass-weighted mean age
when streams mix. This age blends current transient illumination toward ambient;
it does not select historical audio or change fluid forcing. Three float32 RG
surfaces add 6 MiB at 512 by 512, without additional transport draws.

The shared multigrid solver uses the correct Neumann boundary diagonal,
three pre/post smoothing iterations, a more complete coarse solve and three
V-cycles. GPU fixtures over landscape, portrait, square and wide domains leave
under 1% of the original RMS divergence while preserving discrete curl. The
V3 fluid grid reaches 512 by 512 (other scenes retain their 256 cap); five dye
taps supply body and resolution-independent wet highlights. Pressure precision,
smoothing counts and V-cycle counts are unchanged. The larger grid uses roughly
four times the surface memory and fine-grid work.

**Fluid.Flow** sets physical expansion, **HalfLife** pigment decay, **Diffusion**
mixing, **Background** B-driven emission and stirring, and **Curl** vortex strength and confinement.
**Comet.Tail** sets material response, **Shockwaves** transient illumination,
**Background** transient brightness, **Size** silhouette size and **Morph** depth
travel. Geometry and quality controls retain their v2 meanings.

Fluid defaults follow the reference settings: flow 0.12, half-life 1 (10 seconds),
diffusion 0, background 0.069 and curl 0.543.

Higher Flow stretches the rotating sheets into long outward plumes. Curl
controls their roll-up. Diffusion is disabled by default; the optional control
still permits deliberate pigment mixing.

The musical and wall clocks retain their existing meanings; the role swap is
between sustained and transient audio components. Every light term requires
dyed material; current transients supply the flash and aging dye supplies the
ambient afterglow. Missing audio stops injection,
and the scene is automatically covered by `ShaderSilenceTests` at normal and high input
gain, including startup, interrupted input, and explicit silent packets. The
regression allows a generous decay and then checks every RGB channel over 60
final frames.

Additional GPU regressions check that Curl increases measured vorticity, pigment
stays finite and bounded, current transients illuminate old pigment at the
matching frequency, lighting leaves fluid state unchanged, and the cloud
can go dark between hits with ambient disabled. Age checks cover elapsed
time, configurable exponential attenuation measured before gamma and tone
mapping, and the adjustable ambient floor.
