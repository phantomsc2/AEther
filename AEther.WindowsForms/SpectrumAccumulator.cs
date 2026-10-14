using SharpDX.Direct3D11;
using System;
using System.Runtime.InteropServices;

namespace AEther.WindowsForms
{

    public abstract class SpectrumAccumulator : IDisposable
    {

        public event EventHandler? OnUpdate;

        public int NoteCount => Texture.Description.Width;

        public readonly Texture2D Texture;

        public SpectrumAccumulator(Texture2D texture)
        {
            Texture = texture;
        }

        public abstract void Clear();

        public abstract void Add(ReadOnlySpan<double> values);

        public virtual int Update()
        {
            OnUpdate?.Invoke(this, EventArgs.Empty);
            return 0;
        }

        public void Dispose()
        {
            Texture.Dispose();
        }

    }

    public abstract class SpectrumAccumulator<T> : SpectrumAccumulator
        where T : struct
    {

        public readonly T[] Buffer;

        protected SpectrumAccumulator(Graphics graphics, int length, SharpDX.DXGI.Format format)
            : base(CreateTexture(graphics, length, format))
        {
            Buffer = new T[4 * length];
        }

        static Texture2D CreateTexture(Graphics graphics, int length, SharpDX.DXGI.Format format)
        {
            var desc = new Texture2DDescription
            {
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.Write,
                Format = format,
                OptionFlags = ResourceOptionFlags.None,
                Usage = ResourceUsage.Dynamic,
                Width = length,
                Height = 1,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                MipLevels = 1,
                ArraySize = 1,
            };
            return graphics.CreateTexture(desc);
        }

        public override void Clear()
        {
            Array.Clear(Buffer, 0, Buffer.Length);
        }

        public override int Update()
        {
            using var map = Texture.Map();
            map.WriteRange(Buffer, 0, Buffer.Length);
            map.Dispose();
            var bandwidth = base.Update();
            return bandwidth + Buffer.Length * Marshal.SizeOf<T>();
        }

    }

    public class ByteSpectrum : SpectrumAccumulator<byte>
    {

        public ByteSpectrum(Graphics graphics, int length)
            : base(graphics, length, SharpDX.DXGI.Format.R8G8B8A8_UNorm)
        { }

        public override void Add(ReadOnlySpan<double> values)
        {
            if (values.Length < Buffer.Length)
            {
                throw new Exception();
            }
            for (int i = 0; i < Buffer.Length; ++i)
            {
                var value = (byte)(255 * values[i].Clamp(0, 1));
                Buffer[i] = Math.Max(value, Buffer[i]);
            }
        }

    }

    public class FloatSpectrum : SpectrumAccumulator<float>
    {

        public FloatSpectrum(Graphics graphics, int length)
            : base(graphics, length, SharpDX.DXGI.Format.R32G32B32A32_Float)
        { }

        public override void Add(ReadOnlySpan<double> values)
        {
            if (values.Length < Buffer.Length)
            {
                throw new Exception();
            }
            for (int i = 0; i < Buffer.Length; ++i)
            {
                var value = (float)values[i].Clamp(0, 1);
                Buffer[i] = Math.Max(value, Buffer[i]);
            }
        }

    }

}
