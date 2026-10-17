using System;

namespace AEther
{

    public abstract class SampleType
    {

        public abstract int Size { get; }
        public abstract double ReadFrom(ReadOnlySpan<byte> source);

        public class Float32 : SampleType
        {
            public static readonly Float32 Instance = new();
            public override int Size => 4;
            public override double ReadFrom(ReadOnlySpan<byte> source) => BitConverter.ToSingle(source);
        }

        /// <summary>Signed 16-bit PCM normalized to [-1, 1), with -32768 mapping to -1.</summary>
        public class Int16 : SampleType
        {
            public const double Scale = 1 / 32768.0;
            public static readonly Int16 Instance = new();
            public override int Size => 2;
            public override double ReadFrom(ReadOnlySpan<byte> source) => BitConverter.ToInt16(source) * Scale;
        }

    }

}
