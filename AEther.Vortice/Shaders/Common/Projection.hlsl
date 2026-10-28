#ifndef AETHER_PROJECTION
#define AETHER_PROJECTION
#include "Inputs.hlsl"

// Standard perspective: +Z forward, UV Y down, projected depth in [0,1].
// Frames are authored in world space; Focal = 1/tan(vertical FOV/2).
static const float ProjectionNear=.025, ProjectionFar=128, ProjectionFocal=2;
struct ProjectionCamera
{
    float3 Origin, Forward, Right, Up;
    float Focal, Near, Far;
};
ProjectionCamera MakeProjectionCamera(float3 origin, float3 forward, float3 right, float3 up)
{
    ProjectionCamera camera;
    camera.Origin=origin; camera.Forward=forward; camera.Right=right; camera.Up=up;
    camera.Focal=ProjectionFocal; camera.Near=ProjectionNear; camera.Far=ProjectionFar;
    return camera;
}
float2 ProjectionScreen(float2 uv) { return (2*uv-1)*float2(AspectRatio,-1); }
float2 ProjectionUV(float2 screen) { return .5+.5*screen/float2(AspectRatio,-1); }
float ProjectionClipDepth(float z, float nearPlane, float farPlane)
{
    return farPlane*(z-nearPlane)/(farPlane-nearPlane);
}
float ProjectionDepth(float z, float nearPlane, float farPlane)
{
    return ProjectionClipDepth(z,nearPlane,farPlane)/z;
}
float ProjectionLinearDepth(float depth, float nearPlane, float farPlane)
{
    return nearPlane*farPlane/(farPlane-depth*(farPlane-nearPlane));
}
float ProjectionLinearDepth(ProjectionCamera camera, float depth)
{
    return ProjectionLinearDepth(depth,camera.Near,camera.Far);
}
float4 ProjectionViewClip(float3 view, float2 scale, float nearPlane, float farPlane)
{
    return float4(view.xy*scale,ProjectionClipDepth(view.z,nearPlane,farPlane),view.z);
}
float3 ProjectionView(ProjectionCamera camera, float3 world)
{
    float3 v=world-camera.Origin;
    return float3(dot(v,camera.Right),dot(v,camera.Up),dot(v,camera.Forward));
}
float4 ProjectionClip(ProjectionCamera camera, float3 world)
{
    return ProjectionViewClip(ProjectionView(camera,world),camera.Focal/float2(AspectRatio,1),camera.Near,camera.Far);
}
float3 ProjectionDirection(ProjectionCamera camera, float2 uv)
{
    float2 screen=ProjectionScreen(uv);
    return normalize(camera.Focal*camera.Forward+screen.x*camera.Right+screen.y*camera.Up);
}
float ProjectionRayDistance(ProjectionCamera camera, float3 direction, float forwardDepth)
{
    return forwardDepth/dot(direction,camera.Forward);
}
float ProjectionDepthDistance(ProjectionCamera camera, float3 direction, float depth)
{
    return ProjectionRayDistance(camera,direction,ProjectionLinearDepth(depth,camera.Near,camera.Far));
}
float3 ProjectionPosition(ProjectionCamera camera, float3 direction, float forwardDepth)
{
    return camera.Origin+direction*ProjectionRayDistance(camera,direction,forwardDepth);
}
float3 ProjectionReconstruct(ProjectionCamera camera, float2 uv, float depth)
{
    return ProjectionPosition(camera,ProjectionDirection(camera,uv),ProjectionLinearDepth(depth,camera.Near,camera.Far));
}
bool ProjectionClipInterval(ProjectionCamera camera, float3 direction, inout float begin, inout float end)
{
    float z=dot(direction,camera.Forward);
    if (z<=0) return false;
    begin=max(begin,camera.Near/z); end=min(end,camera.Far/z);
    return end>begin;
}
// Plane equation dot(world,normal)=offset. Returns a ray distance, or -1.
float ProjectionPlane(ProjectionCamera camera, float3 direction, float3 normal, float offset)
{
    float d=dot(direction,normal);
    if (abs(d)<1e-6) return -1;
    float t=(offset-dot(camera.Origin,normal))/d;
    float z=t*dot(direction,camera.Forward);
    return z>=camera.Near && z<=camera.Far ? t : -1;
}
#endif
