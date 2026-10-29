using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

[assembly: InternalsVisibleTo("AEther.Smoke")]
[assembly: InternalsVisibleTo("AEther.Tests")]
[assembly: InternalsVisibleTo("AEther.MandelLightProfile")]

namespace AEther.Vortice;

/// <summary>Shared projected flow and shader-defined material attachments.</summary>
internal sealed class FluidSimulation : IDisposable
{
    internal static readonly string[] MaterialNames = Enumerable.Range(0, 8).Select(i => $"FluidMaterial{i}").ToArray();
    internal static readonly string[] PredictionNames = Enumerable.Range(0, 8).Select(i => $"FluidPredicted{i}").ToArray();
    internal enum Pass { PredictVelocity, AdvectVelocity, Divergence, Relax, Residual, Restrict, Prolong, Project, PredictMaterial, UpdateMaterial, DiffuseVelocityX, DiffuseVelocityY, ProjectUnforced }

    internal sealed class Settings
    {
        internal readonly ShaderParameters Values;
        readonly ShaderParameter periodic, lowQuality, viscosity;
        internal int GridSize => Values.GetBoolean(lowQuality) ? 128 : 512;
        internal bool Periodic => Values.GetBoolean(periodic);
        internal bool Viscous => Values.GetFloat(viscosity) > 0;
        internal Settings(ShaderParameters? values)
        {
            Values = values ?? throw new InvalidOperationException("Fluid scenes require Parameters.Fluid settings.");
            var fields = values.Items.FirstOrDefault(p => p.Name == "Fluid")?.Children;
            ShaderParameter Require(string name, bool boolean)
            {
                var field = fields?.FirstOrDefault(p => p.Name == name);
                if (field == null || field.Children.Count != 0 || field.IsBoolean != boolean)
                    throw new InvalidOperationException($"Parameters.Fluid.{name}: expected {(boolean ? "bool" : "float")}.");
                return field;
            }
            periodic = Require("PeriodicBoundary", true);
            lowQuality = Require("LowQuality", true);
            viscosity = Require("Viscosity", false);
        }
    }

    internal sealed class Programs : IDisposable
    {
        internal readonly Dictionary<Pass, ShaderProgram> Shaders = [];
        internal readonly Format[] MaterialFormats;
        internal readonly Settings Configuration;
        internal Programs(ID3D11Device device, string source, string file, ShaderProgram display)
        {
            try
            {
                bool material = MaterialNames.Any(display.Uses);
                foreach (var pass in Enum.GetValues<Pass>())
                {
                    if (!material && pass is Pass.PredictMaterial or Pass.UpdateMaterial) continue;
                    Shaders.Add(pass, new ShaderProgram(device, source, pass.ToString(), file));
                }
                Configuration = new(display.Parameters ?? Shaders.Values.Select(p => p.Parameters).FirstOrDefault(p => p != null));
                MaterialFormats = material ? ReadLayout(Shaders[Pass.UpdateMaterial]) : [];
                if (material && (!MaterialFormats.SequenceEqual(ReadLayout(Shaders[Pass.PredictMaterial]))
                    || !Shaders[Pass.UpdateMaterial].Outputs.OrderBy(o => o.SemanticIndex).Select(o => o.UsageMask)
                        .SequenceEqual(Shaders[Pass.PredictMaterial].Outputs.OrderBy(o => o.SemanticIndex).Select(o => o.UsageMask))))
                    throw new InvalidOperationException($"{file}: PredictMaterial and UpdateMaterial must have matching floating-point SV_Target outputs.");
                foreach (var shader in Shaders.Values.Append(display))
                    for (int i = MaterialFormats.Length; i < 8; i++)
                        if (shader.Uses(MaterialNames[i]) || shader.Uses(PredictionNames[i]))
                            throw new InvalidOperationException($"{file}: material attachment {i} has no matching SV_Target output.");
                if (material && PredictionNames.Any(Shaders[Pass.PredictMaterial].Uses))
                    throw new InvalidOperationException($"{file}: PredictMaterial cannot read its own FluidPredicted outputs.");
            }
            catch { Dispose(); throw; }
        }
        static Format[] ReadLayout(ShaderProgram program)
        {
            var outputs = program.Outputs.OrderBy(o => o.SemanticIndex).ToArray();
            if (outputs.Length is < 1 or > 8) throw new InvalidOperationException("Material passes require one to eight SV_Target outputs.");
            var formats = new Format[outputs.Length];
            for (int i = 0; i < outputs.Length; i++)
            {
                var output = outputs[i];
                if (!output.SemanticName.Equals("SV_Target", StringComparison.OrdinalIgnoreCase)
                    || output.SemanticIndex != i || output.ComponentType != RegisterComponentType.Float32)
                    throw new InvalidOperationException("Material outputs must be contiguous floating-point SV_Target0..7.");
                formats[i] = (int)output.UsageMask switch
                {
                    1 => Format.R32_Float,
                    3 => Format.R32G32_Float,
                    7 or 15 => Format.R32G32B32A32_Float,
                    _ => throw new InvalidOperationException("Material outputs must be float, float2, float3 or float4."),
                };
            }
            return formats;
        }
        public void Dispose() { foreach (var shader in Shaders.Values) shader.Dispose(); }
    }

    sealed class Surface : IDisposable
    {
        internal readonly int Width, Height;
        internal readonly Format Format;
        internal int Channels => Format == Format.R32_Float ? 1 : Format == Format.R32G32_Float ? 2 : 4;
        internal readonly ID3D11Texture2D Texture;
        internal readonly ID3D11ShaderResourceView View;
        internal readonly ID3D11RenderTargetView Target;
        internal Surface(ID3D11Device device, int width, int height, Format format)
        {
            Width = width; Height = height; Format = format;
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

    sealed class Level(Surface pressure, Surface scratch, Surface rhs, Surface residual)
    {
        internal Surface Pressure = pressure, Scratch = scratch;
        internal readonly Surface Rhs = rhs, Residual = residual;
    }

    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly List<IDisposable> resources = [];
    readonly List<Level> levels = [];
    readonly ID3D11Buffer constants;
    readonly ID3D11SamplerState borderSampler, pressureSampler, wrapSampler;
    Surface velocity, nextVelocity;
    readonly Surface predictedVelocity;
    Surface[] material, nextMaterial;
    readonly Surface[] predictedMaterial;
    readonly ID3D11RenderTargetView[] materialTargets;
    readonly Format[] materialFormats;
    Programs programs;
    Parameters parameters;
    double accumulator;
    bool velocityWarmupPending;
    const int InitialVelocitySteps = 2;
    public const double TimeStep = 1.0 / 60;
    public int Width => velocity.Width;
    public int Height => velocity.Height;
    public int GridLimit { get; }
    public float Aspect { get; }
    public int MaterialCount => material.Length;
    public bool PeriodicBoundary { get; private set; }
    public ID3D11SamplerState Sampler => PeriodicBoundary ? wrapSampler : borderSampler;
    ShaderParameters? sceneParameters;
    readonly Dictionary<string, ID3D11Buffer> namedBuffers = new(StringComparer.Ordinal);
    readonly Dictionary<string, ID3D11ShaderResourceView> namedTextures = new(StringComparer.Ordinal);
    readonly Dictionary<string, ID3D11SamplerState> namedSamplers = new(StringComparer.Ordinal);
    readonly Dictionary<(Pass, int, int), string> drawNames = [];
    internal Action<string, bool>? PassMarker { get; set; }
    internal Action<string, bool>? DrawMarker { get; set; }
    internal int LastSteps { get; private set; }
    internal int LastMaterialSteps { get; private set; }
    internal int LastDraws { get; private set; }

    T Own<T>(T resource) where T : IDisposable { resources.Add(resource); return resource; }
    Surface Create(int w, int h, Format format) => Own(new Surface(device, w, h, format));
    internal bool CompatibleWith(Programs next) => GridLimit == next.Configuration.GridSize
        && materialFormats.SequenceEqual(next.MaterialFormats);

    internal FluidSimulation(ID3D11Device device, ID3D11DeviceContext context, int width, int height, Programs programs)
    {
        this.device = device; this.context = context; this.programs = programs;
        Aspect = width / (float)height;
        GridLimit = programs.Configuration.GridSize;
        materialFormats = programs.MaterialFormats;
        int w = GridLimit, h = GridLimit;
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
            predictedVelocity = Create(w, h, Format.R32G32_Float);
            Surface[] Material() => materialFormats.Select(f => Create(w, h, f)).ToArray();
            material = Material(); nextMaterial = Material(); predictedMaterial = Material();
            materialTargets = new ID3D11RenderTargetView[material.Length];
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

    public void Advance(double dt, double sourceAgeRate, Programs shaders, ShaderParameters? sceneParameters,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers,
        IReadOnlyDictionary<string, ID3D11ShaderResourceView> textures,
        IReadOnlyDictionary<string, ID3D11SamplerState> samplers, bool displayMaterial = true)
    {
        programs = shaders;
        LastSteps = LastDraws = LastMaterialSteps = 0;
        this.sceneParameters = sceneParameters;
        PeriodicBoundary = programs.Configuration.Periodic;
        foreach (var pair in buffers) namedBuffers[pair.Key] = pair.Value;
        foreach (var pair in textures) namedTextures[pair.Key] = pair.Value;
        foreach (var pair in samplers) namedSamplers[pair.Key] = pair.Value;
        parameters = new Parameters
        {
            B = new((float)TimeStep, 0, 0, 0),
            C = new((float)Math.Max(0, dt - .05), 0, 0, 0),
        };
        if (velocityWarmupPending)
        {
            for (int i = 0; i < InitialVelocitySteps; i++) StepVelocity();
            velocityWarmupPending = false;
        }
        accumulator += Math.Clamp(dt, 0, .05);
        while (accumulator + 1e-7 >= TimeStep)
        {
            accumulator -= TimeStep;
            parameters.B.Y = (float)(Math.Max(0, accumulator) * sourceAgeRate);
            StepVelocity();
            if (!displayMaterial) { StepMaterial(); parameters.C.X = 0; }
        }
        if (displayMaterial && dt > 0)
        {
            parameters.B.X = (float)Math.Clamp(dt, 0, .05);
            parameters.B.Y = 0;
            StepMaterial();
        }
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
    }

    internal void Publish(IDictionary<string, ID3D11ShaderResourceView> textures)
    {
        textures["FluidVelocity"] = velocity.View;
        for (int i = 0; i < 8; i++)
            if (i < material.Length) textures[MaterialNames[i]] = material[i].View;
            else textures.Remove(MaterialNames[i]);
    }

    void Grid(Surface surface)
    {
        parameters.A = new(surface.Width, surface.Height, 2 * Aspect / surface.Width, 2f / surface.Height);
        context.UpdateSubresource(parameters, constants);
    }

    void Draw(Pass pass, Surface target, Surface? v = null, Surface? pressure = null,
        Surface? rhs = null, Surface? correction = null, Surface? advected = null, Surface[]? outputs = null)
    {
        if (!drawNames.TryGetValue((pass, target.Width, target.Height), out var label))
            drawNames[(pass, target.Width, target.Height)] = label = $"{pass} {target.Width}x{target.Height}";
        DrawMarker?.Invoke(label, true);
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        if (outputs == null) context.OMSetRenderTargets(target.Target);
        else
        {
            for (int i = 0; i < outputs.Length; i++) materialTargets[i] = outputs[i].Target;
            context.OMSetRenderTargets(materialTargets);
        }
        context.RSSetViewport(0, 0, target.Width, target.Height);
        namedBuffers["FluidConstants"] = constants;
        namedSamplers["ClampFluid"] = Sampler;
        namedSamplers["PressureSampler"] = PeriodicBoundary ? wrapSampler : pressureSampler;
        if (v != null) { namedTextures["Velocity"] = v.View; namedTextures["FluidVelocity"] = v.View; }
        if (pressure != null) namedTextures["Pressure"] = pressure.View;
        if (rhs != null) namedTextures["Rhs"] = rhs.View;
        if (correction != null) namedTextures["Correction"] = correction.View;
        if (advected != null) namedTextures["Advected"] = advected.View;
        programs.Shaders[pass].Bind(context, sceneParameters, namedBuffers, namedTextures, namedSamplers);
        context.Draw(3, 0);
        LastDraws++;
        DrawMarker?.Invoke(label, false);
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

    void VCycle(int index)
    {
        var level = levels[index];
        if (index == levels.Count - 1) { Smooth(level, 16); return; }
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

    void Project(bool unforced = false)
    {
        PassMarker?.Invoke("Fluid pressure", true);
        Grid(velocity);
        Draw(Pass.Divergence, levels[0].Rhs, v: velocity);
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.ClearRenderTargetView(levels[0].Pressure.Target, new(0, 0, 0, 0));
        for (int cycle = 0; cycle < 3; cycle++) VCycle(0);
        Grid(velocity);
        Draw(unforced ? Pass.ProjectUnforced : Pass.Project, nextVelocity, v: velocity, pressure: levels[0].Pressure);
        (velocity, nextVelocity) = (nextVelocity, velocity);
        PassMarker?.Invoke("Fluid pressure", false);
    }

    void StepVelocity()
    {
        LastSteps++;
        PassMarker?.Invoke("Fluid velocity", true);
        BindMaterialInputs();
        Grid(velocity);
        Draw(Pass.PredictVelocity, predictedVelocity, v: velocity);
        Draw(Pass.AdvectVelocity, nextVelocity, v: velocity, advected: predictedVelocity);
        (velocity, nextVelocity) = (nextVelocity, velocity);
        if (programs.Configuration.Viscous)
        {
            Draw(Pass.DiffuseVelocityX, nextVelocity, v: velocity);
            (velocity, nextVelocity) = (nextVelocity, velocity);
            Draw(Pass.DiffuseVelocityY, nextVelocity, v: velocity);
            (velocity, nextVelocity) = (nextVelocity, velocity);
        }
        PassMarker?.Invoke("Fluid velocity", false);
        Project();
    }

    void StepMaterial()
    {
        if (material.Length == 0) return;
        LastMaterialSteps++;
        PassMarker?.Invoke("Fluid material", true);
        Grid(material[0]);
        BindMaterialInputs();
        Draw(Pass.PredictMaterial, predictedMaterial[0], v: velocity, outputs: predictedMaterial);
        Draw(Pass.UpdateMaterial, nextMaterial[0], v: velocity, outputs: nextMaterial);
        (material, nextMaterial) = (nextMaterial, material);
        PassMarker?.Invoke("Fluid material", false);
    }

    void BindMaterialInputs()
    {
        for (int i = 0; i < material.Length; i++)
        {
            namedTextures[MaterialNames[i]] = material[i].View;
            namedTextures[PredictionNames[i]] = predictedMaterial[i].View;
        }
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
            var values = ReadFloatSurface(surface);
            if (values.Any(v => !float.IsFinite(v))) throw new InvalidOperationException("Nonfinite fluid state.");
            return Math.Sqrt(values.Average(v => (double)v * v));
        }
        var before = Rms(levels[0].Rhs);
        Project(unforced: true);
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

    internal float[] ReadVelocity() => ReadFloatSurface(velocity);
    internal float[] ReadMaterial(int attachment) => ReadFloatSurface(material[attachment]);
    internal void WriteVelocity(float[] values) => WriteFloatSurface(velocity, values);
    internal void WriteMaterial(int attachment, float[] values) => WriteFloatSurface(material[attachment], values);
    internal int MaterialChannels(int attachment) => material[attachment].Channels;
    unsafe void WriteFloatSurface(Surface surface, float[] values)
    {
        if (values.Length != surface.Width * surface.Height * surface.Channels) throw new ArgumentException("Surface data size mismatch.", nameof(values));
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        fixed (float* p = values) context.UpdateSubresource(surface.Texture, 0, null, (IntPtr)p, (uint)(surface.Width * surface.Channels * 4), 0);
    }
    float[] ReadFloatSurface(Surface surface)
    {
        int stride = surface.Width * surface.Channels * 4;
        var desc = surface.Texture.Description;
        desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None; desc.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = device.CreateTexture2D(desc);
        context.CopyResource(staging, surface.Texture);
        var map = context.Map(staging, 0, MapMode.Read, global::Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bytes = new byte[stride * surface.Height];
            for (int y = 0; y < surface.Height; y++)
                Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, bytes, y * stride, stride);
            return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        }
        finally { context.Unmap(staging, 0); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Parameters { public Vector4 A, B, C; }
    public void Dispose() { for (int i = resources.Count - 1; i >= 0; i--) resources[i].Dispose(); }
}
