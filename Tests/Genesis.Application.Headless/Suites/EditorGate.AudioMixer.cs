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
using Genesis.Shared.Assets;
using Genesis.Shared.Audio;

namespace Genesis.Application.Headless.Suites;

internal static partial class EditorGate
{
    // ── The synthesiser's GS2 options, bus effects and snapshots ─────────────────────────────────────

    // Every preset as it rendered before the GS2 options were added (29 October 2026): samples, peak and
    // an FNV-1a hash of the 16-bit samples. The options all default to the old arithmetic, so these
    // must not move; a preset made on purpose to sound different needs its line changed here.
    private static readonly Dictionary<string, (int Length, int Peak, ulong Hash)> PresetFingerprints = new()
    {
        ["Coin"] = (14112, 29490, 0xEA4B8DD57E4859F5UL), ["Jump"] = (9702, 29490, 0xBDF01AA5DECD5A35UL),
        ["Laser"] = (11025, 29490, 0x0067CEBBC7B4667CUL), ["Explosion"] = (52920, 29490, 0x6B043A38E344C855UL),
        ["Hit"] = (6175, 29490, 0x9886F23F3507E7F7UL), ["Power Up"] = (15436, 29490, 0x0398A7CF2AD2F881UL),
        ["Blip"] = (3528, 29490, 0xDF7500E27A174FC3UL), ["Footstep"] = (3969, 29490, 0xD61EB65736F40EC5UL),
        ["Footstep Grass"] = (3969, 29490, 0xD61EB65736F40EC5UL), ["Footstep Path"] = (6175, 29490, 0x95540F8CA8A25BE7UL),
        ["Heartbeat"] = (40572, 29490, 0x29329DE7451C969DUL), ["Wind"] = (264600, 29490, 0x1B5FBDBC7098C965UL),
        ["Birdsong"] = (130095, 29490, 0x3F7465DFE96585F7UL), ["Drone"] = (352800, 29490, 0xF78B5DCD9E66084CUL),
        ["Choir Pad"] = (352800, 29490, 0x7918998EF7DEABC0UL), ["Organ Chord"] = (176400, 29490, 0x0A4AF2B2D954AAB3UL),
        ["Piano"] = (66150, 29490, 0x4AB8BA53C369E5FDUL), ["Pluck"] = (35280, 29490, 0xA85E6F1F8C08FD56UL),
        ["Bass"] = (44100, 29490, 0x8C6235C91B7E016BUL), ["Lead"] = (44100, 29490, 0x6E0BDEA6E5193A7DUL),
        ["Pad"] = (110250, 29490, 0x13DE145918F26ABBUL), ["Organ"] = (66150, 29490, 0x847005FCCA2A0186UL),
        ["Drum Kit"] = (79821, 29490, 0x45A826B1731629AAUL), ["Kick Drum"] = (15435, 29490, 0x2C6E79AB1535C5BEUL),
        ["Snare Drum"] = (9702, 29490, 0xC470760F9DCF42B7UL), ["Clap"] = (8203, 29490, 0x163DE20F3886D32CUL),
        ["Hi-Hat"] = (2646, 29490, 0xF4A99858CB2E9785UL), ["Open Hi-Hat"] = (15435, 29490, 0xE4E940B86D9CF517UL),
        ["Crash Cymbal"] = (61740, 29490, 0xAED33D4CA80BCCD1UL), ["Tom"] = (17640, 29490, 0x260BFB767B9D3073UL),
    };

    private static (int Length, int Peak, ulong Hash) Fingerprint(float[] samples)
    {
        ulong hash = 14695981039346656037UL;
        int peak = 0;
        foreach (float sample in samples)
        {
            short value = SynthQuantise(sample);
            peak = Math.Max(peak, Math.Abs((int)value));
            hash ^= (ushort)value;
            hash *= 1099511628211UL;
        }
        return (samples.Length, peak, hash);
    }

    // The power of one frequency in a channel of the samples (Goertzel).
    private static double PowerAt(float[] samples, double frequency, int channels = 1, int channel = 0)
    {
        int count = samples.Length / channels;
        double k = 2 * Math.Cos(2 * Math.PI * frequency / SoundSynth.SampleRate), s1 = 0, s2 = 0;
        for (int index = 0; index < count; index++)
        {
            double s = samples[index * channels + channel] + k * s1 - s2;
            s2 = s1; s1 = s;
        }
        return (s1 * s1 + s2 * s2 - k * s1 * s2) / count;
    }

    private static float[] Layer(SynthLayer layer, int seed = 7) => SoundSynth.Render([layer], seed: seed);

    private static void AudioMixerWorkflow(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Audio.Synth.OldPresetsRenderTheSameSamplesAsBefore", () =>
        {
            foreach ((string preset, (int Length, int Peak, ulong Hash) expected) in PresetFingerprints)
            {
                SynthSound sound = SynthSounds.Render(new SynthRecipe { Preset = preset });
                (int Length, int Peak, ulong Hash) now = Fingerprint(sound.Samples);
                HeadlessHarness.Assert(sound.Channels == 1 && now == expected,
                    $"{preset} changed: {now.Length} samples, peak {now.Peak}, hash {now.Hash:X16} (was {expected.Length}, {expected.Peak}, {expected.Hash:X16}).");
            }
            foreach (string preset in SoundSynth.Presets.Keys)
                HeadlessHarness.Assert(PresetFingerprints.ContainsKey(preset), $"The preset '{preset}' has no fingerprint here: add its line.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Audio.Synth.FiltersWithQNoisesCurvesAndBandLimitedWaves", () =>
        {
            HeadlessHarness.Step("a band-pass with Q keeps the band around its frequency; a sharper Q keeps a narrower band", () =>
            {
                SynthLayer noise = new() { Wave = SynthWave.WhiteNoise, Duration = 2, Attack = 0.001, Sustain = 1, Release = 0.01, Volume = 1 };
                float[] narrow = Layer(new SynthLayer { Wave = SynthWave.WhiteNoise, Duration = 2, Attack = 0.001, Sustain = 1, Release = 0.01, Volume = 1, BandPass = 1000, Q = 8 });
                double centre = PowerAt(narrow, 1000), below = PowerAt(narrow, 250), above = PowerAt(narrow, 4000), near = PowerAt(narrow, 1400);
                HeadlessHarness.Assert(centre > below * 20 && centre > above * 20 && centre > near * 2,
                    $"A 1 kHz band-pass at Q 8 did not peak at 1 kHz: {centre:E2} there, {below:E2} at 250 Hz, {near:E2} at 1.4 kHz, {above:E2} at 4 kHz.");
                float[] wide = Layer(new SynthLayer { Wave = SynthWave.WhiteNoise, Duration = 2, Attack = 0.001, Sustain = 1, Release = 0.01, Volume = 1, BandPass = 1000, Q = 0.7 });
                HeadlessHarness.Assert(PowerAt(wide, 1400) / PowerAt(wide, 1000) > PowerAt(narrow, 1400) / centre,
                    "A Q of 0.7 did not keep a wider band than a Q of 8.");
                float[] high = Layer(new SynthLayer { Wave = SynthWave.WhiteNoise, Duration = 2, Attack = 0.001, Sustain = 1, Release = 0.01, Volume = 1, HighPass = 3000 });
                HeadlessHarness.Assert(PowerAt(high, 8000) > PowerAt(high, 300) * 30, "A 3 kHz high-pass did not take away the low end.");
                float[] open = Layer(noise);
                HeadlessHarness.Assert(PowerAt(open, 300) > PowerAt(open, 8000) * 0.2 && PowerAt(open, 300) < PowerAt(open, 8000) * 5, "White noise is not even across the spectrum.");
            });
            HeadlessHarness.Step("pink and brown noise lean to the low end, brown more", () =>
            {
                double Slope(SynthWave wave)
                {
                    float[] samples = Layer(new SynthLayer { Wave = wave, Duration = 3, Attack = 0.001, Sustain = 1, Release = 0.01, Volume = 1 });
                    double low = new[] { 90.0, 110, 130 }.Average(f => PowerAt(samples, f)), high = new[] { 4800.0, 5000, 5200 }.Average(f => PowerAt(samples, f));
                    return low / high;
                }
                double white = Slope(SynthWave.WhiteNoise), pink = Slope(SynthWave.PinkNoise), brown = Slope(SynthWave.BrownNoise);
                HeadlessHarness.Assert(pink > white * 5 && brown > pink * 5, $"Low-to-high power: white {white:0.0}, pink {pink:0.0}, brown {brown:0.0}.");
            });
            HeadlessHarness.Step("an exponential decay falls faster at first than a linear one and ends in the same place", () =>
            {
                SynthLayer Tone(SynthEnvelope envelope) => new() { Wave = SynthWave.Sine, Frequency = 440, Duration = 1.2, Attack = 0.005, Decay = 1, Sustain = 0, Release = 0.1, Volume = 1, Envelope = envelope };
                float[] linear = Layer(Tone(SynthEnvelope.Linear)), curved = Layer(Tone(SynthEnvelope.Exponential));
                static double Rms(float[] samples, double from, double to) =>
                    Math.Sqrt(samples.Skip((int)(from * SoundSynth.SampleRate)).Take((int)((to - from) * SoundSynth.SampleRate)).Average(s => (double)s * s));
                HeadlessHarness.Assert(Rms(curved, 0.25, 0.35) < Rms(linear, 0.25, 0.35) * 0.5 && Rms(curved, 1.1, 1.19) < 0.01,
                    $"Exponential decay at 0.3 s: {Rms(curved, 0.25, 0.35):0.000} against linear {Rms(linear, 0.25, 0.35):0.000}.");
            });
            HeadlessHarness.Step("band-limited saw has less aliasing at a high note, and band-limited square has no offset", () =>
            {
                SynthLayer Saw(bool clean) => new() { Wave = SynthWave.Saw, Frequency = 3520, Duration = 1, Attack = 0.001, Sustain = 1, Release = 0.01, Volume = 1, BandLimited = clean };
                float[] raw = Layer(Saw(false)), clean = Layer(Saw(true));
                // A 3520 Hz saw's 12th and 13th harmonics fold back to 1860 Hz and 1660 Hz at 44.1 kHz.
                double rawAlias = PowerAt(raw, 1860) + PowerAt(raw, 1660), cleanAlias = PowerAt(clean, 1860) + PowerAt(clean, 1660);
                double rawTone = PowerAt(raw, 3520), cleanTone = PowerAt(clean, 3520);
                HeadlessHarness.Assert(cleanAlias / cleanTone < rawAlias / rawTone * 0.5, $"Aliasing against the note: raw {rawAlias / rawTone:E2}, band-limited {cleanAlias / cleanTone:E2}.");
                SynthLayer Square(bool clean) => new() { Wave = SynthWave.Square, Frequency = 220, Duty = 0.2, Duration = 1, Attack = 0.001, Sustain = 1, Release = 0.001, Volume = 1, BandLimited = clean };
                double offsetRaw = Layer(Square(false)).Average(s => (double)s), offsetClean = Layer(Square(true)).Average(s => (double)s);
                HeadlessHarness.Assert(Math.Abs(offsetRaw) > 0.2 && Math.Abs(offsetClean) < 0.02, $"Square at duty 0.2: offset {offsetRaw:0.000} raw, {offsetClean:0.000} band-limited.");
            });
            HeadlessHarness.Step("a panned layer makes a stereo sound, placed in the ear it is panned to", () =>
            {
                SynthSound sound = SynthSounds.Render(new SynthRecipe { Layers = [new SynthLayer { Wave = SynthWave.Sine, Frequency = 440, Duration = 0.5, Pan = -1 }] });
                float left = 0, right = 0;
                for (int index = 0; index < sound.Samples.Length; index += 2) { left = Math.Max(left, Math.Abs(sound.Samples[index])); right = Math.Max(right, Math.Abs(sound.Samples[index + 1])); }
                HeadlessHarness.Assert(sound.Channels == 2 && Math.Abs(sound.Seconds - 0.5) < 0.001 && left > 0.8f && right < 0.001f,
                    $"Pan -1 gave {sound.Channels} channels, left peak {left:0.000}, right {right:0.000}.");
                string wav = SynthSounds.WithWavFile(sound, "Stereo probe", path => { File.Copy(path, Path.Combine(ctx.Captures, "synth-stereo.wav"), true); return Path.Combine(ctx.Captures, "synth-stereo.wav"); });
                PcmAudioClip clip = PcmAudioClip.Load(wav);
                HeadlessHarness.Assert(clip.Channels == 2 && Math.Abs(clip.Duration - 0.5) < 0.001, $"The stereo WAV reads as {clip.Channels} channels, {clip.Duration:0.000} s.");
            });
            HeadlessHarness.Step("a loop render is exactly as long as asked and its end leads on to its start", () =>
            {
                foreach (string preset in new[] { "Wind", "Drone" })
                {
                    float[] samples = SynthSounds.Render(new SynthRecipe { Preset = preset, LoopSeconds = 3 }).Samples;
                    double step = Enumerable.Range(1, samples.Length - 1).Average(index => Math.Abs((double)samples[index] - samples[index - 1]));
                    double join = Math.Abs(samples[^1] - samples[0]);
                    HeadlessHarness.Assert(samples.Length == 3 * SoundSynth.SampleRate && join <= Math.Max(4 * step, 0.002),
                        $"{preset} as a 3 s loop: {samples.Length} samples, the join jumps {join:0.0000} where a sample to the next moves {step:0.0000} on average.");
                }
            });
            HeadlessHarness.Step("more than 512 layers make a sound", () =>
            {
                List<SynthLayer> many = [.. Enumerable.Range(0, 600).Select(index => new SynthLayer { Frequency = 200 + index, Start = index * 0.002, Duration = 0.05, Volume = 0.1 })];
                SynthSound sound = SynthSounds.Render(new SynthRecipe { Layers = many });
                HeadlessHarness.Assert(sound.LayerCount == 600 && sound.Samples.Max(sample => Math.Abs(sample)) > 0.5f, "600 layers did not render.");
            });
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Audio.Mixer.BusEffectsChangeTheMixedOutput", () =>
        {
            string wav = Path.Combine(ctx.Workspace, "mixer-probe-6k.wav");
            float[] tone = Enumerable.Range(0, SoundSynth.SampleRate * 2).Select(index => (float)(0.8 * Math.Sin(2 * Math.PI * 6000 * index / SoundSynth.SampleRate))).ToArray();
            SoundSynth.WriteWav(wav, tone);
            using XAudioSystem audio = new(string.Empty) { Muted = true };
            float Settle(Func<float> read)
            {
                float level = 0;
                for (int frame = 0; frame < 20; frame++) { System.Threading.Thread.Sleep(20); audio.Update(); level = Math.Max(level, read()); }
                return level;
            }
            audio.GetBusLevel("probe");
            int sound = audio.LoadSound(wav, new AudioAssetSettings { Bus = "probe", Loop = true });
            AudioChannel channel = audio.Play(sound, 1f, 1f, loop: true);
            HeadlessHarness.Assert(channel.IsValid, "The probe tone did not play.");
            float plain = Settle(() => audio.GetBusLevel("probe"));
            HeadlessHarness.Assert(plain > 0.3f, $"The bus meter read {plain:0.000} for a 0.8 tone with no effects.");
            audio.SetBusEffects("probe", new AudioBusEffects { FilterEnabled = true, FilterType = "LowPass", FilterFrequency = 300 });
            System.Threading.Thread.Sleep(100);
            float filtered = Settle(() => audio.GetBusLevel("probe"));
            HeadlessHarness.Assert(filtered < plain * 0.2f, $"A 300 Hz low-pass left a 6 kHz tone at {filtered:0.000} of {plain:0.000}.");
            audio.SetBusEffects("probe", new AudioBusEffects { EqEnabled = true, EqHigh = -18, EqHighFrequency = 6000 });
            System.Threading.Thread.Sleep(100);
            float cut = Settle(() => audio.GetBusLevel("probe"));
            HeadlessHarness.Assert(cut < plain * 0.5f, $"An EQ cut of 18 dB at 6 kHz left the tone at {cut:0.000} of {plain:0.000}.");
            audio.SetBusEffects("probe", new AudioBusEffects { CompressorEnabled = true, CompressorThreshold = -30, CompressorRatio = 10, CompressorAttack = 0.01f });
            Settle(() => audio.GetBusLevel("probe"));
            HeadlessHarness.Assert(audio.GetBusCompression("probe") < -10f, $"A compressor at -30 dB, 10:1, turned a loud bus down only {audio.GetBusCompression("probe"):0.0} dB.");
            audio.SetBusEffects("probe", new AudioBusEffects());
            System.Threading.Thread.Sleep(100);
            float back = Settle(() => audio.GetBusLevel("probe"));
            HeadlessHarness.Assert(back > plain * 0.8f && audio.GetBusCompression("probe") == 0f, $"Taking the effects off left the bus at {back:0.000} of {plain:0.000}.");
            audio.Stop(channel);
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Audio.Mixer.SnapshotsBlendToTheirTargetInTimeFromScriptAndProject", () =>
        {
            string root = Path.Combine(ctx.Workspace, "MixerProject");
            Directory.CreateDirectory(root);
            AudioMixerSettings mixer = new();
            mixer.Buses["music"] = new AudioBusMix { Volume = 0.5f };
            mixer.Snapshots["Combat"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["music"] = new AudioBusMix { Volume = 0.3f, Effects = new AudioBusEffects { ReverbEnabled = true, ReverbPlace = "hall", ReverbMix = 0.5f } },
                ["sfx"] = new AudioBusMix { Volume = 1f, Effects = new AudioBusEffects { FilterEnabled = true, FilterType = "LowPass", FilterFrequency = 800 } },
            };
            mixer.Save(root);
            using XAudioSystem audio = new(root) { Muted = true };
            HeadlessHarness.Assert(Math.Abs(audio.GetBusVolume("music") - 0.5f) < 0.001f && audio.SnapshotNames.Contains("Combat"),
                "The project's AudioMixer.json did not give the music its volume or the snapshot.");
            using RuntimeScene scene = new("Mixer gameplay") { Input = new InputState() };
            ProjectGameContext game = new(root, scene, null, null, RoomAsset.Create("Mixer Room", RoomDimension.TwoD), null, audio);
            var oldGame = PgslCommands.ActiveGameContext;
            PgslCommands.ActiveGameContext = game;
            try
            {
                HeadlessHarness.Step("a script saves the mix as a snapshot and blends to the project's Combat over 0.4 s", () =>
                {
                    PgslCommands.AudioSnapshotSave("Explore");
                    HeadlessHarness.Assert(PgslCommands.AudioSnapshotBlend("Combat", 0.4) && PgslCommands.AudioSnapshotProgress() < 1 && PgslCommands.AudioSnapshotCurrent() == "Combat",
                        "The blend to Combat did not start.");
                    System.Threading.Thread.Sleep(150); audio.Update();
                    float halfway = audio.GetBusVolume("music");
                    HeadlessHarness.Assert(halfway < 0.49f && halfway > 0.31f && PgslCommands.AudioMusicReverbEnabled && PgslCommands.AudioMusicReverbMix < 0.49,
                        $"Part way the music was at {halfway:0.000} with reverb mix {PgslCommands.AudioMusicReverbMix:0.00}.");
                    System.Threading.Thread.Sleep(300); audio.Update();
                    AudioBusEffects music = audio.GetBusEffects("music"), sfx = audio.GetBusEffects("sfx");
                    HeadlessHarness.Assert(Math.Abs(audio.GetBusVolume("music") - 0.3f) < 0.001f && music.ReverbEnabled && music.ReverbPlace == "hall" && Math.Abs(music.ReverbMix - 0.5f) < 0.001f
                        && sfx.FilterEnabled && Math.Abs(sfx.FilterFrequency - 800) < 0.5f && PgslCommands.AudioSnapshotProgress() == 1 && !PgslCommands.AudioSnapshotEnabled,
                        $"After 0.45 s the blend had not arrived: music {audio.GetBusVolume("music"):0.000}, reverb {music.ReverbEnabled} {music.ReverbMix:0.00}, sfx filter {sfx.FilterEnabled} {sfx.FilterFrequency:0}.");
                });
                HeadlessHarness.Step("the parts and bus commands set effects, and blending back to Explore takes them off", () =>
                {
                    PgslCommands.AudioEffectsEqHigh = -6;
                    PgslCommands.AudioEffectsEqEnabled = true;
                    PgslCommands.BusCompressor("ui", -20, 4);
                    HeadlessHarness.Assert(audio.GetBusEffects("sfx") is { EqEnabled: true, EqHigh: -6 } && audio.GetBusEffects("ui").CompressorEnabled,
                        "Engine.Audio.Effects.Eq or BusCompressor did not set the bus's effects.");
                    HeadlessHarness.Assert(PgslCommands.AudioSnapshotBlend("Explore", 0), "Explore was not kept.");
                    AudioBusEffects sfx = audio.GetBusEffects("sfx");
                    HeadlessHarness.Assert(Math.Abs(audio.GetBusVolume("music") - 0.5f) < 0.001f && !sfx.Active && !audio.GetBusEffects("music").Active,
                        "Blending at once back to Explore did not restore the music's volume and take the effects off.");
                    HeadlessHarness.Assert(!PgslCommands.AudioSnapshotBlend("Nowhere", 1), "A snapshot that does not exist was blended to.");
                });
            }
            finally { PgslCommands.ActiveGameContext = oldGame; }
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Audio.Mixer.PageSavesTheProjectMixerThatAGameStartsWith", () =>
        {
            string parent = Path.Combine(ctx.Workspace, "AudioMixerEditor");
            Directory.CreateDirectory(parent);
            Genesis.Application.Core.Projects.ProjectSession project = new Genesis.Application.Core.Projects.ProjectService().CreateProject(parent, "Mixer Editor", "Blank");
            string resource = new ResourceService(project).CreateResource(project.AssetsPath, ResourceKind.Audio, "Mixer Probe");
            Form host = GateSuite.NewHost(1000, 760);
            AudioEditorControl editor = new(resource, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(4, 20);
            try
            {
                ToolStripDropDownButton options = editor.CommandBar.Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options");
                options.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Mixer and snapshots…").PerformClick();
                GateSuite.Pump(3, 20);
                FlowLayoutPanel page = SurfaceControls(editor).OfType<FlowLayoutPanel>().Single(panel => panel.Name == "AudioMixer");
                HeadlessHarness.Assert(page.Visible, "Mixer and snapshots did not open the Mixer page.");
                ComboBox bus = SurfaceControls(editor).OfType<ComboBox>().Single(control => control.Name == "AudioMixerBus");
                bus.SelectedItem = "music";
                SurfaceControls(editor).OfType<CheckBox>().Single(control => control.Name == "AudioMixerReverb").Checked = true;
                SurfaceControls(editor).OfType<NumericUpDown>().Single(control => control.Name == "AudioMixerReverbMix").Value = 45;
                SurfaceControls(editor).OfType<NumericUpDown>().Single(control => control.Name == "AudioMixerVolume").Value = 70;
                SurfaceControls(editor).OfType<TextBox>().Single(control => control.Name == "AudioMixerSnapshotName").Text = "Cavern";
                SurfaceControls(editor).OfType<Button>().Single(control => control.Name == "AudioMixerKeepSnapshot").PerformClick();
                SurfaceControls(editor).OfType<Button>().Single(control => control.Name == "AudioMixerSave").PerformClick();
                string capture = "audio-mixer.png";
                ctx.Report.Images.Add(ImageResult.From("Audio editor Mixer page", capture, VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture))));
                AudioMixerSettings saved = AudioMixerSettings.Load(project.RootPath);
                HeadlessHarness.Assert(File.Exists(AudioMixerSettings.PathFor(project.RootPath)) && saved.Buses.TryGetValue("music", out AudioBusMix? music)
                    && Math.Abs(music.Volume - 0.7f) < 0.001f && music.Effects.ReverbEnabled && Math.Abs(music.Effects.ReverbMix - 0.45f) < 0.001f && saved.Snapshots.ContainsKey("Cavern"),
                    "The Mixer page did not save the music's volume, reverb and the snapshot: " + saved.Serialize());
                using XAudioSystem game = new(project.RootPath) { Muted = true };
                HeadlessHarness.Assert(Math.Abs(game.GetBusVolume("music") - 0.7f) < 0.001f && game.GetBusEffects("music").ReverbEnabled && game.SnapshotNames.Contains("Cavern"),
                    "A game's audio did not start with the saved mixer.");
            }
            finally
            {
                host.Close();
                host.Dispose();
            }
        });
    }
}
