using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

/// <summary>One raw affine density iteration per canvas frame, without stabilization.</summary>
public sealed class FlameEffect : SceneEffect
{
    readonly ShaderProgram iterate;
    readonly ID3D11SamplerState anisotropic, displaySampler;
    readonly ID3D11BlendState opaque;
    readonly ID3D11DepthStencilState noDepth;
    DensityImage[]? images;
    int current;

    internal FlameEffect(string name, ID3D11Device device, ID3D11DeviceContext context, string source, string file)
        : base(name, device, context, source, file)
    {
        try
        {
            if ((device.CheckFormatSupport(Format.R32_Float) & FormatSupport.MipAutogen) == 0)
                throw new InvalidOperationException("Flame requires R32_Float automatic mip generation support.");
            iterate = new(device, source, "IteratePS", file);
            foreach (var program in new[] { Display, iterate })
                program.ValidateBindings(FrameBindings.Concat([
                    "FlameDensity", "FlameMean", "FlameSampler", "FlameDisplaySampler"
                ]));
            Parameters = ShaderParameters.Merge([Display.Parameters, iterate.Parameters]);
            anisotropic = MakeSampler(Filter.Anisotropic);
            displaySampler = MakeSampler(Filter.MinMagLinearMipPoint, 0);

            ID3D11SamplerState MakeSampler(Filter filter, float maxLod = float.MaxValue) => device.CreateSamplerState(
                new SamplerDescription
                {
                    Filter = filter, MaxAnisotropy = 16,
                    AddressU = TextureAddressMode.Border, AddressV = TextureAddressMode.Border,
                    AddressW = TextureAddressMode.Border, MaxLOD = maxLod,
                });

            opaque = device.CreateBlendState(BlendDescription.Opaque);
            noDepth = device.CreateDepthStencilState(new DepthStencilDescription
                { DepthEnable = false, StencilEnable = false });
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal override unsafe void Prepare(EffectFrame frame, ShaderParameters? parameters,
        Dictionary<string, ID3D11Buffer> buffers, Dictionary<string, ID3D11ShaderResourceView> textures,
        Dictionary<string, ID3D11SamplerState> samplers)
    {
        Context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        int width = PowerOfTwo(frame.Width), height = PowerOfTwo(frame.Height);
        if (images == null || images[0].Texture.Description.Width != width ||
            images[0].Texture.Description.Height != height)
        {
            var first = new DensityImage(Device, width, height);
            DensityImage second;
            try
            {
                second = new(Device, width, height);
            }
            catch
            {
                first.Dispose();
                throw;
            }

            ReleaseImages();
            images = [first, second];
            current = 0;
            Context.ClearRenderTargetView(first.Target, new(1f / 96, 0, 0, 0));
        }

        frame.Marker("Flame mipmaps", true);
        try
        {
            Context.GenerateMips(images[current].View);
        }
        finally
        {
            frame.Marker("Flame mipmaps", false);
        }

        textures["FlameDensity"] = images[current].View;
        textures["FlameMean"] = images[current].MeanView;
        samplers["FlameSampler"] = anisotropic;
        samplers["FlameDisplaySampler"] = displaySampler;
        int next = 1 - current;
        float* factor = stackalloc float[4];
        Context.OMGetBlendState(out var previousBlend, factor, out uint mask);
        Context.OMGetDepthStencilState(out var previousDepth, out uint stencil);
        frame.Marker("Flame iteration", true);
        try
        {
            Context.OMSetRenderTargets(images[next].Target);
            Context.OMSetBlendState(opaque);
            Context.OMSetDepthStencilState(noDepth);
            Context.RSSetViewport(0, 0, width, height);
            Context.VSSetShader(frame.Vertex);
            iterate.Bind(Context, parameters, buffers, textures, samplers);
            Context.Draw(3, 0);
            current = next;
        }
        finally
        {
            Context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
            Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            Context.OMSetBlendState(previousBlend, factor, mask);
            Context.OMSetDepthStencilState(previousDepth, stencil);
            previousBlend?.Dispose();
            previousDepth?.Dispose();
            frame.Marker("Flame iteration", false);
        }

        textures["FlameDensity"] = images[current].BaseView;
    }

    static int PowerOfTwo(int value) => checked((int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)value));

    internal float[] ReadDensityPixels()
    {
        var image = images![current].Texture;
        var description = image.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MipLevels = 1;
        description.MiscFlags = ResourceOptionFlags.None;
        using var staging = Device.CreateTexture2D(description);
        Context.CopySubresourceRegion(staging, 0, 0, 0, 0, image, 0);
        var map = Context.Map(staging, 0, MapMode.Read, global::Vortice.Direct3D11.MapFlags.None);
        try
        {
            int width = (int)description.Width, height = (int)description.Height;
            var values = new float[width * height];
            for (int y = 0; y < height; y++)
                Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, values, y * width, width);
            return values;
        }
        finally
        {
            Context.Unmap(staging, 0);
        }
    }

    void ReleaseImages()
    {
        if (images != null)
            foreach (var image in images)
                image.Dispose();
        images = null;
    }

    internal override void Deactivate()
    {
        base.Deactivate();
        ReleaseImages();
    }

    public override void Dispose()
    {
        base.Dispose();
        iterate?.Dispose();
        anisotropic?.Dispose();
        displaySampler?.Dispose();
        opaque?.Dispose();
        noDepth?.Dispose();
    }

    sealed class DensityImage : IDisposable
    {
        internal readonly ID3D11Texture2D Texture;
        internal readonly ID3D11RenderTargetView Target;
        internal readonly ID3D11ShaderResourceView View;
        internal readonly ID3D11ShaderResourceView BaseView;
        internal readonly ID3D11ShaderResourceView MeanView;

        internal DensityImage(ID3D11Device device, int width, int height)
        {
            Texture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 0, ArraySize = 1,
                Format = Format.R32_Float, SampleDescription = new(1, 0),
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.GenerateMips,
            });
            try
            {
                Target = device.CreateRenderTargetView(Texture, new RenderTargetViewDescription
                {
                    Format = Format.R32_Float, ViewDimension = RenderTargetViewDimension.Texture2D,
                    Texture2D = new Texture2DRenderTargetView { MipSlice = 0 },
                });
                View = device.CreateShaderResourceView(Texture);
                BaseView = device.CreateShaderResourceView(Texture, new ShaderResourceViewDescription
                {
                    Format = Format.R32_Float, ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
                });
                MeanView = device.CreateShaderResourceView(Texture, new ShaderResourceViewDescription
                {
                    Format = Format.R32_Float, ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = new Texture2DShaderResourceView
                    {
                        MostDetailedMip = (uint)System.Numerics.BitOperations.Log2((uint)Math.Max(width, height)),
                        MipLevels = 1,
                    },
                });
            }
            catch
            {
                Target?.Dispose();
                View?.Dispose();
                MeanView?.Dispose();
                Texture.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            View.Dispose();
            BaseView.Dispose();
            MeanView.Dispose();
            Target.Dispose();
            Texture.Dispose();
        }
    }
}
