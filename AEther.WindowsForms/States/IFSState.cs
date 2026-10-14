
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using Vector4 = System.Numerics.Vector4;

namespace AEther.WindowsForms
{
    public class IFSState : GraphicsState
    {

        Texture2D Source;
        Texture2D Target;

        readonly IFSElement[] Elements;
        readonly Shader InputShader;
        readonly Shader OutputShader;

        public IFSState(Graphics graphics)
            : base(graphics)
        {

            InputShader = Graphics.LoadShader("ifs-input.fx");
            OutputShader = Graphics.LoadShader("ifs-output.fx");

            //var ifsSize = Math.Max(Graphics.BackBuffer.Width, Graphics.BackBuffer.Height);
            var ifsSize = 1 << 8;

            var ifsDescription = new Texture2DDescription
            {
                ArraySize = 1,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                Format = Format.R16G16B16A16_Float,
                Width = ifsSize,
                Height = ifsSize,
                MipLevels = 1,
                OptionFlags = ResourceOptionFlags.None,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
            };

            Source = Graphics.CreateTexture(ifsDescription);
            Target = Graphics.CreateTexture(ifsDescription);

            Elements = new IFSElement[]
            {
                //new IFSElement(Graphics.Shader["ifs-input.fx"]),
                //new IFSAffine(Graphics.Shader["ifs-fisheye.fx"]) {  Speed = -.0687f },
                //new IFSAffine(Graphics.Shader["ifs-rcp.fx"]) {  Speed = +.0535f },
                //new IFSAffine(Graphics.Shader["ifs-hyperbolic.fx"]) { Speed = +.335f },
                //new IFSAffine(Graphics.Shader["ifs-sphere-inversion.fx"]) {  Speed = +.58684f },
                //new IFSAffine(Graphics.Shader["ifs-sqrt.fx"]) {  Speed = -.0435f },
                //new IFSAffine(Graphics.Shader["ifs-polar.fx"]) {  Speed = -.4935f },
                //new IFSElement(Graphics.Shader["ifs-swirl.fx"]),
                new IFSElement(Graphics, "ifs-hyperbolic.fx"),
                //new IFSElement(Graphics.Shader["ifs-sqrt.fx"]),
                //new IFSAffine(Graphics.Shaders) {  Scale = .5f, Speed = -.1535f, OffsetScale = 2f },
                new IFSAffine(Graphics) {  Scale = .9f, OffsetScale = 1f },
                //new IFSAffine(Graphics.Shader) {  Speed = -.0256f },
                //new IFSAffine(Graphics.Shader) {  Speed = .2125f },
                //new IFSAffine(Graphics.Shader) { Speed = -.04f },
            };

            Source.Clear(Color4.White);

        }


        public override void Render()
        {

            var t = Graphics.ElapsedSeconds;

            var sumWeight = Vector4.Zero;
            foreach (var element in Elements)
            {
                element.Update(t);
                sumWeight += element.Weight;
            }
            //sumWeight = .25f * Vector4.Dot(sumWeight, Vector4.One) * Vector4.One;

            Graphics.SetModel();
            Graphics.SetRenderTarget(null, Source);
            Graphics.Draw(InputShader);

            //Graphics.Context.ClearRenderTargetView(Source.GetRenderTargetView(), Color4.White);
            //for (int n = 0; n < 8; ++n)
            {

                Target.Clear();
                Graphics.SetRenderTarget(null, Target);
                foreach (var element in Elements)
                {
                    element.Weight /= sumWeight;
                    element.Draw(Source);
                }

                (Source, Target) = (Target, Source);

            }

            Graphics.SetRenderTarget(null, Graphics.BackBuffer);
            OutputShader.ShaderResources["Source"].SetResource(Source.SRView);
            Graphics.Draw(OutputShader);

        }

        public override void Dispose()
        {
            GC.SuppressFinalize(this);
            Source.Dispose();
            Target.Dispose();
            InputShader.Dispose();
            OutputShader.Dispose();
            foreach (var element in Elements)
            {
                element.Dispose();
            }
        }
    }
}
