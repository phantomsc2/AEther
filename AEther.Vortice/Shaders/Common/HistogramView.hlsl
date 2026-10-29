float4 DrawHistogram(float2 uv, float widthPixels, bool CombinedLevel)
{
    uint width, height;
    Histogram0.GetDimensions(width, height);
    float age = 2 * abs(uv.x - .5);
    int frequency = min((int)((1 - uv.y) * width), (int)width - 1);
    // Integrate the piecewise-constant history over this pixel's time footprint.
    // Fractional coverage makes sharp events scroll smoothly without interpolating
    // observations in time or holding missing rows. Unwritten time contributes black.
    float halfPixel = HistorySpan / widthPixels;
    float lo = max(0, age * HistorySpan - halfPixel);
    float hi = age * HistorySpan + halfPixel;
    float footprint = max(1e-8, (hi - lo) * height);
    lo = max(lo, HistoryNewestAge);
    hi = min(hi, HistoryAvailable + 1.0 / height);
    if (hi <= lo || HistoryAvailable <= 0) return float4(0, 0, 0, 1);
    float first = (HistogramShift - hi) * height + .5;
    float end = (HistogramShift - lo) * height + .5;
    float4 v = 0;
    [loop] for (int row = (int)floor(first); row < (int)ceil(end); row++)
    {
        int slot = (row % (int)height + (int)height) % (int)height;
        float coverage = max(0, min(end, row + 1.0) - max(first, (float)row));
        float4 block = uv.x < .5 ? Histogram0.Load(int3(frequency, slot, 0)) : Histogram1.Load(int3(frequency, slot, 0));
        v += block * (coverage / footprint);
    }
    return float4(CombinedLevel ? v.aaa : v.rgb, 1);
}
