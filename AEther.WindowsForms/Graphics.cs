using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Device = SharpDX.Direct3D11.Device;

namespace AEther.WindowsForms
{
    public class Graphics
    {

        public event EventHandler<ModeDescription>? OnModeChange;
        public event EventHandler<string>? OnShaderChange;

        public Texture2D BackBuffer { get; protected set; }
        public readonly ModeDescription NativeMode;
        public readonly IAssetImporter AssetImporter;
        public readonly Dictionary<string, SharpDX.Direct3D11.Buffer> ShaderConstants = new();
        public float ElapsedSeconds { get; internal set; }
        public float RhythmLight { get; internal set; }
        bool IsDisposed;
        readonly Dictionary<string, (string File, ShaderMacro[] Macros)> ShaderVariants = new();

        int IndexCount;
        SharpDX.Direct3D11.Texture2D BackBufferResource;
        readonly ShaderManager Shaders;
        readonly FileSystemWatcher ShaderWatcher;
        readonly Device Device;
        readonly DeviceDebug? Debug;
        readonly SwapChain Chain;
        readonly Model Quad;
        readonly string AssemblyDirectory;
        readonly string? ShaderDirectory;
        DeviceContext Context => Device.ImmediateContext;

        public Graphics(IntPtr handle, string? shaderDirectory = null)
        {
            ShaderDirectory = shaderDirectory;

            var format = Format.R8G8B8A8_UNorm;
            var modeFlags = DisplayModeEnumerationFlags.Scaling;
            using var dxgiFactory = new Factory1();
            using var dxgiAdapater = dxgiFactory.GetAdapter(0);
            using var output = dxgiAdapater.GetOutput(0);
            NativeMode = output.GetDisplayModeList(format, modeFlags)
                .OrderByDescending(m => m.Width * m.Height)
                .ThenByDescending(m => m.RefreshRate.Numerator / (float)m.RefreshRate.Denominator)
                .First();

            var desc = new SwapChainDescription()
            {
                BufferCount = 3,
                ModeDescription = NativeMode,
                IsWindowed = true,
                OutputHandle = handle,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipDiscard,
                Usage = Usage.RenderTargetOutput,
                Flags = SwapChainFlags.AllowModeSwitch,
            };

            DeviceCreationFlags deviceFlags = DeviceCreationFlags.None;
#if DEBUG
            deviceFlags |= DeviceCreationFlags.Debug;
#endif

            Device.CreateWithSwapChain(
                DriverType.Hardware,
                deviceFlags,
                new[] { FeatureLevel.Level_11_0 },
                desc,
                out Device,
                out Chain);

            using var factory = Chain.GetParent<Factory>();
            factory.MakeWindowAssociation(handle, WindowAssociationFlags.None);

            if (Device.CreationFlags.HasFlag(DeviceCreationFlags.Debug))
            {
                Debug = new DeviceDebug(Device);
            }


            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            AssemblyDirectory = Path.GetDirectoryName(assemblyPath) ?? throw new NullReferenceException();

            BackBufferResource = Chain.GetBackBuffer<SharpDX.Direct3D11.Texture2D>(0);
            BackBuffer = new Texture2D(BackBufferResource);
            (Shaders, ShaderWatcher) = CreateShaderManager();
            ShaderWatcher.Changed += ShaderWatcher_Changed;
            Quad = new Model(Device, Mesh.CreateGrid(2, 2));

            AssetImporter = CreateAssetImporter();
            var c = new GraphicsCapabilities(Device);

        }

        private void ShaderWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            var extension = Path.GetExtension(e.FullPath);
            if (extension == ".fx" || extension == ".fxi")
                OnShaderChange?.Invoke(sender, e.FullPath);
        }

        public bool IsFullscreen
        {
            get
            {
                Chain.GetFullscreenState(out var state, out _);
                return state;
            }
            set
            {
                Chain.SetFullscreenState(value, null);
            }

        }

        public Shader LoadShader(string key, params ShaderMacro[] macros)
        {
            var variant = key + string.Join(";", macros.Select(m => m.Name + "=" + m.Definition));
            ShaderVariants[variant] = (key, macros);
            var bytecode = Shaders.Load(key, macros);
            var shader = new Shader(Device, bytecode);
            foreach (var kvp in ShaderConstants)
            {
                shader.ConstantBuffers[kvp.Key].SetConstantBuffer(kvp.Value);
            }
            return shader;
        }

        // Compile every active variant before retiring the current working session.
        public void ValidateShaders()
        {
            foreach (var variant in ShaderVariants.Values)
            {
                using var bytecode = Shaders.Load(variant.File, variant.Macros);
            }
        }

        public void ThrowIfDebugErrors()
        {
            if (Debug is null)
                return;
            using var messages = Device.QueryInterface<SharpDX.Direct3D11.InfoQueue>();
            for (long i = 0; i < messages.NumStoredMessagesAllowedByRetrievalFilter; ++i)
            {
                var message = messages.GetMessage(i);
                if (message.Severity == MessageSeverity.Error || message.Severity == MessageSeverity.Corruption)
                    throw new InvalidOperationException(message.Description);
            }
            messages.ClearStoredMessages();
        }

        public Model CreateModel(Mesh mesh)
            => new(Device, mesh);

        public SharpDX.Direct3D11.Texture3D CreateNoiseVolume(int size, int seed)
        {
            var values = new float[checked(size * size * size)];
            var random = new Random(seed);
            for (var i = 0; i < values.Length; ++i)
                values[i] = (float)random.NextDouble();
            using var data = new SharpDX.DataStream(values.Length * sizeof(float), true, true);
            data.WriteRange(values);
            return new SharpDX.Direct3D11.Texture3D(Device, new Texture3DDescription
            {
                Width = size, Height = size, Depth = size, MipLevels = 1,
                Format = Format.R32_Float, BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None, Usage = ResourceUsage.Immutable,
                OptionFlags = ResourceOptionFlags.None,
            }, new[] { new SharpDX.DataBox(data.DataPointer, size * sizeof(float), size * size * sizeof(float)) });
        }

        public void SetMode(ModeDescription targetMode)
        {

            using var dxgiFactory = new Factory1();
            using var dxgiAdapater = dxgiFactory.GetAdapter(0);
            using var output = dxgiAdapater.GetOutput(0);
            output.GetClosestMatchingMode(null, targetMode, out var mode);

            if (!Chain.Description.ModeDescription.Equals(mode))
            {
                BackBuffer.Dispose();
                Context.ClearState();
                Context.Flush();
                Chain.ResizeBuffers(Chain.Description.BufferCount, mode.Width, mode.Height, mode.Format, SwapChainFlags.AllowModeSwitch);
                Chain.ResizeTarget(ref mode);
                BackBufferResource = Chain.GetBackBuffer<SharpDX.Direct3D11.Texture2D>(0);
                BackBuffer = new Texture2D(BackBufferResource);
                OnModeChange?.Invoke(this, mode);
            }

        }

        public void Dispose()
        {
            if (IsDisposed)
                return;
            IsDisposed = true;
            ShaderWatcher.EnableRaisingEvents = false;
            ShaderWatcher.Changed -= ShaderWatcher_Changed;
            ShaderWatcher.Dispose();
            Context.ClearState();
            Context.Flush();
            BackBuffer.Dispose();
            Shaders.Dispose();
            Quad.Dispose();
            Chain.Dispose();
            Context.Dispose();
            Device.Dispose();

            Debug?.Dispose();

        }

        public ConstantBuffer<T> CreateConstantBuffer<T>()
            where T : struct
            => new(Device);

        public ComputeBuffer CreateComputeBuffer(int stride, int size, bool cpuWrite)
            => new(Device, stride, size, cpuWrite);

        public Texture2D CreateTexture(int width, int height, Format format,
            BindFlags bindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CpuAccessFlags accessFlags = CpuAccessFlags.None,
            ResourceOptionFlags optionFlags = ResourceOptionFlags.None,
            ResourceUsage usage = ResourceUsage.Default)
             => CreateTexture(new Texture2DDescription
             {
                 ArraySize = 1,
                 BindFlags = bindFlags,
                 CpuAccessFlags = accessFlags,
                 Format = format,
                 Width = width,
                 Height = height,
                 MipLevels = 1,
                 OptionFlags = optionFlags,
                 SampleDescription = new SampleDescription(1, 0),
                 Usage = usage,
             });

        public Texture2D CreateTexture(Texture2DDescription description)
            => new(new SharpDX.Direct3D11.Texture2D(Device, description));

        public DepthBuffer CreateDepthBuffer(int width, int height, Format format)
        {
            var texture = new SharpDX.Direct3D11.Texture2D(Device, new Texture2DDescription
            {
                ArraySize = 1,
                BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                Format = format,
                Width = width,
                Height = height,
                MipLevels = 1,
                OptionFlags = ResourceOptionFlags.None,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
            });
            return new DepthBuffer(texture);
        }

        public TextureCube CreateTextureCube(int width, int height, Format format)
        {
            var texture = new SharpDX.Direct3D11.Texture2D(Device, new()
            {
                ArraySize = 6,
                BindFlags = BindFlags.ShaderResource | BindFlags.DepthStencil,
                CpuAccessFlags = CpuAccessFlags.None,
                Format = format,
                Width = width,
                Height = height,
                MipLevels = 1,
                OptionFlags = ResourceOptionFlags.TextureCube,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
            });
            return new TextureCube(texture);
        }

        IAssetImporter CreateAssetImporter()
        {
            var prefix = AssemblyDirectory;
            prefix = Path.Join(prefix, "data", "assets");
            return new AssimpAssetImporter(prefix);
        }

        (ShaderManager, FileSystemWatcher) CreateShaderManager()
        {

            var shaderDir = AssemblyDirectory;

#if DEBUG
            shaderDir = Path.Join(shaderDir, "..", "..", "..");
            //shaderDir = Directory.GetParent(shaderDir)?.FullName ?? string.Empty;
            //shaderDir = Directory.GetParent(shaderDir)?.FullName ?? string.Empty;
            //shaderDir = Directory.GetParent(shaderDir)?.FullName ?? string.Empty;
#endif
            var shaderPath = Path.Join(shaderDir, "data", "fx");
            shaderPath = ShaderDirectory ?? shaderPath;
            var shader = new ShaderManager(this, shaderPath);
            var watcher = new FileSystemWatcher(shaderPath)
            {
                NotifyFilter = NotifyFilters.LastWrite,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            return (shader, watcher);
        }

        public void SetModel(Model? model = default)
        {
            model ??= Quad;
            if (Context != default && model != default)
            {
                Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                Context.InputAssembler.SetVertexBuffers(0, model.VertexBufferBinding);
                Context.InputAssembler.SetIndexBuffer(model.IndexBuffer, Format.R32_UInt, 0);
                IndexCount = model.IndexBuffer.Description.SizeInBytes / sizeof(uint);
            }
        }

        public void Present()
        {
            Chain.Present(1, PresentFlags.None);
        }

        /// <summary>Read back the rendered RGBA frame for visual regression review.</summary>
        public void SaveFrame(string path)
        {
            var description = BackBufferResource.Description;
            description.BindFlags = BindFlags.None;
            description.CpuAccessFlags = CpuAccessFlags.Read;
            description.OptionFlags = ResourceOptionFlags.None;
            description.Usage = ResourceUsage.Staging;
            using var staging = new SharpDX.Direct3D11.Texture2D(Device, description);
            Context.CopyResource(BackBufferResource, staging);
            var mapped = Context.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            try
            {
                using var bitmap = new System.Drawing.Bitmap(description.Width, description.Height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var locked = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
                try
                {
                    var row = new byte[description.Width * 4];
                    for (var y = 0; y < description.Height; ++y)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(mapped.DataPointer + y * mapped.RowPitch, row, 0, row.Length);
                        for (var x = 0; x < row.Length; x += 4)
                            (row[x], row[x + 2]) = (row[x + 2], row[x]);
                        System.Runtime.InteropServices.Marshal.Copy(row, 0, locked.Scan0 + y * locked.Stride, row.Length);
                    }
                }
                finally { bitmap.UnlockBits(locked); }
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            finally { Context.UnmapSubresource(staging, 0); }
        }

        RenderTargetView[] RTViews = Array.Empty<RenderTargetView>();

        public void SetRenderTarget(Texture2D depthBuffer)
        {
            Context.Rasterizer.SetViewport(0, 0, depthBuffer.Width, depthBuffer.Height);
            Context.OutputMerger.SetRenderTargets(depthBuffer.DSView);
        }

        public void SetRenderTarget(Texture2D? depthBuffer, Texture2D renderTarget)
        {
            Context.Rasterizer.SetViewport(0, 0, renderTarget.Width, renderTarget.Height);
            Context.OutputMerger.SetRenderTargets(depthBuffer?.DSView, renderTarget.RTView);
        }

        public void SetRenderTargets(Texture2D? depthBuffer, Texture2D[] renderTargets)
        {
            if (RTViews.Length < renderTargets.Length)
            {
                RTViews = new RenderTargetView[renderTargets.Length];
            }
            Array.Clear(RTViews, 0, RTViews.Length);
            for (var i = 0; i < renderTargets.Length; ++i)
            {
                RTViews[i] = renderTargets[i].RTView;
            }
            if (depthBuffer is Texture2D db)
            {
                Context.Rasterizer.SetViewport(0, 0, db.Width, db.Height);
            }
            else if (renderTargets.FirstOrDefault() is Texture2D rt)
            {
                Context.Rasterizer.SetViewport(0, 0, rt.Width, rt.Height);
            }
            Context.OutputMerger.SetRenderTargets(depthBuffer?.DSView, RTViews);
        }

        public void Compute(Shader shader, (int, int, int)? threadCount = default, int? techniqueIndex = default)
        {

            if (Context == default)
            {
                throw new Exception();
            }

            var (tx, ty, tz) = threadCount ?? (1, 1, 1);
            var technique = shader[techniqueIndex ?? 0];
            for (int passIndex = 0; passIndex < technique.PassCount; ++passIndex)
            {
                var pass = technique[passIndex];
                pass.Apply(Context);
                Context.Dispatch(tx, ty, tz);
            }

            UnsetResources(shader);

        }

        public void Draw(Shader shader, int instanceCount = 0, int techniqueIndex = 0, int indexOffset = 0, int vertexOffset = 0, int instanceOffset = 0)
        {

            var technique = shader[techniqueIndex];
            for (int passIndex = 0; passIndex < technique.PassCount; ++passIndex)
            {

                var pass = technique[passIndex];
                pass.Apply(Context);

                if (0 < instanceCount)
                {
                    Context.DrawIndexedInstanced(IndexCount, instanceCount, indexOffset, vertexOffset, instanceOffset);
                }
                else
                {
                    Context.DrawIndexed(IndexCount, indexOffset, vertexOffset);
                }

                UnsetResources(shader);

            }
        }

        void UnsetResources(Shader shader)
        {

            for (int i = 0; i < shader.ShaderResources.Count; ++i)
            {
                Context.ComputeShader.SetShaderResource(i, null);
                Context.PixelShader.SetShaderResource(i, null);
                Context.VertexShader.SetShaderResource(i, null);
                Context.GeometryShader.SetShaderResource(i, null);
            }

            for (int i = 0; i < shader.UnorderedAccesses.Count; ++i)
            {
                Context.ComputeShader.SetUnorderedAccessView(i, null);
            }
        }


    }
}
