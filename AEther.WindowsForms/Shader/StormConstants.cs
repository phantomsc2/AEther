using System.Runtime.InteropServices;

namespace AEther.WindowsForms
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct StormConstants
    {
        public float Energy, Bass, Body, Air;
        public float Flash, Afterglow, Breath, WeatherTime;
        public float Distance, Yaw, Pitch, Roll;
        public float Movement, Exposure, UpdraftTime, BackdropOnly;
        public float LowSustain, HighTransient, CloudResponse, OrbitResponse;
    }
}
