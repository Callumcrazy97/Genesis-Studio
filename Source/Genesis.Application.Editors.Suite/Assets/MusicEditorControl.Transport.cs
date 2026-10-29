using System.Diagnostics;
using Genesis.Application.Core.Audio;
using Genesis.Application.Core.Audio.Music;
using Genesis.Audio;
using Genesis.Shared.Audio;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// Play, Stop and Loop: the part to be heard is rendered through the instruments and played on the
/// game's own mixer, while a clock moves the play-head across the roll. Edits are heard the next time
/// Play is pressed.
/// </summary>
public sealed partial class MusicEditorControl
{
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 30 };
    private readonly Stopwatch _playWatch = new();
    private XAudioSystem? _audio;
    private AudioChannel _channel = AudioChannel.Invalid;
    private bool _playing, _playingLoop;
    private long _playFrom;
    private double _playSeconds;
    private long _playhead;

    /// <summary>True from Play until Stop (or the end of the music when it does not loop).</summary>
    public bool IsPlaying => _playing;

    /// <summary>True when Play repeats the loop range.</summary>
    public bool LoopEnabled { get; private set; }

    /// <summary>Where Play starts, ticks; it follows the music while playing.</summary>
    public long PlayheadTick
    {
        get => _playhead;
        set
        {
            _playhead = Math.Max(0, value);
            UpdateTimeLabel();
            _roll.Invalidate();
        }
    }

    /// <summary>How many samples the last Play rendered (0 when nothing was played): the render ran even without an audio device.</summary>
    public int LastPlayedSamples { get; private set; }

    /// <summary>False when the last Play found no audio device (a test host): the music was rendered but not heard.</summary>
    public bool LastPlayAudible { get; private set; }

    private void InitializeTransport()
    {
        _clock.Tick += (_, _) => AdvancePlayhead();
    }

    /// <summary>Turns Loop on or off; turned on with no range yet, it loops the selected notes' bars, or the whole piece.</summary>
    public void SetLoop(bool on)
    {
        if (on && _document.LoopEnd <= _document.LoopStart)
        {
            IEnumerable<MusicNote> notes = _selection.Count > 0 ? _selection : _document.Tracks.SelectMany(track => track.Notes);
            long first = notes.Select(note => note.Start).DefaultIfEmpty(0).Min(), last = notes.Select(note => note.End).DefaultIfEmpty(_document.TicksPerBar).Max();
            long bar = _document.TicksPerBar;
            long start = first / bar * bar, end = Math.Max(start + bar, (last + bar - 1) / bar * bar);
            Commit("Loop range", () => { _document.LoopStart = start; _document.LoopEnd = end; });
        }
        LoopEnabled = on && _document.LoopEnd > _document.LoopStart;
        bool wasPlaying = _playing;
        SyncFields();
        _roll.Invalidate();
        if (wasPlaying) Play();
    }

    /// <summary>
    /// Plays from the play-head to the end, or repeats the loop range when Loop is on. The music is
    /// rendered first (so Play always plays what the piece is now) and played on the game's mixer.
    /// </summary>
    public void Play()
    {
        Stop();
        bool loop = LoopEnabled && _document.LoopEnd > _document.LoopStart;
        long from = loop ? _document.LoopStart : _playhead;
        long to = loop ? _document.LoopEnd : 0;
        if (!loop && from >= _document.EndTick) from = 0;
        if (MusicRenderer.AudibleNotes(_document, from, to) == 0)
        {
            _statusLabel.Text = loop ? "No notes start inside the loop range." : "There is nothing to play from the play-head: add notes, or unmute a track.";
            return;
        }
        float[] samples;
        try { samples = MusicRenderer.Render(_document, from, to); }
        catch (ArgumentException exception) { _statusLabel.Text = exception.Message; return; }
        LastPlayedSamples = samples.Length;
        LastPlayAudible = false;
        _playFrom = from;
        _playSeconds = samples.Length / (double)SoundSynth.SampleRate;
        _playingLoop = loop;
        _playing = true;
        _playhead = from;
        try
        {
            _audio?.Dispose();
            _audio = new XAudioSystem(string.Empty);
            XAudioSystem audio = _audio;
            AudioAssetSettings settings = new() { Loop = loop, Bus = "music" };
            int sound = SynthSounds.WithWavFile(samples, "Music preview", wav => audio.LoadSound(wav, settings));
            _channel = sound == 0 ? AudioChannel.Invalid : audio.Play(sound, 1f, 1f, loop);
            LastPlayAudible = _channel.IsValid;
            _statusLabel.Text = _channel.IsValid
                ? $"Playing {_playSeconds:0.0} s" + (loop ? " · looping" : string.Empty) + " · Space or Stop to stop"
                : "The mixer refused the music (no free voice).";
        }
        catch (Exception exception) when (exception is DllNotFoundException or InvalidOperationException or SharpGen.Runtime.SharpGenException)
        {
            // No audio device (a headless test host, a remote session): the play-head still runs.
            _audio = null;
            _statusLabel.Text = "No audio output device: the music was rendered but cannot be heard here.";
        }
        _playWatch.Restart();
        _clock.Start();
        _roll.Invalidate();
    }

    public void Stop()
    {
        _clock.Stop();
        _playWatch.Stop();
        if (_audio is not null && _channel.IsValid) _audio.Stop(_channel);
        _channel = AudioChannel.Invalid;
        bool was = _playing;
        _playing = false;
        if (was) { SyncFields(); _roll.Invalidate(); }
    }

    private void AdvancePlayhead()
    {
        if (!_playing) return;
        double elapsed = _playWatch.Elapsed.TotalSeconds;
        if (_playingLoop && _playSeconds > 0) elapsed %= _playSeconds;
        else if (elapsed >= _playSeconds)
        {
            _playhead = _document.TickAt(_document.SecondsAt(_playFrom) + _playSeconds);
            Stop();
            return;
        }
        _audio?.Update();
        _playhead = _document.TickAt(_document.SecondsAt(_playFrom) + elapsed);
        _roll.FollowPlayhead();
        UpdateTimeLabel();
        _roll.Invalidate();
    }

    /// <summary>Plays one note on the selected track's instrument (the keyboard beside the roll).</summary>
    public void PreviewNote(int pitch)
    {
        if (_playing) return;
        MusicTrack track = SelectedTrack;
        MusicDocument single = new() { Tempo = 120, Tracks = [new MusicTrack
        {
            Instrument = track.Instrument, Attack = track.Attack, Release = track.Release, Filter = track.Filter, Volume = Math.Max(0.5f, track.Volume),
            Notes = [new MusicNote { Pitch = pitch, Start = 0, Length = MusicDocument.DefaultTicksPerBeat, Velocity = 100 }],
        }] };
        float[] samples = MusicRenderer.Render(single);
        try
        {
            _audio?.Dispose();
            _audio = new XAudioSystem(string.Empty);
            XAudioSystem audio = _audio;
            int sound = SynthSounds.WithWavFile(samples, "Note preview", wav => audio.LoadSound(wav, new AudioAssetSettings()));
            if (sound != 0) audio.Play(sound);
            _statusLabel.Text = $"{MusicInstruments.NoteName(pitch)} on {track.Instrument}";
        }
        catch (Exception exception) when (exception is DllNotFoundException or InvalidOperationException or SharpGen.Runtime.SharpGenException)
        {
            _audio = null;
        }
    }

    /// <summary>Run This Resource: the music plays.</summary>
    public bool RunResource()
    {
        Play();
        return true;
    }

    private void UpdateTimeLabel()
    {
        long bar = _document.TicksPerBar, beat = _document.TicksPerSignatureBeat;
        long tick = _playhead;
        string position = $"{tick / bar + 1}.{tick % bar / beat + 1}";
        double seconds = _document.SecondsAt(tick);
        _timeLabel.Text = $"{position} · {(int)seconds / 60}:{seconds % 60:00.0}";
    }

    private void DisposeTransport()
    {
        Stop();
        _clock.Dispose();
        _audio?.Dispose();
        _audio = null;
    }
}
