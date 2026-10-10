using Genesis.Shared.Audio;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// How a running game finds a project's resources: a sound's file named beside its Audio
/// resource, and the resources of one kind in a folder.
/// </summary>
internal static class RuntimeResourcesSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "RuntimeResources");

        HeadlessHarness.RunCase(ctx.Report, "Engine.Audio.ASourceBesideItsResourcePlaysByName", () =>
        {
            string project = Path.Combine(ctx.Workspace, "AudioSources" + Guid.NewGuid().ToString("N")[..6]);
            string audio = Path.Combine(project, "Assets", "Audio");
            string sounds = Path.Combine(project, "Assets", "Sounds");
            Directory.CreateDirectory(audio);
            Directory.CreateDirectory(sounds);
            // Written beside its resource, as a tool writes it ("source": "Beside.wav").
            WriteSilentWave(Path.Combine(audio, "Beside.wav"), 0.25);
            File.WriteAllText(Path.Combine(audio, "Beside.audio.json"), "{ \"source\": \"Beside.wav\", \"volume\": 0.5 }");
            // Named from the project, as Studio's Audio editor writes it: projects made so still play.
            WriteSilentWave(Path.Combine(sounds, "Studio.wav"), 0.25);
            File.WriteAllText(Path.Combine(audio, "Studio.audio.json"), "{ \"Source\": \"Assets/Sounds/Studio.wav\" }");
            // A neighbouring folder, relative to the resource.
            File.WriteAllText(Path.Combine(audio, "Next.audio.json"), "{ \"source\": \"../Sounds/Studio.wav\" }");
            // A file outside the project is not the project's: an export would not carry it.
            string outside = Path.Combine(ctx.Workspace, "Outside" + Guid.NewGuid().ToString("N")[..6] + ".wav");
            WriteSilentWave(outside, 0.25);
            File.WriteAllText(Path.Combine(audio, "Outside.audio.json"),
                "{ \"source\": \"" + Path.GetRelativePath(audio, outside).Replace('\\', '/') + "\" }");
            Genesis.Shared.Assets.ResourceCatalog.Invalidate(project);

            HeadlessHarness.Assert(AudioAssetSettings.ResolveSourceBeside(project, Path.Combine(audio, "Beside.audio.json"), "Beside.wav")
                    == Path.Combine(audio, "Beside.wav")
                && AudioAssetSettings.ResolveSourceBeside(project, Path.Combine(audio, "Studio.audio.json"), "Assets/Sounds/Studio.wav") == null
                && AudioAssetSettings.ResolveSourceBeside(project, Path.Combine(audio, "Outside.audio.json"),
                    Path.GetRelativePath(audio, outside).Replace('\\', '/')) == null,
                "A source was looked for in the wrong place beside its resource.");

            // In the project, and in a copy laid out as an export lays it out.
            string exported = Path.Combine(ctx.Workspace, "AudioExport" + Guid.NewGuid().ToString("N")[..6]);
            CopyDirectory(project, exported);
            foreach (string root in new[] { project, exported })
            {
                using var mixer = new Genesis.Audio.XAudioSystem(root) { Muted = true };
                HeadlessHarness.Assert(mixer.LoadSound("Beside") != 0,
                    $"PlaySound(\"Beside\") found no sound when its source is written beside the resource ({root}).");
                HeadlessHarness.Assert(mixer.LoadSound("Studio") != 0,
                    $"A source named from the project, as Studio writes it, no longer plays ({root}).");
                HeadlessHarness.Assert(mixer.LoadSound("Next") != 0,
                    $"A source in a neighbouring folder, named from the resource, did not play ({root}).");
                HeadlessHarness.Assert(mixer.LoadSound("Outside") == 0,
                    $"A source outside the project was played ({root}).");
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Pgsl.Resources.ResourceListNamesAFoldersResourcesOfOneKind", () =>
        {
            string project = Path.Combine(ctx.Workspace, "ResourceList" + Guid.NewGuid().ToString("N")[..6]);
            string packs = Path.Combine(project, "Assets", "Shaders", "Packs");
            Directory.CreateDirectory(Path.Combine(packs, "More"));
            File.WriteAllText(Path.Combine(project, "Listing.genesisproj"), "{}");
            void Shader(string folder, string name, string pipeline) => File.WriteAllText(Path.Combine(folder, name + ".shader.json"),
                "{ \"pipeline\": \"" + pipeline + "\", \"entry\": \"MainPS\", \"source\": \"float4 MainPS() : SV_Target { return 1; }\" }");
            Shader(packs, "Pack Noir", "Fullscreen");
            Shader(packs, "Pack Ink", "Fullscreen");
            Shader(packs, "Chunk Surface", "Mesh");
            Shader(Path.Combine(packs, "More"), "Pack Deep", "Fullscreen");
            Shader(Path.Combine(project, "Assets", "Shaders"), "Elsewhere", "Fullscreen");
            File.WriteAllText(Path.Combine(packs, "Pack Preview.image.json"), "{}");
            // A resource's own name, from its identity file, is the name listed.
            File.WriteAllText(Path.Combine(packs, "Pack Ink.shader.json.meta"), "{ \"resourceName\": \"Ink Pack\" }");
            Genesis.Shared.Assets.ResourceCatalog.Invalidate(project);

            string exported = Path.Combine(ctx.Workspace, "ResourceListExport" + Guid.NewGuid().ToString("N")[..6]);
            CopyDirectory(project, exported);
            string previousProject = Genesis.Runtime.Scripting.PgslCommands.ProjectPath;
            var previousContext = Genesis.Runtime.Scripting.PgslCommands.BindContext(new Genesis.Shared.Scripting.PgslContext());
            try
            {
                foreach (string root in new[] { project, exported })
                {
                    Genesis.Runtime.Scripting.PgslCommands.ProjectPath = root;
                    string Names(double list)
                    {
                        var names = new List<string>();
                        for (int i = 0; i < Genesis.Runtime.Scripting.PgslCommands.DsListSize(list); i++)
                            names.Add(Genesis.Runtime.Scripting.PgslCommands.DsListGetString(list, i));
                        Genesis.Runtime.Scripting.PgslCommands.DsListDestroy(list);
                        return string.Join("|", names);
                    }

                    string fullscreen = Names(Genesis.Runtime.Scripting.PgslCommands.ResourceList("Shaders/Packs", "Fullscreen shader"));
                    HeadlessHarness.Assert(fullscreen == "Ink Pack|Pack Noir",
                        $"The full screen shaders of Shaders/Packs were listed as '{fullscreen}' ({root}).");
                    string fromProject = Names(Genesis.Runtime.Scripting.PgslCommands.ResourceList("Assets/Shaders/Packs", "shader"));
                    HeadlessHarness.Assert(fromProject == "Chunk Surface|Ink Pack|Pack Noir",
                        $"Every Shader of Assets/Shaders/Packs was listed as '{fromProject}' ({root}).");
                    string deep = Names(Genesis.Runtime.Scripting.PgslCommands.ResourceList("Shaders/Packs", "Fullscreen shader", true));
                    HeadlessHarness.Assert(deep == "Ink Pack|Pack Deep|Pack Noir",
                        $"With subfolders the full screen shaders were listed as '{deep}' ({root}).");
                    string every = Names(Genesis.Runtime.Scripting.PgslCommands.ResourceList("Shaders/Packs", ""));
                    HeadlessHarness.Assert(every == "Chunk Surface|Ink Pack|Pack Noir|Pack Preview",
                        $"Every resource of the folder was listed as '{every}' ({root}).");
                    foreach ((string folder, string type) in new[] { ("../..", "Shader"), ("Missing", "Shader"), ("Shaders/Packs", "Spaceship"), ("C:/", "") })
                        HeadlessHarness.Assert(Names(Genesis.Runtime.Scripting.PgslCommands.ResourceList(folder, type)) == "",
                            $"ResourceList(\"{folder}\", \"{type}\") listed something ({root}).");
                }
            }
            finally
            {
                Genesis.Runtime.Scripting.PgslCommands.ProjectPath = previousProject;
                Genesis.Runtime.Scripting.PgslCommands.BindContext(previousContext);
            }
        });

        RunBackgroundDecode(ctx);
        RunCompressedAndPreloaded(ctx);
    }

    /// <summary>
    /// A game's Ogg sounds, short or long, are decoded on a worker; every way a script names a sound
    /// finds the same one, decoded once; a sound preloaded (SoundPreload, or the project's preload
    /// budget) starts at once when it is played.
    /// </summary>
    internal static void RunCompressedAndPreloaded(HeadlessContext ctx)
    {
        string tone = Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone-440-660.ogg");
        string NewProject(string name)
        {
            string project = Path.Combine(ctx.Workspace, name + Guid.NewGuid().ToString("N")[..6]);
            string audio = Path.Combine(project, "Assets", "Audio");
            Directory.CreateDirectory(audio);
            File.WriteAllText(Path.Combine(project, name + ".genesisproj"), "{}");
            File.Copy(tone, Path.Combine(audio, "Tone.ogg"));
            File.WriteAllText(Path.Combine(audio, "Tone.audio.json"), "{ \"source\": \"Tone.ogg\", \"volume\": 0.5 }");
            WriteSilentWave(Path.Combine(audio, "Blip.wav"), 0.2);
            File.WriteAllText(Path.Combine(audio, "Blip.audio.json"), "{ \"source\": \"Blip.wav\" }");
            // A longer sound, which a small budget never reaches.
            File.WriteAllText(Path.Combine(audio, "Long Blip.audio.json"), "{ \"source\": \"Long Blip.wav\" }");
            WriteSilentWave(Path.Combine(audio, "Long Blip.wav"), 3.0);
            Genesis.Shared.Assets.ResourceCatalog.Invalidate(project);
            return project;
        }

        object Entry(Genesis.Audio.XAudioSystem mixer, int sound) =>
            ((System.Collections.IDictionary)typeof(Genesis.Audio.XAudioSystem)
                .GetField("_sounds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mixer)!)[sound]!;
        bool Decoded(Genesis.Audio.XAudioSystem mixer, int sound) =>
            Entry(mixer, sound) is { } entry && entry.GetType().GetField("Effect")!.GetValue(entry) != null;
        string DecodeKey(Genesis.Audio.XAudioSystem mixer, int sound) =>
            Entry(mixer, sound) is { } entry ? (string)entry.GetType().GetField("DecodeKey")!.GetValue(entry)! : string.Empty;
        bool Sounding(Genesis.Audio.XAudioSystem mixer, AudioChannel channel)
        {
            var channels = (System.Collections.IDictionary)typeof(Genesis.Audio.XAudioSystem)
                .GetField("_channels", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mixer)!;
            return channels[channel.Id] is { } state && state.GetType().GetField("Voice")!.GetValue(state) != null;
        }
        void WaitForPreloads(Genesis.Audio.XAudioSystem mixer)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (mixer.SoundsLoading > 0 && watch.Elapsed.TotalSeconds < 20) Thread.Sleep(5);
            HeadlessHarness.Assert(mixer.SoundsLoading == 0, "Preloaded sounds were still loading after 20 seconds.");
        }

        HeadlessHarness.RunCase(ctx.Report, "Engine.Audio.EveryNameOfASoundIsOneSoundAndOggIsDecodedOffTheFrame", () =>
        {
            HeadlessHarness.Assert(File.Exists(tone), "The Ogg Vorbis test tone is missing from the test output: " + tone);
            string project = NewProject("SoundNames");
            using var mixer = new Genesis.Audio.XAudioSystem(project) { Muted = true, DecodeLargeSoundsInBackground = true };
            // As GenesisCraft's scripts write them: the resource's name, its document from the
            // project or from Assets, its sound file from the project or from Assets, and the sound
            // file's own name.
            string[] names = { "Tone", "Assets/Audio/Tone.audio.json", "Audio/Tone.audio.json", "Tone.audio.json",
                "Assets/Audio/Tone.ogg", "Audio/Tone.ogg", "Tone.ogg", "Assets\\Audio\\Tone.ogg" };
            int sound = mixer.LoadSound(names[0]);
            HeadlessHarness.Assert(sound != 0, "PlaySound(\"Tone\") found no sound.");
            foreach (string name in names)
                HeadlessHarness.Assert(mixer.LoadSound(name) == sound, $"PlaySound(\"{name}\") was not the same sound as PlaySound(\"Tone\").");
            foreach (string name in new[] { "Nope.ogg", "Tone.wav", "Audio/Nope.audio.json", "../Tone.ogg" })
                HeadlessHarness.Assert(mixer.LoadSound(name) == 0, $"PlaySound(\"{name}\") found a sound where there is none.");

            // An Ogg file, however short, is decoded on a worker in a game: its first play counts
            // as playing at once and is heard when the samples are ready.
            HeadlessHarness.Assert(DecodeKey(mixer, sound).Length > 0, "A game decoded a short Ogg sound in the frame that played it.");
            AudioChannel channel = mixer.Play(sound);
            HeadlessHarness.Assert(channel.IsValid && mixer.IsPlaying(channel), "The Ogg sound's first play did not count as playing.");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!Sounding(mixer, channel) && watch.Elapsed.TotalSeconds < 20) { mixer.Update(); Thread.Sleep(2); }
            HeadlessHarness.Assert(Sounding(mixer, channel), "The Ogg sound was never heard.");
            Console.WriteLine($"[Audio] a 0.5 s Ogg sound was decoded on a worker and heard {watch.Elapsed.TotalMilliseconds:F0} ms after it was asked for.");

            // A short WAV is only copied: it still plays in the frame that asks.
            int blip = mixer.LoadSound("Blip");
            HeadlessHarness.Assert(blip != 0 && DecodeKey(mixer, blip).Length == 0 && Sounding(mixer, mixer.Play(blip)),
                "A short WAV did not play at once.");

            // An editor (the setting off) still decodes an Ogg when it is loaded.
            using var editor = new Genesis.Audio.XAudioSystem(project) { Muted = true };
            int direct = editor.LoadSound("Tone.ogg");
            HeadlessHarness.Assert(direct != 0 && Decoded(editor, direct) && Sounding(editor, editor.Play(direct)),
                "With the setting off, an Ogg sound should be decoded when it is loaded.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Audio.APreloadedSoundStartsAtOnce", () =>
        {
            string project = NewProject("SoundPreload");
            using var mixer = new Genesis.Audio.XAudioSystem(project) { Muted = true, DecodeLargeSoundsInBackground = true };
            int preloaded = mixer.PreloadSound("Assets/Audio/Tone.ogg");
            HeadlessHarness.Assert(preloaded != 0 && mixer.PreloadSound("Nope") == 0, "SoundPreload did not answer for a sound and for no sound.");
            WaitForPreloads(mixer);
            int played = mixer.LoadSound("Tone");
            AudioChannel channel = mixer.Play(played);
            HeadlessHarness.Assert(played == preloaded && Decoded(mixer, played) && Sounding(mixer, channel),
                "A preloaded sound, played under another of its names, did not start at once.");

            // The same through the script commands, and in an editor's mixer too: preloading is asked for.
            using var editor = new Genesis.Audio.XAudioSystem(project) { Muted = true };
            using var scene = new Genesis.Runtime.RuntimeScene("Sound preload");
            var game = new Genesis.Runtime.Project.ProjectGameContext(project, scene, null, null,
                Genesis.Runtime.Scene.RoomAsset.Create("Room", Genesis.Runtime.Scene.RoomDimension.TwoD), null, editor);
            var previous = Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext;
            Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext = game;
            try
            {
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.SoundPreload("Long Blip")
                    && !Genesis.Runtime.Scripting.PgslCommands.SoundPreload("No such sound")
                    && !Genesis.Runtime.Scripting.PgslCommands.SoundPreload(""),
                    "SoundPreload's answers were wrong.");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (Genesis.Runtime.Scripting.PgslCommands.SoundsLoading() > 0 && watch.Elapsed.TotalSeconds < 20) Thread.Sleep(5);
                int sound = editor.LoadSound("Long Blip");
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.SoundsLoading() == 0 && Decoded(editor, sound)
                    && DecodeKey(editor, sound).Length > 0, "SoundPreload did not decode the sound on a worker.");
            }
            finally
            {
                Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext = previous;
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Audio.TheProjectsPreloadBudgetDecodesSmallestFirst", () =>
        {
            string project = NewProject("SoundBudget");
            var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using (var mixer = new Genesis.Audio.XAudioSystem(project) { Muted = true, DecodeLargeSoundsInBackground = true })
            {
                mixer.PreloadProjectSounds(64L * 1024 * 1024, reports.Enqueue);
                WaitForPreloads(mixer);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (reports.IsEmpty && watch.Elapsed.TotalSeconds < 5) Thread.Sleep(5);
                HeadlessHarness.Assert(reports.TryPeek(out string? report) && report.Contains("3 of the project's 3", StringComparison.Ordinal),
                    "The preload budget did not report all three sounds decoded: " + string.Join(" | ", reports));
                foreach (string name in new[] { "Tone", "Blip", "Long Blip" })
                {
                    int sound = mixer.LoadSound(name);
                    HeadlessHarness.Assert(Decoded(mixer, sound) && Sounding(mixer, mixer.Play(sound)), $"'{name}' was preloaded but did not start at once.");
                }
            }

            // A budget smaller than one sound stops after the smallest file (the Ogg tone).
            reports.Clear();
            using (var mixer = new Genesis.Audio.XAudioSystem(project) { Muted = true, DecodeLargeSoundsInBackground = true })
            {
                mixer.PreloadProjectSounds(1, reports.Enqueue);
                WaitForPreloads(mixer);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (reports.IsEmpty && watch.Elapsed.TotalSeconds < 5) Thread.Sleep(5);
                HeadlessHarness.Assert(reports.TryPeek(out string? report) && report.Contains("of the project's 3", StringComparison.Ordinal)
                    && !report.Contains("3 of the project's 3", StringComparison.Ordinal),
                    "A one-byte budget should stop before decoding every sound: " + string.Join(" | ", reports));
                // Preloaded: ready the moment it is loaded. Not preloaded: a short WAV, decoded here.
                HeadlessHarness.Assert(Decoded(mixer, mixer.LoadSound("Tone")) && DecodeKey(mixer, mixer.LoadSound("Long Blip")).Length == 0,
                    "The budget did not start with the smallest file, or went on past it.");
            }

            HeadlessHarness.Assert(Genesis.Runtime.Project.ProjectPaths.ReadPreloadAudioMegabytes(project) == 0,
                "A project that does not ask for a preload budget has one.");
            File.WriteAllText(Directory.GetFiles(project, "*.genesisproj")[0], "{ \"runtime\": { \"preloadAudioMegabytes\": 48 } }");
            HeadlessHarness.Assert(Genesis.Runtime.Project.ProjectPaths.ReadPreloadAudioMegabytes(project) == 48,
                "runtime.preloadAudioMegabytes was not read from the project.");
        });
    }

    /// <summary>
    /// A long sound (music) a game plays is decoded on a worker: the frame that asks for it goes on,
    /// the play counts as playing at once and sounds when the samples are ready. A file that cannot
    /// be decoded stops counting as playing. An editor (the setting off) still decodes when it loads.
    /// </summary>
    internal static void RunBackgroundDecode(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Audio.MusicIsDecodedOffTheFrame", () =>
        {
            string project = Path.Combine(ctx.Workspace, "AudioDecode" + Guid.NewGuid().ToString("N")[..6]);
            string audio = Path.Combine(project, "Assets", "Audio");
            Directory.CreateDirectory(audio);
            string music = Path.Combine(audio, "Long Theme.wav");
            WriteSilentWave(music, 40.0);   // 1.7 MB: over the size decoded on a worker
            string broken = Path.Combine(audio, "Broken.wav");
            File.WriteAllBytes(broken, new byte[Genesis.Audio.XAudioSystem.BackgroundDecodeBytes + 4096]);
            string blip = Path.Combine(audio, "Blip.wav");
            WriteSilentWave(blip, 0.2);
            HeadlessHarness.Assert(new FileInfo(music).Length >= Genesis.Audio.XAudioSystem.BackgroundDecodeBytes, "The test's music is too short.");

            using var mixer = new Genesis.Audio.XAudioSystem(project) { Muted = true, DecodeLargeSoundsInBackground = true };
            var channels = (System.Collections.IDictionary)typeof(Genesis.Audio.XAudioSystem)
                .GetField("_channels", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mixer)!;
            bool Waiting(AudioChannel channel) => channels[channel.Id] is { } state
                && (bool)state.GetType().GetField("Pending")!.GetValue(state)!;
            bool Sounding(AudioChannel channel) => channels[channel.Id] is { } state
                && state.GetType().GetField("Voice")!.GetValue(state) != null;

            int theme = mixer.LoadSound(music);
            AudioChannel playing = mixer.Play(theme, 0.8f, 1f, loop: false);
            HeadlessHarness.Assert(theme != 0 && playing.IsValid && mixer.IsPlaying(playing),
                "A long sound asked to play while it was being decoded did not count as playing.");
            int small = mixer.LoadSound(blip);
            AudioChannel blipped = mixer.Play(small);
            HeadlessHarness.Assert(small != 0 && Sounding(blipped), "A short sound did not play at once.");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!Sounding(playing) && watch.Elapsed.TotalSeconds < 20)
            {
                mixer.Update();
                Thread.Sleep(5);
            }
            HeadlessHarness.Assert(Sounding(playing) && !Waiting(playing) && mixer.IsPlaying(playing) && mixer.SoundsDecoding == 0,
                "The long sound never started once it was decoded.");
            HeadlessHarness.Assert(mixer.LoadSound(music) == theme && Sounding(mixer.Play(theme)),
                "The decoded sound was not kept: playing it again should start at once.");
            Console.WriteLine($"[Audio] a 40 s sound was decoded on a worker and started {watch.ElapsedMilliseconds} ms after it was asked for.");

            int bad = mixer.LoadSound(broken);
            AudioChannel badPlay = mixer.Play(bad);
            watch.Restart();
            while (mixer.IsPlaying(badPlay) && watch.Elapsed.TotalSeconds < 20)
            {
                mixer.Update();
                Thread.Sleep(5);
            }
            HeadlessHarness.Assert(!mixer.IsPlaying(badPlay) && !mixer.Play(bad).IsValid,
                "A file that could not be decoded went on counting as playing, or could be played.");

            // The setting off (an editor): decoded where it is asked for, as before.
            using var editor = new Genesis.Audio.XAudioSystem(project) { Muted = true };
            int direct = editor.LoadSound(music);
            HeadlessHarness.Assert(direct != 0 && editor.SoundsDecoding == 0 && editor.Play(direct).IsValid,
                "With the setting off, a long sound should be decoded when it is loaded.");
        });
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (string directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void WriteSilentWave(string path, double seconds)
    {
        const int rate = 22050;
        int samples = (int)(rate * seconds);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        writer.Write(new byte[samples * 2]);
    }
}
