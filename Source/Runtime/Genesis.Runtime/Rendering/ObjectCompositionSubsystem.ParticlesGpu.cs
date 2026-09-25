#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Genesis.Rendering.Particles;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering;

public sealed partial class ObjectCompositionSubsystem
{
    private sealed class DeferredParticleDraw : IDeferredDraw2D
    {
        private readonly ObjectCompositionSubsystem _owner;
        private readonly ParticleState _state;
        private readonly ParticleLayerState _layer;
        private readonly float _centerX;
        private readonly float _centerY;
        private readonly float _zoom;

        public DeferredParticleDraw(ObjectCompositionSubsystem owner, ParticleState state, ParticleLayerState layer,
            float centerX, float centerY, float zoom)
        {
            _owner = owner;
            _state = state;
            _layer = layer;
            _centerX = centerX;
            _centerY = centerY;
            _zoom = zoom;
        }

        public void Submit(IRenderController renderer, Vector2 viewportOffset, Vector4 clip) =>
            _owner.SubmitParticleLayer2D(_state, _layer, renderer,
                _centerX + viewportOffset.X, _centerY + viewportOffset.Y, _zoom, clip);
    }

    private static Matrix4x4 ResolveParticleWorld(
        ParticleConfig config,
        in ParticleComponent component,
        in TransformComponent transform,
        Vector3 cameraPosition)
    {
        if (config.FollowCameraXZ)
            return Matrix4x4.CreateTranslation(cameraPosition.X, 0f, cameraPosition.Z);

        if (!component.FollowEntity)
            return Matrix4x4.Identity;

        float sx = MathF.Abs(transform.ScaleX) < 0.0001f ? 1f : transform.ScaleX;
        float sy = MathF.Abs(transform.ScaleY) < 0.0001f ? 1f : transform.ScaleY;
        float sz = MathF.Abs(transform.ScaleZ) < 0.0001f ? 1f : transform.ScaleZ;
        float rx = transform.RotationX * (MathF.PI / 180f);
        float ry = transform.RotationY * (MathF.PI / 180f);
        float rz = (transform.RotationZ + transform.Rotation) * (MathF.PI / 180f);

        return Matrix4x4.CreateScale(sx, sy, sz)
            * Matrix4x4.CreateRotationX(rx)
            * Matrix4x4.CreateRotationY(ry)
            * Matrix4x4.CreateRotationZ(rz)
            * Matrix4x4.CreateTranslation(transform.X, transform.Y, transform.Z);
    }

    private void EnsureGpuEmitters(ParticleState state, IGpuParticleRenderer renderer)
    {
        foreach (ParticleLayerState layer in state.Layers)
        {
            GpuParticleDefinition definition = GpuParticleDefinitionBuilder.Build(
                layer.Config, layer.World, layer.MeshSurfaceSamples);

            bool replace = layer.GpuEmitter is null
                || layer.GpuEmitter.IsDisposed
                || !ReferenceEquals(layer.GpuOwner, renderer)
                || layer.GpuEmitter.Capacity != definition.Capacity;

            if (replace)
            {
                layer.GpuEmitter?.Dispose();
                layer.GpuEmitter = renderer.CreateParticleEmitter(definition, StableEmitterSeed(state.Entity.Id, layer.EmitterId));
                layer.GpuOwner = renderer;
                layer.Sequence = 0;
                layer.EmitAccumulator = 0f;
                layer.PendingBurst = !layer.Config.Loop ? Math.Max(0, layer.Config.BurstCount) : 0;
            }
            else
            {
                layer.GpuEmitter.UpdateDefinition(definition);
            }

            layer.LastDiagnostics = layer.GpuEmitter.Diagnostics;
        }
    }

    private IEnumerable<ParticleLayerState> EventOrderedLayers(ParticleState state)
    {
        if (state.Effect.EventLinks is not { Count: > 0 })
            return state.Layers;

        Dictionary<string, ParticleLayerState> byId = state.Layers
            .GroupBy(layer => layer.EmitterId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        List<ParticleLayerState> ordered = new(state.Layers.Count);
        HashSet<string> complete = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visiting = new(StringComparer.OrdinalIgnoreCase);

        void Visit(ParticleLayerState layer)
        {
            if (complete.Contains(layer.EmitterId)) return;
            if (!visiting.Add(layer.EmitterId)) return; // cycle: retain deterministic source order.
            foreach (ParticleEventLink link in state.Effect.EventLinks)
            {
                if (!string.Equals(link.TargetEmitterId, layer.EmitterId, StringComparison.OrdinalIgnoreCase)) continue;
                if (byId.TryGetValue(link.SourceEmitterId, out ParticleLayerState? source)) Visit(source);
            }
            visiting.Remove(layer.EmitterId);
            complete.Add(layer.EmitterId);
            ordered.Add(layer);
        }

        foreach (ParticleLayerState layer in state.Layers) Visit(layer);
        return ordered;
    }

    private void AdvanceGpuLayer(
        ParticleState state,
        ParticleLayerState layer,
        IGpuParticleRenderer renderer)
    {
        GpuParticleEmitter? emitter = layer.GpuEmitter;
        if (emitter is null || emitter.IsDisposed) return;

        GpuParticleDefinition definition = GpuParticleDefinitionBuilder.Build(
            layer.Config, layer.World, layer.MeshSurfaceSamples);
        emitter.UpdateDefinition(definition);

        ParticleEventConnection[] links = BuildEventConnections(state, layer);
        int safety = 0;
        while (layer.PendingSeconds >= (1f / 60f) && safety++ < 16)
        {
            const float step = 1f / 60f;
            int births = CalculateBirths(layer, step);
            renderer.EnqueueParticleStep(emitter, step, births, ++layer.Sequence, links);
            layer.PendingSeconds -= step;
        }

        // Preserve very small deltas until the next frame. At low frame rates, process one bounded
        // remainder after the fixed steps so visible simulation does not permanently lag.
        if (layer.PendingSeconds > 0f && safety == 0)
        {
            float step = Math.Min(layer.PendingSeconds, 0.25f);
            int births = CalculateBirths(layer, step);
            renderer.EnqueueParticleStep(emitter, step, births, ++layer.Sequence, links);
            layer.PendingSeconds -= step;
        }

        layer.LastDiagnostics = emitter.Diagnostics;
    }

    private ParticleEventConnection[] BuildEventConnections(ParticleState state, ParticleLayerState target)
    {
        if (state.Effect.EventLinks is not { Count: > 0 }) return [];
        List<ParticleEventConnection> result = [];
        foreach (ParticleEventLink link in state.Effect.EventLinks)
        {
            if (!string.Equals(link.TargetEmitterId, target.EmitterId, StringComparison.OrdinalIgnoreCase)
                || link.Trigger == ParticleEventTrigger.None)
                continue;

            ParticleLayerState? source = state.Layers.FirstOrDefault(layer =>
                string.Equals(layer.EmitterId, link.SourceEmitterId, StringComparison.OrdinalIgnoreCase));
            if (source?.GpuEmitter is null || source.GpuEmitter.IsDisposed) continue;

            result.Add(new ParticleEventConnection(
                source.GpuEmitter,
                (int)link.Trigger,
                (float)Math.Clamp(link.Probability, 0d, 1d),
                Math.Clamp(link.Count, 1, 32),
                (float)Math.Clamp(link.InheritVelocity, 0d, 4d)));
        }
        return result.ToArray();
    }

    private static int CalculateBirths(ParticleLayerState layer, float delta)
    {
        int births = 0;
        if (layer.PendingBurst > 0)
        {
            births = layer.PendingBurst;
            layer.PendingBurst = 0;
        }
        if (!layer.Config.Loop || layer.Config.EmitRate <= 0) return births;

        layer.EmitAccumulator += (float)layer.Config.EmitRate * delta;
        int continuous = Math.Min((int)layer.EmitAccumulator, GpuParticleProtocol.MaximumCapacity);
        layer.EmitAccumulator -= continuous;
        return Math.Min(GpuParticleProtocol.MaximumCapacity, births + continuous);
    }

    private void AdvanceSoftwareLayer(ParticleState state, ParticleLayerState layer, RuntimeScene scene)
    {
        if (layer.Simulation is null)
        {
            layer.Simulation = new ParticleSimulation();
            layer.Simulation.LoadConfig(layer.Config);
            layer.Simulation.SetMeshSurfaceSamples(layer.MeshSurfaceSamples);
        }
        else
        {
            layer.Simulation.UpdateConfig(layer.Config);
        }

        Vector3 origin = new(layer.World.M41, layer.World.M42, layer.World.M43);
        layer.Simulation.SetEmitterOrigin(origin);
        layer.Simulation.UpdateCameraPosition(scene.Camera3D.Position);

        if (layer.Config.CollisionMode != ParticleCollisionMode.None && scene.Physics is not null)
        {
            layer.Simulation.SetCollisionHeightProvider(position =>
            {
                if (!scene.Physics.Raycast(scene.World, position + Vector3.UnitY * 0.5f, -Vector3.UnitY,
                        1000f, out var hit, state.Entity))
                    return float.NaN;
                bool terrainOrStatic = hit.Entity.IsNull;
                return (terrainOrStatic ? layer.Config.CollideWithTerrain : layer.Config.CollideWithGeometry)
                    ? hit.Point.Y
                    : float.NaN;
            });
        }

        float remaining = layer.PendingSeconds;
        layer.PendingSeconds = 0f;
        while (remaining > 0f)
        {
            float step = Math.Min(remaining, 0.25f);
            layer.Simulation.Step(step);
            remaining -= step;
        }
    }

    private void SubmitParticleLayer2D(
        ParticleState state,
        ParticleLayerState layer,
        IRenderController renderer,
        float centerX,
        float centerY,
        float zoom,
        Vector4 clip)
    {
        _lastRenderer = renderer;
        EnsureParticleRenderResources(layer, renderer);
        ParticleExecutionDecision execution = ParticleExecutionPolicy.Resolve(renderer);
        if (execution.Target == ParticleExecutionTarget.Gpu)
        {
            if (renderer is not IGpuParticleRenderer gpuRenderer)
                throw new InvalidOperationException("Renderer reports GPU particle capability but does not expose IGpuParticleRenderer.");
            EnsureGpuEmitters(state, gpuRenderer);
            AdvanceGpuLayer(state, layer, gpuRenderer);
            if (layer.GpuEmitter is not null && layer.Frames.Length > 0 && layer.Frames[0].IsValid)
            {
                float pixelScale = MathF.Max(4f, zoom * 12f);
                gpuRenderer.SubmitParticles2D(layer.GpuEmitter, layer.Frames[0], layer.Texture,
                    centerX, centerY, zoom, pixelScale, 0, clip);
            }
            return;
        }

        if (execution.Target == ParticleExecutionTarget.UnsupportedHardware)
        {
            ParticleExecutionPolicy.ThrowIfHardwareWouldFallbackToCpu(renderer);
            return;
        }

        if (_lastScene is null) return;
        AdvanceSoftwareLayer(state, layer, _lastScene);
        ParticleSimulation? simulation = layer.Simulation;
        if (simulation is null) return;
        int capacity = Math.Max(1, simulation.Capacity);
        if (layer.SpriteCalls.Length != capacity) layer.SpriteCalls = new SpriteDrawCall[capacity];
        int count = simulation.FillSpriteDrawCalls2D(layer.SpriteCalls, centerX, centerY, zoom, layer.Texture);
        if (count > 0) renderer.DrawSpriteBatch(layer.SpriteCalls.AsSpan(0, count));
    }

    private Vector3[] LoadMeshSurfaceSamples(ParticleConfig config)
    {
        if (config.Shape != ParticleEmitShape.MeshSurface || string.IsNullOrWhiteSpace(config.MeshSurfaceAsset))
            return [];

        try
        {
            GModelAsset asset = _particleModelAssets.Load(_projectPath, config.MeshSurfaceAsset);
            List<Vector3> positions = [];
            foreach (GModelMesh mesh in asset.Meshes)
            {
                if (mesh.Vertices is { Length: > 0 })
                    positions.AddRange(mesh.Vertices.Select(vertex => vertex.Position));
                else if (mesh.SkinnedVertices is { Length: > 0 })
                    positions.AddRange(mesh.SkinnedVertices.Select(vertex => vertex.Position));
                if (positions.Count >= 100_000) break;
            }
            if (positions.Count > 100_000) positions.RemoveRange(100_000, positions.Count - 100_000);
            return positions.ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _ = exception;
            return [];
        }
    }

    private static int StableEmitterSeed(int entityId, string emitterId)
    {
        unchecked
        {
            uint hash = 2166136261u ^ (uint)entityId;
            foreach (char ch in emitterId ?? string.Empty)
            {
                hash ^= ch;
                hash *= 16777619u;
            }
            return (int)hash;
        }
    }
}
