# Mandelbench

Mandelbench now renders **signed Euclidean distance to the boundary of the 2D
fractal slice**. It is static and independent of audio. Rebuild/restart the app
once for the new cached GPU passes, then shader edits use normal hot reload.
Reset defaults after upgrading to get the new five-iteration distance-band view.

## Geometry and distance

The finite-depth solid contains points whose initial position and EVERY visited
orbit position stay in the escape sphere: `max_k length(z_k) <= EscapeRadius`,
with `z_0 = p`. Intersecting this solid with the viewing plane defines the 2D
shape. Iterations, partial steps, Julia mode, slice, and escape radius all change
that shape. This is a finite approximation to the bounded-orbit fractal; at
unusual scales or with folds disabled it may not approximate the infinite set
well.

The GPU samples inside/outside membership on a grid at twice display resolution
(capped at 4096 along the longest axis), in a domain 50% wider and taller than
the view. Edge sign changes are refined with nine bisection steps to produce
subpixel boundary samples. Jump flooding, followed by two additional correction
passes, propagates nearby boundary samples across the grid. Each cell stores
signed Euclidean distance to its selected boundary sample. The final shader
bilinearly interpolates that scalar field, producing continuous bands.

This computes geometric distance to sampled boundary points, rather than using
orbit radius divided by stretching or projecting toward orbit roots. It is an
approximation: features smaller than the grid can be missed, jump flooding is an
approximate nearest-neighbor algorithm, and boundaries outside the padded domain
are not included. It is distance WITHIN the viewing plane, not 3D distance to the
fractal. Increasing display resolution improves boundary sampling until the cap.

The field is cached. Geometry edits, resize, or shader reload rebuild it; changing
band spacing, line width, selected level, or colors reuses it. There is no
per-pixel iterative closest-point search in the display shader.

## Bands

Inside bands are cyan, outside bands are gold. The periodic view also outlines
the boundary in pale white. Enable DistanceContours for just one absolute-distance
level on both sides, without a persistent boundary overlay. Compare shows the
one-pass scalar estimate on the left and the sampled geometric distance on the
right, for the same finite boundary. CoordinateGrid restores the optional UV
fold view.

Line thickness uses world-space pixel size, never `fwidth(distance)`. Periodic
lines use an integrated pulse filter to average unresolved bands. Antialiasing
adds 2x2 display samples. ShowFailures highlights regions with no propagated
boundary sample in magenta; these regions do not draw contours.

## Controls

All numeric controls are normalized to [0, 1].

| Control | Mapping / purpose |
|---|---|
| `IFS.Scale` | -4 to +4; default .3125 = -1.5 |
| `IFS.Iterations` | Completed iterations, rounded from 0 to 12; default 4 |
| `IFS.Step` | Next iteration: 0 unchanged, 1/3 box fold, 2/3 sphere fold, 1 scale plus translation; default 1 |
| `IFS.BoxFold`, `IFS.SphereFold` | Enable each fold |
| `IFS.MinimumRadius` | Inner sphere radius .05 to 1; default .5; outer radius 1 |
| `IFS.Juliabox` | Replace addition of p with the Julia constant |
| `IFS.JuliaConstant` | Components -2 to +2; default (.7, .2, 0) |
| `Search.EscapeRadius` | Finite orbit boundary radius, 2 to 32; default 8 |
| `Search.ShowFailures` | Highlight a missing sampled boundary |
| `View.DistanceContours` | Single selected level instead of periodic bands |
| `View.DistanceLevel` | Single absolute distance: `2^(-16 + 18 * value)`; default .0625 |
| `View.BandSpacing` | Periodic spacing: `2^(-8 + 8 * value)`; default .125 |
| `View.LineWidth` | .35 to 1.65 pixels; default .805 |
| `View.Antialiasing` | Additional 2x2 display sampling; default off |
| `View.Compare` | Local scalar estimate left, sampled slice distance right |
| `View.Zoom` | Larger zooms out; default .5 |
| `View.Pan` | x/y center -4 to +4; default centered |
| `View.SliceDepth` | Slice z -2 to +2; default 0 |
| `View.Rotation` | Yaw/pitch -pi to +pi; default unrotated |
| `View.CoordinateGrid` | Optional folded-coordinate view |
| `View.GridDensity`, `View.CoordinateColor` | Optional grid appearance |

## Optional Jacobian diagnostics

Jacobian.Enabled shows the analytic orbit derivative. With Compare, running
scalar stretching is left and largest singular value is right. BoundRatio shows
their ratio. MatrixEntries shows all nine signed components (rows = output
x/y/z, columns = input x/y/z), cyan positive and orange negative. LogRange selects
+/-1 to +/-16 powers of two, default +/-8.5. DistanceContours returns to the
boundary-distance comparison. These derivatives are not used to construct the
sampled slice distance.

Mandelbench intentionally stays visible in silence. It is the explicit exception
to the music scenes' silence regression, with separate GPU checks for stable
rendering, analytic derivatives, and sampled distance against an exact circle.
