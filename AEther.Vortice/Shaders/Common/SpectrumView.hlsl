float4 DrawSpectrum(float2 uv, bool ShowS, bool ShowT, bool ShowB, bool ShowR, bool ShowSum)
{
    uint width, height;
    Spectrum0.GetDimensions(width, height);
    int bin = min((int)((1 - uv.y) * width), (int)width - 1);
    float4 v = uv.x < .5 ? Spectrum0.Load(int3(bin, 0, 0)) : Spectrum1.Load(int3(bin, 0, 0));
    float x = 2 * abs(uv.x - .5);
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
