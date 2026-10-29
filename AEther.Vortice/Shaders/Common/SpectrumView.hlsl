float4 DrawSpectrum(float2 uv, float widthPixels, bool ShowS, bool ShowT, bool ShowB, bool ShowR, bool ShowSum, bool CompareSmoothers)
{
    uint width, height;
    Spectrum0.GetDimensions(width, height);
    int bin = min((int)((1 - uv.y) * width), (int)width - 1);
    float4 v = uv.x < .5 ? Spectrum0.Load(int3(bin, 0, 0)) : Spectrum1.Load(int3(bin, 0, 0));
    float x = 2 * abs(uv.x - .5);
    if (CompareSmoothers)
    {
        // Same input on both sides: stereo-averaged linear amplitudes, same row.
        // Left/red=A (log model), right/cyan=B (complex model); raw=white marker.
        float4 comparison = .5 * (Spectrum0.Load(int3(bin, 1, 0)) + Spectrum1.Load(int3(bin, 1, 0)));
        float observed = comparison.g;
        float amplitude = uv.x < .5 ? comparison.b : comparison.a;
        float marker = observed > 0 && abs(x - observed) < 1.5 / widthPixels ? 1 : 0;
        float3 color = uv.x < .5 ? float3(1, 0, 0) : float3(0, 1, 1);
        return float4(max(color * (x < amplitude ? 1 : 0), marker.xxx), 1);
    }
    bool raw = !(ShowS || ShowT || ShowB || ShowR);
    // Residual RMS, aggregated over the same display interval as STB.
    float r = abs(uv.x < .5 ? Spectrum0.Load(int3(bin, 1, 0)).r : Spectrum1.Load(int3(bin, 1, 0)).r);
    if (raw || ShowSum)
    {
        // Residual brightness is only a display tint; sum its full amplitude.
        float sum = raw ? v.a : dot(v.rgb, float3(ShowS, ShowT, ShowB)) + (ShowR ? r : 0);
        float bar = x < sum ? 1 : 0;
        return float4(bar.xxx, 1);
    }
    v.rgb = (x < v.rgb) * float3(ShowS, ShowT, ShowB);
    float residualBar = ShowR && x < r ? .25 : 0;
    return float4(max(saturate(v.rgb), residualBar.xxx), 1);
}
