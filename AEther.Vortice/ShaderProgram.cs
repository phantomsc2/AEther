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
    internal void ValidateBindings(IEnumerable<string> available)
    {
        var names = available.Append("Parameters").ToHashSet(StringComparer.Ordinal);
        foreach (var name in bindings.Keys)
            if (!names.Contains(name)) throw new InvalidOperationException($"Missing shader binding: {name}");
    }
    internal readonly List<ShaderParameterDescription> Outputs = [];
    public ShaderProgram(ID3D11Device device, string source, string entry, string file, string profile = "ps_5_0")
    {
        stage = profile[0];
        var code = ShaderSource.Compile(source, entry, file, profile);
        using var reflection = Compiler.Reflect<ID3D11ShaderReflection>(code.Span);
        for (uint i = 0; i < reflection.Description.OutputParameters; i++)
            Outputs.Add(reflection.GetOutputParameterDescription(i));
        for (uint i = 0; i < reflection.Description.BoundResources; i++)
        {
            var binding = reflection.GetResourceBindingDescription(i);
            if (binding.BindCount != 1) throw new InvalidOperationException($"{file}: resource arrays are not supported ({binding.Name}).");
            if (stage != 'c' && binding.Type is (ShaderInputType.UnorderedAccessViewRWTyped
                or ShaderInputType.UnorderedAccessViewRWStructured or ShaderInputType.UnorderedAccessViewRWByteAddress))
                throw new InvalidOperationException($"{file}: UAV bindings require a compute pass ({binding.Name}).");
            if (binding.Type is not (ShaderInputType.ConstantBuffer or ShaderInputType.Sampler
                or ShaderInputType.Texture or ShaderInputType.Structured or ShaderInputType.ByteAddress
                or ShaderInputType.UnorderedAccessViewRWTyped or ShaderInputType.UnorderedAccessViewRWStructured
                or ShaderInputType.UnorderedAccessViewRWByteAddress))
                throw new InvalidOperationException($"{file}: unsupported resource type {binding.Type} for '{binding.Name}'.");
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
            else if (binding.Type is ShaderInputType.UnorderedAccessViewRWTyped or ShaderInputType.UnorderedAccessViewRWByteAddress or ShaderInputType.UnorderedAccessViewRWStructured)
                {
                if (outputs == null || !outputs.TryGetValue(name, out var output))
                    throw new InvalidOperationException($"Missing shader output: {name}");
                context.CSSetUnorderedAccessView(slot, output);
            }
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
