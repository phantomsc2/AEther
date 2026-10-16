using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using AEther;
using AEther.DftStudy;

CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
if(args.Contains("--pin"))
{
    var previous=Native.SetThreadAffinityMask(Native.GetCurrentThread(),1);
    if(previous==0)throw new System.ComponentModel.Win32Exception();
    Console.WriteLine($"Benchmark thread pinned to logical CPU 0; hybrid core type={(X86Base.CpuId(0x1a,0).Eax>>24)&255:X2}");
}
if(!Avx2.IsSupported||!Fma.IsSupported)throw new PlatformNotSupportedException("This study requires AVX2 and FMA; production needs portable dispatch.");
var cpu=new StringBuilder();for(int leaf=unchecked((int)0x80000002);leaf<=unchecked((int)0x80000004);leaf++)
{ var(a,b,c,d)=X86Base.CpuId(leaf,0);foreach(var word in new[]{a,b,c,d})cpu.Append(Encoding.ASCII.GetString(BitConverter.GetBytes(word))); }
Console.WriteLine($"CPU={cpu.ToString().Trim('\0',' ')}; runtime={Environment.Version}; OS={RuntimeInformation.OSDescription}; Vector<double>={Vector<double>.Count}; AVX2={Avx2.IsSupported}; FMA={Fma.IsSupported}; logicalCPUs={Environment.ProcessorCount}");
if(args.Contains("--verify"))Verify(args.Contains("--wide"));
if(args.Contains("--long"))LongRun();
if(args.Contains("--bench"))Bench(args.Contains("--extended"),args.Contains("--large"));

static List<Func<IBank>> Candidates(Domain d,DFTWindow w,int block)=>new()
{
    ()=>new Existing(d,w,false),()=>new Existing(d,w,true),()=>new CoefficientBank(d,w),
    ()=>new SpectrumBank(d,w,false,true,16),()=>new SpectrumBank(d,w,false,true,64),
    ()=>new SpectrumBank(d,w,false,true,block),()=>new SpectrumBank(d,w,true,true,block),
    ()=>new SpectrumBank(d,w,false,false,16),
    ()=>new SpectrumBank(d,w,false,false,16,true),
    ()=>new SpectrumBank(d,w,false,false,16,true,true),
};

static void Verify(bool wide)
{
    double maximum=0, directError=0, evidenceError=0, amplitudeError=0, estimatorError=0;
    int cases=0;
    foreach(var window in wide ? new[]{DFTWindow.Hann,DFTWindow.FlatTop}:Enum.GetValues<DFTWindow>())
    {
        var domain=wide ? new AnalyzerOptions().Domain : new Domain(110,12,19); // Deliberately not a multiple of SIMD width.
        var scalar=new Existing(domain,window,false);
        var banks=Candidates(domain,window,187).Select(f=>f()).ToArray();
        var evidence=banks.Select(_=>new Evidence(domain.Length,domain.Resolution,true)).ToArray();
        var observed=Candidates(domain,window,187).Select(f=>f()).ToArray();
        int m=(int)Math.Round(1/(Math.Pow(2,1.0/domain.Resolution)-1));
        int maxN=(int)Math.Round(m*48000/domain.MinValue);
        var random=new Random(71);
        var input=Enumerable.Range(0,3*maxN+37).Select(i=>.1+.3*Math.Sin(i*.173)+.05*(random.NextDouble()-.5)).ToArray();
        var reference=new Complex[domain.Length];var actual=new Complex[domain.Length];
        int offset=0;
        while(offset<input.Length)
        {
            int count=Math.Min(new[]{1,73,187,1024,19}[cases%5],input.Length-offset);
            var slice=input.AsMemory(offset,count);scalar.Process(slice,null);scalar.Read(reference);
            for(int b=0;b<banks.Length;b++)
            {
                banks[b].Process(slice,null);banks[b].Read(actual);
                for(int k=0;k<actual.Length;k++)maximum=Math.Max(maximum,Complex.Abs(actual[k]-reference[k]));
                observed[b].Process(slice,evidence[b]);observed[b].Read(actual);
                for(int k=0;k<actual.Length;k++)maximum=Math.Max(maximum,Complex.Abs(actual[k]-reference[k]));
                evidenceError=Math.Max(evidenceError,Math.Abs(evidence[b].Checksum-evidence[0].Checksum));
                estimatorError=Math.Max(estimatorError,evidence[b].Difference(evidence[0]));
                for(int n=0;n<count;n++)for(int k=0;k<domain.Length;k++)
                    amplitudeError=Math.Max(amplitudeError,Math.Abs(evidence[b].Amplitude(n,k)-evidence[0].Amplitude(n,k)));
            }
            offset+=count;cases++;
        }
        var cosines=DFTProcessor.GetWindowCosines(window);
        foreach(int k in new[]{0,domain.Length/2,domain.Length-1})
        {
            int length=(int)Math.Round(m*48000/domain.ElementAt(k));var sum=Complex.Zero;
            for(int age=0;age<length;age++)
            {
                double weight=cosines[0];for(int j=1;j<cosines.Length;j++)weight+=(j%2==0?1:-1)*cosines[j]*Math.Cos(2*Math.PI*j*age/length);
                sum+=input[input.Length-1-age]*weight*Complex.FromPolarCoordinates(1,2*Math.PI*m*age/length);
            }
            directError=Math.Max(directError,Complex.Abs(sum/(length*cosines[0])-reference[k]));
        }
        // No reset: retained state must decay after the finite window has emptied.
        var zeros=new double[187];for(int n=0;n<2*maxN;n+=187)foreach(var bank in banks)bank.Process(zeros,null);
        foreach(var bank in banks){bank.Read(actual);if(actual.Any(x=>x.Magnitude>1e-8))throw new Exception($"Silence failed: {bank.Name}, {window}");}
        Console.WriteLine($"Verified {window}: irregular chunks, padded lanes, shared-ring wrap, per-sample observations, silence");
    }
    if(maximum>1e-9||directError>1e-9||amplitudeError>1e-10||evidenceError>1e-3||estimatorError>1e-3)throw new Exception($"Parity failed {maximum:E3}, {directError:E3}, evidence {evidenceError:E3}");
    Console.WriteLine($"VERIFY PASS checkpoints={cases}; max complex parity={maximum:E6}; max direct reference={directError:E6}; max amplitude error={amplitudeError:E6}; max individual estimator difference={estimatorError:E6}; max evidence checksum difference={evidenceError:E6}");
}

static void Bench(bool extended,bool large)
{
    var options=new AnalyzerOptions();var domain=options.Domain;
    var rows=new List<string>{"window,bands,block,workload,backend,median_us_stereo,min_us_stereo,max_us_stereo,allocated_bytes_per_call"};
    foreach(var window in new[]{DFTWindow.Hann,DFTWindow.Blackman,DFTWindow.Nuttall,DFTWindow.FlatTop})
    foreach(var workload in large ? new[]{"dft","time","joint"} : new[]{"dft","exact","time","joint"})
    {
        int block=large?1024:187;
        var random=new Random(123);var input=Enumerable.Range(0,32).Select(_=>Enumerable.Range(0,block).Select(_=>.3*(random.NextDouble()-.5)).ToArray()).ToArray();
        var factories=Candidates(domain,window,block);
        if(extended)factories=factories.Where((_,i)=>i<3||i>=8).Concat(new[]{4,8,16,32,64,187}.SelectMany(t=>new Func<IBank>[] {()=>new SpectrumBank(domain,window,false,true,t),()=>new SpectrumBank(domain,window,true,true,t)})).ToList();
        if(large)factories=new List<Func<IBank>> {()=>new Existing(domain,window,true),()=>new CoefficientBank(domain,window),()=>new SpectrumBank(domain,window,false,false,16,true),()=>new SpectrumBank(domain,window,false,false,16,true,true),()=>new SpectrumBank(domain,window,false,true,16),()=>new SpectrumBank(domain,window,false,true,64),()=>new SpectrumBank(domain,window,true,true,64)};
        foreach(var factory in factories)
        {
            var banks=new[]{factory(),factory()};
            var ev=workload is "time" or "joint" ? new[]{new Evidence(domain.Length,domain.Resolution,workload=="joint"),new Evidence(domain.Length,domain.Resolution,workload=="joint")}:null;
            var split=workload=="exact" ? new[]{new Splitter(domain,options,48000.0/block),new Splitter(domain,options,48000.0/block)}:null;
            var complex=new Complex[domain.Length];var spectrum=new double[domain.Length];var output=new double[domain.Length*4];
            int frame=0;
            void Step()
            {
                for(int c=0;c<2;c++)
                {
                    banks[c].Process(input[frame&31],ev?[c]);
                    banks[c].Read(complex);
                    if(split is not null)
                    {
                        for(int k=0;k<spectrum.Length;k++)spectrum[k]=Math.Log10(Math.Max(1e-10,2*complex[k].Magnitude));
                        split[c].Process(spectrum,output);
                    }
                }
                frame++;
            }
            for(int n=0;n<Math.Max(40,(int)Math.Ceiling(37400.0/block));n++)Step();
            var timer=Stopwatch.StartNew();for(int n=0;n<20;n++)Step();
            int iterations=Math.Clamp((int)(60/Math.Max(.001,timer.Elapsed.TotalMilliseconds/20)),20,4000);
            var times=new double[3];long allocated=0;
            for(int round=0;round<3;round++)
            {
                long before=GC.GetAllocatedBytesForCurrentThread();timer.Restart();for(int n=0;n<iterations;n++)Step();timer.Stop();
                allocated+=GC.GetAllocatedBytesForCurrentThread()-before;times[round]=timer.Elapsed.TotalMicroseconds/iterations;
            }
            Array.Sort(times);
            var row=$"{window},{domain.Length},{block},{workload},{banks[0].Name},{times[1]:F3},{times[0]:F3},{times[2]:F3},{allocated/(3.0*iterations):F1}";
            rows.Add(row);Console.WriteLine(row);
        }
    }
    File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"results.csv"),rows);
}

static void LongRun()
{
    double maxError=0,maxSilence=0;
    foreach(var window in new[]{DFTWindow.Hann,DFTWindow.Blackman,DFTWindow.Nuttall,DFTWindow.FlatTop})
    {
        var domain=new Domain(441.37,12,1);
        var banks=Candidates(domain,window,187).Select(f=>f()).ToArray();
        var input=Enumerable.Range(0,1024).Select(n=>.1+.3*Math.Sin(.173*n)).ToArray();
        for(int n=0;n<1_000_000;n+=1024)foreach(var b in banks)b.Process(input.AsMemory(0,Math.Min(1024,1_000_000-n)),null);
        var expected=new Complex[1];banks[0].Read(expected);var actual=new Complex[1];
        foreach(var b in banks){b.Read(actual);maxError=Math.Max(maxError,Complex.Abs(actual[0]-expected[0]));}
        var zeros=new double[1024];for(int i=0;i<4;i++)foreach(var b in banks)b.Process(zeros,null);
        foreach(var b in banks){b.Read(actual);maxSilence=Math.Max(maxSilence,actual[0].Magnitude);}
    }
    if(maxError>1e-8||maxSilence>1e-8)throw new Exception($"Long run failed: {maxError:E6}, silence {maxSilence:E6}");
    Console.WriteLine($"LONG PASS: 1M samples per backend/window; max parity={maxError:E6}; max retained magnitude after 4096 zeros={maxSilence:E6}");
}

static class Native
{
    [DllImport("kernel32.dll")] internal static extern nint GetCurrentThread();
    [DllImport("kernel32.dll",SetLastError=true)] internal static extern nuint SetThreadAffinityMask(nint thread,nuint mask);
}
