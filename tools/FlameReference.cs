using System;
using System.Collections.Generic;

// Independent double-precision CPU reference; not used by the renderer.
internal static class FlameReference
{
    internal readonly record struct Map(double A, double B, double C, double D, double X, double Y)
    {
        internal double Det => A * D - B * C;
        internal (double X, double Y) Forward(double x, double y) => (X + A * x + B * (y - 6), Y + C * x + D * (y - 6));
    }

    internal static Map[] Maps(double growth = .5, double bend = .75, double spread = .5, double scale = .5)
    {
        double g = .78 + .14 * growth, b = -.08 + .16 * bend;
        var maps = new[]
        {
            new Map(g, b, -b, g, 0, 6.7), new Map(.20, -.26, .23, .22, -1.56, 2.92),
            new Map(-.15, .28, .26, .24, 1.68, 2.84)
        };
        for (int i = 0; i < maps.Length; i++)
        {
            var m = maps[i];
            if (i != 0)
            {
                double a = (-20 + 40 * spread) * Math.PI / 180 * (i == 1 ? 1 : -1), k = .75 + .5 * scale;
                double co = Math.Cos(a), si = Math.Sin(a);
                m = m with
                {
                    A = k * (co * m.A - si * m.C), B = k * (co * m.B - si * m.D), C = k * (si * m.A + co * m.C),
                    D = k * (si * m.B + co * m.D)
                };
            }

            double s = Math.Min(1, Math.Min((3.92 - Math.Abs(m.X)) / (4 * Math.Abs(m.A) + 6 * Math.Abs(m.B)),
                (5.88 - Math.Abs(m.Y - 6)) / (4 * Math.Abs(m.C) + 6 * Math.Abs(m.D))));
            maps[i] = m with { A = s * m.A, B = s * m.B, C = s * m.C, D = s * m.D };
        }

        return maps;
    }

    internal static float[] Step(float[] source, int width, int height, Map[] maps)
    {
        var result = new float[source.Length];
        for (int py = 0; py < height; py++)
        for (int px = 0; px < width; px++)
        {
            double x = 8 * (px + .5) / width - 4, y = 12 * (1 - (py + .5) / height), density = 0;
            foreach (var m in maps)
            {
                double dx = x - m.X, dy = y - m.Y;
                double sx = (m.D * dx - m.B * dy) / m.Det, sy = (-m.C * dx + m.A * dy) / m.Det + 6;
                double u = (sx + 4) / 8, v = 1 - sy / 12;
                double fx = u * width - .5, fy = v * height - .5;
                int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                double tx = fx - ix, ty = fy - iy;
                double sample = (1 - ty) * ((1 - tx) * At(ix, iy) + tx * At(ix + 1, iy))
                                + ty * ((1 - tx) * At(ix, iy + 1) + tx * At(ix + 1, iy + 1));
                density += sample / (3 * Math.Abs(m.Det));
            }

            result[py * width + px] = (float)density;
        }

        return result;
        double At(int x, int y) => x < 0 || x >= width || y < 0 || y >= height ? 0 : source[y * width + x];
    }

    internal static IEnumerable<double[]> Settings()
    {
        foreach (double g in new[] { 0, .5, 1 })
        foreach (double b in new[] { 0, .5, 1 })
        foreach (double s in new[] { 0, .5, 1 })
        foreach (double k in new[] { 0, .5, 1 })
            yield return [g, b, s, k];
    }
}