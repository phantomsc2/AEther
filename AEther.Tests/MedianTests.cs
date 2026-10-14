
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace AEther.Tests
{
    class MedianTests
    {

        readonly int Width = 1 << 8;
        readonly int Count = 1 << 16;
        readonly int Seed = 123;
        readonly double State = 0.0;
        readonly Comparer<double> Comparer = Comparer<double>.Default;

        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void TestMedianArray()
            => TestMedian(new MovingMedianRef<double>(State, Width, Comparer), new MovingMedianArray<double>(State, Width, Comparer), Count, Seed);

        [Test]
        public void TestMedianBST()
            => TestMedian(new MovingMedianRef<double>(State, Width, Comparer), new MovingMedianBST<double>(State, Width, Comparer), Count, Seed);

        [Test]
        public void TestMedianHeap()
            => TestMedian(new MovingMedianRef<double>(State, Width, Comparer), new MovingMedianHeap<double>(State, Width, Comparer), Count, Seed);

        [TestCase(2)]
        [TestCase(3)]
        [TestCase(8)]
        [TestCase(51)]
        public void HeapHandlesNegativeLogLevelsDuplicatesAndRepeatedClear(int width)
        {
            var expected = new MovingMedianRef<double>(0, width, Comparer);
            var actual = new MovingMedianHeap<double>(0, width, Comparer);
            var random = new Random(919);
            foreach (var seed in new[] { 0.0, -10.0, -2.0, 5.0 })
            {
                expected.Clear(seed);
                actual.Clear(seed);
                for (var i = 0; i < 20 * width; ++i)
                {
                    var value = i % 3 == 0 ? -3 : -10 * random.NextDouble();
                    expected.Filter(value);
                    actual.Filter(value);
                    Assert.That(actual.State, Is.EqualTo(expected.State), $"width {width}, clear {seed}, sample {i}");
                }
            }
        }

        static void TestMedian(MovingFilter<double> a, MovingFilter<double> b, int length, int seed = 0)
        {
            var rng = new Random(seed);
            for (var i = 0; i < length; ++i)
            {
                var x = rng.NextDouble();
                a.Filter(x);
                b.Filter(x);
                Assert.That(b.State, Is.EqualTo(a.State), $"Median differs at sample {i}.");
            }
        }

    }
}
