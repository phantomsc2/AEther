#include "states.fxi"
#include "globals.fxi"

Texture2D<float4> CloudImage : register(t0);

float4 PS(const PSDefaultin IN) : SV_Target
{
    float3 color = max(0, CloudImage.Sample(Linear, IN.UV).rgb);
    // Stable filmic output; no frame-varying dither or temporal ghosting of thunder attacks.
    color = saturate((color * (2.51 * color + .03)) / (color * (2.43 * color + .59) + .14));
    color = pow(color, 1 / 2.2);
    return float4(color, 1);
}

technique11 t0
{
    pass p0
    {
        SetRasterizerState(RasterizerDefault);
        SetDepthStencilState(DepthStencilNone, 0);
        SetBlendState(BlendNone, float4(0, 0, 0, 0), 0xFFFFFFFF);
        SetVertexShader(CompileShader(vs_4_0, VSDefault()));
        SetGeometryShader(0);
        SetPixelShader(CompileShader(ps_4_0, PS()));
    }
}
