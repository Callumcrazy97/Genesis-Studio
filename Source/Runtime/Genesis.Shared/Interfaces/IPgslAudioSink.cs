namespace Genesis.Shared.Interfaces
{
    /// <summary>
    /// Runtime audio playback sink used by PGSL audio commands (AudioPlay/AudioStop/…).
    /// The host owns the actual audio backend and registers an implementation on the engine,
    /// mirroring <see cref="IPgslDrawSurface"/> for graphics. Keeping this as an interface in
    /// Genesis.Shared lets Genesis.Runtime drive audio without referencing Genesis.Audio.
    /// </summary>
    public interface IPgslAudioSink
    {
        /// <summary>Play a sound by resource name. Returns a voice id (>= 0) or -1 on failure.</summary>
        int Play(string soundName, float volume = 1f, bool loop = false);

        /// <summary>Stop a previously started voice (best-effort).</summary>
        void Stop(int voiceId);

        /// <summary>Set the master output volume (0..1).</summary>
        void SetMasterVolume(float volume);
    }
}
