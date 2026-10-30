#nullable enable
using System;
using System.Collections.Generic;
namespace AEther.WinUI;

/// <summary>Every retained sample is mapped independently to a soft five-pixel cross.</summary>
internal readonly record struct ScatterPoint(double Time, double LogMilliseconds);

internal static class ScatterRaster
{
    // Premultiplied BGRA: alpha 64/255 approximates one-quarter opacity.
    internal static int HaloOver(int color, int background)
    {
        uint c = (uint)color, b = (uint)background;
        uint r = (((c >> 16) & 255) * 64 + ((b >> 16) & 255) * 191 + 127) / 255;
        uint g = (((c >> 8) & 255) * 64 + ((b >> 8) & 255) * 191 + 127) / 255;
        uint blue = ((c & 255) * 64 + (b & 255) * 191 + 127) / 255;
        uint a = 64 + ((b >> 24) * 191 + 127) / 255;
        return unchecked((int)((a << 24) | (r << 16) | (g << 8) | blue));
    }

    internal static double Wrap(double time, double seconds) => (time % seconds + seconds) % seconds;

    internal static void Draw(int[] pixels, int width, int height, List<ScatterPoint> points,
        int color, double lower, double upper, double seconds, int[]? background = null)
    {
        double xScale = (width - 1) / seconds, yScale = (height - 1) / (upper - lower);
        foreach (var point in points)
        {
            if (!double.IsFinite(point.Time) || !double.IsFinite(point.LogMilliseconds) || point.LogMilliseconds < lower || point.LogMilliseconds > upper) continue;
            int x = (int)(Wrap(point.Time, seconds) * xScale);
            int y = (int)((upper - point.LogMilliseconds) * yScale);
            if ((uint)x < (uint)width && (uint)y < (uint)height)
            {
                pixels[y * width + x] = color;
                Halo(x - 1, y); Halo(x + 1, y); Halo(x, y - 1); Halo(x, y + 1);
            }
        }

        void Halo(int x, int y)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
            int i = y * width + x;
            if ((uint)pixels[i] >> 24 == 255) return;
            pixels[i] = HaloOver(color, background?[i] ?? 0);
        }
    }
}
