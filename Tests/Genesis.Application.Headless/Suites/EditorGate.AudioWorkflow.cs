using System.Collections;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Audio;
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
using JsonArray = System.Text.Json.Nodes.JsonArray;
using JsonObject = System.Text.Json.Nodes.JsonObject;

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
                // A sound rechecks its files at most twice a second (XAudioSystem.IsCurrent): an edit is
                // picked up within half a second, not on the very next play.
                System.Threading.Thread.Sleep(600);
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
                System.Threading.Thread.Sleep(600); // within the half second a sound takes to see a file change
                HeadlessHarness.Assert(editor.PreviewClip?.Duration == 3 && audio.LoadSound(asset) == sound && Math.Abs(Effect().DurationInSeconds - 3) < .0001,
                    "A source WAV edit did not refresh both editor samples and gameplay playback.");
                scripts.Shutdown();
                HeadlessHarness.Assert(!audio.IsPlaying(replay) && scripts.RecentDiagnostics.Count == 0, "Destroy left its real audio voice running.");
            }
            finally { scripts.Shutdown(); PgslCommands.BindContext(oldContext); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath; }
        });
        AudioSynthWorkflow(ctx);
        MusicStudioWorkflow(ctx);
        AudioMixerWorkflow(ctx);
    }

    // ── Make a sound here: the synthesiser from inside the Audio editor ─────────────────────────────

    private static short SynthQuantise(float sample) => (short)Math.Clamp((int)Math.Round(sample * 32767), short.MinValue, short.MaxValue);

    // The samples a recipe makes once written as a 16-bit WAV, which is what the editor and audio.synth both import.
    private static short[] SynthSamples(string preset, double length = 0, double pitch = 1) =>
        SynthSounds.Render(new SynthRecipe { Preset = preset, Length = length, Pitch = pitch }).Samples.Select(SynthQuantise).ToArray();

    private static int SignChanges(short[] samples)
    {
        int changes = 0;
        for (int index = 1; index < samples.Length; index++)
            if ((samples[index] >= 0) != (samples[index - 1] >= 0)) changes++;
        return changes;
    }

    private static void AudioSynthWorkflow(HeadlessContext ctx)
    {
        string parent = Path.Combine(ctx.Workspace, "AudioSynth");
        Directory.CreateDirectory(parent);
        Genesis.Application.Core.Projects.ProjectSession project =
            new Genesis.Application.Core.Projects.ProjectService().CreateProject(parent, "Audio Synth", "Blank");
        ResourceService resources = new(project);
        string audioFolder = Path.Combine(project.AssetsPath, "Audio");
        Directory.CreateDirectory(audioFolder);

        (Form Host, AudioEditorControl Editor, string Resource) Open(string name)
        {
            string resource = resources.CreateResource(audioFolder, ResourceKind.Audio, name);
            Form host = GateSuite.NewHost(1000, 700);
            AudioEditorControl editor = new(resource, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(6, 30);
            return (host, editor, resource);
        }
        T Find<T>(AudioEditorControl editor, string name) where T : Control =>
            SurfaceControls(editor).OfType<T>().Single(control => control.Name == name);
        string WavOf(string resource) =>
            Path.Combine(project.RootPath, (AudioAssetSettings.Load(resource)?.Source ?? throw new InvalidOperationException("The saved Audio resource names no sound file."))
                .Replace('/', Path.DirectorySeparatorChar));

        HeadlessHarness.RunCase(ctx.Report, "Editor.Audio.Synth.GalleryOffersEveryPresetAndTheInstructionsSayASoundCanBeMade", () =>
        {
            var opened = Open("Synth Gallery"); using Form host = opened.Host; AudioEditorControl editor = opened.Editor;
            HeadlessHarness.Step("the instructions say a sound can be made here or imported", () =>
            {
                string[] texts = SurfaceControls(editor).OfType<Label>().Select(label => label.Text).ToArray();
                HeadlessHarness.Assert(texts.Any(text => text.Contains("import", StringComparison.OrdinalIgnoreCase) && text.Contains("make", StringComparison.OrdinalIgnoreCase)),
                    "No instruction text on the Audio editor says a sound can be made here or imported: " + string.Join(" | ", texts));
                HeadlessHarness.Assert(Find<Button>(editor, "AudioMakeSound").Text.StartsWith("Make a sound", StringComparison.Ordinal),
                    "Quick setup has no Make a sound button.");
            });
            HeadlessHarness.Step("the gallery lists the registry's presets, the benchmark's footsteps and birdsong among them", () =>
            {
                IReadOnlyList<string> gallery = AudioEditorControl.SynthPresets;
                foreach (string name in new[] { "Coin", "Jump", "Laser", "Explosion", "Hit", "Power Up", "Blip", "Footstep Grass", "Footstep Path",
                    "Heartbeat", "Wind", "Birdsong", "Drone", "Choir Pad", "Organ Chord" })
                    HeadlessHarness.Assert(gallery.Contains(name) && SoundSynth.Presets.ContainsKey(name), $"The preset '{name}' is not in both the gallery and automation's registry.");
                foreach (string name in SoundSynth.Presets.Keys)
                    HeadlessHarness.Assert(gallery.Contains(name) || !SoundSynth.PresetInfo[name].InGallery, $"The preset '{name}' is in the registry but missing from the gallery.");
                HeadlessHarness.Assert(SynthSamples("Footstep").SequenceEqual(SynthSamples("Footstep Grass")) && !SynthSamples("Footstep Path").SequenceEqual(SynthSamples("Footstep Grass")),
                    "Footstep should be the grass step under its first name, and the path step a different sound.");
            });
            HeadlessHarness.Step("the Make a sound button opens the gallery panel and Back returns to Quick setup", () =>
            {
                Find<Button>(editor, "AudioMakeSound").PerformClick();
                FlowLayoutPanel panel = Find<FlowLayoutPanel>(editor, "AudioSynth");
                ListBox list = Find<ListBox>(editor, "AudioSynthPresets");
                HeadlessHarness.Assert(panel.Visible && list.Items.Cast<string>().SequenceEqual(AudioEditorControl.SynthPresets) && list.SelectedIndex == 0,
                    "The Make a sound panel did not open with the whole gallery listed.");
                HeadlessHarness.Assert(Find<Label>(editor, "AudioSynthDescription").Text.Length > 0, "The chosen preset has no description.");
                list.SelectedItem = "Birdsong";
                HeadlessHarness.Assert(Find<Label>(editor, "AudioSynthDescription").Text.Contains("chirps", StringComparison.OrdinalIgnoreCase)
                    && Find<CheckBox>(editor, "AudioSynthLoop").Checked, "Choosing Birdsong did not describe it or tick Repeat.");
                string capture = "audio-make-a-sound.png";
                ctx.Report.Images.Add(ImageResult.From("Audio Make a sound", capture, VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture))));
                Find<Button>(editor, "AudioSynthBack").PerformClick();
                HeadlessHarness.Assert(!panel.Visible && Find<Button>(editor, "AudioMakeSound").Visible, "Back to Quick setup left the Make a sound panel open.");
            });
            host.Close();
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Audio.Synth.ChoosingAPresetAndUsingItSavesAnAudioResourceThatReopens", () =>
        {
            var opened = Open("Synth Birdsong"); using Form host = opened.Host; AudioEditorControl editor = opened.Editor; string resource = opened.Resource;
            HeadlessHarness.Step("Birdsong through the real panel and Use button", () =>
            {
                Find<Button>(editor, "AudioMakeSound").PerformClick();
                Find<ListBox>(editor, "AudioSynthPresets").SelectedItem = "Birdsong";
                HeadlessHarness.Assert(Math.Abs((double)Find<NumericUpDown>(editor, "AudioSynthLength").Value - 2.95) < 0.006,
                    "The length did not start at the 2.95 s Birdsong is made at.");
                Find<Button>(editor, "AudioSynthUse").PerformClick();
                HeadlessHarness.Assert(!Find<FlowLayoutPanel>(editor, "AudioSynth").Visible && !editor.IsDirty,
                    "Use did not save and return to Quick setup: " + Find<Label>(editor, "AudioSynthResult").Text);
                AudioAssetSettings saved = AudioAssetSettings.Load(resource) ?? throw new InvalidOperationException("The saved Audio resource could not be read.");
                string wav = WavOf(resource);
                HeadlessHarness.Assert(saved.Source!.StartsWith("Assets/Audio/", StringComparison.OrdinalIgnoreCase) && File.Exists(wav) && saved.Loop,
                    $"The resource does not play a WAV made in Assets/Audio, repeating: source '{saved.Source}', loop {saved.Loop}.");
                PcmAudioClip clip = PcmAudioClip.Load(wav);
                HeadlessHarness.Assert(clip.Samples.Max(sample => Math.Abs((int)sample)) > 3000, "The made sound is silent.");
                HeadlessHarness.Assert(Math.Abs(clip.Duration - 2.95) < 0.001 && clip.SampleRate == SoundSynth.SampleRate && clip.Channels == 1,
                    $"The made sound is {clip.Duration:0.###} s at {clip.SampleRate} Hz, not 2.95 s.");
                HeadlessHarness.Assert(clip.Samples.SequenceEqual(SynthSamples("Birdsong")), "The saved WAV is not the Birdsong preset's samples.");
            });
            HeadlessHarness.Step("the saved resource reopens with the same samples and settings", () =>
            {
                using AudioEditorControl reopened = new(resource, project.RootPath);
                HeadlessHarness.Assert(!reopened.IsDirty && reopened.HasWaveform && reopened.Settings.Source == editor.Settings.Source && reopened.Settings.Loop
                    && reopened.PreviewClip!.Samples.SequenceEqual(editor.PreviewClip!.Samples) && reopened.PreviewClip.Samples.Any(sample => Math.Abs((int)sample) > 3000),
                    "The made sound did not reopen with its samples and repeat setting.");
            });
            HeadlessHarness.Step("a second use takes pitch and volume from the panel and replaces the sound, keeping the first file", () =>
            {
                string first = WavOf(resource);
                Find<Button>(editor, "AudioMakeSound").PerformClick();
                Find<ListBox>(editor, "AudioSynthPresets").SelectedItem = "Footstep Path";
                Find<NumericUpDown>(editor, "AudioSynthPitch").Value = 0.5m;
                Find<NumericUpDown>(editor, "AudioSynthVolume").Value = 60m;
                Find<Button>(editor, "AudioSynthUse").PerformClick();
                AudioAssetSettings saved = AudioAssetSettings.Load(resource) ?? throw new InvalidOperationException("The saved Audio resource could not be read.");
                HeadlessHarness.Assert(Math.Abs(saved.Volume - 0.6f) < 0.001f && !saved.Loop && File.Exists(first) && WavOf(resource) != first,
                    $"Pitch/volume were not carried into the saved resource (volume {saved.Volume}, loop {saved.Loop}).");
                HeadlessHarness.Assert(PcmAudioClip.Load(WavOf(resource)).Samples.SequenceEqual(SynthSamples("Footstep Path", pitch: 0.5)),
                    "The Footstep Path at half pitch is not what the shared synthesiser makes.");
            });
            host.Close();
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Audio.Synth.LengthPitchAndVolumeChangeTheSoundAndBadValuesAreRefused", () =>
        {
            var opened = Open("Synth Controls"); using Form host = opened.Host; AudioEditorControl editor = opened.Editor;
            HeadlessHarness.Step("length sets how long the sound lasts", () =>
            {
                editor.UseSynthSound("Wind", length: 2);
                HeadlessHarness.Assert(Math.Abs(editor.PreviewClip!.Duration - 2) < 0.001 && editor.PreviewClip.Samples.Max(sample => Math.Abs((int)sample)) > 3000,
                    $"Wind at a length of 2 s lasts {editor.PreviewClip.Duration:0.###} s.");
                editor.UseSynthSound("Coin", length: 1);
                HeadlessHarness.Assert(Math.Abs(editor.PreviewClip!.Duration - 1) < 0.001, $"Coin at a length of 1 s lasts {editor.PreviewClip.Duration:0.###} s.");
            });
            HeadlessHarness.Step("pitch doubles the frequency an octave up", () =>
            {
                editor.UseSynthSound("Blip");
                int normal = SignChanges(editor.PreviewClip!.Samples);
                editor.UseSynthSound("Blip", pitch: 2);
                int octave = SignChanges(editor.PreviewClip!.Samples);
                HeadlessHarness.Assert(normal > 100 && octave > normal * 1.8 && octave < normal * 2.2,
                    $"An octave up should double the sign changes of an 880 Hz blip: {normal} then {octave}.");
            });
            HeadlessHarness.Step("volume and repeat follow the arguments, or the preset", () =>
            {
                editor.UseSynthSound("Blip", volume: 0.4);
                HeadlessHarness.Assert(Math.Abs(editor.Settings.Volume - 0.4f) < 0.001f && !editor.Settings.Loop, "Volume 0.4 or the effect's no-repeat was not applied.");
                editor.UseSynthSound("Wind");
                HeadlessHarness.Assert(editor.Settings.Loop, "Wind (ambience) should repeat unless told not to.");
                editor.UseSynthSound("Wind", loop: false);
                HeadlessHarness.Assert(!editor.Settings.Loop, "Repeat could not be turned off for Wind.");
            });
            HeadlessHarness.Step("an unknown preset or a pitch of zero is refused and the sound is left as it was", () =>
            {
                string? source = editor.Settings.Source;
                bool unknown = false, silent = false;
                try { editor.UseSynthSound("Kazoo"); } catch (ArgumentException exception) { unknown = exception.Message.Contains("Birdsong", StringComparison.Ordinal); }
                try { editor.UseSynthSound("Blip", pitch: 0); } catch (ArgumentException) { silent = true; }
                HeadlessHarness.Assert(unknown && silent && editor.Settings.Source == source,
                    $"A bad preset or pitch was not refused cleanly (unknown {unknown}, pitch {silent}).");
            });
            host.Close();
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Audio.Synth.EditorAndAutomationMakeTheSameSoundIncludingTheNewPresets", () =>
        {
            var opened = Open("Synth Same"); using Form host = opened.Host; AudioEditorControl editor = opened.Editor;
            Genesis.Application.Core.Automation.AutomationContext context = Genesis.Application.Core.Automation.AutomationContext.For(project);
            context.Caller = "test";
            Genesis.Application.Core.Automation.AutomationRegistry registry = Genesis.Application.Core.Automation.AutomationRegistry.CreateDefault();
            Genesis.Application.Core.Automation.AutomationResult Call(string name, JsonObject input) => registry.Invoke(context, name, input);
            short[] Made(Genesis.Application.Core.Automation.AutomationResult result)
            {
                HeadlessHarness.Assert(result.Ok, "audio.synth failed: " + result.Message);
                string resource = context.Resolve(result.Data!["path"]!.GetValue<string>());
                Newtonsoft.Json.Linq.JObject document = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(resource));
                string source = (string)document["source"]!;
                string wav = Path.Combine(Path.GetDirectoryName(resource)!, Path.GetFileName(source));
                if (!File.Exists(wav)) wav = context.Resolve(source);
                return PcmAudioClip.Load(wav).Samples;
            }
            HeadlessHarness.Step("audio.synth makes Birdsong, and the editor makes the same samples", () =>
            {
                short[] automation = Made(Call("audio.synth", new JsonObject { ["name"] = "Auto Birdsong", ["preset"] = "Birdsong" }));
                editor.UseSynthSound("Birdsong");
                HeadlessHarness.Assert(automation.Length > 100000 && automation.Any(sample => Math.Abs((int)sample) > 3000)
                    && automation.SequenceEqual(editor.PreviewClip!.Samples) && automation.SequenceEqual(SynthSamples("Birdsong")),
                    "audio.synth and the Audio editor did not make the same Birdsong.");
            });
            HeadlessHarness.Step("the presets are the benchmark's own sounds", () =>
            {
                // NatureWalk.ps1 builds these by hand: four chirps gliding up, three gliding down; a path step of noise over a thud.
                JsonArray chirps = new();
                for (int index = 0; index < 4; index++)
                {
                    double pitch = 2780 + index * 180;
                    chirps.Add(new JsonObject { ["wave"] = "Sine", ["frequency"] = pitch, ["frequencyEnd"] = pitch * 1.45, ["start"] = 0.4 + index * 0.13, ["duration"] = 0.08,
                        ["attack"] = 0.008, ["decay"] = 0.03, ["sustain"] = 0.4, ["release"] = 0.04, ["volume"] = 0.35 });
                }
                for (int index = 0; index < 3; index++)
                {
                    double pitch = 3200 - index * 200;
                    chirps.Add(new JsonObject { ["wave"] = "Sine", ["frequency"] = pitch, ["frequencyEnd"] = pitch * 0.8, ["start"] = 2.52 + index * 0.16, ["duration"] = 0.11,
                        ["attack"] = 0.01, ["release"] = 0.05, ["volume"] = 0.25 });
                }
                HeadlessHarness.Assert(Made(Call("audio.synth", new JsonObject { ["name"] = "Hand Birds", ["layers"] = chirps })).SequenceEqual(SynthSamples("Birdsong")),
                    "The Birdsong preset is not the benchmark's bird chirps.");
                JsonArray step = new(
                    new JsonObject { ["wave"] = "Noise", ["frequency"] = 700, ["duration"] = 0.14, ["attack"] = 0.003, ["decay"] = 0.05, ["sustain"] = 0.3, ["release"] = 0.06, ["lowPass"] = 1400, ["volume"] = 0.8 },
                    new JsonObject { ["wave"] = "Sine", ["frequency"] = 90, ["frequencyEnd"] = 60, ["duration"] = 0.08, ["attack"] = 0.002, ["release"] = 0.05, ["volume"] = 0.4 });
                HeadlessHarness.Assert(Made(Call("audio.synth", new JsonObject { ["name"] = "Hand Step", ["layers"] = step })).SequenceEqual(SynthSamples("Footstep Path")),
                    "The Footstep Path preset is not the benchmark's step on the path.");
                HeadlessHarness.Assert(Made(Call("audio.synth", new JsonObject { ["name"] = "Auto Step", ["preset"] = "Footstep" })).SequenceEqual(SynthSamples("Footstep Grass")),
                    "audio.synth's Footstep preset changed: it is the benchmark's step on grass.");
            });
            HeadlessHarness.Step("an unknown preset is still explained, now with the new names", () =>
            {
                Genesis.Application.Core.Automation.AutomationResult odd = Call("audio.synth", new JsonObject { ["name"] = "Odd", ["preset"] = "Kazoo" });
                HeadlessHarness.Assert(!odd.Ok && odd.Message.Contains("'Kazoo' is not a preset. Presets:", StringComparison.Ordinal)
                    && odd.Message.Contains("Birdsong", StringComparison.Ordinal) && odd.Message.Contains("Footstep Path", StringComparison.Ordinal),
                    "An unknown preset was not explained with the preset names: " + odd.Message);
            });
            host.Close();
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Audio.Synth.ImportingAWaveFileStillWorksBesideMakingASound", () =>
        {
            var opened = Open("Synth Import"); using Form host = opened.Host; AudioEditorControl editor = opened.Editor; string resource = opened.Resource;
            string outside = Path.Combine(ctx.Captures, "audio-synth-import.wav"); WriteToneWave(outside, 440, 2);
            byte[] original = File.ReadAllBytes(outside);
            string imported = editor.ImportWave(outside);
            HeadlessHarness.Assert(imported.StartsWith("Assets/Audio/", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(project.RootPath, imported.Replace('/', Path.DirectorySeparatorChar)))
                && editor.HasWaveform && editor.PreviewClip?.Duration == 2 && File.ReadAllBytes(outside).SequenceEqual(original),
                "Importing a WAV no longer copies it into Assets/Audio and selects it.");
            editor.Save();
            using (AudioEditorControl reopened = new(resource, project.RootPath))
                HeadlessHarness.Assert(reopened.Settings.Source == imported && reopened.PreviewClip!.Samples.SequenceEqual(editor.PreviewClip!.Samples),
                    "An imported WAV did not save and reopen.");
            string made = editor.UseSynthSound("Coin");
            HeadlessHarness.Assert(made != imported && editor.Settings.Source == made && File.Exists(Path.Combine(project.RootPath, imported.Replace('/', Path.DirectorySeparatorChar))),
                "Making a sound replaced the imported WAV instead of sitting beside it.");
            editor.Save();
            host.Close();
        });
    }
}
