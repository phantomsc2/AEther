using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AEther.Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;

internal static partial class Program
{
    // Research harness only: raw shaders are substituted at the existing pass
    // markers. No new resources or behavior are added to the production renderer.
    static void RunAirlightLutStudy(string[] args)
    {
        string[] positional=args.Where(x=>!x.StartsWith("--")).ToArray();
        string source=Path.GetFullPath(positional.ElementAtOrDefault(0) ?? "AEther.Vortice/Shaders");
        string output=Path.GetFullPath(positional.ElementAtOrDefault(1) ?? "artifacts/mandellight-v4/lut-study");
        Directory.CreateDirectory(output);
        bool cpuOnly=args.Contains("--cpu-only");
        double h=.01+.49*.25*.25;
        double[] extinction=[.3*.65*.65*1.03,.3*.65*.65*1.05,.3*.65*.65*1.08];
        var tables=new List<TailTable>();
        var validation=new List<object>();
        foreach (var spec in new[] {(257,513,false),(513,1025,false),(513,1025,true)})
        {
            var table=new TailTable(spec.Item1,spec.Item2,h,extinction,128,spec.Item3);
            tables.Add(table);
            validation.Add(ValidateTailTable(table));
        }
        var unscaled=new TailTable(513,1025,h,extinction,128,false,scaled:false);
        tables.Add(unscaled);validation.Add(ValidateTailTable(unscaled));
        // Exercise parameter changes, collinear/near-light rays and optical tails.
        foreach (var spec in new[] {(.01,.1),(.5,.65),(.040625,1.0)})
        {
            double[] e=[.3*spec.Item2*spec.Item2*1.03,.3*spec.Item2*spec.Item2*1.05,.3*spec.Item2*spec.Item2*1.08];
            validation.Add(ValidateTailTable(new(513,1025,spec.Item1,e,128,false)));
        }
        File.WriteAllText(Path.Combine(output,"cpu-validation.json"),JsonSerializer.Serialize(validation,JsonOptions));
        if (cpuOnly) return;

        string tree=Path.Combine(output,"Shaders");
        Directory.CreateDirectory(Path.Combine(tree,"Scenes"));
        Directory.CreateDirectory(Path.Combine(tree,"Common"));
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source,"Common"),"*.hlsl",SearchOption.AllDirectories))
        {
            string target=Path.Combine(tree,Path.GetRelativePath(source,file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file,target,true);
        }
        File.Copy(Path.Combine(source,"Scenes/MandelLightv4.hlsl"),Path.Combine(tree,"Scenes/MandelLightv4.hlsl"),true);
        using var window=new Form {ClientSize=new(1280,720),ShowInTaskbar=false};
        var clock=new Clock();
        using var renderer=new Renderer(window.Handle,1280,720,32,60,shaderDirectory:tree,timeProvider:clock);
        renderer.SelectScene("MandelLightv4");
        var device=Field<ID3D11Device>(renderer,"device");
        var context=Field<ID3D11DeviceContext>(renderer,"context");
        Console.WriteLine("GPU: "+renderer.AdapterDescription);
        using var study=new LutGpuStudy(renderer,device,context,source);
        var results=new List<object>();
        var captures=new Dictionary<string,(float[] Hdr,byte[] Display)>();
        var tableGpu=new List<(TailTable Cpu,ID3D11Texture2D Texture,ID3D11ShaderResourceView View)>();
        try
        {
            foreach (var table in tables)
            {
                var texture=table.Upload(device,context);
                tableGpu.Add((table,texture,device.CreateShaderResourceView(texture)));
            }
            foreach (bool half in new[] {false,true})
            {
                string resolution=half ? "half" : "full";
                var cases=new List<(string Name,int Mode,int Samples,bool Shadows,int Table)>
                {
                    ("shadow-reference",0,1024,true,1),
                    ("unshadowed-reference",0,1024,false,1),
                    ("sampled-shadow-32",0,32,true,1),
                    ("sampled-unshadowed-32",0,32,false,1),
                    ("sampled-unshadowed-8",0,8,false,1),
                    ("angular-unshadowed-8",3,8,false,1),
                    ("angular-unshadowed-16",3,16,false,1),
                    ("lut-small",1,32,false,0),
                    ("lut-float",1,32,false,1),
                    ("lut-half",1,32,false,2),
                    ("lut-unscaled",1,32,false,3),
                    ("lut-shadow-8",2,8,true,1),
                    ("lut-shadow-16",2,16,true,1),
                    ("lut-shadow-32",2,32,true,1),
                    ("lut-shadow-64",2,64,true,1),
                    ("lut-unscaled-shadow-16",2,16,true,3),
                    ("lut-unscaled-shadow-32",2,32,true,3),
                    ("angular-shadow-8",3,8,true,1),
                    ("angular-shadow-16",3,16,true,1),
                    ("angular-shadow-32",3,32,true,1),
                };
                foreach (var item in cases)
                {
                    renderer.ActiveShader!.Parameters!.Reset();
                    Set(renderer,"Lights.Count",0); Set(renderer,"View.CameraSpeed",0);
                    Set(renderer,"TonalLights.Enabled",false);
                    Set(renderer,"RingLight.Intensity",0);
                    Set(renderer,"Lights.Directional",false); // Preserve the study's isotropic point-source reference.
                    Set(renderer,"Lights.Shadows",true); // The study overrides visibility, not transport routing.
                    Set(renderer,"Shape.Speed",0); Set(renderer,"Filter.Enabled",false);
                    Set(renderer,"Quality.HalfResolutionAirlight",half);
                    var table=tableGpu[item.Table];
                    study.Configure(item.Mode,item.Samples,item.Shadows,table.Cpu,table.View);
                    clock.Reset();
                    for (int k=0;k<16;k++) { clock.Advance(); renderer.TransportPassMarker=study.Override; RenderV4(renderer); }
                    var frames=new List<Dictionary<string,double>>();
                    int measured=item.Name.EndsWith("reference") ? 11 : 41;
                    using var timer=new Timestamps(device,context);
                    for (int k=0;k<measured;k++)
                    {
                        // Tiny isolated passes let laptop GPUs downclock between
                        // query readbacks. Keep a common substantial workload in
                        // flight, outside the measured interval, then restore
                        // and warm the actual mode. Do not alter driver settings.
                        renderer.TransportPassMarker=study.Override;
                        study.Configure(0,1024,item.Shadows,table.Cpu,table.View);
                        RenderV4(renderer);
                        study.Configure(item.Mode,item.Samples,item.Shadows,table.Cpu,table.View);
                        RenderV4(renderer);
                        timer.Reset();
                        renderer.TransportPassMarker=(name,start)=> {study.Override(name,start); timer.Marker(name,start);};
                        clock.Advance(); timer.Begin(); RenderV4(renderer); timer.End(); frames.Add(timer.Resolve());
                    }
                    renderer.TransportPassMarker=null;
                    var median=frames.SelectMany(x=>x.Keys).Distinct().ToDictionary(k=>k,k=>Median(frames.Select(x=>x.GetValueOrDefault(k))));
                    float[] hdr=ReadAirlight(renderer,device,context);
                    byte[] display=renderer.ReadPixels(false);
                    string key=resolution+"-"+item.Name;
                    captures[key]=(hdr,display);
                    object? error=null;
                    if (!item.Name.EndsWith("reference"))
                    {
                        var reference=captures[resolution+(item.Shadows ? "-shadow-reference" : "-unshadowed-reference")];
                        error=ImageError(hdr,reference.Hdr,display,reference.Display);
                        if(item.Mode==1 && table.Cpu.Scaled)
                        {
                            double relative=HdrError(hdr,reference.Hdr);
                            double limit=table.Cpu.Half ? .005 : .002;
                            if(relative>limit)throw new InvalidOperationException($"GPU LUT accuracy failed: {relative} > {limit}.");
                        }
                    }
                    results.Add(new {name=key,width=1280,height=720,item.Mode,item.Samples,item.Shadows,
                        lut=table.Cpu.Label,milliseconds=median,error,
                        airP10=Percentile(frames.Select(x=>x["Raster airlight"]),.1),
                        airP90=Percentile(frames.Select(x=>x["Raster airlight"]),.9)});
                    Console.WriteLine(key+": "+string.Join(", ",median.Select(x=>$"{x.Key}={x.Value:F4}ms"))+" "+JsonSerializer.Serialize(error));
                    Save(display,1280,720,Path.Combine(output,key+".png"));
                }
            }
            // Measure refresh cost separately. All preceding steady-state cases
            // use cached maps so the air integration comparison is unconfounded.
            Set(renderer,"Shape.Speed",.1f);
            study.Configure(0,32,true,tableGpu[1].Cpu,tableGpu[1].View);
            var refresh=new List<double>();
            using (var timer=new Timestamps(device,context))
            {
                for (int k=0;k<16;k++) {renderer.TransportPassMarker=study.Override; clock.Advance(); RenderV4(renderer);}
                for (int k=0;k<41;k++)
                {
                    renderer.TransportPassMarker=study.Override;
                    study.Configure(0,1024,true,tableGpu[1].Cpu,tableGpu[1].View);RenderV4(renderer);
                    study.Configure(0,32,true,tableGpu[1].Cpu,tableGpu[1].View);
                    timer.Reset(); renderer.TransportPassMarker=(n,s)=>{study.Override(n,s);timer.Marker(n,s);};
                    clock.Advance(); timer.Begin();RenderV4(renderer);timer.End();refresh.Add(timer.Resolve()["Raster light 0 cube"]);
                }
            }
            renderer.TransportPassMarker=study.Override;
            Set(renderer,"Shape.Speed",0);
            var silence=new List<object>();
            foreach (int mode in new[] {0,1,2,3})
            {
                study.Configure(mode,32,mode!=1,tableGpu[1].Cpu,tableGpu[1].View);
                clock.Advance();RenderV4(renderer);
                int peak=0;
                for (int frame=0;frame<64;frame++)
                {
                    clock.Advance();
                    int bands=renderer.Audio.Bands;
                    renderer.PostAudio(new(new double[8*bands],4*bands,DateTime.UnixEpoch){AnalysisRow=v4AudioRow++},2);
                    renderer.Render(false);
                    if (frame>=60) peak=Math.Max(peak,renderer.ReadPixels(false).Where((_,i)=>i%4!=3).Max(x=>(int)x));
                }
                if (peak!=0) throw new InvalidOperationException($"Mode {mode} retained illumination after silence: {peak}");
                silence.Add(new {mode,finalRgbPeak=peak});
            }
            renderer.TransportPassMarker=null;
            File.WriteAllText(Path.Combine(output,"gpu-results.json"),JsonSerializer.Serialize(new
            {
                adapter=renderer.AdapterDescription,
                note="1280x720, one live spectral light, default air/radius/128 cubes. Static geometry and camera, cached maps, same raw shaders and parameter buffers for every mode. GPU timestamps exclude readback, compilation and CPU LUT generation; half mode includes full-resolution fallback. No full-resolution spatial filter. 16 warmups then 41 frames (11 for references). Every measurement is preceded by an untimed 1024-sample GPU primer and one untimed render of the measured mode to suppress laptop GPU idle downclocking.",
                shadowRefreshMedianMs=Median(refresh),silence,results,
            },JsonOptions));
        }
        finally {renderer.TransportPassMarker=null; foreach(var item in tableGpu){item.View.Dispose();item.Texture.Dispose();}}
    }

    static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
    static double Percentile(IEnumerable<double> values,double q) {var a=values.Order().ToArray();return a[(int)Math.Round(q*(a.Length-1))];}

    sealed class TailTable
    {
        // x = t-c, r = sqrt(x*x+b*b), H = sqrt(b*b+h*h).
        // U(b,x) = exp(e*(x+r)) * integral[x,infinity]
        //          phase(v)*exp(-e*(v+sqrt(v*v+b*b)))/(v*v+b*b+h*h) dv.
        // A physical tail is exp(-e*(t+r))*U(b,t-c), which never needs the
        // potentially large exp(-e*c). The table includes phase but not sigma_s
        // or light intensity. RGB extinction is fixed for a given table.
        internal readonly int Width,Height;
        internal readonly double H,Bmax,Bwarp;
        internal readonly double[] E;
        internal readonly bool Half,Scaled;
        internal readonly float[] Data;
        internal readonly double BuildMs;
        internal string Label=>$"{Width}x{Height}-{(Half ? "rgba16f" : "rgba32f")}-{(Scaled ? "scaled" : "unscaled")}";
        static readonly double[] Nodes=[-.8611363115940526,-.3399810435848563,.3399810435848563,.8611363115940526];
        static readonly double[] Weights=[.3478548451374538,.6521451548625461,.6521451548625461,.3478548451374538];
        internal TailTable(int width,int height,double h,double[] e,double bmax,bool half,bool scaled=true)
        {
            Width=width;Height=height;H=h;E=e;Bmax=bmax;Half=half;Scaled=scaled;Bwarp=Math.Asinh(bmax/h);
            Data=new float[width*height*4];var clock=Stopwatch.StartNew();
            Parallel.For(0,width,column=>
            {
                double b=h*Math.Sinh(Bwarp*column/(width-1)),height=Math.Sqrt(b*b+h*h);
                double[] tail=new double[3];
                for (int row=Height-2;row>=0;row--)
                {
                    double angle=TableAngle(row/(double)(Height-1));
                    double step=TableAngle((row+1.0)/(Height-1))-angle,baseU=LightCoordinate(b,height,angle);
                    double upperU=LightCoordinate(b,height,angle+step);
                    for(int channel=0;channel<3;channel++)
                    {
                        double sum=0;
                        for (int j=0;j<4;j++)
                        {
                            double at=angle+.5*step*(1+Nodes[j]),x=height*Math.Tan(at),r2=x*x+b*b;
                            double phase=3/(16*Math.PI)*(r2==0 ? 2 : 2-b*b/r2);
                            double u=LightCoordinate(b,height,at);
                            sum+=Weights[j]*phase*Math.Exp(-e[channel]*Math.Max(0,u-baseU))/height;
                        }
                        tail[channel]=.5*step*sum+Math.Exp(-e[channel]*Math.Max(0,upperU-baseU))*tail[channel];
                        float value=(float)(tail[channel]*(scaled ? 1 : Math.Exp(-e[channel]*baseU)));if(half)value=(float)(System.Half)value;
                        Data[4*(row*width+column)+channel]=value;
                    }
                }
            });
            BuildMs=clock.Elapsed.TotalMilliseconds;
        }
        static double LightCoordinate(double b,double height,double theta)
        {
            double x=height*Math.Tan(theta),r=Math.Sqrt(x*x+b*b);
            return x<0 ? b*b/(r-x) : x+r;
        }
        internal static double TableAngle(double q)
        {
            double v=2*q-1;
            return Math.CopySign(Math.PI*.5*(1-Math.Pow(1-Math.Abs(v),2)),v);
        }
        internal static double AngleCoordinate(double theta) =>
            .5+.5*Math.CopySign(1-Math.Sqrt(Math.Max(0,1-2*Math.Abs(theta)/Math.PI)),theta);
        internal double Tail(double b,double x,int channel)
        {
            double gx=Math.Clamp(Math.Asinh(b/H)/Bwarp,0,1)*(Width-1);
            double gy=AngleCoordinate(Math.Atan(x/Math.Sqrt(b*b+H*H)))*(Height-1);
            int ix=Math.Min((int)gx,Width-2),iy=Math.Clamp((int)gy,0,Height-2);
            double fx=gx-ix,fy=gy-iy;
            double a=Data[4*(iy*Width+ix)+channel]*(1-fx)+Data[4*(iy*Width+ix+1)+channel]*fx;
            double z=Data[4*((iy+1)*Width+ix)+channel]*(1-fx)+Data[4*((iy+1)*Width+ix+1)+channel]*fx;
            return a*(1-fy)+z*fy;
        }
        internal double Integral(double c,double b,double end,int channel,bool clamp=true)
        {
            double first=Tail(b,-c,channel)*Math.Exp(-E[channel]*(Scaled ? Math.Sqrt(c*c+b*b) : c));
            if(double.IsPositiveInfinity(end)) return first;
            double x=end-c,r=Math.Sqrt(x*x+b*b);
            double result=first-Tail(b,x,channel)*Math.Exp(-E[channel]*(Scaled ? end+r : c));
            return clamp ? Math.Max(0,result) : result;
        }
        internal ID3D11Texture2D Upload(ID3D11Device device,ID3D11DeviceContext context)
        {
            byte[] bytes=new byte[Data.Length*(Half ? 2 : 4)];
            if(Half) for(int i=0;i<Data.Length;i++) BitConverter.TryWriteBytes(bytes.AsSpan(2*i,2),(System.Half)Data[i]);
            else Buffer.BlockCopy(Data,0,bytes,0,bytes.Length);
            var texture=device.CreateTexture2D(new Texture2DDescription
            {
                Width=(uint)Width,Height=(uint)Height,ArraySize=1,MipLevels=1,SampleDescription=new(1,0),
                Format=Half ? Format.R16G16B16A16_Float : Format.R32G32B32A32_Float,
                Usage=ResourceUsage.Default,BindFlags=BindFlags.ShaderResource,
            });
            var pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);
            try {context.UpdateSubresource(texture,0,null,pin.AddrOfPinnedObject(),(uint)(Width*4*(Half ? 2 : 4)),0);}
            finally {pin.Free();}
            return texture;
        }
    }

    static object ValidateTailTable(TailTable table)
    {
        var random=new Random(71283);var relative=new List<double>();var absolute=new List<double>();
        double squared=0,energy=0;var worst=new List<object>();
        var relativeCases=new List<(double Error,object Details)>();int negativeDifferences=0;
        for(int sample=0;sample<1200;sample++)
        {
            double b=sample%17==0 ? 0 : sample%19==0 ? table.Bmax : table.H*Math.Sinh(random.NextDouble()*table.Bwarp);
            double c=sample%11==0 ? 0 : 128*(random.NextDouble()-.5);
            double end=sample%3==0 ? double.PositiveInfinity : sample%7==0 ? .0001+random.NextDouble()*.01 : 128*random.NextDouble();
            for(int channel=0;channel<3;channel++)
            {
                double expected=ReferenceAir(c,b,end,table.H,table.E[channel]);
                double actual=table.Integral(c,b,end,channel),difference=Math.Abs(actual-expected);
                if(table.Integral(c,b,end,channel,clamp:false)<0)negativeDifferences++;
                if(!double.IsFinite(actual)||actual<0)throw new InvalidOperationException("Invalid LUT integral.");
                squared+=difference*difference;energy+=expected*expected;absolute.Add(difference);
                if(expected>1e-7)
                {
                    relative.Add(difference/expected);
                    relativeCases.Add((difference/expected,new {c,b,end=double.IsInfinity(end) ? "infinity" : end.ToString("R"),channel,expected,actual,relativeError=difference/expected}));
                }
                if(difference>.002 && worst.Count<8) worst.Add(new {c,b,end=double.IsInfinity(end) ? "infinity" : end.ToString("R"),channel,expected,actual});
            }
        }
        double normalizedRmse=Math.Sqrt(squared/Math.Max(energy,1e-30));
        Console.WriteLine($"CPU {table.Label}, h={table.H}, e={table.E[0]}: build {table.BuildMs:F1}ms, NRMSE={normalizedRmse:G4}, relative p99={Percentile(relative,.99):G4}, max abs={absolute.Max():G4}");
        double limit=table.Scaled ? table.Half ? .005 : .0005 : .025;
        if(normalizedRmse>limit) throw new InvalidOperationException($"LUT accuracy failed: normalized RMSE {normalizedRmse} exceeds {limit}.");
        return new {table.Label,table.H,table.E,table.Bmax,table.BuildMs,bytes=table.Data.Length*(table.Half ? 2 : 4),
            scalarCases=absolute.Count,normalizedRmse,relativeMedian=Median(relative),relativeP99=Percentile(relative,.99),
            maximumAbsolute=absolute.Max(),negativeDifferences,worst,
            worstRelative=relativeCases.OrderByDescending(x=>x.Error).Take(8).Select(x=>x.Details).ToArray()};
    }

    // Independent direct integration of the physical integrand. Adaptive Simpson
    // integrates each half of the angular domain, rather than reusing LUT cells,
    // the tail recurrence, or the LUT's four-point Gaussian integration.
    static double ReferenceAir(double c,double b,double end,double h,double e)
    {
        double height=Math.Sqrt(b*b+h*h),start=Math.Atan(-c/height);
        double finish=double.IsInfinity(end) ? Math.PI*.5 : Math.Atan((end-c)/height);
        double F(double theta)
        {
            if(theta>=Math.PI*.5)return 0;
            double t=Math.Max(0,c+height*Math.Tan(theta)),x=t-c,r2=x*x+b*b;
            double phase=3/(16*Math.PI)*(r2==0 ? 2 : 2-b*b/r2);
            return phase*Math.Exp(-e*(t+Math.Sqrt(r2)))/height;
        }
        double Part(double left,double right)
        {
            if(right<=left)return 0;
            double middle=.5*(left+right),fa=F(left),fm=F(middle),fb=F(right);
            return Adaptive(F,left,right,fa,fm,fb,(right-left)*(fa+4*fm+fb)/6,1e-11,22);
        }
        return Part(start,Math.Min(0,finish))+Part(Math.Max(0,start),finish);
    }
    static double Adaptive(Func<double,double> f,double a,double b,double fa,double fm,double fb,double whole,double tolerance,int depth)
    {
        double m=.5*(a+b),fl=f(.5*(a+m)),fr=f(.5*(m+b));
        double left=(m-a)*(fa+4*fl+fm)/6,right=(b-m)*(fm+4*fr+fb)/6,delta=left+right-whole;
        if(depth==0||Math.Abs(delta)<=15*tolerance)return left+right+delta/15;
        return Adaptive(f,a,m,fa,fl,fm,left,tolerance*.5,depth-1)+Adaptive(f,m,b,fm,fr,fb,right,tolerance*.5,depth-1);
    }

    sealed class LutGpuStudy : IDisposable
    {
        readonly Renderer renderer;readonly ID3D11DeviceContext context;
        readonly ID3D11PixelShader air,composite,surface;
        readonly ID3D11Buffer constants;readonly ID3D11SamplerState sampler;
        ID3D11ShaderResourceView? lut;
        internal LutGpuStudy(Renderer renderer,ID3D11Device device,ID3D11DeviceContext context,string source)
        {
            this.renderer=renderer;this.context=context;
            string expanded=ShaderSource.Read(source,"Scenes/MandelLightv4.hlsl")
                .Replace("cbuffer FrameConstants\n","cbuffer FrameConstants : register(b0)\n")
                .Replace("cbuffer FrameConstants\r\n","cbuffer FrameConstants : register(b0)\r\n")
                .Replace("Texture2DArray<float> RasterShadows;","Texture2DArray<float> RasterShadows : register(t0);")
                .Replace("SamplerComparisonState RasterShadowSampler;","SamplerComparisonState RasterShadowSampler : register(s13);")
                .Replace("Texture2D<float4> RasterSurface;","Texture2D<float4> RasterSurface : register(t1);")
                .Replace("Texture2D<float> RasterDepth;","Texture2D<float> RasterDepth : register(t2);")
                .Replace("Texture2D<float4> RasterAirlight;","Texture2D<float4> RasterAirlight : register(t3);")
                .Replace("float4 IntegrateAirlight(int2 pixel)","float4 SampledAirlight(int2 pixel)")
                .Replace("float3 radiance=UnshadowedAirlight(origin,direction,hit,end,scatter,extinction);","float3 radiance=0;")
                .Replace("int samples=1+(int)round(127*Quality.Samples);","int samples=StudySamples;")
                // Retain this historical soft-kernel experiment in its private
                // shader copy. The production renderer has no source radius.
                .Replace("source.Height=max(sqrt(source.Perpendicular2),NumericDistance);","source.Height=sqrt(source.Perpendicular2+StudyRadius*StudyRadius);")
                .Replace("falloff=rcp(max(r2,NumericDistance2));","falloff=rcp(r2+StudyRadius*StudyRadius);")
                .Replace("float3 delta=p-LightPosition(light);","if (StudyShadows==0) return 1;\n    float3 delta=p-LightPosition(light);");
            string code="""
                cbuffer StudyConstants : register(b4) {
                    int StudyMode; int StudySamples; int StudyShadows; float StudyBmax;
                    float StudyBwarp; float StudyScaled; float StudyRadius; float StudyReserved;
                };
                Texture2D<float4> StudyLut : register(t4);
                SamplerState StudySampler : register(s14);
                float4 IntegrateAirlight(int2 pixel);
                """+expanded+"\n"+File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"AirlightLut.hlsl"));
            air=device.CreatePixelShader(Compiler.Compile(code,"RasterAirlightPS","AirlightLut.hlsl","ps_5_0").Span);
            composite=device.CreatePixelShader(Compiler.Compile(code,"PS","AirlightLut.hlsl","ps_5_0").Span);
            surface=device.CreatePixelShader(Compiler.Compile(code,"RasterSurfacePS","AirlightLut.hlsl","ps_5_0").Span);
            constants=device.CreateBuffer(32,BindFlags.ConstantBuffer);
            sampler=device.CreateSamplerState(new SamplerDescription
            {
                Filter=Filter.MinMagLinearMipPoint,AddressU=TextureAddressMode.Clamp,AddressV=TextureAddressMode.Clamp,
                AddressW=TextureAddressMode.Clamp,MinLOD=0,MaxLOD=0,
            });
        }
        internal void Configure(int mode,int samples,bool shadows,TailTable table,ID3D11ShaderResourceView view)
        {
            lut=view;context.UpdateSubresource(new StudyConstants(mode,samples,shadows ? 1 : 0,(float)table.Bmax,(float)table.Bwarp,new(table.Scaled ? 1 : 0,(float)table.H,0)),constants);
        }
        internal void Override(string name,bool start)
        {
            if(!start || name is not ("Raster surface" or "Raster airlight" or "Raster composite"))return;
            object raster=Field<object>(renderer,"rasterTransport");
            context.PSSetShader(name=="Raster surface" ? surface : name=="Raster airlight" ? air : composite);
            context.PSSetConstantBuffer(0,Field<ID3D11Buffer>(renderer,"frameBuffer"));
            context.PSSetConstantBuffer(3,Field<ID3D11Buffer>(raster,"lights"));
            context.PSSetConstantBuffer(4,constants);
            context.PSSetShaderResource(0,Field<ID3D11ShaderResourceView>(raster,"shadowView"));
            if(name!="Raster surface")context.PSSetShaderResource(2,Field<ID3D11ShaderResourceView>(raster,"depthView"));
            if(name=="Raster composite")
            {
                context.PSSetShaderResource(1,Field<ID3D11ShaderResourceView>(raster,"colorView"));
                context.PSSetShaderResource(3,Field<ID3D11ShaderResourceView>(raster,"airlightView"));
            }
            context.PSSetShaderResource(4,lut!);
            context.PSSetSampler(13,Field<ID3D11SamplerState>(raster,"shadowSampler"));
            context.PSSetSampler(14,sampler);
        }
        public void Dispose(){surface.Dispose();composite.Dispose();air.Dispose();sampler.Dispose();constants.Dispose();}
    }
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct StudyConstants(int Mode,int Samples,int Shadows,float Bmax,float Bwarp,Vector3 Reserved);

    static float[] ReadAirlight(Renderer renderer,ID3D11Device device,ID3D11DeviceContext context)
    {
        object raster=Field<object>(renderer,"rasterTransport");
        var texture=Field<ID3D11Texture2D>(raster,"airlight");var desc=texture.Description;
        int width=(int)desc.Width,height=(int)desc.Height;
        desc.Usage=ResourceUsage.Staging;desc.BindFlags=BindFlags.None;desc.CPUAccessFlags=CpuAccessFlags.Read;
        using var staging=device.CreateTexture2D(desc);context.CopyResource(staging,texture);
        var map=context.Map(staging,0,MapMode.Read,global::Vortice.Direct3D11.MapFlags.None);
        try
        {
            bool half=desc.Format==Format.R16G16B16A16_Float;
            int stride=half ? 8 : 16;
            byte[] row=new byte[width*stride];var result=new float[width*height*3];
            for(int y=0;y<height;y++)
            {
                Marshal.Copy(map.DataPointer+y*(int)map.RowPitch,row,0,row.Length);
                for(int x=0;x<width;x++)for(int c=0;c<3;c++)result[3*(y*width+x)+c]=half
                    ? (float)BitConverter.ToHalf(row,stride*x+2*c) : BitConverter.ToSingle(row,stride*x+4*c);
            }
            return result;
        }
        finally {context.Unmap(staging,0);}
    }
    static object ImageError(float[] actual,float[] reference,byte[] display,byte[] expected)
    {
        double square=0,energy=0,displaySquare=0,bias=0,max=0;
        for(int i=0;i<actual.Length;i++)
        {
            if(!float.IsFinite(actual[i])||actual[i]<0)throw new InvalidOperationException("Invalid GPU airlight.");
            double d=actual[i]-reference[i];square+=d*d;energy+=(double)reference[i]*reference[i];bias+=d;max=Math.Max(max,Math.Abs(d));
        }
        for(int i=0;i<display.Length;i++)if(i%4!=3)displaySquare+=Math.Pow((display[i]-expected[i])/255.0,2);
        return new {hdrNormalizedRmse=Math.Sqrt(square/Math.Max(energy,1e-30)),hdrMeanBias=bias/actual.Length,
            hdrMaximumAbsolute=max,displayRgbRmse=Math.Sqrt(displaySquare/(display.Length*.75))};
    }
    static double HdrError(float[] actual,float[] reference)
    {
        double square=0,energy=0;
        for(int i=0;i<actual.Length;i++){double d=actual[i]-reference[i];square+=d*d;energy+=(double)reference[i]*reference[i];}
        return Math.Sqrt(square/Math.Max(energy,1e-30));
    }
}
