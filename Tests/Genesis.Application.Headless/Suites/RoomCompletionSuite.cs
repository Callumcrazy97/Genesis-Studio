using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Core;
using Genesis.Rendering.Meshes;
using Genesis.Runtime;
using Genesis.Runtime.Core;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Shared.Audio;
using Genesis.Shared.Interfaces;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Commands;

namespace Genesis.Application.Headless.Suites;

internal static class RoomCompletionSuite
{
    public static void Run(HeadlessContext ctx)
    {
        using var cameras = new CameraRegistryScope();
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Soundscape.LevelsDialogRuntimeWithoutSkyAndPersistence", () => Soundscape(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Room.Camera.LiveThreeDInstanceFollowAndDeadZone", Follow);
        foreach (RenderBackendOption backend in new[] { RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
                     RenderBackendOption.Vulkan, RenderBackendOption.OpenGL, RenderBackendOption.Software })
            HeadlessHarness.RunCase(ctx.Report, "Runtime.Room.Camera.SplitOutputPixelsAndResize." + backend, () => SplitViews(ctx, backend));
    }

    private static void Soundscape(HeadlessContext ctx)
    {
        string directory = Path.Combine(ctx.Workspace, "RoomSoundscape"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Soundscape.room.json");
        RoomAsset room = RoomAsset.Create("Soundscape", RoomDimension.ThreeD);
        room.Environment.DynamicSky = false; room.Environment.AutomaticWeather = false;
        room.Environment.Weather = "Rain"; room.Environment.TimeOfDayHours = 0; room.Environment.TimeScale = 0;
        room.Environment.RainAudio = "rain.wav"; room.Environment.NightAudio = "night.wav";
        using (var dialog = new RoomSoundscapeDialog(room.Environment, directory))
        {
            ThemeService.Apply(dialog); UnattendedWindowing.ShowWithoutFocus(dialog);
            ((NumericUpDown)dialog.Controls.Find("SoundscapeMasterLevel", true).Single()).Value = 50;
            ((NumericUpDown)dialog.Controls.Find("SoundscapeRainLevel", true).Single()).Value = 25;
            dialog.ApplyTo(room.Environment);
            Editor3DInspectionSuite.Capture(ctx, dialog, "room-soundscape-levels");
        }
        RoomAssetLoader.Save(room, path);
        using (var editor = new RoomEditorControl(path, directory))
        {
            var levels = editor.Room.Environment.SoundscapeLevels.Clone(); levels.Master = .8f;
            editor.SetEnvironmentSoundscape(rain: "rain.wav", night: "night.wav", levels: levels);
            editor.Undo(); Assert(editor.Room.Environment.SoundscapeLevels.Master == .5f, "Soundscape undo lost prior master gain.");
            editor.Redo(); editor.Save();
            using var host = UnattendedWindowing.NewHost(1380, 900);
            editor.Dock = DockStyle.Fill; host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
            editor.Navigation.SetSection(RoomNavSection.Views); GateSuite.Pump(3, 20);
            ComboBox sceneCamera = (ComboBox)editor.Controls.Find("RoomSceneCamera", true).Single();
            Assert(sceneCamera.Visible && sceneCamera.Text == "Scene" && sceneCamera.Height >= 20
                && sceneCamera.Parent!.ClientRectangle.Contains(sceneCamera.Bounds),
                "Scene camera control was hidden, clipped or detached from the camera section.");
            ((Button)editor.Controls.Find("RoomCameraLayout4", true).Single()).PerformClick();
            Assert(editor.Room.Viewports.Count(port => port.Enabled) == 4
                && editor.Room.Viewports[3].PortX == 640 && editor.Room.Viewports[3].PortY == 360,
                "Quad layout button did not configure the actual camera output ports.");
            editor.Undo(); Assert(!editor.Room.UsesViewports, "Camera layout was not one undoable edit.");
            editor.Redo(); editor.Save();
            Editor3DInspectionSuite.Capture(ctx, host, "room-camera-layout-controls");
        }
        room = RoomAssetLoader.Parse(path);
        Assert(room.Environment.SoundscapeLevels.Master == .8f && room.Environment.SoundscapeLevels.Rain == .25f,
            "Soundscape gains did not survive clone/undo/save/reopen.");
        using var scene = new RuntimeScene(); RoomSceneBuilder.ApplySceneSettings(scene, room);
        Vector4 background = scene.Environment.BackgroundColor;
        var fullAudio = new AudioRecorder(); var quietAudio = new AudioRecorder();
        RoomEnvironment full = room.DeepClone().Environment; full.SoundscapeLevels = new();
        using var loud = new RoomEnvironmentAudioSubsystem(full, fullAudio);
        using (var quiet = new RoomEnvironmentAudioSubsystem(room.Environment, quietAudio))
        {
            Assert(quietAudio.Plays == 0, "Loading a soundscape started playback before Update.");
            GameTime time = new();
            for (int index = 0; index < 100; index++) { time.Advance(.05f); loud.Update(scene, time); quiet.Update(scene, time); }
            Assert(scene.Climate is null && scene.Environment.BackgroundColor == background, "Audio enabled visual sky or altered the environment.");
            Assert(fullAudio.Volumes.GetValueOrDefault("rain.wav") > .1f && quietAudio.LoopsOnly,
                "Rain ambience failed to play without Dynamic Sky or failed to loop.");
            Assert(Math.Abs(quietAudio.Volumes["rain.wav"] / fullAudio.Volumes["rain.wav"] - .2f) < .001f,
                "Runtime did not multiply master and per-role gains.");
            Assert(quietAudio.Volumes.GetValueOrDefault("night.wav") > 0, "Night ambience ignored the authored clock.");
        }
        Assert(quietAudio.Stops == quietAudio.Plays, "Disposing the room left ambience channels playing.");
    }

    private static void Follow()
    {
        RoomAsset room = RoomAsset.Create("Follow", RoomDimension.ThreeD);
        RoomViewport port = room.Viewports[0]; port.Enabled = true;
        port.SourceX = -10; port.SourceY = -10; port.SourceZ = -10;
        port.SourceWidth = port.SourceHeight = port.SourceDepth = 10;
        port.FollowMarginX = port.FollowMarginY = port.FollowMarginZ = 2;
        port.FollowSpeedX = port.FollowSpeedY = port.FollowSpeedZ = .5f;
        port.FollowTarget = "Named instance";
        using var scene = new RuntimeScene(); using var presentation = new RoomRenderSubsystem("", room);
        var entity = scene.World.CreateEntity();
        scene.World.Set(entity, new TransformComponent { X = 500 });
        scene.World.Set(entity, new Transform3DComponent { Position = new(-5, -5, -5), Scale = Vector3.One });
        ObjectDrawAssetRegistry.Set(entity, new ObjectDrawAssetEntry { InstanceName = "Named instance", Prefab = "different.object.json" });
        try
        {
            GameTime time = new(); time.Advance(.016f); presentation.Update(scene, time);
            Assert(presentation.GetViewportPosition(0) == new Vector3(-10), "Camera moved inside the dead zone or clamped a negative 3D origin.");
            scene.World.GetRef<Transform3DComponent>(entity).Position = Vector3.One;
            presentation.Update(scene, time);
            Assert(presentation.GetViewportPosition(0) == new Vector3(-9.5f), "Live 3D follow ignored instance identity, 3D transform or speed.");
            presentation.ConfigureViewportCamera(scene, 0);
            Assert(scene.Camera3D.Position == new Vector3(-9.5f) && Vector3.Dot(scene.Camera3D.Forward, Vector3.Normalize(new Vector3(10.5f))) > .999f,
                "The rendered camera ignored the tracked origin or follow orientation.");
            Assert(port.SourceX == -10 && port.SourceY == -10 && port.SourceZ == -10, "Playback mutated the authored camera.");
        }
        finally { ObjectDrawAssetRegistry.Remove(entity); }
    }

    private static void SplitViews(HeadlessContext ctx, RenderBackendOption backend)
    {
        using var host = UnattendedWindowing.NewHost(640, 480);
        UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(backend);
        renderer.Initialize(host.Handle, 640, 480);
        using var scene = new RuntimeScene();
        RoomAsset room = RoomAsset.Create("Split", RoomDimension.ThreeD);
        room.Environment.DynamicSky = false; RoomSceneBuilder.ApplySceneSettings(scene, room);
        scene.Environment.FogEnabled = false;
        using var presentation = new RoomRenderSubsystem("", room);
        var (vertices, indices) = MeshGeometry.BuildCube(RenderColor.White, 1f);
        MeshHandle cube = renderer.RegisterMesh(vertices, indices);
        Vector3 savedCamera = scene.Camera3D.Position;
        for (int views = 1; views <= 4; views *= 2)
        {
            for (int index = 0; index < room.Viewports.Count; index++) room.Viewports[index].Enabled = index < views;
            int width = views == 1 ? 640 : 320, height = views == 4 ? 240 : 480;
            for (int index = 0; index < views; index++)
            {
                RoomViewport port = room.Viewports[index];
                port.PortX = index % 2 * width; port.PortY = index / 2 * height;
                port.PortWidth = width; port.PortHeight = height;
                port.SourceX = index % 2 == 0 ? -4 : 4; port.SourceY = 0; port.SourceZ = 6;
                port.FieldOfView = 45; presentation.SetViewportPosition(index, new(port.SourceX, 0, 6));
            }
            renderer.BeginFrame(); renderer.Clear(.01f, .01f, .01f, 1);
            int submissions = 0;
            Assert(presentation.RenderViewports3D(scene, renderer, () =>
            {
                submissions++;
                renderer.SetCamera3D(scene.Camera3D.ViewMatrix, scene.Camera3D.ProjectionMatrix);
                Mesh3DState state = EnvironmentMapper.ToMesh3DState(scene.Environment, scene.Camera3D, new(), false, RenderDebugView.Shaded);
                state.LightingEnabled = false; state.ShadowsEnabled = false; state.FogEnabled = false;
                renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                renderer.DrawMesh(new MeshDrawCall { Mesh = cube, World = Matrix4x4.CreateTranslation(-4, 0, 0), Tint = new(1, .02f, .02f, 1), Alpha = 1 });
                renderer.DrawMesh(new MeshDrawCall { Mesh = cube, World = Matrix4x4.CreateTranslation(4, 0, 0), Tint = new(.02f, 1, .02f, 1), Alpha = 1 });
            }), "Configured 3D ports were not rendered.");
            renderer.EndFrame();
            Assert(renderer.TryReadFramePixels(out int actualWidth, out int actualHeight, out byte[] pixels), "Could not read split-camera output.");
            Assert(submissions == views && scene.Camera3D.Position == savedCamera, "Camera pass count or restored default camera is wrong.");
            for (int index = 0; index < views; index++)
            {
                RoomViewport port = room.Viewports[index]; int red = 0, green = 0;
                for (int y = port.PortY; y < port.PortY + height; y++)
                for (int x = port.PortX; x < port.PortX + width; x++)
                {
                    int offset = (y * actualWidth + x) * 4;
                    // Port isolation is independent of the backend's tone mapping / transfer curve.
                    if (pixels[offset + 2] > 30 && pixels[offset + 2] > pixels[offset + 1] * 2) red++;
                    if (pixels[offset + 1] > 30 && pixels[offset + 1] > pixels[offset + 2] * 2) green++;
                }
                Assert(index % 2 == 0 ? red > 100 && green == 0 : green > 100 && red == 0,
                    $"Camera {index} did not own its output rectangle: red={red}, green={green}, views={views}.");
            }
            string name = $"room-runtime-{views}-views-{backend}.png";
            using Bitmap image = new(actualWidth, actualHeight, PixelFormat.Format32bppArgb);
            BitmapData data = image.LockBits(new Rectangle(0, 0, actualWidth, actualHeight), ImageLockMode.WriteOnly, image.PixelFormat);
            try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { image.UnlockBits(data); }
            image.Save(Path.Combine(ctx.Captures, name));
            ctx.Report.Images.Add(new ImageResult("Room camera output", name, actualWidth, actualHeight, 0, 0));
            renderer.Present();
        }
        if (backend != RenderBackendOption.Software)
        {
            room.Environment.DynamicSky = true; room.Environment.AutomaticWeather = false;
            room.Environment.VolumetricClouds = false; room.Environment.TimeScale = 0;
            byte[][] skies = new byte[2][];
            for (int sample = 0; sample < 2; sample++)
            {
                room.Environment.TimeOfDayHours = sample == 0 ? 12 : 0;
                RoomSceneBuilder.ApplySceneSettings(scene, room);
                renderer.BeginFrame(); renderer.Clear(0, 0, 0, 1);
                presentation.RenderViewports3D(scene, renderer, () =>
                {
                    renderer.SetCamera3D(scene.Camera3D.ViewMatrix, scene.Camera3D.ProjectionMatrix);
                    Mesh3DState state = EnvironmentMapper.ToMesh3DState(scene.Environment, scene.Camera3D, new(), false, RenderDebugView.Shaded);
                    EnvironmentMapper.StampClimateAtmosphere(ref state, scene.Climate!, scene.Atmosphere!);
                    state.CloudTemporalEnabled = false;
                    renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                });
                renderer.EndFrame();
                Assert(renderer.TryReadFramePixels(out _, out _, out skies[sample]), "Could not read per-camera atmosphere.");
                renderer.Present();
            }
            for (int port = 0; port < 4; port++)
            {
                int x = room.Viewports[port].PortX + 30, y = room.Viewports[port].PortY + 30;
                int offset = (y * 640 + x) * 4;
                Assert(Enumerable.Range(0, 3).Sum(channel => Math.Abs(skies[0][offset + channel] - skies[1][offset + channel])) > 35,
                    "Split output lost the authored noon/midnight atmosphere in port " + port);
            }
        }
        foreach (RoomViewport port in room.Viewports) port.Enabled = false;
        Assert(!presentation.RenderViewports3D(scene, renderer, () => throw new InvalidOperationException()), "No-port rooms no longer use the normal camera.");
        renderer.ReleaseMesh(cube);
    }

    private sealed class CameraRegistryScope : IDisposable
    {
        private readonly Array _registry = (Array)typeof(Engine).GetField("_cameras3D", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        private readonly Array _saved;
        public CameraRegistryScope() { _saved = (Array)_registry.Clone(); Array.Clear(_registry); }
        public void Dispose() => Array.Copy(_saved, _registry, _registry.Length);
    }

    private sealed class AudioRecorder : IAudioSystem
    {
        private readonly Dictionary<int, string> _sounds = new();
        private readonly Dictionary<int, string> _channels = new();
        public Dictionary<string, float> Volumes { get; } = new();
        public int Plays { get; private set; }
        public int Stops { get; private set; }
        public bool LoopsOnly { get; private set; } = true;
        public float MasterVolume { get; set; } = 1;
        public int LoadSound(string path) { int id = _sounds.Count + 1; _sounds[id] = path; return id; }
        public AudioChannel Play(int soundId, float volume = 1, float pitch = 1, bool loop = false)
        { int id = ++Plays; _channels[id] = _sounds[soundId]; LoopsOnly &= loop; return new(id); }
        public void Stop(AudioChannel channel) => Stops++;
        public void StopAll() { }
        public bool IsPlaying(AudioChannel channel) => channel.IsValid;
        public void SetChannelVolume(AudioChannel channel, float volume) => Volumes[_channels[channel.Id]] = volume;
        public void SetChannelPosition(AudioChannel channel, Vector3 position) { }
        public void SetListener(Vector3 position, Vector3 forward) { }
        public void Update() { }
    }
    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}
