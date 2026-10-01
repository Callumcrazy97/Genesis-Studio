using System.Numerics;
using Genesis.Application.Runtime;
using Genesis.Rendering.Core;
using Genesis.Rendering.D3dMath;
using Genesis.Rendering.Primitives;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Animated characters are culled by where their pose puts them, not by where they were modelled.
/// </summary>
/// <remarks>
/// Reported from a game: a character built from several skinned meshes (head, eyes, hair) played a
/// seated clip that lowered the hips half a metre. With the camera about a metre away the hair and
/// eyes vanished, because each mesh was tested against its bind-pose sphere, which was by then
/// outside the view. The first five cases are pure calculation; the last draws the scene on each
/// graphics backend through the hidden viewport harness.
/// </remarks>
internal static class SkinnedCullingSuite
{
    private const int Hips = 0, Spine = 1, Head = 2, Unused = 3;

    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Skinned mesh culling");

        HeadlessHarness.RunCase(context.Report, "Engine.Render.SkinnedCulling.PosedBoundFollowsTheMeshesOwnJoints", () =>
        {
            SkinnedMeshVertex[] hair = Blob(new Vector3(0f, 1.7f, 0f), 0.12f, Head);
            int[] joints = ForwardRenderer.CollectSkinJoints(hair, out bool unweighted);
            HeadlessHarness.Assert(joints.SequenceEqual([Head]) && !unweighted,
                $"A mesh weighted to one joint reported joints [{string.Join(", ", joints)}], unweighted={unweighted}.");

            Matrix4x4[] palette = SeatedPalette();
            BindSphere(hair, out Vector3 bindCenter, out float bindRadius);
            ForwardRenderer.PosedSkinBounds(bindCenter, bindRadius, joints, unweighted, palette, out Vector3 center, out float radius);
            AssertContainsPosed(hair, palette, center, radius, "hair");
            HeadlessHarness.Assert(Vector3.Distance(center, Vector3.Transform(bindCenter, palette[Head])) < 1e-4f
                && MathF.Abs(radius - bindRadius) < 1e-4f,
                $"A rigidly moved mesh should keep its radius and move with its joint; got centre {center}, radius {radius} (bind {bindRadius}).");
            // The far-away joint this mesh never uses must not stretch its bound across the room.
            HeadlessHarness.Assert(MathF.Abs(center.X) < 0.01f && radius < 0.2f,
                $"A joint the mesh does not use changed its bound (centre {center}, radius {radius}).");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Render.SkinnedCulling.BlendedAndUnweightedVerticesStayInside", () =>
        {
            // A neck: every vertex is shared between the spine and the head, and two carry no weight.
            SkinnedMeshVertex[] neck = Blob(new Vector3(0f, 1.5f, 0f), 0.1f, Spine);
            for (int i = 0; i < neck.Length; i++)
            {
                float toHead = i / (float)(neck.Length - 1);
                neck[i].JointIndices = new Vector4(Spine, Head, 0f, 0f);
                neck[i].JointWeights = new Vector4(1f - toHead, toHead, 0f, 0f);
            }

            neck[3].JointWeights = Vector4.Zero;
            neck[7].JointWeights = Vector4.Zero;
            int[] joints = ForwardRenderer.CollectSkinJoints(neck, out bool unweighted);
            HeadlessHarness.Assert(joints.SequenceEqual([Spine, Head]) && unweighted,
                $"Expected joints [Spine, Head] and unweighted vertices; got [{string.Join(", ", joints)}], {unweighted}.");

            Matrix4x4[] palette = SeatedPalette();
            // Scale and rotation on one joint: the bound must allow for both.
            palette[Spine] = Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateRotationZ(0.6f) * Matrix4x4.CreateTranslation(0.2f, -0.3f, 0f);
            BindSphere(neck, out Vector3 bindCenter, out float bindRadius);
            ForwardRenderer.PosedSkinBounds(bindCenter, bindRadius, joints, unweighted, palette, out Vector3 center, out float radius);
            AssertContainsPosed(neck, palette, center, radius, "neck");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Render.SkinnedCulling.SeatedHeadStaysVisibleToACloseCamera", () =>
        {
            SkinnedMeshVertex[] hair = Blob(new Vector3(0f, 1.7f, 0f), 0.12f, Head);
            int[] joints = ForwardRenderer.CollectSkinJoints(hair, out bool unweighted);
            Matrix4x4[] palette = SeatedPalette();
            BindSphere(hair, out Vector3 bindCenter, out float bindRadius);
            ForwardRenderer.PosedSkinBounds(bindCenter, bindRadius, joints, unweighted, palette, out Vector3 center, out float radius);

            // One metre from the seated head, looking straight at it through a 30 degree lens: the
            // view spans roughly 0.27 m above and below the head, so standing height is off screen.
            Vector3 seatedHead = Vector3.Transform(new Vector3(0f, 1.7f, 0f), palette[Head]);
            Matrix4x4 view = Matrix4x4.CreateLookAt(seatedHead + new Vector3(0f, 0f, 1f), seatedHead, Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 6f, 16f / 9f, 0.05f, 100f);
            Frustum frustum = new(view * projection);
            HeadlessHarness.Assert(!frustum.ContainsSphere(bindCenter, bindRadius),
                "The test camera still sees the bind-pose position, so it does not reproduce the report.");
            HeadlessHarness.Assert(frustum.ContainsSphere(center, radius),
                $"The seated head's posed bound (centre {center}, radius {radius}) was culled by a camera looking straight at it.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Render.SkinnedCulling.BrokenPoseOrMissingJointFallsBackToTheBindSphere", () =>
        {
            SkinnedMeshVertex[] hair = Blob(new Vector3(0f, 1.7f, 0f), 0.12f, Head);
            BindSphere(hair, out Vector3 bindCenter, out float bindRadius);

            Matrix4x4[] broken = SeatedPalette();
            broken[Head].M42 = float.NaN;
            ForwardRenderer.PosedSkinBounds(bindCenter, bindRadius, [Head], false, broken, out Vector3 center, out float radius);
            HeadlessHarness.Assert(center == bindCenter && radius == bindRadius,
                "A pose containing NaN did not fall back to the bind sphere, so the mesh could be culled for ever.");

            // The mesh names joint 9; the palette has four. The shader cannot move those vertices.
            ForwardRenderer.PosedSkinBounds(bindCenter, bindRadius, [9], false, SeatedPalette(), out center, out radius);
            HeadlessHarness.Assert(Vector3.Distance(center, bindCenter) < 1e-5f && radius >= bindRadius,
                "A joint outside the palette did not keep the bind sphere inside the bound.");

            ForwardRenderer.PosedSkinBounds(bindCenter, bindRadius, [], false, SeatedPalette(), out center, out radius);
            HeadlessHarness.Assert(center == bindCenter && radius == bindRadius, "A mesh with no joints should keep its bind sphere.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Render.SkinnedCulling.AnimatedObjectsAreNotRejectedByTheirBindBox", () =>
        {
            EcsWorld world = new();
            Entity still = world.CreateEntity();
            Entity idleAnimator = world.CreateEntity();
            world.Set(idleAnimator, new ModelAnimatorComponent());
            Entity seated = world.CreateEntity();
            world.Set(seated, new ModelAnimatorComponent { ClipName = "Sit", Playing = true });
            Entity controlled = world.CreateEntity();
            world.Set(controlled, new ModelAnimatorComponent { Controller = new Genesis.Runtime.Modeling.AnimationController() });

            HeadlessHarness.Assert(!ObjectDrawPass.IsPosedByAnimation(world, still) && !ObjectDrawPass.IsPosedByAnimation(world, idleAnimator),
                "An Object with no clip is still culled by its model's box, so static scenery keeps its early rejection.");
            HeadlessHarness.Assert(ObjectDrawPass.IsPosedByAnimation(world, seated) && ObjectDrawPass.IsPosedByAnimation(world, controlled),
                "An Object posed by a clip or a controller must be left to the renderer's posed-mesh test.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Render.SkinnedCulling.SeatedMeshIsDrawnOnAllBackends", () => DrawnOnAllBackends(context));
    }

    private static readonly RenderBackendOption[] GpuBackends =
    [
        RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
        RenderBackendOption.Vulkan, RenderBackendOption.OpenGL,
    ];

    /// <summary>
    /// The report, end to end, through each real renderer with frustum culling on: the mesh is
    /// modelled above the view and posed into the middle of it.
    /// </summary>
    private static void DrawnOnAllBackends(HeadlessContext context)
    {
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        try
        {
            foreach (RenderBackendOption backend in GpuBackends)
            {
                RenderBackendSelection.Configure(backend);
                string name = backend.ToString().ToLowerInvariant();
                using RuntimeViewportHarness harness = new();
                string bindFile = Path.Combine(context.Captures, $"skinned-cull-{name}-bind.png");
                string seatedFile = Path.Combine(context.Captures, $"skinned-cull-{name}-seated.png");
                (_, RenderStats bindStats) = harness.CaptureSkinnedCulling(bindFile, posed: false);
                (_, RenderStats seatedStats) = harness.CaptureSkinnedCulling(seatedFile, posed: true);

                System.Drawing.Point centre = harness.ProjectSkinnedCullingPoint(RuntimeViewportHarness.SkinnedSeatedCentre);
                using System.Drawing.Bitmap bind = new(bindFile);
                using System.Drawing.Bitmap seated = new(seatedFile);
                System.Drawing.Color bindPixel = bind.GetPixel(centre.X, centre.Y);
                System.Drawing.Color seatedPixel = seated.GetPixel(centre.X, centre.Y);

                // In its bind pose the mesh is above the view: culled, and nothing but background.
                HeadlessHarness.Assert(bindStats.InstancesCulled >= 1 && bindPixel.R < 60,
                    $"{backend}: the bind-pose mesh should be off screen and culled (culled {bindStats.InstancesCulled}, pixel {bindPixel}).");
                HeadlessHarness.Assert(seatedPixel.R > 120 && seatedPixel.R - seatedPixel.B > 60,
                    $"{backend}: the seated skinned mesh was not drawn in front of the camera "
                    + $"(pixel {seatedPixel} at {centre}; culled {seatedStats.InstancesCulled}, drawn {seatedStats.InstancesDrawn}).");
            }
        }
        finally { RenderBackendSelection.Configure(previous); }
    }

    /// <summary>Hips drop half a metre and tilt; the head follows and nods. Joint 3 is far away and unused.</summary>
    private static Matrix4x4[] SeatedPalette()
    {
        Matrix4x4 hips = Matrix4x4.CreateRotationX(-0.25f) * Matrix4x4.CreateTranslation(0f, -0.5f, 0.05f);
        Matrix4x4[] palette = new Matrix4x4[4];
        palette[Hips] = hips;
        palette[Spine] = hips;
        palette[Head] = Matrix4x4.CreateTranslation(0f, -1.7f, 0f) * Matrix4x4.CreateRotationX(0.2f)
            * Matrix4x4.CreateTranslation(0f, 1.7f, 0f) * Matrix4x4.CreateTranslation(0f, -0.5f, 0.05f);
        palette[Unused] = Matrix4x4.CreateTranslation(25f, 0f, 0f);
        return palette;
    }

    private static SkinnedMeshVertex[] Blob(Vector3 centre, float radius, int joint)
    {
        SkinnedMeshVertex[] vertices = new SkinnedMeshVertex[14];
        for (int i = 0; i < vertices.Length; i++)
        {
            float around = i * 2.399963f;
            float up = 1f - (2f * i / (vertices.Length - 1));
            float ring = MathF.Sqrt(MathF.Max(0f, 1f - (up * up)));
            Vector3 direction = new(MathF.Cos(around) * ring, up, MathF.Sin(around) * ring);
            vertices[i] = new SkinnedMeshVertex
            {
                Position = centre + (direction * radius),
                Normal = direction,
                Color = Vector4.One,
                JointIndices = new Vector4(joint, 0f, 0f, 0f),
                JointWeights = new Vector4(1f, 0f, 0f, 0f),
            };
        }

        return vertices;
    }

    /// <summary>The bind-pose sphere as the renderer computes it: box centre, farthest vertex.</summary>
    private static void BindSphere(SkinnedMeshVertex[] vertices, out Vector3 centre, out float radius)
    {
        Vector3 min = vertices[0].Position, max = vertices[0].Position;
        foreach (SkinnedMeshVertex vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        centre = (min + max) * 0.5f;
        radius = 0f;
        foreach (SkinnedMeshVertex vertex in vertices)
        {
            radius = MathF.Max(radius, Vector3.Distance(vertex.Position, centre));
        }
    }

    /// <summary>Skins every vertex exactly as the vertex shader does and checks it is inside the bound.</summary>
    private static void AssertContainsPosed(SkinnedMeshVertex[] vertices, Matrix4x4[] palette, Vector3 centre, float radius, string name)
    {
        Span<float> weights = stackalloc float[4];
        Span<float> indices = stackalloc float[4];
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].JointWeights.CopyTo(weights);
            vertices[i].JointIndices.CopyTo(indices);
            float total = weights[0] + weights[1] + weights[2] + weights[3];
            Vector3 posed = vertices[i].Position;
            if (total > 0.0001f)
            {
                posed = Vector3.Zero;
                for (int k = 0; k < 4; k++)
                {
                    if (weights[k] > 0.0001f)
                    {
                        posed += Vector3.Transform(vertices[i].Position, palette[(int)MathF.Round(indices[k])]) * weights[k];
                    }
                }

                posed /= total;
            }

            float distance = Vector3.Distance(posed, centre);
            HeadlessHarness.Assert(distance <= radius + 1e-4f,
                $"Posed {name} vertex {i} at {posed} is {distance - radius:0.####} outside its bound (centre {centre}, radius {radius}).");
        }
    }
}
