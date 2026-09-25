using System.Drawing;
using System.Numerics;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Runtime.Modeling;
using Newtonsoft.Json;

namespace Genesis.Application.Headless.Suites;

internal static partial class ModelRigWizardSuite
{
    private static void Editing(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigWizard.IndependentCorrectionsAndDirectBinding", () =>
        {
            var detected = ModelRigWizardWorkflow.Detect(Fixture(GModelBodyPlan.Humanoid, 2), new() { ArmPairs = 2, ReuseExistingRig = false });
            var before = detected.RigWizard.Joints.ToDictionary(j => j.Role, j => j.Position);
            const string role = "Arm1.Elbow.L";
            var position = before[role] + new Vector3(.025f, .025f, .015f);
            ModelRigWizardWorkflow.EditLandmark(detected, role, position);
            Assert(detected.RigWizard.Joints.All(j => j.Position == (j.Role == role ? position : before[j.Role])),
                "A default template correction moved a mirrored joint or descendant.");
            var worlds = GModelPrimitiveFactory.ComputeWorldTransforms(detected.Rig.Bones, ModelPoseWorkflow.BindPose(detected));
            Assert(detected.RigWizard.Joints.All(j => Vector3.Distance(worlds[j.BoneIndex].Translation, j.Position) < .0001f),
                "Template correction changed an unselected world position through the hierarchy.");
            foreach (var joint in detected.RigWizard.Joints) { joint.Reviewed = false; joint.Issue = "Ambiguous placement"; }
            Assert(!detected.RigWizard.OrientationConfirmed, "Fixture should not have acknowledged orientation.");
            var bound = ModelRigWizardWorkflow.Bind(detected);
            Assert(bound.RigWizard.Bound && bound.Meshes.All(m => m.IsSkinned), "Direct binding required acknowledgement steps.");
            Assert(bound.RigWizard.Joints.Any(j => j.Issue.Length > 0 && !j.Reviewed), "Binding hid advisory placement warnings.");
            Neutral(bound);
            Assert(ModelRigWizardMotions.Generate(bound, [new()]).Animations.Count == 1, "Motion generation required extra confirmations.");
            var invalid = ModelPoseWorkflow.Copy(detected);
            var chain = invalid.RigWizard.Chains[0];
            invalid.RigWizard.Joints.Single(j => j.Role == chain.Roles[1]).Position = invalid.RigWizard.Joints.Single(j => j.Role == chain.Roles[0]).Position;
            Reject(() => ModelRigWizardWorkflow.Bind(invalid));
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigWizard.UseRigAdjustBindAndApplyWithoutPrompts", () =>
        {
            var source = ModelRigWizardWorkflow.Bind(ModelRigWizardWorkflow.Detect(Fixture(GModelBodyPlan.Humanoid, 1), new() { ReuseExistingRig = false }));
            source = ModelRigWizardMotions.Generate(source, [new()]);
            string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, "Independent template rig");
            StudioModelResourceLoader.SaveCanonical(path, source);
            string original = JsonConvert.SerializeObject(source);
            using var editor = new ModelEditorControl(path, ctx.Project!.RootPath);
            using var parent = editor.CreateAnimationStudio();
            using (var wizard = parent.CreateRigWizard())
            {
                Show(wizard); wizard.Setup.ReuseExistingRig = false;
                Await(wizard.DetectAsync());
                var controls = Editor3DInspectionSuite.Descendants(wizard).ToArray();
                var mirror = controls.OfType<CheckBox>().Single(c => c.Text == "Mirror corrections");
                Assert(!mirror.Checked, "Wizard defaults to moving the mirrored limb.");
                Assert(!controls.OfType<Button>().Any(b => b.Text.StartsWith("Confirm", StringComparison.Ordinal)), "Wizard still requires per-joint confirmation.");
                var list = (ListBox)wizard.Controls.Find("RigWizardLandmarks", true).Single();
                list.SelectedIndex = wizard.Setup.Joints.FindIndex(j => j.Role == "Arm1.Elbow.L");
                var before = wizard.Setup.Joints.ToDictionary(j => j.Role, j => j.Position);
                var xRow = Editor3DInspectionSuite.Descendants(wizard).OfType<FlowLayoutPanel>()
                    .Single(row => row.Controls.OfType<Label>().Any(label => label.Text == "X"));
                xRow.Controls.OfType<NumericUpDown>().Single().Value += .025m;
                Assert(wizard.Setup.Joints.Where(j => j.Role != "Arm1.Elbow.L").All(j => j.Position == before[j.Role]),
                    "Numeric template editing moved another joint.");
                var apply = (Button)wizard.Controls.Find("ApplyRigWizard", true).Single();
                Assert(apply.Enabled && apply.Text == "Use Rig" && !wizard.Setup.Bound, "An unbound fitted rig cannot use the normal rigging workflow.");
                Editor3DInspectionSuite.Capture(ctx, wizard, "wizard-independent-joints-use-rig");
                apply.PerformClick();
                Assert(wizard.DialogResult == DialogResult.OK, "Use Rig did not accept the template directly.");
                parent.ApplyRigWizardResult(wizard.Result, wizard.SelectedClip);
            }
            Assert(parent.SelectedPage == 0 && parent.Result.Meshes.All(m => !m.IsSkinned), "Use Rig did not return an editable unbound layout to Rigging.");
            Show(parent);
            var view = parent.Preview; view.FrameModelForTest();
            view.Viewport.Camera.Yaw = MathF.PI; view.Viewport.Camera.Pitch = 0;
            using (var frame = view.Viewport.CaptureFrame(4)) Assert(frame is not null, "Rigging viewport did not render.");
            var rig = view.RiggedAsset!; var rest = view.CaptureWorkingPose();
            var worlds = GModelPrimitiveFactory.ComputeWorldTransforms(rig.Rig.Bones, rest);
            int elbow = rig.Rig.Bones.FindIndex(b => b.Name == "Arm1.Elbow.L");
            int hand = rig.Rig.Bones.FindIndex(b => b.Name == "Arm1.Hand.L");
            // Even a remembered pose propagation setting must not spread a layout edit.
            view.FollowAnimationChildren = true;
            var start = At(worlds[elbow].Translation); var end = new Point(start.X + 12, start.Y + 10);
            Assert(view.DirectPointerDown(start) && !view.SelectedRigElementIsBone, "Joint centre was treated as the incoming bone.");
            view.DirectPointerMove(end); view.DirectPointerUp(end);
            var moved = GModelPrimitiveFactory.ComputeWorldTransforms(rig.Rig.Bones, view.CaptureWorkingPose());
            Assert(Vector3.Distance(moved[elbow].Translation, worlds[elbow].Translation) > .005f, "Joint did not move.");
            UnchangedExcept(moved, worlds, elbow);
            view.Undo();
            start = At((worlds[elbow].Translation + worlds[hand].Translation) * .5f); end = new(start.X + 10, start.Y + 8);
            Assert(view.DirectPointerDown(start) && view.SelectedRigElementIsBone, "Bone midpoint missed.");
            view.DirectPointerMove(end); view.DirectPointerUp(end);
            moved = GModelPrimitiveFactory.ComputeWorldTransforms(rig.Rig.Bones, view.CaptureWorkingPose());
            UnchangedExcept(moved, worlds, elbow, hand);
            var delta = moved[elbow].Translation - worlds[elbow].Translation;
            Assert(delta.Length() > .005f && Vector3.Distance(moved[hand].Translation - worlds[hand].Translation, delta) < .0001f,
                "Moving a bone did not translate only its endpoints.");
            ((Button)parent.Controls.Find("ModelActionBindToMesh", true).Single()).PerformClick();
            Assert(parent.SelectedPage == 1 && parent.Result.RigWizard!.Bound, "Normal Bind To Mesh did not bind the adjusted template.");
            foreach (var joint in parent.Result.RigWizard!.Joints)
                Assert(Vector3.Distance(joint.Position, moved[joint.BoneIndex].Translation) < .0001f, "Binding lost corrected wizard landmarks.");
            Editor3DInspectionSuite.Capture(ctx, parent, "wizard-normal-rig-binding");
            ((Button)parent.Controls.Find("ApplyModelAnimation", true).Single()).PerformClick();
            Assert(parent.DialogResult == DialogResult.OK, "Normal Apply to model refused the template.");
            editor.ApplyAnimationWorkspace(parent.Result, parent.SelectedClip);
            editor.Undo(); Assert(editor.RiggedAsset!.Animations.Count == source.Animations.Count, "Undo did not restore the previous rig and clips.");
            editor.Redo(); editor.Save();
            using var reopened = new ModelEditorControl(path, ctx.Project.RootPath);
            Assert(reopened.RiggedAsset!.RigWizard!.Bound, "Saved template lost its ordinary binding state.");
            foreach (var joint in reopened.RiggedAsset.RigWizard.Joints)
                Assert(Vector3.Distance(joint.Position, moved[joint.BoneIndex].Translation) < .0001f, "Save/reopen moved a corrected joint.");
            Assert(JsonConvert.SerializeObject(source) == original, "Template editing changed the input model.");

            Point At(Vector3 position)
            {
                var viewport = view.Viewport; var screen = viewport.WorldToSurface(position - rig.Pivot.Position);
                return new((int)Math.Round(screen.X * viewport.Host.Width / viewport.SurfaceWidth), (int)Math.Round(screen.Y * viewport.Host.Height / viewport.SurfaceHeight));
            }
        });
    }
    private static void UnchangedExcept(Matrix4x4[] actual, Matrix4x4[] expected, params int[] changed)
    {
        for (int i = 0; i < actual.Length; i++)
            if (!changed.Contains(i)) Assert(Vector3.Distance(actual[i].Translation, expected[i].Translation) < .0001f, "Layout edit moved unselected joint " + i);
    }
}
