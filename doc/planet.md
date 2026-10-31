# Planet

Select **Planet** in the scene picker, or launch with `--scene=Planet`.
Both Planet and ground-level [Atmosphere](atmosphere.md) use shared exponential
single-scattering transport, two white directional suns, a normalized Draine
phase function, and two cached RGBA32F LUTs. Planet has an invisible opaque sphere
of radius R = 6,360 km. It blocks light and terminates camera rays; there is no
reflected surface, mesh, emission, bloom, or audio dependency.

## Medium and numerical integration

Density is a plain exponential without a taper or display fade:

```text
h = altitude above the solid sphere
H = scale height
rho(h) = exp(-h/H)
h_max = 10H
```

The model has an exponential tail. Computation discards material beyond 10 scale
heights, where density is exp(-10), approximately 0.0000454 of surface density.
This internal numerical limit replaces the former planet-size-based height
control. It is not an atmosphere-density parameter or a rigorous radiance error
bound. At the default H = 0.55R, the numerical sphere extends to 6.5R from the
center; the former limit was 2.8R. No brightness compensation is applied.

Both LUT builders use equal-length ray-distance intervals with midpoint samples:
128 segments for Transmittance and 64 for Sky-View. Segment length and scattering
coefficient times segment length are calculated once per ray. Sky-View reuses
each segment's exponential transmission for both scattering and attenuation.
There is no sampling-mode control or nonlinear path-distribution setup.

Camera attenuation uses Beer-Lambert transmission and an analytic
constant-coefficient scattering contribution within each segment. Each sun has a
separate analytic body-occlusion test and Transmittance lookup. LUT coordinate
mappings still concentrate texels around dense material and the limb; this is
independent of the uniform spacing of integration samples along each ray.

Ray intervals start at the camera if it is inside the numerical sphere, or at the
near entry if outside. They end at the far exit or the first opaque surface hit.
All material behind the final integration endpoint is discarded.

## Camera and controls

Vertical FOV is 120 degrees. The default observer is approximately 3.87R from the
center on +Z, looking toward the center. Orbit distance is independent of H and
the numerical limit. The observer always stays outside the solid body, but may
be inside the numerical atmosphere volume. Shader floats use normalized [0,1].

| Control | Mapping | Default |
|---|---|---|
| `Orbit.Azimuth` | Position azimuth, 0 to 360 degrees | 0 degrees |
| `Orbit.Elevation` | Position elevation, -89 to 89 degrees | 0 degrees |
| `Orbit.Altitude` | Radius `2.94R * 3^value` | Approximately 3.87R (value 0.25) |
| `View.Azimuth` | Look yaw relative to center, -180 to 180 degrees | 0 degrees (value 0.5) |
| `View.Altitude` | Look pitch, -90 to 90 degrees | 0 degrees (value 0.5) |
| `Atmosphere.Density` | Base density multiplier, 0 to 0.1 | 0.015 (value 0.15) |
| `Atmosphere.ScaleHeight` | Exponential scale height, 0.1R to 1.5R | 0.55R / 3,498 km |

Sun, Sun2, Phase, scattering RGB, and raw LUT previews have the same meanings as
in Atmosphere. Existing matching controls survive reload; `Atmosphere.Height`
has been removed from both scenes. Reset loads complete defaults.

## LUT coordinates and final rendering

Planet uses Transmittance 256x64 and Sky-View 128x64, totaling 384 KiB.
Ground Atmosphere retains its 256x128 Sky-View. Instances own separate resources.

Transmittance stores point-to-numerical-boundary attenuation. It retains the
boundary-distance U mapping, with the top set to R + 10H. U spans
outward to ground-tangent paths. V concentrates altitude rows near dense material:
`v = sqrt((h/(h+H)) * (11/10))`. Its inverse is
`q = v*v*(10/11); h = H*q/(1-q)`. Halfway down the table is about 0.29H,
rather than spending those rows on the dilute outer tail. Analytic
planet occlusion rejects blocked sun rays before lookup.

Planet Sky-View stores atmospheric RGB radiance summed over both suns, alpha one,
for every direction around the observer:

- U runs around the direction to the planet center, with duplicated seam endpoints.
  Zero azimuth points along the orbital camera's right vector.
- Rows 0 through 31 cover the opaque disk, center to solid limb, concentrating
  rows near the limb with `angle = limb * (1-(1-v)^2)`.
- Rows 32 through 63 cover the remaining directions, limb to directly away from
  the center, with a rational mapping that adapts to H and observer distance. Half the
  non-disk rows reach the projected altitude 2H, or the tangent hemisphere
  when the observer is inside that region. The remaining rows cover the tail
  and the rest of the sphere.

The solid limb is duplicated, and lookup stays within the appropriate disk or
non-disk row band to avoid filtering across different path endpoints. A ray that
misses the numerical volume contributes zero during LUT generation. There is no
outer-sphere rejection in the final shader: it performs an analytic body test
and one Sky-View lookup, with no path integration. Looking away from the planet
can show local scattering when the observer lies within the diffuse medium.

## Updates and verification

| Change | LUT work |
|---|---|
| Atmosphere properties | Rebuild both |
| Orbit position, either sun, or phase | Rebuild Sky-View only |
| Look direction, exposure, canvas size, or raw previews | Display only |
| First use, reload, or reactivation | Build both |

```powershell
dotnet build AEther.sln --no-restore
dotnet test AEther.Tests/AEther.Tests.csproj --no-restore --filter 'FullyQualifiedName~Planet|FullyQualifiedName~Atmosphere'
```

GPU tests compare disk, grazing, outward, and tail paths against independent
double-precision dense quadrature, including cameras inside and outside the
numerical limit and scale-height extremes. References extending to 20H check the
discarded tail against a 0.5% relative or 0.0005 absolute radiance budget. The
absolute floor permits a larger relative change in faint tails: at the default
observer, outward blue radiance changes by about 0.0002 (approximately 1%).
Radial transmittance is compared with the analytic exponential
column. Tests also cover seams, finite values, zero density, atmosphere-only
output, full-direction coverage, and cache/reload/reset/resize behavior.

`PlanetTimingAndCapturesAt720p` writes default, eclipse, terminator, low-orbit,
and raw Sky-View RGBA8 captures plus GPU pass timings under ignored
`artifacts/planet`, at 1280x720. Timings exclude CPU readback and presentation;
shader compilation is excluded from cached pass timings.

`AtmosphereQualityTradeoffsAt720p("Planet")` compares 64, 128, and 256 linear
Sky-View steps (with twice as many Transmittance steps) using identical texture
sizes, 10H bounds, and coordinate mappings. It measures integration error against
the 256/512-step reference in default and eclipse views; this excludes angular
LUT interpolation and the discarded tail. Warmed GPU timing and RMS error reports
are written to `artifacts/atmosphere/quality-planet-720p.txt`. Device clock and
other activity affect timings.

The linear-only 64/128-step configuration measured 0.2578 ms mean for Sky-View
and 0.0873 ms for Transmittance on Intel(R) Arc(TM) Graphics at 1280x720.
Relative image RMS error against 256/512 steps was 0.0751% for the default
view and 0.0685% for the eclipse. These are integration comparisons at fixed
texture resolution and extent, rather than total physical error measurements.
