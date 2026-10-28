using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;
using Vortice.Mathematics;
using Serilog;

namespace AEther.Vortice;

public sealed partial class Renderer
{
    /// <summary>Update spectrum presentation; retention changes clear history without restarting analysis or simulation.</summary>
    public void SetGraphicsOptions(GraphicsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        lock (graphicsGate)
        {
            if (Options == options) return;
            if (Options.HistorySeconds != options.HistorySeconds)
            {
                var configuration = Audio.PrepareHistory(options.HistorySeconds);
                if (configuration.Rows != Audio.Rows) ReplaceHistogramTextures(configuration.Rows);
                Audio.CommitHistory(configuration);
            }
            Audio.SpectrumAggregation = options.SpectrumAggregation;
            Options = options;
            UploadAudio(reset: true);
        }
    }

    void ReplaceHistogramTextures(int rows)
    {
        var nextTextures = new ID3D11Texture2D[2];
        var nextViews = new ID3D11ShaderResourceView[2];
        try
        {
            for (int channel = 0; channel < 2; channel++)
            {
                nextTextures[channel] = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)Audio.Bands, Height = (uint)rows, MipLevels = 1, ArraySize = 1,
                    Format = Format.R32G32B32A32_Float, SampleDescription = new(1, 0),
                    Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
                });
                nextTextures[channel].DebugName = $"Histogram{channel}";
                nextViews[channel] = device.CreateShaderResourceView(nextTextures[channel]);
            }
        }
        catch
        {
            for (int channel = 0; channel < 2; channel++)
            { nextViews[channel]?.Dispose(); nextTextures[channel]?.Dispose(); }
            throw;
        }
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.VSSetShaderResources(0, ShaderProgram.EmptyViews);
        for (int channel = 0; channel < 2; channel++)
        {
            int slot = channel + 2;
            resources.Remove(views[slot]); views[slot].Dispose();
            resources.Remove(textures[slot]); textures[slot].Dispose();
            textures[slot] = Own(nextTextures[channel]);
            views[slot] = Own(nextViews[channel]);
            namedTextures[$"Histogram{channel}"] = views[slot];
            namedTextures[$"FilteredHistogram{channel}"] = views[slot];
        }
    }

    /// <summary>
    /// Call after draining the old analyzer. Resets its row/history generation;
    /// rebuilds dimension-dependent textures only. Device, swap chain, compiled
    /// scene programs and their parameter values survive every contract change.
    /// </summary>
    public void RestartAudio(AnalysisOutput input, PipelineProfile? profile = null, ILogger? logger = null)
    {
        lock (graphicsGate)
        {
            var next = Audio.Restart(input);
            next.Profile = profile;
            using var restart = profile?.Measure("startup.gpu_audio_ms");
            if (next.Bands != Audio.Bands || next.Rows != Audio.Rows)
                ReplaceAudioTextures(next);
            Audio = next;
            Input = input;
            previousMusicalTime = 0;
            // Texture contents must be zeroed before any scene (including transport
            // preparation) can sample them. No old rows or queued packets survive.
            UploadAudio(reset: true);
            if (logger != null) Logger = logger.ForContext<Renderer>();
        }
    }

    void ReplaceAudioTextures(AudioHistory history)
    {
        var nextTextures = new ID3D11Texture2D[4];
        var nextViews = new ID3D11ShaderResourceView[4];
        try
        {
            for (int i = 0; i < nextTextures.Length; i++)
            {
                nextTextures[i] = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)history.Bands, Height = (uint)(i < 2 ? 2 : history.Rows),
                    MipLevels = 1, ArraySize = 1, Format = Format.R32G32B32A32_Float,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                });
                nextTextures[i].DebugName = i < 2 ? $"Spectrum{i}" : $"Histogram{i - 2}";
                nextViews[i] = device.CreateShaderResourceView(nextTextures[i]);
            }
        }
        catch
        {
            for (int i = 0; i < nextTextures.Length; i++)
            { nextViews[i]?.Dispose(); nextTextures[i]?.Dispose(); }
            throw;
        }
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.VSSetShaderResources(0, ShaderProgram.EmptyViews);
        for (int i = 0; i < textures.Length; i++)
        {
            if (views[i] != null) { resources.Remove(views[i]); views[i].Dispose(); }
            if (textures[i] != null) { resources.Remove(textures[i]); textures[i].Dispose(); }
            textures[i] = Own(nextTextures[i]); views[i] = Own(nextViews[i]);
        }
        namedTextures["Spectrum0"] = views[0]; namedTextures["Spectrum1"] = views[1];
        namedTextures["Histogram0"] = views[2]; namedTextures["Histogram1"] = views[3];
        namedTextures["FilteredHistogram0"] = views[2]; namedTextures["FilteredHistogram1"] = views[3];
    }

    // Internal reference path for correctness comparisons and upload measurements.
    internal bool FullHistoryUploads { get; set; }
    internal readonly record struct AudioUpload(int Rows, long Bytes, int Calls, double Milliseconds);
    // Counts cover both histogram textures; Rows is the number updated per channel.
    internal AudioUpload LastHistoryUpload { get; private set; }

    unsafe void UploadAudio(bool reset = false)
    {
        long start = Stopwatch.GetTimestamp();
        // Capture/analysis never touches a graphics lock. All uploads follow Advance.
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.VSSetShaderResources(0, ShaderProgram.EmptyViews);
        uint pitch = (uint)(Audio.Bands * 16);
        bool blocks = SceneName is "Spectrum" or "Debug";
        var spectrum = blocks ? Audio.BlockSpectrum : Audio.Spectrum;
        for (int channel = 0; channel < 2; channel++)
            fixed (float* data = spectrum[channel])
                context.UpdateSubresource(textures[channel], 0, new Box(0, 0, 0, Audio.Bands, 1, 1), (IntPtr)data, pitch, 0);


        if (blocks || reset)
            for (int channel = 0; channel < 2; channel++)
                fixed (float* data = Audio.BlockMomentaryResidual[channel])
                    context.UpdateSubresource(textures[channel], 0, new Box(0, 1, 0, Audio.Bands, 2, 1),
                        (IntPtr)data, pitch, 0);

        Span<AudioHistory.HistoryRange> ranges = stackalloc AudioHistory.HistoryRange[2];
        int count = Audio.GetHistoryUpdates(ranges);
        if (FullHistoryUploads) { ranges[0] = new(0, Audio.Rows); count = 1; }
        int rows = 0;
        for (int i = 0; i < count; i++)
        {
            var range = ranges[i];
            Box? box = FullHistoryUploads ? null
                : new Box(0, range.Start, 0, Audio.Bands, range.Start + range.Count, 1);
            for (int channel = 0; channel < 2; channel++)
                fixed (float* data = Audio.History[channel])
                    context.UpdateSubresource(textures[channel + 2], 0, box,
                        (IntPtr)(data + range.Start * Audio.Bands * 4), pitch, 0);
            rows += range.Count;
        }
        Audio.AcknowledgeHistoryUpload();
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        LastHistoryUpload = new(rows, (long)rows * pitch * 2, count * 2, milliseconds);
        Audio.Profile?.Record("render.audio_upload_ms", milliseconds);
    }

    // GPU readback is used only by validation, never by the live upload path.
    internal float[] ReadHistogram(int channel)
    {
        if ((uint)channel >= 2) throw new ArgumentOutOfRangeException(nameof(channel));
        lock (graphicsGate)
        {
            var texture = textures[channel + 2];
            var desc = texture.Description;
            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            using var staging = device.CreateTexture2D(desc);
            context.CopyResource(staging, texture);
            var map = context.Map(staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                var result = new float[Audio.Rows * Audio.Bands * 4];
                for (int row = 0; row < Audio.Rows; row++)
                    Marshal.Copy(map.DataPointer + row * (int)map.RowPitch, result,
                        row * Audio.Bands * 4, Audio.Bands * 4);
                return result;
            }
            finally { context.Unmap(staging, 0); }
        }
    }
}
