float4 PS(PSDefaultin input) : SV_Target
{
    uint width, height;
    Histogram0.GetDimensions(width, height);
    float age = 2 * abs(input.UV.x - .5);
    float frequency = (min(floor((1 - input.UV.y) * width), width - 1) + .5) / width;
    float2 uv = float2(frequency, HistogramShift - age * HistorySpan);
    float4 v = input.UV.x < .5 ? Histogram0.Sample(Linear, uv) : Histogram1.Sample(Linear, uv);
    return float4(((int)ViewFlags & 1) != 0 ? v.aaa : v.rgb, 1);
}
