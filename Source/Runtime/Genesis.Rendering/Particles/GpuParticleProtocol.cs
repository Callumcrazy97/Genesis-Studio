using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Particles;

/// <summary>
/// The binary particle protocol is shared by DX11, DX12, Vulkan and OpenGL. Every member is
/// a complete 16-byte register; no CLR bools, doubles or backend-specific layouts cross the GPU.
/// </summary>
public static class GpuParticleProtocol
{
    public const int Threads = 128;
    public const int MaximumCapacity = 262144;
    public const int CurveSamples = 128;
    public const int TrailSamples = 8;
    public const int StateStride = 208;
    public const int EventStride = 32;
    public const int EventCapacity = 4096;
    public const int HeaderWords = 64;
    public const int MaximumGroups = MaximumCapacity / Threads;
    public const int GroupWords = 4;
    public const int LocalWords = 4;
    public const int GroupBase = HeaderWords;
    public const int LocalBase = GroupBase + MaximumGroups * GroupWords;
    public const int ArgumentsBytes = 64;
    public const int CounterBytes = HeaderWords * sizeof(uint);
    public const long DefaultDeviceBudgetBytes = 512L * 1024 * 1024;

    public static int ValidateCapacity(int capacity)
    {
        if (capacity < 1 || capacity > MaximumCapacity)
            throw new ArgumentOutOfRangeException(nameof(capacity), $"Particle capacity must be 1–{MaximumCapacity:N0}.");
        return capacity;
    }

    public static int Groups(int capacity) => (ValidateCapacity(capacity) + Threads - 1) / Threads;
    public static int AliveBase(int capacity) => checked(LocalBase + ValidateCapacity(capacity) * LocalWords);
    public static int PoolBytes(int capacity) => checked((AliveBase(capacity) + capacity) * sizeof(uint));
    public static long AllocationBytes(int capacity) => checked((long)ValidateCapacity(capacity) * StateStride
        + PoolBytes(capacity) + (long)EventCapacity * 2 * EventStride + ArgumentsBytes);
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuParticleParameters
{
    public Matrix4x4 World;
    public Matrix4x4 InverseWorld;
    // shape, radius, cone half-angle in radians, downward
    public Vector4 Emission;
    // box dimensions, mesh sample count
    public Vector4 Box;
    // speed, speed variation, lifetime, lifetime variation
    public Vector4 Motion;
    // gravity xyz, drag
    public Vector4 Forces;
    // wind xz, turbulence, reserved
    public Vector4 Wind;
    // start/end size, x/y aspect
    public Vector4 Sizes;
    // rotation speed radians, initial rotation range radians, colour jitter, emissive
    public Vector4 Rotation;
    // response, plane height, bounce, particle radius
    public Vector4 Collision;
    // renderer kind, alignment, trail duration, ribbon maximum segment length
    public Vector4 Render;
    // columns, rows, fps, enabled
    public Vector4 Flipbook;
    // local-space, collision plane enabled, trail width, velocity stretch
    public Vector4 Options;
    // beam end in emitter space, beam noise
    public Vector4 Beam;
    // mesh sample offset, BVH node offset/count, triangle data offset
    public Vector4 Geometry;
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuParticleStep
{
    // dt, normal birth requests, accumulated time, enabled
    public Vector4 Timing;
    public uint Capacity;
    public uint Groups;
    public uint Seed;
    public uint Sequence;
    // event mask, probability, count, inherited velocity fraction
    public Vector4 Event;
    // parent event capacity, index count, reserved
    public Vector4 Limits;
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuParticleDraw
{
    public Matrix4x4 ViewProjection;
    public Vector4 CameraRight;
    public Vector4 CameraUp;
    public Vector4 CameraForward;
    // 2D offset xy, simulation-to-screen scale, particle size scale
    public Vector4 Screen;
    // is2D, capacity, texture enabled, current time
    public Vector4 Mode;
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuParticleState
{
    public Vector4 PositionAge;
    public Vector4 VelocityLife;
    public Vector4 RotationSpeedScale;
    // birth serial, previous ribbon serial, previous ribbon slot, trail clock (uints bit-cast)
    public Vector4 Identity;
    public Vector4 PreviousEvent;
    public Vector4 Trail0, Trail1, Trail2, Trail3, Trail4, Trail5, Trail6, Trail7;
}

/// <summary>Immutable upload, regenerated only when an authored value/source geometry changes.</summary>
public sealed class GpuParticleDefinition
{
    public int Capacity { get; init; } = 1000;
    public GpuParticleParameters Parameters { get; init; }
    public Vector4[] Lookup { get; init; } = [];
    public string DebugName { get; init; } = "Particles";
    public int BlendMode { get; init; }
}

public readonly record struct ParticleDiagnostics(
    string Execution,
    int Capacity,
    int Alive,
    long Spawned,
    long Died,
    long Collisions,
    long Dropped,
    long AllocatedBytes,
    double? SimulationMilliseconds,
    bool CountsPending,
    long CompletedSteps,
    string Warning);

public readonly record struct GpuParticleMesh(
    GpuBufferHandle Vertices,
    GpuBufferHandle Indices,
    int VertexStride,
    int IndexCount,
    GpuIndexFormat IndexFormat);

public readonly record struct ParticleEventConnection(
    GpuParticleEmitter Parent, int Mask, float Probability, int Count, float InheritVelocity);

/// <summary>Implemented by hardware renderers only. Software uses the explicit scalar simulator.</summary>
public interface IGpuParticleRenderer
{
    GpuParticleEmitter CreateParticleEmitter(GpuParticleDefinition definition, int seed);
    void EnqueueParticleStep(GpuParticleEmitter emitter, float delta, int births, uint sequence,
        ParticleEventConnection[] parents);
    void EnqueueParticleReset(GpuParticleEmitter emitter, int seed);
    void SubmitParticles3D(GpuParticleEmitter emitter, MeshHandle mesh, TextureHandle texture);
    void SubmitParticles2D(GpuParticleEmitter emitter, MeshHandle mesh, TextureHandle texture, float offsetX,
        float offsetY, float scale, float sizeScale, int depth, Vector4 clip);
}
