using System;
using System.IO;
using System.Linq;
using AEther.Vortice;

internal static partial class Program
{
    static void CheckFlame(IntPtr window, string shaders, string output)
    {
        const int width=1280, height=720;
        using var renderer=new Renderer(window,width,height,64,60,shaderDirectory:shaders,initialScene:"Flame");
        if (renderer.ShaderError!=null) throw new Exception(renderer.ShaderError);
        FlameEffect Effect() => (FlameEffect)renderer.ActiveShader!.Effects[0];
        float[] Density() => Effect().ReadDensityPixels();
        var parameters=Effect().Parameters!;
        var cpu=Enumerable.Repeat(1f/96,width*height).ToArray();
        double maxRelativeError=0;
        void Compare(float[] expected)
        {
            var actual=Density();
            if (actual.Any(v=>!float.IsFinite(v) || v<0)) throw new Exception("Invalid Flame density during reference comparison.");
            double error=0, mass=0;
            for (int i=0;i<actual.Length;i++) { error+=Math.Abs(actual[i]-expected[i]); mass+=Math.Abs(expected[i]); }
            double relative=error/Math.Max(mass,1e-20);
            maxRelativeError=Math.Max(maxRelativeError,relative);
            if (relative>.004) throw new Exception($"Flame CPU reference relative L1 error {relative:E3}.");
        }
        for (int frame=1;frame<=64;frame++)
        {
            renderer.Render(false);
            if (frame<=4) { cpu=FlameReference.Step(cpu,width,height,FlameReference.Maps()); Compare(cpu); }
            if (frame is 1 or 8 or 24 or 64)
            {
                var density=Density();
                if (density.Any(v=>!float.IsFinite(v) || v<0)) throw new Exception("Invalid early Flame density.");
                Save(renderer.ReadPixels(false),width,height,Path.Combine(output,$"flame-{frame:D2}.png"));
                Console.WriteLine($"Flame iteration {frame}: mass={density.Sum(v=>(double)v)*96/(width*height):G6}, max={density.Max():G6}");
            }
        }
        // Every edit acts on the current state, rather than reseeding it.
        foreach (var (path,value) in new[] { ("Fern.Growth",.9f), ("Fern.Bend",.1f), ("Fern.Spread",1f), ("Fern.FrondScale",.9f) })
        {
            cpu=Density(); parameters.SetFloat(Find(parameters,path),value);
            double P(string name) => parameters.GetFloat(Find(parameters,"Fern."+name));
            cpu=FlameReference.Step(cpu,width,height,FlameReference.Maps(P("Growth"),P("Bend"),P("Spread"),P("FrondScale")));
            renderer.Render(false); Compare(cpu);
            for (int frame=0;frame<7;frame++) renderer.Render(false);
            Save(renderer.ReadPixels(false),width,height,Path.Combine(output,$"flame-{path}.png"));
        }
        cpu=Density(); parameters.Reset();
        cpu=FlameReference.Step(cpu,width,height,FlameReference.Maps()); renderer.Render(false); Compare(cpu);
        cpu=Density(); parameters.SetFloat(Find(parameters,"DensityGain"),0);
        cpu=FlameReference.Step(cpu,width,height,FlameReference.Maps()); renderer.Render(false); Compare(cpu);

        renderer.Resize(1281,721); renderer.Render(false);
        CompareSeeded(1281,721);
        renderer.Resize(width,height); renderer.Render(false); CompareSeeded(width,height);
        renderer.SelectScene("Spectrum"); renderer.Render(false);
        renderer.SelectScene("Flame"); renderer.Render(false); CompareSeeded(width,height);

        var pathShader=Path.Combine(shaders,"Scenes","Flame.hlsl");
        var original=File.ReadAllText(pathShader);
        cpu=Density(); File.WriteAllText(pathShader,original+"\nINVALID FLAME\n");
        if (renderer.ReloadActiveScene()) throw new Exception("Invalid Flame reload accepted.");
        renderer.Render(false); Compare(FlameReference.Step(cpu,width,height,FlameReference.Maps()));
        File.WriteAllText(pathShader,original);
        if (!renderer.ReloadActiveScene()) throw new Exception(renderer.ShaderError);
        renderer.Render(false); CompareSeeded(width,height);

        // A test-only shader can zero the state for one frame. Normal updates must leave it zero.
        File.WriteAllText(pathShader,original.Replace("return density;","return DensityGain < .1 ? 0 : density;"));
        if (!renderer.ReloadActiveScene()) throw new Exception(renderer.ShaderError);
        parameters=Effect().Parameters!;
        parameters.SetFloat(Find(parameters,"DensityGain"),0); renderer.Render(false);
        parameters.SetFloat(Find(parameters,"DensityGain"),.5f);
        for (int frame=0;frame<4;frame++) renderer.Render(false);
        if (Density().Any(v=>v!=0)) throw new Exception("Zero density was reseeded.");
        File.WriteAllText(pathShader,original);
        if (!renderer.ReloadActiveScene()) throw new Exception(renderer.ShaderError);
        renderer.Render(false); CompareSeeded(width,height);
        Console.WriteLine($"Flame 1280x720: CPU reference max relative L1 error {maxRelativeError:E3}; edits, reset, exposure, resize, reactivation, reload and zero state passed.");
        CheckFlameFiltering(renderer, pathShader, original, output);

        void CompareSeeded(int w,int h)
        {
            Compare(FlameReference.Step(Enumerable.Repeat(1f/96,w*h).ToArray(),w,h,FlameReference.Maps()));
        }
    }

    static void CheckFlameFiltering(Renderer renderer, string shaderPath, string original, string output)
    {
        const int width=1280, height=720;
        FlameEffect Effect() => (FlameEffect)renderer.ActiveShader!.Effects[0];
        ShaderParameters Parameters() => Effect().Parameters!;
        void Set(string name, bool value) => Parameters().SetBoolean(Find(Parameters(),"Filtering."+name),value);
        void Mode(bool mips, bool tri, bool aniso) { Set("Mipmaps",mips); Set("Trilinear",tri); Set("Anisotropic",aniso); }
        void Restart() { renderer.SelectScene(null); renderer.SelectScene("Flame"); }
        float[] Read() => Effect().ReadDensityPixels();
        void CheckFinite(float[] values)
        {
            if (values.Any(v=>!float.IsFinite(v) || v<0)) throw new Exception("Invalid filtered density.");
        }
        var captures=new System.Collections.Generic.Dictionary<(bool,bool,bool),float[]>();
        int mipPasses=0;
        renderer.TransportPassMarker=(name,start)=> { if (start && name=="Flame mipmaps") mipPasses++; };
        foreach (bool mips in new[] {false,true})
        foreach (bool tri in new[] {false,true})
        foreach (bool aniso in new[] {false,true})
        {
            Restart(); Parameters().Reset(); Mode(mips,tri,aniso);
            int before=mipPasses;
            for (int i=0;i<24;i++) renderer.Render(false);
            if (mipPasses-before!=(mips?24:0)) throw new Exception("Wrong mip generation cadence.");
            var values=Read(); CheckFinite(values); captures[(mips,tri,aniso)]=values;
            string mode=aniso?"anisotropic":tri?"trilinear":"bilinear";
            if (!aniso || !tri)
            {
                Save(renderer.ReadPixels(false),width,height,Path.Combine(output,$"flame-{mode}-mips-{mips}.png"));
                Console.WriteLine($"Flame {mode}, mips={mips}: mass={values.Sum(v=>(double)v)*96/(width*height):G6}");
            }
        }
        foreach (bool mips in new[] {false,true})
            if (!captures[(mips,false,true)].SequenceEqual(captures[(mips,true,true)]))
                throw new Exception("Anisotropic precedence changed density.");
        if (!captures[(false,false,false)].SequenceEqual(captures[(false,true,false)]))
            throw new Exception("No-mip bilinear/trilinear differ.");

        // Enabling mipmaps late must build the current state, not restart from the uniform seed.
        Restart(); Parameters().Reset();
        for (int i=0;i<8;i++) renderer.Render(false);
        var previous=Read(); Mode(true,true,false);
        if (!previous.SequenceEqual(Read())) throw new Exception("Toggling filtering changed stored density.");
        renderer.Render(false); CheckFinite(Read());
        Mode(false,true,false); previous=Read();
        renderer.Render(false);
        var expected=FlameReference.Step(previous,width,height,FlameReference.Maps());
        double difference=Read().Zip(expected,(a,b)=>Math.Abs(a-b)).Sum()/expected.Sum(v=>(double)v);
        if (difference>.004) throw new Exception("Disabling mipmaps reset density or sampled stale mips.");

        // A test-only first frame provides Nyquist checkerboard density; all modes then warp the same source.
        var fixture=original.Replace("return density;","return DensityGain < .1 ? ((uint(input.Position.x)+uint(input.Position.y)) & 1) : density;");
        File.WriteAllText(shaderPath,fixture);
        try
        {
            if (!renderer.ReloadActiveScene()) throw new Exception(renderer.ShaderError);
            var results=new System.Collections.Generic.Dictionary<bool,float[]>();
            foreach (bool mips in new[] {false,true})
            {
                Restart(); Parameters().Reset(); Mode(mips,true,false);
                Parameters().SetFloat(Find(Parameters(),"DensityGain"),0); renderer.Render(false);
                var checker=Read();
                if (checker.Min()!=0 || checker.Max()!=1) throw new Exception("Checker fixture failed.");
                Parameters().SetFloat(Find(Parameters(),"DensityGain"),.5f); renderer.Render(false);
                results[mips]=Read(); CheckFinite(results[mips]);
            }
            if (results[false].SequenceEqual(results[true])) throw new Exception("Mip minification has no effect on checker density.");
            // Mips reduce aliasing against the ideal .5 checker average. Trilinear
            // filtering only partially averages the mildly contracted main branch.
            var maps=FlameReference.Maps(); double filteredError=0, rawError=0;
            for (int y=10;y<height-10;y+=13) for (int x=10;x<width-10;x+=13)
            {
                double px=8*(x+.5)/width-4, py=12*(1-(y+.5)/height), average=0;
                bool boundary=false;
                foreach (var m in maps)
                {
                    double dx=px-m.X,dy=py-m.Y;
                    double u=((m.D*dx-m.B*dy)/m.Det+4)/8;
                    double v=1-((-m.C*dx+m.A*dy)/m.Det+6)/12;
                    if (u>-.02 && u<1.02 && v>-.02 && v<1.02)
                    {
                        if (u<.02 || u>.98 || v<.02 || v>.98) { boundary=true; break; }
                        average+=.5/(3*Math.Abs(m.Det));
                    }
                }
                if (!boundary)
                {
                    double filtered=results[true][y*width+x]-average, raw=results[false][y*width+x]-average;
                    filteredError+=filtered*filtered; rawError+=raw*raw;
                }
            }
            if (!(filteredError<rawError)) throw new Exception("Mips did not reduce checker minification error.");
            Console.WriteLine($"Flame checker: filtered/raw squared error {filteredError/rawError:G6}");

            // Force lookup centers outside the texture, but keep overlapping footprints.
            File.WriteAllText(shaderPath,original.Replace("float2 uv = DensityUV(x);",
                "float2 uv = float2(-.25 / Resolution.x, .5);"));
            if (!renderer.ReloadActiveScene()) throw new Exception(renderer.ShaderError);
            Parameters().Reset(); Mode(true,true,false); renderer.Render(false);
            if (!Read().All(v=>float.IsFinite(v) && v>0))
                throw new Exception("Outside-center footprints were rejected instead of border-filtered.");
        }
        finally
        {
            File.WriteAllText(shaderPath,original);
            if (!renderer.ReloadActiveScene()) throw new Exception(renderer.ShaderError);
            renderer.TransportPassMarker=null;
        }
        // Mip resources survive resizing, reactivation and failed reload with toggles retained.
        Parameters().Reset(); Mode(true,false,true);
        renderer.Resize(1281,721); renderer.Render(false); CheckFinite(Read());
        renderer.Resize(width,height); renderer.Render(false); CheckFinite(Read());
        Restart(); renderer.Render(false); CheckFinite(Read());
        previous=Read(); File.WriteAllText(shaderPath,original+"\nINVALID FILTER\n");
        try
        {
            if (renderer.ReloadActiveScene()) throw new Exception("Invalid filtering reload accepted.");
            if (!previous.SequenceEqual(Read())) throw new Exception("Failed reload lost density.");
            renderer.Render(false); CheckFinite(Read());
        }
        finally
        {
            File.WriteAllText(shaderPath,original);
            if (!renderer.ReloadActiveScene()) throw new Exception(renderer.ShaderError);
        }
        renderer.Render(false); CheckFinite(Read());
        Console.WriteLine("Flame filtering: eight toggle combinations, mip cadence, precedence, late toggles, checker minification, resize and reload passed at 720p or higher.");
    }
}
