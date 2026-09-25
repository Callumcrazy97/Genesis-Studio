using System;
using System.Collections.Generic;

namespace Genesis.Audio
{
    public sealed class AudioGroupState
    {
        public string Name { get; set; } = "Default Audio Group";
        public float Volume { get; set; } = 1.0f;
        public bool IsLoaded { get; set; } = true;
        public int SoundCount { get; set; } = 0;
        public int ActiveVoices { get; set; } = 0;
        public long MemoryBytes { get; set; } = 0;
    }

    /// <summary>
    /// Manages project Audio Groups, playback volumes, memory tracking, and telemetry for Genesis Engine.
    /// </summary>
    public sealed class AudioGroupManager
    {
        private static readonly AudioGroupManager _instance = new();
        public static AudioGroupManager Instance => _instance;

        private readonly Dictionary<string, AudioGroupState> _groups = new(StringComparer.OrdinalIgnoreCase);

        public AudioGroupManager()
        {
            // Seed Default Audio Group
            GetOrCreateGroup("Default Audio Group");
        }

        public IReadOnlyDictionary<string, AudioGroupState> Groups => _groups;

        public AudioGroupState GetOrCreateGroup(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                name = "Default Audio Group";

            if (!_groups.TryGetValue(name, out var group))
            {
                group = new AudioGroupState { Name = name };
                _groups[name] = group;
            }
            return group;
        }

        public void RegisterSound(string groupName, long memoryBytes)
        {
            var group = GetOrCreateGroup(groupName);
            group.SoundCount++;
            group.MemoryBytes += memoryBytes;
        }

        public void SetGroupVolume(string groupName, float volume)
        {
            var group = GetOrCreateGroup(groupName);
            group.Volume = Math.Clamp(volume, 0f, 1f);
        }

        public float GetGroupVolume(string groupName)
        {
            return GetOrCreateGroup(groupName).Volume;
        }

        public void RecordVoicePlayback(string groupName)
        {
            var group = GetOrCreateGroup(groupName);
            group.ActiveVoices++;
        }

        public void ResetFrameVoices()
        {
            foreach (var group in _groups.Values)
            {
                group.ActiveVoices = 0;
            }
        }
    }
}
