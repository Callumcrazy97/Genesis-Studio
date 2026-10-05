using System.Buffers.Binary;
using System.Drawing;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// A clips-only Model (an animation library other Models borrow through <c>animationLibraries</c>)
/// opens in the Model editor as an animation library: clip list, playback, bone-line and body
/// preview, and a Save that never rewrites the library.
/// </summary>
internal static class ModelClipsOnlySuite
{
    private static readonly string[] BoneNames = ["Root", "Shoulder", "Hand"];

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Model.ClipsOnly");
        var project = HeadlessHarness.Require(ctx.Project, "Project");
        var resources = HeadlessHarness.Require(ctx.Resources, "Resources");
        string library = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Robot moves");
        StudioModelResourceLoader.SaveCanonical(library, Library());
        string body = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Robot body");
        StudioModelResourceLoader.SaveCanonical(body, ModelPoseWorkflowSuite.Fixture());
        Genesis.Shared.Assets.ResourceCatalog.Invalidate(project.RootPath);

        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.ClipsOnly.OpensAsAnimationLibrary", () =>
        {
            using var editor = new ModelEditorControl(library, project.RootPath);
            using var host = Host(editor);
            Assert(editor.IsAnimationLibrary, "A clips-only Model did not open as an animation library.");
            Assert(editor.LibraryClipNames.SequenceEqual(["Wave", "Nod"]) && editor.LibraryListedClipCount == 2,
                "The library's clips were not listed: " + string.Join(", ", editor.LibraryClipNames));
            Assert(Math.Abs(editor.LibraryClipDurations[0] - 31 / 30f) < 1e-4f && Math.Abs(editor.LibraryClipDurations[1] - 16 / 30f) < 1e-4f,
                "Clip lengths were wrong: " + string.Join(", ", editor.LibraryClipDurations));
            Assert(editor.ActiveClip == "Wave" && editor.LibrarySelectedListClip == "Wave", "Opening a library did not select its first clip.");
            Control panel = editor.Controls.Find("ModelAnimationLibrary", true).SingleOrDefault()
                ?? throw new InvalidOperationException("The animation library panel is missing.");
            Assert(panel.Visible && panel.Width > 150 && panel.Height > 200, $"The animation library panel is hidden or too small ({panel.Size}).");
            Assert(editor.Controls.Find("ModelEditorModeShell", true).Length == 0, "Mesh editing tools were shown for a Model with no mesh.");
            Assert(editor.Controls.Find("EmptyModelHint", true).All(hint => !hint.Visible), "The \"No model loaded\" hint refused the library.");
            var import = editor.Controls.OfType<Control>().SelectMany(Descendants).OfType<ToolStrip>()
                .SelectMany(strip => strip.Items.Cast<ToolStripItem>()).FirstOrDefault(item => item.Name == "ImportModelFiles");
            Assert(import is null || !import.Enabled, "Import / Replace stayed available over an animation library.");

            using var viewer = new ModelViewerControl(library, project.RootPath);
            Assert(viewer.IsAnimationLibrary && viewer.LibraryClipNames.Count == 2, "The Model viewer did not open the library's clips.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.ClipsOnly.ScrubAndPlayChangeThePose", () =>
        {
            using var editor = new ModelEditorControl(library, project.RootPath);
            using var host = Host(editor);
            Assert(editor.SelectLibraryClip("Wave") && !editor.SelectLibraryClip("Missing"), "Clip selection by name failed.");
            editor.SetLibraryPreviewTime(0); Vector3[] start = editor.LibraryPreviewJointPositions();
            editor.SetLibraryPreviewTime(.25f); Vector3[] quarter = editor.LibraryPreviewJointPositions();
            Assert(start.Length == 3 && quarter.Length == 3, $"The preview skeleton has {start.Length} joints (3 expected).");
            Assert(Vector3.Distance(start[2], quarter[2]) > .3f && Vector3.Distance(start[1], quarter[1]) < 1e-4f,
                $"Scrubbing did not move the hand ({start[2]} to {quarter[2]}) or moved the shoulder.");
            editor.PlayLibraryClip(); Assert(editor.IsPlaying, "Play did not start the clip.");
            editor.AdvancePreview(.1f);
            Assert(Math.Abs(editor.AnimationTime - .35f) < 1e-3f, $"Playback did not advance the clip ({editor.AnimationTime}).");
            editor.PauseLibraryClip(); Assert(!editor.IsPlaying, "Pause did not stop the clip.");
            Assert(editor.SelectLibraryClip("Nod") && editor.LibrarySelectedListClip == "Nod" && editor.AnimationFrameCount == 16,
                "Selecting another clip did not reach the list and timeline.");
            editor.SetLibraryPreviewTime(0); Vector3 rootStart = editor.LibraryPreviewJointPositions()[0];
            editor.SetLibraryPreviewTime(7 / 30f); Vector3 rootMid = editor.LibraryPreviewJointPositions()[0];
            Assert(rootMid.Y - rootStart.Y > .2f, $"The Nod clip did not lift the root ({rootStart} to {rootMid}).");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.ClipsOnly.CaptureShowsClipListAndBones", () =>
        {
            using var editor = new ModelEditorControl(library, project.RootPath);
            using var host = Host(editor);
            editor.SelectLibraryClip("Wave"); editor.SetLibraryPreviewTime(.25f); editor.FrameModelForTest();
            Editor3DInspectionSuite.Pump();
            using (Bitmap? frame = editor.Viewport.CaptureFrame(6))
            {
                Assert(frame is not null, "The library preview did not render.");
                (int bone, int joint) = CountBonePixels(frame!);
                if (bone <= 30 || joint <= 6) frame!.Save(Path.Combine(ctx.Captures, "model-clips-only-bones-diagnostic.png"));
                Assert(bone > 30 && joint > 6, $"The bone lines were not drawn (bone pixels {bone}, joint pixels {joint}).");
            }
            Editor3DInspectionSuite.Capture(ctx, host, "model-clips-only-library");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.ClipsOnly.PreviewsOnAPickedBody", () =>
        {
            using var editor = new ModelEditorControl(library, project.RootPath);
            using var host = Host(editor);
            editor.SelectLibraryClip("Wave");
            editor.SetLibraryPreviewBody("Robot body");
            Assert(editor.LibraryPreviewBody == "Robot body", "The body Model was not picked by name.");
            Assert(editor.LibraryPreviewBodyClips.SequenceEqual(["Wave", "Nod"]), "The body did not receive the library's clips: "
                + string.Join(", ", editor.LibraryPreviewBodyClips));
            editor.SetLibraryPreviewTime(0); Vector3[] start = editor.LibraryPreviewJointPositions();
            editor.SetLibraryPreviewTime(.25f); Vector3[] quarter = editor.LibraryPreviewJointPositions();
            // The body's arm is longer (1.6) than the library's (1.2): it keeps its own proportions.
            Assert(Math.Abs(Vector3.Distance(start[1], start[2]) - 1.6f) < 1e-3f, "The body did not keep its own bone lengths.");
            Assert(Vector3.Distance(start[2], quarter[2]) > .3f, "The library clip did not move the body's hand.");
            using (Bitmap? frame = editor.Viewport.CaptureFrame(6))
            {
                Assert(frame is not null, "The body preview did not render.");
                int body = CountBodyPixels(frame!);
                if (body <= 200) frame!.Save(Path.Combine(ctx.Captures, "model-clips-only-body-diagnostic.png"));
                Assert(body > 200, $"The body was not drawn ({body} body pixels).");
            }
            Editor3DInspectionSuite.Capture(ctx, host, "model-clips-only-body");
            Reject(() => editor.SetLibraryPreviewBody(library));
            editor.SetLibraryPreviewBody(null);
            Assert(editor.LibraryPreviewBody.Length == 0 && Math.Abs(Vector3.Distance(editor.LibraryPreviewJointPositions()[1], editor.LibraryPreviewJointPositions()[2]) - 1.2f) < 1e-3f,
                "Skeleton only did not return to the library's own skeleton.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.ClipsOnly.SaveAndReopenKeepTheClips", () =>
        {
            string canonical = StudioModelResourceLoader.CanonicalPath(library);
            byte[] descriptorBefore = File.ReadAllBytes(library), canonicalBefore = File.ReadAllBytes(canonical);
            using (var editor = new ModelEditorControl(library, project.RootPath))
            {
                editor.SelectLibraryClip("Nod"); editor.SetLibraryPreviewBody("Robot body"); editor.SetLibraryPreviewTime(.2f);
                editor.Save();
                Assert(!editor.IsDirty, "Save left the library dirty.");
            }
            using (var viewer = new ModelViewerControl(library, project.RootPath)) viewer.Save();
            Assert(File.ReadAllBytes(library).SequenceEqual(descriptorBefore) && File.ReadAllBytes(canonical).SequenceEqual(canonicalBefore),
                "Saving rewrote the clips-only Model.");
            using var reopened = new ModelEditorControl(library, project.RootPath);
            GModelAsset expected = Library();
            Assert(reopened.IsAnimationLibrary && !reopened.PreviewAsset.HasRenderableMeshes, "Reopening lost the animation library view or gained a mesh.");
            Assert(reopened.LibraryClipNames.SequenceEqual(["Wave", "Nod"]), "Reopening lost clips.");
            foreach (GModelAnimationClip clip in expected.Animations)
            {
                GModelAnimationClip saved = reopened.PreviewAsset.Animations.Single(c => c.Name == clip.Name);
                Assert(saved.Frames.Count == clip.Frames.Count && saved.Fps == clip.Fps
                    && saved.Frames.Zip(clip.Frames).All(pair => pair.First.LocalBoneTransforms.SequenceEqual(pair.Second.LocalBoneTransforms)),
                    $"Clip '{clip.Name}' changed through save and reopen.");
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.ClipsOnly.OpensAnImportedClipsOnlyGltf", () =>
        {
            string folder = Path.Combine(ctx.Workspace, "Clips only import");
            Directory.CreateDirectory(folder);
            string gltf = AnimationOnly(AnimatedGlbFixture.Write(folder), folder);
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Imported moves");
            GModelAsset imported = ExternalModelImporter.Import(gltf, project.RootPath, path);
            Assert(imported.Meshes.Count == 0 && imported.Metadata.ContainsKey("source.clipsOnly"), "The fixture did not import as clips only.");
            StudioModelResourceLoader.SaveCanonical(path, imported);
            using var editor = new ModelEditorControl(path, project.RootPath);
            using var host = Host(editor);
            Assert(editor.IsAnimationLibrary && editor.LibraryClipNames.SequenceEqual(imported.Animations.Select(c => c.Name)),
                "An imported clips-only Model did not open as an animation library.");
            Assert(editor.LibraryPreviewJointPositions().Length == imported.Nodes.Count, "The imported library's skeleton was not previewed.");
        });
    }

    /// <summary>Shaped like ExternalModelImporter's clips-only Model: one bone per node, one transform per node per frame.</summary>
    internal static GModelAsset Library()
    {
        Matrix4x4[] bind = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, 1.5f, 0), Matrix4x4.CreateTranslation(1.2f, 0, 0)];
        var asset = new GModelAsset { Schema = GModelAsset.ImportSchema, Name = "Robot moves" };
        asset.Metadata["source.clipsOnly"] = "true";
        asset.Materials.Add(new GModelMaterial());
        for (int i = 0; i < BoneNames.Length; i++)
        {
            asset.Nodes.Add(new GModelNode { Name = BoneNames[i], ParentIndex = i - 1, LocalTransform = bind[i] });
            asset.Rig.Bones.Add(new GModelBone { Name = BoneNames[i], ParentIndex = i - 1, BindLocal = bind[i] });
        }
        asset.Rig.InverseBindMatrices = Enumerable.Repeat(Matrix4x4.Identity, BoneNames.Length).ToArray();
        asset.Animations.Add(Clip("Wave", 31, bind, (locals, t) => locals[1] = Matrix4x4.CreateRotationZ(MathF.Sin(t * MathF.Tau) * .9f) * bind[1]));
        asset.Animations.Add(Clip("Nod", 16, bind, (locals, t) =>
        {
            locals[0] = Matrix4x4.CreateTranslation(0, MathF.Sin(t * MathF.PI) * .4f, 0);
            locals[2] = Matrix4x4.CreateRotationY(t * 1.2f) * bind[2];
        }));
        asset.RecalculateBounds();
        return asset;
    }

    private static GModelAnimationClip Clip(string name, int frames, Matrix4x4[] bind, Action<Matrix4x4[], float> pose)
    {
        var clip = new GModelAnimationClip { Name = name, Fps = 30, Loop = true };
        for (int frame = 0; frame < frames; frame++)
        {
            Matrix4x4[] locals = (Matrix4x4[])bind.Clone();
            pose(locals, frame / (float)(frames - 1));
            clip.Frames.Add(new GModelAnimationFrame { LocalBoneTransforms = locals });
        }
        return clip;
    }

    private static Form Host(ModelViewerControl viewer)
    {
        var form = UnattendedWindowing.NewHost(1440, 920); form.Controls.Add(viewer); ThemeService.Apply(form);
        UnattendedWindowing.ShowWithoutFocus(form); Editor3DInspectionSuite.Pump(); return form;
    }

    /// <summary>Gold bone lines and cyan joints, as the library overlay draws them.</summary>
    private static (int Bone, int Joint) CountBonePixels(Bitmap frame)
    {
        int bone = 0, joint = 0;
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                Color pixel = frame.GetPixel(x, y);
                if (pixel.R > 200 && pixel.G is > 150 and < 230 && pixel.B < 110) bone++;
                if (pixel.R < 140 && pixel.G > 190 && pixel.B > 210) joint++;
            }
        return (bone, joint);
    }

    /// <summary>The robot's lit blue body and gold limbs, distinct from the grey grid and dark canvas.</summary>
    private static int CountBodyPixels(Bitmap frame)
    {
        int body = 0;
        for (int y = 0; y < frame.Height; y += 2)
            for (int x = 0; x < frame.Width; x += 2)
            {
                Color pixel = frame.GetPixel(x, y);
                bool blue = pixel.B > 80 && pixel.B > pixel.R * 1.8f && pixel.G > pixel.R;
                bool warm = pixel.R > 60 && pixel.R > pixel.G * 1.025f && pixel.G > pixel.B * 1.2f;
                if (blue || warm) body++;
            }
        return body;
    }

    private static string AnimationOnly(string glb, string folder)
    {
        byte[] bytes = File.ReadAllBytes(glb); int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        var root = JsonNode.Parse(bytes.AsSpan(20, length))!.AsObject();
        root.Remove("meshes"); root.Remove("skins"); root.Remove("images"); root.Remove("textures"); root.Remove("materials");
        foreach (var node in root["nodes"]!.AsArray()) { node!.AsObject().Remove("mesh"); node.AsObject().Remove("skin"); }
        root["buffers"]![0]!["uri"] = "Clips-only.bin";
        File.WriteAllBytes(Path.Combine(folder, "Clips-only.bin"), bytes.AsSpan(28 + length, root["buffers"]![0]!["byteLength"]!.GetValue<int>()).ToArray());
        string path = Path.Combine(folder, "Clips-only.gltf"); File.WriteAllText(path, root.ToJsonString()); return path;
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        yield return control;
        foreach (Control child in control.Controls) foreach (Control nested in Descendants(child)) yield return nested;
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected the library itself to be refused as a preview body.");
    }

    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}
