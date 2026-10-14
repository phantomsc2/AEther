using System.Runtime.InteropServices;

namespace AEther.WindowsForms
{
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    public struct FrameConstants
    {

        [FieldOffset(0)]
        public float T;

        [FieldOffset(4)]
        public float DT;

        [FieldOffset(8)]
        public float HistogramShift;

        [FieldOffset(12)]
        public float AspectRatio;

        [FieldOffset(16)]
        public float Transient;

        [FieldOffset(20)]
        public float Motion;

        [FieldOffset(24)]
        public float MotionVelocity;

        [FieldOffset(28)]
        public float HistorySpan;

        [FieldOffset(32)] public float PoseX;
        [FieldOffset(36)] public float PoseY;
        [FieldOffset(40)] public float PoseZ;
        [FieldOffset(44)] public float Breath;
        [FieldOffset(48)] public float VisualEnergy;
        [FieldOffset(52)] public float VisualFlash;
        [FieldOffset(56)] public float VisualGlow;
        [FieldOffset(60)] public float VisualSustain;
        [FieldOffset(64)] public float S;


    }
}
