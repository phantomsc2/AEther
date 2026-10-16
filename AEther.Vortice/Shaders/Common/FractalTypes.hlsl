struct GeometryParameters
{
    float Scale;
    float JuliaBlend;
    float FoldRotation;
    float Anisotropy;
    float SliceDepth;
};
struct QualityParameters
{
    bool Antialiasing;
    float MaximumIterations;
    float MovementSpeed;
};
Texture2D<float4> FilteredHistogram0;
Texture2D<float4> FilteredHistogram1;
