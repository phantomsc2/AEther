# Shared perspective projection

`Common/Projection.hlsl` provides camera rays, world-to-clip projection, depth
mapping, and world-position reconstruction. The camera is entirely shader-owned;
there are no new host buffers, parameter names, or rendering passes.

```hlsl
#include "../Common/Projection.hlsl"

ProjectionCamera SceneCamera()
{
    float3 origin=float3(0,1,-10),forward=float3(0,0,1);
    return MakeProjectionCamera(origin,forward,float3(1,0,0),float3(0,1,0));
}

// Raster geometry:
float4 clip=ProjectionClip(SceneCamera(),world);
// Fullscreen ray:
ProjectionCamera camera=SceneCamera();
float3 direction=ProjectionDirection(camera,input.UV);
```

## Conventions

`ProjectionCamera` contains `Origin`, orthonormal world-space `Forward`, `Right`,
and `Up`, plus `Focal`, `Near`, and `Far`. The helper constructor defaults to focal
scale 2, near distance 0.025, and far distance 128. Scenes can override these fields
in HLSL. Camera motion and meaningful lens controls remain scene responsibilities.
Supply a positive focal scale and `0 < Near < Far`.

Focal scale is `1/tan(verticalFOV/2)`. The default vertical field of view is about
53.13 degrees. Horizontal coverage scales with `AspectRatio`; portrait windows
retain the same vertical coverage and show less horizontally. The former
shortest-viewport-dimension framing has been removed from the migrated scenes.

UV starts at the upper left, with positive Y down. Camera up points up; positive
forward distance is view-space Z. The authored basis determines world orientation;
projection does not impose a world up axis or mirror an existing camera frame.
Rays are normalized and start at the camera origin. Depth is forward distance,
not distance along an off-axis ray.

For forward distance `z`, near `n`, and far `f`:

```text
clip.x = Focal * view.x / AspectRatio
clip.y = Focal * view.y
clip.z = f * (z-n) / (f-n)
clip.w = z
depth  = clip.z / clip.w
z      = n*f / (f-depth*(f-n))
t      = z / dot(ray, Forward)
```

Depth zero maps to near and one maps to far. This is ordinary D3D perspective
depth, without reversed Z. A cleared depth buffer of one represents a missing
surface; callers test for a hit before reconstructing it.

## Helpers

| Helper | Purpose |
| --- | --- |
| `ProjectionScreen` / `ProjectionUV` | Aspect-correct screen coordinates and inverse |
| `ProjectionDirection` | Normalized world ray through a UV coordinate |
| `ProjectionView` / `ProjectionClip` | World to camera coordinates / homogeneous clip position |
| `ProjectionViewClip` | Perspective projection of an existing view position, including shadow aperture/guard scaling |
| `ProjectionClipDepth` / `ProjectionDepth` | Clip Z / divided depth from forward distance |
| `ProjectionLinearDepth` | Forward distance from divided depth |
| `ProjectionRayDistance` / `ProjectionDepthDistance` | Ray distance from forward / divided depth |
| `ProjectionPosition` / `ProjectionReconstruct` | World position from forward / divided depth |
| `ProjectionClipInterval` | Intersect a finite primary ray interval with the camera frustum |
| `ProjectionPlane` | Frustum-clipped ray-plane intersection; negative means no hit |

For integer pixel coordinates, use `(pixel+.5)/Resolution`. `ProjectionViewClip`
takes an explicit XY scale and near/far pair: square spot/cube shadow views use
their existing aperture and guard scale rather than the screen aspect ratio.

## Migrated scenes

- MandelLight v2/v4/v5 and MandelVolume use the shared camera, ray, and depth math.
  V4's spot/cube shadow maps retain their existing projection scales and near/far.
- FilamentFlight and FoldFlight intersect world-Z cohort planes with camera rays.
  Steering rotates the camera; bends and twists remain world geometry.
- SpectrumRoad uses one camera for the road plane and roadside cohort planes.
  Its independent screen-space horizon adjustment has been removed.
- PlaneFlight uses its existing camera controls and world plane with shared rays.
- FluidFlight intersects a world-Z cylinder instead of assigning depth from
  inverse screen radius. `SourceDistance` controls its radius. The foreground
  and fluid injection use the same hit function, including steering and roll.

Mandelbox, MandelFluid variants, and analysis scenes retain their 2D mappings.
Sampling a slice of a 3D field does not itself require a perspective camera.

## Clipping and infinite airlight

Finite primary geometry and volume intervals obey near/far clipping. Shadow
transmission rays do not inherit the camera frustum. Homogeneous v4/v5 airlight
integrates from the camera to an opaque surface, or to infinity when no surface
is present. Neither near nor far truncates that physical integral. The atmosphere
LUT's coordinate range is also independent of the camera far plane.

GPU tests check depth and position round trips, independent vertical-FOV rays,
off-axis distances, plane clipping, raster depth reconstruction, shadow projection
primitives, and fluid-hit alignment. Both v4 and v5 test that changing camera
near/far leaves the infinite background integral unchanged; independent quadrature
tests retain infinite and surface-clipped cases.

`tools/AEther.MandelLightProfile --projection` captures all nine migrated scenes
at 1280x720, 720x1280, and 720x720 into `artifacts/shared-projection`. These are
visual smoke captures, not performance measurements.
