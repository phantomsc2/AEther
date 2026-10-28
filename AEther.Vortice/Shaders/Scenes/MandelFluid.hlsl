#define MANDEL_FLUID
#include "../Common/Inputs.hlsl"
#include "../Common/FractalTypes.hlsl"
#include "../Common/RingFluidParameters.hlsl"
cbuffer Parameters
{
    GeometryParameters Geometry = { 0.3125, 0.3, 0.35, 0.44, 0.5 };
    QualityParameters Quality = { false, 0.31034483, 0.3 };
    EmitterParameters Emitter = RING_EMITTER_DEFAULTS;
    FluidParameters Fluid = RING_FLUID_DEFAULTS;
};
#include "../Common/RingEmitter.hlsl"
#include "../Common/RingFluidMaterial.hlsl"
#include "../Common/FluidLight.hlsl"
#include "../Common/Mandelbox.hlsl"
