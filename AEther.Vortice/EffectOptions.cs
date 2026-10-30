using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace AEther.Vortice;

/// <summary>Numeric presentation metadata shared by option editors.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NumericScaleAttribute(bool powersOfTwo = false, bool adaptive = false) : Attribute
{
    public bool PowersOfTwo { get; } = powersOfTwo;
    public bool Adaptive { get; } = adaptive;
}

public sealed record FluidOptions(
    [property: Category("Sizes"), Range(16, 2048), NumericScale(powersOfTwo: true)] int Width = 512,
    [property: Category("Sizes"), Range(16, 2048), NumericScale(powersOfTwo: true)] int Height = 512)
{
    internal void Validate()
    {
        if (Width is < 16 or > 2048 || (Width & (Width - 1)) != 0
            || Height is < 16 or > 2048 || (Height & (Height - 1)) != 0)
            throw new InvalidOperationException("Fluid dimensions must be powers of two from 16 through 2048.");
    }
}

public sealed record RasterOptions(
    [property: Category("Sizes"), Range(0, 4194240), NumericScale(adaptive: true)] uint PointLights = 180,
    [property: Category("Sizes"), Range(0d, uint.MaxValue), NumericScale(adaptive: true)] uint Vertices = 36,
    [property: Category("Sizes"), Range(0d, uint.MaxValue), NumericScale(adaptive: true)] uint Instances = 128,
    [property: Category("Sizes"), Range(0, 16384), NumericScale(adaptive: true), Description("Zero follows canvas width.")] int SurfaceWidth = 0,
    [property: Category("Sizes"), Range(0, 16384), NumericScale(adaptive: true), Description("Zero follows canvas height.")] int SurfaceHeight = 0,
    [property: Category("Sizes"), Range(0, 16384), NumericScale(adaptive: true), Description("Zero follows canvas width.")] int AirWidth = 0,
    [property: Category("Sizes"), Range(0, 16384), NumericScale(adaptive: true), Description("Zero follows canvas height.")] int AirHeight = 0,
    [property: Category("Sizes"), Range(0, 16384), NumericScale(adaptive: true), Description("Zero follows canvas width; explicit values must be at least two.")] int LookupWidth = 0,
    [property: Category("Sizes"), Range(0, 16384), NumericScale(adaptive: true), Description("Zero follows canvas height; explicit values must be at least two.")] int LookupHeight = 0)
{
    internal RasterOptions Resolve(int width, int height)
    {
        if (PointLights > 64u * 65535) throw new InvalidOperationException("PointLights exceeds the backend's dispatch limit.");
        if ((ulong)Vertices * Instances > uint.MaxValue) throw new InvalidOperationException("Geometry exceeds the draw vertex limit.");
        static int Axis(int n, int canvas, int min = 1)
        {
            if (n == 0) n = canvas;
            if (n < min || n > 16384) throw new InvalidOperationException($"Texture dimensions must be from {min} through 16384.");
            return n;
        }
        return this with
        {
            SurfaceWidth = Axis(SurfaceWidth, width), SurfaceHeight = Axis(SurfaceHeight, height),
            AirWidth = Axis(AirWidth, width), AirHeight = Axis(AirHeight, height),
            LookupWidth = Axis(LookupWidth, width, 2), LookupHeight = Axis(LookupHeight, height, 2),
        };
    }
}
