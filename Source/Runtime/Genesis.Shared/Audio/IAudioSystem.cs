using System;
using System.Numerics;

namespace Genesis.Shared.Audio
{
    // ════════════════════════════════════════════════════════════════════════════
    //   IAudioSystem — backend-agnostic runtime audio service.
    //
    //   Exposed to gameplay via IGameContext.Audio and the Engine.Audio.* commands.
    //   The interface lives in Genesis.Shared (the leaf library) so it carries no
    //   XAudio2/NAudio types; the concrete XAudio2 implementation lives in
    //   Genesis.Audio and is constructed by the runtime host.
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A handle to a currently-playing sound instance, returned by
    /// <see cref="IAudioSystem.Play"/>. Use it with <see cref="IAudioSystem.Stop"/>
    /// or to poll <see cref="IAudioSystem.IsPlaying"/>.
    /// </summary>
    public readonly struct AudioChannel
    {
        public readonly int Id;
        public AudioChannel(int id) { Id = id; }
        public static readonly AudioChannel Invalid = new(0);
        public bool IsValid => Id != 0;
    }

    /// <summary>
    /// Backend-agnostic audio service. The runtime constructs the XAudio2-backed
    /// implementation and exposes it through <c>IGameContext.Audio</c>; editor
    /// sandboxes use the same interface (with a scoped/null implementation) so
    /// behaviours that play audio run identically in both hosts.
    /// </summary>
    public interface IAudioSystem
    {
        /// <summary>Master volume multiplier (0..1).</summary>
        float MasterVolume { get; set; }

        /// <summary>
        /// Load (or fetch a cached) sound effect from a project-relative path.
        /// Returns a stable handle usable across calls.
        /// </summary>
        int LoadSound(string projectRelativePath);

        /// <summary>
        /// Play a previously-loaded sound. Returns a channel handle for control.
        /// </summary>
        /// <param name="soundId">Value from <see cref="LoadSound"/>.</param>
        /// <param name="volume">0..1 per-playback volume.</param>
        /// <param name="pitch">frequency ratio (1 = original).</param>
        /// <param name="loop">loop the buffer until <see cref="Stop"/> is called.</param>
        AudioChannel Play(int soundId, float volume = 1f, float pitch = 1f, bool loop = false);

        /// <summary>Stop a playing channel. Safe to call with an invalid/expired channel.</summary>
        void Stop(AudioChannel channel);

        /// <summary>Stop every currently-playing channel.</summary>
        void StopAll();

        /// <summary>True if the channel is still producing sound.</summary>
        bool IsPlaying(AudioChannel channel);

        /// <summary>Adjust a live channel without restarting it. Used for continuous environment beds.</summary>
        void SetChannelVolume(AudioChannel channel, float volume);

        /// <summary>Move a live spatial channel in world space.</summary>
        void SetChannelPosition(AudioChannel channel, Vector3 position);

        /// <summary>Override spatial mode and distance falloff for this emitter's live channel.</summary>
        void SetChannelSpatialSettings(AudioChannel channel, bool spatial, AudioSpatialSettings settings) { }

        /// <summary>
        /// Set the spatial listener (camera/ear) for 3D positional audio. For 2D
        /// games, call once with the room centre and forward = -Z.
        /// </summary>
        void SetListener(Vector3 position, Vector3 forward);

        /// <summary>
        /// Sets the listener with the direction of its right ear, so positioned sounds are heard
        /// more in the ear they are nearer to. A system that does not pan uses the position alone.
        /// </summary>
        void SetListener(Vector3 position, Vector3 forward, Vector3 right) => SetListener(position, forward);

        /// <summary>Plays a sound at a place in the world: quieter with distance, and to one side.</summary>
        AudioChannel PlayAt(int soundId, Vector3 position, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            AudioChannel channel = Play(soundId, volume, pitch, loop);
            if (channel.IsValid) SetChannelPosition(channel, position);
            return channel;
        }

        /// <summary>Changes the pitch of a live channel (1 is as recorded).</summary>
        void SetChannelPitch(AudioChannel channel, float pitch) { }

        /// <summary>
        /// Moves a live channel's volume to a new level over a time, and optionally stops it on
        /// arrival. A system with no clock makes the change at once.
        /// </summary>
        void FadeChannel(AudioChannel channel, float volume, float seconds, bool stopWhenDone = false)
        {
            SetChannelVolume(channel, volume);
            if (stopWhenDone) Stop(channel);
        }

        /// <summary>Volume of a group of sounds: "music", "sfx" or "master".</summary>
        void SetBusVolume(string bus, float volume) { }

        /// <summary>The volume a group of sounds was last set to; 1 when it never was.</summary>
        float GetBusVolume(string bus) => 1f;

        /// <summary>Advance voice recycling / 3D panning. Called once per frame by the host.</summary>
        void Update();
    }

    /// <summary>
    /// No-op implementation used by editor sandboxes / tests when no real audio
    /// device is attached. Lets <c>Engine.Audio.*</c> calls run without crashing.
    /// </summary>
    public sealed class NullAudioSystem : IAudioSystem
    {
        public static readonly NullAudioSystem Instance = new NullAudioSystem();
        private NullAudioSystem() { }

        public float MasterVolume { get; set; } = 1f;
        public int LoadSound(string projectRelativePath) => 1;
        public AudioChannel Play(int soundId, float volume = 1f, float pitch = 1f, bool loop = false) => new AudioChannel(1);
        public void Stop(AudioChannel channel) { }
        public void StopAll() { }
        public bool IsPlaying(AudioChannel channel) => false;
        public void SetChannelVolume(AudioChannel channel, float volume) { }
        public void SetChannelPosition(AudioChannel channel, Vector3 position) { }
        public void SetListener(Vector3 position, Vector3 forward) { }
        public void Update() { }
    }
}
