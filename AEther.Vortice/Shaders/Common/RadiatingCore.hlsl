// V3's dark Mandelbox core. The fluid emitter and the display use this same
// field and projection, so clouds leave the actual silhouette rather than a jet.
float RadiatingTime() { return S * Quality.MovementSpeed * 10; }
float RadiatingShapeTime()
{
    return (ShapeTime * 1.25 + .35 * S) * Quality.MovementSpeed * 20;
}
float2 RadiatingCenter()
{
    float t = .12 * RadiatingShapeTime();
    return float2(min(1, AspectRatio) * (.1 + .06 * sin(.73 * t)),
        -.06 + .045 * sin(.61 * t + .6));
}
float RadiatingBoundary() { return .003 + .003 * saturate(PoseX); }
float2 RadiatingDistances(float2 p)
{
    float t = RadiatingShapeTime();
    float3 q = float3((p - RadiatingCenter()) * (8 / min(1, AspectRatio)), 0);
    q = mul(Rz(.52 * t + .22 * sin(1.7 * t)), q);
    q = mul(Rx(.65 * sin(.91 * t)), q);
    q = mul(Ry(.7 * cos(.73 * t)), q);
    q.z += Geometry.SliceDepth * 4 - 2 + .84 * (.9 * sin(1.1 * t) + .4 * sin(1.93 * t));
    float depth = max(1, (1 + round(Quality.MaximumIterations * 29))
        * lerp(.3, 1, saturate(PoseX + PoseZ)));
    return max(1e-8, BoxDistanceAtDepth(q, t, depth));
}
float3 RadiatingPalette(float frequency)
{
    return .5 + .5 * cos(6.2831853 * (frequency * .85 + float3(.04, .38, .62)));
}
