using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

// A single non-indexed triangle-list draw. Counts and vertices belong to HLSL.
internal sealed class RasterGeometry : IDisposable
{
    internal sealed class Programs : IDisposable
    {
        internal readonly ShaderProgram Prepare, Vertex;
        internal Programs(ID3D11Device device, string source, string file)
        {
            Prepare = new(device, source, "PrepareGeometry", file, "cs_5_0");
            try { Vertex = new(device, source, "RasterGeometryVS", file, "vs_5_0"); }
            catch { Prepare.Dispose(); throw; }
        }
        public void Dispose() { Vertex.Dispose(); Prepare.Dispose(); }
    }

    readonly ID3D11Buffer arguments;
    readonly ID3D11UnorderedAccessView output;
    readonly Dictionary<string, ID3D11UnorderedAccessView> outputs;

    internal RasterGeometry(ID3D11Device device)
    {
        arguments = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 16, Usage = ResourceUsage.Default, BindFlags = BindFlags.UnorderedAccess,
            MiscFlags = ResourceOptionFlags.DrawIndirectArguments | ResourceOptionFlags.BufferAllowRawViews,
        });
        try
        {
            output = device.CreateUnorderedAccessView(arguments, new UnorderedAccessViewDescription
            {
                Format = Format.R32_Typeless, ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView { NumElements = 4, Flags = BufferUnorderedAccessViewFlags.Raw },
            });
        }
        catch { arguments.Dispose(); throw; }
        outputs = new() { ["GeometryDrawArguments"] = output };
    }

    internal void Prepare(ID3D11DeviceContext context, Programs program, ShaderParameters p,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers,
        IReadOnlyDictionary<string, ID3D11ShaderResourceView> textures,
        IReadOnlyDictionary<string, ID3D11SamplerState> samplers)
    {
        program.Prepare.Bind(context, p, buffers, textures, samplers, outputs);
        context.Dispatch(1, 1, 1);
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.CSSetShader(null);
    }

    internal void Draw(ID3D11DeviceContext context)
    {
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.DrawInstancedIndirect(arguments, 0);
    }

    public void Dispose() { output.Dispose(); arguments.Dispose(); }
}
