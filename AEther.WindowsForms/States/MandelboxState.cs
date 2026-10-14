using System;

namespace AEther.WindowsForms
{
    public class MandelboxState : ShaderState
    {
        float SliceDepth;
        float HistoryWarp = .7f;
        float TimeCompression = 4;

        public MandelboxState(Graphics graphics, SpectrumAccumulator[] spectrum, Histogram[] history)
            : base(graphics, graphics.LoadShader("mandelbox-slice.fx"))
        {
            for (var i = 0; i < spectrum.Length; ++i)
            {
                Shader.Variables["Spectrum" + i.ToString()].AsShaderResource().SetResource(spectrum[i].Texture.SRView);
            }
            for (var i = 0; i < history.Length; ++i)
                Shader.ShaderResources["Histogram" + i].SetResource(history[i].Texture.SRView);
            Shader.Variables["HistoryWarp"].AsScalar().Set(HistoryWarp);
            Shader.Variables["TimeCompression"].AsScalar().Set(TimeCompression);
        }

        public override void ProcessKeyPress(System.Windows.Forms.KeyPressEventArgs evt)
        {
            if (evt.KeyChar == '[') SliceDepth = Math.Max(-2, SliceDepth - .05f);
            if (evt.KeyChar == ']') SliceDepth = Math.Min(2, SliceDepth + .05f);
            if (char.ToLowerInvariant(evt.KeyChar) == 'r') SliceDepth = 0;
            if (char.ToLowerInvariant(evt.KeyChar) == 'v')
                HistoryWarp = HistoryWarp == .7f ? 1 : HistoryWarp == 1 ? 0 : .7f;
            if (char.ToLowerInvariant(evt.KeyChar) == 'c')
                TimeCompression = TimeCompression == 4 ? 8 : TimeCompression == 8 ? 2 : 4;
            Shader.Variables["SliceDepth"].AsScalar().Set(SliceDepth);
            Shader.Variables["HistoryWarp"].AsScalar().Set(HistoryWarp);
            Shader.Variables["TimeCompression"].AsScalar().Set(TimeCompression);
        }

        public override string ToString() => "Mandelbox - history hulls";

        public override void Dispose()
        {
            GC.SuppressFinalize(this);
            Shader.Dispose();
            base.Dispose();
        }

    }
}
