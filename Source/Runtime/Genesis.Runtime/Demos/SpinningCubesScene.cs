using System;
using System.Numerics;
using Genesis.Rendering.Meshes;
using Genesis.Runtime.Core;
using Genesis.Runtime.Scene;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Demos
{
    /// <summary>
    /// WinForms-free port of the old TempDev SpinningCubesDemo. Builds a glowing,
    /// pulsing centre cube ringed by orbiting cubes directly against a RuntimeScene,
    /// so it can run inside the pure Silk.NET runtime (GenesisRuntimeHost) with no
    /// dependency on the editor sandbox (ISandboxDemo / SandboxContext).
    /// </summary>
    public static class SpinningCubesScene
    {
        public static void Build(RuntimeScene scene, IRenderController renderer)
        {
            scene.Camera3D.Position = new Vector3(0f, 6f, 18f);
            scene.Camera3D.Yaw = 0f;
            scene.Camera3D.Pitch = -18f * MathUtil.DegToRad;

            MeshHandle cubeMesh = MeshGeometry.RegisterCube(renderer, RenderColor.White, 1f);
            scene.AddSubsystem(new SpinningCubesSubsystem(scene, cubeMesh));
        }

        private sealed class SpinningCubesSubsystem : ISceneSubsystem
        {
            private readonly Entity _center;
            private readonly Entity[] _orbiters = new Entity[8];
            private const float CenterBaseScale = 2.5f;

            public SpinningCubesSubsystem(RuntimeScene scene, MeshHandle cubeMesh)
            {
                _center = scene.CreateEntity(new Vector3(0f, 2.5f, 0f));
                scene.World.Set(_center, new MeshDrawComponent
                {
                    Mesh = cubeMesh,
                    Tint = new Vector4(0.9f, 0.75f, 0.4f, 1f),
                    Emissive = 0.5f,
                    Flags = MeshDrawFlags.Emissive,
                });

                for (int i = 0; i < _orbiters.Length; i++)
                {
                    _orbiters[i] = scene.CreateEntity(Vector3.Zero);
                    float hue = (float)i / _orbiters.Length;
                    scene.World.Set(_orbiters[i], new MeshDrawComponent
                    {
                        Mesh = cubeMesh,
                        Tint = HsvToVector4(hue, 0.7f, 0.95f),
                    });
                }
            }

            public void Update(RuntimeScene scene, GameTime time)
            {
                float t = time.Total;

                if (scene.World.IsAlive(_center))
                {
                    ref var centerTransform = ref scene.World.GetRef<Transform3DComponent>(_center);
                    centerTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, t * 2.8f);
                    centerTransform.Scale = Vector3.One * (CenterBaseScale * (1f + 0.08f * MathF.Sin(t * 5.5f)));

                    if (scene.World.Has<MeshDrawComponent>(_center))
                    {
                        ref var centerMesh = ref scene.World.GetRef<MeshDrawComponent>(_center);
                        centerMesh.Emissive = 0.35f + 0.65f * (0.5f + 0.5f * MathF.Sin(t * 0.9f));
                        centerMesh.Flags = MeshDrawFlags.Emissive;
                    }
                }

                for (int i = 0; i < _orbiters.Length; i++)
                {
                    Entity orbiter = _orbiters[i];
                    if (!scene.World.IsAlive(orbiter))
                        continue;

                    float phase = i * (MathF.Tau / _orbiters.Length);
                    float angle = t * 1.6f + phase;
                    float radius = 5.5f;
                    float bobY = 0.8f * MathF.Sin(angle * 0.7f);
                    float oScale = 1f + 0.06f * MathF.Sin(t * 8f + phase);
                    var center = new Vector3(0f, 2.5f, 0f);

                    ref var transform = ref scene.World.GetRef<Transform3DComponent>(orbiter);
                    transform.Position = center + new Vector3(MathF.Cos(angle) * radius, bobY, MathF.Sin(angle) * radius);
                    transform.Scale = Vector3.One * oScale;
                    transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle * 2f);
                }
            }

            public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

            public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer) { }

            public void Dispose() { }

            private static Vector4 HsvToVector4(float h, float s, float v)
            {
                float i = MathF.Floor(h * 6);
                float f = h * 6 - i;
                float p = v * (1 - s);
                float q = v * (1 - f * s);
                float t = v * (1 - (1 - f) * s);
                return ((int)i % 6) switch
                {
                    0 => new Vector4(v, t, p, 1f),
                    1 => new Vector4(q, v, p, 1f),
                    2 => new Vector4(p, v, t, 1f),
                    3 => new Vector4(p, q, v, 1f),
                    4 => new Vector4(t, p, v, 1f),
                    _ => new Vector4(v, p, q, 1f),
                };
            }
        }
    }
}
