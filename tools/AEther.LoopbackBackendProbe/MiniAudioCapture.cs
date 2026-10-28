using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Hexa.NET.MiniAudio;
namespace AEther.LoopbackBackendProbe;
internal unsafe sealed class MiniAudioCapture : ICapture
{
    MaDevice* device;
    GCHandle measurementsHandle;
    string internalConfiguration = "";
    readonly string requestedConfiguration;
    public MiniAudioCapture(CaptureMeasurements measurements,string variant,int sampleRate,int channels)
    {
        requestedConfiguration=$"requested {sampleRate} Hz, {channels} channels, " + (variant=="period2"?"96 frames / 2 periods":"period 1 ms");
        measurementsHandle=GCHandle.Alloc(measurements);
        device=(MaDevice*)NativeMemory.AllocZeroed((nuint)sizeof(MaDevice));
        var config=MiniAudio.DeviceConfigInit(MaDeviceType.Loopback);
        config.SampleRate=(uint)sampleRate;
        config.Capture.Channels=(uint)channels;
        config.Capture.Format=MaFormat.F32;
        config.PeriodSizeInMilliseconds=1;
        if(variant=="period2") {config.PeriodSizeInMilliseconds=0;config.PeriodSizeInFrames=96;config.Periods=2;}
        config.PerformanceProfile=MaPerformanceProfile.LowLatency;
        config.NoFixedSizedCallback=1;
        config.Wasapi.NoAutoConvertSRC=variant=="no-src"?(byte)1:(byte)0;
        config.PUserData=(void*)GCHandle.ToIntPtr(measurementsHandle);
        config.DataCallback=(void*)(delegate* unmanaged[Cdecl]<MaDevice*,void*,void*,uint,void>)&OnData;
        MaBackend backend=MaBackend.Wasapi;
        var result=MiniAudio.DeviceInitEx(&backend,1,default(MaContextConfigPtr),in config,new MaDevicePtr(device));
        if((int)result==0) internalConfiguration=$"native internal capture period={device->Capture.InternalPeriodSizeInFrames} frames, periods={device->Capture.InternalPeriods}, noFixedSizedCallback={device->NoFixedSizedCallback}";
        if((int)result!=0){NativeMemory.Free(device);device=null;measurementsHandle.Free();throw new Exception($"miniaudio DeviceInitEx: {result}");}
    }
    [UnmanagedCallersOnly(CallConvs=new[]{typeof(CallConvCdecl)})]
    static void OnData(MaDevice* device,void* output,void* input,uint frames)
    {
        ((CaptureMeasurements)GCHandle.FromIntPtr((IntPtr)device->PUserData).Target!).Add((int)frames);
    }
    public int SampleRate=>(int)device->SampleRate;
    public string Details=>"Hexa.NET.MiniAudio 1.0.1; WASAPI-only backend; " + requestedConfiguration + "; fixed-sized callback adaptation disabled; " + internalConfiguration;
    public void Start(){var r=MiniAudio.DeviceStart(new MaDevicePtr(device));if((int)r!=0)throw new Exception($"miniaudio DeviceStart: {r}");}
    public void Stop(){if(device!=null)MiniAudio.DeviceStop(new MaDevicePtr(device));}
    public void Dispose(){if(device==null)return;MiniAudio.DeviceUninit(new MaDevicePtr(device));NativeMemory.Free(device);device=null;measurementsHandle.Free();}
}
