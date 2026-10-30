using System;

namespace AEther.WinUI;

internal static class AdaptiveScatterRaster
{
    internal static int Dimension(int current, double physical)
    {
        if (!double.IsFinite(physical) || physical <= 0) return current;
        if (current >= 2 && physical >= current * 1.5 && physical <= current * 2.5) return current;
        return Math.Max(2, (int)Math.Round(physical / 2, MidpointRounding.AwayFromZero));
    }

    internal static void Restore(int[] source, int oldWidth, int oldHeight,
        int[] destination, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            int sy = (int)Math.Round((double)y * (oldHeight - 1) / (height - 1));
            for (int x = 0; x < width; x++)
            {
                int sx = (int)Math.Round((double)x * (oldWidth - 1) / (width - 1));
                destination[y * width + x] = source[sy * oldWidth + sx];
            }
        }
    }

    internal static bool ClearSweep(int[] pixels, int width, int height,
        double previous, double now, double seconds)
    {
        int first = (int)(ScatterRaster.Wrap(previous, seconds) / seconds * (width - 1));
        int last = (int)(ScatterRaster.Wrap(now, seconds) / seconds * (width - 1));
        int columns = now - previous >= seconds || now < previous
            ? width : (last - first + width) % width + 1;
        bool changed = false;
        for (int offset = 0; offset < columns; offset++)
        {
            int x = (first + offset) % width;
            for (int y = 0; y < height; y++)
            {
                int index = y * width + x;
                if (pixels[index] == 0) continue;
                pixels[index] = 0;
                changed = true;
            }
        }
        return changed;
    }
}
