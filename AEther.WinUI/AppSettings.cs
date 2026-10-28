using AEther.Vortice;

namespace AEther.WinUI;

/// <summary>User preferences persisted between app launches.</summary>
public sealed class AppSettings
{
    public AnalyzerOptions Analyzer { get; set; } = new();
    public GraphicsOptions Graphics { get; set; } = new();
}
