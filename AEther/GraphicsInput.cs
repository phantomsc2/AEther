namespace AEther;

/// <summary>
/// Analysis-to-graphics contract. Domain describes the meaning of each bin;
/// AnalysisRate is the actual publication rate after integer sample batching.
/// HistorySeconds determines retained GPU history. Filter implementation and tuning
/// do not change this contract. Changing it never requires shader compilation.
/// </summary>
public sealed record GraphicsInput(Domain Domain, double AnalysisRate, double HistorySeconds);
