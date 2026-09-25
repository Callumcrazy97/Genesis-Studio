#nullable enable
using System;
using System.IO;
using System.Text.Json;

namespace Genesis.Shared.Audio
{
    // ════════════════════════════════════════════════════════════════════════════
    //   AudioAssetSettings — the single parser for a .audio.json document.
    //
    //   Both the Audio Editor (which writes the file and auditions through it) and
    //   XAudioSystem (which honours it at play time) go through this type, so the
    //   editor's knobs and the shipped game cannot drift apart. They previously did:
    //   the editor wrote "Volume"/"Loop"/"Spatial" while the runtime looked for
    //   "gain"/"loop"/"spatial", and JsonElement.TryGetProperty is case-sensitive,
    //   so every setting in the editor was silently discarded at runtime.
    //
    //   Two on-disk shapes are accepted:
    //     • Resource document  — Assets/Audio/Jump.audio.json with a "Source" field
    //                            pointing at the WAV. This is what the editor saves.
    //     • Sidecar            — Jump.wav + Jump.audio.json alongside it, no Source.
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Playback settings for one audio asset, as authored in the Audio Editor.
    /// </summary>
    public sealed class AudioAssetSettings
    {
        /// <summary>Project-relative path of the WAV this document describes (resource form only).</summary>
        public string? Source { get; set; }

        /// <summary>Playback gain, 0–1. Multiplied with the per-call volume and the bus gain.</summary>
        public float Volume { get; set; } = 1f;

        /// <summary>Playback rate multiplier. 1.0 = unmodified.</summary>
        public float Pitch { get; set; } = 1f;

        /// <summary>Loop by default, even when the caller does not ask for it.</summary>
        public bool Loop { get; set; }

        /// <summary>Attenuate by distance from the listener.</summary>
        public bool Spatial { get; set; }

        /// <summary>Audio group this sound belongs to for memory management and telemetry.</summary>
        public string AudioGroup { get; set; } = "Default Audio Group";

        /// <summary>Mixer bus: "sfx", "music" or "master".</summary>
        public string Bus { get; set; } = "sfx";

        /// <summary>Optional adaptive soundscape role: None, Wind, Rain, Water, Fire, Wildlife or Night.</summary>
        public string EnvironmentRole { get; set; } = "None";

        /// <summary>Distance (world units) within which a spatial sound plays at full volume.</summary>
        public float MinDistance { get; set; } = 1f;

        /// <summary>Distance at which a spatial sound reaches silence.</summary>
        public float MaxDistance { get; set; } = 48f;

        /// <summary>Curve shaping between min and max distance. 1 = linear, &gt;1 = quieter sooner.</summary>
        public float Falloff { get; set; } = 1f;

        /// <summary>
        /// Attenuation multiplier for a spatial source at <paramref name="distance"/> from the
        /// listener. Returns 1 for non-spatial sounds so callers can apply it unconditionally.
        /// </summary>
        public float AttenuationAt(float distance)
        {
            float near = MathF.Max(0.001f, MinDistance);
            float far = MathF.Max(near + 0.001f, MaxDistance);
            if (distance <= near) return 1f;
            if (distance >= far) return 0f;

            float t = (distance - near) / (far - near);         // 0 at the inner radius, 1 at the outer
            return MathF.Pow(1f - t, MathF.Max(0.01f, Falloff));
        }

        /// <summary>
        /// Parse a .audio.json document. Accepts the editor's PascalCase names and the
        /// legacy lowercase sidecar names ("gain"/"loop"/"spatial"/"bus") interchangeably.
        /// Returns null when the text is not usable JSON.
        /// </summary>
        public static AudioAssetSettings? Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                var settings = new AudioAssetSettings();
                settings.Source = ReadString(root, "Source") ?? ReadString(root, "source");
                settings.AudioGroup = ReadString(root, "AudioGroup") ?? ReadString(root, "audioGroup") ?? ReadString(root, "group") ?? settings.AudioGroup;
                settings.Volume = ReadFloat(root, "Volume", "gain", "volume") ?? settings.Volume;
                settings.Pitch = ReadFloat(root, "Pitch", "pitch") ?? settings.Pitch;
                settings.Loop = ReadBool(root, "Loop", "loop") ?? settings.Loop;
                settings.Spatial = ReadBool(root, "Spatial", "spatial") ?? settings.Spatial;
                settings.Bus = ReadString(root, "Bus") ?? ReadString(root, "bus") ?? settings.Bus;
                settings.EnvironmentRole = ReadString(root, "EnvironmentRole") ?? ReadString(root, "environmentRole") ?? settings.EnvironmentRole;
                settings.MinDistance = ReadFloat(root, "MinDistance", "minDistance") ?? settings.MinDistance;
                settings.MaxDistance = ReadFloat(root, "MaxDistance", "maxDistance") ?? settings.MaxDistance;
                settings.Falloff = ReadFloat(root, "Falloff", "falloff") ?? settings.Falloff;
                return settings;
            }
            catch (JsonException)
            {
                // A malformed sidecar must not take the sound down with it — the caller
                // falls back to engine defaults and the asset still plays.
                return null;
            }
        }

        /// <summary>Parse the document at <paramref name="path"/>, or null if it is missing/unreadable.</summary>
        public static AudioAssetSettings? Load(string path)
        {
            try
            {
                return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string? ReadString(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static bool? ReadBool(JsonElement root, params string[] names)
        {
            foreach (string name in names)
            {
                if (root.TryGetProperty(name, out JsonElement value) &&
                    value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return value.GetBoolean();
                }
            }

            return null;
        }

        private static float? ReadFloat(JsonElement root, params string[] names)
        {
            foreach (string name in names)
            {
                if (root.TryGetProperty(name, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.Number &&
                    value.TryGetSingle(out float number))
                {
                    return number;
                }
            }

            return null;
        }
    }
}
