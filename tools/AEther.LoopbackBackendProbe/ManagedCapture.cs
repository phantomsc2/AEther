using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace AEther.LoopbackBackendProbe;
internal sealed class NAudioCapture : ICapture
{
    readonly WasapiRecorder recorder;
    public NAudioCapture(MMDevice endpoint,CaptureMeasurements measurements)
    {
        recorder=new WasapiRecorderBuilder().WithDevice(endpoint).WithLoopbackCapture().WithBufferLength(1).WithEventSync().WithMmcssThreadPriority("Pro Audio").Build();
        recorder.DataAvailable+=(data,flags,position,qpc)=>measurements.Add(data.Length/recorder.WaveFormat.BlockAlign);
    }
    public int SampleRate=>recorder.WaveFormat.SampleRate;
    public string Details=>"NAudio 3.4.0-preview.1, event-driven loopback, requested buffer 1 ms, one callback per native packet";
    public void Start()=>recorder.StartRecording();
    public void Stop()=>recorder.StopRecording();
    public void Dispose()=>recorder.Dispose();
}
internal sealed class CSCoreCapture : ICapture
{
    readonly CSCore.SoundIn.WasapiCapture capture;
    readonly string variant;
    public CSCoreCapture(string endpointId,CaptureMeasurements measurements,string variant)
    {
        // The stock loopback class polls; also test the generic capture implementation with loopback + event flags.
        this.variant=variant;
        capture=variant=="event"?new EventLoopback():new CSCore.SoundIn.WasapiLoopbackCapture(variant=="zero"?0:1,null,ThreadPriority.Highest);
        using var enumerator=new CSCore.CoreAudioAPI.MMDeviceEnumerator();
        capture.Device=enumerator.GetDevice(endpointId);
        capture.Initialize();
        capture.DataAvailable+=(_,e)=>measurements.Add(e.ByteCount/capture.WaveFormat.BlockAlign);
    }
    public int SampleRate=>capture.WaveFormat.SampleRate;
    public string Details=>$"CSCore 1.2.1.2 loopback variant={variant}; {capture.WaveFormat}";
    public void Start()=>capture.Start();
    public void Stop()=>capture.Stop();
    public void Dispose()=>capture.Dispose();
}

internal sealed class EventLoopback : CSCore.SoundIn.WasapiCapture
{
    public EventLoopback() : base(true,CSCore.CoreAudioAPI.AudioClientShareMode.Shared,1,null,ThreadPriority.Highest) { }
    protected override CSCore.CoreAudioAPI.AudioClientStreamFlags GetStreamFlags()=>base.GetStreamFlags()|CSCore.CoreAudioAPI.AudioClientStreamFlags.StreamFlagsLoopback;
}