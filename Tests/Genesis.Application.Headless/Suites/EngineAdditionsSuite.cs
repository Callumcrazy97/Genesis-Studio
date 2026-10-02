using System.Numerics;
using Genesis.Runtime;
using Genesis.Runtime.Climate;
using Genesis.Runtime.Core;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.Audio;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless;

/// <summary>
/// Engine features added for games that need them rather than for one game: weather the engine
/// draws itself, and what follows it in this file.
/// </summary>
internal static class EngineAdditionsSuite
{
    private sealed class ThunderRecorder : IAudioSystem
    {
        public readonly List<(string Sound, float Volume)> Played = new();
        private readonly Dictionary<int, string> _sounds = new();
        public float MasterVolume { get; set; } = 1f;
        public int LoadSound(string path) { int id = _sounds.Count + 1; _sounds[id] = path; return id; }
        public AudioChannel Play(int soundId, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            Played.Add((_sounds[soundId], volume));
            return new AudioChannel(Played.Count);
        }

        public void Stop(AudioChannel channel) { }
        public void StopAll() { }
        public bool IsPlaying(AudioChannel channel) => false;
        public void SetChannelVolume(AudioChannel channel, float volume) { }
        public void SetChannelPosition(AudioChannel channel, Vector3 position) { }
        public void SetListener(Vector3 position, Vector3 forward) { }
        public void Update() { }
    }

    /// <summary>Writes down what scripts ask the audio system to do.</summary>
    private sealed class AudioRecorder : IAudioSystem
    {
        public readonly List<string> Calls = new();
        private int _channels;
        private static string F(float value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        private static string V(Vector3 value) => $"{F(value.X)},{F(value.Y)},{F(value.Z)}";
        public float MasterVolume { get; set; } = 1f;
        public int LoadSound(string path) => 7;
        public AudioChannel Play(int soundId, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            Calls.Add($"play {soundId} {F(volume)} {F(pitch)} {loop}");
            return new AudioChannel(++_channels);
        }

        public void Stop(AudioChannel channel) => Calls.Add($"stop {channel.Id}");
        public void StopAll() { }
        public bool IsPlaying(AudioChannel channel) => true;
        public void SetChannelVolume(AudioChannel channel, float volume) => Calls.Add($"volume {channel.Id} {F(volume)}");
        public void SetChannelPosition(AudioChannel channel, Vector3 position) => Calls.Add($"position {channel.Id} {V(position)}");
        public void SetChannelPitch(AudioChannel channel, float pitch) => Calls.Add($"pitch {channel.Id} {F(pitch)}");
        public void SetBusVolume(string bus, float volume) => Calls.Add($"bus {bus} {F(volume)}");
        public void SetChannelBus(AudioChannel channel, string bus) => Calls.Add($"channel {channel.Id} on {bus}");
        public void SetListener(Vector3 position, Vector3 forward) => Calls.Add($"listener {V(position)}");
        public void Update() { }
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

    private static void RunInputAndAudio(HeadlessContext context)
    {
        HeadlessHarness.RunCase(context.Report, "Engine.Input.Gamepad.ScriptsReadTheControllerAndTheMouseWheel", () =>
        {
            using var scene = new RuntimeScene("Controller") { Input = new Genesis.Runtime.Input.InputState() };
            var game = new ProjectGameContext(context.Workspace, scene, null, null, RoomAsset.Create("Hall", RoomDimension.ThreeD), null);
            var oldGame = Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext;
            try
            {
                Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext = game;
                Genesis.Runtime.Input.InputState input = scene.Input;
                HeadlessHarness.Assert(!Genesis.Runtime.Scripting.PgslCommands.GamepadConnected(), "A controller was reported with none plugged in.");
                input.GamepadConnected = true;

                // A button: down this frame, held the next, up the frame it is let go.
                input.SetGamepadButton(Genesis.Runtime.Input.GamepadButton.A, true);
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.GamepadConnected()
                    && Genesis.Runtime.Scripting.PgslCommands.GamepadPressed("A") && Genesis.Runtime.Scripting.PgslCommands.GamepadCheck("a")
                    && !Genesis.Runtime.Scripting.PgslCommands.GamepadReleased("A"), "Pressing A was not seen as pressed and held.");
                input.NextFrame();
                input.SetGamepadButton(Genesis.Runtime.Input.GamepadButton.A, true);
                HeadlessHarness.Assert(!Genesis.Runtime.Scripting.PgslCommands.GamepadPressed("A") && Genesis.Runtime.Scripting.PgslCommands.GamepadCheck("A"),
                    "A held for a second frame was reported as pressed again, or not as held.");
                input.SetGamepadButton(Genesis.Runtime.Input.GamepadButton.A, false);
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.GamepadReleased("A") && !Genesis.Runtime.Scripting.PgslCommands.GamepadCheck("A"),
                    "Letting go of A was not seen.");
                input.NextFrame();
                HeadlessHarness.Assert(!Genesis.Runtime.Scripting.PgslCommands.GamepadReleased("A"), "A release lasted more than its frame.");

                // The names a script may use, and ones it may not.
                input.SetGamepadButton(Genesis.Runtime.Input.GamepadButton.RightTrigger, true);
                input.SetGamepadButton(Genesis.Runtime.Input.GamepadButton.DPadLeft, true);
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.GamepadCheck("RT") && Genesis.Runtime.Scripting.PgslCommands.GamepadCheck("R2")
                    && Genesis.Runtime.Scripting.PgslCommands.GamepadCheck("RightTrigger") && Genesis.Runtime.Scripting.PgslCommands.GamepadCheck("DPadLeft")
                    && Genesis.Runtime.Scripting.PgslCommands.GamepadCheck("left") && !Genesis.Runtime.Scripting.PgslCommands.GamepadCheck("LT")
                    && !Genesis.Runtime.Scripting.PgslCommands.GamepadCheck("Q") && !Genesis.Runtime.Scripting.PgslCommands.GamepadCheck("14")
                    && !Genesis.Runtime.Scripting.PgslCommands.GamepadCheck(""),
                    "Controller button names were not understood as documented.");

                // Sticks and triggers: right and away from the player are positive on both sticks.
                input.LeftStick = new Vector2(0.5f, 0.75f);
                input.RightStick = new Vector2(-0.25f, 0.5f);
                input.LeftTrigger = 0.3f;
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.GamepadAxis("LeftX") == 0.5 && Genesis.Runtime.Scripting.PgslCommands.GamepadAxis("lefty") == 0.75
                    && Genesis.Runtime.Scripting.PgslCommands.GamepadAxis("RightX") == -0.25 && Genesis.Runtime.Scripting.PgslCommands.GamepadAxis("RightY") == -0.5
                    && Math.Abs(Genesis.Runtime.Scripting.PgslCommands.GamepadAxis("LeftTrigger") - 0.3) < 1e-6
                    && Genesis.Runtime.Scripting.PgslCommands.GamepadAxis("Nonsense") == 0,
                    "The sticks and triggers did not read back as they were set.");

                // The wheel is for its frame only.
                input.OnWheel(2f);
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.MouseWheel() == 2, "The mouse wheel did not reach scripts.");
                input.NextFrame();
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.MouseWheel() == 0, "The mouse wheel kept turning after its frame.");

                // Vibration is kept in range, and the key emulation can be turned off.
                Genesis.Runtime.Scripting.PgslCommands.GamepadVibrate(0.5, 3, 0.25);
                HeadlessHarness.Assert(input.RumbleLow == 0.5f && input.RumbleHigh == 1f && input.RumbleSeconds == 0.25f, "Vibration was not stored within its limits.");
                HeadlessHarness.Assert(input.GamepadEmulatesKeyboard, "A controller should press keys as well until a game says otherwise.");
                Genesis.Runtime.Scripting.PgslCommands.GamepadKeyboardEmulation(false);
                HeadlessHarness.Assert(!input.GamepadEmulatesKeyboard, "A script could not stop the controller pressing keys.");

                // Unplugging lets go of everything.
                input.ReleaseGamepad();
                HeadlessHarness.Assert(!Genesis.Runtime.Scripting.PgslCommands.GamepadCheck("RT") && Genesis.Runtime.Scripting.PgslCommands.GamepadReleased("RT")
                    && Genesis.Runtime.Scripting.PgslCommands.GamepadAxis("LeftX") == 0, "An unplugged controller left a button held or a stick pushed.");
            }
            finally
            {
                Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext = oldGame;
            }
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Audio.Positional.ASoundIsHeardFromWhereItIsAndCanFade", () =>
        {
            // Which ear: the nearer one is at full level, the further one falls with how far to the side the sound is.
            static (float Left, float Right) Ears(Vector3 source, Vector3 right)
            {
                AudioPanning.StereoLevels(Vector3.Zero, right, source, out float l, out float r);
                return (l, r);
            }

            var toRight = Ears(new Vector3(10, 0, 0), Vector3.UnitX);
            var toLeft = Ears(new Vector3(-10, 0, 0), Vector3.UnitX);
            var ahead = Ears(new Vector3(0, 0, 10), Vector3.UnitX);
            var above = Ears(new Vector3(0, 10, 0), Vector3.UnitX);
            var inside = Ears(new Vector3(0.1f, 0, 0), Vector3.UnitX);
            var arm = Ears(new Vector3(1, 0, 0), Vector3.UnitX);
            var turned = Ears(new Vector3(10, 0, 0), -Vector3.UnitX);
            HeadlessHarness.Assert(toRight.Right == 1f && MathF.Abs(toRight.Left - AudioPanning.FarEarLevel) < 1e-4f
                && toLeft.Left == 1f && MathF.Abs(toLeft.Right - AudioPanning.FarEarLevel) < 1e-4f,
                $"A sound to one side should be full in that ear and faint in the other: right gives {toRight}, left gives {toLeft}.");
            HeadlessHarness.Assert(ahead == (1f, 1f) && above == (1f, 1f) && inside == (1f, 1f),
                $"A sound ahead, above or at the listener should be equal in both ears: {ahead}, {above}, {inside}.");
            HeadlessHarness.Assert(arm.Right == 1f && arm.Left > toRight.Left + 0.2f && arm.Left < 0.9f,
                $"A sound a metre to the right should be only partly to the side ({arm}).");
            HeadlessHarness.Assert(turned == toLeft, "Turning to face the other way did not swap the ears.");

            var matrix = new float[12];
            HeadlessHarness.Assert(AudioPanning.FillMatrix(matrix, 1, 2, 0.3f, 1f) && matrix[0] == 0.3f && matrix[1] == 1f,
                "A mono sound's levels did not go to the left and right outputs.");
            HeadlessHarness.Assert(AudioPanning.FillMatrix(matrix, 2, 2, 0.3f, 1f) && matrix.Take(4).SequenceEqual([0.3f, 0f, 0f, 1f]),
                "A stereo sound's channels should keep their own sides, each at its ear's level.");
            HeadlessHarness.Assert(AudioPanning.FillMatrix(matrix, 1, 6, 0.3f, 1f) && matrix.Take(6).SequenceEqual([0.3f, 1f, 0f, 0f, 0f, 0f]),
                "On a surround device a positioned sound should use the front pair.");
            HeadlessHarness.Assert(!AudioPanning.FillMatrix(matrix, 3, 2, 1f, 1f) && !AudioPanning.FillMatrix(matrix, 1, 1, 1f, 1f),
                "A layout that cannot be panned was not refused.");

            // What the script commands ask of the audio system.
            using var scene = new RuntimeScene("Sound") { Input = new Genesis.Runtime.Input.InputState() };
            var recorder = new AudioRecorder();
            var game = new ProjectGameContext(context.Workspace, scene, null, null, RoomAsset.Create("Hall", RoomDimension.ThreeD), null, recorder);
            var oldGame = Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext;
            bool oldManual = Genesis.Runtime.Scripting.PgslCommands.AudioListenerManual;
            try
            {
                Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext = game;
                Genesis.Runtime.Scripting.PgslCommands.AudioListenerManual = false;
                double channel = Genesis.Runtime.Scripting.PgslCommands.PlaySoundAt("Fire", 4, 1, -2, 0.8, 1.5, true);
                Genesis.Runtime.Scripting.PgslCommands.SoundSetPosition(channel, 5, 1, -2);
                Genesis.Runtime.Scripting.PgslCommands.SoundSetVolume(channel, 3);
                Genesis.Runtime.Scripting.PgslCommands.SoundSetPitch(channel, 0.5);
                Genesis.Runtime.Scripting.PgslCommands.SoundFade(channel, 0.25, 2);
                Genesis.Runtime.Scripting.PgslCommands.StopSoundFaded(channel, 1);
                Genesis.Runtime.Scripting.PgslCommands.SetBusVolume("music", 0.4);
                Genesis.Runtime.Scripting.PgslCommands.SoundSetBus(channel, "sfx");
                string[] expected =
                [
                    "play 7 0.8 1.5 True", "position 1 4,1,-2", "position 1 5,1,-2", "volume 1 1", "pitch 1 0.5",
                    // A system with no clock of its own makes a fade at once, and stops when asked to.
                    "volume 1 0.25", "volume 1 0", "stop 1", "bus music 0.4", "channel 1 on sfx",
                ];
                HeadlessHarness.Assert(channel == 1 && recorder.Calls.SequenceEqual(expected),
                    "The sound commands asked for: " + string.Join(" | ", recorder.Calls));
                HeadlessHarness.Assert(!Genesis.Runtime.Scripting.PgslCommands.AudioListenerManual, "The listener should follow the camera until a script places it.");
                Genesis.Runtime.Scripting.PgslCommands.SetAudioListener(1, 2, 3);
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.AudioListenerManual && recorder.Calls[^1] == "listener 1,2,3",
                    "Placing the listener from a script did not take it from the camera.");
                Genesis.Runtime.Scripting.PgslCommands.AudioListenerFollowCamera();
                HeadlessHarness.Assert(!Genesis.Runtime.Scripting.PgslCommands.AudioListenerManual, "The listener could not be given back to the camera.");
            }
            finally
            {
                Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext = oldGame;
                Genesis.Runtime.Scripting.PgslCommands.AudioListenerManual = oldManual;
            }

            // The real mixer, with a silent sound so the check makes no noise.
            string project = Path.Combine(context.Workspace, "PositionalAudio");
            Directory.CreateDirectory(project);
            string wave = Path.Combine(project, "Quiet.wav");
            WriteSilentWave(wave, 1.0);
            using var audio = new Genesis.Audio.XAudioSystem(project);
            int sound = audio.LoadSound(wave);
            HeadlessHarness.Assert(sound != 0, "The mixer could not load a plain WAV file.");
            AudioChannel live = audio.PlayAt(sound, new Vector3(10, 0, 0), 1f, 1f, loop: true);
            HeadlessHarness.Assert(live.IsValid && audio.IsPlaying(live), "A positioned sound did not start.");
            (float Left, float Right) Pan()
            {
                var channels = (System.Collections.IDictionary)typeof(Genesis.Audio.XAudioSystem)
                    .GetField("_channels", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(audio)!;
                object state = channels[live.Id]!;
                return ((float)state.GetType().GetField("PanLeft")!.GetValue(state)!, (float)state.GetType().GetField("PanRight")!.GetValue(state)!);
            }

            audio.SetListener(Vector3.Zero, Vector3.UnitZ, Vector3.UnitX);
            var heard = Pan();
            HeadlessHarness.Assert(heard.Right == 1f && MathF.Abs(heard.Left - AudioPanning.FarEarLevel) < 1e-3f,
                $"The mixer did not put a sound on the listener's right into the right ear ({heard}).");
            audio.SetListener(Vector3.Zero, -Vector3.UnitZ, -Vector3.UnitX);
            heard = Pan();
            HeadlessHarness.Assert(heard.Left == 1f && MathF.Abs(heard.Right - AudioPanning.FarEarLevel) < 1e-3f,
                $"Turning round did not move the sound to the left ear ({heard}).");
            audio.SetBusVolume("music", 0.4f);
            HeadlessHarness.Assert(audio.GetBusVolume("music") == 0.4f && audio.GetBusVolume("sfx") == 1f, "A bus volume did not read back.");

            // The caller may say which group a sound is in, and a muted device stays muted whatever the volume is set to.
            string Bus()
            {
                var channels = (System.Collections.IDictionary)typeof(Genesis.Audio.XAudioSystem)
                    .GetField("_channels", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(audio)!;
                object state = channels[live.Id]!;
                return (string)state.GetType().GetField("Bus")!.GetValue(state)!;
            }

            HeadlessHarness.Assert(Bus() == "sfx", $"A one-second sound should start in the effects group, not '{Bus()}'.");
            audio.SetChannelBus(live, "Music");
            HeadlessHarness.Assert(Bus() == "music", "A playing sound could not be moved to the music group.");
            audio.SetChannelBus(live, "sfx");
            audio.Muted = true;
            audio.MasterVolume = 0.8f;
            var engine = (Genesis.Audio.AudioEngine)typeof(Genesis.Audio.XAudioSystem)
                .GetField("_engine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(audio)!;
            HeadlessHarness.Assert(audio.MasterVolume == 0.8f && engine.MasterVolume == 0f,
                $"A muted device should stay silent while the game's own volume reads back ({audio.MasterVolume}, device {engine.MasterVolume}).");
            audio.Muted = false;
            HeadlessHarness.Assert(engine.MasterVolume == 0.8f, "Unmuting did not give the device back the game's volume.");

            // A fade to silence that ends by stopping the sound.
            audio.FadeChannel(live, 0f, 0.15f, stopWhenDone: true);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool stillAt60 = false;
            while (audio.IsPlaying(live) && watch.Elapsed.TotalSeconds < 3)
            {
                audio.Update();
                if (watch.Elapsed.TotalMilliseconds < 60) stillAt60 = audio.IsPlaying(live);
                Thread.Sleep(5);
            }

            HeadlessHarness.Assert(stillAt60 && !audio.IsPlaying(live),
                $"A 0.15 s fade-out should still be playing at 0.06 s and have stopped the sound afterwards (playing early: {stillAt60}, playing now: {audio.IsPlaying(live)}).");
        });
    }

    /// <summary>A four-bone figure: a root, a spine with an arm on it, and a leg. It walks with the leg and strikes with the arm.</summary>
    private static Genesis.Runtime.Modeling.GModelAsset Figure()
    {
        var model = new Genesis.Runtime.Modeling.GModelAsset { Name = "Figure" };
        Matrix4x4 root = Matrix4x4.Identity, spine = Matrix4x4.CreateTranslation(0, 1, 0),
            arm = Matrix4x4.CreateTranslation(0.5f, 0, 0), leg = Matrix4x4.CreateTranslation(0, -0.5f, 0);
        model.Rig = new Genesis.Runtime.Modeling.GModelRig
        {
            Bones =
            [
                new() { Name = "Root", ParentIndex = -1, BindLocal = root },
                new() { Name = "Spine", ParentIndex = 0, BindLocal = spine },
                new() { Name = "Arm", ParentIndex = 1, BindLocal = arm },
                new() { Name = "Leg", ParentIndex = 0, BindLocal = leg },
            ],
            InverseBindMatrices = [Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity],
        };
        var walk = new Genesis.Runtime.Modeling.GModelAnimationClip { Name = "Walk", Fps = 30f };
        var strike = new Genesis.Runtime.Modeling.GModelAnimationClip { Name = "Strike", Fps = 30f };
        for (int frame = 0; frame < 30; frame++)
        {
            // Walking moves the leg forward a centimetre a frame; striking raises the arm two.
            walk.Frames.Add(new() { LocalBoneTransforms = [root, spine, arm, Matrix4x4.CreateTranslation(0, -0.5f, 0.01f * frame)] });
            strike.Frames.Add(new() { LocalBoneTransforms = [root, spine, Matrix4x4.CreateTranslation(0.5f, 0.02f * frame, 0), leg] });
        }

        model.Animations.Add(walk);
        model.Animations.Add(strike);
        model.Sockets.Add(new Genesis.Runtime.Modeling.GModelSocket { Name = "Hand", BoneIndex = 2, LocalTransform = Matrix4x4.CreateTranslation(0, 0, 0.25f) });
        return model;
    }

    private static void RunModelInstances(HeadlessContext context)
    {
        HeadlessHarness.RunCase(context.Report, "Engine.Models.Instance.ScriptsFindBonesAndPlayAClipOnPartOfTheBody", () =>
        {
            string project = Path.Combine(context.Workspace, "ModelInstanceProject");
            Directory.CreateDirectory(Path.Combine(project, "Models"));
            Genesis.Runtime.Modeling.RuntimeModelStore.Save(Path.Combine(project, "Models", "Figure.gmodel"), Figure());

            using var scene = new RuntimeScene("Figure");
            var world = scene.World;
            var game = new ProjectGameContext(project, scene, null, null, RoomAsset.Create("Hall", RoomDimension.ThreeD), null);
            string oldProject = Genesis.Runtime.Scripting.PgslCommands.ProjectPath;
            var oldGame = Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext;
            Genesis.Shared.Scripting.PgslContext? oldContext = null;
            try
            {
                Genesis.Runtime.Scripting.PgslCommands.ProjectPath = project;
                Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext = game;
                Entity figure = world.CreateEntity();
                world.Set(figure, new TransformComponent { X = 10, Y = 0, Z = 5, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                world.Set(figure, new ModelRendererComponent { ModelAsset = "Figure", ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                oldContext = Genesis.Runtime.Scripting.PgslCommands.BindContext(new Genesis.Shared.Scripting.PgslContext { InstanceId = figure.Id });

                Vector3 Where(string name)
                {
                    HeadlessHarness.Assert(Genesis.Runtime.Modeling.ModelInstance.TryGetSocketPosition(world, figure, name, out Vector3 position),
                        $"The figure's '{name}' could not be found.");
                    return position;
                }

                void Near(Vector3 actual, Vector3 expected, string what) =>
                    HeadlessHarness.Assert(Vector3.Distance(actual, expected) < 1e-3f, $"{what}: at {actual}, expected {expected}.");

                void Step(float seconds) => Genesis.Runtime.ECS.ComponentLifecycle.OnUpdate(world, figure, seconds);

                // At rest: a socket is its bone's place plus its own offset; a bone answers to its name; nothing else does.
                Near(Where("Hand"), new Vector3(10.5f, 1f, 5.25f), "The hand socket at rest");
                Near(Where("arm"), new Vector3(10.5f, 1f, 5f), "The arm bone at rest");
                HeadlessHarness.Assert(!Genesis.Runtime.Modeling.ModelInstance.TryGetSocketWorld(world, figure, "Tail", out _)
                    && game is Genesis.Runtime.Scripting.IGameContext scripts && scripts.TryGetSocketWorld(figure, "Hand", out Matrix4x4 viaGame)
                    && Vector3.Distance(viaGame.Translation, new Vector3(10.5f, 1f, 5.25f)) < 1e-3f,
                    "A name the model lacks was found, or the game context did not give the same answer.");
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.ModelSocketExists("Hand")
                    && Math.Abs(Genesis.Runtime.Scripting.PgslCommands.ModelSocketX("Hand") - 10.5) < 1e-3
                    && Math.Abs(Genesis.Runtime.Scripting.PgslCommands.ModelSocketZ("Hand") - 5.25) < 1e-3
                    && !Genesis.Runtime.Scripting.PgslCommands.ModelSocketExists("Tail"),
                    "The socket commands did not give the hand's place.");

                // A clip started part of the way in, played at the rate it was authored at.
                HeadlessHarness.Assert(!Genesis.Runtime.Modeling.ModelInstance.Play(world, figure, "Dance")
                    && Genesis.Runtime.Modeling.ModelInstance.Play(world, figure, "Walk", loop: true, blendSeconds: 0f, startSeconds: 0.5f)
                    && MathF.Abs(Genesis.Runtime.Modeling.ModelInstance.ClipLength(world, figure, "Walk") - 1f) < 1e-4f,
                    "Playing a clip by name did not behave: a missing clip must fail and a 30-frame clip at 30 a second is one second long.");
                Near(Where("Leg"), new Vector3(10f, -0.5f, 5.15f), "The leg half a second into the walk");

                // The arm strikes while the leg keeps walking.
                HeadlessHarness.Assert(!Genesis.Runtime.Modeling.ModelInstance.PlayLayer(world, figure, "Strike", "Wing")
                    && Genesis.Runtime.Modeling.ModelInstance.PlayLayer(world, figure, "Strike", "Spine", loop: false, fadeSeconds: 0f),
                    "A clip on part of the body must name a bone the model has.");
                Step(0.25f);
                Near(Where("Leg"), new Vector3(10f, -0.5f, 5.22f), "The leg, still walking under the strike");
                Near(Where("Arm"), new Vector3(10.5f, 1.14f, 5f), "The arm a quarter of a second into the strike");
                Near(Where("Hand"), new Vector3(10.5f, 1.14f, 5.25f), "The hand socket, carried by the striking arm");
                HeadlessHarness.Assert(Genesis.Runtime.Modeling.ModelInstance.LayerCrossed(world, figure, 0.2f)
                    && !Genesis.Runtime.Modeling.ModelInstance.LayerCrossed(world, figure, 0.5f)
                    && Genesis.Runtime.Modeling.ModelInstance.Crossed(world, figure, 0.6f)
                    && !Genesis.Runtime.Modeling.ModelInstance.Crossed(world, figure, 0.4f),
                    "The points each clip passed in that quarter second were not reported.");

                // The walk's mark comes round once a lap; the strike ends and lets go by itself.
                int laps = 0;
                for (int i = 0; i < 8; i++)
                {
                    Step(0.25f);
                    if (Genesis.Runtime.Modeling.ModelInstance.Crossed(world, figure, 0.6f)) laps++;
                }

                HeadlessHarness.Assert(laps == 2, $"A looping walk passed its mark {laps} times in two more seconds; it should pass twice.");
                HeadlessHarness.Assert(!Genesis.Runtime.Modeling.ModelInstance.LayerPlaying(world, figure), "A finished strike still had hold of the arm.");
                Near(Where("Arm"), new Vector3(10.5f, 1f, 5f), "The arm after the strike has let go");

                // Fading in: half way through a 0.2 s fade the arm is half way to the strike's pose.
                Genesis.Runtime.Modeling.ModelInstance.PlayLayer(world, figure, "Strike", "Spine", loop: false, fadeSeconds: 0.2f, startSeconds: 0.5f);
                Step(0.1f);
                float armHeight = Where("Arm").Y;
                HeadlessHarness.Assert(armHeight > 1.14f && armHeight < 1.22f,
                    $"Half faded in, the arm should be about half way to the strike's 1.36 ({armHeight:F3}).");
                Genesis.Runtime.Modeling.ModelInstance.StopLayer(world, figure, 0f);
                HeadlessHarness.Assert(!Genesis.Runtime.Modeling.ModelInstance.LayerPlaying(world, figure), "Stopping a part-body clip at once left it playing.");

                // Backwards: from the end to the start, passing its marks on the way and stopping there.
                HeadlessHarness.Assert(Genesis.Runtime.Modeling.ModelInstance.PlayReversed(world, figure, "Strike", blendSeconds: 0f),
                    "A clip could not be played backwards.");
                Near(Where("Arm"), new Vector3(10.5f, 1.58f, 5f), "The arm at the end of the strike, where a reversed clip starts");
                Step(0.5f);
                HeadlessHarness.Assert(Genesis.Runtime.Modeling.ModelInstance.Crossed(world, figure, 0.75f)
                    && !Genesis.Runtime.Modeling.ModelInstance.Crossed(world, figure, 0.25f)
                    && !Genesis.Runtime.Modeling.ModelInstance.Finished(world, figure),
                    "A clip played backwards did not report the mark it passed, or said it had finished half way.");
                Step(0.75f);
                HeadlessHarness.Assert(Genesis.Runtime.Modeling.ModelInstance.Finished(world, figure)
                    && world.GetRef<ModelAnimatorComponent>(figure).TimeSeconds == 0f,
                    "A clip played backwards should stop at its start.");
                Near(Where("Arm"), new Vector3(10.5f, 1f, 5f), "The arm back at the start of the strike");
            }
            finally
            {
                if (oldContext != null || Genesis.Runtime.Scripting.PgslCommands.BindContext(null) != null) Genesis.Runtime.Scripting.PgslCommands.BindContext(oldContext);
                Genesis.Runtime.Scripting.PgslCommands.ProjectPath = oldProject;
                Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext = oldGame;
            }
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Models.Instance.AScriptTintsAModelAndLightsItsMaterials", () =>
        {
            string project = Path.Combine(context.Workspace, "ModelTintProject");
            Directory.CreateDirectory(Path.Combine(project, "Models"));
            Genesis.Runtime.Modeling.GModelAsset lamp = Genesis.Runtime.Modeling.GModelPrimitiveFactory.CreateCube("Lamp", 1f);
            lamp.Materials[0].Name = "Glass";
            lamp.Materials[0].BaseColor = new Vector4(0.5f, 0.5f, 0.5f, 1f);
            Genesis.Runtime.Modeling.RuntimeModelStore.Save(Path.Combine(project, "Models", "Lamp.gmodel"), lamp);

            using var scene = new RuntimeScene("Lamp");
            var world = scene.World;
            string oldProject = Genesis.Runtime.Scripting.PgslCommands.ProjectPath;
            using System.Windows.Forms.Form host = UnattendedWindowing.NewHost(320, 240);
            UnattendedWindowing.ShowWithoutFocus(host);
            using IRenderController renderer = Genesis.Rendering.Core.RenderControllerFactory.Create(Genesis.Rendering.Core.RenderBackendOption.Software);
            renderer.Initialize(host.Handle, 320, 240);
            try
            {
                Genesis.Runtime.Scripting.PgslCommands.ProjectPath = project;
                Entity entity = world.CreateEntity();
                world.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                world.Set(entity, new ModelRendererComponent { ModelAsset = "Lamp", ScaleX = 1, ScaleY = 1, ScaleZ = 1, CastShadows = true, ReceiveShadows = true });
                var models = new Genesis.Runtime.Modeling.RuntimeModelRenderSystem();

                MeshDrawCall Drawn()
                {
                    var queue = new Genesis.Runtime.Modeling.ModelRenderQueue();
                    HeadlessHarness.Assert(models.Enqueue(queue, project, "Lamp", "", Matrix4x4.Identity,
                        new Draw3DComponent { Visible = true, CastShadows = true, ReceiveShadows = true },
                        world.GetRef<ModelRendererComponent>(entity), default, renderer) && queue.Count == 1, "The lamp was not drawn as one mesh.");
                    var buffer = new MeshDrawCall[1];
                    queue.CopyTo(buffer, 0);
                    return buffer[0];
                }

                MeshDrawCall plain = Drawn();
                HeadlessHarness.Assert(MathF.Abs(plain.Tint.R - 0.5f) < 1e-4f && plain.Alpha == 1f && plain.Emissive == 0f
                    && (plain.Flags & (MeshDrawFlags.Emissive | MeshDrawFlags.NoShadow | MeshDrawFlags.Transparent)) == 0,
                    "An untouched model should be drawn in its authored colour, unlit by itself and solid.");

                // A tint multiplies the colour; an alpha below one draws it through.
                Genesis.Runtime.Modeling.ModelInstance.SetTint(world, entity, new Vector4(1f, 0.5f, 0.25f, 1f));
                MeshDrawCall tinted = Drawn();
                HeadlessHarness.Assert(MathF.Abs(tinted.Tint.R - 0.5f) < 1e-4f && MathF.Abs(tinted.Tint.G - 0.25f) < 1e-4f
                    && MathF.Abs(tinted.Tint.B - 0.125f) < 1e-4f && (tinted.Flags & MeshDrawFlags.Transparent) == 0,
                    $"A tint should multiply the model's colour ({tinted.Tint.R}, {tinted.Tint.G}, {tinted.Tint.B}).");
                Genesis.Runtime.Modeling.ModelInstance.SetTint(world, entity, new Vector4(1f, 1f, 1f, 0.4f));
                MeshDrawCall faded = Drawn();
                HeadlessHarness.Assert(MathF.Abs(faded.Alpha - 0.4f) < 1e-4f && (faded.Flags & MeshDrawFlags.Transparent) != 0,
                    "A tint with alpha below one should fade the model.");
                Genesis.Runtime.Modeling.ModelInstance.SetTint(world, entity, Vector4.One);
                HeadlessHarness.Assert(world.GetRef<ModelRendererComponent>(entity).Tint == null, "A tint of one should be no tint at all.");

                // A glow lights the whole model and leaves its shadow.
                Genesis.Runtime.Modeling.ModelInstance.SetGlow(world, entity, 0.8f);
                MeshDrawCall glowing = Drawn();
                HeadlessHarness.Assert(MathF.Abs(glowing.Emissive - 0.8f) < 1e-4f && (glowing.Flags & MeshDrawFlags.Emissive) != 0
                    && (glowing.Flags & MeshDrawFlags.NoShadow) == 0, "A glow should light the model and keep its shadow.");
                Genesis.Runtime.Modeling.ModelInstance.SetGlow(world, entity, 0f);

                // A material given light of its own, then every material scaled, down to none.
                HeadlessHarness.Assert(!Genesis.Runtime.Modeling.ModelInstance.SetMaterialEmission(world, entity, "Brass", 1f)
                    && Genesis.Runtime.Modeling.ModelInstance.SetMaterialEmission(world, entity, "glass", 2f),
                    "A material's light is set by the material's name.");
                MeshDrawCall lit = Drawn();
                HeadlessHarness.Assert(MathF.Abs(lit.Emissive - 2f) < 1e-4f && MathF.Abs(lit.SurfaceParams.Z - 2f) < 1e-4f
                    && (lit.Flags & MeshDrawFlags.Emissive) != 0, $"The glass should give off light of strength 2 ({lit.Emissive}).");
                Genesis.Runtime.Modeling.ModelInstance.SetEmissionScale(world, entity, 0.25f);
                HeadlessHarness.Assert(MathF.Abs(Drawn().Emissive - 0.5f) < 1e-4f, "Scaling a model's light by a quarter should leave a quarter of it.");
                Genesis.Runtime.Modeling.ModelInstance.SetEmissionScale(world, entity, 0f);
                MeshDrawCall dark = Drawn();
                HeadlessHarness.Assert(dark.Emissive == 0f && (dark.Flags & (MeshDrawFlags.Emissive | MeshDrawFlags.NoShadow)) == 0,
                    "A model whose light is scaled to nothing should be drawn as an ordinary solid again.");
                Genesis.Runtime.Modeling.ModelInstance.SetEmissionScale(world, entity, 1f);
                Genesis.Runtime.Modeling.ModelInstance.SetMaterialEmission(world, entity, "Glass", -1f);
                HeadlessHarness.Assert(Drawn().Emissive == 0f && world.GetRef<ModelRendererComponent>(entity).EmissionScale == null,
                    "Giving a material back its authored light did not.");

                // Light in a colour: the material glows in its own colour multiplied by the one given, and is given both back together.
                RenderColor plainGlass = Drawn().Tint;
                HeadlessHarness.Assert(Genesis.Runtime.Modeling.ModelInstance.SetMaterialEmission(world, entity, "Glass", 3f, new Vector3(1f, 0.5f, 0.25f))
                    && !Genesis.Runtime.Modeling.ModelInstance.SetMaterialEmission(world, entity, "Brass", 3f, Vector3.One),
                    "Coloured light is set by the material's name.");
                MeshDrawCall warm = Drawn();
                HeadlessHarness.Assert(MathF.Abs(warm.Emissive - 3f) < 1e-4f && MathF.Abs(warm.Tint.R - plainGlass.R) < 1e-4f
                    && MathF.Abs(warm.Tint.G - plainGlass.G * 0.5f) < 1e-4f && MathF.Abs(warm.Tint.B - plainGlass.B * 0.25f) < 1e-4f,
                    $"The glass should glow at strength 3 in a warm colour; it is drawn at {warm.Emissive} tinted {warm.Tint.R:F2},{warm.Tint.G:F2},{warm.Tint.B:F2}.");
                Genesis.Runtime.Modeling.ModelInstance.SetMaterialEmission(world, entity, "Glass", -1f, Vector3.One);
                MeshDrawCall again = Drawn();
                HeadlessHarness.Assert(again.Emissive == 0f && MathF.Abs(again.Tint.G - plainGlass.G) < 1e-4f && MathF.Abs(again.Tint.B - plainGlass.B) < 1e-4f,
                    "Giving a material back its light did not give back its colour.");
            }
            finally
            {
                Genesis.Runtime.Scripting.PgslCommands.ProjectPath = oldProject;
            }
        });
    }

    /// <summary>Keeps the rectangles and lines a HUD shape is made of.</summary>
    private sealed class ShapeRecorder : Genesis.Runtime.Scripting.IHudCanvas
    {
        public readonly List<(float X, float Y, float W, float H)> Rects = new();
        public readonly List<(Vector2 From, Vector2 To)> Lines = new();
        public int Width => 640;
        public int Height => 360;
        public void Text(string text, float x, float y, float size, Vector4 color) { }
        public void TextCentered(string text, float centerX, float y, float width, float size, Vector4 color) { }
        public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true) => Rects.Add((x, y, w, h));
        public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f) =>
            Lines.Add((new Vector2(x1, y1), new Vector2(x2, y2)));
    }

    private static void RunHudParticlesAndSaves(HeadlessContext context)
    {
        HeadlessHarness.RunCase(context.Report, "Engine.Hud.Canvas.MeasuresTextAndDrawsCirclesArcsAndPolygons", () =>
        {
            var recorder = new ShapeRecorder();
            Genesis.Runtime.Scripting.IHudCanvas hud = recorder;

            // Text: capitals are wider than thin letters, twice the size is twice the width, and nothing is no width.
            Vector2 wide = hud.MeasureText("WWWW", 16f), thin = hud.MeasureText("iiii", 16f), large = hud.MeasureText("WWWW", 32f);
            HeadlessHarness.Assert(wide.X > thin.X * 2f && wide.Y > 12f && wide.Y < 32f,
                $"Four capital Ws should be far wider than four small is at one height ({wide} against {thin}).");
            HeadlessHarness.Assert(MathF.Abs(large.X / wide.X - 2f) < 0.15f, $"Text at twice the size should be about twice as wide ({wide.X} then {large.X}).");
            HeadlessHarness.Assert(hud.MeasureText("", 16f).X == 0f && hud.MeasureText(null!, 16f).Y > 0f, "Empty text should have no width and still a line height.");

            // A disc: one row of pixels each, as wide as the circle is at that row, the same above and below.
            hud.Circle(50f, 50f, 10f, Vector4.One);
            HeadlessHarness.Assert(recorder.Rects.Count == 20 && recorder.Rects.All(row => row.H == 1f), $"A disc of radius 10 should be 20 rows ({recorder.Rects.Count}).");
            float area = recorder.Rects.Sum(row => row.W);
            HeadlessHarness.Assert(MathF.Abs(area - MathF.PI * 100f) < 6f, $"A disc of radius 10 covers about 314 pixels; it drew {area:F0}.");
            HeadlessHarness.Assert(MathF.Abs(recorder.Rects[0].W - recorder.Rects[19].W) < 1e-3f && recorder.Rects.All(row => MathF.Abs(row.X + row.W / 2f - 50f) < 1e-3f),
                "A disc should be the same top and bottom and centred on its centre.");

            // A ring is a closed run of lines on the circle; a quarter arc is a quarter of it.
            recorder.Rects.Clear();
            hud.Circle(100f, 100f, 40f, Vector4.One, filled: false, thickness: 2f);
            HeadlessHarness.Assert(recorder.Rects.Count == 0 && recorder.Lines.Count >= 24
                && Vector2.Distance(recorder.Lines[0].From, recorder.Lines[^1].To) < 0.01f
                && recorder.Lines.All(line => MathF.Abs(Vector2.Distance(line.From, new Vector2(100f, 100f)) - 40f) < 0.01f),
                $"A ring should be a closed loop of lines on the circle ({recorder.Lines.Count} lines).");
            int ring = recorder.Lines.Count;
            recorder.Lines.Clear();
            hud.Arc(100f, 100f, 40f, -90f, 90f, Vector4.One);
            HeadlessHarness.Assert(recorder.Lines.Count >= 3 && recorder.Lines.Count <= ring / 4 + 1
                && Vector2.Distance(recorder.Lines[0].From, new Vector2(100f, 60f)) < 0.01f
                && Vector2.Distance(recorder.Lines[^1].To, new Vector2(140f, 100f)) < 0.01f,
                "A quarter arc from twelve o'clock should end at three o'clock.");

            // A filled triangle covers its area; an outline joins back to the start.
            recorder.Lines.Clear();
            Vector2[] triangle = [new(10f, 10f), new(110f, 10f), new(10f, 60f)];
            hud.Polygon(triangle, Vector4.One);
            float triangleArea = recorder.Rects.Sum(row => row.W);
            HeadlessHarness.Assert(MathF.Abs(triangleArea - 2500f) < 30f && recorder.Rects.Count == 50,
                $"A triangle 100 wide and 50 tall covers 2500 pixels in 50 rows; it drew {triangleArea:F0} in {recorder.Rects.Count}.");
            hud.Polygon(triangle, Vector4.One, filled: false);
            HeadlessHarness.Assert(recorder.Lines.Count == 3 && recorder.Lines[^1].To == triangle[0], "A polygon's outline should join back to its first point.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Sky.Sun.IsARoundDiscOfOneSizeAnywhereInTheView", () =>
        {
            // A wide view: 100 degrees up and down, 16 by 9, looking along +Z from a hilltop.
            Vector3 camera = new(40f, 12f, -7f), forward = Vector3.UnitZ;
            Matrix4x4 view = Matrix4x4.CreateLookAt(camera, camera + forward, Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(100f * MathF.PI / 180f, 16f / 9f, 0.1f, 2000f);
            Vector2 OnScreen(Matrix4x4 world, float x, float y)
            {
                Vector4 clip = Vector4.Transform(new Vector4(x, y, 0f, 1f), world * view * projection);
                return new Vector2(clip.X / clip.W * 960f, clip.Y / clip.W * 540f);
            }

            (float Across, float Along) Size(Vector3 towardSun, float size = 14f)
            {
                Matrix4x4 world = Genesis.Rendering.Primitives.SkyBillboardMath.SunQuad(camera, forward, towardSun, 450f, size);
                return (Vector2.Distance(OnScreen(world, -0.5f, 0f), OnScreen(world, 0.5f, 0f)),
                    Vector2.Distance(OnScreen(world, 0f, -0.5f), OnScreen(world, 0f, 0.5f)));
            }

            var centre = Size(forward);
            HeadlessHarness.Assert(centre.Across > 5f && MathF.Abs(centre.Across - centre.Along) < centre.Across * 0.01f,
                $"In the middle of the view the sun should be as tall as it is wide ({centre.Across:F1} by {centre.Along:F1} pixels).");
            foreach ((float right, float up) in new[] { (25f, 0f), (45f, 0f), (0f, 40f), (38f, 30f), (-50f, 20f) })
            {
                // A direction that many degrees to the side and above the view direction.
                Vector3 towardSun = Vector3.Normalize(new Vector3(MathF.Tan(right * MathF.PI / 180f), MathF.Tan(up * MathF.PI / 180f), 1f));
                var off = Size(towardSun);
                HeadlessHarness.Assert(MathF.Abs(off.Across - off.Along) < off.Across * 0.03f,
                    $"At {right:F0} degrees right and {up:F0} up the sun is {off.Across:F1} by {off.Along:F1} pixels: it should be round.");
                HeadlessHarness.Assert(MathF.Abs(off.Across - centre.Across) < centre.Across * 0.03f,
                    $"At {right:F0} degrees right and {up:F0} up the sun is {off.Across:F1} pixels across; in the middle it is {centre.Across:F1}.");
            }

            // A plain camera-facing quad, as it was drawn before, is what this corrects.
            Vector3 aside = Vector3.Normalize(new Vector3(1f, 0f, 1f));
            Matrix4x4 facing = Matrix4x4.CreateScale(14f) * Matrix4x4.CreateBillboard(camera + aside * 450f, camera, Vector3.UnitY, forward);
            float facingAcross = Vector2.Distance(OnScreen(facing, -0.5f, 0f), OnScreen(facing, 0.5f, 0f));
            float facingAlong = Vector2.Distance(OnScreen(facing, 0f, -0.5f), OnScreen(facing, 0f, 0.5f));
            HeadlessHarness.Assert(facingAcross > facingAlong * 1.3f,
                $"The check itself is wrong: an uncorrected quad 45 degrees aside should be stretched ({facingAcross:F1} by {facingAlong:F1}).");

            // The room's sun size makes it that much larger, and the setting is kept with the room.
            var doubled = Size(forward, 28f);
            HeadlessHarness.Assert(MathF.Abs(doubled.Across / centre.Across - 2f) < 0.02f, "Twice the size should be twice as wide.");
            RoomAsset room = RoomAsset.Create("Sunny", RoomDimension.ThreeD);
            room.Environment.SunDiscScale = 2.5f;
            string roomFile = Path.Combine(context.Workspace, "Sunny.room.json");
            File.WriteAllText(roomFile, Newtonsoft.Json.JsonConvert.SerializeObject(room));
            RoomAsset reopened = RoomAssetLoader.Parse(roomFile);
            HeadlessHarness.Assert(reopened.Environment.SunDiscScale == 2.5f && RoomAsset.Create("Plain", RoomDimension.ThreeD).Environment.SunDiscScale == 1f,
                "A room's sun size was not saved with it, or does not start at 1.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Particles.Bursts.PlayOnceAndRemoveThemselves", () =>
        {
            using var scene = new RuntimeScene("Sparks");
            var composition = new Genesis.Runtime.Rendering.ObjectCompositionSubsystem(context.Workspace);
            void Run(float seconds)
            {
                for (float done = 0f; done < seconds - 1e-4f; done += 0.05f)
                {
                    scene.GameTime.Advance(0.05f);
                    composition.Update(scene, scene.GameTime);
                    scene.World.FlushDeferred();
                }
            }

            try
            {
                // A single burst: gone once its last particle has lived its life, and not before.
                Entity burst = ParticleBursts.Play(scene.World, "builtin://Explosion", new Vector3(3f, 1f, 2f), 2f);
                HeadlessHarness.Assert(!burst.IsNull && scene.World.GetRef<ParticleComponent>(burst).RemoveWhenDone
                    && scene.World.GetRef<TransformComponent>(burst).X == 3f && scene.World.GetRef<TransformComponent>(burst).ScaleX == 2f,
                    "A burst was not made where and as large as it was asked for.");
                ParticleConfig explosion = ParticlePresets.Explosion();
                float life = (float)(explosion.Lifetime * (1.0 + explosion.LifetimeVariance));
                Run(MathF.Max(0.1f, life * 0.5f));
                HeadlessHarness.Assert(scene.World.IsAlive(burst), "A burst was removed while its particles were still alive.");
                HeadlessHarness.Assert(composition.ParticleEmitterCount == 1, $"One burst should be one emitter ({composition.ParticleEmitterCount}).");
                Run(life + 1f);
                HeadlessHarness.Assert(!scene.World.IsAlive(burst) && composition.ParticleEmitterCount == 0,
                    $"A burst whose particles live {life:F1} s was still there {life * 1.5f + 1f:F1} s later.");

                // An effect that emits continuously is given its time, stopped, and then removed.
                Entity shower = ParticleBursts.Play(scene.World, "builtin://Rain", Vector3.Zero, 1f, emitSeconds: 0.5f);
                Run(0.6f);
                ref ParticleComponent raining = ref scene.World.GetRef<ParticleComponent>(shower);
                HeadlessHarness.Assert(scene.World.IsAlive(shower) && raining.HasEmitRateOverride && raining.EmitRate == 0f,
                    "A continuous effect played as a burst should stop emitting after its time and still be there to die away.");
                ParticleConfig rain = ParticlePresets.Rain();
                Run((float)(rain.Lifetime * (1.0 + rain.LifetimeVariance)) + 1f);
                HeadlessHarness.Assert(!scene.World.IsAlive(shower), "A continuous effect played as a burst was never removed.");

                // An emitter that is kept can fire its burst again; an unknown effect makes nothing.
                Entity kept = scene.World.CreateEntity();
                scene.World.Set(kept, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                scene.World.Set(kept, new ParticleComponent { Asset = "builtin://Explosion", ParticleTypeId = -1, RateScale = 1f, FollowEntity = true, Emitting = true });
                Run(1f);
                scene.World.GetRef<ParticleComponent>(kept).Restart = true;
                Run(0.05f);
                ref ParticleComponent again = ref scene.World.GetRef<ParticleComponent>(kept);
                HeadlessHarness.Assert(scene.World.IsAlive(kept) && !again.Restart && again.Age < 0.11f,
                    $"Asking an emitter to fire again should start it over (age {again.Age:F2}, still asking: {again.Restart}).");
                HeadlessHarness.Assert(ParticleBursts.Play(scene.World, "", Vector3.Zero).IsNull, "A burst with no effect named made something.");
            }
            finally
            {
                composition.Dispose();
            }
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Save.Slots.ARichSaveIsWrittenReadListedAndRemoved", () =>
        {
            string project = Path.Combine(context.Workspace, "SaveSlotProject");
            Directory.CreateDirectory(project);
            string oldProject = Genesis.Runtime.Scripting.PgslCommands.ProjectPath;
            string folder = ProjectNumberSave.GetWritableDirectory(project);
            try
            {
                Genesis.Runtime.Scripting.PgslCommands.ProjectPath = project;
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.SaveSlotCount() == 0 && !Genesis.Runtime.Scripting.PgslCommands.SaveSlotExists("Quick")
                    && Genesis.Runtime.Scripting.PgslCommands.SaveSlotRead("Quick") == "", "A game with no saves reported one.");

                string story = "{\"hero\":\"Æthelflæd\",\"gold\":412,\"inventory\":[\"sword\",\"ale\"],\"note\":\"line one\\nline two\"}";
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.SaveSlotWrite("Slot 1", story)
                    && Genesis.Runtime.Scripting.PgslCommands.SaveSlotLastError() == ""
                    && Genesis.Runtime.Scripting.PgslCommands.SaveSlotRead("Slot 1") == story,
                    "A save did not read back as it was written: " + Genesis.Runtime.Scripting.PgslCommands.SaveSlotLastError());
                HeadlessHarness.Assert(File.Exists(Path.Combine(folder, "Slots", "Slot 1.save")) && !Directory.EnumerateFiles(Path.Combine(folder, "Slots"), "*.tmp").Any(),
                    "The slot was not written where saves are kept, or a part-written file was left behind.");

                // A later save is listed first; writing a slot again replaces it.
                File.SetLastWriteTimeUtc(Path.Combine(folder, "Slots", "Slot 1.save"), DateTime.UtcNow.AddMinutes(-5));
                Genesis.Runtime.Scripting.PgslCommands.SaveSlotWrite("Quick", "first");
                Genesis.Runtime.Scripting.PgslCommands.SaveSlotWrite("Quick", "second");
                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.SaveSlotCount() == 2
                    && Genesis.Runtime.Scripting.PgslCommands.SaveSlotName(0) == "Quick" && Genesis.Runtime.Scripting.PgslCommands.SaveSlotName(1) == "Slot 1"
                    && Genesis.Runtime.Scripting.PgslCommands.SaveSlotName(2) == "" && Genesis.Runtime.Scripting.PgslCommands.SaveSlotRead("Quick") == "second",
                    "Two slots should be listed newest first, and a slot written twice should hold the second text.");

                // A name that would leave the save folder, or is not a name, is refused and says why.
                foreach (string bad in new[] { "..\\escape", "a/b", "", " padded ", new string('x', 49), "dot.name" })
                    HeadlessHarness.Assert(!Genesis.Runtime.Scripting.PgslCommands.SaveSlotWrite(bad, "x") && Genesis.Runtime.Scripting.PgslCommands.SaveSlotLastError().Length > 0
                        && !Genesis.Runtime.Scripting.PgslCommands.SaveSlotExists(bad), $"The slot name '{bad}' was accepted.");
                HeadlessHarness.Assert(Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length == 2, "A refused name still wrote a file.");

                HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.SaveSlotDelete("Quick") && !Genesis.Runtime.Scripting.PgslCommands.SaveSlotExists("Quick")
                    && Genesis.Runtime.Scripting.PgslCommands.SaveSlotCount() == 1 && Genesis.Runtime.Scripting.PgslCommands.SaveSlotDelete("Quick"),
                    "Removing a slot did not remove it, or removing it twice was an error.");
            }
            finally
            {
                Genesis.Runtime.Scripting.PgslCommands.ProjectPath = oldProject;
                try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { }
            }
        });
    }

    public static void Run(HeadlessContext context)
    {
        RunInputAndAudio(context);
        RunModelInstances(context);
        RunHudParticlesAndSaves(context);
        HeadlessHarness.RunCase(context.Report, "Engine.World.Weather.TheEngineDrawsRainSnowAndLightningWhenARoomAsks", () =>
        {
            RoomAsset room = RoomAsset.Create("Moor", RoomDimension.ThreeD);
            HeadlessHarness.Assert(!room.Environment.WeatherEffects && !RoomWeatherEffectsSubsystem.ShouldRegister(room),
                "A new room must not be given weather effects: a game that draws its own would get two.");
            room.Environment.DynamicSky = true;
            room.Environment.AutomaticWeather = false;
            room.Environment.Weather = "Thunderstorm";
            room.Environment.WeatherEffects = true;
            room.Environment.ThunderAudio = "Thunder Clap";
            room.Environment.SoundscapeLevels.Thunder = 0.5f;
            HeadlessHarness.Assert(RoomWeatherEffectsSubsystem.ShouldRegister(room), "A 3D room with a dynamic sky that asks for weather effects was refused them.");

            using var scene = new RuntimeScene("Weather");
            RoomSceneBuilder.ApplySceneSettings(scene, room);
            var audio = new ThunderRecorder();
            using var weather = new RoomWeatherEffectsSubsystem(room.Environment, audio);
            var time = new GameTime();
            float brightest = 0f;
            int framesLit = 0, frames = 0;
            void Run(float seconds)
            {
                for (float elapsed = 0f; elapsed < seconds; elapsed += 1f / 30f)
                {
                    time.Advance(1f / 30f);
                    scene.UpdateVariable(1f / 30f);
                    weather.Update(scene, time);
                    scene.World.FlushDeferred();
                    brightest = MathF.Max(brightest, weather.Flash);
                    frames++;
                    if (weather.Flash > 0.01f) framesLit++;
                    HeadlessHarness.Assert(scene.Environment.LightningFlash == weather.Flash, "The scene was not told about the flash.");
                }
            }

            // A storm: heavy rain falling, and lightning every few seconds with thunder after it.
            Run(60f);
            HeadlessHarness.Assert(weather.Precipitation == "builtin://Rain" && weather.PrecipitationRate > 1f,
                $"A thunderstorm should bring a downpour; the effect is '{weather.Precipitation}' at rate {weather.PrecipitationRate:F2}.");
            int emitters = 0;
            float emitterRate = 0f;
            scene.World.Query<TransformComponent, ParticleComponent>((Entity _, ref TransformComponent _, ref ParticleComponent particles) =>
            {
                emitters++;
                emitterRate = particles.RateScale;
                HeadlessHarness.Assert(particles.Asset == "builtin://Rain" && particles.Emitting, "The rain emitter is not emitting rain.");
            });
            HeadlessHarness.Assert(emitters == 1 && MathF.Abs(emitterRate - weather.PrecipitationRate) < 1e-4f,
                $"There should be one rain emitter at the weather's rate; there are {emitters} at {emitterRate:F2}.");

            // The rain goes with the wind: the way the climate says it is blowing, no faster than rain is carried.
            Vector3 localWind = scene.Climate.Current.LocalWind;
            var blowing = new Vector2(localWind.X, localWind.Z);
            HeadlessHarness.Assert(blowing.Length() > 0.5f, $"A thunderstorm with almost no wind ({blowing.Length():F2} m/s) cannot show that rain follows it.");
            Vector2 carried = weather.PrecipitationWind;
            HeadlessHarness.Assert(carried.Length() > 0.5f && carried.Length() <= RoomWeatherEffectsSubsystem.RainWindLimit + 1e-3f
                && Vector2.Dot(Vector2.Normalize(carried), Vector2.Normalize(blowing)) > 0.999f,
                $"The rain is carried at {carried.X:F2},{carried.Y:F2} m/s in a wind of {blowing.X:F2},{blowing.Y:F2} m/s.");
            Entity rainEmitter = Entity.Null;
            Vector2? onEmitter = null;
            scene.World.Query<ParticleComponent>((Entity entity, ref ParticleComponent particles) =>
            {
                rainEmitter = entity;
                onEmitter = particles.Wind;
            });
            HeadlessHarness.Assert(onEmitter == carried, "The rain emitter was not given the wind the rain is carried on.");

            // The effect drifts on that wind, and its drops start upwind of the camera so the shower stays around it.
            using (var effects = new Genesis.Runtime.Rendering.ObjectCompositionSubsystem(context.Workspace))
            {
                scene.Camera3D.Position = new Vector3(100f, 20f, -40f);
                effects.Update(scene, time);
                HeadlessHarness.Assert(effects.TryGetParticleFlow(rainEmitter, out Vector2 drift, out Vector3 origin)
                    && Vector2.Distance(drift, carried) < 1e-3f,
                    $"The rain effect drifts at {drift.X:F2},{drift.Y:F2} m/s; it was given {carried.X:F2},{carried.Y:F2}.");
                var upwind = new Vector2(100f - origin.X, -40f - origin.Z);
                HeadlessHarness.Assert(origin.Y > 20f && upwind.Length() > 0.3f && Vector2.Dot(Vector2.Normalize(upwind), Vector2.Normalize(carried)) > 0.999f,
                    $"The rain should start above and upwind of the camera; it starts {upwind.X:F2},{upwind.Y:F2} m from it at height {origin.Y:F1}.");

                // An emitter left alone keeps the wind its effect was made with.
                Entity plain = scene.World.CreateEntity();
                scene.World.Set(plain, new TransformComponent { ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f });
                scene.World.Set(plain, new ParticleComponent { Asset = "builtin://Rain", ParticleTypeId = -1, RateScale = 1f, FollowEntity = true, Emitting = true });
                effects.Update(scene, time);
                HeadlessHarness.Assert(effects.TryGetParticleFlow(plain, out Vector2 authored, out _)
                    && MathF.Abs(authored.X - (float)Genesis.Runtime.Particles.ParticlePresets.Rain().WindX) < 1e-4f && authored.Y == 0f,
                    $"An emitter with no wind of its own drifts at {authored.X:F2},{authored.Y:F2} m/s instead of the effect's authored wind.");
                scene.World.DestroyEntity(plain);
                scene.World.FlushDeferred();
            }
            HeadlessHarness.Assert(weather.Strikes is >= 3 and <= 14, $"A minute of thunderstorm struck {weather.Strikes} times; expected roughly one every five to thirteen seconds.");
            HeadlessHarness.Assert(brightest > 0.3f && framesLit > 0 && framesLit < frames / 4,
                $"Lightning should light a few frames brightly; it lit {framesLit} of {frames}, the brightest at {brightest:F2}.");

            // The flash lights the frame: more light from above, and a paler sky.
            var dark = new Mesh3DState { AmbientColor = new Vector3(0.05f), SkyHorizonColor = new Vector3(0.1f), SkyZenithColor = new Vector3(0.05f), BackgroundColor = new Vector3(0.1f) };
            Mesh3DState lit = dark;
            EnvironmentMapper.ApplyLightningFlash(ref lit, 1f);
            Mesh3DState untouched = dark;
            EnvironmentMapper.ApplyLightningFlash(ref untouched, 0f);
            HeadlessHarness.Assert(lit.AmbientColor.Y > dark.AmbientColor.Y + 1f && lit.SkyHorizonColor.Y > 0.5f && untouched.AmbientColor == dark.AmbientColor,
                "A lightning flash did not light the frame, or lit a frame with no flash.");

            // Thunder follows each strike, never before it, at the level the room set.
            Run(13f);
            HeadlessHarness.Assert(weather.Thunderclaps >= weather.Strikes - 2 && weather.Thunderclaps <= weather.Strikes
                && audio.Played.Count == weather.Thunderclaps,
                $"{weather.Strikes} strikes were followed by {weather.Thunderclaps} thunderclaps ({audio.Played.Count} played).");
            HeadlessHarness.Assert(audio.Played.All(clap => clap.Sound == "Thunder Clap" && clap.Volume > 0f && clap.Volume <= 0.5f),
                "Thunder was played with the wrong sound, or louder than the room's thunder level.");

            // The weather turns to snow, then clears: the effect follows, and the lightning stops.
            scene.Climate.SetWeather(WeatherKind.Snow, 0f);
            int strikes = weather.Strikes;
            Run(20f);
            HeadlessHarness.Assert(weather.Precipitation == "builtin://Snow" && weather.Strikes == strikes,
                $"Snow should replace the rain and end the lightning; the effect is '{weather.Precipitation}' after {weather.Strikes - strikes} more strikes.");
            HeadlessHarness.Assert(weather.PrecipitationWind.Length() <= RoomWeatherEffectsSubsystem.SnowWindLimit + 1e-3f,
                $"Snow is carried at {weather.PrecipitationWind.Length():F2} m/s; a flake is in the air too long to be carried faster than {RoomWeatherEffectsSubsystem.SnowWindLimit} m/s.");
            scene.Climate.SetWeather(WeatherKind.Clear, 0f);
            Run(2f);
            emitters = 0;
            scene.World.Query<ParticleComponent>((Entity _, ref ParticleComponent _) => emitters++);
            HeadlessHarness.Assert(weather.Precipitation.Length == 0 && emitters == 0 && weather.PrecipitationWind == Vector2.Zero,
                "Clear weather left a precipitation emitter, or its wind, behind.");

            // A script can call lightning down from a clear sky.
            weather.Strike(120f);
            Run(1f / 30f);
            HeadlessHarness.Assert(weather.Strikes == strikes + 1 && weather.Flash > 0.5f, "A strike asked for by a script did not flash.");
            Run(1.5f);
            HeadlessHarness.Assert(weather.Flash == 0f && audio.Played.Count == weather.Thunderclaps && audio.Played[^1].Volume > 0.4f,
                "A strike 120 m away should be over within a second, with loud thunder just after it.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Models.Cache.AnExportedGameShipsCachesThatSurviveCopyingAndArchiving", () =>
        {
            // A "project" with one large model and one small one.
            string project = Path.Combine(context.Workspace, "SealedCacheGame");
            string models = Path.Combine(project, "Assets", "Models");
            Directory.CreateDirectory(models);
            var big = new Genesis.Runtime.Modeling.GModelAsset { Name = "Hall" };
            var vertices = new MeshVertex[40000];
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = new MeshVertex { Position = new Vector3(i % 200, i / 200, MathF.Sin(i)), Normal = Vector3.UnitY, Color = Vector4.One };
            var indices = new ushort[90000];
            for (int i = 0; i < indices.Length; i++) indices[i] = (ushort)(i % 39999);
            big.Meshes.Add(new Genesis.Runtime.Modeling.GModelMesh { Name = "Walls", Vertices = vertices, Indices = indices });
            string bigFile = Path.Combine(models, "Hall.gmodel");
            Genesis.Runtime.Modeling.RuntimeModelStore.Save(bigFile, big);
            var small = new Genesis.Runtime.Modeling.GModelAsset { Name = "Peg" };
            small.Meshes.Add(new Genesis.Runtime.Modeling.GModelMesh { Name = "Peg", Vertices = vertices[..12], Indices = [0, 1, 2] });
            Genesis.Runtime.Modeling.RuntimeModelStore.Save(Path.Combine(models, "Peg.gmodel"), small);

            int written = Genesis.Runtime.Modeling.RuntimeModelStore.WriteSealedCaches(project);
            string cache = Genesis.Runtime.Modeling.ModelBinaryCache.PathFor(bigFile)!;
            HeadlessHarness.Assert(written == 1 && File.Exists(cache),
                $"Exporting should cache the one large model and leave the small one alone; it wrote {written}.");

            // The game is copied and unpacked on its way to the player: the folder moves and every
            // file's date changes. The cache must still be taken, because the model is the same.
            string shipped = Path.Combine(context.Workspace, "SealedCacheGame Shipped");
            if (Directory.Exists(shipped)) Directory.Delete(shipped, true);
            foreach (string file in Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(shipped, Path.GetRelativePath(project, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                File.SetLastWriteTimeUtc(target, new DateTime(2030, 1, 2, 3, 4, 6, DateTimeKind.Utc));
            }

            string shippedModel = Path.Combine(shipped, "Assets", "Models", "Hall.gmodel");
            int hits = Genesis.Runtime.Modeling.ModelBinaryCache.Hits, viaContent = Genesis.Runtime.Modeling.ModelBinaryCache.SealedHits;
            Genesis.Runtime.Modeling.GModelAsset loaded = Genesis.Runtime.Modeling.RuntimeModelStore.Load(shippedModel);
            HeadlessHarness.Assert(Genesis.Runtime.Modeling.ModelBinaryCache.Hits == hits + 1
                && Genesis.Runtime.Modeling.ModelBinaryCache.SealedHits == viaContent + 1,
                "The shipped game did not use its cache after its files were copied and re-dated.");
            HeadlessHarness.Assert(loaded.Meshes.Count == 1 && loaded.Meshes[0].Vertices.Length == 40000
                && loaded.Meshes[0].Vertices[777].Position == vertices[777].Position && loaded.Meshes[0].Indices.AsSpan().SequenceEqual(indices),
                "The model read from the shipped cache is not the model that was exported.");

            // A model replaced after shipping (a patch, a mod) must be read, not the cache.
            byte[] patched = File.ReadAllBytes(shippedModel);
            int at = Array.IndexOf(patched, (byte)'7', patched.Length / 2);
            patched[at] = (byte)'8';
            File.WriteAllBytes(shippedModel, patched);
            hits = Genesis.Runtime.Modeling.ModelBinaryCache.Hits;
            Genesis.Runtime.Modeling.RuntimeModelStore.Load(shippedModel);
            HeadlessHarness.Assert(Genesis.Runtime.Modeling.ModelBinaryCache.Hits == hits,
                "A model changed after shipping, to the same length, was still read from the old cache.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.World.Foliage.BushesAndSaplingsAreSolidShapesNotFlatCards", () =>
        {
            foreach (Genesis.World.Foliage.FoliageSpecies species in Enum.GetValues<Genesis.World.Foliage.FoliageSpecies>())
            foreach (bool near in new[] { true, false })
            {
                Genesis.World.MeshData mesh = Genesis.World.Foliage.FoliageGeometry.Build(species, near);
                HeadlessHarness.Assert(mesh.Indices.Length / 3 == Genesis.World.Foliage.FoliageGeometry.TriangleCount(species, near),
                    $"{species} ({(near ? "near" : "far")}) has {mesh.Indices.Length / 3} triangles; its budget says {Genesis.World.Foliage.FoliageGeometry.TriangleCount(species, near)}.");
                HeadlessHarness.Assert(mesh.Indices.All(index => index < mesh.Vertices.Length)
                    && mesh.Vertices.All(vertex => float.IsFinite(vertex.Position.Length()) && MathF.Abs(vertex.Normal.Length() - 1f) < 1e-3f),
                    $"{species} has an index past its vertices, or a normal that is not unit length.");
            }

            // A bush seen from any side has depth: a flat card has none along its own normal.
            foreach (Genesis.World.Foliage.FoliageSpecies species in new[] { Genesis.World.Foliage.FoliageSpecies.Shrub, Genesis.World.Foliage.FoliageSpecies.Sapling })
            foreach (bool near in new[] { true, false })
            {
                Genesis.World.MeshData mesh = Genesis.World.Foliage.FoliageGeometry.Build(species, near);
                // Measure the leaves: all of a bush, and a sapling above where its trunk ends.
                MeshVertex[] crown = species == Genesis.World.Foliage.FoliageSpecies.Shrub
                    ? mesh.Vertices
                    : mesh.Vertices.Where(vertex => vertex.Position.Y > 1.0f && new Vector2(vertex.Position.X, vertex.Position.Z).Length() > 0.1f
                        || vertex.Position.Y > 1.5f).ToArray();
                float width = crown.Max(vertex => vertex.Position.X) - crown.Min(vertex => vertex.Position.X);
                float depth = crown.Max(vertex => vertex.Position.Z) - crown.Min(vertex => vertex.Position.Z);
                HeadlessHarness.Assert(width > 0.3f && depth > 0.3f && MathF.Min(width, depth) / MathF.Max(width, depth) > 0.6f,
                    $"The {species} ({(near ? "near" : "far")}) is {width:F2} m wide and {depth:F2} m deep: it should be about as deep as it is wide.");
                // Leaves face outward all round, and the underside is shaded.
                HeadlessHarness.Assert(crown.Any(vertex => vertex.Normal.X > 0.3f) && crown.Any(vertex => vertex.Normal.X < -0.3f)
                    && crown.Any(vertex => vertex.Normal.Z > 0.3f) && crown.Any(vertex => vertex.Normal.Z < -0.3f),
                    $"The {species}'s leaves do not face outward on every side.");
                float brightest = mesh.Vertices.Max(vertex => vertex.Color.Y), darkestLeaf = crown.Min(vertex => vertex.Color.Y);
                HeadlessHarness.Assert(species == Genesis.World.Foliage.FoliageSpecies.Sapling || mesh.Vertices.Min(vertex => vertex.Color.Y) < brightest * 0.85f,
                    $"The {species} is one flat colour; its underside should be darker (leaf {darkestLeaf:F2} to {brightest:F2}).");
            }

            HeadlessHarness.Assert(Genesis.World.Foliage.FoliageGeometry.Build(Genesis.World.Foliage.FoliageSpecies.Shrub, true).Vertices
                .SequenceEqual(Genesis.World.Foliage.FoliageGeometry.Build(Genesis.World.Foliage.FoliageSpecies.Shrub, true).Vertices),
                "A bush must be the same shape every time it is built.");

            // Grass: dark at the root and lighter at the tip, each blade bending outward as it rises.
            foreach (Genesis.World.Foliage.FoliageSpecies species in new[]
                { Genesis.World.Foliage.FoliageSpecies.MeadowGrass, Genesis.World.Foliage.FoliageSpecies.TallGrass, Genesis.World.Foliage.FoliageSpecies.Reed })
            foreach (bool near in new[] { true, false })
            {
                Genesis.World.MeshData tuft = Genesis.World.Foliage.FoliageGeometry.Build(species, near);
                float top = tuft.Vertices.Max(vertex => vertex.Position.Y);
                MeshVertex[] roots = tuft.Vertices.Where(vertex => vertex.Position.Y < 0.01f).ToArray();
                MeshVertex[] tips = tuft.Vertices.Where(vertex => vertex.Position.Y > top * 0.75f).ToArray();
                HeadlessHarness.Assert(roots.Length > 0 && tips.Length > 0 && roots.Max(vertex => vertex.Color.Y) < tips.Min(vertex => vertex.Color.Y) * 0.7f,
                    $"{species} ({(near ? "near" : "far")}) should be darker at its roots than at its tips.");
                float rootReach = roots.Max(vertex => new Vector2(vertex.Position.X, vertex.Position.Z).Length());
                float tipReach = tips.Max(vertex => new Vector2(vertex.Position.X, vertex.Position.Z).Length());
                HeadlessHarness.Assert(tipReach > rootReach, $"{species}'s blades should lean outward as they rise ({rootReach:F2} at the root, {tipReach:F2} at the tip).");
            }

            HeadlessHarness.Assert(Genesis.World.Foliage.FoliageGeometry.Build(Genesis.World.Foliage.FoliageSpecies.TallGrass, true).Vertices.Max(vertex => vertex.Position.Y) < 1.0f,
                "Tall grass should stand under a metre before it is scaled.");

            // Flowers: a yellow heart with petals all round it, the head tipped so it has height as well as width.
            foreach (bool near in new[] { true, false })
            {
                Genesis.World.MeshData flowers = Genesis.World.Foliage.FoliageGeometry.Build(Genesis.World.Foliage.FoliageSpecies.Wildflower, near);
                MeshVertex[] hearts = flowers.Vertices.Where(vertex => vertex.Color.X > 0.9f && vertex.Color.Z < 0.4f).ToArray();
                MeshVertex[] petals = flowers.Vertices.Where(vertex => vertex.Color.Z > 0.6f).ToArray();
                HeadlessHarness.Assert(hearts.Length == (near ? 3 : 2) && petals.Length == hearts.Length * (near ? 5 : 4),
                    $"Wildflowers ({(near ? "near" : "far")}) should be {(near ? 3 : 2)} heads of {(near ? 5 : 4)} petals; found {hearts.Length} hearts and {petals.Length} petals.");
                MeshVertex heart = hearts[0];
                MeshVertex[] ring = petals.Where(vertex => Vector3.Distance(vertex.Position, heart.Position) < 0.2f).ToArray();
                float rise = ring.Max(vertex => vertex.Position.Y) - ring.Min(vertex => vertex.Position.Y);
                float span = ring.Max(vertex => vertex.Position.X) - ring.Min(vertex => vertex.Position.X);
                HeadlessHarness.Assert(ring.Length == (near ? 5 : 4) && rise > 0.04f && span > 0.08f,
                    $"A flower head should be a tipped cup of petals: it rises {rise:F2} m and spans {span:F2} m.");
            }

            // A terrain can refuse kinds of plant; others from the same preset grow where they would have.
            var meadow = new Genesis.World.Terrain.TerrainAsset(65, 65, 1f, 0f, 0f, 0f, 20f);
            for (int z = 0; z < 65; z++)
            for (int x = 0; x < 65; x++)
                meadow.SetSplat(x, z, 255, 0, 0, 0);
            Genesis.World.Foliage.FoliageScatterSettings Meadow(params Genesis.World.Foliage.FoliageSpecies[] without) => new()
            {
                Seed = 915, Preset = Genesis.World.Foliage.FoliagePreset.Meadow, MaximumInstances = 4000, Density = 0.9f, MinimumSpacing = 1.2f,
                ExcludedSpecies = [.. without],
            };
            var everything = Genesis.World.Foliage.FoliageScatter.Generate(meadow, null, Meadow());
            var noFlowers = Genesis.World.Foliage.FoliageScatter.Generate(meadow, null, Meadow(Genesis.World.Foliage.FoliageSpecies.Wildflower));
            var nothing = Genesis.World.Foliage.FoliageScatter.Generate(meadow, null, Meadow(Genesis.World.Foliage.FoliageSpecies.Wildflower,
                Genesis.World.Foliage.FoliageSpecies.MeadowGrass, Genesis.World.Foliage.FoliageSpecies.TallGrass));
            HeadlessHarness.Assert(everything.Instances.Count > 300 && everything.Instances.Any(plant => plant.Species == Genesis.World.Foliage.FoliageSpecies.Wildflower),
                $"A meadow of {everything.Instances.Count} plants should have flowers in it.");
            HeadlessHarness.Assert(noFlowers.Instances.All(plant => plant.Species != Genesis.World.Foliage.FoliageSpecies.Wildflower)
                && noFlowers.Instances.Count == everything.Instances.Count
                && noFlowers.Instances.Select(plant => plant.Position).SequenceEqual(everything.Instances.Select(plant => plant.Position)),
                $"A meadow without flowers should be as full, in the same places ({noFlowers.Instances.Count} against {everything.Instances.Count}).");
            HeadlessHarness.Assert(nothing.Instances.Count == 0, "A preset with every kind refused should grow nothing.");
            HeadlessHarness.Assert(Genesis.World.Foliage.FoliageScatter.Generate(meadow, null, Meadow()).Instances.SequenceEqual(everything.Instances),
                "The same settings must grow the same meadow.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Animation.Events.AScriptIsToldTheFrameAClipPassesAPoint", () =>
        {
            // The rule itself: a span of time passes a mark once, and a looping clip once each time round.
            HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.AnimationPassed(0.30, 0.34, 0.32, 2.0, false)
                && !Genesis.Runtime.Scripting.PgslCommands.AnimationPassed(0.34, 0.38, 0.32, 2.0, false)
                && !Genesis.Runtime.Scripting.PgslCommands.AnimationPassed(0.26, 0.30, 0.32, 2.0, false),
                "A clip passing 0.32 s should report it in that step and in no other.");
            HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.AnimationPassed(0.0, 0.016, 0.0, 2.0, false),
                "The start of a clip (fraction 0) was never reported.");
            HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.AnimationPassed(4.30, 4.34, 0.32, 2.0, true)
                && !Genesis.Runtime.Scripting.PgslCommands.AnimationPassed(4.30, 4.34, 0.32, 2.0, false)
                && !Genesis.Runtime.Scripting.PgslCommands.AnimationPassed(4.34, 4.38, 0.32, 2.0, true),
                "A looping clip should pass its mark again on the third time round, and a clip that does not loop should not.");
            HeadlessHarness.Assert(!Genesis.Runtime.Scripting.PgslCommands.AnimationPassed(0.5, 0.5, 0.5, 2.0, true)
                && !Genesis.Runtime.Scripting.PgslCommands.AnimationPassed(0.5, 0.6, 0.55, 0.0, true),
                "A clip that did not move, or has no length, passed a mark.");
            int passes = 0;
            double clock = 0;
            var random = new Random(11);
            for (int frame = 0; frame < 2000; frame++)
            {
                double step = 0.004 + random.NextDouble() * 0.03;
                if (Genesis.Runtime.Scripting.PgslCommands.AnimationPassed(clock, clock + step, 0.9, 1.2, true)) passes++;
                clock += step;
            }

            int expected = (int)Math.Floor((clock - 0.9) / 1.2) + 1;
            HeadlessHarness.Assert(Math.Abs(passes - expected) <= 1, $"Over {clock:F1} s a 1.2 s loop passed its mark {passes} times; expected {expected}.");

            // Through the script commands, on a model with a two-second clip.
            string project = Path.Combine(context.Workspace, "AnimationEventProject");
            Directory.CreateDirectory(Path.Combine(project, "Models"));
            var model = new Genesis.Runtime.Modeling.GModelAsset { Name = "Dancer" };
            var clip = new Genesis.Runtime.Modeling.GModelAnimationClip { Name = "Strike", Fps = 30f };
            for (int frame = 0; frame < 60; frame++)
                clip.Frames.Add(new Genesis.Runtime.Modeling.GModelAnimationFrame { LocalBoneTransforms = [Matrix4x4.Identity] });
            model.Animations.Add(clip);
            Genesis.Runtime.Modeling.RuntimeModelStore.Save(Path.Combine(project, "Models", "Dancer.gmodel"), model);

            using var scene = new RuntimeScene("Animation events");
            var game = new ProjectGameContext(project, scene, null, null, RoomAsset.Create("Hall", RoomDimension.ThreeD), null);
            string oldProject = Genesis.Runtime.Scripting.PgslCommands.ProjectPath;
            var oldGame = Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext;
            var script = new Genesis.Shared.Scripting.PgslContext
            {
                ModelAsset = "Dancer", ModelAnimationClip = "Strike", ModelAnimationFps = 30, ModelAnimationActive = true, ModelAnimationLoop = false,
            };
            Genesis.Shared.Scripting.PgslContext oldContext = Genesis.Runtime.Scripting.PgslCommands.BindContext(script);
            try
            {
                Genesis.Runtime.Scripting.PgslCommands.ProjectPath = project;
                Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext = game;
                HeadlessHarness.Assert(Math.Abs(Genesis.Runtime.Scripting.PgslCommands.ModelAnimationGetLength("Strike") - 2.0) < 1e-6
                    && Genesis.Runtime.Scripting.PgslCommands.ModelAnimationGetLength("Nothing") == 0,
                    "Sixty frames at 30 a second should be a two-second clip, and a clip the model lacks should have no length.");

                // The blow lands 40% of the way through: 0.8 s.
                var landed = new List<double>();
                for (int frame = 0; frame < 150; frame++)
                {
                    scene.GameTime.Advance(1f / 60f);
                    script.ModelAnimationLastTime = script.ModelAnimationTime;
                    script.ModelAnimationTime += 1.0 / 60.0;
                    script.ModelAnimationAdvancedFrame = game.FrameCount;
                    if (Genesis.Runtime.Scripting.PgslCommands.ModelAnimationCrossed(0.4)) landed.Add(script.ModelAnimationTime);
                    // Asking twice in a frame gives the same answer; an event is for the frame, not the first caller.
                    HeadlessHarness.Assert(Genesis.Runtime.Scripting.PgslCommands.ModelAnimationCrossed(0.4) == (landed.Count > 0 && landed[^1] == script.ModelAnimationTime),
                        "Two events in one frame disagreed about whether the blow landed.");
                }

                HeadlessHarness.Assert(landed.Count == 1 && Math.Abs(landed[0] - 0.8) < 0.02,
                    $"The blow should land once, at 0.8 s; it landed {landed.Count} times ({string.Join(", ", landed.Select(time => time.ToString("F2")))}).");
                HeadlessHarness.Assert(Math.Abs(Genesis.Runtime.Scripting.PgslCommands.ModelAnimationGetProgress() - 1.0) < 1e-6
                    && Math.Abs(Genesis.Runtime.Scripting.PgslCommands.ModelAnimationGetTime() - 2.5) < 0.01,
                    "A finished clip should report progress 1 and the time it has run.");

                // The game holds still (a hit-stop): the clip does not move, and the blow must not land again.
                script.ModelAnimationTime = 0.79;
                script.ModelAnimationLastTime = 0.79;
                for (int frame = 0; frame < 20; frame++)
                {
                    scene.GameTime.Advance(0f);
                    HeadlessHarness.Assert(!Genesis.Runtime.Scripting.PgslCommands.ModelAnimationCrossed(0.4), "A clip standing still reported an event.");
                }
            }
            finally
            {
                Genesis.Runtime.Scripting.PgslCommands.BindContext(oldContext);
                Genesis.Runtime.Scripting.PgslCommands.ProjectPath = oldProject;
                Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext = oldGame;
            }
        });

        HeadlessHarness.RunCase(context.Report, "Engine.World.Water.ATerrainCanGiveItsWaterItsOwnLook", () =>
        {
            // By default a lake and a sea keep the look their kind is given.
            var lake = new Genesis.World.Terrain.TerrainWaterDefinition { Name = "Tarn", Kind = Genesis.World.Terrain.TerrainWaterKind.Water };
            var sea = new Genesis.World.Terrain.TerrainWaterDefinition { Name = "Sea", Kind = Genesis.World.Terrain.TerrainWaterKind.Ocean };
            Genesis.World.Water.WaterMaterialSettings standard = Genesis.World.Water.WaterMaterialSettings.Default;
            Genesis.World.Water.WaterMaterialSettings lakeLook = lake.ToWaterBody().Material, seaLook = sea.ToWaterBody().Material;
            HeadlessHarness.Assert(lakeLook.ShallowColor == standard.ShallowColor && lakeLook.Opacity == standard.Opacity && seaLook.Opacity > 0.9f,
                "Water with no look of its own must keep the look of its kind.");

            // A peat river: brown, nearly opaque within a metre, a thin line of foam.
            var peat = new Genesis.World.Terrain.TerrainWaterDefinition
            {
                Name = "Peat Burn", Kind = Genesis.World.Terrain.TerrainWaterKind.River, CustomAppearance = true,
                ShallowColor = new Vector3(0.42f, 0.30f, 0.14f), DeepColor = new Vector3(0.12f, 0.07f, 0.03f),
                Opacity = 0.85f, ClarityDepth = 1.2f, FoamWidth = 0.3f,
            };
            Genesis.World.Water.WaterMaterialSettings peatLook = peat.ToWaterBody().Material;
            HeadlessHarness.Assert(peatLook.ShallowColor == peat.ShallowColor && peatLook.DeepColor == peat.DeepColor
                && peatLook.Opacity == 0.85f && peatLook.DepthFade == 1.2f && peatLook.FoamWidth == 0.3f,
                "The water's own colours, clarity and foam did not reach what is drawn.");

            // The look travels with a copy, survives a save, and nonsense is brought into range.
            Genesis.World.Terrain.TerrainWaterDefinition copy = peat.Clone();
            HeadlessHarness.Assert(copy.CustomAppearance && copy.DeepColor == peat.DeepColor && copy.ClarityDepth == 1.2f, "Copying water lost its look.");
            string folder = Path.Combine(context.Workspace, "WaterLook");
            Directory.CreateDirectory(folder);
            string resource = Path.Combine(folder, "Vale.terrain.json");
            var nature = new Genesis.World.Terrain.TerrainNatureDocument();
            nature.WaterBodies.Add(peat);
            Genesis.World.Terrain.TerrainNatureSerializer.Save(resource, nature);
            Genesis.World.Terrain.TerrainWaterDefinition loaded = Genesis.World.Terrain.TerrainNatureSerializer.LoadOrDefault(resource).WaterBodies.Single();
            HeadlessHarness.Assert(loaded.CustomAppearance && Vector3.Distance(loaded.ShallowColor, peat.ShallowColor) < 1e-5f
                && loaded.Opacity == 0.85f && loaded.FoamWidth == 0.3f,
                $"The water's look was not saved with the terrain (custom {loaded.CustomAppearance}, opacity {loaded.Opacity}).");
            var broken = new Genesis.World.Terrain.TerrainWaterDefinition
            {
                CustomAppearance = true, ShallowColor = new Vector3(float.NaN, 2f, -1f), Opacity = 7f, ClarityDepth = -3f, FoamWidth = float.PositiveInfinity,
            };
            Genesis.World.Water.WaterMaterialSettings mended = broken.ToWaterBody().Material;
            HeadlessHarness.Assert(mended.Opacity == 1f && mended.DepthFade == 0.1f && float.IsFinite(mended.FoamWidth)
                && float.IsFinite(mended.ShallowColor.X) && mended.ShallowColor.Y <= 1f && mended.ShallowColor.Z >= 0f,
                "Out-of-range water settings reached the renderer.");

            // The Water Bodies dialog shows and edits the look.
            using var dialog = new Genesis.Application.Editors.Suite.Terrain.TerrainWaterDialog(new[] { peat }, Vector3.Zero);
            var custom = (System.Windows.Forms.CheckBox)dialog.Controls.Find("TerrainWaterCustomLook", true)[0];
            System.Windows.Forms.Control shallow = dialog.Controls.Find("TerrainWaterShallowColour", true)[0];
            HeadlessHarness.Assert(custom.Checked && shallow.Enabled && shallow.BackColor.R == 107 && shallow.BackColor.B == 36,
                $"The dialog did not open on the water's own look (custom {custom.Checked}, swatch {shallow.BackColor}).");
            Genesis.World.Terrain.TerrainWaterDefinition edited = dialog.WaterBodies[0];
            HeadlessHarness.Assert(edited.CustomAppearance && edited.Opacity == 0.85f && Vector3.Distance(edited.ShallowColor, peat.ShallowColor) < 0.01f,
                "Opening the dialog changed the water's look.");
            custom.Checked = false;
            HeadlessHarness.Assert(!dialog.WaterBodies[0].CustomAppearance && !shallow.Enabled, "Turning the look off in the dialog did not take.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Particles.Weather.FallsFromJustAboveTheCameraAtAnyAltitude", () =>
        {
            // On a mountain 600 m up, rain must fall past the camera, not start at sea level.
            ParticleConfig rain = ParticlePresets.Rain();
            HeadlessHarness.Assert(rain.FollowCameraXZ && rain.FollowCameraHeight >= 5.0, "The rain preset no longer follows the camera from above.");
            var simulation = new ParticleSimulation();
            simulation.LoadConfig(rain);
            simulation.Reset(7);
            var camera = new Vector3(1200f, 600f, -340f);
            simulation.UpdateCameraPosition(camera);
            for (int step = 0; step < 30; step++) simulation.Step(1f / 60f);
            simulation.GetEmitterBounds(out Vector3 centre, out float radius);
            HeadlessHarness.Assert(simulation.ActiveCount > 50, $"Half a second of rain produced {simulation.ActiveCount} drops.");
            HeadlessHarness.Assert(MathF.Abs(centre.X - camera.X) < radius && MathF.Abs(centre.Z - camera.Z) < radius
                && centre.Y > camera.Y - radius && centre.Y < camera.Y + radius && radius < 80f,
                $"Rain around a camera at height 600 is centred at {centre} with radius {radius:F0}; it should surround the camera.");
        });
    }
}
