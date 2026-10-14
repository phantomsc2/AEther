using SharpDX;
using SharpDX.Direct3D;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AEther.WindowsForms
{
    public class SceneState : GraphicsState
    {

        internal class MyLight : Light
        {

            public MyLight()
            {
                Intensity = 1000 * Vector3.One;
                IsVolumetric = true;
                CastsShadows = true;
                Transform.Translation = 10 * Vector3.BackwardLH;
            }

            public override void Update(float dt)
            {
                Vector3 d1 = Vector3.Normalize(Vector3.Cross(Transform.Translation, Vector3.Up));
                Vector3 d2 = Vector3.Normalize(Vector3.Cross(Transform.Translation, d1));
                //Momentum.Translation = 1f * d2;
                base.Update(dt);
            }
        }

        internal class SpectrumLight : Light
        {

            readonly int Note;

            public SpectrumLight(SpectrumAccumulator<float> spectrum, int note, Vector3 offset, Vector3 direction)
            {
                spectrum.OnUpdate += Spectrum_OnUpdate;
                Note = note;
                IsVolumetric = true;
                CastsShadows = false;
                Transform.Translation = offset + .3f * note * direction;
                Intensity = Vector3.Zero;
            }

            private void Spectrum_OnUpdate(object? sender, EventArgs e)
            {
                if (sender is SpectrumAccumulator<float> spectrum)
                {
                    var v = new Vector3()
                    {
                        X = spectrum.Buffer[4 * Note + 0],
                        Y = spectrum.Buffer[4 * Note + 1],
                        Z = spectrum.Buffer[4 * Note + 2],
                    };
                    Intensity = 30 * v * v;
                    //Intensity.Y = 0;
                }
            }

        }

        internal class MyGeometry(Model model) : Geometry(model)
        {
            public override void Update(float dt)
            {
                Acceleration = -.01f * Transform with { ScaleLog = 0 };
                base.Update(dt);
            }

        }

        const int ShadowBufferSize = 1 << 10;
        const int AirlightSize = 1 << 8;
        const bool UseInstancing = true;

        readonly Shader MandelboxShader;
        readonly Shader PresentShader;
        readonly Shader AirlightShader;
        readonly Shader ShadowShader;
        readonly Shader GeometryShader;
        readonly Shader LightShader;
        readonly Shader LightShadowShader;

        //readonly Particles Particles;
        readonly ConstantBuffer<Instance> InstanceConstants;
        readonly ConstantBuffer<CameraConstants> CameraConstants;
        readonly ConstantBuffer<LightConstants> LightConstants;
        readonly ComputeBuffer Instances;

        public Particles Particles { get; }

        readonly Model Cube;
        readonly Model Sphere;
        readonly Light Light;

        GeometryBuffer GeometryBuffer;
        Texture2D LightBuffer;

        readonly Texture2D AirlightTexture;
        readonly TextureCube ShadowBuffer;
        readonly Texture2D MandelboxTexture;

        readonly CameraPerspective Camera;

        protected bool IsDisposed;
        readonly Stopwatch Timer = new();
        readonly List<SceneNode> Scene = new();
        readonly List<Model> Models = new();

        public SceneState(Graphics graphics, SpectrumAccumulator<float>[] spectra)
            : base(graphics)
        {

            ShadowBuffer = Graphics.CreateTextureCube(ShadowBufferSize, ShadowBufferSize, Format.R16_Typeless);

            MandelboxShader = Graphics.LoadShader("mandelbox.fx");
            MandelboxTexture = Graphics.CreateTexture(1 << 10, 1 << 10, SharpDX.DXGI.Format.R8G8B8A8_UNorm);

            InstanceConstants = Graphics.CreateConstantBuffer<Instance>();
            CameraConstants = Graphics.CreateConstantBuffer<CameraConstants>();
            LightConstants = Graphics.CreateConstantBuffer<LightConstants>();
            Instances = Graphics.CreateComputeBuffer(Marshal.SizeOf<Instance>(), 1 << 10, true);
            Particles = new Particles(Graphics, 1 << 10, CameraConstants);

            var instancingMacro = UseInstancing
                ? new[] { new ShaderMacro("ENABLE_INSTANCING", true) }
                : Array.Empty<ShaderMacro>();

            ShadowShader = Graphics.LoadShader("shadow.fx", instancingMacro);
            ShadowShader.ConstantBuffers["CameraConstants"].SetConstantBuffer(CameraConstants.Buffer);
            ShadowShader.ConstantBuffers["LightConstants"].SetConstantBuffer(LightConstants.Buffer);
            ShadowShader.ConstantBuffers["InstanceConstants"].SetConstantBuffer(InstanceConstants.Buffer);
            ShadowShader.ShaderResources["Instances"].SetResource(Instances.SRView);
            //ShadowShader.ShaderResources["ColorMap"].SetResource(MandelboxTexture.SRView);

            GeometryShader = Graphics.LoadShader("geometry.fx", instancingMacro);
            GeometryShader.ConstantBuffers["CameraConstants"].SetConstantBuffer(CameraConstants.Buffer);
            GeometryShader.ConstantBuffers["InstanceConstants"].SetConstantBuffer(InstanceConstants.Buffer);
            GeometryShader.ShaderResources["Instances"].SetResource(Instances.SRView);
            //GeometryShader.ShaderResources["ColorMap"].SetResource(MandelboxTexture.SRView);

            PresentShader = Graphics.LoadShader("present.fx");

            AirlightShader = Graphics.LoadShader("airlight.fx");
            AirlightShader.ConstantBuffers["CameraConstants"].SetConstantBuffer(CameraConstants.Buffer);
            AirlightShader.ConstantBuffers["LightConstants"].SetConstantBuffer(LightConstants.Buffer);
            AirlightTexture = Graphics.CreateTexture(AirlightSize, AirlightSize, Format.R11G11B10_Float);

            LightShadowShader = Graphics.LoadShader("light.fx", new ShaderMacro("ENABLE_SHADOWS", true), new ShaderMacro("EQUIANGULAR", true));
            LightShader = Graphics.LoadShader("light.fx", new ShaderMacro("EQUIANGULAR", true));
            foreach (var shader in new[] { LightShader, LightShadowShader })
            {
                shader.ConstantBuffers["CameraConstants"].SetConstantBuffer(CameraConstants.Buffer);
                shader.ConstantBuffers["LightConstants"].SetConstantBuffer(LightConstants.Buffer);
                shader.ShaderResources["Shadow"].SetResource(ShadowBuffer.SRView);
                shader.ShaderResources["Airlight"].SetResource(AirlightTexture.SRView);
            }

            var rng = new Random(0);
            Cube = Graphics.CreateModel(Mesh.CreateSphere(3, 3).SplitVertices(true));
            Sphere = Graphics.CreateModel(Mesh.CreateSphere(15, 15));


            Camera = new CameraPerspective
            {
                Position = 10 * Vector3.BackwardLH,
                AspectRatio = Graphics.BackBuffer.Width / (float)Graphics.BackBuffer.Height,
            };
            Camera.Direction = Vector3.Normalize(Vector3.Zero - Camera.Position);
            for (var i = 0; i < 1000; ++i)
            {
                var model = rng.NextDouble() < .5 ? Cube : Sphere;
                //model = Trees[i % Trees.Length];
                var obj = new MyGeometry(model)
                {
                    Color = new Vector4(rng.NextVector3(Vector3.Zero, Vector3.One), rng.NextDouble() < .5 ? rng.NextFloat(0, 1) : 1),
                    Transform = rng.NextMomentum(15f, 1, 0),
                    Momentum = rng.NextMomentum(1f, .1f, 0),
                    Roughness = rng.NextFloat(0, 1),
                };
                obj.Transform = obj.Transform with { ScaleLog = -1 };
                Scene.Add(obj);
            }
            Light = new Light()
            {
                Intensity = 1000 * Vector3.One,
                IsVolumetric = true,
                CastsShadows = true,
                Transform = new()
                {
                    Translation = 10 * Vector3.Right,
                },
            };
            Scene.Add(Light);

            //for (var i = 0; i < 10; ++i)
            //{
            //    Scene.Add(new Light()
            //    {
            //        Intensity = 1000 * Vector3.One,
            //        IsVolumetric = true,
            //        CastsShadows = false,
            //        Transform = new()
            //        {
            //            Translation = 10 * i * Vector3.Right,
            //        },
            //    });
            //}


            //for (var c = 0; c < 2; c++)
            //{
            //    var spectrum = spectra[c];
            //    var offset = 5 * (c == 0 ? Vector3.Left : Vector3.Right) + 20 * Vector3.Down;
            //    for (var i = 0; i < spectrum.NoteCount; i += 1)
            //    {
            //        Scene.Add(new SpectrumLight(spectrum, i, offset, Vector3.Up));
            //    }
            //}

            //Scene.Add(new Geometry(Sponza)
            //{
            //    Transform = new()
            //    {
            //        Translation = 5 * Vector3.Down,
            //        ScaleLog = -4
            //    },
            //    Color = new Vector4(1, 1, 1, 1f),
            //    Roughness = .2f,
            //});

            LightConstants.Value.Projection = TextureCube.Projection;
            LightConstants.Value.Anisotropy = 0f;
            LightConstants.Value.Emission = 0f * Vector3.One;
            LightConstants.Value.Scattering = 0.1f * new Vector3(1, 1f, 1f);
            LightConstants.Value.Absorption = 0f * Vector3.One;
            LightConstants.Value.FarPlane = TextureCube.FarPlane;
            LightConstants.Update();

            GeometryBuffer = CreateGeometryBuffer();
            LightBuffer = CreateLightBuffer();

            Graphics.SetModel();
            Graphics.SetRenderTarget(null, AirlightTexture);
            Graphics.Draw(AirlightShader);

            Graphics.OnModeChange += Graphics_OnModeChange;

            Models = Scene.OfType<Geometry>().Select(g => g.Model).ToHashSet().ToList();


        }

        GeometryBuffer CreateGeometryBuffer()
        {

            var buffer = new GeometryBuffer(Graphics, Graphics.BackBuffer.Width, Graphics.BackBuffer.Height);

            LightShader.ShaderResources["Depth"].SetResource(buffer.Depth.SRView);
            LightShader.ShaderResources["Normal"].SetResource(buffer.Normal.SRView);
            LightShader.ShaderResources["Color"].SetResource(buffer.Color.SRView);

            LightShadowShader.ShaderResources["Depth"].SetResource(buffer.Depth.SRView);
            LightShadowShader.ShaderResources["Normal"].SetResource(buffer.Normal.SRView);
            LightShadowShader.ShaderResources["Color"].SetResource(buffer.Color.SRView);

            return buffer;

        }

        Texture2D CreateLightBuffer()
        {
            var buffer = Graphics.CreateTexture(Graphics.BackBuffer.Width, Graphics.BackBuffer.Height, Format.R11G11B10_Float);
            PresentShader.ShaderResources["Light"].SetResource(buffer.SRView);
            return buffer;
        }

        private void Graphics_OnModeChange(object? sender, ModeDescription mode)
        {
            GeometryBuffer.Dispose();
            LightBuffer.Dispose();
            GeometryBuffer = CreateGeometryBuffer();
            LightBuffer = CreateLightBuffer();
            Camera.AspectRatio = mode.Width / (float)mode.Height;
        }

        public override void Dispose()
        {
            if (!IsDisposed)
            {

                MandelboxShader.Dispose();
                PresentShader.Dispose();
                AirlightShader.Dispose();
                ShadowShader.Dispose();
                GeometryShader.Dispose();
                LightShader.Dispose();
                LightShadowShader.Dispose();

                Graphics.OnModeChange -= Graphics_OnModeChange;
                Particles.Dispose();
                InstanceConstants.Dispose();
                CameraConstants.Dispose();
                LightConstants.Dispose();
                Instances.Dispose();
                Cube.Dispose();
                Sphere.Dispose();
                foreach (var model in Models)
                    model.Dispose();

                GeometryBuffer.Dispose();
                LightBuffer.Dispose();
                AirlightTexture.Dispose();
                ShadowBuffer.Dispose();
                MandelboxTexture.Dispose();

                GC.SuppressFinalize(this);
                IsDisposed = true;
            }
        }

        void UpdateScene()
        {

            float dt;
            if (Timer.IsRunning)
            {
                Timer.Stop();
                dt = Math.Min(.1f, (float)Timer.Elapsed.TotalSeconds);
                Timer.Restart();
            }
            else
            {
                dt = 0;
                Timer.Start();
            }

            foreach (var g in Scene)
            {
                g.Update(dt);
            }

            //Particles.Simulate();

            Light.Acceleration = -.1f * Light.Transform;

        }

        public override void ProcessKeyPress(KeyPressEventArgs evt)
        {
            var translationSpeed = 1f;
            var rotationSpeed = .1f;
            var invView = Matrix.Invert(Camera.View);
            switch (evt.KeyChar)
            {
                case 'q':
                    Camera.Position -= translationSpeed * Camera.Direction;
                    break;
                case 'e':
                    Camera.Position += translationSpeed * Camera.Direction;
                    break;
                case 'w':
                    Camera.Direction = Vector3.Transform(Camera.Direction, Quaternion.RotationAxis(invView.Right, -rotationSpeed));
                    break;
                case 's':
                    Camera.Direction = Vector3.Transform(Camera.Direction, Quaternion.RotationAxis(invView.Right, +rotationSpeed));
                    break;
                case 'a':
                    Camera.Direction = Vector3.Transform(Camera.Direction, Quaternion.RotationAxis(Vector3.Up, -rotationSpeed));
                    break;
                case 'd':
                    Camera.Direction = Vector3.Transform(Camera.Direction, Quaternion.RotationAxis(Vector3.Up, +rotationSpeed));
                    break;
            }
            base.ProcessKeyPress(evt);
        }

        void RenderScene(IEnumerable<Geometry> geometry, Shader shader)
        {
            foreach (var model in Models)
            {
                Graphics.SetModel(model);
                var group = geometry.Where(g => g.Model == model);
                var count = group.Count();
                if (UseInstancing)
                {
                    using (var map = Instances.Map())
                    {
                        foreach (var obj in group)
                        {
                            map.Write(obj.ToInstance());
                        }
                    }
                    Graphics.Draw(shader, count);
                }
                else
                {
                    foreach (var obj in group)
                    {
                        InstanceConstants.Value = obj.ToInstance();
                        InstanceConstants.Update();
                        Graphics.Draw(shader);
                    }
                }

            }
        }

        public override void Render()
        {

            UpdateScene();
            Light.Intensity = 1000 * (.35f + 1.65f * Graphics.RhythmLight) * Vector3.One;

            Graphics.SetModel();
            Graphics.SetRenderTarget(null, MandelboxTexture);
            Graphics.Draw(MandelboxShader);

            CameraConstants.Value.View = Camera.View;
            CameraConstants.Value.Projection = Camera.Projection;
            CameraConstants.Value.ViewPosition = Camera.Position;
            CameraConstants.Value.FarPlane = Camera.FarPlane;
            CameraConstants.Value.ViewDirectionMatrix = Camera.GetViewDirectionMatrix();
            CameraConstants.Update();

            GeometryBuffer.Depth.ClearDepth();
            GeometryBuffer.Normal.Clear(new Color4(0, 0, 1, 0));
            GeometryBuffer.Color.Clear();
            Graphics.SetRenderTargets(GeometryBuffer.Depth, GeometryBuffer.Textures);
            RenderScene(Scene.OfType<Geometry>(), GeometryShader);

            LightBuffer.Clear();

            foreach (var light in Scene.OfType<Light>())
            {

                LightConstants.Value.Intensity = light.Intensity;
                LightConstants.Value.Position = light.Transform.Translation;
                LightConstants.Value.Distance = Vector3.Distance(Camera.Position, light.Transform.Translation);
                LightConstants.Update();

                foreach (var slice in ShadowBuffer.Slices)
                {
                    slice.ClearDepth();
                    if (light.CastsShadows)
                    {
                        var position = light.Transform.Translation;
                        LightConstants.Value.View = Matrix.LookAtLH(position, position + slice.Direction, slice.Up);
                        LightConstants.Update();
                        Graphics.SetRenderTarget(slice);
                        RenderScene(Scene.OfType<Geometry>(), ShadowShader);
                    }
                }

                var shader = light.CastsShadows ? LightShadowShader : LightShader;
                Graphics.SetModel();
                Graphics.SetRenderTarget(null, LightBuffer);
                Graphics.Draw(shader);

            }

            Graphics.BackBuffer.Clear();
            Graphics.SetModel();
            Graphics.SetRenderTarget(null, Graphics.BackBuffer);
            Graphics.Draw(PresentShader);


        }

    }
}
