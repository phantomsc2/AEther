using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

/// <summary>Build-time scene composition. Factories construct independent effect instances.</summary>
public sealed class SceneDefinition
{
    public string Name { get; }
    internal Func<ID3D11Device, ID3D11DeviceContext, string, SceneEffect[]> Create { get; }
    internal Action<IReadOnlyList<SceneEffect>, EffectFrame> Draw { get; }
    internal SceneDefinition(string name, Func<ID3D11Device, ID3D11DeviceContext, string, SceneEffect[]> create,
        Action<IReadOnlyList<SceneEffect>, EffectFrame>? draw = null)
    {
        Name = name; Create = create;
        Draw = draw ?? ((effects, frame) => { foreach (var effect in effects) effect.Draw(frame); });
    }

    public static IReadOnlyList<SceneDefinition> Defaults { get; } =
    [
        Fullscreen("Histogram"), Fullscreen("Spectrum"), Fullscreen("Debug"), Fullscreen("MandelPond"),
        new("MandelLight", (d, c, s) => [new RasterEffect("Raster", d, c, s, "MandelLight.hlsl",
            new RasterOptions(LookupWidth: 513, LookupHeight: 1025))]),
        new("MandelFluid", (d, c, s) => [new FluidEffect("Fluid", d, c, s, "MandelFluid.hlsl",
            new FluidOptions(), Format.R32G32B32A32_Float, Format.R32G32_Float)]),
    ];
    internal static SceneDefinition Fullscreen(string name) => new(name,
        (d, c, s) => [new FullscreenEffect("Display", d, c, s, name + ".hlsl")]);
}

public sealed class ShaderScene : IDisposable
{
    public string Name => definition.Name;
    public IReadOnlyList<SceneEffect> Effects { get; private set; } = [];
    readonly SceneDefinition definition;
    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly string source;
    internal bool ProgramReady => Effects.Count != 0;
    internal ShaderScene(ID3D11Device device, ID3D11DeviceContext context, SceneDefinition definition, string source)
    { this.device = device; this.context = context; this.definition = definition; this.source = source; }
    internal ShaderProgram CreateDefaultProgram() => new(device, source, "PS", Name + ".hlsl");
    internal void EnsureCompiled()
    {
        if (ProgramReady) return;
        var next = definition.Create(device, context, source);
        if (next.Length == 0 || next.Any(e => string.IsNullOrWhiteSpace(e.Name))
            || next.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != next.Length)
        {
            foreach (var effect in next) effect.Dispose();
            throw new InvalidOperationException($"Scene '{Name}' requires uniquely named effects.");
        }
        Effects = next;
    }
    public IReadOnlyDictionary<string, EffectSettings> CaptureSettings() =>
        Effects.ToDictionary(e => e.Name, e => e.CaptureSettings(), StringComparer.Ordinal);
    public void RestoreSettings(IReadOnlyDictionary<string, EffectSettings> settings)
    {
        foreach (var effect in Effects)
            if (settings.TryGetValue(effect.Name, out var saved)) effect.RestoreSettings(saved);
    }
    internal ShaderScene Recompile()
    {
        var next = new ShaderScene(device, context, definition, source);
        next.EnsureCompiled(); next.RestoreSettings(CaptureSettings()); return next;
    }
    internal void ReplaceWith(ShaderScene replacement)
    {
        Dispose(); Effects = replacement.Effects; replacement.Effects = [];
    }
    internal void Deactivate() { foreach (var effect in Effects) effect.Deactivate(); }
    internal void Draw(EffectFrame frame) => definition.Draw(Effects, frame);
    public void Dispose() { foreach (var effect in Effects) effect.Dispose(); Effects = []; }
}
