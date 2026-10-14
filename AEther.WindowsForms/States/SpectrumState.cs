using System;

namespace AEther.WindowsForms
{
    using System.Windows.Forms;
    public class SpectrumState : ShaderState
    {
        bool IntensityView;
        bool CombinedLevelView;

        public SpectrumState(Graphics graphics, SpectrumAccumulator[] spectrum)
            : base(graphics, graphics.LoadShader("spectrum.fx"))
        {
            for (var i = 0; i < spectrum.Length; ++i)
            {
                Shader.Variables["Spectrum" + i.ToString()].AsShaderResource().SetResource(spectrum[i].Texture.SRView);
            }
        }

        public override void ProcessKeyPress(KeyPressEventArgs evt)
        {
            if (char.ToLowerInvariant(evt.KeyChar) == 'r')
            {
                CombinedLevelView = !CombinedLevelView;
                Shader.Variables["CombinedLevelView"].AsScalar().Set(CombinedLevelView);
            }
            if (char.ToLowerInvariant(evt.KeyChar) == 'i')
            {
                IntensityView = !IntensityView;
                Shader.Variables["IntensityView"].AsScalar().Set(IntensityView);
            }
        }

        public override void Dispose()
        {
            GC.SuppressFinalize(this);
            Shader.Dispose();
            base.Dispose();
        }

    }
}
