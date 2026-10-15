using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;
using AEther;
using AEther.Vortice;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
            var output = Path.Combine(root, "tools", "AEther.Smoke", "artifacts");
            Directory.CreateDirectory(output);
            var shaderDirectory = Path.Combine(output, "shader-reload");
            Directory.CreateDirectory(shaderDirectory);
            foreach (var file in Directory.GetFiles(Path.Combine(root, "AEther.Vortice", "Shaders"), "*.hlsl"))
                File.Copy(file, Path.Combine(shaderDirectory, Path.GetFileName(file)), true);
            using var form = new Form { ClientSize = new(640, 360), ShowInTaskbar = false };
            for (var restart = 0; restart < 2; restart++)
            {
                using var renderer = new Renderer(form.Handle, 640, 360, 64, 256, 1,
                    shaderDirectory);
                Seed(renderer);
                var hashes = new System.Collections.Generic.HashSet<string>();
                foreach (var scene in Enum.GetValues<Scene>())
                {
                    renderer.Scene = scene;
                    Seed(renderer);
                    renderer.Render(false);
                    var pixels = renderer.ReadPixels();
                    if (pixels.Where((_, i) => i % 4 != 3).Count(v => v > 10) < 100)
                        throw new InvalidOperationException($"{scene} rendered no visible audio.");
                    hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
                    Save(pixels, 640, 360, Path.Combine(output, $"vortice-{scene}-{restart}.png"));
                    renderer.Render();
                    Console.WriteLine($"Rendered {scene}, session {restart + 1}");
                }
                if (hashes.Count != 3) throw new InvalidOperationException("Scenes produced identical images.");
                renderer.Resize(320, 240);
                renderer.Render(false);
                if (renderer.ReadPixels().Length != 320 * 240 * 4) throw new Exception("Resize failed.");
                renderer.Resize(640, 360);
                if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
                var shaderPath = Path.Combine(shaderDirectory, "Mandelbox.hlsl");
                var workingShader = File.ReadAllText(shaderPath);
                File.WriteAllText(shaderPath, workingShader + "\nTHIS IS AN INVALID SHADER\n");
                if (renderer.ReloadShaders() || string.IsNullOrEmpty(renderer.ShaderError))
                    throw new Exception("Invalid shader was accepted.");
                renderer.Render(false); // Last working GPU shaders must still be usable.
                File.WriteAllText(shaderPath, workingShader);
                if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
                Seed(renderer);
                renderer.Scene = Scene.Histogram;
                renderer.Render(false);
                System.Threading.Thread.Sleep(1150);
                renderer.Render(false);
                var silent = renderer.ReadPixels();
                if (silent.Where((_, i) => i % 4 != 3).Any(v => v != 0))
                    throw new Exception("History failed to expire during silent capture.");
            }
            Console.WriteLine("Vortice: all scenes, resize, shader reload, silence expiry and session recreation passed.");
            CheckWindows();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void Seed(Renderer renderer)
    {
        var end = DateTime.Now;
        for (var row = 0; row < 256; row++)
        {
            var data = new double[512];
            for (var c = 0; c < 2; c++)
                for (var k = 0; k < 64; k++)
                {
                    var i = c * 256 + k * 4;
                    data[i] = .15 + .55 * Math.Pow(Math.Sin(k * .12 + c * 2), 8);
                    data[i + 1] = row % 32 < 3 ? .8 : 0;
                    data[i + 2] = .08;
                    data[i + 3] = .65;
                }
            renderer.PostAudio(new(data, 256, end - TimeSpan.FromSeconds((256 - row) / 256.0)), 2);
        }
    }

    static void Save(byte[] rgba, int width, int height, string path)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        for (var i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
        var map = bitmap.LockBits(new(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(rgba, 0, map.Scan0, rgba.Length); }
        finally { bitmap.UnlockBits(map); }
        bitmap.Save(path, ImageFormat.Png);
    }

    static void CheckWindows()
    {
        var started = 0;
        using var host = new AEther.WindowsForms.MainForm(() => new SyntheticSource(() => started++))
        { Opacity = 0, ShowInTaskbar = false };
        var settings = host.OwnedForms.OfType<AEther.WindowsForms.SettingsForm>().Single();
        settings.Opacity = 0;
        using var timer = new System.Windows.Forms.Timer { Interval = 200 };
        var stage = 0;
        Exception? failure = null;
        var bounds = Rectangle.Empty;
        var startedBeforeRestart = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        void Key(Form form, Keys key)
        {
            var method = form.GetType().GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            method.Invoke(form, new object[] { default(Message), key });
        }
        timer.Tick += (_, _) =>
        {
            try
            {
                if (DateTime.UtcNow > deadline) throw new Exception("Window lifecycle check timed out.");
                switch (stage)
                {
                    case 0:
                        if (started == 0) return;
                        Key(host, Keys.Space);
                        if (!settings.Visible || settings.Owner != host) throw new Exception("Settings ownership/show failed.");
                        settings.Close();
                        if (settings.IsDisposed || settings.Visible || host.IsDisposed) throw new Exception("Closing settings exited the app.");
                        Key(host, Keys.Space);
                        bounds = host.Bounds;
                        Key(settings, Keys.F11);
                        if (host.FormBorderStyle != FormBorderStyle.None) throw new Exception("Fullscreen entry failed.");
                        Key(settings, Keys.F11);
                        if (host.FormBorderStyle != FormBorderStyle.Sizable || host.Bounds != bounds) throw new Exception("Fullscreen restore failed.");
                        startedBeforeRestart = started;
                        var scenes = settings.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<ComboBox>()
                            .Single(c => c.Name == "Scene");
                        scenes.SelectedItem = Scene.Histogram;
                        stage++;
                        break;
                    case 1:
                        if (started <= startedBeforeRestart) return;
                        if (!host.Text.Contains("Histogram")) throw new Exception("Scene restart failed.");
                        Key(settings, Keys.Space);
                        if (settings.Visible) throw new Exception("Space did not hide settings.");
                        host.Close(); stage++;
                        break;
                }
            }
            catch (Exception error) { failure = error; timer.Stop(); host.Close(); }
        };
        timer.Start();
        Application.Run(host);
        if (failure != null) throw failure;
        if (stage != 2) throw new Exception("Window closed before lifecycle check finished.");
        Console.WriteLine("Window lifecycle: settings ownership, close/reopen, Space, F11, full session restart and shutdown passed.");
    }

    sealed class SyntheticSource(Action started) : SampleSource
    {
        public override SampleFormat Format { get; } = new(SampleType.Float32.Instance, 48000, 2);
        public override void Start()
        {
            started();
            DataAvailable?.Invoke(this, new byte[480 * 8]);
        }
        public override void Stop() => Stopped?.Invoke(this, null);
        public override void Dispose() { }
    }
}
