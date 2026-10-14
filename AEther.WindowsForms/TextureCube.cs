using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Linq;

namespace AEther.WindowsForms
{
    public class TextureCubeSlice : Texture2D
    {

        public readonly int Index;

        readonly Vector3[] Directions = new[]
        {
            Vector3.Right,
            Vector3.Left,
            Vector3.Up,
            Vector3.Down,
            Vector3.ForwardLH,
            Vector3.BackwardLH,
        };
        public Vector3 Direction => Directions[Index];

        readonly Vector3[] Ups = new[]
        {
            Vector3.Up,
            Vector3.Up,
            Vector3.BackwardLH,
            Vector3.ForwardLH,
            Vector3.Up,
            Vector3.Up,
        };
        public Vector3 Up => Ups[Index];

        public TextureCubeSlice(SharpDX.Direct3D11.Texture2D texture, int index)
            : base(texture, ownsResource: false)
        {
            Index = index;
        }

        protected override DepthStencilView CreateDSView()
        {
            var dsFormat = Resource.Description.Format switch
            {
                Format.R16_Typeless => Format.D16_UNorm,
                Format.R32_Typeless => Format.D32_Float,
                _ => throw new FormatException(nameof(Resource.Description.Format))
            };
            return new DepthStencilView(Resource.Device, Resource, new()
            {
                Format = dsFormat,
                Dimension = DepthStencilViewDimension.Texture2DArray,
                Texture2DArray = new()
                {
                    ArraySize = 1,
                    FirstArraySlice = Index,
                    MipSlice = 0,
                },
            });
        }

    }

    public class TextureCube : Texture2D
    {

        public static readonly float FOV = MathUtil.DegreesToRadians(90);
        public const float NearPlane = .1f;
        public const float FarPlane = 100f;
        public static readonly Matrix Projection = Matrix.PerspectiveFovLH(FOV, 1, NearPlane, FarPlane);

        public readonly TextureCubeSlice[] Slices;

        public TextureCube(SharpDX.Direct3D11.Texture2D texture)
            : base(texture)
        {
            Slices = Enumerable.Range(0, Resource.Description.ArraySize)
                .Select(i => new TextureCubeSlice(texture, i))
                .ToArray();
        }

        public override void Dispose()
        {
            foreach (var slice in Slices)
                slice.Dispose();
            base.Dispose();
        }

        protected override ShaderResourceView CreateSRView()
        {
            var format = Resource.Description.Format switch
            {
                Format.R16_Typeless => Format.R16_UNorm,
                Format.R32_Typeless => Format.R32_Float,
                _ => throw new FormatException(nameof(Resource.Description.Format))
            };
            return new ShaderResourceView(Resource.Device, Resource, new()
            {
                Format = format,
                Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.TextureCube,
                TextureCube = new()
                {
                    MipLevels = -1,
                    MostDetailedMip = 0,
                },
            });
        }

        //public override void ClearDepth(float depth = 1f)
        //{
        //    //base.ClearDepth(depth);
        //    for (var i = 0; i < DSViews.Length; ++i)
        //    {
        //        Resource.Device.ImmediateContext.ClearDepthStencilView(DSViews[i], DepthStencilClearFlags.Depth, depth, 0);
        //    }
        //}

    }
}
