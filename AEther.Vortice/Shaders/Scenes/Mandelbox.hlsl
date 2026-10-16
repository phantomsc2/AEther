#include "../Common/Inputs.hlsl"
#include "../Common/FractalTypes.hlsl"
cbuffer Parameters
{
    GeometryParameters Geometry = { 0.3125, 0.3, 0.35, 0.44, 0.5 };
    QualityParameters Quality = { 0, 0.31034483, 0.3 };
    FilterParameters Filtering = { 1, 0.47368421, 0.47368421, 0.0099901 };
};
#include "../Common/Mandelbox.hlsl"
