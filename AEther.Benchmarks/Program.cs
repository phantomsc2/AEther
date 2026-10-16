using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;

namespace AEther.Benchmarks
{
    class Program
    {
        static void Main(string[] args)
        {
            var config = ManualConfig
                .Create(DefaultConfig.Instance)
                .AddDiagnoser(MemoryDiagnoser.Default);
            if (System.Array.IndexOf(args, "--splitter") >= 0)
            {
                BenchmarkRunner.Run<SplitterBenchmark>(config);
                return;
            }
#if DEBUG
            new MatrixBenchmark().SystemNumericsBenchmark();
#else
            BenchmarkRunner.Run<FloatBenchmark>(config);
            //BenchmarkRunner.Run<WAVBenchmark>(config);
            //BenchmarkRunner.Run<MatrixBenchmark>(config);
            //BenchmarkRunner.Run<TimerBenchmark>(config);
#endif
        }
    }
}
