using System.Numerics;

namespace AEther.SwiftStudy;

static class Quality
{
    static double Db(double x) => 20 * Math.Log10(Math.Max(1e-16, x));
    internal static ISpectrumProcessor Create(string name, Domain domain, int fs) => name switch
    {
        "Hann SIMD" => new DFTProcessor(domain, fs, true, DFTWindow.Hann),
        "SWIFT scalar" => new SwiftProcessor(domain, fs, false),
        "SWIFT 5-3" => new SwiftProcessor(domain.Select(f => SwiftTuning.FromCycles(f, fs)).ToArray(), fs, true),
        _ => new SwiftProcessor(domain, fs, true),
    };

    public static void Run(string root, string directory)
    {
        using (var tuning = new StreamWriter(Path.Combine(directory, "tuning.csv")))
        {
            tuning.WriteLine("sample_rate,bins_per_octave,frequency,slow_cycles,fast_cycles,hann_neighbor_db,swift_neighbor_db,expiry_seconds");
            foreach (var fs in new[] { 44100, 48000 })
            foreach (var res in new[] { 12, 24 })
            foreach (var f in Domain.FromRange(30, 8000, res))
            {
                var b = SwiftTuning.FromBandwidth(f, res, fs);
                var lo = f * (Math.Pow(2, -1.0 / res) - 1); var hi = f * (Math.Pow(2, 1.0 / res) - 1);
                var cycles = Math.Round(1 / (Math.Pow(2, 1.0 / res) - 1));
                var length = (int)Math.Round(cycles * fs / f);
                var center = cycles * fs / length;
                double Hann(double offset) => SwiftTuning.HannResponse(f + offset - center, fs, length).Magnitude;
                var hannLeak = Math.Max(Hann(lo), Hann(hi)) / Hann(0);
                var leak = Math.Max(SwiftTuning.Response(lo, fs, b.SlowSeconds, b.FastSeconds).Magnitude,
                    SwiftTuning.Response(hi, fs, b.SlowSeconds, b.FastSeconds).Magnitude);
                tuning.WriteLine($"{fs},{res},{f:G17},{b.SlowSeconds * f:G17},{b.FastSeconds * f:G17},{Db(hannLeak):G17},{Db(leak):G17},{b.SilenceSamples / (double)fs:G17}");
            }
        }
        Transfer(directory);
        Scenarios(root, directory);
        Console.WriteLine("Quality CSVs saved to " + directory);
    }

    static void Transfer(string directory)
    {
        const int fs = 48000;
        using var response = new StreamWriter(Path.Combine(directory, "transfer.csv"));
        using var metrics = new StreamWriter(Path.Combine(directory, "response.csv"));
        response.WriteLine("frequency,backend,offset_semitones,relative_db");
        metrics.WriteLine("frequency,backend,slow_cycles,fast_cycles,mean_age_ms,attack10_ms,attack50_ms,attack90_ms,release10_ms,release50_ms,release90_ms,enbw_hz,center_gain,image_db,real_min,real_max,half_note_gain");
        foreach (var f in new[] { 30.0, 55, 110, 440, 1000, 7000, 20000 })
        foreach (var name in new[] { "Hann SIMD", "SWIFT SIMD", "SWIFT 5-3" })
        {
            var n = (int)Math.Round(17 * fs / f);
            var actual = 17.0 * fs / n;
            var band = name == "SWIFT 5-3" ? SwiftTuning.FromCycles(f, fs) : SwiftTuning.FromBandwidth(f, 12, fs);
            Complex H(double tone) => name == "Hann SIMD" ? SwiftTuning.HannResponse(tone - actual, fs, n)
                : SwiftTuning.Response(tone - f, fs, band.SlowSeconds, band.FastSeconds);
            var gain = H(f).Magnitude;
            for (int j = -800; j <= 800; ++j)
            {
                double offset = j / 100.0;
                if (f * Math.Pow(2, offset / 12) > fs / 2.0) continue;
                response.WriteLine($"{f},{name},{offset},{Db(H(f * Math.Pow(2, offset / 12)).Magnitude / gain):G17}");
            }
            int count = name == "Hann SIMD" ? n : (int)Math.Ceiling(36 * band.SlowSeconds * fs) + 3;
            var full = H(f);
            Complex step = Complex.Zero;
            var attack = new double[3]; var release = new double[3];
            Array.Fill(attack, double.NaN); Array.Fill(release, double.NaN);
            double area = 0, moment = 0, squares = 0;
            var a = Math.Exp(-1 / (fs * band.SlowSeconds));
            var b = Math.Exp(-1 / (fs * band.FastSeconds));
            double ca = -Em1(-1 / (fs * band.SlowSeconds)), cb = -Em1(-1 / (fs * band.FastSeconds));
            for (var k = 0; k < count; ++k)
            {
                double w;
                if (name == "Hann SIMD") w = (1 - Math.Cos(2 * Math.PI * k / n)) / n;
                else if (band.SlowSeconds == 0) w = k == 1 ? 1 : 0;
                else if (a == b) w = k == 0 ? 0 : ca * cb * k * Math.Pow(a, k - 1);
                else w = ca * cb * (Math.Pow(a, k) - Math.Pow(b, k)) / (a - b);
                area += w; moment += k * w; squares += w * w;
                step += w * Complex.FromPolarCoordinates(1, 2 * Math.PI * (actual - f) * k / fs * (name == "Hann SIMD" ? 1 : 0));
                for (var j = 0; j < 3; ++j)
                {
                    var p = new[] { .1, .5, .9 }[j];
                    if (double.IsNaN(attack[j]) && step.Magnitude >= p * gain) attack[j] = k * 1000.0 / fs;
                    if (double.IsNaN(release[j]) && (full - step).Magnitude <= (1 - p) * gain) release[j] = k * 1000.0 / fs;
                }
            }
            var image = H(-f).Magnitude;
            metrics.WriteLine($"{f},{name},{(name == "Hann SIMD" ? 0 : band.SlowSeconds * f):G17},{(name == "Hann SIMD" ? 0 : band.FastSeconds * f):G17},"
                + $"{moment / area / fs * 1000:G17},{attack[0]:G17},{attack[1]:G17},{attack[2]:G17},{release[0]:G17},{release[1]:G17},{release[2]:G17},"
                + $"{fs * squares / (gain * gain):G17},{gain:G17},{Db(image / gain):G17},{Math.Abs(gain - image):G17},{gain + image:G17},{H(f * Math.Pow(2, .5 / 12)).Magnitude:G17}");
        }
    }

    static double Em1(double x) => Math.Abs(x) < 1e-4
        ? 2 * Math.Exp(x / 2) * Math.Sinh(x / 2) : Math.Exp(x) - 1;

    static void Scenarios(string root, string directory)
    {
        const int fs = 48000;
        var options = new AnalyzerOptions { InputGainDb = 0 };
        var domain = options.Domain;
        var frequencies = domain.ToArray();
        int center = Array.FindIndex(frequencies, f => f >= 55);
        var f0 = frequencies[center];
        using var trace = new StreamWriter(Path.Combine(directory, "traces.csv"));
        using var summary = new StreamWriter(Path.Combine(directory, "scenarios.csv"));
        trace.WriteLine("scenario,backend,time_s,center_amplitude,neighbor_amplitude,local_s,local_t,local_b,local_r");
        summary.WriteLine("scenario,backend,sample_rate,channels,seconds,peak_t,time_peak_t,peak_r,time_peak_r,mean_s,mean_b,mean_r");
        foreach (var scenario in new[] { "Tone attack", "Tone plus noise", "Neighbors equal", "Neighbor louder", "Note replacement", "Synchronous attacks" })
        {
            var random = new Random(417);
            var samples = new double[3 * fs];
            for (var i = 0; i < samples.Length; ++i)
            {
                var t = i / (double)fs;
                var first = t >= .3 && t < 1.25;
                var second = t >= .65 && t < 1.6;
                double value = first ? .3 * Math.Cos(2 * Math.PI * f0 * t + .37) : 0;
                if (scenario == "Tone plus noise" && t >= .65 && t < .73) value += .3 * (2 * random.NextDouble() - 1);
                if (scenario is "Neighbors equal" or "Neighbor louder" && second)
                    value += (scenario == "Neighbor louder" ? 3 : .3) * Math.Cos(2 * Math.PI * frequencies[center + 1] * t + 1.1);
                if (scenario == "Note replacement" && t >= .8 && first)
                    value = .3 * Math.Cos(2 * Math.PI * frequencies[center + 1] * t + 1.1);
                if (scenario == "Synchronous attacks")
                {
                    value = 0;
                    if (first) for (var k = 0; k < frequencies.Length; k += 6)
                        value += .06 * Math.Cos(2 * Math.PI * frequencies[k] * t + .73 * k);
                }
                samples[i] = value;
            }
            Analyze(scenario, new[] { samples }, fs, options, domain, center, trace, summary);
        }
        var music = ReadWave(Path.Combine(root, "TestFiles", "test_input.wav"), 12);
        Analyze("Music WAV", music.Channels, music.SampleRate, options, domain, center, trace, summary);
    }

    static void Analyze(string scenario, double[][] samples, int fs, AnalyzerOptions options, Domain domain,
        int center, StreamWriter trace, StreamWriter summary)
    {
        int block = fs / options.TimeResolution;
        double rate = fs / (double)block;
        foreach (var name in new[] { "Hann SIMD", "SWIFT SIMD", "SWIFT 5-3" })
        {
            var banks = samples.Select(_ => Create(name, domain, fs)).ToArray();
            var splits = samples.Select(_ => new Splitter(domain, options, rate)).ToArray();
            var magnitudes = new double[samples.Length * domain.Length];
            var output = new double[domain.Length * Splitter.OutputStride];
            double peakT = 0, timeT = 0, peakR = 0, timeR = 0, sumS = 0, sumB = 0, sumR = 0;
            int rows = 0;
            for (var start = 0; start + block <= samples[0].Length; start += block)
            {
                for (int c = 0; c < samples.Length; ++c)
                {
                    banks[c].Process(samples[c].AsMemory(start, block));
                    banks[c].OutputLinear(magnitudes.AsMemory(c * domain.Length, domain.Length));
                    splits[c].ProcessLinear(magnitudes.AsMemory(c * domain.Length, domain.Length), output);
                }
                var local = splits[0].Estimates.Span[center];
                var t = (start + block) / (double)fs;
                if (local.Transient > peakT) { peakT = local.Transient; timeT = t; }
                if (local.ResidualRms > peakR) { peakR = local.ResidualRms; timeR = t; }
                sumS += local.Sine; sumB += local.Background; sumR += local.ResidualRms; rows++;
                if (rows % 5 == 0)
                {
                    trace.WriteLine($"{scenario},{name},{t:G17},{magnitudes[center]:G17},{magnitudes[center + 1]:G17},"
                        + $"{local.Sine:G17},{local.Transient:G17},{local.Background:G17},{local.ResidualRms:G17}");
                }
            }
            summary.WriteLine($"{scenario},{name},{fs},{samples.Length},{samples[0].Length / (double)fs:G17},{peakT:G17},{timeT:G17},{peakR:G17},{timeR:G17},{sumS / rows:G17},{sumB / rows:G17},{sumR / rows:G17}");
            Console.WriteLine($"Quality: {scenario}, {name}");
        }
    }

    static (int SampleRate, double[][] Channels) ReadWave(string path, int seconds)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        string Four() => new(reader.ReadChars(4));
        if (Four() != "RIFF") throw new InvalidDataException("Expected RIFF WAV.");
        reader.ReadUInt32(); if (Four() != "WAVE") throw new InvalidDataException("Expected WAVE.");
        int format = 0, channels = 0, fs = 0, bits = 0; byte[] data = Array.Empty<byte>();
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunk = Four(); int size = reader.ReadInt32(); var next = reader.BaseStream.Position + size + (size & 1);
            if (chunk == "fmt ")
            {
                format = reader.ReadUInt16(); channels = reader.ReadUInt16(); fs = reader.ReadInt32();
                reader.ReadInt32(); reader.ReadUInt16(); bits = reader.ReadUInt16();
                if (format == 0xfffe) { reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt32(); format = reader.ReadUInt16(); }
            }
            else if (chunk == "data") data = reader.ReadBytes(Math.Min(size, checked(seconds * fs * channels * bits / 8)));
            reader.BaseStream.Position = next;
        }
        if (channels <= 0 || fs <= 0 || data.Length == 0) throw new InvalidDataException("Missing WAV format/data.");
        int bytes = bits / 8, count = data.Length / (channels * bytes);
        var result = Enumerable.Range(0, channels).Select(_ => new double[count]).ToArray();
        for (int i = 0; i < count; ++i)
        for (int c = 0; c < channels; ++c)
        {
            int p = (i * channels + c) * bytes;
            result[c][i] = (format, bits) switch
            {
                (1, 16) => BitConverter.ToInt16(data, p) / 32768.0,
                (1, 24) => ((data[p] << 8 | data[p + 1] << 16 | data[p + 2] << 24) >> 8) / 8388608.0,
                (1, 32) => BitConverter.ToInt32(data, p) / 2147483648.0,
                (3, 32) => BitConverter.ToSingle(data, p),
                (3, 64) => BitConverter.ToDouble(data, p),
                _ => throw new NotSupportedException($"WAV format {format}, {bits} bits"),
            };
        }
        return (fs, result);
    }
}
