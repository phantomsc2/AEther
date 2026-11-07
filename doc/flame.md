# Flame

Flame is an audio-independent, bounded affine IFS density experiment. It performs
exactly one inverse-gather pass each time the canvas is rendered. Each of three
invertible branches contributes one third of the source density divided by its
absolute forward Jacobian determinant. There is no random branch selection.

Two power-of-two R32 float textures alternate source and destination. Each
dimension rounds up independently from the canvas size (1280x720 uses
2048x1024), and the display samples that density into the canvas. The
initial density is 1/96 over the rectangle [-4,4] x [0,12], whose area is 96.
Feedback uses `SampleGrad` with analytic inverse-affine UV gradients per destination
pixel. Filtering uses anisotropic sampling (maximum anisotropy 16) over the
full mip chain and a zero border, including footprints whose centers are
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

Both textures reserve full mip chains, approximately one-third more storage
than their base levels. D3D11 `GenerateMips` runs every iteration. The iteration shader reads the 1x1
mip of the source on the GPU and divides its output by 96 times that mean.
The initial seed therefore has a normalization factor of one. No CPU readback
or extra normalization pass is used. A zero mean produces zero output.
Power-of-two dimensions make a 2x2 averaging chain preserve the mean
mathematically; D3D11 does not guarantee exact arithmetic for `GenerateMips`.
Unsupported R32 float mip generation is reported as a scene compilation error.

The display uses its own bilinear mip-0 sampler.

The display is an upright, aspect-preserving green image on black, with
`log2(1 + gain*density)` followed by ordinary scene presentation. DensityGain
never affects the stored density. Shape edits and Reset defaults immediately
change the maps applied to the current density; neither resets that density.
Resizing across an internal power-of-two boundary, successful shader reload,
and scene reactivation create a fresh seed. Resizing within the same internal
dimensions preserves the density.
Failed reload retains the old programs and density.

Apart from per-iteration mass normalization, this is raw feedback. There is no additional
smoothing, density injection, clamping, convergence detection, or automatic
recovery. Hardware texture filtering is not exact pixel-area integration. Repeated
iteration can concentrate, fade, alias, or eventually disappear, and numerical
range is limited by float storage. Iteration speed follows canvas frame rate.
Equal branch weights differ from the usual Barnsley fern probabilities; no
singular stem branch is included.

Run `dotnet run --project tools/AEther.Smoke -- . --flame-normalization-only`
for a GPU check of power-of-two sizing, 1x1 mean lookup, and live iteration.

For final-mip normalization accuracy, even/odd dimension checks, and GPU versus
CPU readback costs, see the [mip-average probe](flame-mip-average.md).
