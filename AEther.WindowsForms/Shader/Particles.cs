using SharpDX.Direct3D11;
using System;

namespace AEther.WindowsForms
{
    public class Particles : GraphicsComponent, IDisposable
    {

        readonly ComputeBuffer Buffer;
        readonly Shader ShaderSimulate;
        readonly Shader ShaderDraw;
        readonly Model Model;

        public Particles(Graphics graphics, int count, ConstantBuffer<CameraConstants> cameraConstants)
            : base(graphics)
        {

            Buffer = Graphics.CreateComputeBuffer(80, count, false);
            ShaderSimulate = Graphics.LoadShader("particles-simulate.fx");
            ShaderDraw = Graphics.LoadShader("particles-draw.fx");
            ShaderDraw.ConstantBuffers["CameraConstants"].SetConstantBuffer(cameraConstants.Buffer);
            //Model = new Model(Graphics.Device, Mesh.CreateSphere(3, 3).SplitVertices(true));
            Model = Graphics.CreateModel(Mesh.CreateGrid(3, 3));

        }

        public void Simulate()
        {
            ShaderSimulate.UnorderedAccesses["Particles"].Set(Buffer.UAView);
            Graphics.Compute(ShaderSimulate, (Buffer.ElementCount, 1, 1));
        }

        public void Draw(ShaderResourceView? texture = null)
        {
            Graphics.SetModel(Model);
            ShaderDraw.ShaderResources["Texture"].SetResource(texture);
            ShaderDraw.ShaderResources["Particles"].SetResource(Buffer.SRView);
            Graphics.Draw(ShaderDraw, Buffer.ElementCount);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Buffer.Dispose();
            ShaderSimulate.Dispose();
            ShaderDraw.Dispose();
            Model.Dispose();
        }
    }
}
