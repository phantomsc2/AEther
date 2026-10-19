using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

[assembly: InternalsVisibleTo("AEther.Smoke")]
[assembly: InternalsVisibleTo("AEther.Tests")]

namespace AEther.Vortice;

/// <summary>Persistent pigment and projected turbulence, owned by the render thread.</summary>
internal sealed class MandelFluidSimulation : IDisposable
{
    internal enum Pass { PredictVelocity, AdvectVelocity, Divergence, Relax, Residual, Restrict, Prolong, Project, PredictDye, AdvectDye, DiffuseVelocityX, DiffuseVelocityY }
    internal sealed class Programs : IDisposable
    {
        internal readonly Dictionary<Pass, ShaderProgram> Shaders = [];
        public ID3D11PixelShader this[Pass pass] => Shaders[pass].Pixel;
        public bool CorrectedAdvection => Shaders[Pass.AdvectVelocity].Uses("Advected");
        public bool TransportsAge => Shaders[Pass.PredictDye].Uses("DyeAge");
        public bool RingFluid { get; }
        public Programs(ID3D11Device device, string source)
        {
            RingFluid = source.Contains("#define RING_FLUID", StringComparison.Ordinal);
            try
            {
                foreach (var pass in Enum.GetValues<Pass>())
                {
                    if (!RingFluid && pass is Pass.DiffuseVelocityX or Pass.DiffuseVelocityY) continue;
                    Shaders.Add(pass, new ShaderProgram(device, source, pass.ToString(), "FluidSimulation.hlsl"));
                }
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
    readonly ID3D11SamplerState borderSampler, pressureSampler, wrapSampler;
    Surface velocity, nextVelocity, dye, nextDye;
    Surface? predictedVelocity, predictedDye;
    Surface? age, nextAge, predictedAge;
    Programs programs = null!; // Borrowed from Renderer, swapped atomically on reload.
    Parameters parameters;
    double accumulator;
    bool velocityWarmupPending;
    const int InitialVelocitySteps = 8;
    public const double TimeStep = 1.0 / 60;
    public int Width => velocity.Width;
    public int Height => velocity.Height;
    public int GridLimit { get; }
    public bool SquareGrid { get; }
    public float Aspect { get; }
    public ID3D11ShaderResourceView Dye => dye.View;
    public ID3D11ShaderResourceView? Age => age?.View;
    public bool PeriodicBoundary { get; private set; }
    public ID3D11SamplerState Sampler => PeriodicBoundary ? wrapSampler : borderSampler;
    ShaderParameters? sceneParameters;
    readonly Dictionary<string, ID3D11Buffer> namedBuffers = new(StringComparer.Ordinal);
    readonly Dictionary<string, ID3D11ShaderResourceView> namedTextures = new(StringComparer.Ordinal);
    readonly Dictionary<string, ID3D11SamplerState> namedSamplers = new(StringComparer.Ordinal);

    T Own<T>(T resource) where T : IDisposable { resources.Add(resource); return resource; }
    Surface Create(int w, int h, Format format) => Own(new Surface(device, w, h, format));

    public MandelFluidSimulation(ID3D11Device device, ID3D11DeviceContext context, int width, int height, int gridLimit = 256, bool squareGrid = false)
    {
        this.device = device; this.context = context;
        Aspect = width / (float)height;
        GridLimit = gridLimit;
        SquareGrid = squareGrid;
        // Power-of-two levels guarantee exact 2:1 restriction, even for odd windows.
        // Bound cost independently of display resolution; pressure stays in float32.
        int Size(float n) => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(n, 32, gridLimit));
        int w = squareGrid ? gridLimit : Size(gridLimit * Math.Min(Aspect, 1));
        int h = squareGrid ? gridLimit : Size(gridLimit / Math.Max(Aspect, 1));
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
            wrapSampler = Own(device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagLinearMipPoint, AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap,
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
        velocityWarmupPending = true;
    }

    public void Advance(double dt, AudioHistory audio, Programs shaders, ShaderParameters? sceneParameters = null,
        IReadOnlyDictionary<string, ID3D11Buffer>? buffers = null,
        IReadOnlyDictionary<string, ID3D11ShaderResourceView>? textures = null,
        IReadOnlyDictionary<string, ID3D11SamplerState>? samplers = null)
    {
        programs = shaders;
        EnsureMaterial();
        EnsureAge();
        this.sceneParameters = sceneParameters;
        var boundary = sceneParameters?.Items.FirstOrDefault(p => p.Name == "Fluid")?.Children
            .FirstOrDefault(p => p.Name == "PeriodicBoundary");
        PeriodicBoundary = boundary != null && sceneParameters!.GetBoolean(boundary);
        if (buffers != null) foreach (var pair in buffers) namedBuffers[pair.Key] = pair.Value;
        if (textures != null) foreach (var pair in textures) namedTextures[pair.Key] = pair.Value;
        if (samplers != null) foreach (var pair in samplers) namedSamplers[pair.Key] = pair.Value;
        parameters = new Parameters
        {
            B = new((float)TimeStep, 0, 0, 0),
            // Age follows elapsed time even when motion drops a stalled interval.
            C = new((float)Math.Max(0, dt - .05), 0, 0, 0),
        };
        if (programs.RingFluid && velocityWarmupPending && audio.Energy > 0)
        {
            // Establish inlet flow and pressure before introducing the first dye.
            // Use the live source without advancing material age or audio history.
            for (int i = 0; i < InitialVelocitySteps; i++) StepVelocity();
            velocityWarmupPending = false;
        }
        // Fixed steps avoid frame-rate-dependent decay and forcing. Drop excess time
        // after stalls/minimize rather than replaying seconds of stale injection.
        accumulator += Math.Clamp(dt, 0, .05);
        while (accumulator + 1e-10 >= TimeStep)
        {
            accumulator -= TimeStep;
            parameters.B.Y = (float)(accumulator / audio.Seconds) * audio.TimeScale;
            Step();
            parameters.C.X = 0;
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
        Surface? pressure = null, Surface? rhs = null, Surface? correction = null, Surface? advected = null,
        Surface? ageInput = null, Surface? agePrediction = null, Surface? ageTarget = null)
    {
        // Unbind reads before changing the target; no pass reads its own render target.
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        if (ageTarget != null) context.OMSetRenderTargets([target.Target, ageTarget.Target]);
        else context.OMSetRenderTargets(target.Target);
        context.RSSetViewport(0, 0, target.Width, target.Height);
        namedBuffers["FluidConstants"] = constants;
        namedSamplers["ClampFluid"] = Sampler;
        namedSamplers["PressureSampler"] = PeriodicBoundary ? wrapSampler : pressureSampler;
        if (v != null) namedTextures["Velocity"] = v.View;
        if (pigment != null) namedTextures["Dye"] = pigment.View;
        if (pressure != null) namedTextures["Pressure"] = pressure.View;
        if (rhs != null) namedTextures["Rhs"] = rhs.View;
        if (correction != null) namedTextures["Correction"] = correction.View;
        if (advected != null) namedTextures["Advected"] = advected.View;
        if (ageInput != null) namedTextures["DyeAge"] = ageInput.View;
        if (agePrediction != null) namedTextures["AdvectedAge"] = agePrediction.View;
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
        // Finish low spatial frequencies on the tiny coarse grid rather than
        // leaving them for the expensive fine-grid smoother to propagate.
        if (index == levels.Count - 1) { Smooth(level, 64); return; }
        Smooth(level, 3);
        Draw(Pass.Residual, level.Residual, pressure: level.Pressure, rhs: level.Rhs);
        var coarse = levels[index + 1];
        Draw(Pass.Restrict, coarse.Rhs, rhs: level.Residual);
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.ClearRenderTargetView(coarse.Pressure.Target, new(0, 0, 0, 0));
        VCycle(index + 1);
        Grid(level.Pressure);
        Draw(Pass.Prolong, level.Scratch, pressure: level.Pressure, correction: coarse.Pressure);
        (level.Pressure, level.Scratch) = (level.Scratch, level.Pressure);
        Smooth(level, 3);
    }

    void Project()
    {
        Grid(velocity);
        Draw(Pass.Divergence, levels[0].Rhs, v: velocity);
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.ClearRenderTargetView(levels[0].Pressure.Target, new(0, 0, 0, 0));
        VCycle(0);
        VCycle(0);
        VCycle(0);
        Grid(velocity);
        Draw(Pass.Project, nextVelocity, v: velocity, pressure: levels[0].Pressure);
        (velocity, nextVelocity) = (nextVelocity, velocity);
    }

    void EnsureMaterial()
    {
        // V4 keeps raw STB and density in FP32 for repeated material transport.
        var format = programs.RingFluid ? Format.R32G32B32A32_Float : Format.R16G16B16A16_Float;
        if (dye.Texture.Description.Format == format) return;
        dye = Create(Width, Height, format);
        nextDye = Create(Width, Height, format);
        predictedDye = null;
        Clear();
    }
    void EnsureAge()
    {
        if (!programs.TransportsAge) return;
        var format = Format.R32G32_Float;
        if (age != null && age.Texture.Description.Format == format) return;
        age = Create(Width, Height, format);
        nextAge = Create(Width, Height, format);
        predictedAge = Create(Width, Height, format);
        // A material layout change on shader reload invalidates the old dye too.
        if (programs.RingFluid) Clear();
        else context.ClearRenderTargetView(age.Target, new(0, 0, 0, 0));
    }
    void StepVelocity()
    {
        Grid(velocity);
        if (programs.CorrectedAdvection)
        {
            predictedVelocity ??= Create(Width, Height, Format.R32G32_Float);
            Draw(Pass.PredictVelocity, predictedVelocity, v: velocity);
        }
        Draw(Pass.AdvectVelocity, nextVelocity, v: velocity, advected: predictedVelocity);
        (velocity, nextVelocity) = (nextVelocity, velocity);
        if (programs.RingFluid)
        {
            var viscosity = sceneParameters?.Items.FirstOrDefault(p => p.Name == "Fluid")?.Children
                .FirstOrDefault(p => p.Name == "Viscosity");
            if (viscosity != null && sceneParameters!.GetFloat(viscosity) > 0)
            {
                Draw(Pass.DiffuseVelocityX, nextVelocity, v: velocity);
                (velocity, nextVelocity) = (nextVelocity, velocity);
                Draw(Pass.DiffuseVelocityY, nextVelocity, v: velocity);
                (velocity, nextVelocity) = (nextVelocity, velocity);
            }
        }
        Project();
    }
    void Step()
    {
        EnsureMaterial();
        EnsureAge();
        StepVelocity();
        if (programs.CorrectedAdvection)
        {
            predictedDye ??= Create(Width, Height, dye.Texture.Description.Format);
            Draw(Pass.PredictDye, predictedDye!, v: velocity, pigment: dye,
                ageInput: age, ageTarget: programs.TransportsAge ? predictedAge : null);
        }
        Draw(Pass.AdvectDye, nextDye, v: velocity, pigment: dye, advected: predictedDye,
            ageInput: age, agePrediction: predictedAge, ageTarget: programs.TransportsAge ? nextAge : null);
        (dye, nextDye) = (nextDye, dye);
        if (programs.TransportsAge) (age, nextAge) = (nextAge, age);
    }

    // GPU smoke checks use the actual production passes at deterministic time steps.
    internal void AdvanceForCheck(Programs shaders, int steps, float halfLife = 1.4f)
    {
        programs = shaders;
        parameters.B = new((float)TimeStep, 0, MathF.Log(2) / halfLife, .001f);
        parameters.C = default;
        for (int i = 0; i < steps; i++) Step();
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
    }

    internal unsafe (double Before, double After, double CurlError) CheckProjection(Programs shaders)
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
        var projected = ReadVelocity();
        double curlPower = 0, curlError = 0;
        double Curl(float[] v, int x, int y)
        {
            int i = 2 * (y * Width + x);
            return (v[i + 3] - v[i + 1]) / (2 * Aspect / Width)
                - (v[i + 2 * Width] - v[i]) / (2.0 / Height);
        }
        for (int y = 0; y < Height - 1; y++)
            for (int x = 0; x < Width - 1; x++)
            {
                double original = Curl(data, x, y), difference = Curl(projected, x, y) - original;
                curlPower += original * original;
                curlError += difference * difference;
            }
        Clear();
        return (before, after, Math.Sqrt(curlError / Math.Max(1e-20, curlPower)));
    }

    internal float[] ReadVelocity() => ReadFloatSurface(velocity, 2);
    /// <summary>GPU transport fixture: a small dyed patch near the right edge in a uniform current.</summary>
    internal unsafe void SeedRecirculationForCheck(float densityScale = 1, float speed = .8f)
    {
        var flow = new float[Width * Height * 2];
        var pigment = new Half[Width * Height * 4];
        var residence = new float[Width * Height * 2];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int cell = y * Width + x;
                flow[2 * cell] = speed;
                double px = (2 * (x + .5) / Width - 1) * Aspect;
                double py = 2 * (y + .5) / Height - 1;
                float density = densityScale * (float)Math.Exp(-((px - Aspect + .16) * (px - Aspect + .16) + py * py) / .003);
                pigment[4 * cell] = pigment[4 * cell + 3] = (Half)density;
                residence[2 * cell] = .5f * (float)(Half)density;
                residence[2 * cell + 1] = (float)(Half)density;
            }
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        fixed (float* p = flow) context.UpdateSubresource(velocity.Texture, 0, null, (IntPtr)p, (uint)(Width * 8), 0);
        fixed (Half* p = pigment) context.UpdateSubresource(dye.Texture, 0, null, (IntPtr)p, (uint)(Width * 8), 0);
        if (age != null)
            fixed (float* p = residence) context.UpdateSubresource(age.Texture, 0, null, (IntPtr)p, (uint)(Width * 8), 0);
    }
    internal float[] ReadAge() => ReadFloatSurface(age!, 2);
    internal unsafe void AddAgeForCheck(float seconds)
    {
        var values = ReadAge();
        for (int i = 0; i < values.Length; i += 2) values[i] += seconds * values[i + 1];
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        fixed (float* p = values) context.UpdateSubresource(age!.Texture, 0, null, (IntPtr)p, (uint)(Width * 8), 0);
    }
    internal float[] ReadDye()
    {
        if (dye.Texture.Description.Format == Format.R32G32B32A32_Float)
            return ReadFloatSurface(dye, 4);
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
