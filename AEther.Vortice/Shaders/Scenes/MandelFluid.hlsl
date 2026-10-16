#define MANDEL_FLUID
#include "../Common/Inputs.hlsl"
#include "../Common/FractalTypes.hlsl"
struct FluidParameters
{
    float Flow;
    float HalfLife;
    float Diffusion;
    float Noise;
    float Curl;
};
cbuffer Parameters
{
    GeometryParameters Geometry = { 0.3125, 0.3, 0.35, 0.44, 0.5 };
    QualityParameters Quality = { 0, 0.31034483, 0.3 };
    FilterParameters Filtering = { 1, 0.47368421, 0.47368421, 0.0099901 };
    FluidParameters Fluid = { 0.33333333, 0.13131313, 0.1, 0.33333333, 0.225 };
};
#include "../Common/FluidLight.hlsl"
#include "../Common/Mandelbox.hlsl"
