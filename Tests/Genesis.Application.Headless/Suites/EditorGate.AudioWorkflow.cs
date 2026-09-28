using System.Collections;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Audio;
using Genesis.Runtime;
using Genesis.Runtime.Input;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Assets;
using Genesis.Shared.Audio;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

internal static partial class EditorGate
{
    private static void AudioNoviceWorkflow(HeadlessContext ctx, GateSuite.GateFixture fixture, AudioEditorControl editor, Form host)
    {
        string? created = null;
        string imported = string.Empty;
        HeadlessHarness.Step("Audio Quick setup imports real samples, applies a starting preset and rejects invalid edits", () =>
        {
            string[] primary = editor.CommandBar.Items.Cast<ToolStripItem>().Where(item => item.Available && item.Alignment != ToolStripItemAlignment.Right)
                .Select(item => item.Text ?? string.Empty).ToArray();
            HeadlessHarness.Assert(primary.SequenceEqual(new[] { "Quick setup", "Use in game", "Options" }) && editor.CommandBar.IsSaveVisible,
                "Audio still exposes duplicate or crowded primary actions: " + string.Join(',', primary));
            string outside = Path.Combine(ctx.Captures, "audio-import-example.wav"); WriteToneWave(outside, 440, 2);
            byte[] original = File.ReadAllBytes(outside); imported = editor.ImportWave(outside);
            HeadlessHarness.Assert(editor.HasWaveform && editor.PreviewClip?.Duration == 2 && File.ReadAllBytes(outside).SequenceEqual(original),
                "Import failed to decode real samples or changed the original WAV.");
            ComboBox preset = SurfaceControls(editor).OfType<ComboBox>().Single(input => input.Name == "AudioStartingPreset");
            preset.SelectedIndex = 1;
            HeadlessHarness.Assert(editor.Settings is { Bus: "music", Loop: true, Spatial: false }, "The actual Music starting selector did not apply playback settings.");
            editor.Undo(); HeadlessHarness.Assert(editor.Settings.EnvironmentRole == "Water", "Starting preset Undo lost the previous ambience settings.");
            editor.Redo();
            string before = System.Text.Json.JsonSerializer.Serialize(editor.Settings);
            HeadlessHarness.Assert(!editor.TryApplyInspectorValue("volume", float.NaN) && !editor.TryApplyInspectorValue("pitch", float.PositiveInfinity)
                && !editor.TryApplyInspectorValue("environmentRole", "99") && System.Text.Json.JsonSerializer.Serialize(editor.Settings) == before,
                "An invalid numeric or ambience role edit mutated the working document.");
            HeadlessHarness.Assert(editor.SetRegion(.2f, .8f, .05f, .05f), "The imported source could not be trimmed and faded.");
            editor.Save();
            using AudioEditorControl reopened = new(editor.ResourcePath, fixture.Blank.RootPath);
            HeadlessHarness.Assert(!reopened.IsDirty && reopened.PreviewClip!.Samples.SequenceEqual(editor.PreviewClip!.Samples), "Quick setup did not save/reopen the same processed samples.");
        });
        HeadlessHarness.Step("the actual Use in game button writes executable sound Object events and prevents duplicate names", () =>
        {
            editor.OpenLinkedResourceRequested += (_, resourcePath) => created = resourcePath;
            editor.CommandBar.Items.OfType<ToolStripButton>().Single(button => button.Text == "Use in game").PerformClick();
            SurfaceControls(editor).OfType<TextBox>().Single(input => input.Name == "AudioObjectName").Text = "Saved Audio example";
            SurfaceControls(editor).OfType<Button>().Single(button => button.Name == "AudioCreateObject").PerformClick();
            HeadlessHarness.Assert(created is not null && File.Exists(created), "Use in game did not create/open its sound Object.");
            var events = ObjectEventStore.Load(created!);
            HeadlessHarness.Assert(events.Count == 3 && events["Create"].Contains("PlaySound(", StringComparison.Ordinal)
                && events["Step"].Contains("Space", StringComparison.Ordinal) && events["Destroy"].Contains("StopSound(", StringComparison.Ordinal),
                "The saved Object has no actual play/replay/stop events.");
            byte[] before = File.ReadAllBytes(created!);
            SurfaceControls(editor).OfType<Button>().Single(button => button.Name == "AudioCreateObject").PerformClick();
            HeadlessHarness.Assert(File.ReadAllBytes(created!).SequenceEqual(before)
                && SurfaceControls(editor).OfType<Label>().Single(label => label.Name == "AudioCreateResult").Text.Contains("already", StringComparison.OrdinalIgnoreCase),
                "Repeated creation silently produced another Object or replaced existing work.");
            string capture = "audio-use-in-game.png";
            ctx.Report.Images.Add(ImageResult.From("Audio Use in game", capture, VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture))));
        });
        HeadlessHarness.Step("the generated Object plays on the actual mixer and replay consumes saved resource and WAV edits", () =>
        {
            using XAudioSystem audio = new(fixture.Blank.RootPath);
            using RuntimeScene scene = new("Saved Audio gameplay") { Input = new InputState() };
            RoomAsset room = RoomAsset.Create("Sound Room", RoomDimension.TwoD);
            room.Nodes.Add(new RoomNode { Kind = RoomNodeKind.GameObject, Name = "Sound instance",
                GameObject = new() { Prefab = ResourceNames.Name(fixture.Blank.RootPath, created!) } });
            ProjectGameContext game = new(fixture.Blank.RootPath, scene, null, null, room, null, audio);
            VMEngine.Initialize(); ScriptHostSystem scripts = new(); scripts.SetContext(game);
            var oldGame = PgslCommands.ActiveGameContext; string oldPath = PgslCommands.ProjectPath;
            PgslContext? oldContext = PgslCommands.BindContext(new PgslContext());
            PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = fixture.Blank.RootPath;
            try
            {
                new RoomSceneBuilder(fixture.Blank.RootPath, scripts).Build(scene, room);
                PgslBehavior behavior = scripts.Instances.OfType<PgslBehavior>().Single();
                AudioChannel Channel() => new(Convert.ToInt32(behavior.GetVariablesSnapshot()["soundChannel"]));
                AudioChannel first = Channel();
                HeadlessHarness.Assert(first.IsValid && audio.IsPlaying(first) && scripts.RecentDiagnostics.Count == 0, "Create did not start a real saved-resource voice: " + string.Join(';', scripts.RecentDiagnostics));
                string asset = ResourceNames.Name(fixture.Blank.RootPath, editor.ResourcePath);
                int sound = audio.LoadSound(asset);
                object Entry() => ((IDictionary)typeof(XAudioSystem).GetField("_sounds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(audio)!)[sound]!;
                SoundEffect Effect() => (SoundEffect)Entry().GetType().GetField("Effect")!.GetValue(Entry())!;
                HeadlessHarness.Assert(Math.Abs(Effect().DurationInSeconds - .6) < .0001, "Gameplay played the untrimmed source.");
                editor.SetVolume(.37f); editor.SetRegion(.1f, .5f, .04f, .04f); editor.Save();
                HeadlessHarness.Assert(audio.LoadSound(asset) == sound && Math.Abs(Effect().DurationInSeconds - .4) < .0001
                    && Math.Abs((float)Entry().GetType().GetField("Gain")!.GetValue(Entry())! - .37f) < .001,
                    "The cached runtime asset did not reload actual samples/settings after Audio Editor Save.");
                scene.Input.OnKeyDown(Key.Space); scripts.Update(1f / 60); scene.Input.NextFrame(); scene.Input.OnKeyUp(Key.Space);
                AudioChannel replay = Channel();
                HeadlessHarness.Assert(replay.Id != first.Id && !audio.IsPlaying(first) && audio.IsPlaying(replay), "Space did not stop the old voice and play the saved edit.");
                byte[] valid = File.ReadAllBytes(editor.ResourcePath); File.WriteAllText(editor.ResourcePath, "{ broken");
                HeadlessHarness.Assert(audio.LoadSound(asset) == sound && Math.Abs(Effect().DurationInSeconds - .4) < .0001,
                    "An invalid live Audio replacement lost the last valid sound.");
                File.WriteAllBytes(editor.ResourcePath, valid);
                string source = Path.Combine(fixture.Blank.RootPath, imported.Replace('/', Path.DirectorySeparatorChar));
                WriteToneWave(source, 660, 3);
                editor.HandleAssetChanges(new ProjectAssetChangeSet(fixture.Blank.RootPath, [source], [editor.ResourcePath], generation: 1));
                editor.SetRegion(0, 0, 0, 0); editor.Save();
                HeadlessHarness.Assert(editor.PreviewClip?.Duration == 3 && audio.LoadSound(asset) == sound && Math.Abs(Effect().DurationInSeconds - 3) < .0001,
                    "A source WAV edit did not refresh both editor samples and gameplay playback.");
                scripts.Shutdown();
                HeadlessHarness.Assert(!audio.IsPlaying(replay) && scripts.RecentDiagnostics.Count == 0, "Destroy left its real audio voice running.");
            }
            finally { scripts.Shutdown(); PgslCommands.BindContext(oldContext); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath; }
        });
    }
}
