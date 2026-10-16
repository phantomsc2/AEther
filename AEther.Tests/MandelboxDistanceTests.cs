using System;
using NUnit.Framework;

namespace AEther.Tests
{
    public class MandelboxDistanceTests
    {
        // Independent finite differences check what the shader's scalar bound
        // means geometrically; the derivative is never taken from the recurrence.
        static (double[] z, double bound) Orbit(double[] point, double scale = -1.5,
            double time = 0, double julia = 0, double rotation = 0, double anisotropy = 0)
        {
            var z = (double[])point.Clone();
            var bound = 1.0;
            var beta = 1 - julia * (.55 + .45 * Math.Sin(.11 * time + .4));
            var offset = new[] { beta * point[0] + julia * .65 * Math.Sin(.13 * time + .7),
                beta * point[1] + julia * .5 * Math.Sin(.17 * time - 1.1),
                beta * point[2] + julia * .6 * Math.Cos(.09 * time + .2) };
            var stretch = new[] { Math.Exp(anisotropy * Math.Cos(.14 * time)),
                Math.Exp(anisotropy * Math.Cos(.14 * time + 2.0943951)),
                Math.Exp(anisotropy * Math.Cos(.14 * time + 4.1887902)) };
            // Independent double-precision rotations, applied X then Y then Z.
            void Rotate(int a, int b, double angle)
            {
                var x = z[a]; var y = z[b];
                z[a] = Math.Cos(angle) * x - Math.Sin(angle) * y;
                z[b] = Math.Sin(angle) * x + Math.Cos(angle) * y;
            }
            for (var iteration = 0; iteration < 9; ++iteration)
            {
                for (var axis = 0; axis < 3; ++axis)
                    z[axis] = 2 * Math.Clamp(z[axis], -1, 1) - z[axis];
                var radius2 = z[0] * z[0] + z[1] * z[1] + z[2] * z[2];
                var fold = 1 / Math.Clamp(radius2, .25, 1);
                for (var axis = 0; axis < 3; ++axis) z[axis] *= fold;
                Rotate(1, 2, rotation * Math.Cos(.17 * time + .2));
                Rotate(2, 0, rotation * Math.Sin(.13 * time - .4));
                Rotate(0, 1, rotation * Math.Sin(.19 * time + .6));
                for (var axis = 0; axis < 3; ++axis)
                    z[axis] = scale * stretch[axis] * z[axis] + offset[axis];
                bound = Math.Abs(scale) * Math.Max(stretch[0], Math.Max(stretch[1], stretch[2]))
                    * fold * bound + Math.Abs(beta);
            }
            return (z, bound);
        }

        static double[,] JacobianByFiniteDifferences(double[] point, double scale = -1.5,
            double time = 0, double julia = 0, double rotation = 0, double anisotropy = 0)
        {
            const double epsilon = 1e-6;
            var jacobian = new double[3, 3];
            for (var axis = 0; axis < 3; ++axis)
            {
                var plus = (double[])point.Clone();
                var minus = (double[])point.Clone();
                plus[axis] += epsilon;
                minus[axis] -= epsilon;
                var positive = Orbit(plus, scale, time, julia, rotation, anisotropy).z;
                var negative = Orbit(minus, scale, time, julia, rotation, anisotropy).z;
                for (var component = 0; component < 3; ++component)
                    jacobian[component, axis] = (positive[component] - negative[component]) / (2 * epsilon);
            }
            return jacobian;
        }

        [TestCase(.3, .35, .22)]
        [TestCase(1, 1, .5)]
        [TestCase(.3, 0, 0)]
        [TestCase(0, .35, 0)]
        [TestCase(0, 0, .22)]
        public void VariationBoundCoversFiniteDifferenceExpansion(double julia, double rotation, double anisotropy)
        {
            foreach (var scale in new[] { -3.0, -1.5, 0, 1.5 })
            foreach (var time in new[] { 0.0, 4, 12, 30, 70 })
            for (var index = 1; index <= 100; index++)
            {
                var point = new[] { 2.3 * Math.Sin(index * 12.783),
                    2.3 * Math.Sin(index * 4.317), .15 + .3 * Math.Sin(index * 1.13) };
                var (_, bound) = Orbit(point, scale, time, julia, rotation, anisotropy);
                var j = JacobianByFiniteDifferences(point, scale, time, julia, rotation, anisotropy);
                // Probe mixed directions as well as the coordinate axes.
                foreach (var direction in new[] { new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 },
                    new[] { 0.0, 0, 1 }, new[] { .6, 0, .8 }, new[] { 1 / Math.Sqrt(3), 1 / Math.Sqrt(3), 1 / Math.Sqrt(3) } })
                {
                    double length2 = 0;
                    for (var row = 0; row < 3; row++)
                    {
                        var value = j[row, 0] * direction[0] + j[row, 1] * direction[1] + j[row, 2] * direction[2];
                        length2 += value * value;
                    }
                    Assert.That(bound, Is.GreaterThanOrEqualTo(Math.Sqrt(length2) * .999),
                        $"scale {scale}, time {time}, sample {index}");
                }
            }
        }

        [TestCase(-3.0), TestCase(-2.0), TestCase(-1.5), TestCase(-.5)]
        [TestCase(0.0), TestCase(.5), TestCase(1.5), TestCase(2.0)]
        public void ScalarRecurrenceBoundsTheLocalRadialDerivative(double scale)
        {
            for (var index = 1; index <= 300; ++index)
            {
                var point = new[] { 2.3 * Math.Sin(index * 12.783),
                    2.3 * Math.Sin(index * 4.317), .15 + .3 * Math.Sin(index * 1.13) };
                var (z, bound) = Orbit(point, scale);
                var jacobian = JacobianByFiniteDifferences(point, scale);
                var radius = Math.Sqrt(z[0] * z[0] + z[1] * z[1] + z[2] * z[2]);
                var gradient2 = 0.0;
                for (var axis = 0; axis < 3; ++axis)
                {
                    var directional = 0.0;
                    for (var component = 0; component < 3; ++component)
                        directional += jacobian[component, axis] * z[component] / radius;
                    gradient2 += directional * directional;
                }
                Assert.That(bound, Is.GreaterThanOrEqualTo(Math.Sqrt(gradient2) * .999),
                    $"sample {index}");
            }
        }

        [Test]
        public void CubeRootJacobianDeterminantCanMissTheRelevantDerivative()
        {
            var point = new[] { .9382586305707307, 2.047139978515149, .35972615086374277 };
            var (z, _) = Orbit(point);
            var j = JacobianByFiniteDifferences(point);
            var determinant = j[0, 0] * (j[1, 1] * j[2, 2] - j[1, 2] * j[2, 1])
                            - j[0, 1] * (j[1, 0] * j[2, 2] - j[1, 2] * j[2, 0])
                            + j[0, 2] * (j[1, 0] * j[2, 1] - j[1, 1] * j[2, 0]);
            var radius = Math.Sqrt(z[0] * z[0] + z[1] * z[1] + z[2] * z[2]);
            var gradient2 = 0.0;
            for (var axis = 0; axis < 3; ++axis)
            {
                var directional = 0.0;
                for (var component = 0; component < 3; ++component)
                    directional += j[component, axis] * z[component] / radius;
                gradient2 += directional * directional;
            }
            var radialDerivative = Math.Sqrt(gradient2);
            var determinantScale = Math.Cbrt(Math.Abs(determinant));
            TestContext.Out.WriteLine($"Local radial derivative={radialDerivative:F3}, cube-root determinant={determinantScale:F3}, ratio={radialDerivative / determinantScale:F3}");
            Assert.That(radialDerivative, Is.GreaterThan(4 * determinantScale));
        }
    }
}
