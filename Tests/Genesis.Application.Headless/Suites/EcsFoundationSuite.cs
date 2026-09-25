using System.Numerics;
using Genesis.Runtime;
using Genesis.Runtime.ECS;
using Genesis.Runtime.Systems;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Rendering;

namespace Genesis.Application.Headless.Suites;

/// <summary>Cheap CPU-only checks for the Phase 1 visibility/LOD ECS foundation.</summary>
internal static class EcsFoundationSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Runtime.ECS");

        HeadlessHarness.RunCase(ctx.Report, "Runtime.ECS.VisibilityAndSystemOrder", () =>
        {
            HeadlessHarness.Assert(SystemPhase.Visibility < SystemPhase.Lod,
                "Visibility must execute before LOD selection.");
            HeadlessHarness.Assert(SystemPhase.Lod < SystemPhase.FrameBudget,
                "LOD selection must execute before the frame-budget arbiter.");
            HeadlessHarness.Assert(SystemPhase.FrameBudget < SystemPhase.RenderThreshold,
                "Frame-budget preparation must finish before render-system execution.");

            using RuntimeScene scene = CreateProbeScene();

            Entity visible = scene.CreateEntity(new Vector3(0f, 0f, -10f));
            scene.World.Set(visible, VisibilityComponent.Default(1f));
            scene.World.Set(visible, DistanceLod());

            Entity behind = scene.CreateEntity(new Vector3(0f, 0f, 10f));
            scene.World.Set(behind, VisibilityComponent.Default(1f));
            scene.World.Set(behind, DistanceLod());

            Entity disabled = scene.CreateEntity(new Vector3(0f, 0f, -5f));
            VisibilityComponent authoredOff = VisibilityComponent.Default(1f);
            authoredOff.Enabled = false;
            scene.World.Set(disabled, authoredOff);

            scene.UpdateVariable(0f);

            HeadlessHarness.Assert(
                scene.World.GetRef<VisibilityComponent>(visible).LastResult == VisibilityResult.Visible,
                "A sphere directly in front of the camera was not marked visible.");
            HeadlessHarness.Assert(
                scene.World.GetRef<LodComponent>(visible).SelectionValid == 1,
                "LOD selection did not run for a visible entity.");

            HeadlessHarness.Assert(
                scene.World.GetRef<VisibilityComponent>(behind).LastResult == VisibilityResult.FrustumCulled,
                "A sphere behind the camera was not frustum-culled.");
            HeadlessHarness.Assert(
                scene.World.GetRef<LodComponent>(behind).SelectionValid == 0,
                "LOD ran before visibility or selected an entity already rejected by visibility.");

            HeadlessHarness.Assert(
                scene.World.GetRef<VisibilityComponent>(disabled).LastResult == VisibilityResult.Disabled,
                "Authored visibility disablement was ignored.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.ECS.AutoFrustumDistanceCulling", () =>
        {
            // R7.10: frustum + distance + sub-pixel before LOD / submit.
            using RuntimeScene scene = CreateProbeScene();
            RenderAutoState.FrustumCulling = true;
            RenderAutoState.DistanceCulling = true;
            RenderAutoState.SubPixelCulling = true;
            RenderAutoState.SubPixelCullPixels = 1f;
            RenderAutoState.ResetFrameCounters();

            Entity visible = scene.CreateEntity(new Vector3(0f, 0f, -10f));
            scene.World.Set(visible, VisibilityComponent.Default(1f));

            Entity behind = scene.CreateEntity(new Vector3(0f, 0f, 10f));
            scene.World.Set(behind, VisibilityComponent.Default(1f));

            Entity farAway = scene.CreateEntity(new Vector3(0f, 0f, -150f));
            VisibilityComponent farVis = VisibilityComponent.Default(1f);
            farVis.MaxDrawDistance = 80f;
            scene.World.Set(farAway, farVis);

            Entity tinyDistant = scene.CreateEntity(new Vector3(0f, 0f, -180f));
            scene.World.Set(tinyDistant, VisibilityComponent.Default(0.01f));

            scene.UpdateVariable(0f);

            HeadlessHarness.Assert(
                scene.World.GetRef<VisibilityComponent>(visible).LastResult == VisibilityResult.Visible,
                "In-frustum near sphere must stay Visible.");
            HeadlessHarness.Assert(
                scene.World.GetRef<VisibilityComponent>(behind).LastResult == VisibilityResult.FrustumCulled,
                "Sphere behind the camera must be FrustumCulled.");
            HeadlessHarness.Assert(
                scene.World.GetRef<VisibilityComponent>(farAway).LastResult == VisibilityResult.DistanceCulled,
                "Sphere past authored MaxDrawDistance (still inside camera far) must be DistanceCulled.");
            HeadlessHarness.Assert(
                scene.World.GetRef<VisibilityComponent>(tinyDistant).LastResult == VisibilityResult.SubPixelCulled,
                "Tiny distant sphere must be SubPixelCulled.");

            HeadlessHarness.Assert(
                VisibilitySystem.IsSubPixelCulled(180f, 0.01f, 720f, MathF.PI / 2f, 1f),
                "Sub-pixel helper must agree with the VisibilitySystem threshold.");
            HeadlessHarness.Assert(
                !VisibilitySystem.IsSubPixelCulled(10f, 1f, 720f, MathF.PI / 2f, 1f),
                "Near large sphere must not be treated as sub-pixel.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.ECS.LodSelectionAndCost", () =>
        {
            LodComponent distanceLod = DistanceLod();
            HeadlessHarness.Assert(LodSystem.SelectDistanceLevel(5f, in distanceLod) == 0,
                "Distance LOD0 selection changed.");
            HeadlessHarness.Assert(LodSystem.SelectDistanceLevel(15f, in distanceLod) == 1,
                "Distance LOD1 selection changed.");
            HeadlessHarness.Assert(LodSystem.SelectDistanceLevel(25f, in distanceLod) == 2,
                "Distance LOD2 selection changed.");
            HeadlessHarness.Assert(LodSystem.SelectDistanceLevel(35f, in distanceLod) == 3,
                "Distance LOD3 selection changed.");

            distanceLod.Selected = 1;
            distanceLod.Bias = 2;
            distanceLod.Cost0 = 1000;
            distanceLod.Cost1 = 500;
            distanceLod.Cost2 = 250;
            distanceLod.Cost3 = 100;
            HeadlessHarness.Assert(distanceLod.EffectiveSelected == 3,
                "LOD budget bias did not clamp onto the local selection.");
            HeadlessHarness.Assert(distanceLod.EffectiveCost == 100,
                "Effective LOD cost did not follow the biased level.");
            HeadlessHarness.Assert(distanceLod.Selected == 1,
                "Reading the biased LOD must not mutate the local hysteresis selection.");

            using RuntimeScene scene = CreateProbeScene();
            Entity entity = scene.CreateEntity(new Vector3(0f, 0f, -80f));
            scene.World.Set(entity, VisibilityComponent.Default(1f));

            LodComponent screenLod = LodComponent.Default;
            screenLod.ScreenErrorPixels = 2f;
            byte at720 = LodSystem.SelectScreenLevel(
                80f, Vector3.One, scene.World, entity, 720f, MathF.PI / 3f, in screenLod);
            byte at2160 = LodSystem.SelectScreenLevel(
                80f, Vector3.One, scene.World, entity, 2160f, MathF.PI / 3f, in screenLod);
            HeadlessHarness.Assert(at2160 < at720,
                $"Screen-aware LOD did not retain more detail at higher resolution ({at720} vs {at2160}).");

            // AetherForge's useful behaviour is retained: do not chatter at a threshold. With a
            // 10% band, crossing 10 by only 5% stays on LOD0; moving beyond 11 switches to LOD1.
            Entity hysteresisEntity = scene.CreateEntity(new Vector3(0f, 0f, -9f));
            scene.World.Set(hysteresisEntity, VisibilityComponent.Default(1f));
            LodComponent hysteresisLod = DistanceLod();
            hysteresisLod.Hysteresis = 10;
            scene.World.Set(hysteresisEntity, hysteresisLod);

            scene.UpdateVariable(0f);
            HeadlessHarness.Assert(scene.World.GetRef<LodComponent>(hysteresisEntity).Selected == 0,
                "Initial near LOD selection was not LOD0.");

            ref Transform3DComponent transform = ref scene.World.GetRef<Transform3DComponent>(hysteresisEntity);
            transform.Position = new Vector3(0f, 0f, -10.5f);
            scene.UpdateVariable(0f);
            HeadlessHarness.Assert(scene.World.GetRef<LodComponent>(hysteresisEntity).Selected == 0,
                "LOD chattered inside the authored hysteresis band.");

            transform.Position = new Vector3(0f, 0f, -11.2f);
            scene.UpdateVariable(0f);
            HeadlessHarness.Assert(scene.World.GetRef<LodComponent>(hysteresisEntity).Selected == 1,
                "LOD did not leave the hysteresis band after crossing its outer edge.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.ECS.FrameBudgetFeedback", () =>
        {
            using RuntimeScene scene = CreateProbeScene();
            HeadlessHarness.Assert(!scene.FrameBudgetSettings.Enabled,
                "Frame budgeting must remain opt-in until project/editor preferences expose it.");
            HeadlessHarness.Assert(scene.FrameBudgetSettings.RelaxFrames == 45,
                "The anti-oscillation relaxation window changed from the AetherForge-derived default.");

            Entity entity = scene.CreateEntity(new Vector3(0f, 0f, -5f));
            scene.World.Set(entity, VisibilityComponent.Default(1f));
            LodComponent lod = new LodComponent
            {
                Policy = LodSelectionPolicy.Fixed,
                Selected = 0,
                SelectionValid = 0,
                Cost0 = 2400,
                Cost1 = 1200,
                Cost2 = 200,
                Cost3 = 100,
            };
            scene.World.Set(entity, lod);

            // Disabled means no hidden quality change, even if a stale bias was present on the data.
            ref LodComponent authored = ref scene.World.GetRef<LodComponent>(entity);
            authored.Bias = 3;
            scene.UpdateVariable(0f);
            HeadlessHarness.Assert(scene.World.GetRef<LodComponent>(entity).Bias == 0,
                "Disabled frame budgeting left a stale runtime LOD bias behind.");

            FrameBudgetSettings settings = scene.FrameBudgetSettings;
            settings.Enabled = true;
            settings.TriangleBudget = 1000;
            settings.RelaxFrames = 3;

            scene.UpdateVariable(0f);
            HeadlessHarness.Assert(scene.FrameBudgetState.GlobalLodBias == 1,
                "An over-budget visible set did not tighten by one LOD level in one frame.");
            HeadlessHarness.Assert(scene.FrameBudgetState.EffectiveTriangleCost == 1200,
                "Frame-budget cost did not follow the newly applied global bias.");

            scene.UpdateVariable(0f);
            HeadlessHarness.Assert(scene.FrameBudgetState.GlobalLodBias == 2,
                "A still-over-budget set did not converge by one additional level on the next frame.");
            HeadlessHarness.Assert(scene.FrameBudgetState.EffectiveTriangleCost == 200,
                "Second-stage budget tightening selected the wrong effective cost.");

            scene.UpdateVariable(0f);
            scene.UpdateVariable(0f);
            HeadlessHarness.Assert(scene.FrameBudgetState.GlobalLodBias == 2,
                "LOD bias relaxed before the configured consecutive-headroom window elapsed.");
            scene.UpdateVariable(0f);
            HeadlessHarness.Assert(scene.FrameBudgetState.GlobalLodBias == 1,
                "LOD bias did not relax by one level after sustained sub-half-budget headroom.");

            // Reset, then prove that real backend timing can independently request emergency LOD.
            settings.Enabled = false;
            scene.UpdateVariable(0f);
            settings.Enabled = true;
            settings.TriangleBudget = 10_000;
            settings.RelaxFrames = 45;
            scene.ObserveRenderFrame(50.0);
            scene.UpdateVariable(0f);
            HeadlessHarness.Assert(scene.FrameBudgetState.GpuOverBudget,
                "Resolved GPU timing above the target was not recognised as over budget.");
            HeadlessHarness.Assert(scene.FrameBudgetState.GlobalLodBias == 1,
                "GPU pressure did not tighten the global LOD bias.");
        });
    }

    private static RuntimeScene CreateProbeScene()
    {
        RuntimeScene scene = new("ECS foundation probe");
        scene.Camera3D.Position = Vector3.Zero;
        scene.Camera3D.Yaw = 0f;
        scene.Camera3D.Pitch = 0f;
        scene.Camera3D.FieldOfView = MathF.PI / 2f;
        scene.Camera3D.AspectRatio = 16f / 9f;
        scene.Camera3D.NearPlane = 0.1f;
        scene.Camera3D.FarPlane = 200f;
        scene.RenderViewportHeightPixels = 720f;
        return scene;
    }

    private static LodComponent DistanceLod() => new LodComponent
    {
        Policy = LodSelectionPolicy.Distance,
        Distance0 = 10f,
        Distance1 = 20f,
        Distance2 = 30f,
        Hysteresis = 10,
        Selected = 0,
        Bias = 0,
        SelectionValid = 0,
    };
}
