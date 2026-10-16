#include "../Common/Inputs.hlsl"
#include "../Common/FractalTypes.hlsl"
cbuffer Parameters
{
    GeometryParameters Geometry = { 0.3125, 0.3, 0.35, 0.44, 0.5 };
    QualityParameters Quality = { false, 0.31034483, 0.3 };
};
#include "../Common/Mandelbox.hlsl"
