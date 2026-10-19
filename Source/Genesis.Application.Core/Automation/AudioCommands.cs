using System.ComponentModel;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Audio;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// Sounds made in Genesis: the synthesiser renders a WAV from presets, layers and melodies, and it is
/// imported as an Audio resource exactly as a dropped sound file is, ready for an Object's
/// AudioComponent or a script's sound commands.
/// </summary>
public static class AudioCommands
{
    public static IEnumerable<IAutomationCommand> All() => [new AudioSynth()];

    public sealed class MelodyInput
    {
        [Description("Notes separated by spaces: C4 E4 G4, \"-\" a rest, \"C4:2\" two beats, \"C4+E4+G4\" a chord."), RequiredInput] public string Notes { get; set; } = string.Empty;
        [Description("Seconds a beat (default 0.25).")] public double? BeatSeconds { get; set; }
        [Description("When the melody starts, seconds.")] public double? Start { get; set; }
        [Description("The voice each note is played with (wave, envelope, filter, vibrato...); its note, start and duration are ignored.")] public SynthLayer? Voice { get; set; }
    }

    public sealed class SynthInput
    {
        [Description("The sound's name."), RequiredInput] public string Name { get; set; } = string.Empty;
        [Description("Folder to make it in, project-relative (default Assets/Audio).")] public string? Folder { get; set; }
        [Description("A ready-made sound to start from (see the command's description).")] public string? Preset { get; set; }
        [Description("Voices to add: each an oscillator with its pitch, timing, envelope and filter.")] public List<SynthLayer>? Layers { get; set; }
        [Description("Tunes to add, as note strings.")] public List<MelodyInput>? Melodies { get; set; }
        [Description("Echo delay, seconds (with echoFeedback).")] public double? EchoDelay { get; set; }
        [Description("How much each echo repeats, 0 to 0.9.")] public double? EchoFeedback { get; set; }
        [Description("The Audio resource loops (music, ambience).")] public bool? Loop { get; set; }
        [Description("The Audio resource's playback volume, 0 to 1.")] public double? Volume { get; set; }
        [Description("Seed for noise, so the same input makes the same sound (default 1).")] public int? Seed { get; set; }
    }

    public sealed class AudioSynth : AutomationCommand<SynthInput>
    {
        public override string Name => "audio.synth";
        public override string Description => "Makes a sound with Genesis's synthesiser and imports it as an Audio resource: start from a preset ("
            + string.Join(", ", SoundSynth.Presets.Keys) + ") and/or add layers (oscillators with pitch glides, envelopes, vibrato, unison, low-pass) and melodies (note strings), with an echo.";

        protected override AutomationResult Execute(AutomationContext context, SynthInput input)
        {
            var layers = new List<SynthLayer>();
            if (!string.IsNullOrWhiteSpace(input.Preset))
            {
                if (!SoundSynth.Presets.TryGetValue(input.Preset.Trim(), out var preset))
                    throw new AutomationException($"'{input.Preset}' is not a preset. Presets: {string.Join(", ", SoundSynth.Presets.Keys)}.");
                layers.AddRange(preset());
            }
            layers.AddRange(input.Layers ?? []);
            try
            {
                foreach (MelodyInput melody in input.Melodies ?? [])
                {
                    double beat = melody.BeatSeconds ?? 0.25;
                    if (!(beat > 0)) throw new AutomationException("A melody's beat must be above 0 seconds.");
                    layers.AddRange(SoundSynth.Melody(melody.Notes, beat, melody.Start ?? 0, melody.Voice ?? new SynthLayer()));
                }
                if (layers.Count == 0) throw new AutomationException("Give a preset, layers or melodies to make a sound from.");
                if (layers.Count > 512) throw new AutomationException("At most 512 layers and notes.");

                float[] samples = SoundSynth.Render(layers, input.EchoDelay ?? 0, input.EchoFeedback ?? 0, input.Seed ?? 1);
                string stem = string.Concat(input.Name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                if (stem.Length == 0) throw new AutomationException("The sound needs a name.");
                string scratch = Path.Combine(Path.GetTempPath(), "genesis-synth-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratch);
                try
                {
                    string wav = Path.Combine(scratch, stem + ".wav");
                    SoundSynth.WriteWav(wav, samples);
                    string resource = context.Resources.ImportFiles(context.Resolve(input.Folder ?? "Assets/Audio"), [wav]).Single();
                    if (input.Loop is not null || input.Volume is not null)
                    {
                        JObject document = JObject.Parse(File.ReadAllText(resource));
                        if (input.Loop is bool loop) document["loop"] = loop;
                        if (input.Volume is double volume) document["volume"] = Math.Clamp(volume, 0, 1);
                        File.WriteAllText(resource, document.ToString(Newtonsoft.Json.Formatting.Indented));
                    }
                    double seconds = samples.Length / (double)SoundSynth.SampleRate;
                    return AutomationResult.Success($"Made {Path.GetFileName(resource)}: {seconds:0.##} s from {layers.Count} layer(s).",
                        new JsonObject
                        {
                            ["path"] = context.Relative(resource),
                            ["name"] = ResourceNames.Name(context.Root, resource),
                            ["seconds"] = Math.Round(seconds, 3),
                            ["sampleRate"] = SoundSynth.SampleRate,
                            ["layers"] = layers.Count,
                        }, context.Relative(resource));
                }
                finally
                {
                    try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
                }
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                throw new AutomationException(exception.Message);
            }
        }
    }
}
