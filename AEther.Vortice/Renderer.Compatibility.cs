namespace AEther.Vortice;

public enum Scene { Spectrum, Histogram, Mandelbox, MandelFluid }

// Transitional adapters for existing rendering probes. New code uses filename identity and reflected parameters.
public sealed partial class Renderer
{
    // Compatibility for existing rendering probes; discovery and the desktop use filenames.
    public Scene Scene { get => Enum.TryParse<Scene>(SceneName, out var value) ? value : default; set => SelectScene(value.ToString()); }
    // Compatibility adapters for existing callers. The scene runtime and UI use reflected trees exclusively.
    ShaderParameters? Values(string path) => ActiveShader?.Parameters is { } active && ShaderParameters.Leaves(active.Items).Any(p => p.Path == path)
        ? active : scenes.GetValueOrDefault(path.StartsWith("Fluid.", StringComparison.Ordinal) ? "MandelFluid" : "Mandelbox")?.Parameters;
    float ReadValue(string path, float fallback = 0)
    {
        var values = Values(path);
        var parameter = values == null ? null : ShaderParameters.Leaves(values.Items).FirstOrDefault(p => p.Path == path);
        return parameter == null ? fallback : values!.GetFloat(parameter);
    }
    void WriteValue(string path, float value)
    {
        var values = Values(path);
        var parameter = values == null ? null : ShaderParameters.Leaves(values.Items).FirstOrDefault(p => p.Path == path);
        if (parameter != null) values!.SetFloat(parameter, Math.Clamp(value, 0, 1));
    }
    bool ReadBoolean(string path)
    {
        var values = ActiveShader?.Parameters;
        var parameter = values == null ? null : ShaderParameters.Leaves(values.Items).FirstOrDefault(p => p.Path == path);
        return parameter != null && values!.GetBoolean(parameter);
    }
    void WriteBoolean(string path, bool value)
    {
        var values = ActiveShader?.Parameters;
        var parameter = values == null ? null : ShaderParameters.Leaves(values.Items).FirstOrDefault(p => p.Path == path);
        if (parameter != null) values!.SetBoolean(parameter, value);
    }
    public bool CombinedLevel { get => ReadBoolean("CombinedLevel"); set => WriteBoolean("CombinedLevel", value); }
    public bool Intensity { get => ReadBoolean("Intensity"); set => WriteBoolean("Intensity", value); }
    public float SliceDepth { get => ReadValue("Geometry.SliceDepth", .5f) * 4 - 2; set => WriteValue("Geometry.SliceDepth", (value + 2) / 4); }
    public bool MandelboxAntialiasing { get => ReadBoolean("Quality.Antialiasing"); set => WriteBoolean("Quality.Antialiasing", value); }
    public int MandelboxIterations { get => 1 + (int)Math.Round(ReadValue("Quality.MaximumIterations") * 29); set => WriteValue("Quality.MaximumIterations", (value - 1) / 29f); }
    public float MandelboxMovementSpeed { get => ReadValue("Quality.MovementSpeed") * 10; set => WriteValue("Quality.MovementSpeed", value / 10); }
    public float MandelboxScale { get => ReadValue("Geometry.Scale") * 8 - 4; set => WriteValue("Geometry.Scale", (value + 4) / 8); }
    public float MandelboxJuliaBlend { get => ReadValue("Geometry.JuliaBlend"); set => WriteValue("Geometry.JuliaBlend", value); }
    public float MandelboxFoldRotation { get => ReadValue("Geometry.FoldRotation"); set => WriteValue("Geometry.FoldRotation", value); }
    public float MandelboxAnisotropy { get => ReadValue("Geometry.Anisotropy") * .5f; set => WriteValue("Geometry.Anisotropy", value * 2); }
    public float FluidFlow { get => ReadValue("Fluid.Flow") * 3; set => WriteValue("Fluid.Flow", value / 3); }
    public float FluidHalfLife { get => .1f + ReadValue("Fluid.HalfLife") * 9.9f; set => WriteValue("Fluid.HalfLife", (value - .1f) / 9.9f); }
    public float FluidDiffusion { get => ReadValue("Fluid.Diffusion") * .01f; set => WriteValue("Fluid.Diffusion", value * 100); }
    public float FluidNoise { get => ReadValue("Fluid.Background") * 3; set => WriteValue("Fluid.Background", value / 3); }
    public float FluidCurl { get => ReadValue("Fluid.Curl") * 2; set => WriteValue("Fluid.Curl", value / 2); }}
