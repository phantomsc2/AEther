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
    float Antialiasing;
    float MaximumIterations;
    float MovementSpeed;
};
struct FilterParameters
{
    float Strength;
    float FrequencySigma;
    float TimeSigma;
    float RangeSigma;
};
Texture2D<float4> FilteredHistogram0;
Texture2D<float4> FilteredHistogram1;
