using System;

namespace AEther.Vortice;

// The ring payload has scene-specific meanings. Keep these fixtures outside
// the renderer, whose material attachments have no built-in dye/age semantics.
internal static class FluidTestExtensions
{
    internal static float[] ReadDye(this FluidSimulation fluid) => fluid.ReadMaterial(0);
    internal static float[] ReadAge(this FluidSimulation fluid) => fluid.ReadMaterial(1);

    internal static void SeedFilamentsForCheck(this FluidSimulation fluid, float speed = 0)
    {
        fluid.Clear();
        int width = fluid.Width, height = fluid.Height;
        var pigment = new float[width * height * 4];
        var material = new float[width * height * 2];
        var flow = new float[width * height * 2];
        for (int i = 0; i < flow.Length; i += 2) flow[i] = speed;
        for (int y = height / 4; y < 3 * height / 4; y++)
        for (int x = width / 4; x < 3 * width / 4; x += 4)
        {
            int cell = y * width + x;
            pigment[4 * cell] = .2f;
            pigment[4 * cell + 1] = .1f;
            pigment[4 * cell + 2] = .05f;
            pigment[4 * cell + 3] = 1;
            material[2 * cell + 1] = .5f;
        }
        fluid.WriteMaterial(0, pigment);
        fluid.WriteMaterial(1, material);
        fluid.WriteVelocity(flow);
    }

    internal static void SeedRecirculationForCheck(this FluidSimulation fluid, float densityScale = 1, float speed = .8f)
    {
        int width = fluid.Width, height = fluid.Height;
        var flow = new float[width * height * 2];
        var pigment = new float[width * height * 4];
        var residence = new float[width * height * 2];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int cell = y * width + x;
            flow[2 * cell] = speed;
            double px = (2 * (x + .5) / width - 1) * fluid.Aspect;
            double py = 2 * (y + .5) / height - 1;
            float density = densityScale * (float)Math.Exp(-((px - fluid.Aspect + .16) * (px - fluid.Aspect + .16) + py * py) / .003);
            pigment[4 * cell] = pigment[4 * cell + 3] = density;
            residence[2 * cell] = .5f * density;
            residence[2 * cell + 1] = .5f * density;
        }
        fluid.WriteVelocity(flow);
        fluid.WriteMaterial(0, pigment);
        if (fluid.MaterialCount > 1) fluid.WriteMaterial(1, residence);
    }
}
