
#include "states.fxi"
#include "globals.fxi"

Texture2D<float4> Spectrum0 : register(t0);
Texture2D<float4> Spectrum1 : register(t1);
bool IntensityView = false;
bool CombinedLevelView = false;

float4 PS(const PSDefaultin IN) : SV_Target
{

	float2 uv = float2(1 - IN.UV.y, 0);
	float4 l = Spectrum0.Sample(Point, uv);
	float4 r = Spectrum1.Sample(Point, uv);

	float4 v = lerp(l, r, .5 < IN.UV.x);
	if (CombinedLevelView) v = float4(v.aaa, 1);
	if (IntensityView)
		return float4(saturate(v.rgb), 1);
	v = 2 * abs(IN.UV.x - .5) < v;
	
	//float4 v = lerp(l, r, IN.UV.x);
	//v = 1 - exp(-v * (1 - 2 * abs(IN.UV.x - .5)));

	return v;

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
