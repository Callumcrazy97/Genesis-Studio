using System.Text;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Resources;
using Genesis.Shared.Assets;

namespace Genesis.Application.Core.Audio.Music;

/// <summary>What Render to Audio did: the Audio resource it wrote (made if it was new) and its WAV.</summary>
public sealed record MusicRenderOutcome(string AudioPath, string AudioName, string WavPath, double Seconds, bool Created);

/// <summary>
/// Plays a piece through its instruments: every note of every audible track becomes the track's
/// synthesiser preset at the note's key, length and velocity, mixed at <see cref="SoundSynth.SampleRate"/>.
/// The Music mode's Play renders the part it plays; Render to Audio renders the whole piece into an
/// Audio resource's WAV, which a game plays with PlaySound.
/// </summary>
public static class MusicRenderer
{
    public const double MaximumSeconds = 600;
    // Each note comes out of the synthesiser at a peak of 0.9; this leaves room for a few at once.
    private const float NoteGain = 0.35f;
    // A mix louder than this is turned down as a whole, so nothing clips; a quieter one is left alone.
    private const float Ceiling = 0.95f;
    private const double HighestFrequency = 19000;
    private const int Batch = 64;

    /// <summary>Which tracks are heard: the soloed ones when any track is soloed, otherwise every track not muted.</summary>
    public static bool IsAudible(MusicDocument document, MusicTrack track) =>
        document.Tracks.Any(other => other.Solo) ? track.Solo : !track.Mute;

    /// <summary>The number of notes a render from <paramref name="startTick"/> would play.</summary>
    public static int AudibleNotes(MusicDocument document, long startTick = 0, long endTick = 0) =>
        document.Tracks.Where(track => IsAudible(document, track))
            .Sum(track => track.Notes.Count(note => note.Start >= startTick && (endTick <= startTick || note.Start < endTick)));

    /// <summary>
    /// Mono samples from -1 to 1 for the notes that start from <paramref name="startTick"/>. With an
    /// <paramref name="endTick"/> after the start, notes start before it and the sound is exactly that
    /// long (what Loop repeats); without one it runs on until the last note has faded.
    /// </summary>
    public static float[] Render(MusicDocument document, long startTick = 0, long endTick = 0)
    {
        ArgumentNullException.ThrowIfNull(document);
        MusicDocument piece = document.Copy();
        piece.Normalize();
        startTick = Math.Max(0, startTick);
        bool range = endTick > startTick;
        double origin = piece.SecondsAt(startTick);
        int sampleRate = SoundSynth.SampleRate;

        var jobs = new List<(int Offset, List<SynthLayer> Layers, float Gain, int Seed, int Length)>();
        for (int trackIndex = 0; trackIndex < piece.Tracks.Count; trackIndex++)
        {
            MusicTrack track = piece.Tracks[trackIndex];
            if (!IsAudible(piece, track) || track.Volume <= 0) continue;
            for (int noteIndex = 0; noteIndex < track.Notes.Count; noteIndex++)
            {
                MusicNote note = track.Notes[noteIndex];
                if (note.Start < startTick || (range && note.Start >= endTick)) continue;
                double begins = piece.SecondsAt(note.Start);
                List<SynthLayer> layers = NoteLayers(track, note, piece.SecondsAt(note.End) - begins);
                int length = Math.Max(1, (int)Math.Ceiling(layers.Max(layer => layer.Start + layer.Duration) * sampleRate));
                jobs.Add(((int)Math.Round((begins - origin) * sampleRate), layers, NoteGain * track.Volume * note.Velocity / 127f,
                    1 + trackIndex * 7919 + noteIndex * 31, length));
            }
        }

        double seconds = range
            ? piece.SecondsAt(endTick) - origin
            : jobs.Select(job => (job.Offset + (double)job.Length) / sampleRate).DefaultIfEmpty(0).Max();
        if (seconds > MaximumSeconds) throw new ArgumentException($"The music would last {seconds:0} s; the most is {MaximumSeconds:0} s.");
        var mix = new float[Math.Max(1, (int)Math.Ceiling(seconds * sampleRate))];

        // Notes are made side by side a batch at a time and added in order, so the same piece always
        // makes the same samples.
        for (int first = 0; first < jobs.Count; first += Batch)
        {
            int count = Math.Min(Batch, jobs.Count - first);
            var made = new float[count][];
            Parallel.For(0, count, index =>
            {
                var job = jobs[first + index];
                made[index] = SoundSynth.Render(job.Layers, seed: job.Seed);
            });
            for (int index = 0; index < count; index++)
            {
                var job = jobs[first + index];
                float[] samples = made[index];
                int end = Math.Min(samples.Length, mix.Length - job.Offset);
                for (int sample = Math.Max(0, -job.Offset); sample < end; sample++) mix[job.Offset + sample] += samples[sample] * job.Gain;
            }
        }

        float peak = 0;
        foreach (float sample in mix) peak = Math.Max(peak, Math.Abs(sample));
        if (peak > Ceiling)
        {
            float gain = Ceiling / peak;
            for (int index = 0; index < mix.Length; index++) mix[index] *= gain;
        }
        return mix;
    }

    /// <summary>
    /// The synthesiser layers one note plays: the instrument's preset moved to the note's key (a drum
    /// kit picks the drum for the key instead), with the track's attack, release and filter, held for the
    /// note's length when the instrument is held. An instrument the synthesiser does not know plays as Piano.
    /// </summary>
    public static List<SynthLayer> NoteLayers(MusicTrack track, MusicNote note, double seconds)
    {
        MusicInstrumentKind kind = MusicInstruments.Kind(track.Instrument);
        string preset;
        double ratio = 1;
        if (kind == MusicInstrumentKind.Drums) (preset, ratio) = MusicInstruments.DrumPiece(note.Pitch);
        else preset = MusicInstruments.Exists(track.Instrument) ? track.Instrument.Trim() : "Piano";
        IReadOnlyList<SynthLayer> made = SoundSynth.Presets[preset]();
        if (kind != MusicInstrumentKind.Drums)
        {
            double reference = made.Select(LayerFrequency).FirstOrDefault(frequency => frequency > 0);
            if (reference > 0) ratio = MusicInstruments.Frequency(note.Pitch) / reference;
        }
        double held = Math.Clamp(seconds, 0.01, 100);
        var layers = new List<SynthLayer>(made.Count);
        foreach (SynthLayer layer in made)
        {
            SynthLayer copy = layer.Copy();
            copy.Note = null;
            copy.Frequency = Math.Clamp(LayerFrequency(layer) * ratio, 1, HighestFrequency);
            if (layer.FrequencyEnd > 0) copy.FrequencyEnd = Math.Clamp(layer.FrequencyEnd * ratio, 1, HighestFrequency);
            // The filter follows the key half way, so high notes stay bright and low ones are not muddy.
            if (track.Filter is double cutoff) copy.LowPass = cutoff;
            else if (layer.LowPass > 0) copy.LowPass = Math.Clamp(layer.LowPass * Math.Sqrt(ratio), 20, HighestFrequency);
            if (track.Attack is double attack) copy.Attack = attack;
            if (track.Release is double release) copy.Release = release;
            if (kind == MusicInstrumentKind.Sustained) copy.Duration = held + Math.Max(0.0005, copy.Release);
            layers.Add(copy);
        }
        return layers;
    }

    private static double LayerFrequency(SynthLayer layer) =>
        string.IsNullOrWhiteSpace(layer.Note) ? layer.Frequency : SoundSynth.NoteFrequency(layer.Note);

    /// <summary>
    /// Renders the whole piece into its Audio resource (<see cref="MusicDocument.Audio"/>): the WAV beside
    /// it is replaced and its source points at it, so the next PlaySound of that name plays the new
    /// render. When the piece has no Audio resource yet, one is made in the Audio folder, named after the
    /// piece, set to the music bus and to repeat; its name is put in <see cref="MusicDocument.Audio"/>
    /// (the caller saves the piece). <paramref name="beforeWrite"/> is told of each file before it is written.
    /// </summary>
    public static MusicRenderOutcome RenderToAudio(ResourceService resources, string musicPath, MusicDocument document, Action<string>? beforeWrite = null)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(document);
        if (AudibleNotes(document) == 0) throw new InvalidOperationException("There is nothing to hear: add notes to a track that is not muted, then render.");
        float[] samples = Render(document);
        string root = resources.Project.RootPath;

        string audioPath = string.IsNullOrWhiteSpace(document.Audio) ? string.Empty : ResourceNames.Resolve(root, document.Audio, ResourceType.Audio);
        bool created = audioPath.Length == 0 || !File.Exists(audioPath);
        if (created)
        {
            string requested = string.IsNullOrWhiteSpace(document.Audio) ? ResourceNames.Name(root, musicPath, ResourceType.Music) + " Audio" : document.Audio.Trim();
            string name = ResourceNames.For(root).UniqueName(requested);
            audioPath = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.Audio), ResourceKind.Audio, name);
            ResourceNames.Invalidate(root);
        }
        string audioName = ResourceNames.Name(root, audioPath, ResourceType.Audio);

        JsonObject audio;
        try { audio = JsonNode.Parse(File.ReadAllText(audioPath)) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { audio = new JsonObject(); }
        string folder = Path.GetDirectoryName(audioPath)!;
        string stem = Path.GetFileName(audioPath)[..^ResourceDefinitions.Get(ResourceKind.Audio).Extension.Length];
        string wavPath = Path.Combine(folder, stem + ".wav");
        string? current = audio["source"]?.GetValue<string>() ?? audio["Source"]?.GetValue<string>();
        string? currentPath = string.IsNullOrWhiteSpace(current) ? null : Path.GetFullPath(Path.Combine(root, current.Replace('/', Path.DirectorySeparatorChar)));
        // A WAV of that name that this resource does not play is someone else's: it is left alone.
        for (int index = 2; File.Exists(wavPath) && !string.Equals(currentPath, wavPath, StringComparison.OrdinalIgnoreCase); index++)
            wavPath = Path.Combine(folder, $"{stem} ({index}).wav");

        beforeWrite?.Invoke(wavPath);
        SoundSynth.WriteWav(wavPath, samples);
        audio.Remove("Source");
        audio["source"] = Path.GetRelativePath(root, wavPath).Replace('\\', '/');
        if (created)
        {
            audio["schemaVersion"] = 4;
            audio["bus"] = "music";
            audio["loop"] = true;
            audio["volume"] = 0.8;
        }
        beforeWrite?.Invoke(audioPath);
        File.WriteAllText(audioPath, audio.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        RuntimeAssetPolicy.Invalidate();
        document.Audio = audioName;
        return new MusicRenderOutcome(audioPath, audioName, wavPath, samples.Length / (double)SoundSynth.SampleRate, created);
    }
}
