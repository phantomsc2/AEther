using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Numerics;

namespace AEther.WindowsForms
{

    public class Fluid : GraphicsComponent, IDisposable
    {

        readonly EffectScalarVariable DiffusionVariable;
        public float Diffusion
        {
            get => DiffusionVariable.GetFloat();
            set => DiffusionVariable.Set(value);
        }

        Texture2D Velocity;
        Texture2D VelocityNew;
        readonly Texture2D Pressure;
        readonly Texture2D Divergence;
        readonly Shader Input;
        readonly Shader Advect;
        readonly Shader Diffuse;
        readonly Shader DivergenceShader;
        readonly Shader Project;
        readonly Shader Output;
        readonly IPoissonSolver PoissonSolver;

        public Fluid(Graphics graphics, int width, int height)
            : base(graphics)
        {

            Input = Graphics.LoadShader("fluid-input.fx");
            Advect = Graphics.LoadShader("fluid-advect.fx");
            Diffuse = Graphics.LoadShader("fluid-diffuse.fx");
            DivergenceShader = Graphics.LoadShader("fluid-divergence.fx");
            Project = Graphics.LoadShader("fluid-project.fx", new SharpDX.Direct3D.ShaderMacro("FAST_DERIVATIVES", true));
            Output = Graphics.LoadShader("fluid-output.fx");
            DiffusionVariable = Diffuse.Variables["Variance"].AsScalar();

            //PoissonSolver = new SOR(graphics, width, height, Format.R16_Float) { Iterations = 256, Omega = 1.8f };
            PoissonSolver = new Multigrid(graphics, width, height, Format.R16_Float, Vector2.One);

            Velocity = Graphics.CreateTexture(width, height, Format.R16G16B16A16_Float);
            VelocityNew = Graphics.CreateTexture(width, height, Format.R16G16B16A16_Float);
            Pressure = Graphics.CreateTexture(width, height, Format.R16_Float);
            Divergence = Graphics.CreateTexture(width, height, Format.R16_Float);

        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            DiffusionVariable.Dispose();
            Velocity.Dispose();
            VelocityNew.Dispose();
            Pressure.Dispose();
            Divergence.Dispose();
            Input.Dispose();
            Advect.Dispose();
            Diffuse.Dispose();
            DivergenceShader.Dispose();
            Project.Dispose();
            Output.Dispose();
            if (PoissonSolver is IDisposable disposable)
                disposable.Dispose();
        }

        public Texture2D Render()
        {

            Graphics.SetModel();

            RenderInput();
            RenderAdvect();
            if (0 < Diffusion)
            {
                RenderDiffuse();
            }
            RenderProject();
            RenderOutput();

            return Velocity;

        }

        void RenderInput()
        {

            Graphics.SetRenderTarget(null, VelocityNew);
            Input.ShaderResources["Velocity"].SetResource(Velocity.SRView);
            Graphics.Draw(Input);

            (Velocity, VelocityNew) = (VelocityNew, Velocity);

        }

        void RenderAdvect()
        {

            Graphics.SetRenderTarget(null, VelocityNew);
            Advect.ShaderResources["Velocity"].SetResource(Velocity.SRView);
            Graphics.Draw(Advect);

            (Velocity, VelocityNew) = (VelocityNew, Velocity);

        }

        void RenderDiffuse()
        {

            Graphics.SetRenderTarget(null, VelocityNew);
            Diffuse.ShaderResources["Velocity"].SetResource(Velocity.SRView);
            Graphics.Draw(Diffuse);

            (Velocity, VelocityNew) = (VelocityNew, Velocity);

        }

        void RenderProject()
        {

            Graphics.SetRenderTarget(null, Divergence);
            DivergenceShader.ShaderResources["Velocity"].SetResource(Velocity.SRView);
            Graphics.Draw(DivergenceShader);

            Pressure.Clear();
            PoissonSolver.Solve(Divergence, Pressure);

            Graphics.SetRenderTarget(null, VelocityNew);
            Project.ShaderResources["Velocity"].SetResource(Velocity.SRView);
            Project.ShaderResources["Pressure"].SetResource(Pressure.SRView);
            Graphics.Draw(Project);

            (Velocity, VelocityNew) = (VelocityNew, Velocity);

        }

        void RenderOutput()
        {

            Graphics.SetRenderTarget(null, Graphics.BackBuffer);
            Output.ShaderResources["Velocity"].SetResource(Velocity.SRView);
            Graphics.Draw(Output);

        }

    }
}
