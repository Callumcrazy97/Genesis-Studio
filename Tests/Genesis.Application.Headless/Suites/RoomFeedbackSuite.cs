using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scene;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static class RoomFeedbackSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Feedback.NavigationCameraSettingsAndFog", () => Workspace(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Terrain.Entities.OwnedPartsAndLegacyCompatibility", () => TerrainParts(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Model.FrameCacheRefreshesChangedAssets", () => FrameCache(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Feedback.SoundscapeAudioPicker", () => Soundscape(ctx));
    }

    private static void Workspace(HeadlessContext ctx)
    {
        ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "RoomFeedback"), "Room feedback");
        var resources = new ResourceService(project);
        string roomPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Staging");
        string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Marker");
        var baked = ModelPartBuilder.Bake([new ModelPart { Name = "Marker", Position = [0, 1, 0], Scale = [1, 2, 1], Color = [.9f, .2f, .1f] }]);
        StudioModelResourceLoader.SaveCanonical(model, ModelRigBridge.BuildAsset("Marker", baked.Vertices, baked.Indices));
        string prefab = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Marker");
        File.WriteAllText(prefab, new JObject { ["name"] = "Marker", ["dimension"] = "ThreeD",
            ["model"] = Path.GetRelativePath(project.RootPath, model).Replace('\\', '/') }.ToString());
        RoomAsset room = RoomAsset.Create("Staging", RoomDimension.ThreeD);
        room.Nodes.Add(new RoomNode { Name = "Marker instance", Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
            Transform = new RoomTransform { X = -3 }, GameObject = new RoomGameObjectData { Prefab = Path.GetRelativePath(project.RootPath, prefab).Replace('\\', '/') } });
        RoomAssetLoader.Save(room, roomPath);
        using var editor = new RoomEditorControl(roomPath, project.RootPath);
        using var host = UnattendedWindowing.NewHost(1440, 900);
        editor.Dock = DockStyle.Fill; host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
        Warm(editor);
        editor.Viewport.Camera.Target = new Vector3(1, 2, 3); editor.Viewport.Camera.Yaw = .8f;
        editor.Viewport.Camera.Pitch = -.25f; editor.Viewport.Camera.Distance = 18;
        foreach (RoomNavSection section in new[] { RoomNavSection.Instances, RoomNavSection.Settings, RoomNavSection.Views, RoomNavSection.Objects })
        {
            editor.Navigation.SetSection(section); editor.Select(editor.Room.Nodes[0]); editor.RefreshSceneViews(); Warm(editor);
            Assert(editor.Viewport.Camera.Target == new Vector3(1, 2, 3) && editor.Viewport.Camera.Yaw == .8f
                && editor.Viewport.Camera.Pitch == -.25f && editor.Viewport.Camera.Distance == 18,
                "Selecting an instance or switching " + section + " reset the editor camera.");
        }
        editor.Navigation.SetSection(RoomNavSection.Instances); Warm(editor);
        Assert(editor.Controls.Find("RoomInstanceHierarchy", true).Single().Visible
            && !editor.Controls.Find("RoomObjectAssetShelf", true).Single().Visible, "Instances is not a dedicated navigation section.");
        Capture(ctx, host, "room-feedback-instances");
        editor.Navigation.SetSection(RoomNavSection.Settings); Warm(editor);
        Control settings = editor.Controls.Find("RoomSettingsFields", true).Single();
        NumericUpDown width = settings.Controls.Find("Context.Room.Width", true).OfType<NumericUpDown>().SingleOrDefault()
            ?? Descendants(settings).OfType<NumericUpDown>().First(number => number.Value == editor.Room.Settings.Width);
        int previousWidth = editor.Room.Settings.Width; width.Value = previousWidth + 20;
        Assert(editor.Room.Settings.Width == previousWidth + 20 && editor.SelectedNode == editor.Room.Nodes[0],
            "Dedicated room settings did not edit the room independently of selection.");
        editor.Undo(); Assert(editor.Room.Settings.Width == previousWidth, "Room-settings edit was not undoable.");
        Capture(ctx, host, "room-feedback-settings");
        Assert(!editor.Viewport.InspectionState.FogEnabled && !editor.Viewport.InspectionState.FogScreenSpace, "Room editor fog should start disabled.");
        editor.SetFogPreview(true, 3, 24, .05f);
        Assert(editor.Viewport.InspectionState.FogEnabled && editor.Viewport.InspectionState.FogEnd == 24, "Fog controls did not reach the renderer.");
        editor.SetFogPreview(false);
        ToolStripDropDownButton viewMenu = editor.EditorToolbar.Strip.Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "View");
        var fogToggle = viewMenu.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Name == "RoomViewFog");
        fogToggle.PerformClick(); Assert(editor.FogPreviewEnabled, "View/Fog did not enable fog.");
        fogToggle.PerformClick(); Assert(!editor.FogPreviewEnabled, "View/Fog did not disable fog.");
        Exception? fogFailure = null;
        using (var fogTimer = new System.Windows.Forms.Timer { Interval = 100 })
        {
            fogTimer.Tick += (_, _) =>
            {
                Form? dialog = System.Windows.Forms.Application.OpenForms.Cast<Form>().FirstOrDefault(form => form.Text == "Room fog preview");
                if (dialog is null) return;
                fogTimer.Stop();
                try
                {
                    NumericUpDown[] numbers = Descendants(dialog).OfType<NumericUpDown>().ToArray();
                    numbers.Single(number => number.Value == 20).Value = 3;
                    numbers.Single(number => number.Value == 200).Value = 24;
                    numbers.Single(number => number.DecimalPlaces == 3).Value = .05m;
                    Descendants(dialog).OfType<CheckBox>().Single().Checked = true;
                    Capture(ctx, dialog, "room-feedback-fog-settings");
                    ((Button)dialog.AcceptButton!).PerformClick();
                }
                catch (Exception ex) { fogFailure = ex; dialog.Close(); }
            };
            fogTimer.Start();
            viewMenu.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Fog settings…").PerformClick();
            fogTimer.Stop();
        }
        if (fogFailure is not null) throw fogFailure;
        Assert(editor.FogPreviewEnabled && editor.Viewport.InspectionState.FogEnd == 24, "Fog dialog did not apply its settings.");
        editor.SetFogPreview(false);
        editor.Navigation.SetSection(RoomNavSection.Objects); editor.SetSnapToTerrain(false);
        editor.Viewport.Camera.Target = new Vector3(0, 1, 0); editor.Viewport.Camera.Distance = 10;
        Warm(editor);
        using Bitmap before = editor.Viewport.CaptureFrame(2) ?? throw new InvalidOperationException("No viewport capture.");
        editor.BeginPlacement(prefab);
        editor.EditorPointerMove(editor.ClientFromWorld3D(Vector3.Zero), MouseButtons.None, Keys.None);
        using Bitmap ghost = editor.Viewport.CaptureFrame(2) ?? throw new InvalidOperationException("No ghost capture.");
        Assert(editor.PlacementGhostTransform3D is not null && editor.Room.Nodes.Count == 1, "Placement preview mutated the room.");
        int changed = 0;
        for (int y = ghost.Height / 4; y < ghost.Height * 3 / 4; y++)
            for (int x = ghost.Width / 3; x < ghost.Width * 2 / 3; x++)
            {
                Color a = before.GetPixel(x, y), b = ghost.GetPixel(x, y);
                if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 35 && b.R > b.G * 1.15) changed++;
            }
        Assert(changed > 250, $"The placement ghost has no visible model surface ({changed} changed red pixels).");
        Capture(ctx, host, "room-feedback-model-ghost");
        editor.Save();
        Assert(RoomAssetLoader.Parse(roomPath).Nodes.Count == 1, "A ghost was saved as an instance.");

        string cameraPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Camera");
        JObject cameraObject = JObject.Parse(File.ReadAllText(cameraPath));
        ((JArray)cameraObject["components"]!).Add(new JObject { ["id"] = "Camera3D", ["type"] = "Camera3DComponent",
            ["enabled"] = true, ["props"] = new JObject { ["FOV"] = "72", ["Near"] = "0.25", ["Far"] = "900" } });
        File.WriteAllText(cameraPath, cameraObject.ToString());
        var camera = new RoomNode { Kind = RoomNodeKind.GameObject, Name = "Camera",
            GameObject = new RoomGameObjectData { Prefab = Path.GetRelativePath(project.RootPath, cameraPath).Replace('\\', '/') },
            Transform = new RoomTransform { X = 5, Y = 3, Z = 12 } };
        editor.Room.Nodes.Add(camera); editor.SetActiveGameCamera(camera);
        Vector3 freeTarget = editor.Viewport.Camera.Target;
        float freeDistance = editor.Viewport.Camera.Distance;
        Assert(editor.PreviewGameCamera(), "Game-camera preview did not activate.");
        var preview = editor.Viewport.CameraOverrideFactory!();
        foreach (RoomNavSection section in new[] { RoomNavSection.Instances, RoomNavSection.Settings, RoomNavSection.Views })
        {
            editor.Navigation.SetSection(section); editor.Select(editor.Room.Nodes[0]); editor.RefreshSceneViews(); Warm(editor);
            Assert(editor.IsGameCameraPreview && editor.SceneViewNode == camera && editor.Viewport.CameraOverrideFactory!().Equals(preview),
                "Refreshing controls changed the active game-camera preview.");
        }
        editor.ExitGameCameraPreview();
        Assert(editor.Viewport.Camera.Target == freeTarget && editor.Viewport.Camera.Distance == freeDistance,
            "Refreshing camera choices overwrote the saved free-camera view.");
        editor.Room.Nodes[0].Transform.X = 6000;
        editor.FrameContentForTest();
        Assert(editor.Viewport.FarPlane > editor.Viewport.Camera.Distance,
            "Frame content moved the large room beyond the editor camera's far plane.");
    }

    private static void TerrainParts(HeadlessContext ctx)
    {
        ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "TerrainParts"), "Terrain parts");
        var resources = new ResourceService(project);
        string terrainPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Terrain, "Landscape");
        Assert(ResourceDefinitions.All.All(definition => definition.Kind != ResourceKind.TerrainEntity)
            && ResourceDefinitions.Creatable.All(definition => definition.Kind != ResourceKind.TerrainEntity),
            "Terrain Entity is still advertised as a standalone resource.");
        string part;
        using (var editor = new TerrainEditorControl(terrainPath, project.RootPath))
        {
            part = editor.CreateTerrainEntity(TerrainEntityType.Object);
            Assert(part.StartsWith(terrainPath + ".parts" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "New entity was not stored inside its owning terrain.");
            editor.ActiveEntityWizard!.SaveAndCloseForTest(); editor.Save();
            Assert(File.Exists(part) && ResourceAssociates.IsHiddenImplementationFile(part)
                && ResourceAssociates.Find(terrainPath).Contains(terrainPath + ".parts"), "Terrain parts are not preserved as hidden resource associates.");
        }
        using var reopened = new TerrainEditorControl(terrainPath, project.RootPath);
        Assert(File.ReadAllText(terrainPath).Contains(Path.GetFileName(part)), "Terrain did not retain its part reference after saving.");
        string duplicate = resources.Duplicate(terrainPath);
        Assert(Directory.Exists(duplicate + ".parts") && File.ReadAllText(duplicate).Contains(Path.GetFileName(duplicate) + ".parts"),
            "Duplicating terrain did not copy and rebind its owned parts.");
        string renamed = resources.Rename(duplicate, "Renamed landscape");
        string folder = resources.CreateFolder(Path.Combine(resources.AssetsRoot, "Terrain"), "Moved terrain");
        string moved = resources.Move(renamed, folder);
        string movedPart = Path.Combine(moved + ".parts", Path.GetFileName(part));
        Assert(File.Exists(movedPart) && File.ReadAllText(moved).Contains(Path.GetRelativePath(project.RootPath, movedPart).Replace('\\', '/')),
            "Moving or renaming terrain left references pointing to its previous parts directory.");
        // Legacy fixtures are loaded, not newly authored standalone resources.
        string legacy = Path.Combine(resources.AssetsRoot, "Legacy part" + ResourceDefinitions.Get(ResourceKind.TerrainEntity).Extension);
        File.WriteAllText(legacy, ResourceDefinitions.Get(ResourceKind.TerrainEntity).DefaultContent);
        Assert(ResourceDefinitions.FromPath(legacy)?.Kind == ResourceKind.TerrainEntity
            && ProjectAssetIndex.Enumerate(project.RootPath, ResourceKind.TerrainEntity).Any(entry => entry.FullPath == legacy),
            "Existing entity files are no longer readable for compatibility.");
    }

    private static void Soundscape(HeadlessContext ctx)
    {
        ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "SoundscapePicker"), "Soundscape picker");
        var resources = new ResourceService(project);
        string audio = resources.CreateResource(resources.AssetsRoot, ResourceKind.Audio, "Forest ambience");
        resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Not audio");
        var environment = new RoomEnvironment();
        using var dialog = new RoomSoundscapeDialog(environment, project.RootPath);
        UnattendedWindowing.ShowWithoutFocus(dialog);
        Button browse = (Button)dialog.Controls.Find("SoundscapeWindPicker", true).Single();
        Exception? failure = null;
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        bool observed = false;
        timer.Tick += (_, _) =>
        {
            var picker = System.Windows.Forms.Application.OpenForms.OfType<AssetPickerModal>().FirstOrDefault();
            if (picker is null) return;
            timer.Stop(); observed = true;
            try
            {
                Assert(picker.FilteredAssets.Count > 0 && picker.FilteredAssets.All(entry => entry.Kind == ResourceKind.Audio),
                    "Soundscape picker includes non-audio resources.");
                picker.SetFilter("Forest ambience");
                Capture(ctx, picker, "room-feedback-audio-picker");
                ((Button)picker.AcceptButton!).PerformClick();
            }
            catch (Exception ex) { failure = ex; picker.Close(); }
        };
        timer.Start(); browse.PerformClick(); timer.Stop();
        if (failure is not null) throw failure;
        Assert(observed, "Soundscape did not open the project resource picker.");
        dialog.ApplyTo(environment);
        Assert(environment.WindAudio == Path.GetRelativePath(project.RootPath, audio).Replace('\\', '/'),
            "Soundscape did not retain the selected project audio reference.");
        Capture(ctx, dialog, "room-feedback-soundscape");
        dialog.Close();
    }

    private static void FrameCache(HeadlessContext ctx)
    {
        string directory = Path.Combine(ctx.Workspace, "FrameCache"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Cached.model.json"); File.WriteAllText(path, "{}");
        var small = ModelPartBuilder.Bake([new ModelPart { Scale = [1, 1, 1] }]);
        var large = ModelPartBuilder.Bake([new ModelPart { Scale = [4, 4, 4] }]);
        StudioModelResourceLoader.SaveCanonical(path, ModelRigBridge.BuildAsset("Small", small.Vertices, small.Indices));
        var renderer = new RuntimeModelRenderSystem(); renderer.BeginFrame();
        renderer.TryGetBounds(directory, path, out Vector3 min, out Vector3 max);
        StudioModelResourceLoader.SaveCanonical(path, ModelRigBridge.BuildAsset("Large", large.Vertices, large.Indices));
        File.SetLastWriteTimeUtc(StudioModelResourceLoader.CanonicalPath(path), DateTime.UtcNow.AddSeconds(2));
        renderer.TryGetBounds(directory, path, out Vector3 sameMin, out Vector3 sameMax);
        Assert(sameMin == min && sameMax == max, "Instances within one frame used different asset versions.");
        renderer.EndFrame(); renderer.BeginFrame();
        renderer.TryGetBounds(directory, path, out Vector3 changedMin, out Vector3 changedMax); renderer.EndFrame();
        Assert((changedMax - changedMin).Length() > (max - min).Length() * 3,
            "Frame-scoped caching hid an asset edit from subsequent frames.");
    }

    public static void Profile(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Profile.NatureWalk", () =>
        {
            string project = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Genesis Projects", "My 3D Nature Walk");
            string source = Path.Combine(project, "Assets", "Rooms", "VerdantHollow.room.json");
            string copy = Path.Combine(ctx.Workspace, "NatureWalk.room.json");
            File.Copy(source, copy, true);
            using var editor = new RoomEditorControl(copy, project);
            using var host = UnattendedWindowing.NewHost(1440, 900);
            editor.Dock = DockStyle.Fill; host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
            editor.Viewport.Host.TimerEnabled = false; editor.Viewport.Host.VSync = false;
            editor.FrameContentForTest(); Warm(editor);
            editor.Viewport.Camera.Target = new Vector3(0, 0, -70);
            editor.Viewport.Camera.Distance = 650;
            editor.Viewport.Camera.Yaw = .25f;
            editor.Viewport.Camera.Pitch = -.6f;
            editor.Viewport.FarPlane = 4000;
            Warm(editor);
            List<string> results = [];
            double submissionMs = 0;
            var drawField = typeof(EditorViewport3D).GetField("DrawScene", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var drawScene = (Action<Genesis.Shared.Interfaces.IRenderController>?)drawField.GetValue(editor.Viewport);
            Action<Genesis.Shared.Interfaces.IRenderController> timedDraw = renderer =>
            {
                var watch = Stopwatch.StartNew(); drawScene?.Invoke(renderer); submissionMs = watch.Elapsed.TotalMilliseconds;
            };
            drawField.SetValue(editor.Viewport, timedDraw);
            void Measure(string label)
            {
                Warm(editor); List<double> ms = [], submission = [];
                for (int i = 0; i < 40; i++)
                {
                    var clock = Stopwatch.StartNew(); editor.Viewport.Host.RenderFrame(); clock.Stop();
                    ms.Add(clock.Elapsed.TotalMilliseconds); submission.Add(submissionMs);
                }
                ms.Sort(); submission.Sort();
                results.Add($"{label}: nodes={editor.Room.Nodes.Count}; viewport={editor.Viewport.Host.ClientSize}; median={ms[20]:0.00} ms; p95={ms[38]:0.00} ms; scene submission={submission[20]:0.00} ms; terrain draws={editor.AuthoredTerrainDraws.Count}; backend={editor.Viewport.Host.Renderer.BackendName}");
            }
            Measure("Default");
            using (Bitmap visible = editor.Viewport.CaptureFrame(2)!)
            {
                int terrainPixels = 0;
                for (int y = 0; y < visible.Height; y += 4)
                    for (int x = 0; x < visible.Width; x += 4)
                    {
                        Color pixel = visible.GetPixel(x, y);
                        if (pixel.G > pixel.R * 1.25 && pixel.G > pixel.B * 1.25) terrainPixels++;
                    }
                Assert(terrainPixels > 1000, "Performance camera does not show enough of the actual terrain for a useful comparison.");
            }
            Capture(ctx, host, "room-nature-walk-profile");
            editor.Viewport.ViewSettings.Shadows = false; Measure("Shadows off");
            editor.Room.Environment.VolumetricClouds = false; Measure("Shadows and clouds off");
            string result = string.Join(Environment.NewLine, results);
            Console.WriteLine(result); File.WriteAllText(Path.Combine(ctx.OutputRoot, "room-render-profile.txt"), result);
        });
    }

    private static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static void Warm(RoomEditorControl editor) { GateSuite.Pump(2, 20); using var frame = editor.Viewport.CaptureFrame(2); }
    private static void Capture(HeadlessContext ctx, Form host, string name) => Editor3DInspectionSuite.Capture(ctx, host, name);
    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
