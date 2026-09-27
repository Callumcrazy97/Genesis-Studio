using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Particles;
using Genesis.Rendering.Primitives;
using Genesis.Rendering.SilkNet.DX11;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name); passed++;
}
try
{
    Check(Marshal.SizeOf<GpuParticleState>() == GpuParticleProtocol.StateStride, "state stride matches HLSL");
    Check(Marshal.SizeOf<GpuParticleStep>() == 64, "step constant layout");
    Check(Marshal.SizeOf<GpuParticleParameters>() == 336, "parameter constant layout");
    Check(Marshal.SizeOf<GpuParticleDraw>() == 144, "draw constant layout");
    Check(GpuParticleProtocol.LocalBase == 8256, "prefix buffer layout");
    Check(GpuParticleProtocol.Groups(257) == 3, "partial workgroup capacity");
    Check(GpuParticleProtocol.PoolBytes(257) == (8256 + 257 * 5) * 4, "GPU pool allocation");
    if (args.Contains("--protocol-only")) return 0;
    foreach (GpuShaderBinaryFormat format in Enum.GetValues<GpuShaderBinaryFormat>())
    {
        foreach (string entry in GpuParticleShaders.ComputeEntries)
        {
            var compiled = ShaderCompiler.CompileForBackend(GpuParticleShaders.Compute, entry, GpuShaderStage.Compute, format);
            Check(compiled.Blob.Length > 16, format + " compute " + entry);
        }
        foreach (GpuShaderStage stage in new[] { GpuShaderStage.Vertex, GpuShaderStage.Pixel })
        {
            var compiled = ShaderCompiler.CompileForBackend(GpuParticleShaders.Draw, stage == GpuShaderStage.Vertex ? "VS" : "PS", stage, format);
            Check(compiled.Blob.Length > 16, format + " draw " + stage);
        }
    }
    if (args.Contains("--warp-conformance"))
    {
        Console.WriteLine("Explicit WARP test adapter: validates D3D11 compute/indirect semantics, NOT physical GPU performance.");
        using var runtime = SilkNetDx11Runtime.CreateValidationWarp();
        using var device = new Dx11GpuDevice(runtime);
        using var library = new GpuParticleLibrary(device);
        var table = new Vector4[256];
        for (int i = 0; i < 128; i++) { table[i] = new Vector4(i / 127f,1,1,1); table[128+i] = Vector4.One; }
        var parameters = new GpuParticleParameters
        {
            World=Matrix4x4.Identity,InverseWorld=Matrix4x4.Identity,
            Motion=new Vector4(0,0,.05f,0),Sizes=Vector4.One,Options=new Vector4(0,0,1,0),
        };
        var definition = new GpuParticleDefinition { Capacity=257, Parameters=parameters, Lookup=table };
        using var emitter = library.Create(definition,123);
        emitter.Execute(1/60f,40,1,definition,[]);
        ReadCounters(emitter,device);
        Check(emitter.Diagnostics.Alive==40,"GPU births and prefix compaction on a partial workgroup");
        emitter.Execute(1/60f,1000,2,definition,[]);
        ReadCounters(emitter,device);
        Check(emitter.Diagnostics.Alive==257 && emitter.Diagnostics.Dropped==783,"GPU capacity and dropped-birth accounting");
        emitter.Execute(.1f,0,3,definition,[]);
        ReadCounters(emitter,device);
        Check(emitter.Diagnostics.Alive==0 && emitter.Diagnostics.Died==257,"GPU expiration and reusable slots");
        emitter.RequestReset(123);
        emitter.Execute(0,25,1,definition,[]);
        ReadCounters(emitter,device);
        Check(emitter.Diagnostics.Alive==25 && emitter.Diagnostics.Spawned==25,"GPU reset clears counters");
        using var child = library.Create(definition,456);
        child.Execute(0,0,1,definition,[new ParticleEventConnection(emitter,1,1,2,0)]);
        ReadCounters(child,device);
        Check(child.Diagnostics.Alive==50,"GPU-to-GPU birth sub-emitter without event readback");
    }
    Console.WriteLine($"{passed} particle checks passed.");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

static void ReadCounters(GpuParticleEmitter emitter, IGpuComputeDevice device)
{
    emitter.RequestDiagnosticSample();
    device.WaitIdle();
    for (int i=0;i<5000;i++)
    {
        emitter.PollCompletedDiagnostics();
        if (!emitter.Diagnostics.CountsPending) return;
        Thread.Sleep(1);
    }
    throw new TimeoutException("Particle diagnostic readback did not complete.");
}
