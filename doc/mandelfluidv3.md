# MandelFluidv3

V3 is a cloud scene: real fluid carries colored pigment away from a dark
Mandelbox slice, and musical transients strobe the clouds from its center.
The core occludes the fluid behind it. There is no separate procedural cloud
layer, comet tail, ray layer or illuminated empty background.

## Music and lighting

- **Local S** emits frequency-colored material from the actual fractal boundary.
  Stereo determines its placement. Separated tonal bands strengthen shear.
- **Local B** also emits and stirs material, controlled by **Fluid.Background**.
  It preserves local spectral and stereo contrast.
- **Global B** supplies passage-wide flow, curl and confinement, even when local
  tonal contrast is weak. It also strengthens the small ambient illumination.
- **Global S** drives core evolution and strengthens emitted tonal pigment.
- **Local attacks and positive global T** light existing clouds with a central
  strobe. The spectral attack envelope has a 45 ms release. Illumination falls
  off with distance from the core and responds to the actual density gradient.
  All pigment colors can be revealed by any hit, including old clouds returning
  through the periodic boundary. Residence age does not weaken a new flash.
  Negative global T eases expansion; transients never inject pigment.
- **Global interaction** strengthens turbulent motion, confinement and optional
  mixing. It is spectral/time interaction, not a noise classifier.

Every light term requires transported pigment. After capture stops or packets
become silent, emission stops and the existing fluid decays naturally to black.
The onset envelope releases independently of pigment lifetime.

## Controls

The **Comet** group has been removed. Core size, depth travel and material response
are part of this scene's design. **Geometry** still controls the Mandelbox field;
**Quality** controls antialiasing, maximum iterations and musical movement speed.

| Control | Purpose | Default |
|---|---|---:|
| Fluid.Flow | Outward transport; translating current in periodic mode | 0.089 |
| Fluid.HalfLife | Pigment lifetime; maps 0..1 to 0.1..10 seconds | 0.145 (1.5355 s) |
| Fluid.Diffusion | Optional pigment mixing | 0 |
| Fluid.Background | Local B emission and stirring sensitivity | 0.324 |
| Fluid.Curl | Vortex forcing and confinement | 0.243 |
| Fluid.PeriodicBoundary | Recirculate existing material through opposite edges | off |
| Lighting.Strobe | Central transient illumination strength | 0.7 |
| Lighting.Ambient | Restrained cloud visibility between hits | 0.008 |

Fluid defaults and **Geometry.Anisotropy = 0.526** follow the visible reference
settings. Ambient is deliberately small: the dominant light is the central
strobe. Setting Ambient to zero makes retained pigment dark between hits;
setting Strobe to zero leaves only the ambient cloud response.
Use **Reload**, then **Reset defaults** in the shader panel to adopt the new look
if an existing session has retained older parameter values.

## Fluid implementation

The emitter uses the same distance field and inverse display projection as the
dark core. Limited MacCormack advection transports pigment and velocity without
introducing new pigment extrema. Counter-rotating localized vortices roll sheets
into clouds. Diffusion is optional and starts disabled.

The v3 grid reaches 512 cells on its longest side. Five dye samples provide
opacity and density gradients for central lighting. Cloud shading uses no
fractal relief outside the silhouette, so visible cloud detail comes from the
simulation. Density-weighted residence age remains transported for diagnostics
and periodic transport checks; lighting no longer samples it.

Pressure projection uses the shared multigrid solver. With PeriodicBoundary on,
velocity, dye, age and pressure stencils wrap consistently. The carrier becomes
a music-driven translating current; expansion dilution and edge sinks are
disabled. Half-life decay remains active, and a minimum half-float quantum is
removed from subnormal pigment each fixed step to prevent immortal faint dye.
The checkbox can change live without clearing the simulation.

## Regression coverage

`ShaderSilenceTests` covers silent startup and natural decay after active music,
with stopped capture and silent packets, normal and +60 dB gain, and open and
periodic boundaries. It permits a generous decay period, then checks every RGB
channel for exact black across 60 final frames.

Focused fixtures check B-driven intensity and motion, local B/stereo emission,
Curl-driven vorticity and bounded pigment, central strobe activation of old dye,
frequency-independent illumination, global-only attacks, adjustable lighting,
unchanged simulation state during lighting edits, and darkness without pigment.
