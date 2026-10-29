using System;
using System.IO;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public class ShaderSourceTests
{
    [Test]
    public void CompilerResolvesRelativeIncludesConditionalsAndGuardedCycles()
    {
        string root=Path.Combine(Path.GetTempPath(),"AEther-includes-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"Nested"));
        try
        {
            File.WriteAllText(Path.Combine(root,"Main.hlsl"),"""
                #if 0
                #include "Missing.hlsl"
                #endif
                #include "Nested/A.hlsl" // The compiler handles trailing comments too.
                float4 PS() : SV_Target { return Value(); }
                """);
            File.WriteAllText(Path.Combine(root,"Nested","A.hlsl"),"""
                #ifndef A
                #define A
                #include "../B.hlsl"
                #endif
                """);
            File.WriteAllText(Path.Combine(root,"B.hlsl"),"""
                #include "Nested/A.hlsl"
                float4 Value() { return float4(1,0,0,1); }
                """);
            string source=ShaderSource.IncludeFile(root,"Main.hlsl");
            var first=ShaderSource.Compile(source,"PS","Main.hlsl","ps_5_0");
            File.WriteAllText(Path.Combine(root,"B.hlsl"),"float4 Value() { return float4(0,1,0,1); }");
            var second=ShaderSource.Compile(source,"PS","Main.hlsl","ps_5_0");
            Assert.That(second.ToArray(),Is.Not.EqualTo(first.ToArray()),"Include edits must be read on compilation.");
            File.WriteAllText(Path.Combine(root,"B.hlsl"),"\nINVALID HLSL\n");
            var error=Assert.Catch(()=>ShaderSource.Compile(source,"PS","Main.hlsl","ps_5_0"));
            Assert.That(error!.Message,Does.Contain("B.hlsl(2,"),"Compiler diagnostics must identify the included file and line.");
        }
        finally { Directory.Delete(root,true); }
    }
}
