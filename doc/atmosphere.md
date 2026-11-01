# Atmosphere

Select **Atmosphere** in the scene picker, or launch with `--scene=Atmosphere`.
The scene is independent of audio and time. The observer stays at ground level
on a planet with radius 6,360 km. The atmosphere is spherical; directions below
the flat local horizon are black. There is no ground mesh or ground reflection.
Two independently controlled white directional suns illuminate the atmosphere,
without visible disks. Setting either sun's intensity to zero disables it.

For an orbital observer, a black solid sphere, and an orbiting stream of black octahedra, select the
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

The model uses single scattering with a normalized Draine phase function and
Rayleigh-derived RGB scattering coefficients. Density decreases exponentially
with height. Integration stops at 10 scale heights (80 km by default), where
density is approximately 0.0000454 of surface density; the remaining tail is
discarded. There is no outer taper or separate height control.
Scattering equals extinction: there are no
aerosols, ozone, absorption, clouds, multiple scattering, or scene shadow maps.
Analytic planet occlusion prevents sunlight from passing through the planet and
allows the upper atmosphere to remain lit after sunset. Missing multiple scattering
and a separate aerosol population limit atmospheric realism. Away from the
Rayleigh phase defaults this is a generalized scattering medium, rather than a
pure molecular Rayleigh model.

All shader floats use the existing normalized [0,1] editor. The mappings below
are applied in HLSL; there are no changes to the shared settings UI.

| Control | Physical mapping | Default |
|---|---|---|
| `Atmosphere.RayleighScattering` | Each RGB component: 0 to 0.1 / km | (0.0058, 0.0135, 0.0331) / km |
| `Atmosphere.ScaleHeight` | 1 to 16 km | 8 km |
| `Atmosphere.Density` | Scattering/extinction density multiplier, 0 to 16 | 1 (slider 0.0625) |
| `Sun.Azimuth` | 0 to 360 degrees | 0 degrees |
| `Sun.Altitude` | -90 to 90 degrees | 30 degrees |
| `Sun.Intensity` | 0 to 40 relative white irradiance | 20 |
| `Sun2.Azimuth` | 0 to 360 degrees | 180 degrees |
| `Sun2.Altitude` | -90 to 90 degrees | 20 degrees |
| `Sun2.Intensity` | 0 to 40 relative white irradiance | 10 |
| `View.Azimuth` | 0 to 360 degrees | 0 degrees |
| `View.Altitude` | -90 to 90 degrees | 10 degrees |
| `Phase.Anisotropy` | Draine g: -0.95 to 0.95 | 0 (slider 0.5) |
| `Phase.Shape` | Draine a: 0 to 2 | 1 (slider 0.5) |
| `ShowTransmittance` | Display the raw Transmittance LUT | Off |
| `ShowSkyView` | Display the raw Sky-View LUT | Off |

The vector editor labels RGB as x/y/z. +Y is up; zero azimuth faces +Z and
90 degrees faces +X. The vertical field of view is fixed at 120 degrees. Looking
straight up or down remains well defined. Intensity is relative to the renderer's
linear illumination scale, not calibrated solar irradiance in W/m2. Global
Graphics Exposure controls the displayed sky through the shared presentation pass.

Both suns share the medium's phase parameters. Positive g produces a forward
lobe, negative g a backward lobe; limiting its magnitude to 0.95 avoids the
singularity at magnitude one. Shape a=0 gives Henyey-Greenstein, a=1 gives
Cornette-Shanks. The defaults g=0, a=1 reproduce the previous Rayleigh angular
response; g=0, a=0 is isotropic. For example, slider Anisotropy approximately
0.921053 gives g=0.8. High g concentrates energy in a narrow lobe, whose sharpness
is ultimately limited by the Sky-View texture resolution.

```text
c = dot(view direction, direction to sun)
q = 1 + g*g - 2*g*c
p(c) = (1-g*g)*(1+a*c*c) / (4*pi*(1+a*(1+2*g*g)/3)*q^(3/2))
```

The function integrates to one over solid angle. Coefficients are calculated
once per Sky-View texel and the phase is evaluated once per sun outside the
segment loop, using reciprocal square root rather than a general power.

## LUTs and data flow

The effect owns two RGBA32F textures, requiring 768 KiB combined, with no mipmaps
or temporal accumulation. RGB contains the transport result; alpha is one.

**Transmittance (256 x 64)** stores attenuation from a point to the atmosphere
boundary. Its horizontal axis uses a boundary-distance parameterization. Horizontal
position runs from the shortest outward path to the longest path tangent to the
ground. Vertical position is `sqrt((h/(h+H))*(11/10))`, where h is altitude and H is scale height. This concentrates rows in dense material, while still reaching the numerical top at 10H. At altitude this
includes downward rays that miss the planet. Ground-intersecting sun rays are
rejected analytically before lookup. Generation uses 128 uniform midpoint segments.

**Sky-View (256 x 128)** stores the summed radiance from both suns for the ground
observer. Azimuth spans world directions from 0 to 360 degrees, with fixed +Z
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
and Beer-Lambert camera attenuation. Each sun has its own Draine phase evaluation and
planet occlusion evaluation. Their intensity-weighted incident radiance is added
within each segment; density, optical depth, and camera attenuation are shared.
Zero-intensity suns skip their sunlight evaluation. Sun attenuation comes from Transmittance;
there is no nested sunlight integration. Stable small-optical-depth expressions
preserve the zero-scattering limit and avoid cancellation in thin segments.

Both LUTs use equal-length distance intervals and midpoint samples. The segment
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
| First frame, successful reload, or scene reactivation | Build both LUTs |
| Any `Atmosphere` parameter | Build Transmittance, then Sky-View |
| Azimuth, altitude, or intensity of either sun | Build Sky-View only |
| Either `Phase` parameter | Build Sky-View only |
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
azimuths/altitudes, and verify additivity, swapped/coincident suns, independent
planet occlusion, disabled suns, and rotational consistency with the camera.
They also check bounds, symmetry, seam continuity, view poles, zero scattering,
parameter extremes, raw display/exposure behavior, cache invalidation, reset,
resize, switching, and reload rollback.
GPU phase tests compare angular values with the analytic formula and integrate
them numerically over solid angle to verify normalization, including isotropic,
Rayleigh, HG, Cornette-Shanks, and the supported forward/backward extremes.

`AtmosphereTimingAndCapturesAt720p` measures initial construction, parameter-driven
updates, and cached display separately using GPU timestamps at 1280x720 on the
repository-selected adapter, separately with one and two enabled suns. It writes
`timings-one-sun.txt`, `timings-two-suns.txt`, and RGBA8 captures for the two-sun
default, single-sun day/sunset/twilight, night, and both raw LUTs to ignored
`artifacts/atmosphere`. The second sun is disabled for the single-sun captures;
both default suns are restored for raw LUT captures.
An additional `draine-forward-lobe` capture uses g=0.8, a=1 with a single sun.
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
