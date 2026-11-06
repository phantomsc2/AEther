using System;
using NUnit.Framework;

namespace AEther.Tests;

public class FlameTests
{
    [Test]
    public void InversePixelGradientsMatchFiniteDifferences()
    {
        const double w = 1280, h = 720;
        foreach (var p in FlameReference.Settings())
        foreach (var m in FlameReference.Maps(p[0], p[1], p[2], p[3]))
        {
            (double U, double V) UV(double x, double y) => (((m.D * (x - m.X) - m.B * (y - m.Y)) / m.Det + 4) / 8,
                1 - ((-m.C * (x - m.X) + m.A * (y - m.Y)) / m.Det + 6) / 12);

            var origin = UV(.37, 5.2);
            var right = UV(.37 + 8 / w, 5.2);
            var down = UV(.37, 5.2 - 12 / h);
            Assert.That(right.U - origin.U, Is.EqualTo(m.D / (w * m.Det)).Within(1e-12));
            Assert.That(right.V - origin.V, Is.EqualTo(8 * m.C / (12 * w * m.Det)).Within(1e-12));
            Assert.That(down.U - origin.U, Is.EqualTo(12 * m.B / (8 * h * m.Det)).Within(1e-12));
            Assert.That(down.V - origin.V, Is.EqualTo(m.A / (h * m.Det)).Within(1e-12));
        }
    }

    [Test]
    public void FernFamilyIsBoundedInvertibleAndContractive()
    {
        foreach (var p in FlameReference.Settings())
        foreach (var m in FlameReference.Maps(p[0], p[1], p[2], p[3]))
        {
            Assert.That(Math.Abs(m.Det), Is.GreaterThan(.001));
            double q = m.A * m.A + m.B * m.B + m.C * m.C + m.D * m.D;
            double norm = Math.Sqrt((q + Math.Sqrt(Math.Max(0, q * q - 4 * m.Det * m.Det))) / 2);
            Assert.That(norm, Is.LessThan(1));
            foreach (double x in new[] { -4.0, 4 })
            foreach (double y in new[] { 0.0, 12 })
            {
                var v = m.Forward(x, y);
                Assert.That(Math.Abs(v.X), Is.LessThanOrEqualTo(3.92000001));
                Assert.That(Math.Abs(v.Y - 6), Is.LessThanOrEqualTo(5.88000001));
            }
        }
    }
}