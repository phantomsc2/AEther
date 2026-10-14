using SharpDX.Direct3D11;
using System.Drawing;

namespace AEther.WindowsForms
{
    public class Texture2D : GraphicsResource<SharpDX.Direct3D11.Texture2D>
    {

        public Texture2DDescription Description => Resource.Description;
        public int Width => Description.Width;
        public int Height => Description.Height;
        public Size Size => new(Width, Height);

        public Texture2D(SharpDX.Direct3D11.Texture2D texture, bool ownsResource = true)
            : base(texture, ownsResource)
        { }

    }
}
