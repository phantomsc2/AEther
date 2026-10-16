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
                ShaderInputType.ConstantBuffer => binding.Name is "FrameConstants" or "Parameters" or "FluidConstants",
                ShaderInputType.Sampler => binding.Name is "Linear" or "FluidSampler" or "ClampFluid" or "PressureSampler",
                ShaderInputType.Texture => binding.Name is "Spectrum0" or "Spectrum1" or "Histogram0" or "Histogram1"
                    or "FilteredHistogram0" or "FilteredHistogram1" or "FluidDye" or "Velocity" or "Dye" or "Pressure" or "Rhs" or "Correction",
                _ => false,
            };
            if (entry == "PS" && binding.Name is "FluidConstants" or "Velocity" or "Dye" or "Pressure" or "Rhs" or "Correction" or "ClampFluid" or "PressureSampler")
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
        try { Pixel = device.CreatePixelShader(code.Span); }
        catch { parameterBuffer?.Dispose(); throw; }
    }

    public unsafe void Bind(ID3D11DeviceContext context, ShaderParameters? values,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers,
        IReadOnlyDictionary<string, ID3D11ShaderResourceView> textures,
        IReadOnlyDictionary<string, ID3D11SamplerState> samplers)
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
                        if (values != null && !ReferenceEquals(values, Parameters)) Parameters.Restore(values);
                        if (!hasUploaded || !Parameters.Data.AsSpan().SequenceEqual(uploaded))
                        {
                            fixed (byte* data = Parameters.Data)
                                context.UpdateSubresource(parameterBuffer!, 0, null, (IntPtr)data, 0, 0);
                            Parameters.Data.CopyTo(uploaded!, 0);
                            hasUploaded = true;
                        }
                    }
                    context.PSSetConstantBuffer(slot, parameterBuffer!);
                }
                else if (buffers.TryGetValue(name, out var buffer)) context.PSSetConstantBuffer(slot, buffer);
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
    public ShaderParameters? Parameters => Program.Parameters;
    internal ShaderProgram Program { get; }
    internal ShaderProgram? Filter { get; }
    internal MandelFluidSimulation.Programs? Fluid { get; }
    internal bool UsesFilteredHistory => Program.Uses("FilteredHistogram0") || Program.Uses("FilteredHistogram1");
    internal ShaderScene(ID3D11Device device, string name, string source, string root)
    {
        Name = name;
        Program = new(device, source, "PS", name + ".hlsl");
        try
        {
            if (UsesFilteredHistory)
                Filter = new(device, source + ShaderSource.Read(root, "Common/Bilateral.hlsl"), "BilateralPS", name + ".hlsl");
            if (Program.Uses("FluidDye"))
                Fluid = new(device, source + ShaderSource.Read(root, "Common/FluidSimulation.hlsl"));
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { Program.Dispose(); Filter?.Dispose(); Fluid?.Dispose(); }
}
