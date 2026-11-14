using System;

namespace AEther.WinUI;

internal static class AdaptiveScatterRaster
{
    internal static int Dimension(double physical)
    {
        if (!double.IsFinite(physical) || physical <= 0) return 0;
        return Math.Max(2, (int)Math.Round(physical / 2, MidpointRounding.AwayFromZero));
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
