using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AEther.WindowsForms
{
    public class ShaderManager : GraphicsComponent, IDisposable
    {

        readonly IncludeHandler Includes;
        readonly string BasePath;

        protected bool IsDisposed;

        public ShaderManager(Graphics graphics, string basePath)
            : base(graphics)
        {

            BasePath = basePath;

            //var includes = Directory.EnumerateFiles(basePath, "*.fxi", SearchOption.AllDirectories);
            //.ToDictionary(path => new FileInfo(path).Name, File.ReadAllText);
            Includes = new IncludeHandler(Path.Join(basePath, "include"));

        }

        public ShaderBytecode Load(string key, ShaderMacro[]? macros = null)
        {

            var path = Path.Join(BasePath, key);
            var file = new FileInfo(path);

            ShaderFlags shaderFlags = ShaderFlags.None;
            EffectFlags effectFlags = EffectFlags.None;

#if DEBUG
            shaderFlags |= ShaderFlags.EnableStrictness;
            shaderFlags |= ShaderFlags.Debug;
#else
            shaderFlags |= ShaderFlags.OptimizationLevel3;
#endif

            var profile = "fx_5_0";
            var sourceCode = File.ReadAllText(file.FullName);

            using var compiled = ShaderBytecode.Compile(sourceCode, profile, shaderFlags, effectFlags, macros ?? Array.Empty<ShaderMacro>(), Includes, file.Name);

            if (compiled.Bytecode is null)
            {
                throw new CompilationException(compiled.Message);
            }

            if (!compiled.Message.Equals("warning X4717: Effects deprecated for D3DCompiler_47\n"))
            {
                Debug.WriteLine(compiled.Message);
            }


            // Keep compilation free of file writes: the watcher must not reload its own output.

            return compiled.Bytecode;

        }

        public void Dispose()
        {
            if (!IsDisposed)
            {
                Includes.Dispose();
                GC.SuppressFinalize(this);
                IsDisposed = true;
            }
        }
    }
}
