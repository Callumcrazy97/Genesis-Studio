using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Rendering.Particles;
using Genesis.Rendering.Abstractions;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Core;

public sealed unsafe partial class GpuRenderController
{
    private GpuParticleLibrary _particleLibrary;
    private readonly List<ParticleOperation> _particleOperations = new(64);
    private readonly List<ParticleDraw> _particleDraws = new(32);
    private const int MaximumQueuedParticleOperations = 8192;

    private readonly record struct ParticleOperation(GpuParticleEmitter Emitter, GpuParticleDefinition Definition,
        float Delta, int Births, uint Sequence, ParticleEventConnection[] Parents, int? ResetSeed);
    private sealed class ParticleDraw
    {
        internal GpuParticleEmitter Emitter;
        internal GpuParticleMesh Mesh;
        internal GpuTextureHandle Texture;
        internal bool Is2D;
        internal Vector4 Screen;
        internal Vector4 Clip;
    }

    public GpuParticleEmitter CreateParticleEmitter(GpuParticleDefinition definition, int seed)
    {
        if (!_initialized) throw new InvalidOperationException("The selected renderer must be initialized before creating GPU particles.");
        _particleLibrary ??= new GpuParticleLibrary(_gpu);
        return _particleLibrary.Create(definition, seed);
    }

    public void EnqueueParticleStep(GpuParticleEmitter emitter, float delta, int births, uint sequence,
        ParticleEventConnection[] parents)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        if (emitter.IsDisposed) return;
        if (_particleOperations.Count >= MaximumQueuedParticleOperations)
            throw new InvalidOperationException("Particle simulation is ahead of rendering. Pause the effect or reduce the preview replay speed.");
        _particleOperations.Add(new ParticleOperation(emitter, emitter.Definition, delta, births,
            sequence, parents ?? [], null));
    }

    public void EnqueueParticleReset(GpuParticleEmitter emitter, int seed)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        if (emitter.IsDisposed) return;
        // A reset supersedes old unsubmitted work for this emitter, including timeline seeks.
        _particleOperations.RemoveAll(operation => ReferenceEquals(operation.Emitter, emitter));
        _particleOperations.Add(new ParticleOperation(emitter, emitter.Definition, 0, 0, 0, [], seed));
    }

    public void SubmitParticles3D(GpuParticleEmitter emitter, MeshHandle mesh, TextureHandle texture)
    {
        if (emitter == null || emitter.IsDisposed) return;
        _fwd.TryGetParticleMesh(mesh, out GpuParticleMesh authored);
        _particleDraws.Add(new ParticleDraw
        {
            Emitter = emitter, Mesh = emitter.Geometry(authored), Texture = ResolveParticleTexture(texture),
        });
    }

    public void SubmitParticles2D(GpuParticleEmitter emitter, MeshHandle mesh, TextureHandle texture,
        float offsetX, float offsetY, float scale, float sizeScale, int depth, Vector4 clip)
    {
        if (emitter == null || emitter.IsDisposed) return;
        _fwd.TryGetParticleMesh(mesh, out GpuParticleMesh authored);
        var draw = new ParticleDraw
        {
            Emitter = emitter, Mesh = emitter.Geometry(authored), Texture = ResolveParticleTexture(texture),
            Is2D = true, Screen = new Vector4(offsetX, offsetY, scale, sizeScale), Clip = clip,
        };
        _particleDraws.Add(draw);
        _spr.SubmitExternal(depth, (projection, width, height) => DrawSubmittedParticle2D(draw, projection, width, height));
    }

    private GpuTextureHandle ResolveParticleTexture(TextureHandle texture)
    {
        GpuTextureHandle found = ResolveGpuTexture(texture.Id);
        return found.IsValid ? found : _whiteTexture;
    }

    private void PrepareSubmittedParticles()
    {
        try
        {
            foreach (ParticleOperation operation in _particleOperations)
            {
                if (operation.Emitter.IsDisposed) continue;
                if (operation.ResetSeed is int seed) operation.Emitter.RequestReset(seed);
                else operation.Emitter.Execute(operation.Delta, operation.Births, operation.Sequence,
                    operation.Definition, operation.Parents);
            }
            foreach (ParticleDraw draw in _particleDraws)
                if (!draw.Emitter.IsDisposed) draw.Emitter.PrepareDraw(draw.Mesh);
        }
        finally { _particleOperations.Clear(); }
    }

    private void DrawSubmittedParticles3D(Matrix4x4 view, Matrix4x4 projection)
    {
        if (!Matrix4x4.Invert(view, out Matrix4x4 inverse)) inverse = Matrix4x4.Identity;
        var parameters = new GpuParticleDraw
        {
            ViewProjection = view * projection,
            CameraRight = new Vector4(Vector3.Normalize(new Vector3(inverse.M11,inverse.M12,inverse.M13)),0),
            CameraUp = new Vector4(Vector3.Normalize(new Vector3(inverse.M21,inverse.M22,inverse.M23)),0),
            CameraForward = new Vector4(Vector3.Normalize(new Vector3(inverse.M31,inverse.M32,inverse.M33)),0),
        };
        Matrix4x4 viewProjection = view * projection;
        foreach (ParticleDraw draw in _particleDraws)
        {
            if (draw.Is2D || draw.Emitter.IsDisposed) continue;
            GpuParticleDefinition definition = draw.Emitter.Definition;
            if (!ParticleSphereVisible(definition.BoundsCenter, definition.BoundsRadius, viewProjection))
                continue;
            parameters.Mode = new Vector4(0, draw.Emitter.Capacity, 1, (float)draw.Emitter.SimulationTime);
            draw.Emitter.Draw(parameters,draw.Mesh,draw.Texture,definition.BlendMode);
        }
    }

    private void DrawSubmittedParticle2D(ParticleDraw draw, Matrix4x4 projection, int width, int height)
    {
        if (draw.Emitter.IsDisposed) return;
        Vector4 clip=draw.Clip;
        int x=0,y=0,right=width,bottom=height;
        if(clip.Z>0 && clip.W>0)
        {
            x=(int)Math.Clamp(Math.Floor(clip.X),0,width);y=(int)Math.Clamp(Math.Floor(clip.Y),0,height);
            right=(int)Math.Clamp(Math.Ceiling(clip.X+clip.Z),x,width);
            bottom=(int)Math.Clamp(Math.Ceiling(clip.Y+clip.W),y,height);
        }
        _gpu.SetScissor(x,y,right-x,bottom-y);
        var parameters=new GpuParticleDraw
        {
            ViewProjection=projection,CameraRight=new Vector4(1,0,0,0),CameraUp=new Vector4(0,-1,0,0),
            CameraForward=new Vector4(0,0,-1,0),Screen=draw.Screen,
            Mode=new Vector4(1,draw.Emitter.Capacity,1,(float)draw.Emitter.SimulationTime),
        };
        draw.Emitter.Draw(parameters,draw.Mesh,draw.Texture,draw.Emitter.Definition.BlendMode);
    }

    private static bool ParticleSphereVisible(Vector3 center, float radius, Matrix4x4 viewProjection)
    {
        if (!float.IsFinite(radius) || radius <= 0f) return true;
        Vector4 clip = Vector4.Transform(new Vector4(center, 1f), viewProjection);
        if (clip.W <= 0.0001f) return false;

        float xScale = new Vector3(viewProjection.M11, viewProjection.M12, viewProjection.M13).Length();
        float yScale = new Vector3(viewProjection.M21, viewProjection.M22, viewProjection.M23).Length();
        float zScale = new Vector3(viewProjection.M31, viewProjection.M32, viewProjection.M33).Length();
        float projectedRadius = radius * MathF.Max(xScale, MathF.Max(yScale, zScale));
        return clip.X >= -clip.W - projectedRadius && clip.X <= clip.W + projectedRadius
            && clip.Y >= -clip.W - projectedRadius && clip.Y <= clip.W + projectedRadius
            && clip.Z >= -projectedRadius && clip.Z <= clip.W + projectedRadius;
    }

    private void DisposeParticles()
    {
        _particleOperations.Clear();_particleDraws.Clear();
        _particleLibrary?.Dispose();_particleLibrary=null;
    }
}
