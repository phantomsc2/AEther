using SharpDX;
using SharpDX.Direct3D11;
using System;
using System.Runtime.InteropServices;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace AEther.WindowsForms
{
    public class ConstantBuffer<T> : IDisposable
        where T : struct
    {

        public readonly Buffer Buffer;

        public T Value = default;
        public bool UseResourceMapping = true;

        protected bool IsDisposed;

        public ConstantBuffer(Device device, BufferDescription? description = default)
        {

            Buffer = new Buffer(device, description ?? new BufferDescription()
            {
                BindFlags = BindFlags.ConstantBuffer,
                CpuAccessFlags = UseResourceMapping ? CpuAccessFlags.Write : CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None,
                SizeInBytes = Marshal.SizeOf(Value),
                StructureByteStride = 0,
                Usage = UseResourceMapping ? ResourceUsage.Dynamic : ResourceUsage.Default,
            });

        }

        public void Update()
        {
            var context = Buffer.Device.ImmediateContext;
            if (UseResourceMapping)
            {
                using var mapping = new ContextMapping(Buffer);
                mapping.Write(Value);
            }
            else
            {
                context.UpdateSubresource(ref Value, Buffer);
            }
        }

        public void Dispose()
        {
            if (!IsDisposed)
            {
                Buffer.Dispose();
                GC.SuppressFinalize(this);
                IsDisposed = true;
            }
        }

    }
}
