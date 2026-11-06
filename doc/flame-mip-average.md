# Flame mip-average probe

Measured 2026-11-07 on Intel (R) Arc (TM) Graphics using R32_FLOAT and D3D11
GenerateMips. The probe compares GPU mip values with double-precision sums of
the uploaded float density. Production Flame behavior is unchanged.

Run:

```powershell
dotnet run --project tools/AEther.Smoke -- . --flame-mip-average-only
```

Full per-pattern results and timings are saved to the ignored
`tools/AEther.Smoke/artifacts/flame-mip-average.txt`.

## Accuracy

The fern uses 24 iterations with trilinear mip filtering, starting from uniform
density independently at each resolution.

| Resolution | Final 1x1 mean error, fern | Last even-prefix mip | Prefix average error |
|------------|---------------------------:|---------------------:|---------------------:|
| 2048x1024  |                 -0.000043% |             10 (2x1) |           -0.000043% |
| 1280x720   |                  -29.7263% |            4 (80x45) |          -0.0000156% |
| 1281x720   |                  -29.7298% |                    0 |      0% (base image) |
| 1280x721   |                  -29.6985% |                    0 |      0% (base image) |
| 1281x721   |                  -29.6628% |                    0 |      0% (base image) |
| 1536x1024  |                 +185.3813% |              9 (3x2) |          -0.0000325% |

Constants and linear ramps survive the final mip and can conceal substantial
error for concentrated distributions. At 720p, a center impulse disappears
entirely; a last-column stripe is underestimated by 37.5%, and a last-row stripe
by 46.596%. A checkerboard at 1281x721 has a true mean near .5 but a final mip
near .374947. All tested 2048x1024 patterns preserve the mean to float precision.

An even starting resolution is insufficient: 720p reaches an odd height at
80x45, and subsequent resampling is not a mass-conserving reduction. Averaging
the prefix before that point preserves the original mean on this tested GPU.
This is an observation of the implementation, not a portable exact-sum contract.

The probe also normalizes the 720p fern once using the final mip and reads the
result back. Its true total mass becomes **1.423008**, rather than 1. A 256-thread
single-group reduction of the 80x45 mip instead agrees with the reference within
float rounding.

## Cost at 1280x720

GPU timestamp measurements use eight warmup batches and 32 retained batches of
16 operations. Copy and normalization order alternate. Medians from the final run:

| Operation                                                            |     Median |
|----------------------------------------------------------------------|-----------:|
| Full-image copy compute pass                                         | 0.03292 ms |
| Same pass plus reading the 1x1 mip and dividing by its mass estimate | 0.03312 ms |
| Paired normalization-minus-copy difference                           | 0.00035 ms |
| Separate one-thread dispatch reading the 1x1 mip                     | 0.00312 ms |
| One-group reduction of the 80x45 mip                                 | 0.00945 ms |
| GenerateMips, repeatedly regenerated unchanged source                | 0.05004 ms |
| CPU synchronous 1x1 copy and Map, GPU idle                           |  0.2227 ms |
| CPU synchronous 1x1 copy and Map, mip generation pending             |  0.3151 ms |

These are isolated warm microbenchmarks, not compositor frame measurements.
The small marginal GPU difference is near timing noise; an earlier run measured
0.02726 vs 0.02740 ms for copy vs normalization. Reused staging-resource CPU
readback timings exclude allocation and include synchronization. Actual readback
with more queued GPU work can wait longer.

Using the final mip inside an existing shader avoids a new dispatch and is
cheap, but its density estimate is unsuitable for accurate normalization at
these non-power-of-two dimensions. At 720p the measured 80x45 reduction is also
cheap and substantially more accurate. Odd base dimensions need a different
reduction path, since their exact even prefix is mip 0.
