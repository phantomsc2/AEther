
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace AEther.WindowsForms
{
    public class ComputeBuffer : IDisposable
    {

        public int ElementCount => Description.SizeInBytes / Description.StructureByteStride;

        public BufferDescription Description => Buffer.Description;

        readonly Lazy<UnorderedAccessView> UAViewLazy;
        readonly Lazy<ShaderResourceView> SRViewLazy;

        readonly Buffer Buffer;
        public UnorderedAccessView UAView => UAViewLazy.Value;
        public ShaderResourceView SRView => SRViewLazy.Value;

        protected bool IsDisposed;

        public ComputeBuffer(Device device, int stride, int size, bool cpuWrite)
        {
            Buffer = new Buffer(device, new BufferDescription()
            {
                BindFlags = cpuWrite ? BindFlags.ShaderResource : BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                CpuAccessFlags = cpuWrite ? CpuAccessFlags.Write : CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.BufferStructured,
                SizeInBytes = stride * size,
                StructureByteStride = stride,
                Usage = cpuWrite ? ResourceUsage.Dynamic : ResourceUsage.Default,
            });
            UAViewLazy = new Lazy<UnorderedAccessView>(() => new UnorderedAccessView(Buffer.Device, Buffer, new UnorderedAccessViewDescription
            {
                Dimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new UnorderedAccessViewDescription.BufferResource()
                {
                    ElementCount = ElementCount,
                    FirstElement = 0,
                    Flags = UnorderedAccessViewBufferFlags.None
                },
                Format = Format.Unknown,
            }));
            SRViewLazy = new Lazy<ShaderResourceView>(() => new ShaderResourceView(Buffer.Device, Buffer, new ShaderResourceViewDescription
            {
                Dimension = ShaderResourceViewDimension.ExtendedBuffer,
                BufferEx = new ShaderResourceViewDescription.ExtendedBufferResource()
                {
                    ElementCount = ElementCount,
                    FirstElement = 0,
                    Flags = ShaderResourceViewExtendedBufferFlags.None,
                },
                Format = Format.Unknown,
            }));
        }

        public ContextMapping Map()
        {
            return new ContextMapping(Buffer);
        }

        public void Update<T>(ArraySegment<T> values)
            where T : struct
        {

            using var mapping = new ContextMapping(Buffer);
            mapping.WriteRange(values.Array!, values.Offset, values.Count);

        }

        public void Dispose()
        {
            if (!IsDisposed)
            {
                if (UAViewLazy.IsValueCreated)
                    UAViewLazy.Value.Dispose();
                if (SRViewLazy.IsValueCreated)
                    SRViewLazy.Value.Dispose();
                Buffer.Dispose();
                GC.SuppressFinalize(this);
                IsDisposed = true;
            }
        }

    }
}
