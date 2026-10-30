using System;

namespace AEther;

/// <summary>Number of fractional digits shown by generated numeric editors.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NumericPrecisionAttribute : Attribute
{
    public int FractionDigits { get; }

    public NumericPrecisionAttribute(int fractionDigits)
    {
        if (fractionDigits is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(fractionDigits));
        FractionDigits = fractionDigits;
    }
}
