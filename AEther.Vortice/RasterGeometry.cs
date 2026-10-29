using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace AEther.Vortice;

internal static class RasterGeometry
{
    internal sealed class Programs : IDisposable
    {
        internal readonly ShaderProgram Vertex;
        internal Programs(ID3D11Device device, string source, string file) =>
            Vertex = new(device, source, "RasterGeometryVS", file, "vs_5_0");
        public void Dispose() => Vertex.Dispose();
    }
    internal static void Draw(ID3D11DeviceContext context, uint vertices, uint instances)
    {
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        if (vertices != 0 && instances != 0) context.DrawInstanced(vertices, instances, 0, 0);
    }
}
