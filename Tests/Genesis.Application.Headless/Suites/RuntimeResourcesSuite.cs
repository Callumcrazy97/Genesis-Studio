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
