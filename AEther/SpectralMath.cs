using System;

namespace AEther;

internal static class SpectralMath
{
    internal static double Sanitize(double value) => double.IsFinite(value) && value > 0 ? value : 0;
    internal static double FromLog(double value) => double.IsFinite(value) ? Sanitize(Math.Pow(10, value)) : 0;
}
