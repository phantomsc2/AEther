using System.Numerics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

// Cached, shared boundary samples make distance contours coherent across pixels.
// Only geometry edits rebuild the field; band/level/color edits just composite it.
internal sealed class MandelbenchDistanceField : IDisposable
{
    internal sealed class Programs : IDisposable
    {
        internal readonly Dictionary<string, ShaderProgram> Shaders = [];
        internal Programs(ID3D11Device device, string source)
        {
            try
            {
                foreach (var entry in new[] { "DistanceSeedPS", "DistanceJumpPS", "DistanceResolvePS" })
                    Shaders.Add(entry, new(device, source, entry, "MandelbenchDistance.hlsl"));
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var program in Shaders.Values) program.Dispose(); }
    }

    readonly ID3D11DeviceContext context;
    readonly MandelFluidSimulation.Surface[] seeds;
    readonly MandelFluidSimulation.Surface field;
    readonly ID3D11Buffer constants;
    readonly ID3D11SamplerState sampler;
    Programs? previousPrograms;
    byte[]? previousGeometry;
    readonly Dictionary<string, ID3D11Buffer> buffers = new(StringComparer.Ordinal);
    readonly Dictionary<string, ID3D11ShaderResourceView> textures = new(StringComparer.Ordinal);
    readonly Dictionary<string, ID3D11SamplerState> samplers = new(StringComparer.Ordinal);
    internal ID3D11ShaderResourceView Field => this.field.View;
    internal ID3D11SamplerState Sampler => sampler;

    internal MandelbenchDistanceField(ID3D11Device device, ID3D11DeviceContext context, int width, int height)
    {
        this.context = context;
        float factor = Math.Min(2, 4096f / Math.Max(width, height));
        int w = Math.Max(64, (int)Math.Ceiling(width * factor));
        int h = Math.Max(64, (int)Math.Ceiling(height * factor));
        var owned = new List<IDisposable>();
        try
        {
            seeds = new MandelFluidSimulation.Surface[2];
            for (int i = 0; i < 2; i++)
            {
                seeds[i] = new(device, w, h, Format.R32G32B32A32_Float);
                owned.Add(seeds[i]);
            }
            field = new(device, w, h, Format.R32G32_Float); owned.Add(field);
            constants = device.CreateBuffer(16, BindFlags.ConstantBuffer); owned.Add(constants);
            sampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagLinearMipPoint, AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
                MaxLOD = float.MaxValue, ComparisonFunc = ComparisonFunction.Never,
            });
            owned.Add(sampler);
        }
        catch { foreach (var resource in owned) resource.Dispose(); throw; }
    }

    static byte[] GeometryKey(ShaderParameters values)
    {
        var data = new List<byte>();
        foreach (var group in values.Items)
            foreach (var item in group.Children)
                if (group.Name == "IFS" || group.Name == "Search" && item.Name == "EscapeRadius"
                    || group.Name == "View" && item.Name is "Zoom" or "Pan" or "Rotation" or "SliceDepth" or "Compare")
                    Append(item);
        return data.ToArray();

        void Append(ShaderParameter item)
        {
            if (item.Children.Count != 0) foreach (var child in item.Children) Append(child);
            else data.AddRange(values.Data.AsSpan(item.Offset, 4).ToArray());
        }
    }

    internal void Update(Programs programs, ShaderParameters values,
        IReadOnlyDictionary<string, ID3D11Buffer> frameBuffers)
    {
        byte[] key;
        lock (values.SyncRoot) key = GeometryKey(values);
        if (ReferenceEquals(programs, previousPrograms) && key.AsSpan().SequenceEqual(previousGeometry)) return;
        buffers.Clear(); foreach (var entry in frameBuffers) buffers.Add(entry.Key, entry.Value);
        buffers["DistanceConstants"] = constants;
        textures.Clear(); samplers.Clear();
        context.RSSetViewport(0, 0, field.Width, field.Height);
        int current = 0;
        Draw("DistanceSeedPS", seeds[current], 0);
        int jump = 1;
        while (jump < Math.Max(field.Width, field.Height)) jump <<= 1;
        for (jump >>= 1; jump >= 1; jump >>= 1) Jump(jump);
        // JFA+2 improves propagation around small features and Voronoi corners.
        Jump(2); Jump(1);
        textures["DistanceSeeds"] = seeds[current].View;
        Draw("DistanceResolvePS", field, 0);
        previousPrograms = programs; previousGeometry = key;

        void Jump(int step)
        {
            textures["DistanceSeeds"] = seeds[current].View;
            Draw("DistanceJumpPS", seeds[1 - current], step);
            current = 1 - current;
        }
        void Draw(string entry, MandelFluidSimulation.Surface target, int step)
        {
            context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
            context.OMSetRenderTargets(target.Target);
            var dimensions = new Vector4(field.Width, field.Height, step, 0);
            context.UpdateSubresource(dimensions, constants);
            programs.Shaders[entry].Bind(context, values, buffers, textures, samplers);
            context.Draw(3, 0);
        }
    }

    public void Dispose()
    {
        sampler.Dispose(); constants.Dispose(); field.Dispose();
        foreach (var surface in seeds) surface.Dispose();
    }
}
