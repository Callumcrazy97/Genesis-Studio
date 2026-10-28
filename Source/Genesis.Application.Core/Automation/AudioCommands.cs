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
            try
            {
                // The Audio editor's "Make a sound" renders through the same SynthSounds, so a recipe
                // is the same sound from either.
                SynthSound sound = SynthSounds.Render(new SynthRecipe
                {
                    Preset = input.Preset,
                    Layers = input.Layers ?? [],
                    Melodies = (input.Melodies ?? []).Select(melody => new SynthMelody(melody.Notes, melody.BeatSeconds ?? 0.25, melody.Start ?? 0, melody.Voice)).ToList(),
                    EchoDelay = input.EchoDelay ?? 0,
                    EchoFeedback = input.EchoFeedback ?? 0,
                    Seed = input.Seed ?? 1,
                });
                string stem = SynthSounds.FileStem(input.Name);
                if (stem.Length == 0) throw new AutomationException("The sound needs a name.");
                return SynthSounds.WithWavFile(sound.Samples, stem, wav =>
                {
                    string resource = context.Resources.ImportFiles(context.Resolve(input.Folder ?? "Assets/Audio"), [wav]).Single();
                    if (input.Loop is not null || input.Volume is not null)
                    {
                        JObject document = JObject.Parse(File.ReadAllText(resource));
                        if (input.Loop is bool loop) document["loop"] = loop;
                        if (input.Volume is double volume) document["volume"] = Math.Clamp(volume, 0, 1);
                        File.WriteAllText(resource, document.ToString(Newtonsoft.Json.Formatting.Indented));
                    }
                    double seconds = sound.Seconds;
                    return AutomationResult.Success($"Made {Path.GetFileName(resource)}: {seconds:0.##} s from {sound.LayerCount} layer(s).",
                        new JsonObject
                        {
                            ["path"] = context.Relative(resource),
                            ["name"] = ResourceNames.Name(context.Root, resource),
                            ["seconds"] = Math.Round(seconds, 3),
                            ["sampleRate"] = SoundSynth.SampleRate,
                            ["layers"] = sound.LayerCount,
                        }, context.Relative(resource));
                });
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                throw new AutomationException(exception.Message);
            }
        }
    }
}
