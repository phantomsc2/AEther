using System;
using System.Windows.Forms;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace AEther.WindowsForms
{
    public sealed class StormState : GraphicsState
    {
        public StormResponse Response { get; }
        readonly Shader Clouds;
        readonly Shader Present;
        readonly ConstantBuffer<StormConstants> Constants;
        readonly SharpDX.Direct3D11.Texture3D Noise;
        readonly ShaderResourceView NoiseView;
        Texture2D Image;
        int Quality = 540;
        bool IsDisposed;
        public bool BackdropOnly { get; set; }

        public StormState(Graphics graphics, StormResponse response) : base(graphics)
        {
            Response = response;
            Constants = Graphics.CreateConstantBuffer<StormConstants>();
            Clouds = Graphics.LoadShader("storm.fx");
            Present = Graphics.LoadShader("storm-present.fx");
            Clouds.ConstantBuffers["StormConstants"].SetConstantBuffer(Constants.Buffer);
            Noise = Graphics.CreateNoiseVolume(64, 731);
            NoiseView = new ShaderResourceView(Noise.Device, Noise);
            Clouds.ShaderResources["CloudNoise"].SetResource(NoiseView);
            Image = CreateImage();
            Graphics.OnModeChange += Resize;
        }

        Texture2D CreateImage()
        {
            var height = Math.Min(Quality, Graphics.BackBuffer.Height);
            var width = Math.Max(1, (int)Math.Round(height * Graphics.BackBuffer.Width / (double)Graphics.BackBuffer.Height));
            var image = Graphics.CreateTexture(width, height, Format.R16G16B16A16_Float);
            Present.ShaderResources["CloudImage"].SetResource(image.SRView);
            return image;
        }

        void Resize(object? sender, ModeDescription mode)
        {
            Image.Dispose();
            Image = CreateImage();
        }

        public override void ProcessKeyPress(KeyPressEventArgs evt)
        {
            switch (char.ToLowerInvariant(evt.KeyChar))
            {
                case 'm': Response.CameraMovement = Response.CameraMovement == 1 ? .35 : Response.CameraMovement == .35 ? 0 : 1; break;
                case '+': case '=': Response.InputGain = Math.Min(12, Response.InputGain * 1.2); break;
                case '-': Response.InputGain = Math.Max(.5, Response.InputGain / 1.2); break;
                case 'b': BackdropOnly = !BackdropOnly; break;
                case 'q':
                    Quality = Quality == 540 ? 720 : Quality == 720 ? 360 : 540;
                    Resize(this, Graphics.NativeMode);
                    break;
            }
        }

        public override void Render()
        {
            Constants.Value = new()
            {
                Energy = (float)Response.Energy, Bass = (float)Response.Bass,
                Body = (float)Response.Body, Air = (float)Response.Air,
                Flash = (float)Response.Flash, Afterglow = (float)Response.Afterglow,
                Breath = (float)Response.Breath, WeatherTime = (float)Response.WeatherTime,
                Distance = (float)Response.Distance, Yaw = (float)Response.Yaw,
                Pitch = (float)Response.Pitch, Roll = (float)Response.Roll,
                Movement = (float)Response.MovementScale, Exposure = 1.2f,
                UpdraftTime = (float)Response.UpdraftTime,
                BackdropOnly = BackdropOnly ? 1 : 0,
                LowSustain = (float)Response.LowSustain, HighTransient = (float)Response.HighTransient,
                CloudResponse = (float)Response.CloudResponse, OrbitResponse = (float)Response.OrbitResponse,
            };
            Constants.Update();
            Graphics.SetModel();
            Graphics.SetRenderTarget(null, Image);
            Graphics.Draw(Clouds);
            Graphics.SetRenderTarget(null, Graphics.BackBuffer);
            Graphics.Draw(Present);
        }

        public override string ToString() => "Storm — Central Plains";

        public override void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            Graphics.OnModeChange -= Resize;
            Clouds.Dispose();
            Present.Dispose();
            Image.Dispose();
            NoiseView.Dispose();
            Noise.Dispose();
            Constants.Dispose();
        }
    }
}
