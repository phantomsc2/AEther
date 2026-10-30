using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

public sealed record EffectSettings(ShaderParameters? Parameters, object? Sizes);

/// <summary>A running effect owns every mutable shader binding and all persistent GPU state.</summary>
public abstract class SceneEffect : IDisposable
{
    internal static readonly string[] FrameBindings = ["FrameConstants", "Linear", "Spectrum0", "Spectrum1",
        "Histogram0", "Histogram1", "FilteredHistogram0", "FilteredHistogram1"];
    public string Name { get; }
    public ShaderParameters? Parameters { get; protected set; }
    public virtual object? SizeOptions => null;
    internal readonly ShaderProgram Display;
    protected readonly ID3D11Device Device;
    protected readonly ID3D11DeviceContext Context;
    readonly Dictionary<string, ID3D11Buffer> buffers = [];
    readonly Dictionary<string, ID3D11ShaderResourceView> textures = [];
    readonly Dictionary<string, ID3D11SamplerState> samplers = [];
    ShaderParameters? snapshot;
    long version = -1;

    internal SceneEffect(string name, ID3D11Device device, ID3D11DeviceContext context, string source, string file)
    {
        Name = name; Device = device; Context = context;
        Display = new(device, source, "PS", file);
        Parameters = Display.Parameters;
    }

    public EffectSettings CaptureSettings() => new(Parameters?.Snapshot(), SizeOptions);
    public void RestoreSettings(EffectSettings settings)
    {
        if (settings.Parameters != null) Parameters?.Restore(settings.Parameters);
        RestoreSizes(settings.Sizes);
    }
    protected virtual void RestoreSizes(object? sizes) { }

    internal void Draw(EffectFrame frame)
    {
        if (version != (Parameters?.Version ?? 0))
        {
            snapshot = Parameters?.Snapshot();
            version = snapshot?.Version ?? 0;
            frame.ReportError(null);
        }
        buffers.Clear(); foreach (var pair in frame.Buffers) buffers.Add(pair.Key, pair.Value);
        textures.Clear(); foreach (var pair in frame.Textures) textures.Add(pair.Key, pair.Value);
        samplers.Clear(); foreach (var pair in frame.Samplers) samplers.Add(pair.Key, pair.Value);
        Prepare(frame, snapshot, buffers, textures, samplers);
        Context.RSSetViewport(0, 0, frame.Width, frame.Height);
        Context.VSSetShader(frame.Vertex);
        Context.OMSetRenderTargets(frame.Target);
        frame.Marker("Scene", true);
        Display.Bind(Context, snapshot, buffers, textures, samplers);
        Context.Draw(3, 0);
        frame.Marker("Scene", false);
    }

    internal virtual void Prepare(EffectFrame frame, ShaderParameters? parameters,
        Dictionary<string, ID3D11Buffer> buffers, Dictionary<string, ID3D11ShaderResourceView> textures,
        Dictionary<string, ID3D11SamplerState> samplers) { }

    internal virtual void Deactivate()
    {
        // The immediate context retains COM references independently of our wrappers.
        Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        Context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        Context.VSSetShaderResources(0, ShaderProgram.EmptyViews);
        Context.CSSetShaderResources(0, ShaderProgram.EmptyViews);
        for (uint slot = 0; slot < 8; slot++) Context.CSSetUnorderedAccessView(slot, null);
        for (uint slot = 0; slot < 14; slot++)
        {
            Context.PSSetConstantBuffer(slot, null); Context.VSSetConstantBuffer(slot, null); Context.CSSetConstantBuffer(slot, null);
        }
        buffers.Clear(); textures.Clear(); samplers.Clear();
    }
    public virtual void Dispose() { Deactivate(); Display.Dispose(); }
}

internal readonly record struct EffectFrame(int Width, int Height, double Dt, double SourceAgeRate,
    ID3D11VertexShader Vertex, ID3D11RenderTargetView Target,
    IReadOnlyDictionary<string, ID3D11Buffer> Buffers,
    IReadOnlyDictionary<string, ID3D11ShaderResourceView> Textures,
    IReadOnlyDictionary<string, ID3D11SamplerState> Samplers,
    Action<string, bool> Marker, Action<string, bool> DrawMarker, Action<string?> ReportError,
    bool DisplayMaterial);

internal sealed class FullscreenEffect : SceneEffect
{
    internal FullscreenEffect(string name, ID3D11Device device, ID3D11DeviceContext context, string source, string file)
        : base(name, device, context, source, file)
    {
        try
        {
            Display.ValidateBindings(FrameBindings);
            Parameters = ShaderParameters.Merge([Display.Parameters]);
        }
        catch { Display.Dispose(); throw; }
    }
}

public sealed class FluidEffect : SceneEffect
{
    FluidOptions options;
    FluidOptions? accepted;
    public FluidOptions Options { get => Volatile.Read(ref options); set => Volatile.Write(ref options, value); }
    public FluidOptions DefaultOptions { get; }
    public override object SizeOptions => Options;
    internal FluidSimulation? Simulation { get; private set; }
    internal FluidSimulation.Programs Programs { get; }

    internal FluidEffect(string name, ID3D11Device device, ID3D11DeviceContext context, string source, string file,
        FluidOptions options, params Format[] materials) : base(name, device, context, source, file)
    {
        this.options = options;
        DefaultOptions = options;
        try
        {
            Programs = new(device, source, file, Display, materials);
            Parameters = ShaderParameters.Merge(Programs.Shaders.Values.Select(s => s.Parameters).Prepend(Display.Parameters));
            Display.ValidateBindings(FrameBindings.Concat(FluidSimulation.MaterialNames.Take(materials.Length))
                .Concat(["FluidVelocity", "FluidSampler"]));
        }
        catch { Programs?.Dispose(); Display.Dispose(); throw; }
    }
    protected override void RestoreSizes(object? sizes) { if (sizes is FluidOptions value) Options = value; }
    internal override void Prepare(EffectFrame frame, ShaderParameters? parameters,
        Dictionary<string, ID3D11Buffer> buffers, Dictionary<string, ID3D11ShaderResourceView> textures,
        Dictionary<string, ID3D11SamplerState> samplers)
    {
        var requested = Options;
        try
        {
            requested.Validate();
            if (Simulation == null || !Simulation.CompatibleWith(Programs, requested) || Simulation.Aspect != frame.Width / (float)frame.Height)
            {
                var next = new FluidSimulation(Device, Context, frame.Width, frame.Height, Programs, requested);
                Simulation?.Dispose(); Simulation = next;
            }
            if (requested != accepted) frame.ReportError(null);
            accepted = requested;
        }
        catch (Exception error)
        {
            if (accepted == null || Simulation == null) throw;
            Interlocked.CompareExchange(ref options, accepted, requested);
            frame.ReportError(error.Message);
        }
        var fluid = Simulation!;
        fluid.PassMarker = frame.Marker; fluid.DrawMarker = frame.DrawMarker;
        if (frame.Dt > .5) fluid.Clear();
        frame.Marker("Fluid", true);
        fluid.Advance(frame.Dt, frame.SourceAgeRate, Programs, parameters, buffers, textures, samplers, frame.DisplayMaterial);
        frame.Marker("Fluid", false);
        fluid.Publish(textures);
        samplers["FluidSampler"] = fluid.Sampler;
    }
    internal override void Deactivate() { base.Deactivate(); Simulation?.Dispose(); Simulation = null; accepted = null; }
    public override void Dispose() { base.Dispose(); Programs.Dispose(); }
}

public sealed class RasterEffect : SceneEffect
{
    RasterOptions options;
    RasterOptions? accepted;
    public RasterOptions Options { get => Volatile.Read(ref options); set => Volatile.Write(ref options, value); }
    public RasterOptions DefaultOptions { get; }
    public override object SizeOptions => Options;
    internal RasterLight? Raster { get; private set; }
    readonly RasterLight.Programs programs;

    internal RasterEffect(string name, ID3D11Device device, ID3D11DeviceContext context, string source, string file,
        RasterOptions options) : base(name, device, context, source, file)
    {
        this.options = options;
        DefaultOptions = options;
        try
        {
            programs = new(device, source, file);
            Parameters = ShaderParameters.Merge(programs.ParameterLayouts.Prepend(Display.Parameters));
            Display.ValidateBindings(FrameBindings.Concat(["RasterConstants", "RasterSurface", "RasterDepth", "RasterAirlight",
                "PointLights", "RasterAirlightLut", "RasterLookupState", "RasterLutSampler"]));
        }
        catch { programs?.Dispose(); Display.Dispose(); throw; }
    }
    protected override void RestoreSizes(object? sizes) { if (sizes is RasterOptions value) Options = value; }
    internal override void Prepare(EffectFrame frame, ShaderParameters? parameters,
        Dictionary<string, ID3D11Buffer> buffers, Dictionary<string, ID3D11ShaderResourceView> textures,
        Dictionary<string, ID3D11SamplerState> samplers)
    {
        var requested = Options;
        try
        {
            var resolved = requested.Resolve(frame.Width, frame.Height);
            Raster ??= new(Device, Context);
            Raster.Configure(resolved);
            if (requested != accepted) frame.ReportError(null);
            accepted = requested;
        }
        catch (Exception error)
        {
            if (accepted == null) throw;
            Interlocked.CompareExchange(ref options, accepted, requested);
            frame.ReportError(error.Message);
        }
        var raster = Raster!;
        raster.PassMarker = frame.Marker;
        raster.Prepare(programs, parameters, frame.Width, frame.Height, buffers, textures, samplers);
        Context.VSSetShader(frame.Vertex);
        raster.IntegrateAirlight(programs, parameters);
        buffers["RasterConstants"] = raster.Constants;
        samplers["RasterLutSampler"] = raster.LookupSampler;
    }
    internal override void Deactivate() { base.Deactivate(); Raster?.Dispose(); Raster = null; accepted = null; }
    public override void Dispose() { base.Dispose(); programs.Dispose(); }
}
