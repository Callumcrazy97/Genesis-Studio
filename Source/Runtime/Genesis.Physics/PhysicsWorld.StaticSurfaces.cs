using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;

namespace Genesis.Physics;

public sealed partial class PhysicsWorld
{
    private sealed class ExternalStatic
    {
        public StaticHandle Handle;
        public TypedIndex Shape;
        public string Label = string.Empty;
    }

    private readonly Dictionary<int, ExternalStatic> _externalStatics = new();

    /// <summary>Registers a static triangle mesh owned by a scene subsystem.</summary>
    public int RegisterStaticTriangleMesh(
        IReadOnlyList<Vector3> vertices,
        IReadOnlyList<int> indices,
        Vector3 scale,
        Vector3 position,
        Quaternion orientation,
        string label,
        float friction = 0.9f,
        float restitution = 0f)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Count < 3 || indices.Count % 3 != 0)
            throw new ArgumentException("Triangle indices must be a non-empty multiple of three.", nameof(indices));

        _pool.Take<Triangle>(indices.Count / 3, out Buffer<Triangle> triangles);
        for (int i = 0; i < triangles.Length; i++)
        {
            int offset = i * 3;
            triangles[i] = new Triangle(
                vertices[indices[offset]],
                vertices[indices[offset + 1]],
                vertices[indices[offset + 2]]);
        }

        var mesh = new Mesh(triangles, new Vector3(
            MathF.Max(MathF.Abs(scale.X), 1e-5f),
            MathF.Max(MathF.Abs(scale.Y), 1e-5f),
            MathF.Max(MathF.Abs(scale.Z), 1e-5f)), _pool);
        TypedIndex shape = _simulation.Shapes.Add(mesh);
        StaticHandle handle = _simulation.Statics.Add(new StaticDescription(position, orientation, shape));
        int registrationId = _nextRegistrationId++;
        _staticHandles[handle] = registrationId;
        _staticFriction[handle] = ClampFriction(friction);
        _staticRestitution[handle] = ClampRestitution(restitution);
        _staticSensor[handle] = false;
        _staticCollisionFilter[handle] = (0, 0x7Fu);
        _externalStatics[registrationId] = new ExternalStatic { Handle = handle, Shape = shape, Label = label ?? string.Empty };
        return registrationId;
    }

    public bool UnregisterStaticSurface(int registrationId)
    {
        if (!_externalStatics.Remove(registrationId, out ExternalStatic? surface))
            return false;
        _simulation.Statics.Remove(surface.Handle);
        _simulation.Shapes.RemoveAndDispose(surface.Shape, _pool);
        _staticHandles.Remove(surface.Handle);
        _staticFriction.Remove(surface.Handle);
        _staticRestitution.Remove(surface.Handle);
        _staticSensor.Remove(surface.Handle);
        _staticCollisionFilter.Remove(surface.Handle);
        return true;
    }

    public int ExternalStaticCount => _externalStatics.Count;
    internal bool IsExternalStatic(int registrationId) => _externalStatics.ContainsKey(registrationId);

    private void ClearExternalStatics()
    {
        foreach (int id in new List<int>(_externalStatics.Keys))
            UnregisterStaticSurface(id);
    }
}
