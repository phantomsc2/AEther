# Atmosphere

Select **Atmosphere** in the scene picker, or launch with `--scene=Atmosphere`.
The scene is independent of audio and time. The observer stays at ground level
on a planet with radius 6,360 km. The atmosphere is spherical; directions below
the flat local horizon are black. There is no ground mesh or ground reflection.
A white directional sun illuminates the atmosphere,
without visible disks. Setting the sun's intensity to zero disables it.

For an orbital observer, a black solid sphere, and an orbiting shell of black octahedra, select the
separate [Planet scene](planet.md). Both scenes share finite-path transport and
the Transmittance builder; their camera, physical scale, control mappings, and
Sky-View mappings differ. Planet additionally caches finite-path aerial perspective in a 32-cubed frustum volume for its meteor cutouts. Planet uses an exponential gas envelope with finite truncation;
this scene retains its ground-level exponential profile and kilometre-scale controls.

Each scene declares its own controls, defaults, and mappings from normalized
values to physical scale height and scattering. `Common/AtmosphereTypes.hlsl`
provides shared settings types and constants; `Common/Atmosphere.hlsl` provides
the shared transport and Transmittance builder using the mappings supplied by
the including scene. Ground and planet Sky-View coordinate mappings remain in
their respective scene shaders.

## Physical model and controls

The model uses direct single scattering plus approximate multiple scattering with a normalized Rayleigh + HG phase function and
Rayleigh-derived RGB scattering coefficients. Density decreases exponentially
with height. Integration stops at 10 scale heights (160 km by default), where
density is approximately 0.0000454 of surface density; the remaining tail is
discarded. There is no outer taper or separate height control.
Scattering equals extinction: there are no
aerosols, ozone, absorption, clouds, or scene shadow maps.
Analytic planet occlusion prevents sunlight from passing through the planet and
allows the upper atmosphere to remain lit after sunset. The isotropic multiple-scattering approximation and lack of a separate aerosol population limit atmospheric realism. Away from the
Rayleigh phase defaults this is a generalized scattering medium, rather than a
pure molecular Rayleigh model.

All shader floats use the existing normalized [0,1] editor. The mappings below
are applied in HLSL; there are no changes to the shared settings UI.

| Control | Physical mapping | Default |
|---|---|---|
| `Atmosphere.RayleighScattering` | RGB power curves, 0 to 0.1 / km | (0.0049, 0.0135, 0.049) / km at midpoint |
| `Atmosphere.ScaleHeight` | 16 * 2^(8*(slider-0.5)) km: 1 to 256 km | 16 km at midpoint |
| `Atmosphere.Density` | 16 * slider^4.0831412353 | 0.944 at midpoint |
| `Sun.Azimuth` | 0 to 360 degrees | 54 degrees |
| `Sun.Altitude` | -90 to 90 degrees | 6.48 degrees |
| `Sun.Intensity` | 0 to 40 relative white irradiance | 33.08 |
| `View.Azimuth` | 0 to 360 degrees | 80.28 degrees |
| `View.Altitude` | -90 to 90 degrees | 6.48 degrees |
| `Phase.Anisotropy` | HG g: 0 to 0.9975, logarithmic peak width | 0.95 at midpoint |
| `Phase.Strength` | HG strength: 0 to 1, power curve | 0.288 at midpoint |
| `ShowTransmittance` | Display the raw Transmittance LUT | Off |
| `ShowSkyView` | Display the raw Sky-View LUT | Off |

The vector editor labels RGB as x/y/z. +Y is up; zero azimuth faces +Z and
90 degrees faces +X. The vertical field of view is fixed at 120 degrees. Looking
straight up or down remains well defined. Intensity is relative to the renderer's
linear illumination scale, not calibrated solar irradiance in W/m2. Global
Graphics Exposure controls the displayed sky through the shared presentation pass.

The sun uses the phase parameters. Rayleigh is always present. Strength zero
gives pure Rayleigh; increasing strength adds HG while preserving total energy.
The midpoint preset reproduces the reference sunset: the former RGB controls
(0.049, 0.135, 0.490), density 0.059, scale height 16 km, g=0.95, strength=0.288.
The new RGB curves use exponents (4.3510744405, 2.8889686876, 1.0291463457),
preserving zero and the original maximum per channel. The density curve also
preserves vacuum and its original maximum.

```text
c = dot(view direction, direction to sun)
q = (1-g)^2 + 2*g*(1-c)
Rayleigh(c) = 3*(1+c*c)/(16*pi)
HG(c,g) = (1-g*g)/(4*pi*q^(3/2))
p(c) = (Rayleigh(c) + strength*HG(c,g))/(1+strength)
```
The function integrates to one over solid angle. Coefficients are calculated
once per display pixel and the phase is evaluated once per sun after
transport interpolation, using reciprocal square root rather than a general power.

## LUTs and data flow

The effect owns four RGBA32F textures, requiring 1296 KiB combined, with no mipmaps
or temporal accumulation. RGB contains the transport result; alpha is one.

**Transmittance (256 x 64)** stores attenuation from a point to the atmosphere
boundary. Its horizontal axis uses a boundary-distance parameterization. Horizontal
position runs from the shortest outward path to the longest path tangent to the
ground. Vertical position is `sqrt((h/(h+H))*(11/10))`, where h is altitude and H is scale height. This concentrates rows in dense material, while still reaching the numerical top at 10H. At altitude this
includes downward rays that miss the planet. Ground-intersecting sun rays are
rejected analytically before lookup. Generation uses 128 uniform midpoint segments.

**Sky-View (256 x 128)** stores two separate RGBA32F textures: phase-free
single scattering and multiple-scattering radiance.
The shared raymarch computes all components for the ground observer. Azimuth spans world directions from 0 to 360 degrees, with fixed +Z
(0 degrees) as the reference and both seam endpoints stored. The top row is
zenith and the bottom row is the horizon:

```text
e = (pi/2) * (1-v)^2

e: elevation above the horizon
v: vertical table coordinate, 0 at the top and 1 at the bottom
```

This puts more samples near the horizon. Both LUTs map parameter endpoints to
texel centers and use clamped bilinear filtering. Sky generation integrates 64
uniformly spaced path segments, evaluating density and sunlight at segment
midpoints. Each segment uses the analytic constant-coefficient scattering integral
and Beer-Lambert camera attenuation. The sun has its own planet occlusion evaluation. Intensity-weighted single
scattering and higher orders are stored in separate textures. Density, optical depth, and camera attenuation are shared.
Zero sun intensity skips sunlight evaluation. Sun attenuation comes from Transmittance;
there is no nested sunlight integration. Stable small-optical-depth expressions
preserve the zero-scattering limit and avoid cancellation in thin segments.

The LUT builders use equal-length distance intervals and midpoint samples. The segment
length is calculated once per ray; Sky-View also hoists scattering coefficient
times segment length and reuses segment transmission for scattering and camera
attenuation. Nonlinear path sampling and its toggle have been removed. The 10H
bound and LUT coordinate mappings remain independent of integration spacing.
See [Planet](planet.md) for the shared numerical model.

The fullscreen shader reconstructs a perspective ray in world space and samples
Sky-View once. It performs no integration or density evaluation.
Normal sky output uses `LinearOutput`, preserving HDR radiance for shared exposure
and tone mapping.

Each debug checkbox stretches its raw texture over the entire canvas, bypassing
exposure and tone mapping with `DisplayOutput`. Values are shown directly as display
RGB; radiance above one clips on SDR output. These are numeric LUT visualizations,
not normally exposed sky images. Transmittance takes precedence when both are on.

## Updates and lifecycle

| Change | GPU work |
|---|---|
| First frame, successful reload, or scene reactivation | Build Transmittance, Multiple Scattering, and Sky-View |
| Any `Atmosphere` parameter | Build Transmittance, then Multiple Scattering if enabled, then Sky-View |
| Azimuth, altitude, or intensity of either sun | Build Sky-View only |
| Either `Phase` parameter | Display only; all transport caches reused |
| View direction, exposure, debug toggles, or canvas size | Display only |

The render thread compares relevant normalized values from immutable parameter
snapshots. It unbinds SRVs/UAVs between compute and display passes. LUT storage is
released on deactivation, and compatible controls survive reload and scene switching.
A compilation failure preserves the working effect and textures.

## Verification

```powershell
dotnet build AEther.sln --no-restore
dotnet test AEther.Tests/AEther.Tests.csproj --no-restore --filter 'FullyQualifiedName~Atmosphere'
dotnet run --project tools/AEther.Smoke -- . --shader-authoring-only
```

The GPU tests compare LUT texels with independent double-precision direct
quadrature at sun altitudes 30, 0, and -3 degrees, including zenith and horizon.
Combined-sky tests compare with summed independent references at different sun
azimuths/altitudes, and verify planet occlusion, zero intensity, and rotational
consistency with the camera.
They also check bounds, symmetry, seam continuity, view poles, zero scattering,
parameter extremes, raw display/exposure behavior, cache invalidation, reset,
resize, switching, and reload rollback.
GPU phase tests compare angular values with the analytic formula and integrate
them numerically over solid angle to verify normalization, including isotropic,
the normalized Rayleigh + HG mixture, including the maximum forward peak.

`AtmosphereTimingAndCapturesAt720p` measures initial construction, parameter-driven
updates, and cached display separately using GPU timestamps at 1280x720 on the
repository-selected adapter. It writes `timings.txt` and RGBA8 captures for the
default, sunset, twilight, night, and raw LUT views to ignored
`artifacts/atmosphere`.
An additional `hg-forward-lobe` capture uses g=0.8 with a single sun.
Readbacks bound the GPU queue; their CPU duration is excluded from GPU timings.

`AtmosphereQualityTradeoffsAt720p("Atmosphere")` compares 64, 128, and 256 linear
Sky-View steps, with twice as many Transmittance steps, using identical LUT sizes
and mappings. It records warmed GPU timings and relative image RMS error against
256 steps for default and twilight views. This measures integration error at
fixed LUT resolution and extent. Independent 20H references also check the
removed tail against a 0.5% relative / 0.0005 absolute radiance budget. Reports
are written to `artifacts/atmosphere/quality-atmosphere-720p.txt`.

The linear-only 64/128-step configuration measured 0.4484 ms mean for Sky-View
and 0.0735 ms for Transmittance on Intel(R) Arc(TM) Graphics at 1280x720.
Relative image RMS error against 256/512 steps was 0.0684% for the default
view and 0.4090% for twilight. Timings vary with device clock and other activity.

Construction measurements exclude shader compilation. Cached display statistics
use the last 120 frames after 180 unchanged frames; update statistics exclude the
initial build. These stage timings exclude presentation and readback, and vary
with GPU clock and other work. During the original one-sun implementation, the
shader-authoring rendering checks passed; the combined smoke harness subsequently crashed during WinUI startup
(`Microsoft.UI.Xaml.dll`, exception `0xc000027b`), so its window checks were not
completed in this run.

References: [Hillaire's SIGGRAPH 2020 slides](https://blog.selfshadow.com/publications/s2020-shading-course/hillaire/s2020_pbs_hillaire_slides.pdf)
and [Bruneton and Neyret, Precomputed Atmospheric Scattering](https://scispace.com/pdf/precomputed-atmospheric-scattering-4wluja3fvt.pdf).

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
aerial-perspective integrator. The sun samples it at its own local zenith angle
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

## Scattering sun

The sun has no direct disk emission, angular radius, or Background Emission toggle.
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
## Phase evaluation at display resolution

Sky-View stores phase-free single scattering and isotropic multiple scattering separately. Planet aerial perspective uses
the same split in two RGBA16F volumes, alongside its transmission volume.
All components share one transport march at the existing resolutions and step counts.
The display interpolates transport first, then evaluates the exact Rayleigh + HG
phase for the sun using the actual pixel ray:

```text
radiance = singleSun * phase(dot(ray, sun)) + multiple
```

Intensity and transport toggles retain their existing rebuild semantics. Phase
edits rebuild neither Sky-View nor aerial perspective. Debug previews reconstruct
radiance using the represented LUT/volume direction and bypass exposure as before.
Meteor cutouts use the same per-pixel phase at their finite-path endpoint.
No filtering, blur, peak clamping, larger LUTs, or extra integration passes are added.
Default Planet texture storage is 2.016 MiB, including the shared transmittance
and multiple-scattering lookup tables. Removing the second light saves 0.75 MiB.
