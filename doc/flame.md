# Flame

Flame is an audio-independent, bounded affine IFS density experiment. It performs
exactly one inverse-gather pass each time the canvas is rendered. Each of three
invertible branches contributes one third of the source density divided by its
absolute forward Jacobian determinant. There is no random branch selection.

Two canvas-resolution R32 float textures alternate source and destination. The
initial density is 1/96 over the rectangle [-4,4] x [0,12], whose area is 96.
Feedback uses `SampleGrad` with analytic inverse-affine UV gradients per destination
pixel. All modes use zero-border sampling, including footprints whose centers are
outside the rectangle but which overlap it.
The continuing frond and two side fronds are bounded by construction: each
matrix is uniformly scaled as needed to put its mapped rectangle inside 98%
of the domain. This scale belongs to the affine map and its determinant.

| Control         | Mapping                                               | Default slider |
|-----------------|-------------------------------------------------------|----------------|
| Fern.Growth     | Continuing-frond scale .78 to .92                     | .5             |
| Fern.Bend       | Continuing-frond off-diagonal coefficient -.08 to .08 | .75            |
| Fern.Spread     | Opposite side-frond rotations -20 to +20 degrees      | .5             |
| Fern.FrondScale | Side-frond matrix multiplier .75 to 1.25              | .5             |
| DensityGain     | Display density multiplier 2^(-4 + 16*u)              | .5             |

The `Filtering` group contains three bool toggles, all off by default:

| Toggle      | Meaning                                                                                 |
|-------------|-----------------------------------------------------------------------------------------|
| Mipmaps     | Generate the source density mip chain before each iteration and expose all levels       |
| Trilinear   | Interpolate between mip levels; when off, use bilinear filtering within the nearest mip |
| Anisotropic | Use anisotropic filtering with maximum anisotropy 16; overrides Trilinear               |

With Mipmaps off, the iteration sees only mip 0, so bilinear and trilinear are
equivalent. Anisotropic sampling remains available without mips. Toggle changes
preserve the density and do not allocate new textures or restart iteration.
Both textures reserve full mip chains even when disabled, approximately one-third
more storage than their base levels. D3D11 `GenerateMips` supplies average-density
levels; no global mass measurement or normalization is performed. Unsupported
R32 float mip generation is reported as a scene compilation error.

The display always uses its own bilinear mip-0 sampler, independent of these
feedback controls. Only the evolving density responds to the filtering toggles.

The display is an upright, aspect-preserving green image on black, with
`log2(1 + gain*density)` followed by ordinary scene presentation. DensityGain
never affects the stored density. Shape edits and Reset defaults immediately
change the maps applied to the current density; neither resets that density.
Resize, successful shader reload, and scene reactivation create a fresh seed.
Failed reload retains the old programs and density.

This is deliberately raw feedback. There is no mass normalization, additional
smoothing, density injection, clamping, convergence detection, or automatic
recovery. Hardware texture filtering is not exact pixel-area integration. Repeated
iteration can concentrate, fade, alias, or eventually disappear, and numerical
range is limited by float storage. Iteration speed follows canvas frame rate.
Equal branch weights differ from the usual Barnsley fern probabilities; no
singular stem branch is included.

Run `dotnet run --project tools/AEther.Smoke -- . --flame-only` for a double-precision
CPU comparison, feedback/lifecycle checks, and 1280x720 previews after 1, 8, 24,
and 64 iterations plus shape edits. The check also captures all six effective
filter/mip combinations after 24 iterations from fresh seeds, tests all eight bool
combinations, checks mip-generation cadence and checkerboard minification, and
exercises late mip toggles and resource lifecycle. PNGs go to the ignored smoke
artifacts folder.

For final-mip normalization accuracy, even/odd dimension checks, and GPU versus
CPU readback costs, see the [mip-average probe](flame-mip-average.md).
