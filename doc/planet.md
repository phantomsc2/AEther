# Planet

Select **Planet**, or launch with `--scene=Planet`. Planet shares exponential,
RGB atmospheric transport with [Atmosphere](atmosphere.md): two directional
suns, a normalized Rayleigh + HG phase function, and analytic planet occlusion. The
opaque planet and 1,000 procedural octahedra are black. There is no surface
lighting, emission, or meteor shadowing. Multiple scattering uses the Hillaire 2020 isotropic approximation.

## Default demonstration and controls

The stationary observer is at approximately 1.25 planet radii on +Z, looking at
the center with a 120-degree vertical FOV. The primary sun has azimuth 232 degrees, zero altitude, and an 8-degree angular radius, partially eclipsed by the planet;
the second is disabled. Random circular orbits fill a spherical shell and cross the shadow
behind the planet. Animation uses renderer elapsed time, independently of audio.
Random orbital poles, phases, sizes, orientations, and spin rates are deterministic. Every meteor center remains at exactly the selected altitude; orbital poles cover the full sphere, including prograde, retrograde, and near-polar orbits.

The solid radius R is 6,360 km. Float controls use the normalized [0,1] editor:

| Control | Physical mapping | Default |
|---|---|---|
| Camera.Long | 0 to 360 degrees | 0 |
| Camera.Lat | -89 to 89 degrees | 0 degrees |
| Camera.Alt | Center distance R * 8.82^value | 1.25R |
| Atmosphere.ScaleHeight | H = .1868R * 2^(6*(value-.5)) | value .18 |
| Atmosphere.Density | Density multiplier value^2.53951953 | value .5 |
| Atmosphere.RayleighScattering | Extinction .01 * RGB * density per km | (.201,.454,.687) |
| Camera.Yaw | Look yaw -180 to 180 degrees relative to center | 0 degrees |
| Camera.Pitch | Look pitch -90 to 90 degrees | 0 degrees |
| Camera.Fov | Vertical field of view 30 to 140 degrees | 120 degrees |
| Shell.Alt | Exact center altitude .002R to 2R above surface | .08R |
| Shell.Speed | Angular speed 0 to .2 radians/second | .08 |
| Shell.Size | Octahedron radius .005R to .04R, randomized by .6 to 1 | .007R before variation |
| Shell.Count | Rounded instance count 0 to 4096 | 1000 |
| Shell.Seed | Reproducible orbit/phase/size/spin distribution | .5 |

All controls are organized under Atmosphere, Camera, Debug, Shell, Sun, Sun2, and Transport.
Atmosphere.Phase contains Anisotropy and Shape (defaults .3 and .5). The other
atmosphere mappings are unchanged. Sun and Sun2 retain their azimuth, altitude,
and intensity controls. Camera.Alt retains the previous exponential center-distance
mapping; its physical height above the surface is R * (8.82^value - 1).

Meteor size is capped at .9 times the selected altitude to keep geometry above
the surface. There is no shell thickness or orbital inclination slider: randomized
orbital planes cover all inclinations at the same exact center altitude. Zero speed
freezes both orbit and spin. Camera and sun controls remain editable.

The old Orbit/View/Ring and top-level debug/phase parameter paths have been replaced
by the grouped controls. Atmosphere and sun settings with unchanged names survive
reload; renamed controls start at their new defaults.

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
aerial perspective. Shell motion and shell controls rebuild neither. Exposure and
preview toggles affect display only. Resources are released on deactivation;
reload failures retain the working effect.

Debug.Transmittance and Debug.SkyView retain precedence. Debug.Aerial displays a raw
volume slice selected by Debug.Slice; Debug.Transmission selects transmission
instead of radiance. Raw previews bypass exposure and tone mapping.

GPU markers separate Atmosphere transmittance, Atmosphere sky view, Atmosphere
aerial perspective, Planet meteors, and Scene. PlanetAerialCacheAndCutoutsAt720p
checks volume identities/bounds, monotonic transmission, motion/freezing, cache
invalidation, resize, and reload. PlanetAerialQualityAndTimingsAt720p compares
finite paths against independent double-precision quadrature, records 32/64-cubed
image differences, and captures orbital phases. Reports and RGBA captures are
written to ignored artifacts/planet-aerial at 1280x720. PlanetShellKeepsExactAltitudeInRandomOrbitalPlanes checks exact radius over time and altitude extremes, spherical coverage, and the parameter categories. Existing Planet tests
continue checking Sky-View, transmittance, limb seams, and physical extremes.

References: [Hillaire 2020](https://diglib.eg.org/items/8a3e5350-18b3-46bd-9274-3add5af88c75)
and [Frostbite 2016, section 3.5](https://media.contentapi.ea.com/content/dam/eacom/frostbite/files/s2016-pbs-frostbite-sky-clouds-new.pdf).

## Scattering orders

Both scenes expose **Transport** with independent switches:

- **Surface**: defaults off and has no rendering effect.
- **Single Scattering**: defaults on and controls only directly sunlit atmospheric scattering.
- **Multiple Scattering (approx)**: defaults on and controls the approximate higher-order atmospheric source. It remains meaningful with displayed L1 disabled: the LUT's internal seed is not gated by the display switches.

Attenuation and opaque geometry remain unchanged by these switches. L1+L2 equals
the sum of their separately rendered linear radiances. Both disabled yields black
radiance while preserving RGB transmission. There is no ground reflection in the
multiple-scattering builder; adding L0 later requires a separate implementation.

A new 32x32 RGBA32F LUT (16 KiB) stores the unit-source response versus local sun
zenith cosine and density-focused altitude. Following Hillaire 2020 equations
5-10, it integrates 64 equal-area sphere directions and 32 midpoint path samples,
then closes higher orders with M = B / (1-f), per RGB channel. B is the angular
mean of isotropic single-scattered radiance; f is the angular mean unit-radiance
scattering transfer. Both include atmospheric extinction and the opaque planet.
This is constant cost with respect to scattering order, not an exact solution.
The source construction follows the reference's isotropic phase approximation;
it is independent of the editable direct-light phase parameters.

The denominator is floored at 0.0001 for optically thick, nonabsorbing extremes.
This explicitly bounds amplification; those extremes are not physically validated.
The model still assumes spherical density and does not include meteor/terrain
shadows or atmospheric absorption. Strongly anisotropic higher orders are not
resolved. L2+ represents an isotropic source, rather than a directional light field.

The LUT is built after Transmittance and sampled inside the shared Sky-View and
aerial-perspective integrator. Each sun samples it at its own local zenith angle
and scales it by its intensity. Local direct-sun occlusion does not suppress the
indirect source. RGB transmission is unchanged. The LUT is rebuilt only when
medium properties change, and lazily if L2+ is disabled. Camera, sun, phase,
geometry, and transport toggles do not invalidate the LUT. Toggling L1/L2+ updates
Sky-View and aerial radiance; L0 performs no GPU cache work.

The new GPU marker is `Atmosphere multiple scattering`. Planet provides
Debug.MultipleScattering, and Atmosphere provides ShowMultipleScattering, for raw
LUT inspection independent of exposure and the L2+ display switch. These previews
also trigger construction of a stale LUT while L2+ is disabled.

AtmosphereScatteringOrdersAreIndependent checks additivity, independent toggles,
unchanged transmission, zero density, bounded extreme outputs, invalidation, and
reload for both scenes at 1280x720. AtmosphereMultipleLutMatchesIndependentClosure
compares sampled LUT nodes with independent double-precision dense-path integration.
Legacy numerical fixtures explicitly disable L2+ to continue isolating L1.

Reference: [Hillaire's implementation](https://github.com/sebh/UnrealEngineSkyAtmosphere/blob/master/Resources/RenderSkyRayMarching.hlsl), especially NewMultiScattCS and IntegrateScatteredLuminance.

## Scattering suns

Suns have no direct disk emission, angular radius, or Background Emission toggle.
Their bright cores come entirely from the HG scattering lobe. Disabling both
scattering orders, or setting density to zero, leaves no visible sun. Planet and
meteor cutouts retain their existing finite-path scattering and occlusion.

The shared phase controls are centered on g=0.95 and HG strength=0.288:

```text
g = 1 - 0.05^(2*Anisotropy)
strength = Strength^1.7958592832
p(c) = (Rayleigh(c) + strength*HG(c,g))/(1+strength)
```

Anisotropy zero is g=0; midpoint is g=0.95; maximum is g=0.9975.
Strength zero gives pure Rayleigh; midpoint is 0.288; maximum is 1.
The narrowest lobe is limited visually by Sky-View angular resolution.
GPU tests check the centered preset, phase normalization, extreme anisotropy,
and absence of direct emission at 1280x720. Default captures are written to
artifacts/scattering-suns.