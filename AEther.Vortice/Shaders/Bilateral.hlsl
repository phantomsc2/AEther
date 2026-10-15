// A nonseparable bilateral average in frequency/time, independently per STN channel.
// Input and output stay in linear amplitude; alpha retains the original magnitude.
bool RetainedRow(int row, int rows)
{
    float age = frac(HistogramShift - (row + .5) / rows);
    return age >= HistoryNewestAge - 1e-6 && age <= HistoryAvailable + 1e-6;
}

float4 FilterHistory(Texture2D<float4> source, int2 p, int2 size)
{
    float4 center = source.Load(int3(p, 0));
    // Do not paint into empty rows, gaps or unavailable ring storage.
    if (!RetainedRow(p.y, size.y) || center.a <= 0) return center;
    float3 sum = center.rgb;
    float3 weights = 1;
    int2 radius = (int2)ceil(2 * float2(BilateralFrequencySigma, BilateralTimeSigma));
    [loop] for (int y = -radius.y; y <= radius.y; ++y)
        [loop] for (int x = -radius.x; x <= radius.x; ++x)
        {
            if (x == 0 && y == 0) continue;
            int2 q = int2(p.x + x, (p.y + y + size.y) % size.y);
            if (q.x < 0 || q.x >= size.x || !RetainedRow(q.y, size.y)) continue;
            float4 neighbor = source.Load(int3(q, 0));
            if (neighbor.a <= 0) continue;
            float2 offset = float2(x, y) / float2(BilateralFrequencySigma, BilateralTimeSigma);
            float3 delta = (neighbor.rgb - center.rgb) / BilateralRangeSigma;
            float3 weight = exp(-.5 * (dot(offset, offset) + delta * delta));
            sum += neighbor.rgb * weight;
            weights += weight;
        }
    return float4(lerp(center.rgb, sum / weights, BilateralStrength), center.a);
}

struct FilterOutput { float4 Left : SV_Target0; float4 Right : SV_Target1; };
FilterOutput PS(PSDefaultin input)
{
    uint width, height;
    Histogram0.GetDimensions(width, height);
    int2 size = int2(width, height);
    int2 p = int2(input.Position.xy);
    FilterOutput output;
    output.Left = FilterHistory(Histogram0, p, size);
    output.Right = FilterHistory(Histogram1, p, size);
    return output;
}
