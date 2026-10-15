using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Genesis.Rendering.Core;
using Genesis.Rendering.D3dMath;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Interfaces;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// A posed skinned model is drawn in the pose its clip gives it: the picture the renderer makes
/// matches the same vertices skinned on the CPU.
/// </summary>
/// <remarks>
/// Reported from a game: a hero frozen on a forward roll at 0.6 s (an Animator with Clip FPS 0 and
/// playback speed 0) was drawn standing upright, on Direct3D 11 and Vulkan alike, while the same
/// clip evaluated on the CPU had the hips turned upside down. The game ran an engine from before
/// Clip FPS 0 meant "the clip's own rate": it sampled a 10 fps clip as if it were 60 fps, so 0.6 s
/// was frame 36, held at the roll's last frame, where the character has stood up again. The
/// tumble below is built the same way: upside down in the middle, standing at its last frame.
/// Each case draws through the game's own Object path (ObjectDrawPass) and compares the outline
/// with the same vertices skinned on the CPU.
/// </remarks>
internal static class SkinnedPoseSuite
{
    private const int Width = 480, Height = 480;
    private const string TumbleClip = "Tumble";

    // Hips pitch (degrees) and height for each frame of the 10 fps tumble: upside down from frame
    // 4 to 6, standing again at frame 9, its last.
    private static readonly float[] TumblePitch = [0f, 30f, 70f, 110f, 140f, 150f, 150f, 120f, 60f, 0f];
    private static readonly float[] TumbleHeight = [0.9f, 0.85f, 0.75f, 0.6f, 0.5f, 0.5f, 0.5f, 0.6f, 0.8f, 0.9f];

    private static readonly RenderBackendOption[] GpuBackends =
    [
        RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
        RenderBackendOption.Vulkan, RenderBackendOption.OpenGL,
    ];

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Skinned pose");
        string project = Path.Combine(ctx.Workspace, "SkinnedPose");
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.SkinnedPose.FrozenTumbleIsDrawnInItsClipFrameOnAllBackends",
            () => FrozenTumble(ctx, project));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.SkinnedPose.FrozenTumbleKeepsItsPoseAmongManyCrowdPoses",
            () => TumbleInACrowd(ctx, project));

        // A real project's model, for reports from a game: only when one is named.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GENESIS_SKINNED_POSE_PROJECT"))) return;
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.SkinnedPose.ProjectModelMatchesItsCpuPose", () => ProjectModel(ctx));
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GENESIS_SKINNED_POSE_CROWD")))
            HeadlessHarness.RunCase(ctx.Report, "Engine.Render.SkinnedPose.ProjectHeroKeepsItsPoseInACrowd", () => ProjectCrowd(ctx));
    }

    /// <summary>
    /// The tumbler frozen at 0.6 s (frame 6 of its 10 fps clip, upside down) and at 0.15 s (frame
    /// 1, leaning), with Clip FPS 0, on every graphics backend this machine has.
    /// </summary>
    private static void FrozenTumble(HeadlessContext ctx, string project)
    {
        WriteTumblers(project);
        GModelAsset asset = RuntimeModelAssetRegistry.Shared.Load(project, "Tumbler");
        HeadlessHarness.Assert(asset.Rig?.IsValid == true && asset.Animations.Count > 1, "The tumbler model did not load with its rig and clips.");
        var failures = new List<string>();
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        try
        {
            foreach (RenderBackendOption backend in GpuBackends)
            {
                if (!RenderBackendSelection.IsAvailable(backend)) continue;
                RenderBackendSelection.Configure(backend);
                using Form host = UnattendedWindowing.NewHost(Width, Height);
                UnattendedWindowing.ShowWithoutFocus(host);
                using IRenderController renderer = RenderControllerFactory.Create(backend);
                renderer.Initialize(host.Handle, Width, Height);
                string name = RenderBackendCatalog.Describe(backend).ShortName.ToLowerInvariant();
                try
                {
                    AddFailure(failures, DrawAndCompare(ctx, renderer, project, "Tumbler", asset, TumbleClip, 0.6f,
                        $"tumble-{name}-0_6", expectUpsideDown: true));
                    AddFailure(failures, DrawAndCompare(ctx, renderer, project, "Tumbler", asset, TumbleClip, 0.15f,
                        $"tumble-{name}-0_15"));
                    if (backend == RenderBackendOption.SilkNetDx11)
                    {
                        // The check can tell: sampled at 60 fps, as engines before Clip FPS 0
                        // meant "the clip's own rate" did, 0.6 s is frame 36, held at the last
                        // frame, standing.
                        string? sixty = DrawAndCompare(ctx, renderer, project, "Tumbler", asset, TumbleClip, 0.6f,
                            $"tumble-{name}-0_6-60fps", clipFps: 60f, expectUpsideDown: true);
                        if (sixty?.StartsWith("UPRIGHT", StringComparison.Ordinal) != true)
                            failures.Add("The upside-down check did not notice a standing tumbler (sampled at 60 fps): "
                                + (sixty ?? "it passed."));
                    }
                }
                finally { ObjectDrawPass.InvalidateAssets(renderer); }
            }
        }
        finally { RenderBackendSelection.Configure(previous); }

        HeadlessHarness.Assert(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The frozen tumbler beside a crowd that asks for a new clip frame every frame, on this model
    /// and another, for longer than the model cache keeps an unused pose: hundreds of poses are
    /// made and released, and their skin palettes given out again, while the hero holds its own.
    /// </summary>
    private static void TumbleInACrowd(HeadlessContext ctx, string project)
    {
        WriteTumblers(project);
        GModelAsset asset = RuntimeModelAssetRegistry.Shared.Load(project, "Tumbler");
        using Form host = UnattendedWindowing.NewHost(Width, Height);
        UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.SilkNetDx11);
        renderer.Initialize(host.Handle, Width, Height);
        try
        {
            // The cache keeps 128 poses per model and releases those unused for two seconds.
            var crowd = new Crowd(project, ["Tumbler", "Tumbler Crowd"], perModel: 8, seconds: 0.6f);
            string? failure = DrawAndCompare(ctx, renderer, project, "Tumbler", asset, TumbleClip, 0.6f,
                "tumble-dx11-crowd-0_6", crowd, expectUpsideDown: true);
            HeadlessHarness.Assert(crowd.Phase == 2 && crowd.DistinctPoses >= 2 * 400 && crowd.ReusedPalettes > 0,
                $"The crowd reached phase {crowd.Phase} with {crowd.DistinctPoses} distinct poses in {crowd.Frames} frames and "
                + $"{crowd.ReusedPalettes} palette ids given out again; the test needs three sets of well over the cache's "
                + "128 poses per model, so that it releases palettes and their ids are reused.");
            HeadlessHarness.Assert(failure == null, failure ?? string.Empty);
        }
        finally { ObjectDrawPass.InvalidateAssets(renderer); }
    }

    private static void AddFailure(List<string> failures, string? failure)
    {
        if (failure != null) failures.Add(failure);
    }

    /// <summary>Saves the tumbler, and a second model for the crowd, into the project's Models folder.</summary>
    private static void WriteTumblers(string project)
    {
        string models = RuntimeModelStore.ModelsDirectory(project);
        Directory.CreateDirectory(models);
        foreach (string name in new[] { "Tumbler", "Tumbler Crowd" })
        {
            string path = Path.Combine(models, name + ".gmodel");
            if (!File.Exists(path)) RuntimeModelStore.Save(path, BuildTumbler(name));
        }
    }

    /// <summary>
    /// A 1.8 m figure: a body box on the hips and a head box on the head. Its bones are listed
    /// children first, as an imported character's are (Head, Hips, Root, then the parentless Body).
    /// "Tumble" rolls it upside down and back to its feet; sixty looping "Crowd" clips turn it.
    /// </summary>
    internal static GModelAsset BuildTumbler(string name)
    {
        const int Head = 0, Hips = 1, Root = 2, Body = 3;
        const float Degrees = MathF.PI / 180f;
        var rig = new GModelRig
        {
            Bones =
            [
                new GModelBone { Name = "Head", ParentIndex = Hips, BindLocal = Matrix4x4.CreateTranslation(0f, 0.65f, 0f) },
                new GModelBone { Name = "Hips", ParentIndex = Root, BindLocal = Matrix4x4.CreateTranslation(0f, 0.9f, 0f) },
                new GModelBone { Name = "Root", ParentIndex = Body },
                new GModelBone { Name = "Body", ParentIndex = -1 },
            ],
        };
        GModelPrimitiveFactory.RebuildInverseBindMatrices(rig);

        var vertices = new List<SkinnedMeshVertex>();
        var indices = new List<ushort>();
        AddBox(vertices, indices, new Vector3(-0.2f, 0f, -0.15f), new Vector3(0.2f, 1.3f, 0.15f), Hips);
        AddBox(vertices, indices, new Vector3(-0.15f, 1.3f, -0.15f), new Vector3(0.15f, 1.8f, 0.15f), Head);
        var mesh = new GModelMesh
        {
            Name = "Body",
            IsSkinned = true,
            SkinnedVertices = vertices.ToArray(),
            Vertices = vertices.Select(v => new MeshVertex { Position = v.Position, Normal = v.Normal, Color = v.Color, UV = v.UV }).ToArray(),
            Indices = indices.ToArray(),
        };
        var asset = new GModelAsset
        {
            Name = name,
            Rig = rig,
            Meshes = [mesh],
            Materials = [new GModelMaterial { Name = "Skin", BaseColor = new Vector4(0.95f, 0.55f, 0.1f, 1f), DoubleSided = true }],
        };
        asset.Animations.Add(Clip(TumbleClip, loop: false, frame => (TumblePitch[frame], TumbleHeight[frame], 0f)));
        for (int c = 0; c < 60; c++)
        {
            int clip = c;
            asset.Animations.Add(Clip($"Crowd {c:00}", loop: true, frame => (clip * 2f + frame * 3f, 0.9f, clip * 9f + frame * 4f)));
        }

        asset.RecalculateBounds();
        return asset;

        GModelAnimationClip Clip(string clipName, bool loop, Func<int, (float Pitch, float Height, float Yaw)> pose)
        {
            var clip = new GModelAnimationClip { Name = clipName, Fps = 10f, Loop = loop };
            for (int frame = 0; frame < TumblePitch.Length; frame++)
            {
                (float pitch, float height, float yaw) = pose(frame);
                Matrix4x4[] locals = rig.Bones.Select(bone => bone.BindLocal).ToArray();
                locals[Hips] = Matrix4x4.CreateRotationX(pitch * Degrees) * Matrix4x4.CreateRotationY(yaw * Degrees)
                    * Matrix4x4.CreateTranslation(0f, height, 0f);
                clip.Frames.Add(new GModelAnimationFrame { LocalBoneTransforms = locals });
            }

            return clip;
        }
    }

    private static void AddBox(List<SkinnedMeshVertex> vertices, List<ushort> indices, Vector3 min, Vector3 max, int joint)
    {
        int first = vertices.Count;
        Vector3 centre = (min + max) * 0.5f;
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            vertices.Add(new SkinnedMeshVertex
            {
                Position = corner,
                Normal = Vector3.Normalize(corner - centre),
                Color = Vector4.One,
                JointIndices = new Vector4(joint, 0f, 0f, 0f),
                JointWeights = new Vector4(1f, 0f, 0f, 0f),
            });
        }

        ReadOnlySpan<int> faces =
        [
            0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6,
            0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7,
            0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5,
        ];
        foreach (int index in faces) indices.Add((ushort)(first + index));
    }

    /// <summary>
    /// The project model frozen at GENESIS_SKINNED_POSE_HERO while a crowd on the project's
    /// GENESIS_SKINNED_POSE_CROWD models plays a new clip frame every frame, for long enough that
    /// the model cache releases idle poses and their ids are given out again.
    /// </summary>
    private static void ProjectCrowd(HeadlessContext ctx)
    {
        string? project = Environment.GetEnvironmentVariable("GENESIS_SKINNED_POSE_PROJECT");
        string? model = Environment.GetEnvironmentVariable("GENESIS_SKINNED_POSE_MODEL");
        string? crowdModels = Environment.GetEnvironmentVariable("GENESIS_SKINNED_POSE_CROWD");
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(crowdModels))
            throw new CheckNotRunException("GENESIS_SKINNED_POSE_PROJECT, _MODEL and _CROWD name no models.");
        string hero = Environment.GetEnvironmentVariable("GENESIS_SKINNED_POSE_HERO") ?? "Roll@0.6";
        string backends = Environment.GetEnvironmentVariable("GENESIS_SKINNED_POSE_BACKENDS") ?? "dx11";
        float seconds = float.TryParse(Environment.GetEnvironmentVariable("GENESIS_SKINNED_POSE_PRESSURE_SECONDS"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s) ? s : 1f;
        GModelAsset asset = RuntimeModelAssetRegistry.Shared.Load(project, model);
        int at = hero.LastIndexOf('@');
        string clip = hero[..at];
        float time = float.Parse(hero[(at + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        var failures = new List<string>();
        foreach (string backendName in backends.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using Form host = UnattendedWindowing.NewHost(Width, Height);
            UnattendedWindowing.ShowWithoutFocus(host);
            using IRenderController renderer = RenderControllerFactory.Create(RenderBackendCatalog.ParseExplicitValue(backendName));
            renderer.Initialize(host.Handle, Width, Height);
            try
            {
                var crowd = new Crowd(project,
                    crowdModels.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), 8, seconds);
                string? failure = DrawAndCompare(ctx, renderer, project, model, asset, clip, time,
                    $"{backendName}-crowd-{clip}-{time:0.##}".Replace('.', '_'), crowd);
                if (failure != null) failures.Add(failure);
            }
            finally { ObjectDrawPass.InvalidateAssets(renderer); }
        }

        HeadlessHarness.Assert(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Characters off to the side of the view, each on its own clip and a new frame every frame:
    /// the river-road scene where a frozen hero was reported drawn standing.
    /// </summary>
    private sealed class Crowd(string project, IReadOnlyList<string> models, int perModel, float seconds)
    {
        private readonly List<(Genesis.Shared.ECS.Entity Entity, GModelAsset Asset)> _members = [];

        /// <summary>Distance along Z at which the crowd stands, well outside the hero's camera.</summary>
        public const float Offset = 60f;

        public int Frames { get; private set; }

        public void Populate(EcsWorld world)
        {
            int index = 0;
            foreach (string model in models)
            {
                GModelAsset asset = RuntimeModelAssetRegistry.Shared.Load(project, model);
                for (int i = 0; i < perModel; i++, index++)
                {
                    var entity = world.CreateEntity();
                    world.Set(entity, new TransformComponent { X = index * 1.5f, Z = Offset + i * 2f, ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f });
                    world.Set(entity, new Draw3DComponent { Visible = true, CastShadows = true, ReceiveShadows = true });
                    world.Set(entity, new ModelRendererComponent
                    {
                        ModelAsset = model, ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f, CastShadows = true, ReceiveShadows = true,
                    });
                    world.Set(entity, new ModelAnimatorComponent { Playing = true, PlaybackSpeed = 0f });
                    _members.Add((entity, asset));
                }
            }
        }

        /// <summary>
        /// Plays the crowd through three sets of its models' clips. The first makes well over the
        /// cache's 128 poses per model; the second outlasts the two seconds after which a pose is
        /// idle; the third makes new poses, each of which has the cache release the first set's
        /// palettes and the renderer give their ids out again. The hero is drawn every frame.
        /// </summary>
        /// <param name="frame">Draws one frame; returns the hero's palette and how many it shares with the crowd.</param>
        public void Run(EcsWorld world, Func<(int Hero, int Shared)> frame, Action<string> log)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double phaseStart = 0;
            while (true)
            {
                double now = clock.Elapsed.TotalSeconds;
                if (Phase == 0 && now >= 0.5 && FewestPosesPerModel >= 160 || Phase == 1 && now - phaseStart >= 2.3)
                {
                    Phase++;
                    phaseStart = now;
                    log($"crowd phase {Phase} from frame {Frames} at {now:0.0} s, {DistinctPoses} poses so far");
                }
                else if (Phase == 2 && now - phaseStart >= seconds) break;
                HeadlessHarness.Assert(now < 90, $"The crowd made only {FewestPosesPerModel} poses per model in 90 s.");

                Step(world, Frames);
                (int heroPalette, int shared) = frame();
                if (Frames % 250 == 0 || shared > 0)
                    log($"crowd frame {Frames} at {now:0.0} s (phase {Phase}): hero palette {heroPalette}, shared with the crowd {shared}");
                Frames++;
            }
        }

        private readonly Dictionary<GModelAsset, HashSet<string>> _poses = new(ReferenceEqualityComparer.Instance);
        private GModelAsset? _heroAsset;
        private string _heroClip = string.Empty;
        private int _heroFrame = -1;

        /// <summary>0, 1 or 2: which third of each model's clips the crowd is playing.</summary>
        public int Phase { get; private set; }

        private readonly HashSet<int> _firstSetPalettes = [];
        private readonly HashSet<int> _reusedPalettes = [];

        /// <summary>
        /// Palette ids the crowd drew the first set with and later drew the third set with: the
        /// sets share no pose, so each is a palette released and given out again.
        /// </summary>
        public int ReusedPalettes => _reusedPalettes.Count;

        public void Observe(IEnumerable<int> crowdPalettes)
        {
            foreach (int id in crowdPalettes)
            {
                if (Phase == 0) _firstSetPalettes.Add(id);
                else if (Phase == 2 && _firstSetPalettes.Contains(id)) _reusedPalettes.Add(id);
            }
        }

        /// <summary>Distinct model, clip and frame combinations the crowd has asked for.</summary>
        public int DistinctPoses => _poses.Values.Sum(poses => poses.Count);

        private int FewestPosesPerModel => _poses.Count == 0 ? 0 : _poses.Values.Min(poses => poses.Count);

        /// <summary>
        /// The hero's own pose, which the crowd never takes: the same model on the same clip frame
        /// rightly shares the hero's palette, so any palette they share is then one given out twice.
        /// </summary>
        public void Avoid(GModelAsset asset, string clip, int frame)
        {
            _heroAsset = asset;
            _heroClip = clip;
            _heroFrame = frame;
        }

        public void Step(EcsWorld world, int frame)
        {
            for (int i = 0; i < _members.Count; i++)
            {
                (var entity, GModelAsset asset) = _members[i];
                if (asset.Animations is not { Count: > 0 } clips) continue;
                int third = Math.Max(1, clips.Count / 3);
                int first = Math.Min(Phase * third, clips.Count - 1);
                int span = Phase == 2 ? clips.Count - first : third;
                // Each member walks the set's clips one a frame, and steps three clip frames on
                // after each walk, so every clip frame of the set comes round.
                GModelAnimationClip clip = clips[first + (i * 13 + frame) % span];
                int frames = Math.Max(1, clip.Frames?.Count ?? 1);
                int clipFrame = (frame / span * 3 + i) % frames;
                // By name: a model reloaded after a cache clear is a new object but the same model.
                if (_heroAsset != null && string.Equals(asset.Name, _heroAsset.Name, StringComparison.OrdinalIgnoreCase)
                    && clipFrame == _heroFrame && string.Equals(clip.Name, _heroClip, StringComparison.OrdinalIgnoreCase))
                    clipFrame = (clipFrame + 1) % frames;
                ref ModelAnimatorComponent animator = ref world.GetRef<ModelAnimatorComponent>(entity);
                animator.ClipName = clip.Name;
                // Mid-frame, so the frame sampled cannot depend on rounding.
                animator.TimeSeconds = (clipFrame + 0.5f) / MathF.Max(1f, clip.Fps);
                animator.Loop = clip.Loop;
                if (!_poses.TryGetValue(asset, out HashSet<string>? poses))
                    _poses[asset] = poses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                poses.Add($"{clip.Name}|{clipFrame}");
            }
        }
    }

    /// <summary>
    /// Any project's model, named by GENESIS_SKINNED_POSE_PROJECT and GENESIS_SKINNED_POSE_MODEL,
    /// frozen at each GENESIS_SKINNED_POSE_CLIPS entry ("Clip@seconds;...") and drawn through the
    /// game's Object draw path on each GENESIS_SKINNED_POSE_BACKENDS renderer.
    /// </summary>
    private static void ProjectModel(HeadlessContext ctx)
    {
        string? project = Environment.GetEnvironmentVariable("GENESIS_SKINNED_POSE_PROJECT");
        string? model = Environment.GetEnvironmentVariable("GENESIS_SKINNED_POSE_MODEL");
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(model))
            throw new CheckNotRunException("GENESIS_SKINNED_POSE_PROJECT and GENESIS_SKINNED_POSE_MODEL name no model.");
        string clips = Environment.GetEnvironmentVariable("GENESIS_SKINNED_POSE_CLIPS") ?? "@0;Roll@0.6;Roll@0.15;Death@1.6";
        string backends = Environment.GetEnvironmentVariable("GENESIS_SKINNED_POSE_BACKENDS") ?? "dx11";

        GModelAsset asset = RuntimeModelAssetRegistry.Shared.Load(project, model);
        HeadlessHarness.Assert(asset.Rig?.IsValid == true && asset.HasRenderableMeshes,
            $"'{model}' loaded without a rig or meshes ({asset.ImportMessage}).");
        Console.WriteLine($"[pose] {asset.Name}: {asset.Rig!.Bones.Count} bones, {asset.Meshes.Count} meshes, {asset.Animations.Count} clips");
        foreach (GModelAnimationClip c in asset.Animations.Where(c => c.Name is "Roll" or "Death" or "Knockback" or "SwordA"))
            Console.WriteLine($"[pose] clip {c.Name}: {c.Frames.Count} frames at {c.Fps} fps, loop {c.Loop}, {c.Tracks?.Count ?? 0} tracks");

        var failures = new List<string>();
        foreach (string backendName in backends.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            RenderBackendOption backend = RenderBackendCatalog.ParseExplicitValue(backendName);
            using Form host = UnattendedWindowing.NewHost(Width, Height);
            UnattendedWindowing.ShowWithoutFocus(host);
            using IRenderController renderer = RenderControllerFactory.Create(backend);
            renderer.Initialize(host.Handle, Width, Height);
            try
            {
                foreach (string entry in clips.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    // "Clip@seconds" or "Clip@seconds/fps" (an Animator's Clip FPS; 0 by default).
                    int at = entry.LastIndexOf('@');
                    string clip = at < 0 ? entry : entry[..at];
                    string[] timing = at < 0 ? ["0"] : entry[(at + 1)..].Split('/');
                    float time = float.Parse(timing[0], System.Globalization.CultureInfo.InvariantCulture);
                    float fps = timing.Length > 1 ? float.Parse(timing[1], System.Globalization.CultureInfo.InvariantCulture) : 0f;
                    string label = $"{backendName}-{(clip.Length == 0 ? "bind" : clip)}-{time:0.##}{(fps > 0f ? $"-{fps:0}fps" : "")}".Replace('.', '_');
                    string? failure = DrawAndCompare(ctx, renderer, project, model, asset, clip, time, label, clipFps: fps);
                    if (failure != null) failures.Add(failure);
                }
            }
            finally { ObjectDrawPass.InvalidateAssets(renderer); }
        }

        HeadlessHarness.Assert(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string? DrawAndCompare(HeadlessContext ctx, IRenderController renderer, string project, string model,
        GModelAsset asset, string clip, float time, string label, Crowd? crowd = null, float clipFps = 0f,
        bool expectUpsideDown = false)
    {
        EcsWorld world = new();
        var entity = world.CreateEntity();
        var transform = new TransformComponent { ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f };
        var modelComponent = new ModelRendererComponent
        {
            ModelAsset = model, ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f, CastShadows = true, ReceiveShadows = true,
        };
        var animator = new ModelAnimatorComponent
        {
            ClipName = clip, TimeSeconds = time, PlaybackSpeed = 0f, Playing = clip.Length > 0, Loop = false, ClipFps = clipFps,
        };
        world.Set(entity, transform);
        world.Set(entity, new Draw3DComponent { Visible = true, CastShadows = true, ReceiveShadows = true });
        world.Set(entity, modelComponent);
        world.Set(entity, animator);

        // The same vertices skinned on the CPU, as the vertex shader is meant to.
        RuntimeModelAnimationState state = RuntimeModelAnimationState.From(animator, false);
        Matrix4x4[] locals = GModelPrimitiveFactory.EvaluateAnimatedLocals(asset, state);
        Matrix4x4[] palette = GModelPrimitiveFactory.EvaluateSkinPalette(asset.Rig, locals);
        Vector3 pivot = asset.Pivot?.Position ?? Vector3.Zero;
        Matrix4x4 modelWorld = Matrix4x4.CreateTranslation(-pivot) * RuntimeModelRenderSystem.TransformMatrix(transform, modelComponent);
        int headJoint = asset.Rig.Bones.FindIndex(bone => string.Equals(bone.Name, "Head", StringComparison.OrdinalIgnoreCase));
        int hipsJoint = asset.Rig.Bones.FindIndex(bone => string.Equals(bone.Name, "Hips", StringComparison.OrdinalIgnoreCase));
        var posedHead = new List<Vector3>();
        var bindHead = new List<Vector3>();
        List<Vector3> posed = SkinnedWorldPositions(asset, palette, modelWorld, headJoint, posedHead);
        SkinnedWorldPositions(asset, GModelPrimitiveFactory.EvaluateBindPosePalette(asset.Rig), modelWorld, headJoint, bindHead);
        HeadlessHarness.Assert(posed.Count > 0, "The model has no skinned vertices at level 0.");
        Vector3 hipsWorld = hipsJoint < 0 ? Vector3.Zero
            : Vector3.Transform(GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, locals)[hipsJoint].Translation, modelWorld);

        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Vector3 p in posed) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        Vector3 centre = (min + max) * 0.5f;
        float extent = MathF.Max(0.25f, (max - min).Length());
        const float Fov = MathF.PI / 4f;
        // From the side (+X), so a forward roll or a fall shows as a change of outline.
        Vector3 eye = centre + new Vector3(extent * 0.5f / MathF.Tan(Fov * 0.5f) * 1.4f, 0f, 0f);
        Matrix4x4 view = D3dMatrixHelper.CreateLookAtLh(eye, centre, Vector3.UnitY);
        Matrix4x4 projection = D3dMatrixHelper.CreatePerspectiveLh(Fov, Width / (float)Height, 0.05f, 200f);

        byte[] empty = Frame(renderer, project, view, projection, eye, centre, world: null, out _);
        // The clip frame the hero is frozen on, sampled as the engine samples it.
        GModelAnimationClip? sampled = asset.Animations.FirstOrDefault(c => string.Equals(c.Name, clip, StringComparison.OrdinalIgnoreCase));
        int heroFrame = -1;
        float rate = 0f;
        if (sampled is { Frames.Count: > 0 })
        {
            rate = state.Fps > 0f ? state.Fps : MathF.Max(1f, sampled.Fps);
            heroFrame = (int)MathF.Floor(time * rate);
            heroFrame = state.Loop || sampled.Loop
                ? ((heroFrame % sampled.Frames.Count) + sampled.Frames.Count) % sampled.Frames.Count
                : Math.Clamp(heroFrame, 0, sampled.Frames.Count - 1);
        }

        int firstPalette = 0, sharedFrames = 0;
        if (crowd != null)
        {
            crowd.Avoid(asset, clip, heroFrame);
            crowd.Populate(world);
            crowd.Run(world, () =>
            {
                Frame(renderer, project, view, projection, eye, centre, world, read: false, out _, out int hero, out int shared);
                crowd.Observe(CrowdPalettes);
                if (firstPalette == 0) firstPalette = hero;
                if (shared > 0) sharedFrames++;
                return (hero, shared);
            }, line => Console.WriteLine("[pose] " + line));
        }

        byte[] drawn = Frame(renderer, project, view, projection, eye, centre, world, read: true,
            out RenderStats stats, out int finalPalette, out int finalShared)!;
        if (crowd != null)
            Console.WriteLine($"[pose] {label}: {crowd.Frames} crowd frames, {crowd.DistinctPoses} crowd poses, "
                + $"{crowd.ReusedPalettes} released palette ids given out again; hero palette {firstPalette} first, {finalPalette} last; "
                + $"{sharedFrames} frames shared a hero palette with the crowd, {finalShared} in the captured frame");

        bool[] mask = new bool[Width * Height];
        int left = Width, top = Height, right = -1, bottom = -1, covered = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            int o = i * 4;
            int delta = Math.Abs(drawn[o] - empty[o]) + Math.Abs(drawn[o + 1] - empty[o + 1]) + Math.Abs(drawn[o + 2] - empty[o + 2]);
            if (delta <= 24) continue;
            mask[i] = true;
            covered++;
            int x = i % Width, y = i / Width;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }

        Matrix4x4 viewProjection = view * projection;
        int cpuLeft = Width, cpuTop = Height, cpuRight = -1, cpuBottom = -1, inside = 0, onScreen = 0;
        var points = new List<Point>(posed.Count);
        foreach (Vector3 p in posed)
        {
            Vector4 clipSpace = Vector4.Transform(new Vector4(p, 1f), viewProjection);
            if (clipSpace.W <= 0f) continue;
            int x = (int)MathF.Round((clipSpace.X / clipSpace.W * 0.5f + 0.5f) * Width);
            int y = (int)MathF.Round((0.5f - clipSpace.Y / clipSpace.W * 0.5f) * Height);
            if (x < 0 || y < 0 || x >= Width || y >= Height) continue;
            onScreen++;
            points.Add(new Point(x, y));
            cpuLeft = Math.Min(cpuLeft, x); cpuRight = Math.Max(cpuRight, x);
            cpuTop = Math.Min(cpuTop, y); cpuBottom = Math.Max(cpuBottom, y);
            if (Near(mask, x, y, 2)) inside++;
        }

        float share = onScreen == 0 ? 0f : inside / (float)onScreen;
        string pose = DescribePose(asset, locals, palette);
        if (sampled is { Frames.Count: > 0 })
            pose += $"; frame {heroFrame} of {sampled.Frames.Count} at {rate:0} fps";
        string summary = $"{label}: GPU outline x {left}..{right}, y {top}..{bottom} ({covered} px); "
            + $"CPU pose x {cpuLeft}..{cpuRight}, y {cpuTop}..{cpuBottom}; {share:P0} of {onScreen} CPU vertices on the GPU outline; "
            + $"drawn {stats.InstancesDrawn}, culled {stats.InstancesCulled}; {pose}";
        Console.WriteLine("[pose] " + summary);

        string file = Path.Combine(ctx.Captures, $"pose-{label}.png");
        SaveWithPoints(drawn, points, file);
        ctx.Report.Images.Add(new ImageResult($"pose-{label}", file, Width, Height, covered, share * 100));

        int tolerance = Math.Max(6, (cpuBottom - cpuTop) / 20);
        bool boxAgrees = right >= 0
            && Math.Abs(left - cpuLeft) <= tolerance && Math.Abs(right - cpuRight) <= tolerance
            && Math.Abs(top - cpuTop) <= tolerance && Math.Abs(bottom - cpuBottom) <= tolerance;
        if (sharedFrames > 0 || finalShared > 0)
            return $"SHARED PALETTE {label}: the hero's skin palette was also used by a crowd draw in {sharedFrames} frames. {summary}";
        if (!boxAgrees || share < 0.9f) return "MISMATCH " + summary;
        if (!expectUpsideDown) return null;

        // The pose itself, read from the picture: the head is drawn where the clip puts it, below
        // the hips, and nothing is drawn where a standing figure's head would be.
        HeadlessHarness.Assert(posedHead.Count > 0 && bindHead.Count > 0 && hipsJoint >= 0,
            $"{label}: the model has no Head and Hips to judge an upside-down pose by.");
        Point head = Project(Centroid(posedHead), viewProjection);
        Point standingHead = Project(Centroid(bindHead), viewProjection);
        Point hips = Project(hipsWorld, viewProjection);
        bool headDrawn = Near(mask, head.X, head.Y, 3);
        bool standingHeadDrawn = Near(mask, standingHead.X, standingHead.Y, 0);
        if (head.Y <= hips.Y || !headDrawn || standingHeadDrawn)
            return $"UPRIGHT {label}: head at {head} (drawn {headDrawn}), hips at {hips}, a standing head at {standingHead} "
                + $"(drawn {standingHeadDrawn}); the clip frame has the head below the hips. {summary}";
        return null;
    }

    private static Vector3 Centroid(List<Vector3> points)
    {
        Vector3 sum = Vector3.Zero;
        foreach (Vector3 point in points) sum += point;
        return sum / Math.Max(1, points.Count);
    }

    private static Point Project(Vector3 world, Matrix4x4 viewProjection)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        float w = MathF.Abs(clip.W) < 1e-6f ? 1e-6f : clip.W;
        return new Point(
            (int)MathF.Round((clip.X / w * 0.5f + 0.5f) * Width),
            (int)MathF.Round((0.5f - clip.Y / w * 0.5f) * Height));
    }

    private static readonly MeshDrawCall[] Draws = new MeshDrawCall[16384];

    /// <summary>The skin palettes the crowd's draws used in the last frame drawn.</summary>
    private static readonly HashSet<int> CrowdPalettes = [];

    private static byte[] Frame(IRenderController renderer, string project, Matrix4x4 view, Matrix4x4 projection,
        Vector3 eye, Vector3 target, EcsWorld? world, out RenderStats stats) =>
        Frame(renderer, project, view, projection, eye, target, world, read: true, out stats, out _, out _)!;

    /// <param name="heroPalette">The skin palette of the first draw near the origin (the hero), or 0.</param>
    /// <param name="shared">How many of the hero's palettes a crowd draw (beyond <see cref="Crowd.Offset"/>/2) also uses.</param>
    private static byte[]? Frame(IRenderController renderer, string project, Matrix4x4 view, Matrix4x4 projection,
        Vector3 eye, Vector3 target, EcsWorld? world, bool read, out RenderStats stats, out int heroPalette, out int shared)
    {
        Mesh3DState state = Mesh3DState.Default;
        state.LightingEnabled = false;
        state.FogEnabled = false;
        state.ShadowsEnabled = false;
        state.ShowFloor = false;
        state.ShowSunVisual = false;
        state.FrustumCullingEnabled = true;
        state.BackgroundColor = new Vector3(0.02f, 0.03f, 0.05f);
        renderer.SetMesh3DState(state);
        renderer.Set3DFrameActive(true);
        renderer.SetCamera3D(view, projection);
        MeshDrawCall[] draws = Draws;
        int count = 0;
        if (world != null)
            ObjectDrawPass.SubmitMeshes3D(world, project, draws, ref count, renderer, eye,
                Vector3.Normalize(target - eye), view * projection);
        HeadlessHarness.Assert(count < draws.Length, "The draw buffer filled up; draws were dropped.");
        heroPalette = 0;
        var heroPalettes = new HashSet<int>();
        HashSet<int> crowdPalettes = CrowdPalettes;
        crowdPalettes.Clear();
        for (int i = 0; i < count; i++)
        {
            int id = draws[i].SkinPalette.Id;
            if (id <= 0) continue;
            if (MathF.Abs(draws[i].World.M43) < Crowd.Offset * 0.5f)
            {
                if (heroPalette == 0) heroPalette = id;
                heroPalettes.Add(id);
            }
            else crowdPalettes.Add(id);
        }

        heroPalettes.IntersectWith(crowdPalettes);
        shared = heroPalettes.Count;
        renderer.BeginFrame();
        renderer.Clear(0.02f, 0.03f, 0.05f);
        if (count > 0) renderer.DrawMeshBatch(draws.AsSpan(0, count));
        renderer.EndFrame();
        byte[]? pixels = null;
        if (read)
            HeadlessHarness.Assert(renderer.TryReadSubmittedFramePixels(out int width, out int height, out pixels)
                && width == Width && height == Height, "The frame could not be read back.");
        stats = renderer.GetStats();
        renderer.Present();
        return pixels;
    }

    /// <summary>Every level-0 skinned vertex of a drawn mesh, skinned exactly as the vertex shader does it.</summary>
    /// <param name="focus">Also receives the vertices whose heaviest joint is <paramref name="focusJoint"/>.</param>
    internal static List<Vector3> SkinnedWorldPositions(GModelAsset asset, Matrix4x4[] palette, Matrix4x4 world,
        int focusJoint = -1, List<Vector3>? focus = null)
    {
        var positions = new List<Vector3>();
        Span<float> weights = stackalloc float[4];
        Span<float> joints = stackalloc float[4];
        ModelHairSelection hair = ModelHairRuntime.Resolve(asset, default);
        for (int index = 0; index < asset.Meshes.Count; index++)
        {
            GModelMesh mesh = asset.Meshes[index];
            if (mesh?.SkinnedVertices == null || !mesh.IsSkinned || mesh.Lod != 0) continue;
            if (ModelCollisionNames.IsCollisionOnly(asset, index) || !hair.IsVisible(mesh.Name)) continue;
            foreach (SkinnedMeshVertex vertex in mesh.SkinnedVertices)
            {
                vertex.JointWeights.CopyTo(weights);
                vertex.JointIndices.CopyTo(joints);
                float total = weights[0] + weights[1] + weights[2] + weights[3];
                Vector3 local = vertex.Position;
                int heaviest = -1;
                float heaviestWeight = 0f;
                if (total > 0.0001f)
                {
                    local = Vector3.Zero;
                    for (int k = 0; k < 4; k++)
                    {
                        if (weights[k] <= 0.0001f) continue;
                        int joint = (int)MathF.Round(joints[k]);
                        Matrix4x4 skin = (uint)joint < (uint)palette.Length ? palette[joint] : Matrix4x4.Identity;
                        local += Vector3.Transform(vertex.Position, skin) * weights[k];
                        if (weights[k] > heaviestWeight) { heaviestWeight = weights[k]; heaviest = joint; }
                    }

                    local /= total;
                }

                Vector3 placed = Vector3.Transform(local, world);
                positions.Add(placed);
                if (focus != null && heaviest == focusJoint && focusJoint >= 0) focus.Add(placed);
            }
        }

        return positions;
    }

    private static string DescribePose(GModelAsset asset, Matrix4x4[] locals, Matrix4x4[] palette)
    {
        int hips = asset.Rig!.Bones.FindIndex(bone => string.Equals(bone.Name, "Hips", StringComparison.OrdinalIgnoreCase));
        if (hips < 0) return "no Hips bone";
        Matrix4x4[] posedWorld = GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, locals);
        Matrix4x4 skin = palette[hips];
        float trace = skin.M11 + skin.M22 + skin.M33;
        float turn = MathF.Acos(Math.Clamp((trace - 1f) * 0.5f, -1f, 1f)) * 180f / MathF.PI;
        return $"Hips turned {turn:0.0} deg from bind, at Y {posedWorld[hips].M42:0.000}";
    }

    private static bool Near(bool[] mask, int x, int y, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            int px = x + dx, py = y + dy;
            if (px >= 0 && py >= 0 && px < Width && py < Height && mask[py * Width + px]) return true;
        }

        return false;
    }

    private static void SaveWithPoints(byte[] bgra, List<Point> points, string file)
    {
        using Bitmap bitmap = new(Width, Height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { Marshal.Copy(bgra, 0, data.Scan0, bgra.Length); }
        finally { bitmap.UnlockBits(data); }
        // Every 40th CPU-skinned vertex as a green dot: where the pose says the surface is.
        for (int i = 0; i < points.Count; i += 40)
            bitmap.SetPixel(points[i].X, points[i].Y, Color.Lime);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        bitmap.Save(file, ImageFormat.Png);
    }
}
