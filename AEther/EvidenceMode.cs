using System.ComponentModel;

namespace AEther;

public enum EvidenceMode
{
    [Description("Exact median (legacy)")]
    ExactMedian,
    [Description("Per-sample rolling (experimental)")]
    PerSampleAverageSign,
    [Description("Local Gaussian (default)")]
    LocalJointFit,
}
