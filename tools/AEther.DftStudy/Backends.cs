using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using AEther;
using V = System.Runtime.Intrinsics.Vector256<double>;

namespace AEther.DftStudy;

interface IBank
{
    string Name { get; }
    int Bands { get; }
    void Process(ReadOnlyMemory<double> samples, Evidence? evidence);
    void Read(Span<Complex> output);
}

sealed class Existing : IBank
{
    readonly IDFTFilter[] filters;
    public string Name { get; }
    public int Bands => filters.Length;
    public Existing(Domain domain, DFTWindow window, bool simd)
    {
        Name = simd ? "current-auto" : "scalar";
        var weights = DFTProcessor.CreateWindow(DFTProcessor.GetWindowCosines(window));
        filters = domain.Select(f => simd && weights.Length <= Vector<double>.Count && weights.Length > 1
            ? (IDFTFilter)new WindowedDFTFilterSIMD(f, domain.Resolution, 48000, weights)
            : new WindowedDFTFilter(f, domain.Resolution, 48000, weights)).ToArray();
    }
    public void Process(ReadOnlyMemory<double> samples, Evidence? evidence)
    {
        if (evidence is null) { foreach (var f in filters) f.Process(samples); return; }
        for (int n = 0; n < samples.Length; n++)
        {
            for (int k = 0; k < Bands; k++)
            {
                filters[k].Process(samples.Span[n]);
                evidence.Set(n, k, 2 * filters[k].GetOutput().Magnitude);
            }
        }
        evidence.Advance(samples.Length);
    }
    public void Read(Span<Complex> output) { for(int k=0;k<Bands;k++)output[k]=filters[k].GetOutput(); }
}

// Multiple coefficient vectors per frequency; no shared audio buffer.
sealed class CoefficientBank : IBank
{
    readonly CoefficientFilter[] filters;
    public string Name => "coefficient-multi";
    public int Bands => filters.Length;
    public CoefficientBank(Domain domain, DFTWindow window)
    {
        var weights = DFTProcessor.CreateWindow(DFTProcessor.GetWindowCosines(window));
        filters = domain.Select(f => new CoefficientFilter(f, domain.Resolution, weights)).ToArray();
    }
    public void Process(ReadOnlyMemory<double> samples, Evidence? evidence)
    {
        for (int k = 0; k < Bands; k++) filters[k].Process(samples.Span, evidence, k);
        evidence?.Advance(samples.Length);
    }
    public void Read(Span<Complex> output) { for(int k=0;k<Bands;k++)output[k]=filters[k].Output(); }

    sealed class CoefficientFilter
    {
        readonly double[] ring;
        readonly int components, groups;
        readonly V[] re = new V[3], im = new V[3], cr = new V[3], ci = new V[3], weights = new V[3];
        readonly double normalization;
        int position;
        public CoefficientFilter(double frequency, int resolution, double[] window)
        {
            int m = (int)Math.Round(1 / (Math.Pow(2, 1.0 / resolution) - 1));
            ring = new double[(int)Math.Round(m * 48000 / frequency)];
            components = window.Length; groups = (components + 3) / 4;
            normalization = ring.Length * window[components / 2];
            for (int g = 0; g < groups; g++)
            {
                double[] a = new double[4], b = new double[4], w = new double[4];
                for (int l = 0; l < 4 && 4 * g + l < components; l++)
                {
                    int j = 4 * g + l;
                    var coefficient = Complex.FromPolarCoordinates(1, 2 * Math.PI * (m - components / 2 + j) / ring.Length);
                    a[l] = coefficient.Real; b[l] = coefficient.Imaginary; w[l] = window[j];
                }
                cr[g] = Vector256.Create(a[0],a[1],a[2],a[3]); ci[g] = Vector256.Create(b[0],b[1],b[2],b[3]);
                weights[g] = Vector256.Create(w[0],w[1],w[2],w[3]);
            }
        }
        public void Process(ReadOnlySpan<double> samples, Evidence? evidence, int band)
        {
            var r0=re[0]; var i0=im[0]; var r1=re[1]; var i1=im[1]; var r2=re[2]; var i2=im[2];
            var a0=cr[0]; var b0=ci[0]; var a1=cr[1]; var b1=ci[1]; var a2=cr[2]; var b2=ci[2];
            for (int n=0;n<samples.Length;n++)
            {
                var delta=Vector256.Create(samples[n]-ring[position]); ring[position]=samples[n];
                if (++position==ring.Length) position=0;
                var next=r0*a0-i0*b0+delta; i0=r0*b0+i0*a0; r0=next;
                if(groups>1) { next=r1*a1-i1*b1+delta; i1=r1*b1+i1*a1; r1=next; }
                if(groups>2) { next=r2*a2-i2*b2+delta; i2=r2*b2+i2*a2; r2=next; }
                if(evidence is not null)
                {
                    double r=Vector256.Sum(r0*weights[0]), i=Vector256.Sum(i0*weights[0]);
                    if(groups>1) { r+=Vector256.Sum(r1*weights[1]); i+=Vector256.Sum(i1*weights[1]); }
                    if(groups>2) { r+=Vector256.Sum(r2*weights[2]); i+=Vector256.Sum(i2*weights[2]); }
                    evidence.Set(n,band,2*Math.Sqrt(r*r+i*i)/normalization);
                }
            }
            re[0]=r0;im[0]=i0;re[1]=r1;im[1]=i1;re[2]=r2;im[2]=i2;
        }
        public Complex Output()
        {
            double r=0,i=0;
            for(int j=0;j<components;j++) { r+=re[j/4].GetElement(j%4)*weights[j/4].GetElement(j%4); i+=im[j/4].GetElement(j%4)*weights[j/4].GetElement(j%4); }
            return new Complex(r,i)/normalization;
        }
    }
}

// Shared history, four frequency bands per vector; row staging permits coefficient/time tiling.
sealed partial class SpectrumBank : IBank
{
    readonly int bands, groups, taps, tile;
    readonly bool gather, tapOuter, unrolled, fma;
    readonly double[] ring, weights;
    readonly int mask;
    readonly Vector128<int>[] delays;
    readonly V[] re, im, cr, ci, delta, sumsRe, sumsIm;
    readonly V[] normalization;
    int position;
    public int Bands => bands;
    public string Name => unrolled ? $"bins-unrolled-{(fma?"fma":"mul")}-t{tile}" : $"bins-{(tapOuter ? "tap" : "sample")}-{(gather ? "gather" : "loads")}-t{tile}";
    public SpectrumBank(Domain domain, DFTWindow window, bool gather, bool tapOuter, int tile, bool unrolled=false, bool fma=false)
    {
        bands=domain.Length;groups=(bands+3)/4;this.gather=gather;this.tapOuter=tapOuter;this.tile=tile;this.unrolled=unrolled;this.fma=fma;
        weights=DFTProcessor.CreateWindow(DFTProcessor.GetWindowCosines(window));taps=weights.Length;
        int m=(int)Math.Round(1/(Math.Pow(2,1.0/domain.Resolution)-1));
        var lengths=domain.Select(f=>(int)Math.Round(m*48000/f)).ToArray();
        int capacity=1;while(capacity<lengths.Max()+1024)capacity*=2;
        ring=new double[capacity];mask=capacity-1;
        delays=new Vector128<int>[groups];normalization=new V[groups];
        re=new V[groups*taps];im=new V[re.Length];cr=new V[re.Length];ci=new V[re.Length];
        delta=new V[1024];sumsRe=new V[1024];sumsIm=new V[1024];
        for(int g=0;g<groups;g++)
        {
            var n=Enumerable.Range(0,4).Select(l=>lengths[Math.Min(4*g+l,bands-1)]).ToArray();
            delays[g]=Vector128.Create(n[0],n[1],n[2],n[3]);
            normalization[g]=Vector256.Create(n[0]*weights[taps/2],n[1]*weights[taps/2],n[2]*weights[taps/2],n[3]*weights[taps/2]);
            for(int j=0;j<taps;j++)
            {
                var c=n.Select(v=>Complex.FromPolarCoordinates(1,2*Math.PI*(m-taps/2+j)/v)).ToArray();
                cr[g*taps+j]=Vector256.Create(c[0].Real,c[1].Real,c[2].Real,c[3].Real);
                ci[g*taps+j]=Vector256.Create(c[0].Imaginary,c[1].Imaginary,c[2].Imaginary,c[3].Imaginary);
            }
        }
    }
    unsafe V Difference(double incoming,int absolute,int group)
    {
        var indices=(Vector128.Create(absolute)-delays[group]) & Vector128.Create(mask);
        V outgoing;
        if(gather) { fixed(double* p=ring) outgoing=Avx2.GatherVector256(p,indices,8); }
        else outgoing=Vector256.Create(ring[indices.GetElement(0)],ring[indices.GetElement(1)],ring[indices.GetElement(2)],ring[indices.GetElement(3)]);
        return Vector256.Create(incoming)-outgoing;
    }
    public void Process(ReadOnlyMemory<double> samples,Evidence? evidence)
    {
        if(samples.Length>1024)throw new ArgumentOutOfRangeException(nameof(samples));
        int start=position;
        for(int n=0;n<samples.Length;n++)ring[(start+n)&mask]=samples.Span[n];
        position=(position+samples.Length)&mask;
        for(int begin=0;begin<samples.Length;begin+=tile)
        {
            int count=Math.Min(tile,samples.Length-begin);
            for(int g=0;g<groups;g++)
            {
                for(int n=0;n<count;n++)delta[n]=Difference(samples.Span[begin+n],start+begin+n,g);
                if(unrolled)
                {
                    switch(taps) { case 1: Unroll1(g,begin,count,evidence);break; case 3: Unroll3(g,begin,count,evidence);break;case 5: Unroll5(g,begin,count,evidence);break;case 7: Unroll7(g,begin,count,evidence);break;case 9: Unroll9(g,begin,count,evidence);break; }
                }
                else if(tapOuter)
                {
                    if(evidence is not null) { Array.Clear(sumsRe,0,count);Array.Clear(sumsIm,0,count); }
                    for(int j=0;j<taps;j++)
                    {
                        int index=g*taps+j;var r=re[index];var i=im[index];var a=cr[index];var b=ci[index];var w=Vector256.Create(weights[j]);
                        for(int n=0;n<count;n++)
                        {
                            var next=a*r-b*i+delta[n];i=a*i+b*r;r=next;
                            if(evidence is not null) { sumsRe[n]+=w*r;sumsIm[n]+=w*i; }
                        }
                        re[index]=r;im[index]=i;
                    }
                    if(evidence is not null)for(int n=0;n<count;n++)
                    {
                        var r=sumsRe[n];var i=sumsIm[n];
                        evidence.SetVector(begin+n,g,Vector256.Create(2d)*Vector256.Sqrt(r*r+i*i)/normalization[g]);
                    }
                }
                else
                {
                    for(int n=0;n<count;n++)
                    {
                        var sumR=V.Zero;var sumI=V.Zero;
                        for(int j=0;j<taps;j++)
                        {
                            int index=g*taps+j;var r=re[index];var i=im[index];var a=cr[index];var b=ci[index];
                            var next=a*r-b*i+delta[n];i=a*i+b*r;r=next;re[index]=r;im[index]=i;
                            if(evidence is not null){sumR+=Vector256.Create(weights[j])*r;sumI+=Vector256.Create(weights[j])*i;}
                        }
                        if(evidence is not null)evidence.SetVector(begin+n,g,Vector256.Create(2d)*Vector256.Sqrt(sumR*sumR+sumI*sumI)/normalization[g]);
                    }
                }
            }
            evidence?.Advance(count,begin);
        }
    }
    public void Read(Span<Complex> output)
    {
        for(int k=0;k<bands;k++)
        {
            double r=0,i=0;
            for(int j=0;j<taps;j++){r+=weights[j]*re[k/4*taps+j].GetElement(k%4);i+=weights[j]*im[k/4*taps+j].GetElement(k%4);}
            output[k]=new Complex(r,i)/normalization[k/4].GetElement(k%4);
        }
    }
}

sealed class Evidence
{
    public readonly double[] Amplitudes;
    public readonly double[] Time;
    readonly V[] joint, inverseWidths;
    readonly V[] masks;
    readonly int bands,groups,radius,stride;
    readonly bool includeJoint;
    const double Step=1.0/48000;
    public double Checksum;
    public double Amplitude(int sample,int band)=>Amplitudes[sample*stride+radius+band];
    public double Difference(Evidence other)
    {
        double error=0;
        for(int k=0;k<bands;k++)
        {
            error=Math.Max(error,Math.Abs(Time[k]-other.Time[k]));
            error=Math.Max(error,Math.Abs(joint[k/4].GetElement(k%4)-other.joint[k/4].GetElement(k%4)));
        }
        return error;
    }
    public Evidence(int bands,int resolution,bool includeJoint)
    {
        this.bands=bands;groups=(bands+3)/4;radius=((int)Math.Round(3.0*resolution)|1)/2;
        stride=4*groups+2*radius+4;this.includeJoint=includeJoint;
        Amplitudes=new double[stride*1024];Time=new double[4*groups];joint=new V[groups];inverseWidths=new V[groups];
        masks=new V[groups*(2*radius+1)];
        for(int g=0;g<groups;g++)
        {
            var widths=new double[4];
            for(int l=0;l<4;l++) { int k=4*g+l; widths[l]=k<bands ? 1.0/(Math.Min(bands-1,k+radius)-Math.Max(0,k-radius)+1):0; }
            inverseWidths[g]=Vector256.Create(widths[0],widths[1],widths[2],widths[3]);
            for(int dx=-radius;dx<=radius;dx++)
            {
                var valid=Enumerable.Range(0,4).Select(l=>4*g+l<bands&&4*g+l+dx>=0&&4*g+l+dx<bands?1d:0d).ToArray();
                masks[g*(2*radius+1)+dx+radius]=Vector256.Create(valid[0],valid[1],valid[2],valid[3]);
            }
        }
    }
    public void Set(int sample,int band,double amplitude)
    {
        Amplitudes[sample*stride+radius+band]=amplitude;
        Time[band]+=Step*Math.Sign(amplitude-Time[band]);
    }
    public void SetVector(int sample,int group,V amplitude)
    {
        amplitude.CopyTo(Amplitudes.AsSpan(sample*stride+radius+4*group,4));
        var q=Vector256.LoadUnsafe(ref Time[4*group]);
        var sign=Vector256.ConditionalSelect(Vector256.GreaterThan(amplitude,q),Vector256.Create(1d),V.Zero)
            -Vector256.ConditionalSelect(Vector256.LessThan(amplitude,q),Vector256.Create(1d),V.Zero);
        (q+Vector256.Create(Step)*sign).CopyTo(Time.AsSpan(4*group,4));
    }
    public void Advance(int count,int begin=0)
    {
        if(includeJoint)for(int n=begin;n<begin+count;n++)for(int g=0;g<groups;g++)
        {
            var q=joint[g];var total=V.Zero;
            for(int dx=-radius;dx<=radius;dx++)
            {
                var x=Vector256.LoadUnsafe(ref Amplitudes[n*stride+radius+4*g+dx]);
                var sign=Vector256.ConditionalSelect(Vector256.GreaterThan(x,q),Vector256.Create(1d),V.Zero)
                    -Vector256.ConditionalSelect(Vector256.LessThan(x,q),Vector256.Create(1d),V.Zero);
                total+=sign*masks[g*(2*radius+1)+dx+radius];
            }
            joint[g]=Vector256.Max(V.Zero,q+Vector256.Create(Step)*total*inverseWidths[g]);
        }
        Checksum=Time[0]+Vector256.Sum(joint[0]);
    }
}
