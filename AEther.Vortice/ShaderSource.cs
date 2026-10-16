using System.Text.RegularExpressions;

namespace AEther.Vortice;

// Includes are source composition only. All parameter metadata comes from D3D reflection.
internal static partial class ShaderSource
{
    [GeneratedRegex("^[ \\t]*#include[ \\t]+\"([^\"]+)\"[ \\t]*\\r?$", RegexOptions.Multiline)]
    private static partial Regex Includes();

    public static string Read(string root, string file) => Expand(Path.GetFullPath(Path.Combine(root, file)), new(StringComparer.OrdinalIgnoreCase));

    static string Expand(string path, HashSet<string> stack)
    {
        if (!stack.Add(path)) throw new InvalidOperationException($"Circular shader include: {path}");
        try
        {
            var source = File.ReadAllText(path);
            return "\n#line 1 \"" + path.Replace('\\', '/') + "\"\n" + Includes().Replace(source, match =>
            {
                var line = source.AsSpan(0, match.Index).Count('\n') + 2;
                var included = Expand(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, match.Groups[1].Value)), stack);
                return included + "\n#line " + line + " \"" + path.Replace('\\', '/') + "\"\n";
            });
        }
        finally { stack.Remove(path); }
    }
}
