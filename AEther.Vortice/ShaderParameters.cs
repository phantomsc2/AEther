using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11.Shader;

namespace AEther.Vortice;

public sealed class ShaderParameter
{
    public string Name { get; }
    public string Path { get; }
    public bool IsBoolean { get; }
    public IReadOnlyList<ShaderParameter> Children { get; }
    internal int Offset { get; }
    internal string Kind { get; }
    internal ShaderParameter(string name, string path, int offset, bool boolean, List<ShaderParameter> children, string kind = "float")
    { Name = name; Path = path; Offset = offset; IsBoolean = boolean; Children = children; Kind = kind; }
}

public sealed class ShaderParameters
{
    internal byte[] Data { get; }
    internal Lock SyncRoot { get; } = new();
    long version;
    internal long Version => Interlocked.Read(ref version);
    readonly byte[] defaults;
    public IReadOnlyList<ShaderParameter> Items { get; }
    internal ShaderParameters(ID3D11ShaderReflectionConstantBuffer buffer)
    {
        Data = new byte[buffer.Description.Size];
        var items = new List<ShaderParameter>();
        for (uint i = 0; i < buffer.Description.VariableCount; i++)
        {
            var variable = buffer.GetVariableByIndex(i);
            var desc = variable.Description;
            if (desc.DefaultValue != IntPtr.Zero)
                Marshal.Copy(desc.DefaultValue, Data, (int)desc.StartOffset, (int)desc.Size);
            items.Add(Read(variable.VariableType, desc.Name, desc.Name, (int)desc.StartOffset));
        }
        Items = items.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        defaults = (byte[])Data.Clone();
        foreach (var item in Leaves(Items))
            if (!item.IsBoolean && (!float.IsFinite(GetFloat(item)) || GetFloat(item) is < 0 or > 1))
                throw new InvalidOperationException($"Parameters.{item.Path}: float defaults must be in [0, 1].");
    }

    static ShaderParameter Read(ID3D11ShaderReflectionType type, string name, string path, int offset)
    {
        var d = type.Description;
        if (d.ElementCount != 0) throw new InvalidOperationException($"Parameters.{path}: arrays are not supported.");
        var children = new List<ShaderParameter>();
        if (d.Class == ShaderVariableClass.Struct)
        {
            for (uint i = 0; i < d.MemberCount; i++)
            {
                var member = type.GetMemberTypeByIndex(i);
                var memberName = type.GetMemberTypeName(i);
                children.Add(Read(member, memberName, path + "." + memberName, offset + (int)member.Description.Offset));
            }
        }
        else if (d.Class == ShaderVariableClass.Vector && d.Type == ShaderVariableType.Float)
        {
            for (int i = 0; i < d.ColumnCount; i++)
                children.Add(new("xyzw"[i].ToString(), path + "." + "xyzw"[i], offset + 4 * i, false, [], $"Scalar:{d.Type}:1"));
        }
        else if (d.Class != ShaderVariableClass.Scalar || d.Type is not (ShaderVariableType.Float or ShaderVariableType.Bool))
            throw new InvalidOperationException($"Parameters.{path}: only float, float vectors, bool and structs are supported.");
        return new(name, path, offset, d.Type == ShaderVariableType.Bool,
            children.OrderBy(x => x.Name, StringComparer.Ordinal).ToList(), d.Class == ShaderVariableClass.Struct ? "Struct" : $"{d.Class}:{d.Type}:{d.ColumnCount}");
    }

    public float GetFloat(ShaderParameter parameter) { lock (SyncRoot) return BitConverter.ToSingle(Data, parameter.Offset); }
    public bool GetBoolean(ShaderParameter parameter) { lock (SyncRoot) return BitConverter.ToInt32(Data, parameter.Offset) != 0; }
    ShaderParameters(ShaderParameters source)
    {
        Items = source.Items;
        defaults = source.defaults;
        Data = (byte[])source.Data.Clone();
        version = source.version;
    }
    ShaderParameters(IReadOnlyList<ShaderParameter> items, byte[] data)
    { Items = items; Data = data; defaults = (byte[])data.Clone(); }
    internal static ShaderParameters? Merge(IEnumerable<ShaderParameters?> layouts)
    {
        var sources = layouts.OfType<ShaderParameters>().ToArray();
        if (sources.Length == 0) return null;
        var bytes = new List<byte>();
        var items = MergeItems(sources.SelectMany(s => s.Items.Select(p => (s, p))));
        return new(items, bytes.ToArray());
        IReadOnlyList<ShaderParameter> MergeItems(IEnumerable<(ShaderParameters s, ShaderParameter p)> entries) =>
            entries.GroupBy(e => e.p.Name).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            {
                var first = g.First();
                if (g.Any(e => e.p.Kind != first.p.Kind))
                    throw new InvalidOperationException($"Parameters.{first.p.Path}: conflicting types across shader stages.");
                var offset = bytes.Count;
                var children = MergeItems(g.SelectMany(e => e.p.Children.Select(p => (e.s, p))));
                if (children.Count == 0)
                {
                    var value = first.s.defaults.AsSpan(first.p.Offset, 4).ToArray();
                    if (g.Any(e => !e.s.defaults.AsSpan(e.p.Offset, 4).SequenceEqual(value)))
                        throw new InvalidOperationException($"Parameters.{first.p.Path}: conflicting defaults across shader stages.");
                    bytes.AddRange(value);
                }
                return new ShaderParameter(first.p.Name, first.p.Path, offset, first.p.IsBoolean, children.ToList(), first.p.Kind);
            }).ToArray();
    }
    internal ShaderParameters Snapshot() { lock (SyncRoot) return new(this); }
    public void SetFloat(ShaderParameter parameter, float value)
    {
        if (!float.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
        lock (SyncRoot)
        {
            if (BitConverter.ToSingle(Data, parameter.Offset) == value) return;
            BitConverter.TryWriteBytes(Data.AsSpan(parameter.Offset, 4), value);
            version++;
        }
    }
    public void SetBoolean(ShaderParameter parameter, bool value)
    {
        lock (SyncRoot)
        {
            if ((BitConverter.ToInt32(Data, parameter.Offset) != 0) == value) return;
            BitConverter.TryWriteBytes(Data.AsSpan(parameter.Offset, 4), value ? 1 : 0);
            version++;
        }
    }
    public void Reset() { lock (SyncRoot) { defaults.CopyTo(Data, 0); version++; } }
    internal static IEnumerable<ShaderParameter> Leaves(IEnumerable<ShaderParameter> items) =>
        items.SelectMany(p => p.Children.Count == 0 ? [p] : Leaves(p.Children));
    public void Restore(ShaderParameters previous)
    {
        byte[] source;
        lock (previous.SyncRoot) source = (byte[])previous.Data.Clone();
        lock (SyncRoot) { Copy(Items, previous.Items); version++; }
        void Copy(IReadOnlyList<ShaderParameter> current, IReadOnlyList<ShaderParameter> old)
        {
            foreach (var item in current)
            {
                var match = old.FirstOrDefault(p => p.Name == item.Name && p.Kind == item.Kind);
                if (match == null) continue;
                if (item.Children.Count != 0) Copy(item.Children, match.Children);
                else source.AsSpan(match.Offset, 4).CopyTo(Data.AsSpan(item.Offset, 4));
            }
        }
    }
}
