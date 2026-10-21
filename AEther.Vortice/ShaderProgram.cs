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
                ShaderInputType.ConstantBuffer => binding.Name is "FrameConstants" or "Parameters" or "FluidConstants" or "DistanceConstants" or "LightConstants" or "FourierConstants" or "FourierPassConstants" or "RasterPassConstants" or "RasterLightConstants" or "RasterAirlightConstants",
                ShaderInputType.Sampler => binding.Name is "Linear" or "FluidSampler" or "ClampFluid" or "PressureSampler" or "DistanceSampler" or "AtmosphereSampler" or "RasterShadowSampler" or "RasterLutSampler",
                ShaderInputType.Texture => binding.Name is "PreviousImage" or "SceneImage" or "Spectrum0" or "Spectrum1" or "ResidualSpectrum" or "Histogram0" or "Histogram1"
                    or "FilteredHistogram0" or "FilteredHistogram1"
                    or "FluidDye" or "FluidAge" or "DyeAge" or "AdvectedAge" or "Velocity" or "Dye" or "Advected" or "Pressure" or "Rhs" or "Correction" or "DistanceSeeds" or "DistanceField"
                    or "LightMoments" or "LightAirMoments" or "LightTransport" or "CameraMoments" or "CameraAirMoments" or "CameraTransport" or "CameraImportance" or "AtmosphereOpticalDepth" or "LightRadiance"
                    or "FourierDeep0" or "FourierDeep1" or "FourierDeep2" or "FourierDeep3"
                    or "RasterShadows" or "RasterSurface" or "RasterDepth" or "RasterAirlight" or "RasterAirlightLut",
                _ => false,
            };
            if (entry == "PS" && binding.Name is "FluidConstants" or "DyeAge" or "AdvectedAge" or "Velocity" or "Dye" or "Advected" or "Pressure" or "Rhs" or "Correction" or "ClampFluid" or "PressureSampler" or "DistanceConstants" or "DistanceSeeds" or "LightConstants")
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
    ShaderProgram? composite;
    MandelFluidSimulation.Programs? fluid;
    MandelbenchDistanceField.Programs? distance;
    ShaderProgram? lightTransport, atmosphere, cameraTransport, lighting, fourierShadow;
    RasterLightTransport.Programs? raster;
    ID3D11VertexShader? geometryVertex;
    int geometryVertexCount;
    int geometryInstanceCount = 1;
    bool geometryAdditive;
    bool initialized;
    internal bool ProgramReady => program != null;
    internal ShaderProgram Program => program ?? throw new InvalidOperationException($"Scene '{Name}' has not been compiled yet.");
    public ShaderParameters? Parameters => program?.Parameters;
    internal ID3D11VertexShader? GeometryVertex => geometryVertex;
    internal int GeometryVertexCount => geometryVertexCount;
    internal int GeometryInstanceCount => geometryInstanceCount;
    internal bool GeometryAdditive => geometryAdditive;
    internal ShaderProgram? Composite => composite;
    internal MandelFluidSimulation.Programs? Fluid => fluid;
    internal MandelbenchDistanceField.Programs? Distance => distance;
    internal ShaderProgram? LightTransport => lightTransport;
    internal ShaderProgram? Atmosphere => atmosphere;
    internal ShaderProgram? CameraTransport => cameraTransport;
    internal ShaderProgram? Lighting => lighting;
    internal ShaderProgram? FourierShadow => fourierShadow;
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
            ShaderProgram? nextComposite = null, nextLightTransport = null, nextAtmosphere = null,
                nextCameraTransport = null, nextLighting = null, nextFourierShadow = null;
            RasterLightTransport.Programs? nextRaster = null;
            MandelFluidSimulation.Programs? nextFluid = null;
            MandelbenchDistanceField.Programs? nextDistance = null;
            ID3D11VertexShader? nextGeometryVertex = null;
            try
            {
                var geometry = System.Text.RegularExpressions.Regex.Match(source,
                @"^\s*#define\s+GEOMETRY_VERTEX_COUNT\s+(\d+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
                if (geometry.Success)
                {
                    if (!int.TryParse(geometry.Groups[1].Value, out var count) || count < 3 || count > 65536 || count % 3 != 0)
                        throw new InvalidOperationException("GEOMETRY_VERTEX_COUNT must be a multiple of three from 3 to 65535.");
                    var nextGeometryVertexCount = count;
                    var nextGeometryInstanceCount = 1;
                    var nextGeometryAdditive = System.Text.RegularExpressions.Regex.IsMatch(source,
                        @"^\s*#define\s+GEOMETRY_ADDITIVE\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
                    var instances = System.Text.RegularExpressions.Regex.Match(source,
                        @"^\s*#define\s+GEOMETRY_INSTANCE_COUNT\s+(\d+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
                    if (instances.Success)
                    {
                        if (!int.TryParse(instances.Groups[1].Value, out nextGeometryInstanceCount) || nextGeometryInstanceCount < 1 || nextGeometryInstanceCount > 1048576)
                            throw new InvalidOperationException("GEOMETRY_INSTANCE_COUNT must be from 1 to 1048576.");
                    }
                    nextGeometryVertex = device.CreateVertexShader(Compiler.Compile(source, "GeometryVS", Name + ".hlsl", "vs_5_0").Span);
                    nextComposite = new(device, source, "AfterglowPS", Name + ".hlsl");
                    geometryVertexCount = nextGeometryVertexCount;
                    geometryInstanceCount = nextGeometryInstanceCount;
                    geometryAdditive = nextGeometryAdditive;
                }
                if (nextProgram.Uses("RasterSurface")) nextRaster = new(device, source, Name + ".hlsl");
                if (nextProgram.Uses("FourierDeep0")) nextFourierShadow = new(device, source, "FourierShadowPS", Name + ".hlsl");
                if (nextProgram.Uses("LightRadiance")) nextLighting = new(device, source, "LightPS", Name + ".hlsl");
                if (nextProgram.Uses("LightTransport") || nextLighting?.Uses("LightTransport") == true)
                    nextLightTransport = new(device, source, "LightTransportPS", Name + ".hlsl");
                if (nextProgram.Uses("AtmosphereOpticalDepth") || nextLightTransport?.Uses("AtmosphereOpticalDepth") == true || nextLighting?.Uses("AtmosphereOpticalDepth") == true)
                    nextAtmosphere = new(device, source, "AtmospherePS", Name + ".hlsl");
                if (nextProgram.Uses("CameraTransport") || nextLighting?.Uses("CameraTransport") == true)
                    nextCameraTransport = new(device, source, "CameraTransportPS", Name + ".hlsl");
                if (nextProgram.Uses("PreviousImage")) nextComposite = new(device, source, "FeedbackDisplayPS", Name + ".hlsl");
                if (nextProgram.Uses("FluidDye") || nextComposite?.Uses("FluidDye") == true)
                    nextFluid = new(device, source + ShaderSource.Read(root, Name == "MandelFluidv4"
                        ? "Common/RingFluidSimulation.hlsl" : "Common/FluidSimulation.hlsl"));
                if (nextProgram.Uses("DistanceField"))
                {
                    if (nextProgram.Parameters == null) throw new InvalidOperationException("DistanceField requires scene Parameters for its geometry passes.");
                    nextDistance = new(device, source);
                }
                program = nextProgram; composite = nextComposite; raster = nextRaster; lighting = nextLighting;
                fourierShadow = nextFourierShadow; lightTransport = nextLightTransport; atmosphere = nextAtmosphere;
                cameraTransport = nextCameraTransport; fluid = nextFluid; distance = nextDistance;
                geometryVertex = nextGeometryVertex;
                initialized = true;
            }
            catch
            {
                nextProgram.Dispose(); nextComposite?.Dispose(); nextRaster?.Dispose(); nextLighting?.Dispose();
                nextFourierShadow?.Dispose(); nextLightTransport?.Dispose(); nextAtmosphere?.Dispose();
                nextCameraTransport?.Dispose(); nextFluid?.Dispose(); nextDistance?.Dispose(); nextGeometryVertex?.Dispose();
                throw;
            }
        }
        catch { DisposePrograms(); program = null; initialized = false; throw; }
    }
    void DisposePrograms() { program?.Dispose(); raster?.Dispose(); lighting?.Dispose(); fourierShadow?.Dispose(); lightTransport?.Dispose(); atmosphere?.Dispose(); cameraTransport?.Dispose(); fluid?.Dispose(); distance?.Dispose(); geometryVertex?.Dispose(); composite?.Dispose(); raster = null; lighting = null; fourierShadow = null; lightTransport = null; atmosphere = null; cameraTransport = null; fluid = null; distance = null; geometryVertex = null; composite = null; }
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
        composite = replacement.composite; replacement.composite = null;
        fluid = replacement.fluid; replacement.fluid = null;
        distance = replacement.distance; replacement.distance = null;
        lightTransport = replacement.lightTransport; replacement.lightTransport = null;
        atmosphere = replacement.atmosphere; replacement.atmosphere = null;
        cameraTransport = replacement.cameraTransport; replacement.cameraTransport = null;
        lighting = replacement.lighting; replacement.lighting = null;
        fourierShadow = replacement.fourierShadow; replacement.fourierShadow = null;
        raster = replacement.raster; replacement.raster = null;
        geometryVertex = replacement.geometryVertex; replacement.geometryVertex = null;
        geometryVertexCount = replacement.geometryVertexCount;
        geometryInstanceCount = replacement.geometryInstanceCount;
        geometryAdditive = replacement.geometryAdditive;
        initialized = true;
    }
    public void Dispose() => DisposePrograms();
}
