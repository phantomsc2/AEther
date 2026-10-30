using System;

namespace AEther.WinUI;

/// <summary>Per-pixel sample counts allow exact expiration even when traces or samples overlap.</summary>
internal sealed class ScatterCache
{
    readonly int width, height;
    double lower, upper;
    readonly double seconds;
    readonly int[] colors, background;
    readonly int[][] counts, halos;
    internal int[] Pixels { get; }

    internal ScatterCache(int width, int height, double lower, double upper, double seconds, int[] colors, double[] ticks)
    {
        this.width = width; this.height = height;
        this.lower = lower; this.upper = upper; this.seconds = seconds; this.colors = colors;
        Pixels = new int[checked(width * height)];
        background = new int[height];
        counts = new int[colors.Length][];
        halos = new int[colors.Length][];
        for (int i = 0; i < counts.Length; i++)
        {
            counts[i] = new int[Pixels.Length];
            halos[i] = new int[Pixels.Length];
        }
        Reset(lower, upper, ticks);
    }

    internal void Reset(double lower, double upper, double[] ticks)
    {
        this.lower = lower; this.upper = upper;
        Array.Clear(Pixels);
        Array.Clear(background);
        foreach (var row in counts) Array.Clear(row);
        foreach (var row in halos) Array.Clear(row);
        foreach (double tick in ticks)
        {
            int y = Math.Clamp((int)((upper - tick) / (upper - lower) * (height - 1)), 0, height - 1);
            background[y] = 0x30303030;
            Pixels.AsSpan(y * width, width).Fill(background[y]);
        }
    }

    internal void Add(int trace, ScatterPoint point) => Change(trace, point, 1);
    internal void Remove(int trace, ScatterPoint point) => Change(trace, point, -1);

    void Change(int trace, ScatterPoint point, int delta)
    {
        if (!double.IsFinite(point.Time) || !double.IsFinite(point.LogMilliseconds)
            || point.LogMilliseconds < lower || point.LogMilliseconds > upper) return;
        double xScale = (width - 1) / seconds, yScale = (height - 1) / (upper - lower);
        int x = (int)(ScatterRaster.Wrap(point.Time, seconds) * xScale);
        int y = (int)((upper - point.LogMilliseconds) * yScale);
        ChangePixel(trace, x, y, delta, counts);
        if (x > 0) ChangePixel(trace, x - 1, y, delta, halos);
        if (x + 1 < width) ChangePixel(trace, x + 1, y, delta, halos);
        if (y > 0) ChangePixel(trace, x, y - 1, delta, halos);
        if (y + 1 < height) ChangePixel(trace, x, y + 1, delta, halos);
    }

    void ChangePixel(int trace, int x, int y, int delta, int[][] occupancy)
    {
        int index = y * width + x;
        int count = occupancy[trace][index] + delta;
        if (count < 0) throw new InvalidOperationException("Expired scatter sample was not cached.");
        occupancy[trace][index] = count;
        // An opaque center always wins over any halo, including later traces.
        for (int i = counts.Length - 1; i >= 0; i--)
            if (counts[i][index] != 0) { Pixels[index] = colors[i]; return; }
        for (int i = halos.Length - 1; i >= 0; i--)
            if (halos[i][index] != 0)
            {
                Pixels[index] = ScatterRaster.HaloOver(colors[i], background[y]);
                return;
            }
        Pixels[index] = background[y];
    }
}