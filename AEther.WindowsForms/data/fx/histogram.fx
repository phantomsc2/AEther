
#include "states.fxi"
#include "globals.fxi"

Texture2D<float4> Histogram0 : register(t0);
Texture2D<float4> Histogram1 : register(t1);
bool CombinedLevelView = false;

SamplerState HistoryTime
{
    Filter = MIN_MAG_LINEAR_MIP_POINT;
    AddressU = CLAMP;
    AddressV = WRAP;
};

float4 PS(const PSDefaultin IN) : SV_Target
{

	float d = 2 * (IN.UV.x - .5);
	// Older rows beyond the delayed playhead's valid ring span must not wrap into newer audio.
	if (abs(d) >= HistorySpan)
		return 0;
	float4 v = 0;
	uint width, height;
	Histogram0.GetDimensions(width, height);
	// Frequency remains discrete; only time interpolates between adjacent recorded rows.
	float frequency = (min(floor((1 - IN.UV.y) * width), width - 1) + .5) / width;
	float2 uv = float2(frequency, HistogramShift - abs(d));
	//float2 uv = float2(1 - IN.UV.y, abs(d));
	if(d < 0)
	{
		v = Histogram0.Sample(HistoryTime, uv);
	}
	else
	{
		v = Histogram1.Sample(HistoryTime, uv);
	}

	return CombinedLevelView ? float4(v.aaa, 1) : v;

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
