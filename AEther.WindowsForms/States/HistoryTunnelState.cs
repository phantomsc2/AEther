using System;

namespace AEther.WindowsForms
{
    public sealed class HistoryTunnelState : ShaderState
    {
        public HistoryTunnelState(Graphics graphics, Histogram[] history)
            : base(graphics, graphics.LoadShader("history-tunnel.fx"))
        {
            for (var i = 0; i < history.Length; ++i)
                Shader.ShaderResources["Histogram" + i].SetResource(history[i].Texture.SRView);
        }

        public override string ToString() => "History tunnel - glowing rails";

        public override void Dispose()
        {
            Shader.Dispose();
            base.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
