using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

// A scaled RGB tail integral for homogeneous Rayleigh scattering. Generation is
// CPU-only background work; texture creation/upload remains on the render thread.
internal sealed class UnshadowedAirlightLookup : IDisposable
{
    internal const int Width = 513, Height = 1025;
    internal const float MaximumDistance = 128;
    // Coordinate sampling density only: the kernel remains exactly 1/r^2.
    internal const float DistanceScale = .0001f;
    readonly ID3D11DeviceContext context;
    readonly ID3D11Texture2D texture;
    readonly List<Table> cache = [];
    Task<Table>? pending;
    Key? uploaded;
    internal ID3D11ShaderResourceView View { get; }
    internal ID3D11SamplerState Sampler { get; }
    internal Vector4 Info { get; private set; }
    internal bool Ready => Info.X != 0;
    readonly record struct Key(Vector3 Extinction);
    sealed record Table(Key Key, float[] Data);

    internal UnshadowedAirlightLookup(ID3D11Device device, ID3D11DeviceContext context)
    {
        this.context = context;
        texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = Width, Height = Height, ArraySize = 1, MipLevels = 1,
            Format = Format.R32G32B32A32_Float, SampleDescription = new(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });
        try
        {
            View = device.CreateShaderResourceView(texture);
            try
            {
                Sampler = device.CreateSamplerState(new SamplerDescription
                {
                    Filter = Filter.MinMagLinearMipPoint,
                    AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
                    MinLOD = 0, MaxLOD = 0,
                });
            }
            catch { View.Dispose(); throw; }
        }
        catch { texture.Dispose(); throw; }
    }

    internal void Prepare(Vector3 extinction, bool needed)
    {
        var key = new Key(extinction);
        Info = new(0, MaximumDistance, MathF.Log(1+MaximumDistance/DistanceScale), DistanceScale);
        if (pending is { IsCompleted: true })
        {
            // Observe failures as well as successes; no GPU objects are touched
            // by the task, including when the renderer has already been disposed.
            var table = pending.GetAwaiter().GetResult();
            pending = null;
            cache.RemoveAll(item => item.Key == table.Key);
            cache.Insert(0, table);
            if (cache.Count > 4) cache.RemoveAt(cache.Count - 1);
        }
        if (!needed) return;
        if (uploaded != key)
        {
            int index = cache.FindIndex(item => item.Key == key);
            if (index >= 0)
            {
                var table = cache[index]; cache.RemoveAt(index); cache.Insert(0, table);
                var pin = GCHandle.Alloc(table.Data, GCHandleType.Pinned);
                try { context.UpdateSubresource(texture, 0, null, pin.AddrOfPinnedObject(), Width * 16, 0); }
                finally { pin.Free(); }
                uploaded = key;
            }
            else if (pending == null) pending = Task.Run(() => new Table(key, Build(extinction)));
        }
        // Never sample a table for different coefficients. The shader uses
        // deterministic angular quadrature until this particular key is ready.
        if (uploaded == key) Info = Info with { X = 1 };
    }

    internal static float[] Build(Vector3 extinction)
    {
        double warp = Math.Log(1+MaximumDistance/(double)DistanceScale);
        double[] e = [extinction.X, extinction.Y, extinction.Z];
        var data = new float[Width * Height * 4];
        double[] nodes = [-.8611363115940526, -.3399810435848563, .3399810435848563, .8611363115940526];
        double[] weights = [.3478548451374538, .6521451548625461, .6521451548625461, .3478548451374538];
        static double Angle(double q)
        {
            double v = 2 * q - 1;
            return Math.CopySign(Math.PI * .5 * (1 - (1 - Math.Abs(v)) * (1 - Math.Abs(v))), v);
        }
        static double U(double b, double height, double angle)
        {
            if (b==0) return 0; // Analytic b*tail limit; no emitter core.
            double x = height * Math.Tan(angle), r = Math.Sqrt(x * x + b * b);
            return x < 0 ? b * b / (r - x) : x + r;
        }
        Parallel.For(0, Width, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, column =>
        {
            double b = DistanceScale * (Math.Exp(warp * column / (Width - 1))-1);
            double height = b;
            double[] tail = new double[3];
            double[] sum = new double[3];
            for (int row = Height - 2; row >= 0; row--)
            {
                double angle = Angle(row / (double)(Height - 1));
                double step = Angle((row + 1.0) / (Height - 1)) - angle;
                double start = U(b, height, angle), end = U(b, height, angle + step);
                Array.Clear(sum);
                for (int j = 0; j < 4; j++)
                {
                    double at = angle + .5 * step * (1 + nodes[j]);
                    double sine=Math.Sin(at);
                    double phase = 3 / (16 * Math.PI) * (1+sine*sine);
                    double length = Math.Max(0, U(b, height, at) - start);
                    for (int channel = 0; channel < 3; channel++)
                        sum[channel] += weights[j] * phase * Math.Exp(-e[channel] * length);
                }
                for (int channel = 0; channel < 3; channel++)
                {
                    tail[channel] = .5 * step * sum[channel] + Math.Exp(-e[channel] * Math.Max(0, end - start)) * tail[channel];
                    data[4 * (row * Width + column) + channel] = (float)tail[channel];
                }
            }
        });
        return data;
    }

    public void Dispose() { Sampler.Dispose(); View.Dispose(); texture.Dispose(); }
}
