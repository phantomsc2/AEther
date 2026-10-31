# Atmosphere

Select **Atmosphere** in the scene picker, or launch with `--scene=Atmosphere`.
The scene is independent of audio and time. The observer stays at ground level
on a planet with radius 6,360 km. The atmosphere is spherical; directions below
the flat local horizon are black. There is no ground mesh or ground reflection.
One white directional sun illuminates the atmosphere, without a visible disk.

## Physical model and controls

The model includes molecular Rayleigh single scattering only. Molecular density
decreases exponentially with height. Scattering equals extinction: there are no
aerosols, ozone, absorption, clouds, multiple scattering, or scene shadow maps.
Analytic planet occlusion prevents sunlight from passing through the planet and
allows the upper atmosphere to remain lit after sunset. Missing multiple scattering
and aerosols limit the realism of twilight, haze, and the solar glow.

All shader floats use the existing normalized [0,1] editor. The mappings below
are applied in HLSL; there are no changes to the shared settings UI.

| Control | Physical mapping | Default |
|---|---|---|
| `Atmosphere.RayleighScattering` | Each RGB component: 0 to 0.1 / km | (0.0058, 0.0135, 0.0331) / km |
| `Atmosphere.ScaleHeight` | 1 to 16 km | 8 km |
| `Atmosphere.Height` | 20 to 160 km above ground | 100 km |
| `Sun.Azimuth` | 0 to 360 degrees | 0 degrees |
| `Sun.Altitude` | -90 to 90 degrees | 30 degrees |
| `Sun.Intensity` | 0 to 40 relative white irradiance | 20 |
| `View.Azimuth` | 0 to 360 degrees | 0 degrees |
| `View.Altitude` | -90 to 90 degrees | 10 degrees |
| `ShowTransmittance` | Display the raw Transmittance LUT | Off |
| `ShowSkyView` | Display the raw Sky-View LUT | Off |

The vector editor labels RGB as x/y/z. +Y is up; zero azimuth faces +Z and
90 degrees faces +X. The vertical field of view is fixed at 60 degrees. Looking
straight up or down remains well defined. Intensity is relative to the renderer's
linear illumination scale, not calibrated solar irradiance in W/m2. Global
Graphics Exposure controls the displayed sky through the shared presentation pass.

## LUTs and data flow

The effect owns two RGBA32F textures, requiring 768 KiB combined, with no mipmaps
or temporal accumulation. RGB contains the transport result; alpha is one.

**Transmittance (256 x 64)** stores attenuation from a point to the atmosphere
boundary. Its axes use Bruneton's boundary-distance parameterization. Horizontal
position runs from the shortest outward path to the longest path tangent to the
ground. Vertical position is proportional to `sqrt(r*r-R*R)`, from ground to the
atmosphere top; `r` is radial distance and `R` is planet radius. At altitude this
includes downward rays that miss the planet. Ground-intersecting sun rays are
rejected analytically before lookup. Generation uses 256 midpoint samples.

**Sky-View (256 x 128)** stores radiance for the ground observer and current sun
altitude/intensity. Azimuth spans 0 to 360 degrees relative to the sun, with both
seam endpoints stored. The top row is zenith and the bottom row is the horizon:

```text
e = (pi/2) * (1-v)^2

e: elevation above the horizon
v: vertical table coordinate, 0 at the top and 1 at the bottom
```

This puts more samples near the horizon. Both LUTs map parameter endpoints to
texel centers and use clamped bilinear filtering. Sky generation integrates 128
quadratically spaced path segments, evaluating density and sunlight at segment
midpoints. Each segment uses the analytic constant-coefficient scattering integral
and Beer-Lambert camera attenuation. Sun attenuation comes from Transmittance;
there is no nested sunlight integration. Stable small-optical-depth expressions
preserve the zero-scattering limit and avoid cancellation in thin segments.

The fullscreen shader reconstructs a perspective ray, rotates it into the sun's
azimuth frame, and samples Sky-View. It performs no integration or density evaluation.
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
| Sun altitude or intensity | Build Sky-View only |
| Sun azimuth, view direction, exposure, debug toggles, or canvas size | Display only |

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
They also check bounds, symmetry, seam continuity, view poles, zero scattering,
parameter extremes, raw display/exposure behavior, cache invalidation, reset,
resize, switching, and reload rollback.

`AtmosphereTimingAndCapturesAt720p` measures initial construction, parameter-driven
updates, and cached display separately using GPU timestamps at 1280x720 on the
repository-selected adapter. It writes `timings.txt` and RGBA8 captures for day,
sunset, twilight, night, and both raw LUTs to ignored `artifacts/atmosphere`.
Readbacks bound the GPU queue; their CPU duration is excluded from GPU timings.

Measured on Intel(R) Arc(TM) Graphics at 1280x720 (2026-10-31):

| GPU stage | Initial build | Update mean / p95 |
|---|---|---|
| Transmittance | 0.1020 ms | 0.0729 / 0.1014 ms |
| Sky-View | 0.6173 ms | 0.5800 / 1.4501 ms |
| Cached fullscreen sky | n/a | 0.1204 / 0.1248 ms |

Construction measurements exclude shader compilation. Cached display statistics
use the last 120 frames after 180 unchanged frames; update statistics exclude the
initial build. These stage timings exclude presentation and readback, and vary
with GPU clock and other work. The shader-authoring rendering checks passed;
the combined smoke harness subsequently crashed during WinUI startup
(`Microsoft.UI.Xaml.dll`, exception `0xc000027b`), so its window checks were not
completed in this run.

References: [Hillaire's SIGGRAPH 2020 slides](https://blog.selfshadow.com/publications/s2020-shading-course/hillaire/s2020_pbs_hillaire_slides.pdf)
and [Bruneton and Neyret, Precomputed Atmospheric Scattering](https://scispace.com/pdf/precomputed-atmospheric-scattering-4wluja3fvt.pdf).
