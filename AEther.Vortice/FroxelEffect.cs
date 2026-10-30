using System.Numerics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

/// <summary>Camera-aligned participating media, with independent lighting and column integration.</summary>
public class FroxelEffect : SceneEffect
{
    FroxelOptions options;
    FroxelOptions? accepted;
    public FroxelOptions Options { get => Volatile.Read(ref options); set => Volatile.Write(ref options, value); }
    public FroxelOptions DefaultOptions { get; }
    public override object SizeOptions => Options;
    readonly ShaderProgram? lightsProgram;
    readonly ShaderProgram injectProgram, integrateProgram;
    readonly ShaderProgram? lightingProgram;
    readonly FroxelRaster? raster;
    readonly bool customSource;
    readonly ID3D11Buffer constants;
    readonly ID3D11SamplerState sampler;
    readonly Dictionary<string, ID3D11UnorderedAccessView> outputs = [];
    Volume? source, integrated, lighting;
    LightStorage? lights;
    internal (int Width, int Height, int Depth) Dimensions { get; private set; }
    internal ID3D11Texture3D IntegratedTexture => integrated!.Texture;

    internal FroxelEffect(string name, ID3D11Device device, ID3D11DeviceContext context, string source, string file,
        FroxelOptions options, bool customSource = false, bool enableRaster = false,
        bool preparedLighting = false, string[]? extraBindings = null) : base(name, device, context, source, file)
    {
        this.options = options; DefaultOptions = options; this.customSource = customSource;
        try
        {
            if (!customSource) lightsProgram = new(device, source, "PrepareFroxelLights", file, "cs_5_0");
            injectProgram = new(device, source, "InjectFroxels", file, "cs_5_0");
            integrateProgram = new(device, source, "IntegrateFroxels", file, "cs_5_0");
            if (preparedLighting) lightingProgram = new(device, source, "LightFroxels", file, "cs_5_0");
            if (!customSource || enableRaster) raster = new(device, context, source, file, extraBindings);
            var available = FrameBindings.Concat(["FroxelConstants", "FroxelLights", "FroxelLightOutput",
                "FroxelSource", "FroxelSourceOutput", "FroxelIntegrated", "FroxelIntegratedOutput", "FroxelSampler", "FroxelSurface", "FroxelSurfaceDepth",
                "FroxelLighting", "FroxelLightingOutput"]).Concat(extraBindings ?? []);
            foreach (var program in new[] { Display, lightsProgram, injectProgram, integrateProgram, lightingProgram }.OfType<ShaderProgram>()) program.ValidateBindings(available);
            Parameters = ShaderParameters.Merge(new[] { Display, lightsProgram, injectProgram, integrateProgram, lightingProgram, raster?.Geometry, raster?.Surface }
                .OfType<ShaderProgram>().Select(p => p.Parameters));
            constants = device.CreateBuffer(16, BindFlags.ConstantBuffer);
            sampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxLOD = float.MaxValue,
            });
        }
        catch { Dispose(); throw; }
    }
    protected override void RestoreSizes(object? sizes) { if (sizes is FroxelOptions value) Options = value; }

    void Unbind()
    {
        Context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        Context.CSSetShaderResources(0, ShaderProgram.EmptyViews);
        for (uint slot = 0; slot < 8; slot++) Context.CSSetUnorderedAccessView(slot, null);
    }
    void Configure(FroxelOptions requested, int width, int height)
    {
        var dimensions = requested.Resolve(width, height);
        Volume? nextSource = null, nextIntegrated = null, nextLighting = null;
        LightStorage? nextLights = null;
        try
        {
            if (source == null || dimensions != Dimensions)
            {
                nextSource = new(Device, dimensions);
                nextIntegrated = new(Device, dimensions);
                if (lightingProgram != null) nextLighting = new(Device, dimensions);
            }
            if (!customSource && (lights == null || lights.Capacity != Math.Max(1, requested.PointLights)))
                nextLights = new(Device, Math.Max(1, requested.PointLights));
        }
        catch { nextSource?.Dispose(); nextIntegrated?.Dispose(); nextLighting?.Dispose(); nextLights?.Dispose(); throw; }
        Unbind();
        if (nextSource != null) { source?.Dispose(); integrated?.Dispose(); source = nextSource; integrated = nextIntegrated; }
        if (nextLights != null) { lights?.Dispose(); lights = nextLights; }
        if (nextLighting != null) { lighting?.Dispose(); lighting = nextLighting; }
        Dimensions = dimensions;
    }
    internal override void Prepare(EffectFrame frame, ShaderParameters? parameters,
        Dictionary<string, ID3D11Buffer> buffers, Dictionary<string, ID3D11ShaderResourceView> textures,
        Dictionary<string, ID3D11SamplerState> samplers)
    {
        var requested = Options;
        try
        {
            Configure(requested, frame.Width, frame.Height);
            if (requested != accepted) frame.ReportError(null);
            accepted = requested;
        }
        catch (Exception error)
        {
            if (accepted == null || source == null) throw;
            Interlocked.CompareExchange(ref options, accepted, requested);
            frame.ReportError(error.Message);
        }
        Unbind();
        var (width, height, depth) = Dimensions;
        int count = accepted!.PointLights;
        static float Bits(int n) => BitConverter.UInt32BitsToSingle((uint)n);
        Context.UpdateSubresource(new Vector4(Bits(width), Bits(height), Bits(depth), Bits(count)), constants);
        buffers["FroxelConstants"] = constants;
        samplers["FroxelSampler"] = sampler;
        if (lights != null) textures["FroxelLights"] = lights.View;
        textures["FroxelSource"] = source!.View;
        textures["FroxelIntegrated"] = integrated!.View;
        if (lighting != null) { textures["FroxelLighting"] = lighting.View; outputs["FroxelLightingOutput"] = lighting.Output; }
        if (lights != null) outputs["FroxelLightOutput"] = lights.Output;
        outputs["FroxelSourceOutput"] = source.Output;
        outputs["FroxelIntegratedOutput"] = integrated.Output;
        if (lightsProgram != null) Run(lightsProgram, "Froxel lights", 1, 1, 1);
        Run(injectProgram, lightingProgram != null ? "Froxel material" : customSource ? "Froxel ring injection" : "Froxel injection", (width+7)/8, (height+7)/8, depth);
        if (lightingProgram != null) Run(lightingProgram, "Froxel line lighting", (width+7)/8, (height+7)/8, depth);
        Run(integrateProgram, "Froxel integration", (width+7)/8, (height+7)/8, 1);
        raster?.Draw(frame, parameters, accepted.Instances, buffers, textures, samplers);

        void Run(ShaderProgram program, string marker, int x, int y, int z)
        {
            frame.Marker(marker, true);
            program.Bind(Context, parameters, buffers, textures, samplers, outputs);
            Context.Dispatch((uint)x, (uint)y, (uint)z);
            Unbind();
            frame.Marker(marker, false);
        }
    }
    internal override void Deactivate()
    {
        base.Deactivate(); outputs.Clear(); raster?.Release();
        source?.Dispose(); integrated?.Dispose(); lighting?.Dispose(); lights?.Dispose();
        source = null; integrated = null; lighting = null; lights = null; accepted = null; Dimensions = default;
    }
    public override void Dispose()
    {
        base.Dispose(); lightsProgram?.Dispose(); injectProgram?.Dispose(); integrateProgram?.Dispose(); lightingProgram?.Dispose();
        constants?.Dispose(); sampler?.Dispose(); raster?.Dispose();
    }
    sealed class Volume : IDisposable
    {
        internal readonly ID3D11Texture3D Texture;
        internal readonly ID3D11ShaderResourceView View;
        internal readonly ID3D11UnorderedAccessView Output;
        internal Volume(ID3D11Device device, (int Width, int Height, int Depth) size)
        {
            Texture = device.CreateTexture3D(new Texture3DDescription
            {
                Width = (uint)size.Width, Height = (uint)size.Height, Depth = (uint)size.Depth, MipLevels = 1,
                Format = Format.R16G16B16A16_Float, Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            });
            try { View = device.CreateShaderResourceView(Texture); Output = device.CreateUnorderedAccessView(Texture); }
            catch { View?.Dispose(); Texture.Dispose(); throw; }
        }
        public void Dispose() { Output.Dispose(); View.Dispose(); Texture.Dispose(); }
    }
    sealed class LightStorage : IDisposable
    {
        internal readonly int Capacity;
        readonly ID3D11Buffer buffer;
        internal readonly ID3D11ShaderResourceView View;
        internal readonly ID3D11UnorderedAccessView Output;
        internal LightStorage(ID3D11Device device, int capacity)
        {
            Capacity = capacity;
            buffer = device.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)(24*capacity), StructureByteStride = 24, Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess, MiscFlags = ResourceOptionFlags.BufferStructured,
            });
            try { View = device.CreateShaderResourceView(buffer); Output = device.CreateUnorderedAccessView(buffer); }
            catch { View?.Dispose(); buffer.Dispose(); throw; }
        }
        public void Dispose() { Output.Dispose(); View.Dispose(); buffer.Dispose(); }
    }
}
