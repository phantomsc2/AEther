using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using AEther.Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

internal static partial class Program
{
    // Probe only: production feedback and normalization settings are unchanged.
    static void CheckFlameMipAverage(IntPtr window, string shaders, string output)
    {
        ID3D11Device device=null!; ID3D11DeviceContext context=null!;
        var definition=new SceneDefinition("Flame",(d,c,s)=>
        {
            device=d; context=c;
            return [new FlameEffect("Flame",d,c,s,"Flame.hlsl")];
        });
        using var renderer=new Renderer(window,1280,720,8,60,shaderDirectory:shaders,initialScene:"Flame",sceneDefinitions:[definition]);
        var report=new List<string> { $"Adapter: {renderer.AdapterDescription}",
            "R32_FLOAT, D3D11 GenerateMips; CPU reference uses double accumulation of uploaded float values.",
            "Resolution,Pattern,CPU mean,1x1 mean,relative error %,even-prefix mip,even-prefix mean,relative error %" };
        string F(double x)=>x.ToString("G10",CultureInfo.InvariantCulture);
        foreach (var (width,height) in new[] {(2048,1024),(1280,720),(1281,720),(1280,721),(1281,721),(1536,1024)})
        {
            using var texture=device.CreateTexture2D(new Texture2DDescription
            {
                Width=(uint)width,Height=(uint)height,MipLevels=0,ArraySize=1,Format=Format.R32_Float,
                SampleDescription=new(1,0),BindFlags=BindFlags.RenderTarget|BindFlags.ShaderResource,
                MiscFlags=ResourceOptionFlags.GenerateMips,
            });
            using var view=device.CreateShaderResourceView(texture);
            int top=(int)texture.Description.MipLevels-1, prefix=0, pw=width,ph=height;
            while (pw>1 && ph>1 && pw%2==0 && ph%2==0) { prefix++; pw/=2; ph/=2; }
            foreach (string pattern in new[] {"constant","ramp-x","ramp-y","checker","random","last-column","last-row","center-impulse","fern-24"})
            {
                var values=new float[width*height];
                if (pattern=="fern-24")
                {
                    renderer.Resize(width,height); renderer.SelectScene(null); renderer.SelectScene("Flame");
                    var parameters=renderer.ActiveShader!.Effects[0].Parameters!; parameters.Reset();
                    parameters.SetBoolean(Find(parameters,"Filtering.Mipmaps"),true);
                    parameters.SetBoolean(Find(parameters,"Filtering.Trilinear"),true);
                    for (int i=0;i<24;i++) renderer.Render(false);
                    values=((FlameEffect)renderer.ActiveShader.Effects[0]).ReadDensityPixels();
                }
                else for (int y=0;y<height;y++) for (int x=0;x<width;x++)
                {
                    uint hash=(uint)(y*width+x); hash^=hash>>16; hash*=0x7feb352d; hash^=hash>>15;
                    values[y*width+x]=pattern switch
                    {
                        "constant"=>1f/96,"ramp-x"=>(x+.5f)/width,"ramp-y"=>(y+.5f)/height,
                        "checker"=>(x+y)&1,"random"=>(hash&0xffff)/65535f,
                        "last-column"=>x==width-1?1:0,"last-row"=>y==height-1?1:0,
                        "center-impulse"=>x==width/2 && y==height/2?1:0,_=>0,
                    };
                }
                context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
                context.PSSetShaderResources(0,ShaderProgram.EmptyViews);
                var pinned=GCHandle.Alloc(values,GCHandleType.Pinned);
                try { context.UpdateSubresource(texture,0,null,pinned.AddrOfPinnedObject(),(uint)(width*4),0); }
                finally { pinned.Free(); }
                context.GenerateMips(view);
                double mean=values.Sum(v=>(double)v)/values.Length;
                double highest=ReadMip(texture,top).Average(v=>(double)v), partial=ReadMip(texture,prefix).Average(v=>(double)v);
                var line=$"{width}x{height},{pattern},{F(mean)},{F(highest)},{F(100*(highest/mean-1))},{prefix},{F(partial)},{F(100*(partial/mean-1))}";
                report.Add(line); Console.WriteLine(line);
                if (Math.Abs(partial/mean-1)>2e-5) throw new Exception("Even-prefix average failed.");
                if (width==2048 && Math.Abs(highest/mean-1)>2e-5) throw new Exception("Power-of-two mip average failed.");
            }
            if (width==1280 && height==720) Benchmark(texture,view,top,width,height);
        }
        File.WriteAllLines(Path.Combine(output,"flame-mip-average.txt"),report);
        Console.WriteLine($"Saved {Path.Combine(output,"flame-mip-average.txt")}");

        float[] ReadMip(ID3D11Texture2D texture,int level)
        {
            var desc=texture.Description;
            desc.Width=Math.Max(1,desc.Width>>level); desc.Height=Math.Max(1,desc.Height>>level);
            desc.MipLevels=1; desc.BindFlags=BindFlags.None; desc.MiscFlags=ResourceOptionFlags.None;
            desc.Usage=ResourceUsage.Staging; desc.CPUAccessFlags=CpuAccessFlags.Read;
            using var staging=device.CreateTexture2D(desc);
            context.CopySubresourceRegion(staging,0,0,0,0,texture,(uint)level);
            var map=context.Map(staging,0,MapMode.Read,global::Vortice.Direct3D11.MapFlags.None);
            try
            {
                var data=new float[(int)(desc.Width*desc.Height)];
                for (int y=0;y<desc.Height;y++) Marshal.Copy(map.DataPointer+y*(int)map.RowPitch,data,y*(int)desc.Width,(int)desc.Width);
                return data;
            }
            finally { context.Unmap(staging,0); }
        }

        void Benchmark(ID3D11Texture2D texture,ID3D11ShaderResourceView view,int top,int width,int height)
        {
            // Two otherwise identical 720p compute passes measure marginal GPU work,
            // excluding CPU submission, mip generation and CPU readback.
            string code=$$"""
                Texture2D<float> Source;
                RWTexture2D<float> Destination;
                [numthreads(16,16,1)] void Copy(uint3 id:SV_DispatchThreadID)
                {
                    if(id.x >= {{width}} || id.y >= {{height}}) return;
                    Destination[id.xy]=Source.Load(int3(id.xy,0));
                }
                [numthreads(16,16,1)] void Normalize(uint3 id:SV_DispatchThreadID)
                {
                    if(id.x >= {{width}} || id.y >= {{height}}) return;
                    float mean=Source.Load(int3(0,0,{{top}}));
                    Destination[id.xy]=Source.Load(int3(id.xy,0))/(96*mean);
                }
                [numthreads(1,1,1)] void ReadMean(uint3 id:SV_DispatchThreadID)
                { Destination[uint2(0,0)]=Source.Load(int3(0,0,{{top}})); }
                groupshared float Partial[256];
                [numthreads(256,1,1)] void ReducePrefix(uint index:SV_GroupIndex)
                {
                    float sum=0;
                    for(uint i=index;i<3600;i+=256) sum+=Source.Load(int3(i%80,i/80,4));
                    Partial[index]=sum;
                    GroupMemoryBarrierWithGroupSync();
                    for(uint stride=128;stride>0;stride>>=1)
                    {
                        if(index<stride) Partial[index]+=Partial[index+stride];
                        GroupMemoryBarrierWithGroupSync();
                    }
                    if(index==0) Destination[uint2(0,0)]=Partial[0]/3600;
                }
                """;
            using var copy=new ShaderProgram(device,code,"Copy","MipAverageProbe","cs_5_0");
            using var normalize=new ShaderProgram(device,code,"Normalize","MipAverageProbe","cs_5_0");
            using var readMean=new ShaderProgram(device,code,"ReadMean","MipAverageProbe","cs_5_0");
            using var reducePrefix=new ShaderProgram(device,code,"ReducePrefix","MipAverageProbe","cs_5_0");
            using var destination=device.CreateTexture2D(new Texture2DDescription
            {
                Width=(uint)width,Height=(uint)height,MipLevels=1,ArraySize=1,Format=Format.R32_Float,
                SampleDescription=new(1,0),BindFlags=BindFlags.UnorderedAccess,
            });
            using var target=device.CreateUnorderedAccessView(destination);
            using var disjoint=device.CreateQuery(new(QueryType.TimestampDisjoint));
            using var begin=device.CreateQuery(new(QueryType.Timestamp)); using var end=device.CreateQuery(new(QueryType.Timestamp));
            var buffers=new Dictionary<string,ID3D11Buffer>(); var samplers=new Dictionary<string,ID3D11SamplerState>();
            var textures=new Dictionary<string,ID3D11ShaderResourceView>{{"Source",view}};
            var outputs=new Dictionary<string,ID3D11UnorderedAccessView>{{"Destination",target}};
            void Dispatch(ShaderProgram program,bool tiny=false)
            {
                program.Bind(context,null,buffers,textures,samplers,outputs);
                context.Dispatch(tiny?1u:(uint)((width+15)/16),tiny?1u:(uint)((height+15)/16),1);
            }
            void Unbind()
            {
                context.CSSetShaderResources(0,ShaderProgram.EmptyViews); context.CSSetUnorderedAccessView(0,null);
            }
            T Wait<T>(ID3D11Query query) where T:unmanaged
            {
                var timeout=Stopwatch.StartNew();
                while (!context.GetData(query,AsyncGetDataFlags.None,out T data))
                { if(timeout.Elapsed.TotalSeconds>10) throw new TimeoutException("GPU timing query timeout."); Thread.Yield(); }
                context.GetData(query,AsyncGetDataFlags.None,out T result); return result;
            }
            double Measure(Action action,int batch=16)
            {
                context.Begin(disjoint); context.End(begin);
                for(int i=0;i<batch;i++) action();
                context.End(end); context.End(disjoint); context.Flush();
                var frequency=Wait<QueryDataTimestampDisjoint>(disjoint);
                ulong first=Wait<ulong>(begin),last=Wait<ulong>(end);
                if(frequency.Disjoint) return double.NaN;
                return (last-first)*1000.0/frequency.Frequency/batch;
            }
            var copies=new List<double>(); var normalized=new List<double>(); var reads=new List<double>(); var mips=new List<double>(); var reductions=new List<double>();
            for (int i=0;i<40;i++)
            {
                double c,n;
                if(i%2==0) { c=Measure(()=>Dispatch(copy)); n=Measure(()=>Dispatch(normalize)); }
                else { n=Measure(()=>Dispatch(normalize)); c=Measure(()=>Dispatch(copy)); }
                double r=Measure(()=>Dispatch(readMean,true)), reduction=Measure(()=>Dispatch(reducePrefix,true)); Unbind();
                double m=Measure(()=>context.GenerateMips(view));
                if(i>=8 && new[]{c,n,r,m,reduction}.All(double.IsFinite)) { copies.Add(c); normalized.Add(n); reads.Add(r); mips.Add(m); reductions.Add(reduction); }
            }
            void Stats(string label,IEnumerable<double> sequence)
            {
                var v=sequence.Order().ToArray();
                string line=$"1280x720 {label}: median={F(v[v.Length/2])} ms, p95={F(v[(int)(.95*(v.Length-1))])} ms, n={v.Length}";
                report.Add(line); Console.WriteLine(line);
            }
            Stats("GPU copy pass",copies); Stats("GPU normalize using 1x1 mip",normalized);
            Stats("paired normalize-minus-copy",copies.Zip(normalized,(c,n)=>n-c));
            Stats("GPU 1-thread read-mean dispatch (includes dispatch cost)",reads); Stats("GenerateMips (warm repeated source)",mips);
            Stats("GPU 80x45 even-prefix reduction, one group",reductions);
            // Verify the GPU normalization uses the measured top-level value.
            Dispatch(normalize); Unbind();
            double sourceMean=ReadMip(texture,0).Average(v=>(double)v), mipMean=ReadMip(texture,top)[0];
            double normalizedMass=96*ReadMip(destination,0).Average(v=>(double)v);
            if(Math.Abs(normalizedMass-sourceMean/mipMean)>1e-5) throw new Exception("GPU normalization probe disagrees with mip average.");
            report.Add($"1280x720 fern normalized true mass={F(normalizedMass)} (should equal CPU mean / mip mean)");
            Dispatch(reducePrefix,true); Unbind();
            double reduced=ReadMip(destination,0)[0];
            if(Math.Abs(reduced/sourceMean-1)>2e-5) throw new Exception("GPU even-prefix reduction failed.");
            report.Add($"1280x720 fern even-prefix reduction relative error={F(100*(reduced/sourceMean-1))}%");
            var stageDesc=new Texture2DDescription { Width=1,Height=1,MipLevels=1,ArraySize=1,Format=Format.R32_Float,
                SampleDescription=new(1,0),Usage=ResourceUsage.Staging,CPUAccessFlags=CpuAccessFlags.Read };
            using var stage=device.CreateTexture2D(stageDesc);
            var idle=new List<double>(); var pending=new List<double>();
            double ReadBack(bool regenerate)
            {
                if(regenerate) context.GenerateMips(view);
                long start=Stopwatch.GetTimestamp();
                context.CopySubresourceRegion(stage,0,0,0,0,texture,(uint)top);
                var mapped=context.Map(stage,0,MapMode.Read,global::Vortice.Direct3D11.MapFlags.None);
                float value=Marshal.PtrToStructure<float>(mapped.DataPointer); context.Unmap(stage,0);
                if(!float.IsFinite(value)) throw new Exception("Invalid readback.");
                return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            for(int i=0;i<40;i++) { double a=ReadBack(false),b=ReadBack(true); if(i>=8) { idle.Add(a); pending.Add(b); } }
            Stats("CPU synchronous 1x1 copy+Map, GPU idle",idle);
            Stats("CPU synchronous 1x1 copy+Map, mip generation pending",pending);
            report.Add("Timing notes: 8 warmups, 32 retained batches of 16 GPU operations; copy/normalize order alternates. CPU readback uses a reused staging texture; allocations excluded. These are isolated warm microbenchmarks, not compositor frame timings.");
        }
    }
}
