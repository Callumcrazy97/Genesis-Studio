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
