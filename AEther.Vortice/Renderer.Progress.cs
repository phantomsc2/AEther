using Vortice.Direct3D11;

namespace AEther.Vortice;

public sealed partial class Renderer
{
    sealed class CompletionMarker(ID3D11Query query)
    {
        public readonly ID3D11Query Query = query;
        public PipelineProgress? Owner;
        public SourceWatermark Watermark;
        public long Activation;
    }

    readonly List<CompletionMarker> completionMarkers = [];
    PipelineProgress? progress;
    bool progressCanvasActive;
    long progressActivation;

    public void AttachProgress(PipelineProgress value)
    {
        lock (graphicsGate)
        {
            progress?.SetCanvasActive(false);
            progress = value;
            Audio.Progress = value;
            progressCanvasActive = false;
            progressActivation++;
            if (completionMarkers.Count == 0)
                for (int i = 0; i < 8; i++)
                    completionMarkers.Add(new(Own(device.CreateQuery(new(QueryType.Event)))));
        }
    }

    public void SetCanvasProgressActive(bool active)
    {
        lock (graphicsGate)
        {
            if (progressCanvasActive == active) return;
            progressCanvasActive = active;
            progressActivation++;
            progress?.SetCanvasActive(active);
        }
    }

    void PollCanvasProgress()
    {
        foreach (var marker in completionMarkers)
        {
            if (marker.Owner == null || !context.GetData(marker.Query, AsyncGetDataFlags.DoNotFlush, out int complete) || complete == 0) continue;
            if (ReferenceEquals(marker.Owner, progress) && marker.Activation == progressActivation)
                marker.Owner.Completed(marker.Watermark);
            marker.Owner = null;
        }
    }

    void SubmitCanvasProgress()
    {
        if (progress == null || !progressCanvasActive || Audio.LatestProgress is not { } watermark) return;
        progress.Submitted(watermark);
        // No free marker means less precise observation, never a reason to wait or flush.
        var marker = completionMarkers.FirstOrDefault(item => item.Owner == null);
        if (marker == null) return;
        marker.Owner = progress;
        marker.Watermark = watermark;
        marker.Activation = progressActivation;
        context.End(marker.Query);
    }
}
