using System.Numerics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Headless.Suites;

internal static class ModelArcherAnimationSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Model.ArcherAnimation");
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.ArcherAnimation.StandingToCrouched", () =>
        {
            string root = Environment.GetEnvironmentVariable("GENESIS_ARCHER_SOURCE")
                ?? Path.GetFullPath("Ignore/AssetsForGPT/Models/Mixamo/Characters/Archer");
            string source = Path.Combine(root, "Stand", "Erika Archer.dae");
            string motion = Path.Combine(root, "Between Poses", "Stand To Crouch", "Standing To Crouched.dae");
            string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, "Archer animation review");
            using var viewer = new ModelViewerControl(path, ctx.Project!.RootPath);
            viewer.ImportExternalModel(source);
            var donor = ModelMotionImport.ReadDonor(motion);
            var names = viewer.ImportModelAsAnimation(motion);
            using var host = UnattendedWindowing.NewHost(1440, 920);
            host.Controls.Add(viewer); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
            viewer.SetCameraView("Front"); viewer.FrameModel();
            var asset = viewer.PreviewAsset; var clip = asset.Animations.Last();
            var log = new List<string>();
            bool differentDefaultPose = false;
            for (int i = 0; i < asset.Rig.Bones.Count; i++)
            {
                var bone = asset.Rig.Bones[i]; int j = donor.Rig.Bones.FindIndex(b => b.Name == bone.Name);
                if (j < 0) continue;
                differentDefaultPose |= MatrixDistance(bone.BindLocal, donor.Rig.Bones[j].BindLocal) > .1f;
                float deviation = clip.Frames.Select((f, frame) => MatrixDistance(f.LocalBoneTransforms[i], donor.Animations[0].Frames[frame].LocalBoneTransforms[j])).Max();
                HeadlessHarness.Assert(deviation < .001f, $"{bone.Name} lost authored local motion (difference {deviation}).");
                log.Add($"{i} {bone.Name}: maximum local-transform difference {deviation}");
            }
            HeadlessHarness.Assert(differentDefaultPose && clip.Frames.Count == 20, "Archer fixture no longer exercises different default poses and the full 20-frame clip.");
            File.WriteAllLines(Path.Combine(ctx.Workspace, "archer-transforms.txt"), log);
            var timeline = viewer.Controls.Find("ModelViewerTimeline", true).Single();
            WaitFor(() => Images(timeline).Count >= 12, "Visible front-view previews did not finish rendering: " + timeline.AccessibleDescription);
            foreach (int frame in new[] { 0, clip.Frames.Count / 2, clip.Frames.Count - 1 })
            {
                var eye = viewer.Viewport.Camera.Eye;
                viewer.SetFrame(frame); using var capture = viewer.Viewport.CaptureFrame(5);
                WaitFor(() => Images(timeline).ContainsKey(frame), "Selected frame preview did not render.");
                HeadlessHarness.Assert(viewer.Viewport.Camera.Eye == eye, "Thumbnail rendering changed the main camera.");
                Editor3DInspectionSuite.Capture(ctx, host, $"archer-frame-{frame + 1}");
            }
            HeadlessHarness.Assert(Editor3DInspectionSuite.Difference(Images(timeline)[0], Images(timeline)[19]) > 50, "Standing/crouching previews are blank or identical.");
            viewer.SetFrame(0);
            Invoke(timeline, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 240, 30, 0));
            Invoke(timeline, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 240, 30, 0));
            HeadlessHarness.Assert(viewer.CurrentFrame == 2, "Clicking the third preview did not select frame 3.");
            Invoke(timeline, "OnKeyDown", new KeyEventArgs(Keys.Right));
            HeadlessHarness.Assert(viewer.CurrentFrame == 3, "Right arrow did not step from the selected thumbnail.");
            viewer.SelectClip(""); HeadlessHarness.Assert(Images(timeline).Count == 0, "Clearing the animation retained stale previews.");
            viewer.SelectClip(names[0]); WaitFor(() => Images(timeline).ContainsKey(0), "Reselecting a clip did not regenerate previews.");
            viewer.Save();
            using var reopened = new ModelViewerControl(path, ctx.Project.RootPath);
            var saved = reopened.PreviewAsset.Animations.Last();
            HeadlessHarness.Assert(saved.Frames.Count == 20 && MatrixDistance(saved.Frames[10].LocalBoneTransforms[12], clip.Frames[10].LocalBoneTransforms[12]) < .00001f,
                "Save/reopen changed corrected Archer motion.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.FramePreviews.EditorScrollCacheAndClipChanges", () =>
        {
            string source = AnimatedGlbFixture.Write(Path.Combine(ctx.Workspace, "Filmstrip model"));
            string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, "Filmstrip editor");
            using var editor = new ModelEditorControl(path, ctx.Project!.RootPath); editor.ImportExternalModel(source);
            var asset = editor.PreviewAsset; var clip = asset.Animations[0];
            var original = clip.Frames.ToArray();
            clip.Frames = Enumerable.Range(0, 150).Select(i => new GModelAnimationFrame
                { LocalBoneTransforms = (Matrix4x4[])original[i % original.Length].LocalBoneTransforms.Clone() }).ToList();
            using var host = UnattendedWindowing.NewHost(1440, 920);
            host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
            editor.SelectClip(clip.Name);
            var timeline = editor.Controls.Find("ModelViewerTimeline", true).Single();
            var geometry = asset.Meshes[0].SkinnedVertices.Select(v => v.Position).ToArray();
            for (int frame = 0; frame < 150; frame += 10)
            {
                editor.SetFrame(frame);
                WaitFor(() => Images(timeline).ContainsKey(frame), "Scrolling stopped requesting missing previews.");
                HeadlessHarness.Assert(Images(timeline).Count <= 96, "Long clips exceeded the bounded preview cache.");
            }
            HeadlessHarness.Assert(!Images(timeline).ContainsKey(0), "Long clips never released distant frame images.");
            editor.SetFrame(0); WaitFor(() => Images(timeline).ContainsKey(0), "An evicted preview did not regenerate.");
            using var front = new Bitmap(Images(timeline)[0]);
            editor.SetCameraView("Back"); editor.SelectClip(""); editor.SelectClip(clip.Name);
            WaitFor(() => Images(timeline).ContainsKey(0), "Clip changes retained stale requests.");
            HeadlessHarness.Assert(Editor3DInspectionSuite.Difference(front, Images(timeline)[0]) == 0, "Main camera orientation changed the front-view thumbnails.");
            HeadlessHarness.Assert(asset.Meshes[0].IsSkinned && geometry.SequenceEqual(asset.Meshes[0].SkinnedVertices.Select(v => v.Position)),
                "Background previews modified the editable mesh.");
            Editor3DInspectionSuite.Capture(ctx, host, "model-editor-frame-previews");
        });
    }

    private static float MatrixDistance(Matrix4x4 a, Matrix4x4 b)
    {
        var d = a - b;
        return new Vector4(d.M11, d.M12, d.M13, d.M14).Length() + new Vector4(d.M21, d.M22, d.M23, d.M24).Length()
            + new Vector4(d.M31, d.M32, d.M33, d.M34).Length() + new Vector4(d.M41, d.M42, d.M43, d.M44).Length();
    }
    private static Dictionary<int, Bitmap> Images(Control control) => (Dictionary<int, Bitmap>)control.GetType().GetField("_images", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control)!;
    private static void Invoke(Control control, string method, object arg) => control.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, [arg]);
    private static void WaitFor(Func<bool> ready, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!ready() && DateTime.UtcNow < deadline) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); }
        HeadlessHarness.Assert(ready(), failure);
    }
}
