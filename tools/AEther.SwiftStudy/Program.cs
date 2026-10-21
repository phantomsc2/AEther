using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace AEther.SwiftStudy;

static class Program
{
    static void Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var directory = Path.Combine(root, "doc", "swift-study");
        Directory.CreateDirectory(directory);
        if (args.Contains("--pin"))
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("--pin requires Windows.");
            if (SetThreadAffinityMask(GetCurrentThread(), 1) == 0) throw new System.ComponentModel.Win32Exception();
        }
        var cpu = new StringBuilder();
        if (X86Base.IsSupported)
            for (int leaf = unchecked((int)0x80000002); leaf <= unchecked((int)0x80000004); ++leaf)
            {
                var (a, b, c, d) = X86Base.CpuId(leaf, 0);
                foreach (var word in new[] { a, b, c, d }) cpu.Append(Encoding.ASCII.GetString(BitConverter.GetBytes(word)));
            }
        var metadata = $"UTC={DateTime.UtcNow:O}\nCPU={cpu.ToString().Trim('\0', ' ')}\nRuntime={Environment.Version}\n"
            + $"OS={RuntimeInformation.OSDescription}\nVector<double>={Vector<double>.Count}\nHardwareSIMD={Vector.IsHardwareAccelerated}\n"
            + $"LogicalCPUs={Environment.ProcessorCount}\nPinnedCPU={(args.Contains("--pin") ? "0" : "none")}\nTieredCompilation={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"}\n"
            + "SWIFTCalibration=full -3 dB bandwidth / geometric bin width\nSWIFTWidthScale=1\nSWIFTImbalance=0\n";
        Console.WriteLine(metadata);
        File.WriteAllText(Path.Combine(directory, args.Contains("--bench") ? "benchmark-machine.txt" : "quality-machine.txt"), metadata);
        if (args.Contains("--quality")) Quality.Run(root, directory);
        if (args.Contains("--bench")) Bench.Run(directory);
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)] static extern nuint SetThreadAffinityMask(IntPtr thread, nuint mask);
}
