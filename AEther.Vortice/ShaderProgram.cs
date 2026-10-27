using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D11.Shader;

namespace AEther.Vortice;

internal sealed class ShaderProgram : IDisposable
{
    internal static readonly ID3D11ShaderResourceView[] EmptyViews = new ID3D11ShaderResourceView[128];
    public ID3D11PixelShader Pixel { get; }
    public ShaderParameters? Parameters { get; }
    readonly ID3D11Buffer? parameterBuffer;
    readonly byte[]? uploaded;
    bool hasUploaded;
    ShaderParameters? copiedFrom;
    long copiedVersion = -1;
    long localVersion = -1;
    readonly Dictionary<string, InputBindingDescription> bindings = new(StringComparer.Ordinal);
    public bool Uses(string name) => bindings.ContainsKey(name);

    public ShaderProgram(ID3D11Device device, string source, string entry, string file)
    {
        var code = Compiler.Compile(source, entry, file, "ps_5_0");
        using var reflection = Compiler.Reflect<ID3D11ShaderReflection>(code.Span);
        for (uint i = 0; i < reflection.Description.BoundResources; i++)
        {
            var binding = reflection.GetResourceBindingDescription(i);
            if (binding.BindCount != 1) throw new InvalidOperationException($"{file}: resource arrays are not supported ({binding.Name}).");
            var supported = binding.Type switch
            {
                ShaderInputType.ConstantBuffer => binding.Name is "FrameConstants" or "Parameters" or "FluidConstants" or "RasterPassConstants" or "RasterLightConstants" or "RasterAirlightConstants",
                ShaderInputType.Sampler => binding.Name is "Linear" or "FluidSampler" or "ClampFluid" or "PressureSampler" or "RasterShadowSampler" or "RasterLutSampler",
                ShaderInputType.Texture => binding.Name is "Spectrum0" or "Spectrum1" or "Histogram0" or "Histogram1"
                    or "FilteredHistogram0" or "FilteredHistogram1"
                    or "FluidDye" or "FluidAge" or "DyeAge" or "AdvectedAge" or "Velocity" or "Dye" or "Advected" or "Pressure" or "Rhs" or "Correction"
                    or "RasterShadows" or "RasterSurface" or "RasterDepth" or "RasterAirlight" or "RasterAirlightLut",
                _ => false,
            };
            if (entry == "PS" && binding.Name is "FluidConstants" or "DyeAge" or "AdvectedAge" or "Velocity" or "Dye" or "Advected" or "Pressure" or "Rhs" or "Correction" or "ClampFluid" or "PressureSampler")
                supported = false;
            if (!supported) throw new InvalidOperationException($"{file}: unknown {binding.Type} resource '{binding.Name}'.");
            bindings.Add(binding.Name, binding);
        }
        if (Uses("Parameters"))
        {
            Parameters = new(reflection.GetConstantBufferByName("Parameters"));
            uploaded = new byte[Parameters.Data.Length];
            parameterBuffer = device.CreateBuffer((uint)Parameters.Data.Length, BindFlags.ConstantBuffer);
        }
        try { Pixel = device.CreatePixelShader(code.Span); Pixel.DebugName = $"{file}:{entry}"; }
        catch { parameterBuffer?.Dispose(); throw; }
    }

    public unsafe void Bind(ID3D11DeviceContext context, ShaderParameters? values,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers,
        IReadOnlyDictionary<string, ID3D11ShaderResourceView> textures,
        IReadOnlyDictionary<string, ID3D11SamplerState> samplers, bool vertexConstants = false)
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
                    context.PSSetConstantBuffer(slot, parameterBuffer!);
                    if (vertexConstants) context.VSSetConstantBuffer(slot, parameterBuffer!);
                }
                else if (buffers.TryGetValue(name, out var buffer))
                {
                    context.PSSetConstantBuffer(slot, buffer);
                    if (vertexConstants) context.VSSetConstantBuffer(slot, buffer);
                }
                else throw new InvalidOperationException($"Unknown shader buffer: {name}");
            }
            else if (binding.Type == ShaderInputType.Sampler)
            {
                if (!samplers.TryGetValue(name, out var sampler)) throw new InvalidOperationException($"Unknown shader sampler: {name}");
                context.PSSetSampler(slot, sampler);
            }
            else
            {
                if (!textures.TryGetValue(name, out var texture)) throw new InvalidOperationException($"Unknown shader resource: {name}");
                context.PSSetShaderResource(slot, texture);
            }
        }
        context.PSSetShader(Pixel);
    }

    public void Dispose() { Pixel.Dispose(); parameterBuffer?.Dispose(); }
}

public sealed class ShaderScene : IDisposable
{
    public string Name { get; }
    readonly ID3D11Device device;
    readonly string root;
    readonly string source;
    ShaderProgram? program;
    MandelFluidSimulation.Programs? fluid;
    RasterLightTransport.Programs? raster;
    bool initialized;
    internal bool ProgramReady => program != null;
    internal ShaderProgram Program => program ?? throw new InvalidOperationException($"Scene '{Name}' has not been compiled yet.");
    public ShaderParameters? Parameters => program?.Parameters;
    internal MandelFluidSimulation.Programs? Fluid => fluid;
    internal RasterLightTransport.Programs? Raster => raster;
    internal ShaderScene(ID3D11Device device, string name, string source, string root, bool compileImmediately = true)
    {
        Name = name;
        this.device = device;
        this.source = source;
        this.root = root;
        if (compileImmediately) EnsureCompiled();
    }

    internal void EnsureCompiled()
    {
        if (initialized) return;
        try
        {
            var nextProgram = new ShaderProgram(device, source, "PS", Name + ".hlsl");
            RasterLightTransport.Programs? nextRaster = null;
            MandelFluidSimulation.Programs? nextFluid = null;
            try
            {
                if (nextProgram.Uses("RasterSurface")) nextRaster = new(device, source, Name + ".hlsl");
                if (nextProgram.Uses("FluidDye"))
                    nextFluid = new(device, source + ShaderSource.Read(root, Name == "MandelFluidv4"
                        ? "Common/RingFluidSimulation.hlsl" : "Common/FluidSimulation.hlsl"));
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
        var replacement = new ShaderScene(device, Name, source, root, compileImmediately: false);
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
