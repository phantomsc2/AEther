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
                ShaderInputType.ConstantBuffer => binding.Name is "FrameConstants" or "Parameters" or "FluidConstants" or "DistanceConstants",
                ShaderInputType.Sampler => binding.Name is "Linear" or "FluidSampler" or "ClampFluid" or "PressureSampler" or "DistanceSampler",
                ShaderInputType.Texture => binding.Name is "PreviousImage" or "SceneImage" or "Spectrum0" or "Spectrum1" or "Histogram0" or "Histogram1"
                    or "FilteredHistogram0" or "FilteredHistogram1" or "GlobalHistogram"
                    or "FluidDye" or "FluidAge" or "DyeAge" or "AdvectedAge" or "Velocity" or "Dye" or "Advected" or "Pressure" or "Rhs" or "Correction" or "DistanceSeeds" or "DistanceField",
                _ => false,
            };
            if (entry == "PS" && binding.Name is "FluidConstants" or "DyeAge" or "AdvectedAge" or "Velocity" or "Dye" or "Advected" or "Pressure" or "Rhs" or "Correction" or "ClampFluid" or "PressureSampler" or "DistanceConstants" or "DistanceSeeds")
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
    public ShaderParameters? Parameters => Program.Parameters;
    internal ShaderProgram Program { get; }
    internal ID3D11VertexShader? GeometryVertex { get; }
    internal int GeometryVertexCount { get; }
    internal int GeometryInstanceCount { get; } = 1;
    internal bool GeometryAdditive { get; }
    internal ShaderProgram? Composite { get; }
    internal MandelFluidSimulation.Programs? Fluid { get; }
    internal MandelbenchDistanceField.Programs? Distance { get; }
    internal ShaderScene(ID3D11Device device, string name, string source, string root)
    {
        Name = name;
        Program = new(device, source, "PS", name + ".hlsl");
        try
        {
            if (Program.Uses("PreviousImage"))
                Composite = new(device, source, "FeedbackDisplayPS", name + ".hlsl");
            var geometry = System.Text.RegularExpressions.Regex.Match(source,
                @"^\s*#define\s+GEOMETRY_VERTEX_COUNT\s+(\d+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (geometry.Success)
            {
                if (!int.TryParse(geometry.Groups[1].Value, out var count) || count < 3 || count > 65536 || count % 3 != 0)
                    throw new InvalidOperationException("GEOMETRY_VERTEX_COUNT must be a multiple of three from 3 to 65535.");
                GeometryVertexCount = count;
                var instances = System.Text.RegularExpressions.Regex.Match(source,
                    @"^\s*#define\s+GEOMETRY_INSTANCE_COUNT\s+(\d+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
                if (instances.Success)
                {
                    if (!int.TryParse(instances.Groups[1].Value, out var instanceCount) || instanceCount < 1 || instanceCount > 1048576)
                        throw new InvalidOperationException("GEOMETRY_INSTANCE_COUNT must be from 1 to 1048576.");
                    GeometryInstanceCount = instanceCount;
                }
                GeometryAdditive = System.Text.RegularExpressions.Regex.IsMatch(source,
                    @"^\s*#define\s+GEOMETRY_ADDITIVE\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
                GeometryVertex = device.CreateVertexShader(Compiler.Compile(source, "GeometryVS", name + ".hlsl", "vs_5_0").Span);
                Composite = new(device, source, "AfterglowPS", name + ".hlsl");
            }
            if (Program.Uses("FluidDye") || Composite?.Uses("FluidDye") == true)
                Fluid = new(device, source + ShaderSource.Read(root, "Common/FluidSimulation.hlsl"));
            if (Program.Uses("DistanceField"))
            {
                if (Parameters == null) throw new InvalidOperationException("DistanceField requires scene Parameters for its geometry passes.");
                Distance = new(device, source);
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { Program.Dispose(); Fluid?.Dispose(); Distance?.Dispose(); GeometryVertex?.Dispose(); Composite?.Dispose(); }
}
