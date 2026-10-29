using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D11.Shader;

namespace AEther.Vortice;

internal sealed class ShaderProgram : IDisposable
{
    internal static readonly ID3D11ShaderResourceView[] EmptyViews = new ID3D11ShaderResourceView[128];
    public ID3D11PixelShader Pixel { get; } = null!;
    readonly ID3D11VertexShader? vertex;
    readonly ID3D11ComputeShader? compute;
    readonly char stage;
    public ShaderParameters? Parameters { get; }
    readonly ID3D11Buffer? parameterBuffer;
    readonly byte[]? uploaded;
    bool hasUploaded;
    ShaderParameters? copiedFrom;
    long copiedVersion = -1;
    long localVersion = -1;
    readonly Dictionary<string, InputBindingDescription> bindings = new(StringComparer.Ordinal);
    public bool Uses(string name) => bindings.ContainsKey(name);
    internal readonly List<ShaderParameterDescription> Outputs = [];
    internal static bool MaterialResource(string name, string prefix) => name.Length == prefix.Length + 1
        && name.StartsWith(prefix, StringComparison.Ordinal) && name[^1] is >= '0' and <= '7';
    internal bool UsesFluid => Uses("FluidVelocity") || FluidSimulation.MaterialNames.Any(Uses);

    public ShaderProgram(ID3D11Device device, string source, string entry, string file, string profile = "ps_5_0")
    {
        stage = profile[0];
        var code = Compiler.Compile(source, entry, file, profile);
        using var reflection = Compiler.Reflect<ID3D11ShaderReflection>(code.Span);
        for (uint i = 0; i < reflection.Description.OutputParameters; i++)
            Outputs.Add(reflection.GetOutputParameterDescription(i));
        for (uint i = 0; i < reflection.Description.BoundResources; i++)
        {
            var binding = reflection.GetResourceBindingDescription(i);
            if (binding.BindCount != 1) throw new InvalidOperationException($"{file}: resource arrays are not supported ({binding.Name}).");
            var supported = binding.Type switch
            {
                ShaderInputType.ConstantBuffer => binding.Name is "FrameConstants" or "Parameters" or "FluidConstants" or "RasterAirlightConstants",
                ShaderInputType.Sampler => binding.Name is "Linear" or "FluidSampler" or "ClampFluid" or "PressureSampler" or "RasterLutSampler",
                ShaderInputType.Texture => binding.Name is "Spectrum0" or "Spectrum1" or "Histogram0" or "Histogram1"
                    or "FilteredHistogram0" or "FilteredHistogram1"
                    or "FluidVelocity" or "Velocity" or "Advected" or "Pressure" or "Rhs" or "Correction"
                    or "RasterAirlightLut"
                    or "RasterSurface" or "RasterDepth" or "RasterAirlight"
                    || MaterialResource(binding.Name, "FluidMaterial") || MaterialResource(binding.Name, "FluidPredicted"),
                ShaderInputType.Structured => binding.Name == "PointLights",
                ShaderInputType.ByteAddress => binding.Name == "PointLightCount",
                ShaderInputType.UnorderedAccessViewRWStructured => stage == 'c' && binding.Name == "PointLightOutput",
                ShaderInputType.UnorderedAccessViewRWByteAddress => stage == 'c' && binding.Name is "GeometryDrawArguments" or "PointLightCountOutput",
                _ => false,
            };
            if (entry == "PS" && (binding.Name is "FluidConstants" or "Velocity" or "Advected" or "Pressure" or "Rhs" or "Correction" or "ClampFluid" or "PressureSampler"
                || MaterialResource(binding.Name, "FluidPredicted")))
                supported = false;
            if (!supported) throw new InvalidOperationException($"{file}: unknown {binding.Type} resource '{binding.Name}'.");
            if ((binding.Name == "FluidVelocity" || MaterialResource(binding.Name, "FluidMaterial") || MaterialResource(binding.Name, "FluidPredicted"))
                && (binding.Type != ShaderInputType.Texture || binding.Dimension != ShaderResourceViewDimension.Texture2D || binding.ReturnType != ResourceReturnType.Float))
                throw new InvalidOperationException($"{file}: {binding.Name} must be a floating-point Texture2D.");
            bindings.Add(binding.Name, binding);
        }
        if (Uses("Parameters"))
        {
            Parameters = new(reflection.GetConstantBufferByName("Parameters"));
            uploaded = new byte[Parameters.Data.Length];
            parameterBuffer = device.CreateBuffer((uint)Parameters.Data.Length, BindFlags.ConstantBuffer);
        }
        try
        {
            if (stage == 'v') vertex = device.CreateVertexShader(code.Span);
            else if (stage == 'c') compute = device.CreateComputeShader(code.Span);
            else { Pixel = device.CreatePixelShader(code.Span); Pixel.DebugName = $"{file}:{entry}"; }
        }
        catch { parameterBuffer?.Dispose(); throw; }
    }

    public unsafe void Bind(ID3D11DeviceContext context, ShaderParameters? values,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers,
        IReadOnlyDictionary<string, ID3D11ShaderResourceView> textures,
        IReadOnlyDictionary<string, ID3D11SamplerState> samplers,
        IReadOnlyDictionary<string, ID3D11UnorderedAccessView>? outputs = null)
    {
        foreach (var (name, binding) in bindings)
        {
            var slot = binding.BindPoint;
            if (binding.Type == ShaderInputType.ConstantBuffer)
            {
                if (name == "Parameters")
                {
                    // Each entry point owns its layout; transfer values by path, never by slot or declaration order.
                    lock (Parameters!.SyncRoot)
                    {
                        if (values != null && !ReferenceEquals(values, Parameters))
                        {
                            long version = values.Version;
                            if (!ReferenceEquals(values, copiedFrom) || copiedVersion != version || localVersion != Parameters.Version)
                            {
                                Parameters.Restore(values);
                                copiedFrom = values; copiedVersion = version; localVersion = Parameters.Version;
                            }
                        }
                        if (!hasUploaded || !Parameters.Data.AsSpan().SequenceEqual(uploaded))
                        {
                            fixed (byte* data = Parameters.Data)
                                context.UpdateSubresource(parameterBuffer!, 0, null, (IntPtr)data, 0, 0);
                            Parameters.Data.CopyTo(uploaded!, 0);
                            hasUploaded = true;
                        }
                    }
                    BindConstant(slot, parameterBuffer!);
                }
                else if (buffers.TryGetValue(name, out var buffer))
                {
                    BindConstant(slot, buffer);
                }
                else throw new InvalidOperationException($"Unknown shader buffer: {name}");
            }
            else if (binding.Type == ShaderInputType.Sampler)
            {
                if (!samplers.TryGetValue(name, out var sampler)) throw new InvalidOperationException($"Unknown shader sampler: {name}");
                if (stage == 'v') context.VSSetSampler(slot, sampler);
                else if (stage == 'c') context.CSSetSampler(slot, sampler);
                else context.PSSetSampler(slot, sampler);
            }
            else if (binding.Type is ShaderInputType.UnorderedAccessViewRWByteAddress or ShaderInputType.UnorderedAccessViewRWStructured)
                context.CSSetUnorderedAccessView(slot, outputs![name]);
            else
            {
                if (!textures.TryGetValue(name, out var texture)) throw new InvalidOperationException($"Unknown shader resource: {name}");
                if (stage == 'v') context.VSSetShaderResource(slot, texture);
                else if (stage == 'c') context.CSSetShaderResource(slot, texture);
                else context.PSSetShaderResource(slot, texture);
            }
        }
        if (stage == 'v') context.VSSetShader(vertex);
        else if (stage == 'c') context.CSSetShader(compute);
        else context.PSSetShader(Pixel);

        void BindConstant(uint slot, ID3D11Buffer buffer)
        {
            if (stage == 'v') context.VSSetConstantBuffer(slot, buffer);
            else if (stage == 'c') context.CSSetConstantBuffer(slot, buffer);
            else context.PSSetConstantBuffer(slot, buffer);
        }
    }

    public void Dispose() { Pixel?.Dispose(); vertex?.Dispose(); compute?.Dispose(); parameterBuffer?.Dispose(); }
}

public sealed class ShaderScene : IDisposable
{
    public string Name { get; }
    readonly ID3D11Device device;
    readonly string source;
    ShaderProgram? program;
    FluidSimulation.Programs? fluid;

    RasterLight.Programs? raster;
    bool initialized;
    internal bool ProgramReady => program != null;
    internal ShaderProgram Program => program ?? throw new InvalidOperationException($"Scene '{Name}' has not been compiled yet.");
    internal ShaderProgram CreateDefaultProgram() => new(device, source, "PS", Name + ".hlsl");
    public ShaderParameters? Parameters => program?.Parameters ?? fluid?.Configuration.Values;
    internal FluidSimulation.Programs? Fluid => fluid;

    internal RasterLight.Programs? Raster => raster;
    internal ShaderScene(ID3D11Device device, string name, string source, bool compileImmediately = true)
    {
        Name = name;
        this.device = device;
        this.source = source;
        if (compileImmediately) EnsureCompiled();
    }

    internal void EnsureCompiled()
    {
        if (initialized) return;
        try
        {
            var nextProgram = new ShaderProgram(device, source, "PS", Name + ".hlsl");

            RasterLight.Programs? nextRaster = null;
            FluidSimulation.Programs? nextFluid = null;
            try
            {
                if (nextProgram.Uses("RasterSurface")) nextRaster = new(device, source, Name + ".hlsl", nextProgram.Parameters);
                if (nextProgram.UsesFluid)
                    nextFluid = new(device, source, Name + ".hlsl", nextProgram);
                program = nextProgram;
                raster = nextRaster;
                fluid = nextFluid;
                initialized = true;
            }
            catch
            {
                nextProgram.Dispose(); nextRaster?.Dispose(); nextFluid?.Dispose();
                throw;
            }
        }
        catch { DisposePrograms(); program = null; initialized = false; throw; }
    }
    void DisposePrograms()
    {
        program?.Dispose(); raster?.Dispose(); fluid?.Dispose();
        raster = null; fluid = null;
    }
    internal ShaderScene Recompile()
    {
        var replacement = new ShaderScene(device, Name, source, compileImmediately: false);
        replacement.EnsureCompiled();
        if (Parameters is { } old && replacement.Parameters is { } next) next.Restore(old);
        return replacement;
    }
    internal void ReplaceWith(ShaderScene replacement)
    {
        DisposePrograms();
        program = replacement.program; replacement.program = null;
        fluid = replacement.fluid; replacement.fluid = null;
        raster = replacement.raster; replacement.raster = null;
        initialized = true;
    }
    public void Dispose() => DisposePrograms();
}
