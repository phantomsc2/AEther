using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

// Shared 125-real-coefficient medium, then four camera-independent deep cubemaps.
// Object generation/aggregation is CPU work once per geometry update, never per light.
internal sealed class FourierLightTransport : IDisposable
{
    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly ID3D11Buffer coefficients, selection;
    readonly Vector4[] data = new Vector4[64]; // 63 conjugate pairs/DC + bounds.
    readonly Dictionary<string, ID3D11Buffer> buffers = [];
    readonly ID3D11Texture2D?[] images = new ID3D11Texture2D?[4];
    readonly ID3D11ShaderResourceView?[] views = new ID3D11ShaderResourceView?[4];
    readonly ID3D11RenderTargetView?[,] targets = new ID3D11RenderTargetView?[24, 4];
    readonly float[]?[] lightKeys = new float[]?[4];
    float[]? geometryKey;
    ShaderProgram? previousProgram;
    int size, generation;
    internal Action<string, bool>? PassMarker { get; set; }
    internal double LastAggregationMilliseconds { get; private set; }
    internal bool AggregatedThisFrame { get; private set; }

    internal FourierLightTransport(ID3D11Device device, ID3D11DeviceContext context)
    {
        this.device = device; this.context = context;
        coefficients = device.CreateBuffer(64 * 16, BindFlags.ConstantBuffer);
        try { selection = device.CreateBuffer(16, BindFlags.ConstantBuffer); }
        catch { coefficients.Dispose(); throw; }
    }
    static ShaderParameter Find(ShaderParameters p, string group, string name) =>
        p.Items.Single(x => x.Name == group).Children.Single(x => x.Name == name);
    static float Read(ShaderParameters p, string group, string name) => p.GetFloat(Find(p, group, name));
    static bool Flag(ShaderParameters p, string group, string name) => p.GetBoolean(Find(p, group, name));
    static uint Hash(uint x) { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; return x ^ (x >> 16); }
    static double Random(uint x) => ((Hash(x) >> 9) + .5) / 8388608.0;

    // Complex exponential coefficients of E(x)*h(x-phase), E=h=(1+cos(x))/2.
    // The peak is at phase/2. All three axes share E, so every density vanishes
    // smoothly on the same box. No per-object clipping enters the light pass.
    static void Axis(Span<Complex> values, double phase)
    {
        double c = Math.Cos(phase), s = Math.Sin(phase);
        values[2] = new(.25 + .125 * c, 0);
        values[3] = new(.125 * (1 + c), -.125 * s);
        values[4] = new(.0625 * c, -.0625 * s);
        values[1] = Complex.Conjugate(values[3]); values[0] = Complex.Conjugate(values[4]);
    }
    unsafe void Aggregate(ShaderParameters p, float time)
    {
        long start = Stopwatch.GetTimestamp();
        Array.Clear(data);
        int count = 1 + (int)MathF.Round(511 * Read(p, "Shape", "Count"));
        uint seed = (uint)MathF.Round(65535 * Read(p, "Shape", "Seed"));
        double spread = .44 * Read(p, "Shape", "Spread");
        double motion = Read(p, "Shape", "Motion");
        int repetitions = 1 + 2 * (int)MathF.Round(2 * Read(p, "Shape", "Repetitions"));
        Span<Complex> sums = stackalloc Complex[63]; sums.Clear();
        Span<double> phase = stackalloc double[3];
        Span<Complex> ax = stackalloc Complex[5], ay = stackalloc Complex[5], az = stackalloc Complex[5];
        double totalWeight = 0;
        for (uint b = 0; b < count; b++)
        {
            uint h = Hash(seed ^ ((b + 1) * 0x9e3779b9));
            uint cluster = Hash(seed + (b % 4) * 7919 + 17);
            double normalization = 1;
            for (uint axis = 0; axis < 3; axis++)
            {
                double center = (2 * Random(cluster + axis) - 1) * .8
                    + (2 * Random(h + axis) - 1) * .2;
                center += .12 * motion * Math.Sin(time * .3 + 6.28318530718 * Random(h + axis + 4));
                center = Math.Clamp(center * spread, -.45, .45);
                phase[(int)axis] = Math.IEEERemainder(2 * Math.PI * center * repetitions, 2 * Math.PI);
                double peak = Math.Cos(.25 * phase[(int)axis]);
                peak *= peak; normalization *= peak * peak;
            }
            double weight = .6 + .8 * Random(h + 9);
            totalWeight += weight;
            weight /= normalization;
            Axis(ax, phase[0]); Axis(ay, phase[1]); Axis(az, phase[2]);
            for (int i = 0; i < 63; i++)
            {
                int index = i + 62;
                sums[i] += weight * ax[index / 25] * ay[index / 5 % 5] * az[index % 5];
            }
        }
        for (int i = 0; i < 63; i++)
        {
            double scale = (i == 0 ? 1 : 2) / totalWeight;
            data[i] = new((float)(scale * sums[i].Real), (float)(-scale * sums[i].Imaginary), 0, 0);
        }
        float half = 4 + 4 * Read(p, "Shape", "Size");
        data[63] = new(half, repetitions * MathF.PI / half, 0, 0);
        fixed (Vector4* source = data) context.UpdateSubresource(coefficients, 0, null, (IntPtr)source, 0, 0);
        generation++;
        LastAggregationMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AggregatedThisFrame = true;
    }
    void Allocate(int resolution)
    {
        ReleaseMaps();
        try
        {
            for (int k = 0; k < 4; k++)
            {
                images[k] = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)resolution, Height = (uint)resolution, ArraySize = 24, MipLevels = 1,
                    Format = Format.R16G16B16A16_Float, SampleDescription = new(1, 0),
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget, Usage = ResourceUsage.Default,
                });
                views[k] = device.CreateShaderResourceView(images[k]!);
                for (int face = 0; face < 24; face++)
                    targets[face, k] = device.CreateRenderTargetView(images[k]!, new RenderTargetViewDescription
                    {
                        Format = Format.R16G16B16A16_Float, ViewDimension = RenderTargetViewDimension.Texture2DArray,
                        Texture2DArray = new Texture2DArrayRenderTargetView { MipSlice = 0, FirstArraySlice = (uint)face, ArraySize = 1 },
                    });
            }
            size = resolution;
        }
        catch { ReleaseMaps(); throw; }
    }
    internal void Prepare(ShaderProgram program, ShaderParameters p, float time,
        IReadOnlyDictionary<string, ID3D11Buffer> frameBuffers,
        IDictionary<string, ID3D11Buffer> sceneBuffers, IDictionary<string, ID3D11ShaderResourceView> sceneTextures)
    {
        AggregatedThisFrame = false;
        float motion = Read(p, "Shape", "Motion");
        float[] key = [Read(p, "Shape", "Count"), Read(p, "Shape", "Seed"), Read(p, "Shape", "Size"),
            Read(p, "Shape", "Spread"), Read(p, "Shape", "Repetitions"), motion, motion > 0 ? time : 0];
        if (geometryKey == null || !key.AsSpan().SequenceEqual(geometryKey))
        {
            Aggregate(p, time); geometryKey = key;
        }
        int resolution = 32 << (int)MathF.Round(2 * Read(p, "Quality", "ShadowResolution"));
        if (resolution != size) Allocate(resolution);
        if (!ReferenceEquals(previousProgram, program)) { Array.Clear(lightKeys); previousProgram = program; }
        buffers.Clear(); foreach (var pair in frameBuffers) buffers[pair.Key] = pair.Value;
        buffers["FourierConstants"] = coefficients; buffers["FourierPassConstants"] = selection;
        bool preview = Flag(p, "View", "ShowTransport");
        int previewLight = (int)MathF.Round(3 * Read(p, "View", "TransportLight"));
        bool render = Flag(p, "Material", "Enabled") && Read(p, "Material", "Density") > 0
            && !Flag(p, "View", "ShowDensitySlice") && !Flag(p, "Quality", "ExactShadows");
        for (int light = 0; light < 4; light++)
        {
            string group = light == 0 ? "Light" : "Light" + (light + 1);
            if (!(preview && light == previewLight)
                && !(render && !preview && Flag(p, group, "Enabled") && Read(p, group, "Intensity") > 0)) continue;
            float speed = Read(p, group, "Speed"), rotation = Read(p, "Shape", "Speed");
            float[] lightKey = [generation, size, speed, Read(p, group, "Phase"), Read(p, group, "Orbit"), rotation,
                speed > 0 || rotation > 0 ? time : 0];
            if (lightKeys[light] != null && lightKey.AsSpan().SequenceEqual(lightKeys[light])) continue;
            context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
            context.OMSetBlendState(null);
            context.RSSetViewport(0, 0, size, size);
            PassMarker?.Invoke($"Fourier light {light} cube", true);
            for (int face = 0; face < 6; face++)
            {
                context.UpdateSubresource(new Selection((uint)light, (uint)face, size), selection);
                context.OMSetRenderTargets(new[] { targets[6 * light + face, 0]!, targets[6 * light + face, 1]!,
                    targets[6 * light + face, 2]!, targets[6 * light + face, 3]! });
                program.Bind(context, p, buffers, new Dictionary<string, ID3D11ShaderResourceView>(),
                    new Dictionary<string, ID3D11SamplerState>());
                context.Draw(3, 0);
            }
            PassMarker?.Invoke($"Fourier light {light} cube", false);
            context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            lightKeys[light] = lightKey;
        }
        sceneBuffers["FourierConstants"] = coefficients;
        for (int k = 0; k < 4; k++) sceneTextures["FourierDeep" + k] = views[k]!;
    }
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct Selection(uint Light, uint Face, float Resolution, float Padding = 0);
    void ReleaseMaps()
    {
        for (int f = 0; f < 24; f++) for (int k = 0; k < 4; k++) { targets[f, k]?.Dispose(); targets[f, k] = null; }
        for (int k = 0; k < 4; k++) { views[k]?.Dispose(); views[k] = null; images[k]?.Dispose(); images[k] = null; }
        Array.Clear(lightKeys); size = 0;
    }
    public void Dispose() { ReleaseMaps(); selection.Dispose(); coefficients.Dispose(); }
}
