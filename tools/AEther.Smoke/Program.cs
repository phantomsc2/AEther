using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AEther;
using AEther.WindowsForms;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
            var output = Path.Combine(root, "tools", "AEther.Smoke", "artifacts");
            Directory.CreateDirectory(output);
            using var form = new Form { ClientSize = new System.Drawing.Size(640, 360), ShowInTaskbar = false };
            var graphics = new Graphics(form.Handle, Path.Combine(root, "AEther.WindowsForms", "data", "fx"));
            try
            {
                using var states = new ListBox();
                for (var restart = 0; restart < 2; ++restart)
                {
                    using var module = new GraphicsModule(graphics, states, 2, 64, 128);
                    var spectrum = new double[2 * 4 * 64];
                    for (var i = 0; i < spectrum.Length; ++i)
                        spectrum[i] = .2 + .1 * Math.Sin(i * .1);
                    module.Process(new SampleEvent<double>(spectrum, 4 * 64, DateTime.Now));
                    module.ProcessTransient(.75);
                    for (var state = 0; state < states.Items.Count; ++state)
                    {
                        states.SelectedIndex = state;
                        // Initialize simulation textures and capture before Present discards the backbuffer.
                        module.Render();
                        ((GraphicsState)states.SelectedItem!).Render();
                        graphics.SaveFrame(Path.Combine(output, $"{states.SelectedItem}-{restart}.png"));
                        graphics.ThrowIfDebugErrors();
                        Console.WriteLine($"Rendered {states.SelectedItem}, session {restart + 1}");
                        if (states.SelectedItem is SpectrumState spectrumState && restart == 0)
                        {
                            spectrumState.ProcessKeyPress(new KeyPressEventArgs('i'));
                            spectrumState.Render();
                            graphics.SaveFrame(Path.Combine(output, "spectrum-intensity.png"));
                            graphics.ThrowIfDebugErrors();
                            spectrumState.ProcessKeyPress(new KeyPressEventArgs('i'));
                        }
                        if (states.SelectedItem is StormState storm && restart == 0)
                        {
                            var features = new AudioFeatures(.13, .09, .07, .035, .1);
                            for (var frame = 0; frame < 300; ++frame)
                                storm.Response.Advance(1.0 / 60, features, 0);
                            storm.Render();
                            graphics.SaveFrame(Path.Combine(output, "storm-calm.png"));
                            storm.Render();
                            graphics.SaveFrame(Path.Combine(output, "storm-stable.png"));
                            var first = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "storm-calm.png")));
                            var second = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "storm-stable.png")));
                            if (!first.AsSpan().SequenceEqual(second))
                                throw new InvalidOperationException("Static storm input produced a changing image.");
                            storm.Response.Advance(0, features, .2);
                            storm.Render();
                            graphics.SaveFrame(Path.Combine(output, "storm-thunder.png"));
                            storm.Response.Advance(.75, default, 0);
                            storm.Render();
                            graphics.SaveFrame(Path.Combine(output, "storm-afterglow.png"));
                            var timer = System.Diagnostics.Stopwatch.StartNew();
                            for (var frame = 0; frame < 30; ++frame)
                            {
                                storm.Response.Advance(1.0 / 60, features, frame % 15 == 0 ? .12 : 0);
                                storm.Render();
                                graphics.Present();
                            }
                            // Readback drains queued GPU work; amortize its overhead across the frame run.
                            graphics.SaveFrame(Path.Combine(output, "storm-moving.png"));
                            timer.Stop();
                            Console.WriteLine($"Storm 30-frame run including present and final readback: {timer.Elapsed.TotalMilliseconds / 30:F1} ms/frame");
                            storm.BackdropOnly = true;
                            storm.Response.CameraMovement = 0;
                            for (var frame = 0; frame < 1800; ++frame)
                                storm.Response.Advance(1.0 / 60, default, 0);
                            storm.Render();
                            graphics.SaveFrame(Path.Combine(output, "backdrop-fixed.png"));
                            storm.Response.Advance(5, default, 0); // cloud time changes, spherical wallpaper must not
                            storm.Render();
                            graphics.SaveFrame(Path.Combine(output, "backdrop-later.png"));
                            var backdropA = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "backdrop-fixed.png")));
                            var backdropB = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "backdrop-later.png")));
                            if (!backdropA.AsSpan().SequenceEqual(backdropB))
                                throw new InvalidOperationException("The fixed wallpaper evolved with cloud time.");
                            storm.Response.Advance(0, default, .2);
                            storm.Render();
                            graphics.SaveFrame(Path.Combine(output, "backdrop-flash.png"));
                            var backdropFlash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "backdrop-flash.png")));
                            if (backdropA.AsSpan().SequenceEqual(backdropFlash))
                                throw new InvalidOperationException("The backdrop did not respond to a transient.");
                            storm.BackdropOnly = false;
                            graphics.ThrowIfDebugErrors();
                        }
                    }
                    graphics.ValidateShaders();
                }
                CaptureHistoryExperiments(graphics, output);
                CaptureSilenceExpiry(graphics, output);
            }
            finally { graphics.Dispose(); }
            Console.WriteLine("Renderer construction, shader variants, all states and session recreation passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    static void CaptureHistoryExperiments(Graphics graphics, string output)
    {
        const int width = 64, height = 256;
        using var left = new FloatSpectrum(graphics, width);
        using var right = new FloatSpectrum(graphics, width);
        using var leftHistory = new FloatHistogram(graphics, width, height, true);
        using var rightHistory = new FloatHistogram(graphics, width, height, true);
        using var otherOrderHistory = new FloatHistogram(graphics, width, height, true);
        var row = new double[4 * width];
        const int sampleRate = 48000, batch = 187;
        var domain = new Domain(27.5, 8, width);
        var frequencies = domain.ToArray();
        var dft = new DFTProcessor(domain, sampleRate, true, 0);
        var splitter = new AEther.Splitter(domain, new AnalyzerOptions(), sampleRate / (double)batch);
        var otherOrder = new AEther.Splitter(domain,
            new AnalyzerOptions { SplitOrder = SplitterOrder.FrequencyThenTime }, sampleRate / (double)batch);
        var otherRow = new double[row.Length];
        var samples = new double[batch];
        var spectrum = new double[width];
        var random = new Random(491);
        var analysisTimer = System.Diagnostics.Stopwatch.StartNew();
        for (var time = 0; time < height; ++time)
        {
            for (var i = 0; i < batch; ++i)
            {
                var seconds = (time * batch + i) / (double)sampleRate;
                var tone = .13 * Math.Sin(2 * Math.PI * frequencies[14] * seconds)
                    + .11 * Math.Sin(2 * Math.PI * frequencies[time < height / 2 ? 33 : 41] * seconds)
                    + .09 * Math.Sin(2 * Math.PI * frequencies[time < 3 * height / 4 ? 49 : 53] * seconds);
                var pulse = time % 128 < 5 ? .24 * (random.NextDouble() * 2 - 1) : 0;
                samples[i] = tone + pulse;
            }
            dft.Process(samples);
            dft.Output(spectrum);
            splitter.Process(spectrum, row);
            otherOrder.Process(spectrum, otherRow);
            leftHistory.Add(row);
            rightHistory.Add(row);
            otherOrderHistory.Add(otherRow);
        }
        analysisTimer.Stop();
        Console.WriteLine($"DFT + splitter: {height * batch / (double)sampleRate:F2} seconds of synthesized chords and attacks analyzed in {analysisTimer.Elapsed.TotalSeconds:F2} seconds.");
        leftHistory.Update();
        rightHistory.Update();
        otherOrderHistory.Update();
        left.Add(row); left.Update();
        right.Add(row); right.Update();
        using var frame = graphics.CreateConstantBuffer<FrameConstants>();
        frame.Value = new()
        {
            AspectRatio = 640f / 360, HistogramShift = (height - .5f) / height,
            HistorySpan = 1, PoseX = .18f, PoseY = .24f, PoseZ = -.08f, S = .7f,
        };
        using var hulls = graphics.LoadShader("mandelbox-slice.fx");
        using var tunnel = graphics.LoadShader("history-tunnel.fx");
        using var histogram = graphics.LoadShader("histogram.fx");
        hulls.ConstantBuffers["FrameConstants"].SetConstantBuffer(frame.Buffer);
        tunnel.ConstantBuffers["FrameConstants"].SetConstantBuffer(frame.Buffer);
        histogram.ConstantBuffers["FrameConstants"].SetConstantBuffer(frame.Buffer);
        hulls.ShaderResources["Spectrum0"].SetResource(left.Texture.SRView);
        hulls.ShaderResources["Spectrum1"].SetResource(right.Texture.SRView);
        foreach (var shader in new[] { hulls, tunnel, histogram })
        {
            shader.ShaderResources["Histogram0"].SetResource(leftHistory.Texture.SRView);
            shader.ShaderResources["Histogram1"].SetResource(rightHistory.Texture.SRView);
        }
        byte[] Capture(Shader shader, string name)
        {
            frame.Update();
            graphics.SetModel();
            graphics.SetRenderTarget(null, graphics.BackBuffer);
            graphics.Draw(shader);
            var path = Path.Combine(output, name + ".png");
            graphics.SaveFrame(path);
            graphics.ThrowIfDebugErrors();
            return System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
        }
        var original = Capture(hulls, "fractal-history");
        Capture(histogram, "splitter-histogram");
        histogram.ShaderResources["Histogram1"].SetResource(otherOrderHistory.Texture.SRView);
        Capture(histogram, "splitter-orders-time-left-frequency-right");
        histogram.ShaderResources["Histogram1"].SetResource(rightHistory.Texture.SRView);
        histogram.Variables["CombinedLevelView"].AsScalar().Set(true);
        Capture(histogram, "splitter-retained-level");
        hulls.Variables["HistoryWarp"].AsScalar().Set(0);
        var circular = Capture(hulls, "fractal-screen-history");
        hulls.Variables["HistoryWarp"].AsScalar().Set(1);
        var folded = Capture(hulls, "fractal-fold-history");
        if (original.AsSpan().SequenceEqual(circular) || original.AsSpan().SequenceEqual(folded)
            || circular.AsSpan().SequenceEqual(folded))
            throw new InvalidOperationException("Screen/fractal history mapping did not change the rendered propagation.");
        hulls.Variables["HistoryWarp"].AsScalar().Set(.7f);
        hulls.Variables["TimeCompression"].AsScalar().Set(8);
        var compressed = Capture(hulls, "fractal-time-compressed");
        hulls.Variables["TimeCompression"].AsScalar().Set(2);
        var extended = Capture(hulls, "fractal-time-extended");
        if (original.AsSpan().SequenceEqual(compressed) || original.AsSpan().SequenceEqual(extended)
            || compressed.AsSpan().SequenceEqual(extended))
            throw new InvalidOperationException("Fractal time compression had no visible effect.");
        hulls.Variables["TimeCompression"].AsScalar().Set(4);
        frame.Value.T = 1000;
        var laterWallTime = Capture(hulls, "fractal-wall-time");
        if (!original.AsSpan().SequenceEqual(laterWallTime))
            throw new InvalidOperationException("Fractal texture drifted with wall time rather than musical time.");
        frame.Value.S = 1.3f;
        var laterMusic = Capture(hulls, "fractal-musical-time");
        if (original.AsSpan().SequenceEqual(laterMusic))
            throw new InvalidOperationException("Musical time did not evolve the fractal slice.");
        frame.Value.S = .7f;
        frame.Value.VisualFlash = 1;
        var thunder = Capture(hulls, "fractal-thunder");
        if (original.AsSpan().SequenceEqual(thunder))
            throw new InvalidOperationException("Transient did not ignite the fractal.");
        frame.Value.VisualFlash = 0;
        Capture(tunnel, "tunnel-history");
        frame.Value.HistogramShift += .04f;
        Capture(tunnel, "tunnel-pulse");
        var laterHistory = Capture(hulls, "fractal-history-later");
        if (original.AsSpan().SequenceEqual(laterHistory))
            throw new InvalidOperationException("Recorded history did not propagate through the fractal.");
        Console.WriteLine("History experiments: wall-time stability, musical-time slice evolution, transient illumination and history propagation passed.");
    }

    static void CaptureSilenceExpiry(Graphics graphics, string output)
    {
        using var states = new ListBox();
        using var module = new GraphicsModule(graphics, states, 2, 64, 16, 256, 0);
        var row = new double[2 * 4 * 64];
        for (var i = 0; i < row.Length; i += 4)
        {
            row[i] = .6;
            row[i + 2] = .3;
        }
        module.Process(new SampleEvent<double>(row, 4 * 64, DateTime.Now));
        states.SelectedItem = states.Items.OfType<HistogramState>().First();
        module.Render();
        ((GraphicsState)states.SelectedItem!).Render();
        var before = Path.Combine(output, "histogram-before-silence.png");
        graphics.SaveFrame(before);
        System.Threading.Thread.Sleep(300);
        module.Render();
        ((GraphicsState)states.SelectedItem!).Render();
        var after = Path.Combine(output, "histogram-after-silence.png");
        graphics.SaveFrame(after);
        if (System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(before)).AsSpan()
            .SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(after))))
            throw new InvalidOperationException("History stopped moving when capture produced no packets.");
        graphics.ThrowIfDebugErrors();
        Console.WriteLine("Silent capture expiry: recorded light moved out of the history window.");
    }
}
