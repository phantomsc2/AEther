# Planet

Select **Planet**, or launch with `--scene=Planet`. Planet shares exponential,
RGB single-scattering transport with [Atmosphere](atmosphere.md): two directional
suns, a normalized Draine phase function, and analytic planet occlusion. The
opaque planet and 1,000 procedural octahedra are black. There is no surface
lighting, emission, multiple scattering, or meteor shadowing.

## Default demonstration and controls

The stationary observer is at approximately 1.25 planet radii on +Z, looking at
the center with a 120-degree vertical FOV. One stationary sun points along +X;
the second is disabled. One tilted toroidal stream orbits through the shadow
on the -X side. Animation uses renderer elapsed time, independently of audio.
Random phases, offsets, sizes, orientations, and spin rates are deterministic.

The solid radius R is 6,360 km. Float controls use the normalized [0,1] editor:

| Control | Physical mapping | Default |
|---|---|---|
| Orbit.Azimuth | 0 to 360 degrees | 0 |
| Orbit.Elevation | -89 to 89 degrees | 0 degrees |
| Orbit.Altitude | Center distance R * 8.82^value | 1.25R |
| Atmosphere.ScaleHeight | H = .1868R * 2^(6*(value-.5)) | value .18 |
| Atmosphere.Density | Density multiplier value^2.53951953 | value .5 |
| Atmosphere.RayleighScattering | Extinction .01 * RGB * density per km | (.201,.454,.687) |
| Ring.Radius | Centerline radius 1.02R to 3R | 1.08R |
| Ring.Speed | Angular speed 0 to .2 radians/second | .08 |
| Ring.Width | Tube radius 0 to .15R | .015R |
| Ring.Inclination | Tilt 0 to 90 degrees | 20 degrees |
| Ring.Size | Octahedron radius .005R to .04R, randomized by .6 to 1 | .007R before variation |

Tube radius and meteor size are each capped at .45 times the centerline's
height above the surface, preventing intersections even at extreme controls.
Zero speed freezes both orbit and spin. Camera and sun controls remain editable.
Sun and phase mappings match Atmosphere. Phase defaults are anisotropy .3 and
shape .5. View yaw/pitch default to .5 (toward center).

## Transport and lookup tables

Density is exp(-h/H), numerically truncated at 10H without tapering. Scattering
equals extinction. Transmittance uses 256x64 RGBA32F, 128 midpoint segments,
boundary-distance U, and density-focused altitude V. Sky-View uses 256x128
RGBA32F and 64 midpoint segments. Its separate disk/annulus bands duplicate the
solid limb to prevent interpolation across different path endpoints.

The aerial cache contains two **32x32x32 RGBA16F** volumes (512 KiB combined):
cumulative RGB radiance A and transmission T. XY nodes span the viewport;
Z nodes span camera-forward depth quadratically, including identity at zero.
The far bound is camera-center distance plus numerical atmosphere radius.
Each compute thread integrates one XY column through successive depth intervals,
using four midpoint segments per interval. Integration clips against atmosphere
entry/exit and the first planet intersection. Empty paths preserve A=0, T=1;
values remain constant after path termination. Texture lookups map endpoints to
texel centers and use clamped trilinear filtering. No temporal filtering is used.

The full-resolution instanced geometry pass writes meteor depth. Composition
chooses the nearest meteor or analytic planet endpoint. Visible meteors sample
A at their depth; since their surface radiance is zero, the result is A alone.
T is retained for numerical checks and future surface lighting. Unobstructed sky
and the black planet disk use Sky-View. Keeping its limb-specific mapping avoids
the sharp-limb blur observed when resolving the disk through a coarse volume.
The caches describe the same transport; scattering is added only once.

The coarse volume approximates angular/depth variation, particularly near the
planet shadow and limb. Meteor shadows are deliberately absent. A black meteor
changes appearance through foreground scattering, not through surface shading.

## Updates, previews, and verification

Atmosphere changes rebuild all tables. Suns, phase, or orbit position rebuild
Sky-View and aerial perspective. View direction or aspect changes rebuild only
aerial perspective. Ring motion and ring controls rebuild neither. Exposure and
preview toggles affect display only. Resources are released on deactivation;
reload failures retain the working effect.

ShowTransmittance and ShowSkyView retain precedence. ShowAerial displays a raw
volume slice selected by AerialSlice; ShowAerialTransmission selects transmission
instead of radiance. Raw previews bypass exposure and tone mapping.

GPU markers separate Atmosphere transmittance, Atmosphere sky view, Atmosphere
aerial perspective, Planet meteors, and Scene. PlanetAerialCacheAndCutoutsAt720p
checks volume identities/bounds, monotonic transmission, motion/freezing, cache
invalidation, resize, and reload. PlanetAerialQualityAndTimingsAt720p compares
finite paths against independent double-precision quadrature, records 32/64-cubed
image differences, and captures orbital phases. Reports and RGBA captures are
written to ignored artifacts/planet-aerial at 1280x720. Existing Planet tests
continue checking Sky-View, transmittance, limb seams, and physical extremes.

References: [Hillaire 2020](https://diglib.eg.org/items/8a3e5350-18b3-46bd-9274-3add5af88c75)
and [Frostbite 2016, section 3.5](https://media.contentapi.ea.com/content/dam/eacom/frostbite/files/s2016-pbs-frostbite-sky-clouds-new.pdf).
