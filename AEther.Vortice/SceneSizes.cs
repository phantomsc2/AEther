namespace AEther.Vortice;

/// <summary>Allocation/workload contract for the existing rendering modules.</summary>
internal readonly record struct SceneSizes(uint PointLights, uint Vertices, uint Instances,
    int FluidWidth, int FluidHeight, int SurfaceWidth, int SurfaceHeight,
    int AirWidth, int AirHeight, int LookupWidth, int LookupHeight)
{
    internal static SceneSizes Resolve(ShaderParameters? values, bool raster, bool fluid, int width, int height)
    {
        var fields = values?.Items.FirstOrDefault(p => p.Name == "Sizes");
        if (fields != null && fields.Kind != "Struct")
            throw new InvalidOperationException("Parameters.Sizes must be a struct.");
        var entries = fields?.Children.ToDictionary(p => p.Name, StringComparer.Ordinal) ?? [];
        foreach (var (name, field) in entries)
        {
            var scalar = name == "PointLights" && raster;
            var vector = (raster && name is "Geometry" or "RasterSurface" or "RasterAirlight" or "RasterAirlightLut")
                || (fluid && name == "FluidVelocity");
            if (!scalar && !vector) throw new InvalidOperationException($"Unknown size Parameters.Sizes.{name} for this scene.");
            if (!field.IsUnsigned || (scalar ? field.Children.Count != 0 : field.Children.Count != 2))
                throw new InvalidOperationException($"Parameters.Sizes.{name}: expected {(scalar ? "uint" : "uint2")}.");
        }
        uint Read(string name, int axis, uint fallback, bool required = false)
        {
            if (!entries.TryGetValue(name, out var p))
            {
                if (required) throw new InvalidOperationException($"Missing Parameters.Sizes.{name}.");
                return fallback;
            }
            return values!.GetUnsigned(p.Children.Count == 0 ? p : p.Children[axis]);
        }
        int Dimension(string name, int axis, int fallback, bool simulation = false, int minimum = 1)
        {
            uint n = Read(name, axis, (uint)fallback);
            if (simulation ? n < 16 || n > 2048 || (n & (n - 1)) != 0 : n < minimum || n > 16384)
                throw new InvalidOperationException($"Parameters.Sizes.{name}: {(simulation ? "axes must be powers of two from 16 through 2048" : $"axes must be from {minimum} through 16384")}.");
            return (int)n;
        }
        uint lights = raster ? Read("PointLights", 0, 0, true) : 0;
        // Respect portable buffer size/addressing and compute dispatch limits, without silent clamping.
        if (lights > 64u * 65535)
            throw new InvalidOperationException("Parameters.Sizes.PointLights exceeds the backend's dispatch limit.");
        uint vertices = raster ? Read("Geometry", 0, 0, true) : 0;
        uint instances = raster ? Read("Geometry", 1, 0, true) : 0;
        if ((ulong)vertices * instances > uint.MaxValue)
            throw new InvalidOperationException("Parameters.Sizes.Geometry exceeds the draw vertex limit.");
        return new(lights, vertices, instances,
            fluid ? Dimension("FluidVelocity", 0, width, entries.ContainsKey("FluidVelocity")) : 0,
            fluid ? Dimension("FluidVelocity", 1, height, entries.ContainsKey("FluidVelocity")) : 0,
            raster ? Dimension("RasterSurface", 0, width) : 0, raster ? Dimension("RasterSurface", 1, height) : 0,
            raster ? Dimension("RasterAirlight", 0, width) : 0, raster ? Dimension("RasterAirlight", 1, height) : 0,
            raster ? Dimension("RasterAirlightLut", 0, width, minimum: 2) : 0,
            raster ? Dimension("RasterAirlightLut", 1, height, minimum: 2) : 0);
    }
}
