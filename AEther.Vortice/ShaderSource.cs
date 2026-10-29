using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace AEther.Vortice;

internal static class ShaderSource
{
    public static string IncludeFile(string root, string file) =>
        "#include \"" + Path.GetFullPath(Path.Combine(root, file)).Replace('\\', '/') + "\"\n";

    public static ReadOnlyMemory<byte> Compile(string source, string entry, string file, string profile)
    {
        using var includes = new FileIncludes();
        return Compiler.Compile(source, [], includes, entry, file, profile);
    }

    sealed class FileIncludes : CallbackBase, Include
    {
        public Stream Open(IncludeType type, string fileName, Stream? parentStream) =>
            File.OpenRead(Path.Combine(parentStream is FileStream parent
                ? Path.GetDirectoryName(parent.Name)! : "", fileName));

        public void Close(Stream stream) => stream.Dispose();
    }
}
