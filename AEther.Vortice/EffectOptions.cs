using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace AEther.Vortice;

public sealed record Froxel3Options(
    [property: Category("Sizes"), Range(8, 256), NumericScale(adaptive: true)] int DepthSlices = 86,
    [property: Category("Sizes"), Range(0, 65535), NumericScale(adaptive: true)] int Instances = 1024,
    [property: Category("Sizes"), Range(4, 32), NumericScale(powersOfTwo: true)] int LightingArcs = 8);

public sealed record Froxel2Options(
    [property: Category("Sizes"), Range(8, 256), NumericScale(adaptive: true)] int DepthSlices = 86);

public sealed record FroxelOptions(
    [property: Category("Sizes"), Range(8, 256), NumericScale(adaptive: true)] int DepthSlices = 64,
    [property: Category("Sizes"), Range(0, 64), NumericScale(adaptive: true)] int PointLights = 4,
    [property: Category("Sizes"), Range(0, 65535), NumericScale(adaptive: true)] int Instances = 1024)
{
    internal (int Width, int Height, int Depth) Resolve(int width, int height)
    {
        if (DepthSlices is < 8 or > 256) throw new InvalidOperationException("DepthSlices must be from 8 through 256.");
        if (Instances is < 0 or > 65535) throw new InvalidOperationException("Instances must be from 0 through 65535.");
        if (PointLights is < 0 or > 64) throw new InvalidOperationException("PointLights must be from 0 through 64.");
        if (width < 1 || height < 1 || width > 16384 || height > 16384)
            throw new InvalidOperationException("Canvas dimensions must be from 1 through 16384.");
        return ((width + 7) / 8, (height + 7) / 8, DepthSlices);
    }
}

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
