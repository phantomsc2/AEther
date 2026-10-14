using System;
using NUnit.Framework;

namespace AEther.Tests
{
    public class MandelboxDistanceTests
    {
        // Independent finite differences check what the shader's scalar bound
        // means geometrically; the derivative is never taken from the recurrence.
        static (double[] z, double bound) Orbit(double[] point)
        {
            var z = (double[])point.Clone();
            var bound = 1.0;
            for (var iteration = 0; iteration < 9; ++iteration)
            {
                for (var axis = 0; axis < 3; ++axis)
                    z[axis] = 2 * Math.Clamp(z[axis], -1, 1) - z[axis];
                var radius2 = z[0] * z[0] + z[1] * z[1] + z[2] * z[2];
                var fold = 1 / Math.Clamp(radius2, .25, 1);
                for (var axis = 0; axis < 3; ++axis)
                    z[axis] = -1.5 * fold * z[axis] + point[axis];
                bound = 1.5 * fold * bound + 1;
            }
            return (z, bound);
        }

        static double[,] JacobianByFiniteDifferences(double[] point)
        {
            const double epsilon = 1e-6;
            var jacobian = new double[3, 3];
            for (var axis = 0; axis < 3; ++axis)
            {
                var plus = (double[])point.Clone();
                var minus = (double[])point.Clone();
                plus[axis] += epsilon;
                minus[axis] -= epsilon;
                var positive = Orbit(plus).z;
                var negative = Orbit(minus).z;
                for (var component = 0; component < 3; ++component)
                    jacobian[component, axis] = (positive[component] - negative[component]) / (2 * epsilon);
            }
            return jacobian;
        }

        [Test]
        public void ScalarRecurrenceBoundsTheLocalRadialDerivative()
        {
            for (var index = 1; index <= 300; ++index)
            {
                var point = new[] { 2.3 * Math.Sin(index * 12.783),
                    2.3 * Math.Sin(index * 4.317), .15 + .3 * Math.Sin(index * 1.13) };
                var (z, bound) = Orbit(point);
                var jacobian = JacobianByFiniteDifferences(point);
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
