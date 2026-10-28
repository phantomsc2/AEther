using System.Runtime.InteropServices;
namespace AEther.LoopbackBackendProbe;
internal sealed class BassCapture : ICapture
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate uint WasapiProc(IntPtr data,uint bytes,IntPtr user);
    [StructLayout(LayoutKind.Sequential)]
    struct DeviceInfo{public IntPtr Name,Id;public uint Type,Flags;public float MinPeriod,DefaultPeriod;public uint MixFrequency,MixChannels;}
    [StructLayout(LayoutKind.Sequential)]
    struct Info{public uint Flags,Frequency,Channels,Format,BufferLength;public float VolumeMax,VolumeMin,VolumeStep;}
    [DllImport("basswasapi",CallingConvention=CallingConvention.Winapi)]static extern uint BASS_WASAPI_GetVersion();
    [DllImport("basswasapi",CallingConvention=CallingConvention.Winapi)]static extern bool BASS_WASAPI_GetDeviceInfo(uint index,out DeviceInfo info);
    [DllImport("basswasapi",CallingConvention=CallingConvention.Winapi)]static extern bool BASS_WASAPI_Init(int device,uint rate,uint channels,uint flags,float buffer,float period,WasapiProc proc,IntPtr user);
    [DllImport("basswasapi",CallingConvention=CallingConvention.Winapi)]static extern bool BASS_WASAPI_GetInfo(out Info info);
    [DllImport("basswasapi",CallingConvention=CallingConvention.Winapi)]static extern bool BASS_WASAPI_Start();
    [DllImport("basswasapi",CallingConvention=CallingConvention.Winapi)]static extern bool BASS_WASAPI_Stop(bool reset);
    [DllImport("basswasapi",CallingConvention=CallingConvention.Winapi)]static extern bool BASS_WASAPI_Free();
    [DllImport("bass",CallingConvention=CallingConvention.Winapi)]static extern int BASS_ErrorGetCode();
    [DllImport("bass",CallingConvention=CallingConvention.Winapi)]static extern uint BASS_GetVersion();
    readonly WasapiProc callback;
    readonly Info info;
    readonly uint version,bassVersion;
    bool disposed;
    public BassCapture(string endpointId,CaptureMeasurements measurements,string variant)
    {
        // BASSWASAPI is a native library; the wrapper changes no buffering. Keep binaries local to this probe.
        var native=Path.GetFullPath(Path.Combine("artifacts","capture-backends","native"));
        NativeLibrary.Load(Path.Combine(native,"bass24","x64","bass.dll"));
        NativeLibrary.Load(Path.Combine(native,"basswasapi24","x64","basswasapi.dll"));
        NativeLibrary.SetDllImportResolver(typeof(BassCapture).Assembly,(name,assembly,path)=>name switch {
            "bass"=>NativeLibrary.Load(Path.Combine(native,"bass24","x64","bass.dll")),
            "basswasapi"=>NativeLibrary.Load(Path.Combine(native,"basswasapi24","x64","basswasapi.dll")),
            _=>IntPtr.Zero
        });
        version=BASS_WASAPI_GetVersion(); bassVersion=BASS_GetVersion();
        int index=-1;
        for(uint i=0;BASS_WASAPI_GetDeviceInfo(i,out var d);i++)
        {
            string? id=Marshal.PtrToStringUTF8(d.Id);
            if((d.Flags&8)!=0 && id==endpointId){index=(int)i;break;}
        }
        if(index<0)throw new Exception("BASS loopback endpoint matching the default render endpoint was not found");
        int channels=2;
        callback=(data,bytes,user)=>{measurements.Add((int)bytes/(4*channels));return 1;};
        uint flags=variant=="event"?16u:0u;
        float buffer=variant=="request2"?0:.001f;
        float period=variant=="request2"?.002f:.001f;
        if(!BASS_WASAPI_Init(index,0,0,flags,buffer,period,callback,IntPtr.Zero))throw new Exception($"BASS_WASAPI_Init error {BASS_ErrorGetCode()}");
        if(!BASS_WASAPI_GetInfo(out info)){BASS_WASAPI_Free();throw new Exception("BASS_WASAPI_GetInfo failed");}
        channels=(int)info.Channels;
    }
    public int SampleRate=>(int)info.Frequency;
    public string Details=>$"BASSWASAPI 0x{version:x8}, BASS 0x{bassVersion:x8}; float {info.Frequency} Hz {info.Channels} ch; actual buffer {info.BufferLength} bytes ({info.BufferLength*1000.0/(4*info.Channels*info.Frequency):F4} ms); flags=0x{info.Flags:x}";
    public void Start(){if(!BASS_WASAPI_Start())throw new Exception($"BASS_WASAPI_Start error {BASS_ErrorGetCode()}");}
    public void Stop(){if(!disposed)BASS_WASAPI_Stop(true);}
    public void Dispose(){if(disposed)return;Stop();BASS_WASAPI_Free();disposed=true;GC.KeepAlive(callback);}
}
