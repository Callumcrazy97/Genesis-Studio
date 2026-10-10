using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Genesis.Shared.Audio;
using Vortice.XAudio2;

namespace Genesis.Audio
{
    /// <summary>
    /// XAudio2-backed <see cref="IAudioSystem"/> with real loop/stop, listener attenuation,
    /// optional .audio.json meta, and simple master/SFX/music buses.
    /// </summary>
    public sealed class XAudioSystem : IAudioSystem, IDisposable
    {
        private readonly AudioEngine _engine;
        private readonly string _projectPath;
        private readonly Dictionary<string, int> _pathToId = new(StringComparer.OrdinalIgnoreCase);
        // Each sound by its source (see SoundSource.Key), so "snd_x", "Assets/Audio/snd_x.ogg" and
        // "Assets/Audio/snd_x.audio.json" are one sound, decoded once.
        private readonly Dictionary<string, int> _sourceToId = new(StringComparer.OrdinalIgnoreCase);
        private SoundDecoder? _decoder;
        private readonly Dictionary<int, SoundEntry> _sounds = new();
        private readonly Dictionary<int, (string Path, long Stamp, long Length)[]> _soundVersions = new();
        private readonly Dictionary<int, ChannelState> _channels = new();
        private int _nextSoundId = 1;
        private int _nextChannelId = 1;

        private Vector3 _listenerPos;
        private Vector3 _listenerForward = -Vector3.UnitZ;
        private Vector3 _listenerRight = Vector3.UnitX;
        private int _outputChannels = -1;
        private float[] _panMatrix = new float[16];
        private float _busMaster = 1f;
        // Every other bus by name: "sfx" and "music" always exist, and a project may use its own
        // ("ui", "ambient", "voice"), each with its own volume. A bus nothing has set plays at 1.
        private readonly Dictionary<string, float> _buses = new(StringComparer.OrdinalIgnoreCase)
        {
            ["sfx"] = 1f,
            ["music"] = 1f,
        };

        private static string BusName(string bus)
        {
            string name = (bus ?? string.Empty).Trim().ToLowerInvariant();
            return name.Length == 0 ? "sfx" : name;
        }

        private sealed class SoundEntry
        {
            public SoundEffect Effect = null!;
            public float Gain = 1f;
            public float Pitch = 1f;
            public bool Loop;
            public bool Spatial;
            public string Bus = "sfx";
            /// <summary>Authored falloff shape; also the source of Gain/Loop/Spatial/Bus above.</summary>
            public AudioAssetSettings Settings = new();
            /// <summary>The samples being decoded on a worker; null once <see cref="Effect"/> is set (or failed).</summary>
            public System.Threading.Tasks.Task<SoundEffect?>? Decoding;
            /// <summary>The group follows the length (music over ten seconds), known once decoded.</summary>
            public bool BusFromLength;
            public bool Failed;
            /// <summary>The source the decoder keeps its samples by, when the decoder made them.</summary>
            public string DecodeKey = string.Empty;
        }

        /// <summary>
        /// Where a sound comes from: the file decoded, the settings it plays with (its resource or
        /// the sidecar beside its file), the file a script named, and the key its samples are kept
        /// by: the file, its size and time, and the region played.
        /// </summary>
        internal readonly record struct SoundSource(string Key, string File, AudioAssetSettings? Settings, string ResourcePath, long Length);

        private sealed class ChannelState
        {
            public int Id;
            public int SoundId;
            public bool Loop;
            public bool Spatial;
            public Vector3 Position;
            public float BaseVolume = 1f;
            public string Bus = "sfx";
            public AudioAssetSettings Settings = new();
            public IXAudio2SourceVoice? Voice;
            public AudioSpatialSettings? SpatialOverride;
            public SoundEffect Effect = null!;
            /// <summary>The volume asked for, before the sound's own level, its bus and distance.</summary>
            public float UserVolume = 1f;
            /// <summary>The sound's own authored pitch; a script's pitch multiplies it.</summary>
            public float AssetPitch = 1f;
            public bool Fading, StopAfterFade;
            public float FadeFrom, FadeTo, FadeSeconds;
            public long FadeStarted;
            /// <summary>The levels last given to the left and right outputs; 1 and 1 until it is panned.</summary>
            public float PanLeft = 1f, PanRight = 1f;
            /// <summary>Asked to play while its sound was still being decoded: it starts when the sound is ready.</summary>
            public bool Pending;
            public float PendingPitch = 1f;
            public bool BusChosen;
        }

        /// <summary>
        /// True decodes every Ogg Vorbis sound, and any other sound file of
        /// <see cref="BackgroundDecodeBytes"/> or more (music, long ambience), on a worker thread:
        /// <see cref="LoadSound(string)"/> returns at once, and a play of it starts as soon as it is
        /// decoded, a moment later, while the game goes on. Decoded in the frame that asked for
        /// it, a piece of music held that frame for 150 to 730 ms, and a 650 KB ambience loop
        /// (27 s of compressed sound, under the size limit) 420 ms. A WAV under the limit is only
        /// copied and stays immediate. Off, every sound is decoded when it is loaded, which an
        /// editor auditioning a sound relies on. A game turns it on.
        /// </summary>
        public bool DecodeLargeSoundsInBackground { get; set; }

        /// <summary>The file size from which <see cref="DecodeLargeSoundsInBackground"/> decodes any sound on a worker.</summary>
        public const long BackgroundDecodeBytes = 1L << 20;

        private SoundDecoder Decoder => _decoder ??= new SoundDecoder();

        /// <summary>
        /// Sounds asked for ahead of time (<see cref="PreloadSound"/>, <see cref="PreloadProjectSounds"/>)
        /// that are not decoded yet. A room's loading cover waits for them.
        /// </summary>
        public int SoundsLoading => _decoder?.PreloadsPending ?? 0;

        /// <summary>Sounds still being decoded on a worker thread.</summary>
        public int SoundsDecoding
        {
            get
            {
                int count = 0;
                foreach (SoundEntry entry in _sounds.Values) if (entry.Decoding != null) count++;
                return count;
            }
        }

        public XAudioSystem(string projectPath)
        {
            _projectPath = projectPath ?? string.Empty;
            _engine = new AudioEngine();
        }

        public float MasterVolume
        {
            get => _busMaster;
            set
            {
                _busMaster = Math.Clamp(value, 0f, 2f);
                if (_engine != null)
                    _engine.MasterVolume = _muted ? 0f : _busMaster;
            }
        }

        private bool _muted;

        /// <summary>
        /// Silences the device without changing what <see cref="MasterVolume"/> reads back, so a
        /// game's own volume setting cannot turn the sound back on. Used for test windows.
        /// </summary>
        public bool Muted
        {
            get => _muted;
            set
            {
                _muted = value;
                if (_engine != null) _engine.MasterVolume = _muted ? 0f : _busMaster;
            }
        }

        public void SetChannelBus(AudioChannel channel, string bus)
        {
            if (!channel.IsValid || !_channels.TryGetValue(channel.Id, out ChannelState? state) || state == null) return;
            state.Bus = BusName(bus);
            state.BusChosen = true;
            ApplySpatial(channel.Id);
        }

        public void SetBusVolume(string bus, float volume)
        {
            float v = Math.Clamp(volume, 0f, 2f);
            string name = BusName(bus);
            if (name == "master") MasterVolume = v;
            else _buses[name] = v;
            RefreshChannelVolumes();
        }

        public int LoadSound(string projectRelativePath) => LoadSound(projectRelativePath, null);

        public int LoadSound(string projectRelativePath, AudioAssetSettings? auditionSettings)
        {
            if (string.IsNullOrEmpty(projectRelativePath)) return 0;
            int cached = 0;
            if (auditionSettings is null && _pathToId.TryGetValue(projectRelativePath, out cached) && IsCurrent(cached))
            {
                if (_sounds.TryGetValue(cached, out SoundEntry? known) && known.Decoding != null) TryFinishDecoding(known);
                return cached;
            }

            using var timed = Genesis.Shared.Assets.LoadClock.Measure(Genesis.Shared.Assets.LoadWork.Sound);
            if (!TryDescribe(_projectPath, projectRelativePath, auditionSettings, out SoundSource source)) return cached;
            if (auditionSettings is null && TryReuse(projectRelativePath, cached, source, out int same)) return same;

            SoundEntry entry;
            if (auditionSettings is null
                && ((DecodeLargeSoundsInBackground && DecodesOnWorker(source)) || (_decoder?.Knows(source.Key) ?? false)))
            {
                // Decoded on a worker, or already decoded ahead of time (PreloadSound).
                entry = NewDecodingEntry(source, preload: false);
            }
            else
            {
                var effect = SoundEffect.FromWavFile(source.File, source.Settings);
                if (effect == null) return cached;
                entry = new SoundEntry { Effect = effect };
                entry.Bus = effect.DurationInSeconds > 10f ? "music" : "sfx";
                ApplyAudioMeta(source.File, entry, source.Settings);
            }

            int id = cached != 0 ? cached : _nextSoundId++;
            Store(id, entry);
            if (auditionSettings is null) Remember(projectRelativePath, id, source);
            return id;
        }

        /// <summary>
        /// Starts decoding a sound on a worker thread now, so that a later play of it (by any of
        /// its names) starts at once. Returns at once with the sound's id, as <see cref="LoadSound(string)"/>
        /// would, or 0 when there is no such sound. A play of it before it is decoded counts as
        /// playing and is heard when it is ready.
        /// </summary>
        public int PreloadSound(string projectRelativePath)
        {
            if (string.IsNullOrWhiteSpace(projectRelativePath)) return 0;
            if (_pathToId.TryGetValue(projectRelativePath, out int cached) && IsCurrent(cached)) return cached;
            if (!TryDescribe(_projectPath, projectRelativePath, null, out SoundSource source)) return 0;
            if (TryReuse(projectRelativePath, cached, source, out int same)) return same;
            int id = cached != 0 ? cached : _nextSoundId++;
            Store(id, NewDecodingEntry(source, preload: true));
            Remember(projectRelativePath, id, source);
            return id;
        }

        /// <summary>
        /// Decodes the project's Audio resources on worker threads, smallest file first, until
        /// <paramref name="budgetBytes"/> of samples are held (the project setting
        /// <c>runtime.preloadAudioMegabytes</c>). A play of one of them then starts at once.
        /// <paramref name="report"/> is told, from a worker, what was decoded once it is done.
        /// </summary>
        public void PreloadProjectSounds(long budgetBytes, Action<string>? report = null)
        {
            if (budgetBytes <= 0 || string.IsNullOrWhiteSpace(_projectPath)) return;
            string project = _projectPath;
            Decoder.StartBudget(() => ListProjectSources(project), budgetBytes, report);
        }

        /// <summary>Every Audio resource of a project as the decoder reads it, smallest file first.</summary>
        internal static IReadOnlyList<SoundSource> ListProjectSources(string projectPath)
        {
            var sources = new List<SoundSource>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Genesis.Shared.Assets.NamedResource resource in ResourceNames.For(projectPath).Entries)
            {
                if (resource.Type != ResourceType.Audio) continue;
                if (TryDescribe(projectPath, resource.FullPath, null, out SoundSource source) && keys.Add(source.Key))
                    sources.Add(source);
            }

            return sources.OrderBy(source => source.Length).ThenBy(source => source.File, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        // Another name for a sound already loaded (or being decoded) from the same source.
        private bool TryReuse(string reference, int cached, SoundSource source, out int id)
        {
            id = 0;
            if (!_sourceToId.TryGetValue(source.Key, out int same) || same == cached || !_sounds.ContainsKey(same)) return false;
            _pathToId[reference] = same;
            id = same;
            return true;
        }

        private SoundEntry NewDecodingEntry(SoundSource source, bool preload)
        {
            // A sound of a megabyte or more is music unless its resource says otherwise; without
            // one, its group follows its length once that is known.
            var entry = new SoundEntry
            {
                Bus = source.Length >= BackgroundDecodeBytes ? "music" : "sfx",
                BusFromLength = source.Settings is null,
                DecodeKey = source.Key,
            };
            ApplyAudioMeta(source.File, entry, source.Settings);
            entry.Decoding = Decoder.Request(source.Key, source.File, source.Settings, preload);
            TryFinishDecoding(entry);
            return entry;
        }

        private void Store(int id, SoundEntry entry)
        {
            if (_sounds.TryGetValue(id, out SoundEntry? previous) && previous.DecodeKey.Length > 0
                && !string.Equals(previous.DecodeKey, entry.DecodeKey, StringComparison.OrdinalIgnoreCase))
                _decoder?.Forget(previous.DecodeKey);
            _sounds[id] = entry;
        }

        private void Remember(string reference, int id, SoundSource source)
        {
            _pathToId[reference] = id;
            _sourceToId[source.Key] = id;
            string file = source.File;
            _soundVersions[id] = Array.ConvertAll(
                new[] { source.ResourcePath, file, Path.ChangeExtension(file, ".audio.json"), file + ".audio.json" }, CaptureVersion);
        }

        /// <summary>
        /// Whether a loaded sound still matches its files. An exported game loads each asset once
        /// (<see cref="Genesis.Shared.Assets.RuntimeAssetPolicy.PollingEnabled"/> is off), so it
        /// trusts what it loaded instead of reading four files' times on every play.
        /// </summary>
        private bool IsCurrent(int id)
        {
            if (!_soundVersions.TryGetValue(id, out var versions)) return false;
            if (!Genesis.Shared.Assets.RuntimeAssetPolicy.PollingEnabled) return true;
            return Array.TrueForAll(versions, version => CaptureVersion(version.Path) == version);
        }

        /// <summary>
        /// Whether a game decodes this sound on a worker: every Ogg Vorbis file (its decoding costs
        /// about 2 ms a second of stereo sound once warm, several times that in a game's first
        /// seconds), and any file of <see cref="BackgroundDecodeBytes"/> or more.
        /// </summary>
        private static bool DecodesOnWorker(SoundSource source) =>
            source.Length >= BackgroundDecodeBytes
            || source.File.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
            || source.File.EndsWith(".oga", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// What a script's name for a sound stands for. False when it names no sound of the
        /// project, its settings are not usable, or its file is missing.
        /// </summary>
        internal static bool TryDescribe(string projectPath, string reference, AudioAssetSettings? auditionSettings, out SoundSource source)
        {
            source = default;
            string abs = ResolvePlayReference(projectPath, reference);
            if (abs.Length == 0) return false;
            string resourcePath = abs;

            // A .audio.json resource may be handed to us directly (that is what the Audio
            // Editor saves, and what PGSL PlaySound receives from an asset field); resolve
            // it to the sound file it points at so every spelling of "play this sound" works.
            AudioAssetSettings? authored = null;
            if (abs.EndsWith(".audio.json", StringComparison.OrdinalIgnoreCase))
            {
                authored = AudioAssetSettings.Load(abs);
                if (authored?.Source is not { Length: > 0 } named) return false;
                abs = ResolveSource(projectPath, abs, named);
                if (abs.Length == 0) return false;
            }

            authored = auditionSettings ?? authored;
            if (authored is null)
                authored = AudioAssetSettings.Load(Path.ChangeExtension(abs, ".audio.json"))
                    ?? AudioAssetSettings.Load(abs + ".audio.json");
            if (authored is not null && !ValidPlaybackSettings(authored)) return false;

            long length, written;
            try
            {
                var file = new FileInfo(abs);
                if (!file.Exists) return false;
                length = file.Length;
                written = file.LastWriteTimeUtc.Ticks;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }

            string region = authored is null ? string.Empty : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{authored.TrimStart:R}|{authored.TrimEnd:R}|{authored.FadeIn:R}|{authored.FadeOut:R}");
            string fullPath = Path.GetFullPath(abs);
            source = new SoundSource($"{fullPath}|{length}|{written}|{region}", fullPath, authored, resourcePath, length);
            return true;
        }

        /// <summary>
        /// Takes a sound decoded on a worker once it is done. True when the sound can be played
        /// now; false while it is still being decoded, or when it could not be (see <see cref="SoundEntry.Failed"/>).
        /// </summary>
        private static bool TryFinishDecoding(SoundEntry entry)
        {
            if (entry.Effect != null) return true;
            if (entry.Decoding == null || !entry.Decoding.IsCompleted) return false;
            SoundEffect? effect = entry.Decoding.IsCompletedSuccessfully ? entry.Decoding.Result : null;
            entry.Decoding = null;
            if (effect == null)
            {
                entry.Failed = true;
                return false;
            }

            entry.Effect = effect;
            if (entry.BusFromLength) entry.Bus = effect.DurationInSeconds > 10f ? "music" : "sfx";
            return true;
        }

        /// <summary>Starts the channels that were asked to play a sound while it was being decoded.</summary>
        private void StartPendingChannels()
        {
            List<int>? failed = null;
            foreach (KeyValuePair<int, ChannelState> pair in _channels)
            {
                ChannelState state = pair.Value;
                if (!state.Pending || !_sounds.TryGetValue(state.SoundId, out SoundEntry? entry)) continue;
                if (!TryFinishDecoding(entry))
                {
                    if (entry.Failed || entry.Decoding == null) (failed ??= new List<int>()).Add(pair.Key);
                    continue;
                }

                if (!state.BusChosen) state.Bus = entry.Bus;
                IXAudio2SourceVoice? voice = entry.Effect.PlayVoice(_engine, state.BaseVolume * BusGain(state.Bus),
                    state.PendingPitch * state.AssetPitch, state.Loop);
                state.Pending = false;
                if (voice == null)
                {
                    (failed ??= new List<int>()).Add(pair.Key);
                    continue;
                }

                state.Voice = voice;
                state.Effect = entry.Effect;
                ApplySpatial(pair.Key);
            }

            if (failed != null)
                foreach (int id in failed) _channels.Remove(id);
        }

        private static (string Path, long Stamp, long Length) CaptureVersion(string path)
        {
            try { FileInfo file = new(path); return (path, file.Exists ? file.LastWriteTimeUtc.Ticks : 0, file.Exists ? file.Length : 0); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return (path, -1, -1); }
        }

        private static bool ValidPlaybackSettings(AudioAssetSettings settings) =>
            float.IsFinite(settings.Volume) && settings.Volume is >= 0 and <= 1
            && float.IsFinite(settings.Pitch) && settings.Pitch is >= .1f and <= 4
            && float.IsFinite(settings.MinDistance) && settings.MinDistance >= 0
            && float.IsFinite(settings.MaxDistance) && settings.MaxDistance > 0
            && float.IsFinite(settings.Falloff) && settings.Falloff > 0
            && float.IsFinite(settings.TrimStart) && settings.TrimStart >= 0
            && float.IsFinite(settings.TrimEnd) && settings.TrimEnd >= 0
            && float.IsFinite(settings.FadeIn) && settings.FadeIn >= 0
            && float.IsFinite(settings.FadeOut) && settings.FadeOut >= 0;

        public AudioChannel Play(int soundId, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            if (!_sounds.TryGetValue(soundId, out var entry)) return AudioChannel.Invalid;
            if (!TryFinishDecoding(entry))
            {
                if (entry.Failed) return AudioChannel.Invalid;
                // Still being decoded: a channel that plays (and counts as playing) from now, and
                // starts to sound as soon as the samples are ready (see Update). A sound only
                // preloaded so far goes ahead of the others still waiting.
                _decoder?.Promote(entry.DecodeKey);
                int waiting = _nextChannelId++;
                _channels[waiting] = new ChannelState
                {
                    Id = waiting,
                    SoundId = soundId,
                    Loop = loop || entry.Loop,
                    Spatial = entry.Spatial,
                    BaseVolume = volume * entry.Gain,
                    UserVolume = volume,
                    AssetPitch = entry.Pitch,
                    Bus = entry.Bus,
                    Settings = entry.Settings,
                    Position = _listenerPos,
                    Pending = true,
                    PendingPitch = pitch,
                };
                return new AudioChannel(waiting);
            }

            bool wantLoop = loop || entry.Loop;
            float gain = volume * entry.Gain * BusGain(entry.Bus);
            var voice = entry.Effect.PlayVoice(_engine, gain, pitch * entry.Pitch, wantLoop);
            if (voice == null) return AudioChannel.Invalid;

            int channelId = _nextChannelId++;
            _channels[channelId] = new ChannelState
            {
                Id = channelId,
                SoundId = soundId,
                Loop = wantLoop,
                Spatial = entry.Spatial,
                BaseVolume = volume * entry.Gain,
                UserVolume = volume,
                AssetPitch = entry.Pitch,
                Bus = entry.Bus,
                Settings = entry.Settings,
                Voice = voice,
                Effect = entry.Effect,
                Position = _listenerPos,
            };
            ApplySpatial(channelId);
            return new AudioChannel(channelId);
        }

        public AudioChannel PlayAt(int soundId, Vector3 worldPos, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            var ch = Play(soundId, volume, pitch, loop);
            if (!ch.IsValid) return ch;
            if (_channels.TryGetValue(ch.Id, out var state))
            {
                state.Spatial = true;
                state.Position = worldPos;
                _channels[ch.Id] = state;
                ApplySpatial(ch.Id);
            }
            return ch;
        }

        public void Stop(AudioChannel channel)
        {
            if (!channel.IsValid) return;
            if (_channels.TryGetValue(channel.Id, out var state))
            {
                try { state.Voice?.Stop(); state.Voice?.FlushSourceBuffers(); } catch { }
                _channels.Remove(channel.Id);
            }
        }

        public void StopAll()
        {
            foreach (var kv in _channels)
            {
                try { kv.Value.Voice?.Stop(); kv.Value.Voice?.FlushSourceBuffers(); } catch { }
            }
            _channels.Clear();
        }

        public bool IsPlaying(AudioChannel channel)
        {
            if (!channel.IsValid || !_channels.TryGetValue(channel.Id, out var state))
                return false;
            try
            {
                if (state.Pending) return true;
                if (state.Voice == null) return false;
                return state.Voice.State.BuffersQueued > 0 || state.Loop;
            }
            catch
            {
                return false;
            }
        }

        public void SetChannelVolume(AudioChannel channel, float volume)
        {
            if (!channel.IsValid || !_channels.TryGetValue(channel.Id, out ChannelState? state) || state == null) return;
            state.UserVolume = Math.Clamp(volume, 0f, 2f);
            state.Fading = false;
            state.BaseVolume = state.UserVolume * state.Settings.Volume;
            ApplySpatial(channel.Id);
        }

        public void SetChannelPitch(AudioChannel channel, float pitch)
        {
            if (!channel.IsValid || !_channels.TryGetValue(channel.Id, out ChannelState? state) || state == null) return;
            if (!float.IsFinite(pitch)) return;
            if (state.Pending) { state.PendingPitch = pitch; return; }
            if (state.Voice == null) return;
            try { state.Voice.SetFrequencyRatio(Math.Clamp(pitch * state.AssetPitch, 0.01f, 4f), 0); }
            catch (SharpGen.Runtime.SharpGenException) { }
        }

        public void FadeChannel(AudioChannel channel, float volume, float seconds, bool stopWhenDone = false)
        {
            if (!channel.IsValid || !_channels.TryGetValue(channel.Id, out ChannelState? state) || state == null) return;
            float target = Math.Clamp(float.IsFinite(volume) ? volume : 0f, 0f, 2f);
            if (!(seconds > 0f))
            {
                SetChannelVolume(channel, target);
                if (stopWhenDone) Stop(channel);
                return;
            }

            state.Fading = true;
            state.StopAfterFade = stopWhenDone;
            state.FadeFrom = state.UserVolume;
            state.FadeTo = target;
            state.FadeSeconds = seconds;
            state.FadeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public float GetBusVolume(string bus)
        {
            string name = BusName(bus);
            if (name == "master") return _busMaster;
            return _buses.TryGetValue(name, out float volume) ? volume : 1f;
        }

        public void SetChannelPosition(AudioChannel channel, Vector3 position)
        {
            if (!channel.IsValid || !_channels.TryGetValue(channel.Id, out ChannelState? state) || state == null) return;
            state.Spatial = true;
            state.Position = position;
            ApplySpatial(channel.Id);
        }

        public void SetListener(Vector3 position, Vector3 forward) => SetListener(position, forward, Vector3.UnitX);

        public void SetListener(Vector3 position, Vector3 forward, Vector3 right)
        {
            Vector3 facing = forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward) : _listenerForward;
            Vector3 side = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : _listenerRight;
            // Called every frame by the host: nothing to do while the listener stands still.
            if (Vector3.DistanceSquared(position, _listenerPos) < 1e-8f && Vector3.DistanceSquared(facing, _listenerForward) < 1e-8f
                && Vector3.DistanceSquared(side, _listenerRight) < 1e-8f) return;
            _listenerPos = position;
            _listenerForward = facing;
            _listenerRight = side;
            foreach (var id in _channels.Keys)
                ApplySpatial(id);
        }

        public void SetChannelSpatialSettings(AudioChannel channel, bool spatial, AudioSpatialSettings settings)
        {
            if (!channel.IsValid || !_channels.TryGetValue(channel.Id, out ChannelState? state) || state == null) return;
            state.Spatial = spatial;
            state.SpatialOverride = settings;
            ApplySpatial(channel.Id);
        }

        public void Update()
        {
            _engine?.Update();
            if (_channels.Count == 0) return;
            StartPendingChannels();

            List<int>? faded = null;
            foreach (var kv in _channels)
            {
                ChannelState fading = kv.Value;
                if (!fading.Fading) continue;
                float progress = (float)(System.Diagnostics.Stopwatch.GetElapsedTime(fading.FadeStarted).TotalSeconds / fading.FadeSeconds);
                if (progress >= 1f)
                {
                    progress = 1f;
                    fading.Fading = false;
                    if (fading.StopAfterFade) (faded ??= new List<int>()).Add(kv.Key);
                }

                fading.UserVolume = fading.FadeFrom + (fading.FadeTo - fading.FadeFrom) * progress;
                fading.BaseVolume = fading.UserVolume * fading.Settings.Volume;
                ApplySpatial(kv.Key);
            }

            if (faded != null)
                foreach (int id in faded) Stop(new AudioChannel(id));
            if (_channels.Count == 0) return;

            var dead = new List<int>();
            foreach (var kv in _channels)
            {
                try
                {
                    if (kv.Value.Pending) continue;
                    if (kv.Value.Voice == null) { dead.Add(kv.Key); continue; }
                    if (!kv.Value.Loop && kv.Value.Voice.State.BuffersQueued == 0)
                        dead.Add(kv.Key);
                }
                catch
                {
                    dead.Add(kv.Key);
                }
            }
            foreach (int id in dead)
                _channels.Remove(id);
        }

        public void Dispose()
        {
            _decoder?.Dispose();
            _engine?.Dispose();
        }

        /// <summary>
        /// Each loaded sound with the memory its samples take and how many of its voices are
        /// playing, for the debug screen's Resources tab.
        /// </summary>
        public List<(string Name, long Bytes, int Playing, float Seconds)> DescribeSounds()
        {
            var playing = new Dictionary<int, int>();
            foreach (ChannelState channel in _channels.Values)
            {
                playing.TryGetValue(channel.SoundId, out int count);
                playing[channel.SoundId] = count + 1;
            }

            var rows = new List<(string Name, long Bytes, int Playing, float Seconds)>(_pathToId.Count);
            foreach (KeyValuePair<string, int> sound in _pathToId)
            {
                if (!_sounds.TryGetValue(sound.Value, out SoundEntry? entry) || entry.Effect == null) continue;
                playing.TryGetValue(sound.Value, out int voices);
                rows.Add((sound.Key, entry.Effect.SampleBytes, voices, entry.Effect.DurationInSeconds));
            }

            return rows;
        }

        // The master volume is the device's (MasterVolume); a bus scales only its own sounds.
        private float BusGain(string bus)
        {
            string name = BusName(bus);
            if (name == "master") return 1f;
            return _buses.TryGetValue(name, out float volume) ? volume : 1f;
        }

        private void RefreshChannelVolumes()
        {
            foreach (var kv in _channels)
                ApplySpatial(kv.Key);
        }

        private void ApplySpatial(int channelId)
        {
            if (!_channels.TryGetValue(channelId, out var state) || state.Voice == null) return;
            float vol = state.BaseVolume * BusGain(state.Bus);
            float left = 1f, right = 1f;
            if (state.Spatial)
            {
                // Authored MinDistance / MaxDistance / Falloff, shared with the editor's
                // audition so the designer hears the curve they are dialling in.
                float dist = Vector3.Distance(_listenerPos, state.Position);
                vol *= state.SpatialOverride?.AttenuationAt(dist) ?? state.Settings.AttenuationAt(dist);
                AudioPanning.StereoLevels(_listenerPos, _listenerRight, state.Position, out left, out right);
            }

            try
            {
                state.Voice.SetVolume(Math.Clamp(vol, 0f, 2f));
                // The ear a positioned sound is nearer to. Left alone until a sound is first off
                // centre, so sounds that are never positioned keep the device's own routing.
                if (MathF.Abs(left - state.PanLeft) > 0.004f || MathF.Abs(right - state.PanRight) > 0.004f)
                {
                    if (_outputChannels < 0) _outputChannels = _engine.OutputChannels;
                    int source = state.Effect?.Channels ?? 0;
                    if (_panMatrix.Length < source * _outputChannels) _panMatrix = new float[source * _outputChannels];
                    if (AudioPanning.FillMatrix(_panMatrix, source, _outputChannels, left, right))
                        state.Voice.SetOutputMatrix((uint)source, (uint)_outputChannels, _panMatrix);
                    state.PanLeft = left;
                    state.PanRight = right;
                }
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                // The voice was destroyed underneath us (device change / recycle); Update()
                // reaps it on the next frame, so a missed volume set is not worth surfacing.
            }
        }

        private void ApplyAudioMeta(string wavPath, SoundEntry entry, AudioAssetSettings? authored)
        {
            // Either the caller already loaded a .audio.json resource, or we look for a
            // sidecar next to the WAV (Jump.wav → Jump.audio.json, or Jump.wav.audio.json).
            AudioAssetSettings? settings = authored;
            if (settings == null)
            {
                string meta = Path.ChangeExtension(wavPath, ".audio.json");
                if (!File.Exists(meta)) meta = wavPath + ".audio.json";
                settings = AudioAssetSettings.Load(meta);
            }

            if (settings == null) return;

            entry.Settings = settings;
            entry.Gain = settings.Volume;
            entry.Pitch = settings.Pitch <= 0f ? 1f : settings.Pitch;
            entry.Loop = settings.Loop;
            entry.Spatial = settings.Spatial;
            if (!string.IsNullOrWhiteSpace(settings.Bus)) entry.Bus = settings.Bus;
        }

        private static string ResolveFileReference(string projectPath, string reference)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return reference;
            return ResourceNames.ResolveFile(projectPath, reference, ResourceType.Audio);
        }

        /// <summary>
        /// The file a script's name for a sound stands for: an Audio resource's name ("snd_x"), its
        /// .audio.json or sound file from the project ("Assets/Audio/snd_x.ogg") or from the Assets
        /// folder ("Audio/snd_x.audio.json"), or the sound file's own name ("snd_x.ogg") when the
        /// Audio resource of that name plays that file. Empty when it names no sound.
        /// </summary>
        internal static string ResolvePlayReference(string projectPath, string reference)
        {
            string found = ResolveFileReference(projectPath, reference);
            if (found.Length > 0 || string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(reference)) return found;
            string relative = reference.Trim().Replace('\\', '/').TrimStart('/');
            if (relative.Length == 0 || Path.IsPathRooted(relative)) return found;

            if (!relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                found = ResolveFileReference(projectPath, "Assets/" + relative);
                if (found.Length > 0) return found;
            }

            if (relative.IndexOf('/') < 0 && IsSoundFileName(relative))
            {
                // "snd_x.ogg": the resource named snd_x, if snd_x.ogg is the file it plays.
                string named = ResolveFileReference(projectPath, Path.GetFileNameWithoutExtension(relative));
                if (named.EndsWith(".audio.json", StringComparison.OrdinalIgnoreCase)
                    && AudioAssetSettings.Load(named)?.Source is { Length: > 0 } source
                    && string.Equals(Path.GetFileName(source.Replace('\\', '/')), relative, StringComparison.OrdinalIgnoreCase))
                    return named;
            }

            return string.Empty;
        }

        private static bool IsSoundFileName(string name)
        {
            string extension = Path.GetExtension(name);
            return extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".oga", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The sound file an <c>.audio.json</c> names. A source written beside the document
        /// ("Jump.ogg", or "../Music/Theme.ogg") is found from the document's own folder first; then,
        /// as Studio's Audio editor writes it ("Assets/Audio/Jump.wav"), from the project.
        /// </summary>
        internal string ResolveSource(string documentPath, string source) => ResolveSource(_projectPath, documentPath, source);

        private static string ResolveSource(string projectPath, string documentPath, string source) =>
            AudioAssetSettings.ResolveSourceBeside(projectPath, documentPath, source) ?? ResolveFileReference(projectPath, source);
    }
}
