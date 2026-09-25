using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Headless.Suites;

internal static class ModelPoseWorkflowSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Model.PoseWorkflow");
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.PoseWorkflow.MixedAxisRotationInspectorRoundTrip", () =>
        {
            foreach (var degrees in new[] { new Vector3(35, 70, -25), new Vector3(-48, -135, 160), new Vector3(90, 45, 20), new Vector3(-90, -70, 32) })
            {
                var radians = degrees * (MathF.PI / 180);
                var expected = Quaternion.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z);
                var roundTrip = ModelPoseWorkflow.EulerDegrees(expected) * (MathF.PI / 180);
                var actual = Quaternion.CreateFromYawPitchRoll(roundTrip.Y, roundTrip.X, roundTrip.Z);
                Assert(Math.Abs(Quaternion.Dot(expected, actual)) > .99999f, "Inspector round-trip changed a mixed-axis rotation.");
            }
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.PoseWorkflow.QuaternionInterpolationAndBounds", () =>
        {
            var asset = Fixture();
            var rest = ModelPoseWorkflow.BindPose(asset);
            var end = (Matrix4x4[])rest.Clone();
            end[1] = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, 1.5f, 0);
            var a = ModelPoseWorkflow.SavePose(asset, "Rest", rest);
            var b = ModelPoseWorkflow.SavePose(asset, "Raised", end);
            var recipe = new GModelPoseAnimation { Name = "Wave", FrameCount = 61, Fps = 30, Loop = false,
                Keys = [new() { Frame = 1, PoseId = a.Id }, new() { Frame = 61, PoseId = b.Id }] };
            var clip = ModelPoseWorkflow.Generate(asset, recipe);
            Assert(clip.Frames.Count == 61 && asset.PoseAnimations.Count == 1, "Generation did not create the clip and recipe.");
            var midpoint = clip.Frames[30].LocalBoneTransforms[1];
            var axis = Vector3.TransformNormal(Vector3.UnitX, midpoint);
            Assert(Vector3.Distance(axis, new(MathF.Sqrt(.5f), MathF.Sqrt(.5f), 0)) < .0001f, "Rotation did not interpolate in 3D.");
            Assert(asset.Rig.Bones.Select(bone => bone.BindLocal).SequenceEqual(rest), "Posing changed the bind skeleton.");
            recipe.Keys[0].Interpolation = GModelPoseInterpolation.Hold;
            clip = ModelPoseWorkflow.Generate(asset, recipe);
            Assert(clip.Frames[59].LocalBoneTransforms[1] == rest[1] && clip.Frames[60].LocalBoneTransforms[1] == end[1], "Hold lost its endpoint.");
            Assert(asset.Animations.Count == 1, "Regeneration duplicated the clip.");
            var expectedWorld = GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, clip.Frames[30].LocalBoneTransforms);
            var previewWorld = ModelRigBridge.BoneWorldTransforms(asset, clip.Name, 1f);
            Assert(expectedWorld.SequenceEqual(previewWorld), "Skeleton overlay ignored the clip FPS.");
            recipe.Keys[0].Interpolation = GModelPoseInterpolation.Smooth;
            clip = ModelPoseWorkflow.Generate(asset, recipe);
            Assert(clip.Frames[0].LocalBoneTransforms.SequenceEqual(rest), "Smooth changed the first pose.");
            var shortArc = ModelPoseWorkflow.Interpolate(Matrix4x4.CreateRotationY(170 * MathF.PI / 180), Matrix4x4.CreateRotationY(-170 * MathF.PI / 180), .5f);
            Assert(Vector3.TransformNormal(Vector3.UnitZ, shortArc).Z < -.99f, "Quaternion interpolation took the long path.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.PoseWorkflow.SelectedJointAndBranchControl", () =>
        {
            var asset = Fixture(); var bind = ModelPoseWorkflow.BindPose(asset);
            var world = GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, bind);
            var rotated = Matrix4x4.CreateRotationZ(.7f) * bind[1];
            var only = ModelPoseWorkflow.Transform(asset, bind, 1, rotated, false);
            var branch = ModelPoseWorkflow.Transform(asset, bind, 1, rotated, true);
            var onlyWorld = GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, only);
            var branchWorld = GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, branch);
            Assert(Vector3.Distance(onlyWorld[2].Translation, world[2].Translation) < .0001f, "Selected-only moved a descendant.");
            Assert(Vector3.Distance(branchWorld[2].Translation, world[2].Translation) > .1f, "Follow children failed to move the branch.");
            Assert(bind.SequenceEqual(ModelPoseWorkflow.BindPose(asset)), "Transform mutated the source pose.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.PoseWorkflow.InvalidAssignmentsPreserveGeneratedClip", () =>
        {
            var asset = Fixture(); var pose = ModelPoseWorkflow.SavePose(asset, "Rest", ModelPoseWorkflow.BindPose(asset));
            var recipe = new GModelPoseAnimation { Name = "Idle", Keys = [new() { PoseId = pose.Id }] };
            var previous = ModelPoseWorkflow.Generate(asset, recipe);
            recipe.Keys.Add(new() { PoseId = pose.Id });
            Reject(() => ModelPoseWorkflow.Generate(asset, recipe));
            Assert(ReferenceEquals(previous, asset.Animations[0]), "Invalid generation replaced the clip.");
            Reject(() => ModelPoseWorkflow.DeletePose(asset, pose.Id));
            ModelPoseWorkflow.SavePose(asset, "Renamed", pose.LocalBoneTransforms, pose.Id);
            Assert(asset.PoseAnimations[0].Keys[0].PoseId == pose.Id, "Pose rename broke references.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.PoseWorkflow.RigPoseGenerateSaveReopen", () => Workflow(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.PoseWorkflow.DrawnSkeletonBeforeBinding", () =>
        {
            var project = HeadlessHarness.Require(ctx.Project, "Project");
            string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, Genesis.Application.Core.Resources.ResourceKind.Model, "Template fitting");
            StudioModelResourceLoader.SaveCanonical(path, Fixture());
            using var editor = new ModelEditorControl(path, project.RootPath);
            using var dialog = editor.CreateAnimationStudio(0);
            dialog.NewSkeleton(); dialog.DrawBone(Vector3.Zero, new Vector3(0,1,0));
            Reject(() => dialog.GoToPage(1));
            Assert(!dialog.Preview.FollowAnimationChildren, "Skeleton placement must allow individual joint adjustment.");
            dialog.BindMesh(); dialog.GoToPage(1);
            Assert(dialog.Result.Rig.IsValid && dialog.Result.Poses.Count == 1, "Binding did not create a usable rest pose.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.PoseWorkflow.CancelNeverWritesAndCustomRigBinds", () =>
        {
            var project = HeadlessHarness.Require(ctx.Project, "Project"); var resources = HeadlessHarness.Require(ctx.Resources, "Resources");
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "CancelledModelRig");
            using var editor = new ModelEditorControl(path, project.RootPath);
            editor.ApplyAnimationWorkspace(Fixture(), ""); editor.Save();
            string source = File.ReadAllText(path), canonical = File.ReadAllText(StudioModelResourceLoader.CanonicalPath(path));
            using (var dialog = editor.CreateAnimationStudio())
            {
                dialog.NewSkeleton(); dialog.AddJointAt(-1, Vector3.Zero); dialog.AddJointAt(0, new Vector3(.3f, 2, 1)); dialog.BindMesh();
                Assert(dialog.Result.Rig.Bones.Count == 2 && dialog.Result.Meshes.All(m => m.IsSkinned), "Custom rig failed to bind.");
                var world = GModelPrimitiveFactory.ComputeWorldTransforms(dialog.Result.Rig.Bones, ModelPoseWorkflow.BindPose(dialog.Result));
                Assert(Vector3.Distance(world[1].Translation, new Vector3(.3f, 2, 1)) < .0001f, "3D joint placement ignored its parent or depth.");
                dialog.Preview.Save(); // A hidden File/Save shortcut cannot write a draft.
                dialog.CancelWork(); Assert(dialog.DialogResult == DialogResult.Cancel, "Cancel work did not cancel.");
            }
            Assert(File.ReadAllText(path) == source && File.ReadAllText(StudioModelResourceLoader.CanonicalPath(path)) == canonical, "Cancel wrote project files.");
            Assert(editor.RiggedAsset!.Rig.Bones.Count == 3, "Cancel changed the parent model.");
        });
    }

    private static void Workflow(HeadlessContext ctx)
    {
        var project = HeadlessHarness.Require(ctx.Project, "Project"); var resources = HeadlessHarness.Require(ctx.Resources, "Resources");
        string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "PoseWorkflowRobot");
        using var editor = new ModelEditorControl(path, project.RootPath);
        editor.ApplyAnimationWorkspace(Fixture(), ""); editor.Save();
        using var dialog = editor.CreateAnimationStudio(1);
        UnattendedWindowing.Configure(dialog); UnattendedWindowing.ShowWithoutFocus(dialog);
        var rest = dialog.SaveCurrentPose("Default");
        Assert(dialog.Preview.SelectAnimationNode("Shoulder"), "Joint missing from rig hierarchy.");
        Assert(dialog.Preview.RotateSelectedAnimationNode(new Vector3(0, 0, 55)), "Joint could not be posed.");
        var raised = dialog.Preview.CaptureWorkingPose();
        dialog.Preview.Undo(); Assert(dialog.Preview.CaptureWorkingPose().SequenceEqual(rest.LocalBoneTransforms), "Pose undo failed.");
        dialog.Preview.Redo(); Assert(dialog.Preview.CaptureWorkingPose().SequenceEqual(raised), "Pose redo failed.");
        var wave = dialog.SaveCurrentPose("Arm raised");
        Capture(dialog, dialog.Preview.Viewport, ctx, "model-pose-workflow-poses");
        dialog.GoToPage(0); Capture(dialog, dialog.Preview.Viewport, ctx, "model-pose-workflow-rig");
        dialog.GoToPage(2);
        var keys = (DataGridView)dialog.Controls.Find("ModelPoseAssignments", true).Single();
        keys.Rows.Add(1, rest.Id, "Smooth"); keys.Rows.Add(30, wave.Id, "Smooth"); keys.Rows.Add(60, rest.Id, "Linear");
        dialog.GenerateFrames();
        Assert(dialog.Preview.AnimationFrameCount == 60 && dialog.SelectedClip == "Animation", "Generated clip did not reach the dialog timeline.");
        var generated = dialog.Preview.RiggedAsset!.Animations.Single(c => c.Name == dialog.SelectedClip);
        generated.Loop = false;
        dialog.Preview.SetAnimationTime(.5f); dialog.Preview.ResumeAnimation(); dialog.Preview.PauseAnimation();
        Assert(dialog.Preview.AnimationTime == .5f && !dialog.Preview.IsAnimationPlaying, "Pause/resume restarted the clip.");
        dialog.Preview.SetAnimationTime(59 / 30f); dialog.Preview.ResumeAnimation();
        Assert(dialog.Preview.AnimationTime == 0 && dialog.Preview.IsAnimationPlaying, "Completed non-looping clip did not restart.");
        dialog.Preview.PauseAnimation(); generated.Loop = true;
        dialog.Preview.SetAnimationTime(29 / 30f);
        Capture(dialog, dialog.Preview.Viewport, ctx, "model-pose-workflow-animate");
        dialog.ClientSize = new Size(1080, 740); Capture(dialog, dialog.Preview.Viewport, ctx, "model-pose-workflow-narrow");
        var result = dialog.Result;
        Assert(result.Animations.All(c => !c.Name.StartsWith("Working pose")), "Transient pose escaped into the result.");
        editor.ApplyAnimationWorkspace(result, dialog.SelectedClip);
        Assert(editor.AnimationFrameCount == 60, "Generated clip did not reach the main timeline.");
        editor.Undo(); Assert(editor.AnimationClipCount == 0, "Workspace apply could not be undone.");
        editor.Redo(); Assert(editor.AnimationFrameCount == 60, "Workspace apply could not be redone.");
        editor.Save(); dialog.Close();
        using var reopened = new ModelEditorControl(path, project.RootPath, ModelEditorRole.Viewer);
        Assert(reopened.RiggedAsset!.Poses.Count == 2 && reopened.RiggedAsset.PoseAnimations.Count == 1, "Pose data was lost on save/reopen.");
        reopened.PlayClip("Animation", false); reopened.SetAnimationTime(29 / 30f);
        Assert(reopened.AnimationFrameCount == 60, "Viewer cannot see the saved clip.");
        Assert(!reopened.IsDirty, "Viewer animation preview changed the resource.");
        var evaluated = GModelPrimitiveFactory.EvaluateAnimatedLocals(reopened.RiggedAsset,
            new RuntimeModelAnimationState("Animation", 29 / 30f, 30, false));
        Assert(evaluated.SequenceEqual(wave.LocalBoneTransforms), "Runtime did not preserve the assigned frame pose.");
        using var host = new Form { ClientSize = new Size(1440, 920), Text = "Model Viewer · Generated animation" };
        host.Controls.Add(reopened); UnattendedWindowing.Configure(host); UnattendedWindowing.ShowWithoutFocus(host);
        reopened.SetSpin(false); Capture(host, reopened.Viewport, ctx, "model-pose-workflow-viewer");
    }

    private static void Capture(Form form, Genesis.Application.Editors.Suite.EditorViewport3D viewport, HeadlessContext ctx, string name)
    {
        ThemeService.Apply(form); System.Windows.Forms.Application.DoEvents();
        using (var frame = viewport.CaptureFrame(8))
        {
            Assert(frame is not null, "Animation viewport did not render a frame.");
            int warm = 0, cool = 0;
            for (int y = 0; y < frame!.Height; y += Math.Max(1, frame.Height / 140))
                for (int x = 0; x < frame.Width; x += Math.Max(1, frame.Width / 180))
                {
                    Color pixel = frame.GetPixel(x, y);
                    // Lighting desaturates the gold mesh; distinguish it from neutral grid pixels
                    // without requiring the saturated orange used by authoring overlays.
                    if (pixel.R > 60 && pixel.R > pixel.G * 1.025 && pixel.G > pixel.B * 1.2) warm++;
                    if (pixel.G > 60 && pixel.B > pixel.R * 1.3 && pixel.G > pixel.R * 1.2) cool++;
                }
            if (warm <= 4 || cool <= 4) frame.Save(Path.Combine(ctx.Captures, name + "-viewport-diagnostic.png"));
            Assert(warm > 4 && cool > 4, $"Animation viewport omitted the robot's body or limbs ({name}: warm={warm}, cool={cool}).");
        }
        var metrics = VisualCapture.CaptureOpenForm(form, Path.Combine(ctx.Captures, name + ".png"), includeViewports: true);
        ctx.Report.Images.Add(ImageResult.From(name, name + ".png", metrics));
        Assert(viewport.Width >= 420 && viewport.Height >= 280, "Animation panels leave too little viewport space.");
    }

    internal static GModelAsset Fixture()
    {
        var parts = new List<ModelPart>
        {
            new() { Name = "Body", Position = [0, .85f, 0], Scale = [.8f, 1.3f, .5f], Color = [.16f, .5f, .75f] },
            new() { Name = "Head", Primitive = ModelPrimitiveKind.Sphere, Position = [0, 1.9f, 0], Scale = [.7f,.7f,.7f], Color = [.95f,.67f,.26f] },
            new() { Name = "Arm", Position = [.9f, 1.5f, 0], Scale = [1.1f,.3f,.3f], Color = [.95f,.67f,.26f] },
            new() { Name = "Hand", Primitive = ModelPrimitiveKind.Sphere, Position = [1.6f, 1.5f, 0], Scale = [.35f,.35f,.35f], Color = [.95f,.67f,.26f] },
            new() { Name = "Left leg", Position = [-.22f,-.2f,0], Scale = [.3f,.9f,.4f], Color = [.2f,.35f,.55f] },
            new() { Name = "Right leg", Position = [.22f,-.2f,0], Scale = [.3f,.9f,.4f], Color = [.2f,.35f,.55f] },
        };
        var mesh = ModelPartBuilder.Bake(parts); var asset = ModelRigBridge.BuildAsset("Pose robot", mesh.Vertices, mesh.Indices);
        asset.Rig = new() { Bones = [new() { Name = "Root" }, new() { Name = "Shoulder", ParentIndex = 0, BindLocal = Matrix4x4.CreateTranslation(0,1.5f,0) },
            new() { Name = "Hand", ParentIndex = 1, BindLocal = Matrix4x4.CreateTranslation(1.6f,0,0) }] };
        asset.Materials.Add(new GModelMaterial { Name = "Robot", BaseColor = Vector4.One });
        GModelPrimitiveFactory.RebuildInverseBindMatrices(asset.Rig); GModelPrimitiveFactory.BindSkinToMesh(asset);
        // Deliberately rigid robot parts, so the fixture tests transforms without an auto-weighting judgment.
        for (int i = 0; i < asset.Meshes[0].SkinnedVertices.Length; i++)
        {
            var vertex = asset.Meshes[0].SkinnedVertices[i];
            vertex.JointIndices = new Vector4(vertex.Position.X > 1.45f ? 2 : vertex.Position.X > .4f ? 1 : 0, 0, 0, 0);
            vertex.JointWeights = new Vector4(1,0,0,0); asset.Meshes[0].SkinnedVertices[i] = vertex;
        }
        return asset;
    }
    private static void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new InvalidOperationException("Expected validation error."); }
    private static void Assert(bool value, string text) => HeadlessHarness.Assert(value, text);
}
