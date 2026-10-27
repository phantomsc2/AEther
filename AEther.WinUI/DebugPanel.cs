using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEther.WinUI;

public sealed class DebugPanel : UserControl
{
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    readonly TextBlock timing = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    public DebugPanel()
    {
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(status);
        content.Children.Add(timing);
        Content = content;
        SetTiming(null);
    }
    public void SetStatus(string text) => status.Text = text;

    public void SetTiming(Session? session)
    {
        if (session == null) { timing.Text = "No active audio session."; return; }
        var analyzer = session.Analyzer;
        var profile = analyzer.Profile;
        var format = analyzer.Format;
        var audio = session.Renderer.Audio;
        string Stat(string stage)
        {
            var value = profile.Recent(stage);
            return value is { } v ? $"{v.Mean:F2} / {v.P95:F2} ms" : "waiting";
        }
        var interval = profile.Recent("render.frame_interval_ms");
        var fps = interval is { Mean: > 0 } frame ? $"{1000 / frame.Mean:F1} FPS" : "waiting";
        var rate = format.SampleRate / (double)(format.SampleRate / analyzer.Options.TimeResolution);
        timing.Text = $"Mean / p95, last 256 measurements\n"
            + $"Audio: {format.SampleRate:N0} Hz, {format.ChannelCount} channels\n"
            + $"Analysis: {rate:F1} Hz ({1000 / rate:F2} ms/block)\n"
            + $"Startup DFT / smoother / splitter: {Stat("startup.dft_ms")} | {Stat("startup.smoother_ms")} | {Stat("startup.splitter_ms")}\n"
            + $"Startup GPU (CPU wall time): {(profile.Recent("startup.gpu_ms").HasValue ? Stat("startup.gpu_ms") : "retained")} | audio reset: {Stat("startup.gpu_audio_ms")}\n"
            + $"Capture to draw: {Stat("render.capture_to_submission_ms")}\n"
            + $"Capture queue: {Stat("capture.queue_ms")}\n"
            + $"Source to draw: {Stat("render.source_to_submission_ms")}\n"
            + $"Total audio age (estimated presentation): {Stat("render.total_audio_age_ms")}\n"
            + $"Audio age at frame start: {Stat("render.presented_audio_age_ms")}\n"
            + $"Timestamp quality: {analyzer.LastTimestampQuality}\n"
            + $"Analysis CPU: {Stat("analysis.total_ms")}\n"
            + $"Smoother CPU / channel (A+B): {Stat("analysis.smooth_channel_ms")}\n"
            + $"Draw CPU: {Stat("render.draw_cpu_ms")}\n"
            + $"Audio upload CPU: {Stat("render.audio_upload_ms")}\n"
            + $"GPU frame: {Stat("gpu.frame_ms")}\n"
            + $"GPU fluid: {Stat("gpu.fluid_ms")}\n"
            + $"GPU pressure: {Stat("gpu.fluid_pressure_ms")}\n"
            + $"GPU velocity / material: {Stat("gpu.fluid_velocity_ms")} | {Stat("gpu.fluid_material_ms")}\n"
            + $"GPU scene / output: {Stat("gpu.scene_ms")} | {Stat("gpu.output_ms")}\n"
            + $"GPU analysis view: {Stat("gpu.debug_ms")}\n"
            + $"Fluid work: {session.Renderer.FluidWork}\n"
            + $"GPU timing delivery: {Stat("gpu.timing_delivery_ms")}\n"
            + $"GPU timing samples: nonblocking; skipped {session.Renderer.SkippedGpuTimingFrames}, invalid {session.Renderer.InvalidGpuTimingFrames} (not texture/frame drops)\n"
            + $"Frame pacing wait: {Stat("render.pacing_wait_ms")}\n"
            + $"Frame clock: {(session.Renderer.LastPresentationTimestamp.HasValue ? "compositor estimate" : "host QPC")}\n"
            + $"Display interval: {Stat("render.display_interval_ms")}\n"
            + $"Present wait: {Stat("render.present_wait_ms")} | {fps}\n"
            + $"Output: {session.Renderer.OutputDescription}\n"
            + $"GPU: {session.Renderer.AdapterDescription}\n"
            + $"History delay: {audio.PresentationDelay * 1000:F0} ms | Drops: {analyzer.DroppedInputCount} input, {audio.DroppedRows} visual\n"
            + $"Lost audio: {analyzer.DroppedAudioMilliseconds:F1} ms | Discontinuities: {analyzer.DiscontinuityCount}\n"
            + "GPU stages are nested; do not sum them. Source ages end at CPU submission; speaker/display delay is not calibrated.";
    }
}
