using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using V=System.Runtime.Intrinsics.Vector256<double>;
namespace AEther.DftStudy;
sealed partial class SpectrumBank
{
    void Unroll1(int g,int begin,int count,Evidence? evidence)
    {
        int index=g*taps;
        var r0=re[index+0];var i0=im[index+0];
        for(int n=0;n<count;n++)
        {
            var d=delta[n];
            { var a=cr[index+0];var b=ci[index+0];var next=fma?Fma.MultiplyAdd(a,r0,Fma.MultiplyAdd(-b,i0,d)):a*r0-b*i0+d; i0=fma?Fma.MultiplyAdd(a,i0,b*r0):a*i0+b*r0; r0=next; }
            if(evidence is not null)
            {
                var sr=V.Zero;var si=V.Zero;
                sr+=Vector256.Create(weights[0])*r0;si+=Vector256.Create(weights[0])*i0;
                evidence.SetVector(begin+n,g,Vector256.Create(2d)*Vector256.Sqrt(sr*sr+si*si)/normalization[g]);
            }
        }
        re[index+0]=r0;im[index+0]=i0;
    }
    void Unroll3(int g,int begin,int count,Evidence? evidence)
    {
        int index=g*taps;
        var r0=re[index+0];var i0=im[index+0];
        var r1=re[index+1];var i1=im[index+1];
        var r2=re[index+2];var i2=im[index+2];
        for(int n=0;n<count;n++)
        {
            var d=delta[n];
            { var a=cr[index+0];var b=ci[index+0];var next=fma?Fma.MultiplyAdd(a,r0,Fma.MultiplyAdd(-b,i0,d)):a*r0-b*i0+d; i0=fma?Fma.MultiplyAdd(a,i0,b*r0):a*i0+b*r0; r0=next; }
            { var a=cr[index+1];var b=ci[index+1];var next=fma?Fma.MultiplyAdd(a,r1,Fma.MultiplyAdd(-b,i1,d)):a*r1-b*i1+d; i1=fma?Fma.MultiplyAdd(a,i1,b*r1):a*i1+b*r1; r1=next; }
            { var a=cr[index+2];var b=ci[index+2];var next=fma?Fma.MultiplyAdd(a,r2,Fma.MultiplyAdd(-b,i2,d)):a*r2-b*i2+d; i2=fma?Fma.MultiplyAdd(a,i2,b*r2):a*i2+b*r2; r2=next; }
            if(evidence is not null)
            {
                var sr=V.Zero;var si=V.Zero;
                sr+=Vector256.Create(weights[0])*r0;si+=Vector256.Create(weights[0])*i0;
                sr+=Vector256.Create(weights[1])*r1;si+=Vector256.Create(weights[1])*i1;
                sr+=Vector256.Create(weights[2])*r2;si+=Vector256.Create(weights[2])*i2;
                evidence.SetVector(begin+n,g,Vector256.Create(2d)*Vector256.Sqrt(sr*sr+si*si)/normalization[g]);
            }
        }
        re[index+0]=r0;im[index+0]=i0;
        re[index+1]=r1;im[index+1]=i1;
        re[index+2]=r2;im[index+2]=i2;
    }
    void Unroll5(int g,int begin,int count,Evidence? evidence)
    {
        int index=g*taps;
        var r0=re[index+0];var i0=im[index+0];
        var r1=re[index+1];var i1=im[index+1];
        var r2=re[index+2];var i2=im[index+2];
        var r3=re[index+3];var i3=im[index+3];
        var r4=re[index+4];var i4=im[index+4];
        for(int n=0;n<count;n++)
        {
            var d=delta[n];
            { var a=cr[index+0];var b=ci[index+0];var next=fma?Fma.MultiplyAdd(a,r0,Fma.MultiplyAdd(-b,i0,d)):a*r0-b*i0+d; i0=fma?Fma.MultiplyAdd(a,i0,b*r0):a*i0+b*r0; r0=next; }
            { var a=cr[index+1];var b=ci[index+1];var next=fma?Fma.MultiplyAdd(a,r1,Fma.MultiplyAdd(-b,i1,d)):a*r1-b*i1+d; i1=fma?Fma.MultiplyAdd(a,i1,b*r1):a*i1+b*r1; r1=next; }
            { var a=cr[index+2];var b=ci[index+2];var next=fma?Fma.MultiplyAdd(a,r2,Fma.MultiplyAdd(-b,i2,d)):a*r2-b*i2+d; i2=fma?Fma.MultiplyAdd(a,i2,b*r2):a*i2+b*r2; r2=next; }
            { var a=cr[index+3];var b=ci[index+3];var next=fma?Fma.MultiplyAdd(a,r3,Fma.MultiplyAdd(-b,i3,d)):a*r3-b*i3+d; i3=fma?Fma.MultiplyAdd(a,i3,b*r3):a*i3+b*r3; r3=next; }
            { var a=cr[index+4];var b=ci[index+4];var next=fma?Fma.MultiplyAdd(a,r4,Fma.MultiplyAdd(-b,i4,d)):a*r4-b*i4+d; i4=fma?Fma.MultiplyAdd(a,i4,b*r4):a*i4+b*r4; r4=next; }
            if(evidence is not null)
            {
                var sr=V.Zero;var si=V.Zero;
                sr+=Vector256.Create(weights[0])*r0;si+=Vector256.Create(weights[0])*i0;
                sr+=Vector256.Create(weights[1])*r1;si+=Vector256.Create(weights[1])*i1;
                sr+=Vector256.Create(weights[2])*r2;si+=Vector256.Create(weights[2])*i2;
                sr+=Vector256.Create(weights[3])*r3;si+=Vector256.Create(weights[3])*i3;
                sr+=Vector256.Create(weights[4])*r4;si+=Vector256.Create(weights[4])*i4;
                evidence.SetVector(begin+n,g,Vector256.Create(2d)*Vector256.Sqrt(sr*sr+si*si)/normalization[g]);
            }
        }
        re[index+0]=r0;im[index+0]=i0;
        re[index+1]=r1;im[index+1]=i1;
        re[index+2]=r2;im[index+2]=i2;
        re[index+3]=r3;im[index+3]=i3;
        re[index+4]=r4;im[index+4]=i4;
    }
    void Unroll7(int g,int begin,int count,Evidence? evidence)
    {
        int index=g*taps;
        var r0=re[index+0];var i0=im[index+0];
        var r1=re[index+1];var i1=im[index+1];
        var r2=re[index+2];var i2=im[index+2];
        var r3=re[index+3];var i3=im[index+3];
        var r4=re[index+4];var i4=im[index+4];
        var r5=re[index+5];var i5=im[index+5];
        var r6=re[index+6];var i6=im[index+6];
        for(int n=0;n<count;n++)
        {
            var d=delta[n];
            { var a=cr[index+0];var b=ci[index+0];var next=fma?Fma.MultiplyAdd(a,r0,Fma.MultiplyAdd(-b,i0,d)):a*r0-b*i0+d; i0=fma?Fma.MultiplyAdd(a,i0,b*r0):a*i0+b*r0; r0=next; }
            { var a=cr[index+1];var b=ci[index+1];var next=fma?Fma.MultiplyAdd(a,r1,Fma.MultiplyAdd(-b,i1,d)):a*r1-b*i1+d; i1=fma?Fma.MultiplyAdd(a,i1,b*r1):a*i1+b*r1; r1=next; }
            { var a=cr[index+2];var b=ci[index+2];var next=fma?Fma.MultiplyAdd(a,r2,Fma.MultiplyAdd(-b,i2,d)):a*r2-b*i2+d; i2=fma?Fma.MultiplyAdd(a,i2,b*r2):a*i2+b*r2; r2=next; }
            { var a=cr[index+3];var b=ci[index+3];var next=fma?Fma.MultiplyAdd(a,r3,Fma.MultiplyAdd(-b,i3,d)):a*r3-b*i3+d; i3=fma?Fma.MultiplyAdd(a,i3,b*r3):a*i3+b*r3; r3=next; }
            { var a=cr[index+4];var b=ci[index+4];var next=fma?Fma.MultiplyAdd(a,r4,Fma.MultiplyAdd(-b,i4,d)):a*r4-b*i4+d; i4=fma?Fma.MultiplyAdd(a,i4,b*r4):a*i4+b*r4; r4=next; }
            { var a=cr[index+5];var b=ci[index+5];var next=fma?Fma.MultiplyAdd(a,r5,Fma.MultiplyAdd(-b,i5,d)):a*r5-b*i5+d; i5=fma?Fma.MultiplyAdd(a,i5,b*r5):a*i5+b*r5; r5=next; }
            { var a=cr[index+6];var b=ci[index+6];var next=fma?Fma.MultiplyAdd(a,r6,Fma.MultiplyAdd(-b,i6,d)):a*r6-b*i6+d; i6=fma?Fma.MultiplyAdd(a,i6,b*r6):a*i6+b*r6; r6=next; }
            if(evidence is not null)
            {
                var sr=V.Zero;var si=V.Zero;
                sr+=Vector256.Create(weights[0])*r0;si+=Vector256.Create(weights[0])*i0;
                sr+=Vector256.Create(weights[1])*r1;si+=Vector256.Create(weights[1])*i1;
                sr+=Vector256.Create(weights[2])*r2;si+=Vector256.Create(weights[2])*i2;
                sr+=Vector256.Create(weights[3])*r3;si+=Vector256.Create(weights[3])*i3;
                sr+=Vector256.Create(weights[4])*r4;si+=Vector256.Create(weights[4])*i4;
                sr+=Vector256.Create(weights[5])*r5;si+=Vector256.Create(weights[5])*i5;
                sr+=Vector256.Create(weights[6])*r6;si+=Vector256.Create(weights[6])*i6;
                evidence.SetVector(begin+n,g,Vector256.Create(2d)*Vector256.Sqrt(sr*sr+si*si)/normalization[g]);
            }
        }
        re[index+0]=r0;im[index+0]=i0;
        re[index+1]=r1;im[index+1]=i1;
        re[index+2]=r2;im[index+2]=i2;
        re[index+3]=r3;im[index+3]=i3;
        re[index+4]=r4;im[index+4]=i4;
        re[index+5]=r5;im[index+5]=i5;
        re[index+6]=r6;im[index+6]=i6;
    }
    void Unroll9(int g,int begin,int count,Evidence? evidence)
    {
        int index=g*taps;
        var r0=re[index+0];var i0=im[index+0];
        var r1=re[index+1];var i1=im[index+1];
        var r2=re[index+2];var i2=im[index+2];
        var r3=re[index+3];var i3=im[index+3];
        var r4=re[index+4];var i4=im[index+4];
        var r5=re[index+5];var i5=im[index+5];
        var r6=re[index+6];var i6=im[index+6];
        var r7=re[index+7];var i7=im[index+7];
        var r8=re[index+8];var i8=im[index+8];
        for(int n=0;n<count;n++)
        {
            var d=delta[n];
            { var a=cr[index+0];var b=ci[index+0];var next=fma?Fma.MultiplyAdd(a,r0,Fma.MultiplyAdd(-b,i0,d)):a*r0-b*i0+d; i0=fma?Fma.MultiplyAdd(a,i0,b*r0):a*i0+b*r0; r0=next; }
            { var a=cr[index+1];var b=ci[index+1];var next=fma?Fma.MultiplyAdd(a,r1,Fma.MultiplyAdd(-b,i1,d)):a*r1-b*i1+d; i1=fma?Fma.MultiplyAdd(a,i1,b*r1):a*i1+b*r1; r1=next; }
            { var a=cr[index+2];var b=ci[index+2];var next=fma?Fma.MultiplyAdd(a,r2,Fma.MultiplyAdd(-b,i2,d)):a*r2-b*i2+d; i2=fma?Fma.MultiplyAdd(a,i2,b*r2):a*i2+b*r2; r2=next; }
            { var a=cr[index+3];var b=ci[index+3];var next=fma?Fma.MultiplyAdd(a,r3,Fma.MultiplyAdd(-b,i3,d)):a*r3-b*i3+d; i3=fma?Fma.MultiplyAdd(a,i3,b*r3):a*i3+b*r3; r3=next; }
            { var a=cr[index+4];var b=ci[index+4];var next=fma?Fma.MultiplyAdd(a,r4,Fma.MultiplyAdd(-b,i4,d)):a*r4-b*i4+d; i4=fma?Fma.MultiplyAdd(a,i4,b*r4):a*i4+b*r4; r4=next; }
            { var a=cr[index+5];var b=ci[index+5];var next=fma?Fma.MultiplyAdd(a,r5,Fma.MultiplyAdd(-b,i5,d)):a*r5-b*i5+d; i5=fma?Fma.MultiplyAdd(a,i5,b*r5):a*i5+b*r5; r5=next; }
            { var a=cr[index+6];var b=ci[index+6];var next=fma?Fma.MultiplyAdd(a,r6,Fma.MultiplyAdd(-b,i6,d)):a*r6-b*i6+d; i6=fma?Fma.MultiplyAdd(a,i6,b*r6):a*i6+b*r6; r6=next; }
            { var a=cr[index+7];var b=ci[index+7];var next=fma?Fma.MultiplyAdd(a,r7,Fma.MultiplyAdd(-b,i7,d)):a*r7-b*i7+d; i7=fma?Fma.MultiplyAdd(a,i7,b*r7):a*i7+b*r7; r7=next; }
            { var a=cr[index+8];var b=ci[index+8];var next=fma?Fma.MultiplyAdd(a,r8,Fma.MultiplyAdd(-b,i8,d)):a*r8-b*i8+d; i8=fma?Fma.MultiplyAdd(a,i8,b*r8):a*i8+b*r8; r8=next; }
            if(evidence is not null)
            {
                var sr=V.Zero;var si=V.Zero;
                sr+=Vector256.Create(weights[0])*r0;si+=Vector256.Create(weights[0])*i0;
                sr+=Vector256.Create(weights[1])*r1;si+=Vector256.Create(weights[1])*i1;
                sr+=Vector256.Create(weights[2])*r2;si+=Vector256.Create(weights[2])*i2;
                sr+=Vector256.Create(weights[3])*r3;si+=Vector256.Create(weights[3])*i3;
                sr+=Vector256.Create(weights[4])*r4;si+=Vector256.Create(weights[4])*i4;
                sr+=Vector256.Create(weights[5])*r5;si+=Vector256.Create(weights[5])*i5;
                sr+=Vector256.Create(weights[6])*r6;si+=Vector256.Create(weights[6])*i6;
                sr+=Vector256.Create(weights[7])*r7;si+=Vector256.Create(weights[7])*i7;
                sr+=Vector256.Create(weights[8])*r8;si+=Vector256.Create(weights[8])*i8;
                evidence.SetVector(begin+n,g,Vector256.Create(2d)*Vector256.Sqrt(sr*sr+si*si)/normalization[g]);
            }
        }
        re[index+0]=r0;im[index+0]=i0;
        re[index+1]=r1;im[index+1]=i1;
        re[index+2]=r2;im[index+2]=i2;
        re[index+3]=r3;im[index+3]=i3;
        re[index+4]=r4;im[index+4]=i4;
        re[index+5]=r5;im[index+5]=i5;
        re[index+6]=r6;im[index+6]=i6;
        re[index+7]=r7;im[index+7]=i7;
        re[index+8]=r8;im[index+8]=i8;
    }
}
