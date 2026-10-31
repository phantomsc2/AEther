using System;
using System.IO;
using System.Linq;
using AEther.Vortice;

internal static partial class Program
{
    static void CheckFractalCut(IntPtr window, string shaders, string output)
    {
        const int width=1280, height=720;
        var clock=new FluidSmokeClock();
        using var renderer=new Renderer(window,width,height,64,60,
            shaderDirectory:shaders,timeProvider:clock,initialScene:"FractalCut");
        if (renderer.ShaderError!=null) throw new Exception(renderer.ShaderError);
        float[] Volume() => ((FractalCutEffect)renderer.ActiveShader!.Effects[0]).ReadVolumePixels();
        renderer.Render(false);
        var pixels=renderer.ReadPixels(false);
        if (pixels.Where((_,i)=>i%4!=3).All(v=>v==0)) throw new Exception("Julia volume is empty.");
        Save((byte[])pixels.Clone(),width,height,Path.Combine(output,"fractal-cut.png"));
        renderer.Render(false);
        if (!pixels.SequenceEqual(renderer.ReadPixels(false))) throw new Exception("Static volume flickers.");
        clock.Frame=600; Seed(renderer); renderer.Render(false);
        if (!pixels.SequenceEqual(renderer.ReadPixels(false))) throw new Exception("Volume changes with time or audio.");
        var parameters=renderer.ActiveShader!.Effects[0].Parameters!;
        foreach (var (path,value) in new[] { ("Box.Scale",.35f), ("Box.InnerFoldRadius",.65f),
            ("Box.Iterations",0f), ("CutScale",.75f), ("JuliaSeed.x",.7f), ("JuliaSeed.y",.65f),
            ("JuliaSeed.z",.75f), ("Extinction",.1f) })
        {
            var parameter=Find(parameters,path);
            parameters.SetFloat(parameter,value); renderer.Render(false);
            if (pixels.SequenceEqual(renderer.ReadPixels(false))) throw new Exception(path+" has no effect.");
            parameters.Reset(parameter); renderer.Render(false);
            if (!pixels.SequenceEqual(renderer.ReadPixels(false))) throw new Exception(path+" reset changed defaults.");
        }
        parameters.SetFloat(Find(parameters,"CutScale"),.5f); renderer.Render(false);
        var coarse=renderer.ReadPixels(false);
        Save((byte[])coarse.Clone(),width,height,Path.Combine(output,"fractal-cut-coarse.png"));
        double error=pixels.Zip(coarse,(a,b)=>Math.Abs(a-b)).Average();
        Console.WriteLine($"Julia volume 1280x720: mean export-channel difference, 64 vs 256 slices: {error:F3}/255.");
        parameters.Reset();

        // Scale zero makes every orbit bounded, giving a homogeneous reference.
        parameters.SetFloat(Find(parameters,"Box.Scale"),.5f);
        double maxError=0;
        foreach (float spacing in new[] { 1f,.5f,0f })
        foreach (float extinction in new[] { 0f,.0625f,.25f,1f })
        {
            parameters.SetFloat(Find(parameters,"CutScale"),spacing);
            parameters.SetFloat(Find(parameters,"Extinction"),extinction);
            renderer.Render(false);
            var volume=Volume();
            if (volume.Any(v=>!float.IsFinite(v) || v<0 || v>1.00001f))
                throw new Exception("Invalid accumulated volume RGBA.");
            foreach (var (x,y) in new[] { (640,360), (800,400), (970,360), (640,30), (1100,360) })
            {
                double dx=(2*(x+.5)/width-1)*(width/(double)height)/2.8;
                double dy=-(2*(y+.5)/height-1)/2.8;
                double length=Math.Sqrt(dx*dx+dy*dy+1);
                double entry=3*length;
                double exit=Math.Min(5*length,Math.Min(length/Math.Max(Math.Abs(dx),1e-20),length/Math.Max(Math.Abs(dy),1e-20)));
                double distance=3*Math.Max(0,exit-entry);
                double alpha=1-Math.Exp(-4*extinction*distance);
                double[] expected=[.25*alpha,.7*alpha,alpha,alpha];
                for (int channel=0;channel<4;channel++)
                {
                    double delta=Math.Abs(volume[4*(y*width+x)+channel]-expected[channel]);
                    maxError=Math.Max(maxError,delta);
                    if (delta>3e-5) throw new Exception($"Homogeneous slab integral differs by {delta} at ({x},{y}), spacing {spacing}, extinction {extinction}.");
                }
            }
            if (extinction==0 && volume.Any(v=>v!=0)) throw new Exception("Zero extinction is not transparent black.");
        }
        parameters.Reset();
        renderer.Render(false);
        if (!pixels.SequenceEqual(renderer.ReadPixels(false))) throw new Exception("Optical checks did not restore defaults.");

        // Change only the test copy: far blue alpha .5, near red alpha .25.
        // Their expected over result is (.25,0,.375,.625), unlike reversed order.
        var shaderPath=Path.Combine(shaders,"Scenes","FractalCut.hlsl");
        var original=File.ReadAllText(shaderPath);
        int start=original.IndexOf("float4 SlicePS(",StringComparison.Ordinal);
        int end=original.IndexOf("float4 PS(",start,StringComparison.Ordinal);
        var fixture="""
            float4 SlicePS(SliceVarying input) : SV_Target
            {
                if (input.Bounds.x < -.9999) return float4(.25,0,0,.25);
                if (input.Bounds.y > .9999) return float4(0,0,.5,.5);
                return 0;
            }

            """;
        try
        {
            File.WriteAllText(shaderPath,original[..start]+fixture+original[end..]);
            if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
            renderer.Render(false); var ordered=Volume();
            double[] expected=[.25,0,.375,.625];
            for (int channel=0;channel<4;channel++)
                if (Math.Abs(ordered[4*(360*width+640)+channel]-expected[channel])>1e-6)
                    throw new Exception("Back-to-front premultiplied blending failed.");
        }
        finally
        {
            File.WriteAllText(shaderPath,original);
            if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
        }
        renderer.Render(false);
        renderer.Resize(1281,721); renderer.Render(false);
        if (renderer.ReadPixels(false).Length!=1281*721*4) throw new Exception("Volume resize failed.");
        renderer.SelectScene("Spectrum"); renderer.Render(false);
        renderer.SelectScene("FractalCut"); renderer.Render(false);
        if (renderer.ShaderError!=null) throw new Exception(renderer.ShaderError);
        Console.WriteLine($"Volume: analytic integral max error {maxError:E2} at 16/64/256 slices; zero extinction, saturation, ordering, partial slabs, seed controls/reset, time/audio independence, resize, reload and scene switching passed.");
    }
}
