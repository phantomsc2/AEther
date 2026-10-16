using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

[assembly: InternalsVisibleTo("AEther.Smoke")]

namespace AEther.Vortice;

/// <summary>Persistent pigment and projected turbulence, owned by the render thread.</summary>
internal sealed class MandelFluidSimulation : IDisposable
{
    internal enum Pass { AdvectVelocity, Divergence, Relax, Residual, Restrict, Prolong, Project, AdvectDye }
    internal sealed class Programs : IDisposable
    {
        internal readonly Dictionary<Pass, ShaderProgram> Shaders = [];
        public ID3D11PixelShader this[Pass pass] => Shaders[pass].Pixel;
        public Programs(ID3D11Device device, string source)
        {
            try
            {
                foreach (var pass in Enum.GetValues<Pass>())
                    Shaders.Add(pass, new ShaderProgram(device, source, pass.ToString(), "FluidSimulation.hlsl"));
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var shader in Shaders.Values) shader.Dispose(); }
    }

    internal sealed class Surface : IDisposable
    {
        public readonly int Width, Height;
        public readonly ID3D11Texture2D Texture;
        public readonly ID3D11ShaderResourceView View;
        public readonly ID3D11RenderTargetView Target;
        public Surface(ID3D11Device device, int width, int height, Format format)
        {
            Width = width; Height = height;
            Texture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = format, SampleDescription = new(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            });
            try { View = device.CreateShaderResourceView(Texture); }
            catch { Texture.Dispose(); throw; }
            try { Target = device.CreateRenderTargetView(Texture); }
            catch { View.Dispose(); Texture.Dispose(); throw; }
        }
        public void Dispose() { Target.Dispose(); View.Dispose(); Texture.Dispose(); }
    }

    sealed class Level
    {
        public Surface Pressure, Scratch;
        public readonly Surface Rhs, Residual;
        public Level(Surface pressure, Surface scratch, Surface rhs, Surface residual)
        { Pressure = pressure; Scratch = scratch; Rhs = rhs; Residual = residual; }
    }

    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly List<IDisposable> resources = [];
    readonly List<Level> levels = [];
    readonly ID3D11Buffer constants;
    readonly ID3D11SamplerState borderSampler, pressureSampler;
    Surface velocity, nextVelocity, dye, nextDye;
    Programs programs = null!; // Borrowed from Renderer, swapped atomically on reload.
    Parameters parameters;
    double accumulator;
    public const double TimeStep = 1.0 / 60;
    public int Width => velocity.Width;
    public int Height => velocity.Height;
    public float Aspect { get; }
    public ID3D11ShaderResourceView Dye => dye.View;
    public ID3D11SamplerState Sampler => borderSampler;
    ShaderParameters? sceneParameters;
    readonly Dictionary<string, ID3D11Buffer> namedBuffers = new(StringComparer.Ordinal);
    readonly Dictionary<string, ID3D11ShaderResourceView> namedTextures = new(StringComparer.Ordinal);
    readonly Dictionary<string, ID3D11SamplerState> namedSamplers = new(StringComparer.Ordinal);

    T Own<T>(T resource) where T : IDisposable { resources.Add(resource); return resource; }
    Surface Create(int w, int h, Format format) => Own(new Surface(device, w, h, format));

    public MandelFluidSimulation(ID3D11Device device, ID3D11DeviceContext context, int width, int height)
    {
        this.device = device; this.context = context;
        Aspect = width / (float)height;
        // Power-of-two levels guarantee exact 2:1 restriction, even for odd windows.
        // Bound cost independently of display resolution; pressure stays in float32.
        static int Size(float n) => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(n, 32, 256));
        int w = Size(256 * Math.Min(Aspect, 1)), h = Size(256 / Math.Max(Aspect, 1));
        try
        {
            constants = Own(device.CreateBuffer(48, BindFlags.ConstantBuffer));
            borderSampler = Own(device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagLinearMipPoint, AddressU = TextureAddressMode.Border,
                AddressV = TextureAddressMode.Border, AddressW = TextureAddressMode.Border,
                BorderColor = new Color4(0, 0, 0, 0), MaxLOD = float.MaxValue,
                ComparisonFunc = ComparisonFunction.Never,
            }));
            pressureSampler = Own(device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagLinearMipPoint, AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
                MaxLOD = float.MaxValue, ComparisonFunc = ComparisonFunction.Never,
            }));
            velocity = Create(w, h, Format.R32G32_Float);
            nextVelocity = Create(w, h, Format.R32G32_Float);
            dye = Create(w, h, Format.R16G16B16A16_Float);
            nextDye = Create(w, h, Format.R16G16B16A16_Float);
            while (true)
            {
                levels.Add(new(Create(w, h, Format.R32_Float), Create(w, h, Format.R32_Float),
                    Create(w, h, Format.R32_Float), Create(w, h, Format.R32_Float)));
                if (Math.Min(w, h) <= 4) break;
                w /= 2; h /= 2;
            }
            Clear();
        }
        catch { Dispose(); throw; }
    }

    public void Clear()
    {
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        foreach (var surface in resources.OfType<Surface>()) context.ClearRenderTargetView(surface.Target, new(0, 0, 0, 0));
        accumulator = 0;
    }

    public void Advance(double dt, AudioHistory audio, Programs shaders, ShaderParameters? sceneParameters = null,
        IReadOnlyDictionary<string, ID3D11Buffer>? buffers = null,
        IReadOnlyDictionary<string, ID3D11ShaderResourceView>? textures = null,
        IReadOnlyDictionary<string, ID3D11SamplerState>? samplers = null)
    {
        programs = shaders;
        this.sceneParameters = sceneParameters;
        if (buffers != null) foreach (var pair in buffers) namedBuffers[pair.Key] = pair.Value;
        if (textures != null) foreach (var pair in textures) namedTextures[pair.Key] = pair.Value;
        if (samplers != null) foreach (var pair in samplers) namedSamplers[pair.Key] = pair.Value;
        parameters = new Parameters
        {
            B = new((float)TimeStep, 0, 0, 0),
            C = default,
        };
        // Fixed steps avoid frame-rate-dependent decay and forcing. Drop excess time
        // after stalls/minimize rather than replaying seconds of stale injection.
        accumulator += Math.Clamp(dt, 0, .05);
        while (accumulator + 1e-10 >= TimeStep)
        {
            accumulator -= TimeStep;
            parameters.B.Y = (float)(accumulator / audio.Seconds) * audio.TimeScale;
            Step();
        }
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
    }

    void Grid(Surface surface)
    {
        parameters.A = new(surface.Width, surface.Height, 2 * Aspect / surface.Width, 2f / surface.Height);
        context.UpdateSubresource(parameters, constants);
    }

    void Draw(Pass pass, Surface target, Surface? v = null, Surface? pigment = null,
        Surface? pressure = null, Surface? rhs = null, Surface? correction = null)
    {
        // Unbind reads before changing the target; no pass reads its own render target.
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(target.Target);
        context.RSSetViewport(0, 0, target.Width, target.Height);
        namedBuffers["FluidConstants"] = constants;
        namedSamplers["ClampFluid"] = borderSampler;
        namedSamplers["PressureSampler"] = pressureSampler;
        if (v != null) namedTextures["Velocity"] = v.View;
        if (pigment != null) namedTextures["Dye"] = pigment.View;
        if (pressure != null) namedTextures["Pressure"] = pressure.View;
        if (rhs != null) namedTextures["Rhs"] = rhs.View;
        if (correction != null) namedTextures["Correction"] = correction.View;
        programs.Shaders[pass].Bind(context, sceneParameters, namedBuffers, namedTextures, namedSamplers);
        context.Draw(3, 0);
    }

    void Smooth(Level level, int iterations)
    {
        Grid(level.Pressure);
        for (var i = 0; i < iterations; i++)
        {
            Draw(Pass.Relax, level.Scratch, pressure: level.Pressure, rhs: level.Rhs);
            (level.Pressure, level.Scratch) = (level.Scratch, level.Pressure);
        }
    }

    // The historical V-cycle: pre-smooth, restrict residual, solve correction,
    // prolong/add, post-smooth. More levels resolve the global modes the old
    // 32-cell cutoff left to relaxation. Weighted Jacobi is parallel and symmetric.
    void VCycle(int index)
    {
        var level = levels[index];
        if (index == levels.Count - 1) { Smooth(level, 16); return; }
        Smooth(level, 2);
        Draw(Pass.Residual, level.Residual, pressure: level.Pressure, rhs: level.Rhs);
        var coarse = levels[index + 1];
        Draw(Pass.Restrict, coarse.Rhs, rhs: level.Residual);
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.ClearRenderTargetView(coarse.Pressure.Target, new(0, 0, 0, 0));
        VCycle(index + 1);
        Grid(level.Pressure);
        Draw(Pass.Prolong, level.Scratch, pressure: level.Pressure, correction: coarse.Pressure);
        (level.Pressure, level.Scratch) = (level.Scratch, level.Pressure);
        Smooth(level, 2);
    }

    void Project()
    {
        Grid(velocity);
        Draw(Pass.Divergence, levels[0].Rhs, v: velocity);
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.ClearRenderTargetView(levels[0].Pressure.Target, new(0, 0, 0, 0));
        VCycle(0);
        VCycle(0);
        Grid(velocity);
        Draw(Pass.Project, nextVelocity, v: velocity, pressure: levels[0].Pressure);
        (velocity, nextVelocity) = (nextVelocity, velocity);
    }

    void Step()
    {
        Grid(velocity);
        Draw(Pass.AdvectVelocity, nextVelocity, v: velocity);
        (velocity, nextVelocity) = (nextVelocity, velocity);
        Project();
        Draw(Pass.AdvectDye, nextDye, v: velocity, pigment: dye);
        (dye, nextDye) = (nextDye, dye);
    }

    // GPU smoke checks use the actual production passes at deterministic time steps.
    internal void AdvanceForCheck(Programs shaders, int steps, float halfLife = 1.4f)
    {
        programs = shaders;
        parameters.B = new((float)TimeStep, 0, MathF.Log(2) / halfLife, .001f);
        parameters.C = new(1, 1, .45f, 0);
        for (int i = 0; i < steps; i++) Step();
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
    }

    internal unsafe (double Before, double After) CheckProjection(Programs shaders)
    {
        programs = shaders;
        var data = new float[Width * Height * 2];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                var i = 2 * (y * Width + x);
                data[i] = x == Width - 1 ? 0 : (float)(.3 * Math.Sin(2 * Math.PI * (x + 1) / Width)
                    * Math.Cos(4 * Math.PI * (y + .5) / Height));
                data[i + 1] = y == Height - 1 ? 0 : (float)(.2 * Math.Sin(2 * Math.PI * (y + 1) / Height)
                    * Math.Cos(6 * Math.PI * (x + .5) / Width));
            }
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        fixed (float* p = data) context.UpdateSubresource(velocity.Texture, 0, null, (IntPtr)p, (uint)(Width * 8), 0);
        Grid(velocity);
        Draw(Pass.Divergence, levels[0].Rhs, v: velocity);
        double Rms(Surface surface)
        {
            var values = ReadFloatSurface(surface, 1);
            if (values.Any(v => !float.IsFinite(v))) throw new InvalidOperationException("Nonfinite fluid state.");
            return Math.Sqrt(values.Average(v => (double)v * v));
        }
        var before = Rms(levels[0].Rhs);
        Project();
        Draw(Pass.Divergence, levels[0].Rhs, v: velocity);
        var after = Rms(levels[0].Rhs);
        Clear();
        return (before, after);
    }

    internal float[] ReadVelocity() => ReadFloatSurface(velocity, 2);
    internal float[] ReadDye()
    {
        var bytes = ReadSurface(dye, 8);
        return MemoryMarshal.Cast<byte, Half>(bytes).ToArray().Select(h => (float)h).ToArray();
    }
    float[] ReadFloatSurface(Surface surface, int components) =>
        MemoryMarshal.Cast<byte, float>(ReadSurface(surface, 4 * components)).ToArray();
    byte[] ReadSurface(Surface surface, int bytesPerPixel)
    {
        var desc = surface.Texture.Description;
        desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None; desc.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = device.CreateTexture2D(desc);
        context.CopyResource(staging, surface.Texture);
        var map = context.Map(staging, 0, MapMode.Read, global::Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bytes = new byte[surface.Width * surface.Height * bytesPerPixel];
            for (int y = 0; y < surface.Height; y++)
                Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, bytes,
                    y * surface.Width * bytesPerPixel, surface.Width * bytesPerPixel);
            return bytes;
        }
        finally { context.Unmap(staging, 0); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Parameters { public Vector4 A, B, C; }
    public void Dispose() { for (int i = resources.Count - 1; i >= 0; i--) resources[i].Dispose(); }
}
