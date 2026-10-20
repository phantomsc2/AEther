# MandelLight v4 cumulative airlight LUT study

Historical soft-kernel experiment: the current production renderer uses exact
inverse-square point lights with no source radius and a distance-scaled tail LUT.
This study retains its original soft kernels in a private experimental shader
copy so its original results remain reproducible; it does not soften production lights.

Measured 2026-10-20 on NVIDIA GeForce RTX 4070 Laptop GPU, at **1280x720**.

The unshadowed LUT is useful. Replacing every integration bin with LUT endpoint
differences while retaining pointwise cubemap visibility is slower than matched
numerical equiangular quadrature. LUT integration becomes more attractive when
lit interval boundaries are already available, so one can integrate entire runs.

## Scope and reproduction

```powershell
dotnet run -c Release --project tools/AEther.MandelLightProfile -- AEther.Vortice/Shaders artifacts/mandellight-v4/lut-study --airlight-lut
```

The experiment is contained in `tools/AEther.MandelLightProfile/AirlightLutStudy.cs`
and `AirlightLut.hlsl`. It substitutes raw pixel shaders at the renderer's existing
profiling pass markers. Production shader files, resource bindings, transport,
and defaults are unchanged. `--airlight-lut --cpu-only` runs numerical validation.

One live spectral light, 128 cubes, default air coefficients and light radius.
Geometry and camera are frozen. Shadow maps are cached during the main comparison;
they remain resident even for unshadowed modes. Surface shadows are disabled for
unshadowed cases and retained for shadowed cases. A separate moving-geometry
measurement gave **0.0297 ms** for refreshing the single 512-pixel shadow cubemap.

All output viewports are 1280x720. Full-resolution airlight integrates 1280x720
rays; half-resolution airlight integrates 640x360 rays and retains the existing
full-resolution reconstruction fallback. The full-resolution spatial filter is
disabled to measure integration error separately. The half-resolution
reconstruction remains enabled.

GPU timestamps surround normal rendering, excluding compilation, LUT construction,
and readback. Each mode uses 16 initial warmups and 41 measured frames; GPU
references use 11 frames. Each measurement follows an untimed 1024-sample render
and one untimed render of the measured mode. This common workload reduces laptop
GPU idle downclocking without changing driver settings. Medians are reported;
the raw JSON also contains the air pass's 10th and 90th percentiles. There is
still run-to-run clock and thermal variation, particularly for the longer full
resolution sampled passes.

Raw artifacts, ignored by git:

- [GPU measurements and silence checks](../artifacts/mandellight-v4/lut-study/gpu-results.json)
- [CPU validation and worst cases](../artifacts/mandellight-v4/lut-study/cpu-validation.json)
- [Unshadowed LUT capture](../artifacts/mandellight-v4/lut-study/full-lut-float.png)
- [Shadowed reference](../artifacts/mandellight-v4/lut-study/full-shadow-reference.png)
- [Shadowed LUT bins](../artifacts/mandellight-v4/lut-study/full-lut-shadow-16.png)
- [Matched numerical bins](../artifacts/mandellight-v4/lut-study/full-angular-shadow-16.png)

## Integral and storage

With no visibility term, define:

```text
x = t-c
r(x) = sqrt(x*x+b*b)
H = sqrt(b*b+h*h)
p(x) = 3/(16*pi) * (1 + x*x/(x*x+b*b))

F(b,x) = integral[x,infinity] {
    p(v) * exp(-e*(v+r(v))) / (v*v+b*b+h*h)
} dv

U(b,x) = exp(e*(x+r(x))) * F(b,x)
A(t) = exp(-e*(t+r(t-c))) * U(b,t-c)

L[t0,t1] = s*I * (A(t0)-A(t1))
A(infinity) = 0
```

Here `c` is the light projection along the camera ray, `b` its perpendicular
distance, `h` the soft radius, `e` extinction, `s` scattering, and `I` intensity.
The table contains three channel integrals for fixed RGB extinction and radius.
Scattering and intensity are applied after lookup. Camera/light motion changes
coordinates but does not invalidate the table. Extinction, radius or phase changes
require rebuilding it. The represented perpendicular distance is `0..128`, wider
than this scene's current camera/light bounds; unsupported rays explicitly fall
back to the sampled integrator.

The scaled tail U avoids a potentially large `exp(-e*c)` factor and extremely
small unscaled table values. Storage coordinates are:

```text
q_b = asinh(b/h) / asinh(128/h)
theta = atan(x/H)
q_angle = 0.5 + 0.5*sign(theta)*(1-sqrt(1-2*abs(theta)/pi))
```

The angular warp clusters texels near both infinite endpoints. Uniform angular
spacing initially produced over 200% relative error for some weak, nearly
collinear light-behind-camera rays; the final warp removes that large endpoint
error. Table construction integrates successive angular cells with four-point
Gaussian quadrature and a backward scaled-tail recurrence, in double precision.
The runtime uses bilinear texture interpolation. Infinite rays need one table
lookup; surface-terminated rays need two, plus coordinate and attenuation work.

## Unshadowed results

Error is energy-normalized linear HDR RMSE against the matching **1024-sample GPU
reference**. Reference sampling error and the RGBA16F air render target contribute
to these differences; they are not exact integration-error bounds.

| Method | Full air ms | Full frame ms | Half air ms | Half frame ms | HDR error, full |
| --- | ---: | ---: | ---: | ---: | ---: |
| Existing mixed sampler, 32 samples | 0.854 | 0.957 | 0.182 | 0.350 | 3.016% |
| Equiangular midpoint quadrature, 16 samples | 0.133 | 0.232 | 0.0307 | 0.135 | 0.0976% |
| Scaled FP32 LUT, 257x513 | 0.0461 | 0.145 | 0.0113 | 0.113 | 0.0394% |
| Scaled FP32 LUT, 513x1025 | 0.0471 | 0.147 | 0.0113 | 0.113 | 0.0336% |
| Scaled FP16 LUT, 513x1025 | 0.0461 | 0.145 | 0.0113 | 0.112 | 0.1073% |

The larger FP32 LUT makes the full air pass about **18x faster** than the existing
32-sample unshadowed estimator, or **2.8x faster** than the numerical 16-point
equiangular control. Full frame time improves about **6.5x** versus the existing
estimator. With half-resolution airlight the frame improvement is about **3.1x**.
These are one-light results; the LUT prototype evaluates every active light, so
the results should not be extrapolated to many lights without further profiling.

The smaller FP32 table occupies 2.01 MiB and its CPU integration build took
21.2 ms. The larger table occupies 8.02 MiB and took 28.0 ms. FP16 halves the
large table's storage but offered no material GPU timing advantage in this scene.
These construction times are not included in frame timings. Rebuilding every
frame would erase the benefit; parameter changes need caching or asynchronous
generation in a production implementation.

## Shadowed results and matched control

The LUT shadow prototype partitions each ray into equiangular bins, samples the
existing hardware-PCF visibility at each midpoint, and multiplies it by the
bin's unshadowed integral. Adjacent bins share endpoint reads, giving about
`N+1` logical tail queries instead of `2*N`. This approximates visibility as
constant per bin; it does not locate exact shadow boundaries or integrate PCF
variation inside a bin.

The numerical control uses **the same bins and PCF sample positions**, with
midpoint quadrature for the smooth scattering weight. This separates any LUT
benefit from the benefit of changing sampling strategy.

| Method | Full air ms | Half air ms | Half frame ms | HDR error, full |
| --- | ---: | ---: | ---: | ---: |
| Existing mixed sampler, 32 samples | 1.111 | 0.239 | 0.446 | 6.653% |
| Numerical angular bins, 16 | 0.242 | 0.0553 | 0.183 | 4.598% |
| Scaled LUT bins, 16 | 0.362 | 0.0778 | 0.216 | 4.613% |
| Numerical angular bins, 32 | 0.418 | 0.100 | 0.257 | 2.131% |
| Scaled LUT bins, 32 | 0.646 | 0.148 | 0.339 | 2.132% |

For matched shadow samples, the scaled LUT bins are about **1.4-1.5x slower** in
the air pass, with effectively the same error. The extra texture/coordinate work,
endpoint evaluation and attenuation have no useful accuracy payoff here because
the visibility sampling error dominates. Removing scaling so attenuation is
factored outside the loop did not reverse the result: the unscaled 32-bin air pass
took 0.602 ms full and 0.160 ms half, still slower than numerical quadrature.

This is evidence against using cumulative LUT differences at every fixed shadow
step. It does not establish the performance of a hierarchy that finds exact lit
segments. Given K known lit intervals, their contribution requires **2*K** tail
queries, with no integration samples inside each interval. That could be valuable
when K is small compared with the otherwise required sample count. The ordinary
cubemap point query does not provide those interval endpoints; discovering them
still requires traversal/refinement and must be included in a benchmark.
Filtered PCF visibility also requires care: a hard lit/unlit interval traversal
would not reproduce the current fractional visibility exactly.

## Numerical validation and limitations

The CPU study performs 25,200 scalar comparisons against independent adaptive
Simpson integration of the original physical integrand. It covers collinear and
near-collinear rays, light positions ahead/behind the camera, infinite rays,
very short finite segments, `b=0..128`, radii 0.01/0.040625/0.5, and default/thin/
denser extinction. This is a finite parameter sweep rather than an error proof.

For the larger scaled FP32 table at default coefficients:

- Energy-normalized RMSE: **0.000837%**.
- Median relative error: **0.00574%**.
- 99th-percentile relative error: **0.780%**, among integrals above 1e-7.
- No negative finite-segment differences in the sweep.
- Worst retained relative case: **2.23%**, for a weak short collinear segment.

The thin-medium, minimum-radius table had 2.21% relative error at the 99th
percentile. Very short segments remain sensitive to cumulative subtraction.
FP16 had about 100% relative error at that percentile despite good aggregate
image accuracy, so FP32 is preferable for arbitrary interval sums. The unscaled
FP32 control also had weaker tail accuracy (55.5% at the default sweep's 99th
percentile), supporting the scaled representation.

The Release build passed. GPU LUT accuracy assertions passed for all scaled table
sizes and formats. All four experimental integration modes rendered zero in
every RGB channel across the final four frames after 64 silent frames following
active audio. Representative reference/LUT captures were inspected visually.

The next useful production experiments are a cached scaled FP32 unshadowed-light
path, and better numerical angular quadrature for ordinary cubemap-shadowed
lights. Interval LUT integration should be revisited alongside an explicit
visibility-interval traversal, rather than added to every existing shadow sample.
