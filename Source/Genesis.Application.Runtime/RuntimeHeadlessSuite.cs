using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using Genesis.Audio;
using Genesis.Net;
using Genesis.Runtime.Assets;
using Genesis.Shared.Assets;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Net;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Audio;
using Genesis.Shared.Commands;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Net;
using Genesis.Shared.Rendering;
using Genesis.Streaming.Packs;

namespace Genesis.Application.Runtime;

/// <summary>
/// Deterministic Ember runtime checks for the greenfield headless suite:
/// 1D PGSL, 2D/3D D3D capture, audio WAV round-trip, and localhost networking.
/// </summary>
public static class RuntimeHeadlessSuite
{
    public static RuntimeOneDResult RunOneD(string logFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFile);
        string? directory = Path.GetDirectoryName(logFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        VMLogger.ClearInMemoryOnly();
        VMEngine.Initialize();
        VMEngine.ClearCompileCache();

        const string source = """
            var sum = 2 + 2;
            var product = sum * 3;
            """;

        CompileResult? compiled = VMEngine.Compile(source)
            ?? throw new InvalidOperationException("PGSL compile returned null.");
        if (compiled.Instructions.Count == 0)
        {
            throw new InvalidOperationException("PGSL compile produced no instructions.");
        }

        PgslVm vm = VMEngine.CreateVm(debug: false);
        vm.Execute(compiled.Instructions, compiled.Constants, clearVariables: true);

        Dictionary<string, object> variables = vm.GetVariables();
        if (!variables.TryGetValue("sum", out object? sumObj) || Convert.ToDouble(sumObj) != 4d)
        {
            throw new InvalidOperationException($"Expected sum=4, got '{sumObj}'.");
        }

        if (!variables.TryGetValue("product", out object? productObj) || Convert.ToDouble(productObj) != 12d)
        {
            throw new InvalidOperationException($"Expected product=12, got '{productObj}'.");
        }

        string[] logs;
        lock (VMLogger.Logs)
        {
            logs = [.. VMLogger.Logs];
        }

        StringBuilder builder = new();
        builder.AppendLine("Genesis Application — Runtime 1D (PGSL) diagnostics");
        builder.AppendLine($"CompiledInstructions={compiled.Instructions.Count}");
        builder.AppendLine($"sum={sumObj}");
        builder.AppendLine($"product={productObj}");
        builder.AppendLine("VMLogger:");
        foreach (string line in logs)
        {
            builder.AppendLine(line);
        }

        File.WriteAllText(logFile, builder.ToString());
        return new RuntimeOneDResult(compiled.Instructions.Count, Convert.ToDouble(sumObj), Convert.ToDouble(productObj));
    }

    public static ImageMetrics Capture2D(string outputFile)
    {
        using RuntimeViewportHarness harness = new();
        return harness.Capture2D(outputFile);
    }

    public static ImageMetrics Capture3D(string outputFile)
    {
        using RuntimeViewportHarness harness = new();
        return harness.Capture3D(outputFile);
    }

    public static ImageMetrics CapturePgsl3D(string outputFile)
    {
        using RuntimeViewportHarness harness = new();
        return harness.CapturePgsl3D(outputFile);
    }

    public static RuntimeAudioResult RunAudio(string workspaceRoot, string diagnosticsFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticsFile);

        Directory.CreateDirectory(workspaceRoot);
        string? diagnosticsDir = Path.GetDirectoryName(diagnosticsFile);
        if (!string.IsNullOrWhiteSpace(diagnosticsDir))
        {
            Directory.CreateDirectory(diagnosticsDir);
        }

        string audioFolder = Path.Combine(workspaceRoot, "Audio");
        Directory.CreateDirectory(audioFolder);
        string wavPath = Path.Combine(audioFolder, "headless-tone.wav");
        WriteToneWav(wavPath, frequency: 440f, seconds: 0.08f);

        if (!File.Exists(wavPath) || new FileInfo(wavPath).Length < 64)
        {
            throw new InvalidOperationException("Failed to write the headless tone WAV.");
        }

        // Contract check: NullAudioSystem must remain usable without a device.
        IAudioSystem nullAudio = NullAudioSystem.Instance;
        int nullId = nullAudio.LoadSound("Audio/headless-tone.wav");
        AudioChannel nullChannel = nullAudio.Play(nullId);
        if (nullId <= 0 || !nullChannel.IsValid)
        {
            throw new InvalidOperationException("NullAudioSystem failed its basic contract.");
        }

        bool played = false;
        string mode = "null-fallback";
        try
        {
            using XAudioSystem audio = new(workspaceRoot);
            int soundId = audio.LoadSound("Audio/headless-tone.wav");
            if (soundId <= 0)
            {
                throw new InvalidOperationException("XAudioSystem.LoadSound returned an invalid id.");
            }

            AudioChannel channel = audio.Play(soundId, volume: 0.2f);
            if (!channel.IsValid || !audio.IsPlaying(channel))
            {
                throw new InvalidOperationException("XAudioSystem.Play did not start a channel.");
            }

            audio.Update();
            Thread.Sleep(40);
            audio.Stop(channel);
            audio.Update();
            played = true;
            mode = "xaudio2";
        }
        catch (Exception exception)
        {
            // Headless CI / remote sessions may lack a working audio device.
            // WAV round-trip + NullAudioSystem contract still prove the integration surface.
            File.WriteAllText(
                diagnosticsFile,
                $"Audio device path failed; falling back to NullAudioSystem + WAV proof.{Environment.NewLine}{exception}");
        }

        if (!played)
        {
            File.AppendAllText(
                diagnosticsFile,
                $"{Environment.NewLine}WAV={wavPath}{Environment.NewLine}Bytes={new FileInfo(wavPath).Length}{Environment.NewLine}Mode={mode}");
        }
        else
        {
            File.WriteAllText(
                diagnosticsFile,
                $"Mode={mode}{Environment.NewLine}WAV={wavPath}{Environment.NewLine}Bytes={new FileInfo(wavPath).Length}");
        }

        return new RuntimeAudioResult(mode, wavPath, new FileInfo(wavPath).Length);
    }

    public static RuntimeNetworkResult RunNetworkLoopback()
    {
        int port = FindFreeUdpPort();
        using LiteNetGameNetwork host = new();
        using LiteNetGameNetwork client = new();

        byte[]? receivedPayload = null;
        int receivedTag = -1;
        host.OnMessageReceived += message =>
        {
            receivedTag = message.Tag;
            receivedPayload = message.Payload;
        };

        host.Host(port);

        client.Connect("127.0.0.1", port);
        if (!PumpUntil(() => host.PeerCount >= 1 && client.IsConnected, host, client, TimeSpan.FromSeconds(3)))
        {
            throw new InvalidOperationException("Loopback peer connection did not establish within 3s.");
        }

        byte[] payload = Encoding.UTF8.GetBytes("genesis-headless-ping");
        const int tag = 42;
        if (client.PeerCount < 1)
        {
            throw new InvalidOperationException("Client has no peers after connect.");
        }

        int hostPeerId = client.Peers[0].Id;
        client.Send(hostPeerId, tag, payload);

        if (!PumpUntil(() => receivedPayload is not null, host, client, TimeSpan.FromSeconds(3)))
        {
            throw new InvalidOperationException("Host did not receive the loopback payload.");
        }

        if (receivedTag != tag)
        {
            throw new InvalidOperationException($"Unexpected net tag {receivedTag}, expected {tag}.");
        }

        string text = Encoding.UTF8.GetString(receivedPayload!);
        if (!string.Equals(text, "genesis-headless-ping", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected payload '{text}'.");
        }

        client.Disconnect();
        host.Disconnect();
        return new RuntimeNetworkResult(port, text.Length);
    }

    /// <summary>Zstd chunk pack identity + AOI interest cull + draw-phase/occluder contract (capacity spine).</summary>
    public static RuntimeCapacitySpineResult RunCapacitySpineChecks()
    {
        byte[] sample = Encoding.UTF8.GetBytes("genesis-capacity-spine-pack-v1");
        if (!ZstdChunkPack.IdentityRoundTrip(sample))
        {
            throw new InvalidOperationException("Zstd chunk pack round-trip failed.");
        }

        var repl = new ReplicationScaffold { InterestRadius = 10f };
        repl.SetObserver(Vector3.Zero);
        repl.Register(1, Vector3.Zero, ownedLocally: true);
        repl.Register(2, new Vector3(100f, 0f, 0f), ownedLocally: false);
        List<int> near = repl.BuildInterestSendList();
        if (!near.Contains(1) || near.Contains(2))
        {
            throw new InvalidOperationException("AOI interest list incorrect.");
        }

        byte[] payload = repl.EncodeTransform(1);
        if (!ReplicationScaffold.TryDecodeTransform(payload, out int netId, out Vector3 pos)
            || netId != 1
            || pos != Vector3.Zero)
        {
            throw new InvalidOperationException("Transform snapshot encode/decode failed.");
        }

        // Draw submit gate: Engine.DrawSprite must no-op outside Submit.
        OccluderBoundsRegistry.Clear();
        OccluderBoundsRegistry.Set(42, new Vector3(-1f), new Vector3(1f));
        if (OccluderBoundsRegistry.Count != 1)
            throw new InvalidOperationException("Occluder registry did not store chunk bounds.");

        RenderAutoState.AllowDrawSubmit = false;
        RenderAutoState.DrawSubmitRejected = 0;
        Engine.SetDrawCommandSink(null);
        Engine.DrawSprite(TextureHandle.Invalid, 0f, 0f);
        if (RenderAutoState.DrawSubmitRejected < 1)
            throw new InvalidOperationException("DrawSprite outside Submit did not reject.");

        OccluderBoundsRegistry.Clear();
        return new RuntimeCapacitySpineResult(sample.Length, near.Count);
    }

    /// <summary>Sprite descriptor parse, frame path resolution, and frame-index wrapping.</summary>
    public static RuntimeSpriteAssetResult RunSpriteAssetLoaderChecks(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        string assets = Path.Combine(workspaceRoot, "Assets");
        Directory.CreateDirectory(assets);
        string spritePath = Path.Combine(assets, "Runtime Sprite.image.json");
        string dataDirectory = Path.Combine(assets, "Runtime Sprite.spritedata");
        Directory.CreateDirectory(dataDirectory);
        string frameZero = Path.Combine(dataDirectory, "frame-0000.png");
        string frameOne = Path.Combine(dataDirectory, "frame-0001.png");
        WriteSolidPng(frameZero, 8, 8, 220, 40, 40);
        WriteSolidPng(frameOne, 8, 8, 40, 120, 220);

        File.WriteAllText(spritePath, """
            {
              "schemaVersion": 2,
              "canvas": { "width": 8, "height": 8 },
              "import": { "source": "Runtime Sprite.spritedata/frame-0000.png" },
              "origin": { "x": 0.5, "y": 1.0, "space": "normalized" },
              "frames": [
                {
                  "id": "frame-0000",
                  "name": "Frame 1",
                  "durationMilliseconds": 90,
                  "source": "Runtime Sprite.spritedata/frame-0000.png",
                  "sourceRectangle": { "width": 8, "height": 8 }
                },
                {
                  "id": "frame-0001",
                  "name": "Frame 2",
                  "durationMilliseconds": 120,
                  "source": "Runtime Sprite.spritedata/frame-0001.png",
                  "sourceRectangle": { "width": 8, "height": 8 }
                }
              ],
              "tags": [
                {
                  "name": "Ping",
                  "startFrameId": "frame-0000",
                  "endFrameId": "frame-0001",
                  "direction": "pingPong",
                  "loop": true
                }
              ]
            }
            """);

        var asset = SpriteAssetLoader.Load(spritePath);
        if (asset.SchemaVersion != 2 || asset.Frames.Count != 2)
        {
            throw new InvalidOperationException("Sprite descriptor did not parse the expected frame list.");
        }

        string resolvedZero = SpriteAssetLoader.ResolveFrameTexturePath(spritePath, asset, 0);
        string resolvedOne = SpriteAssetLoader.ResolveFrameTexturePath(spritePath, asset, 1);
        if (!File.Exists(resolvedZero) || !File.Exists(resolvedOne))
        {
            throw new InvalidOperationException("Sprite frame texture paths did not resolve.");
        }

        string keyed = SpriteAssetLoader.ResolveFrameTexturePath(workspaceRoot, "Assets/Runtime Sprite.image.json", 1);
        if (!string.Equals(keyed, resolvedOne, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Project-relative sprite frame resolution failed.");
        }

        if (SpriteAssetLoader.NormalizeFrameIndex(3, 2) != 1)
        {
            throw new InvalidOperationException("Sprite frame index wrapping failed.");
        }

        SpriteAssetLoader.RegisterFrameCount("Assets/Runtime Sprite.image.json", asset.Frames.Count);
        if (SpriteAssetLoader.GetFrameCount("Assets/Runtime Sprite.image.json") != 2)
        {
            throw new InvalidOperationException("Sprite frame count registration failed.");
        }

        SpriteRuntimeOrigin bottomCenter = new() { X = 0.5, Y = 1.0, Space = "normalized" };
        (float originX, float originY) = SpriteOriginUtility.ResolvePixels(bottomCenter, 8, 8);
        if (Math.Abs(originX - 4f) > 0.01f || Math.Abs(originY - 8f) > 0.01f)
        {
            throw new InvalidOperationException("Sprite origin resolution failed.");
        }

        SpriteRuntimeOrigin pixelBottomCenter = new() { X = 4, Y = 8, Space = "pixels" };
        (originX, originY) = SpriteOriginUtility.ResolvePixels(pixelBottomCenter, 8, 8);
        if (Math.Abs(originX - 4f) > 0.01f || Math.Abs(originY - 8f) > 0.01f)
        {
            throw new InvalidOperationException("Pixel-space sprite origin resolution failed.");
        }

        // NEXT-092. A project authored before pixel origins were written correctly still opens and
        // plays: the object moves, plays audio and scores, but its sprite resolves a quarter of a
        // screen away and never appears. The resolution itself is left alone — a pivot outside the
        // sprite is legal — but it must not be silent.
        SpriteRuntimeOrigin mislabelled = new() { X = 16, Y = 32, Space = "normalized" };
        (originX, originY) = SpriteOriginUtility.ResolvePixels(mislabelled, 32, 32);
        if (Math.Abs(originX - 512f) > 0.01f || Math.Abs(originY - 1024f) > 0.01f)
        {
            throw new InvalidOperationException(
                $"A normalized origin of (16,32) on a 32x32 sprite resolved to ({originX},{originY}); "
                + "the reproduction for NEXT-092 no longer reproduces.");
        }

        if (!SpriteOriginUtility.IsImplausibleNormalizedOrigin(mislabelled))
        {
            throw new InvalidOperationException(
                "A normalized origin of (16,32) was not reported as implausible, so an invisible "
                + "object would again give the designer nothing to go on.");
        }

        if (SpriteOriginUtility.IsImplausibleNormalizedOrigin(bottomCenter)
            || SpriteOriginUtility.IsImplausibleNormalizedOrigin(pixelBottomCenter))
        {
            throw new InvalidOperationException(
                "A valid origin was reported as implausible; the check would cry wolf on correct projects.");
        }

        VerifyRoomViewports();

        string originReport = SpriteOriginUtility.DescribeImplausibleOrigin(mislabelled, "Player");
        if (!originReport.Contains("Player", StringComparison.Ordinal)
            || !originReport.Contains("pixels", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The implausible-origin report names neither the sprite nor the fix, so it is not actionable.");
        }

        SpriteComponent sprite = new()
        {
            ImageIndex = 0,
            ImageSpeed = 1f,
            AnimationTagIndex = -1,
            AnimationLoopOverride = -1,
        };
        SpriteAssetLoader.RegisterAsset("Assets/Runtime Sprite.image.json", asset);
        SpritePlayback.Advance(ref sprite, asset, 0.1f);
        if (sprite.ImageIndex != 1)
        {
            throw new InvalidOperationException("Duration-based sprite playback did not advance to frame 2.");
        }

        sprite.AnimationTagIndex = 0;
        sprite.ImageIndex = 0;
        sprite.PlaybackElapsedMs = 0f;
        sprite.PlaybackStepDirection = 1;
        SpritePlayback.Advance(ref sprite, asset, 0.1f);
        SpritePlayback.Advance(ref sprite, asset, 0.12f);
        if (sprite.ImageIndex != 0)
        {
            string tagDirection = asset.Tags.Count > 0 ? asset.Tags[0].Direction : "<none>";
            bool tagLoop = asset.Tags.Count > 0 && asset.Tags[0].Loop;
            throw new InvalidOperationException(
                "Ping-pong sprite playback did not return to frame 1. "
                + $"index={sprite.ImageIndex}, direction={sprite.PlaybackStepDirection}, "
                + $"elapsedMs={sprite.PlaybackElapsedMs:0.###}, tagDirection={tagDirection}, tagLoop={tagLoop}.");
        }

        return new RuntimeSpriteAssetResult(asset.Frames.Count, resolvedZero, resolvedOne);
    }

    private static void WriteSolidPng(string path, int width, int height, byte r, byte g, byte b)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(r, g, b));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static bool PumpUntil(
        Func<bool> condition,
        LiteNetGameNetwork host,
        LiteNetGameNetwork client,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            host.Update();
            client.Update();
            if (condition())
            {
                return true;
            }

            Thread.Sleep(15);
        }

        host.Update();
        client.Update();
        return condition();
    }

    private static int FindFreeUdpPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static void WriteToneWav(string path, float frequency, float seconds, int sampleRate = 44100)
    {
        int samples = Math.Max(1, (int)(sampleRate * seconds));
        short[] pcm = new short[samples];
        double phaseStep = 2.0 * Math.PI * frequency / sampleRate;
        double phase = 0;
        for (int i = 0; i < samples; i++)
        {
            pcm[i] = (short)(Math.Sin(phase) * 0.35 * short.MaxValue);
            phase += phaseStep;
        }

        byte[] data = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, data, 0, data.Length);

        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        int byteRate = sampleRate * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + data.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)2); // block align
        writer.Write((short)16); // bits
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(data.Length);
        writer.Write(data);
    }

    /// <summary>
    /// Room viewports: schema shape, the margin follow rule, and the round trip. Pure logic, so it
    /// runs without a device and states what the renderer is entitled to assume.
    /// </summary>
    private static void VerifyRoomViewports()
    {
        RoomAsset room = RoomAsset.Create("Viewport Room", RoomDimension.TwoD);

        if (room.Viewports.Count != RoomAsset.MaxViewports)
        {
            throw new InvalidOperationException(
                $"A new room has {room.Viewports.Count} viewports, expected {RoomAsset.MaxViewports}; "
                + "the editor could not show a fixed set of slots.");
        }

        if (room.UsesViewports)
        {
            throw new InvalidOperationException(
                "A new room reports that it uses viewports. Every room would switch to the viewport "
                + "render path before anyone configured one.");
        }

        // Enabling one must flip the room onto the viewport path, and survive a save/load round trip.
        room.Viewports[2].Enabled = true;
        room.Viewports[2].SourceX = 100f;
        room.Viewports[2].SourceWidth = 640f;
        room.Viewports[2].PortWidth = 640;
        room.Viewports[2].FollowTarget = "Player";
        room.Viewports[2].FollowSpeedX = 4f;

        if (!room.UsesViewports)
        {
            throw new InvalidOperationException("Enabling a viewport did not switch the room onto the viewport path.");
        }

        RoomAsset reloaded = room.DeepClone();
        reloaded.Normalize();
        RoomViewport restored = reloaded.Viewports[2];
        if (!restored.Enabled
            || Math.Abs(restored.SourceX - 100f) > 0.01f
            || Math.Abs(restored.SourceWidth - 640f) > 0.01f
            || restored.PortWidth != 640
            || restored.FollowTarget != "Player"
            || Math.Abs(restored.FollowSpeedX - 4f) > 0.01f)
        {
            throw new InvalidOperationException(
                "Viewport settings did not survive a round trip; the editor would silently lose them.");
        }

        // Normalize must never invent a negative or zero-area source region, whatever it is handed.
        reloaded.Viewports[0].SourceWidth = -50f;
        reloaded.Viewports[0].PortHeight = 0;
        reloaded.Normalize();
        if (reloaded.Viewports[0].SourceWidth <= 0f || reloaded.Viewports[0].PortHeight <= 0)
        {
            throw new InvalidOperationException("Normalize left a viewport with no drawable area.");
        }

        // ── The margin follow rule ──────────────────────────────────────────────
        // Inside the dead zone the viewport must not move at all. This is the whole point of a
        // margin: a viewport that tracks every pixel of movement makes the camera feel glued.
        float stationary = RoomViewportTracker.Advance(
            position: 0f, target: 320f, size: 640f, margin: 200f, speed: -1f);
        if (Math.Abs(stationary) > 0.001f)
        {
            throw new InvalidOperationException(
                $"A target inside the dead zone moved the viewport to {stationary}; the margin is not a dead zone.");
        }

        // Past the right edge, an instant viewport puts the target exactly on that edge.
        float instant = RoomViewportTracker.Advance(
            position: 0f, target: 500f, size: 640f, margin: 200f, speed: -1f);
        if (Math.Abs(instant - 60f) > 0.01f)
        {
            throw new InvalidOperationException(
                $"An instant follow moved to {instant}, expected 60 (target 500 - size 640 + margin 200).");
        }

        // A speed cap must limit the step, not the destination.
        float capped = RoomViewportTracker.Advance(
            position: 0f, target: 500f, size: 640f, margin: 200f, speed: 4f);
        if (Math.Abs(capped - 4f) > 0.01f)
        {
            throw new InvalidOperationException(
                $"A follow speed of 4 moved the viewport {capped}px in one step; the cap is not applied.");
        }

        // A margin at or past half the view pins the target to the centre rather than oscillating
        // between two crossed edges.
        float pinned = RoomViewportTracker.Advance(
            position: 0f, target: 500f, size: 640f, margin: 9999f, speed: -1f);
        float pinnedAgain = RoomViewportTracker.Advance(
            position: pinned, target: 500f, size: 640f, margin: 9999f, speed: -1f);
        if (Math.Abs(pinned - pinnedAgain) > 0.01f)
        {
            throw new InvalidOperationException(
                $"An oversized margin oscillates the viewport ({pinned} then {pinnedAgain}); the edges crossed over.");
        }
    }
}

public sealed record RuntimeOneDResult(int InstructionCount, double Sum, double Product);

public sealed record RuntimeAudioResult(string Mode, string WavPath, long WavBytes);

public sealed record RuntimeNetworkResult(int Port, int PayloadBytes);

public sealed record RuntimeCapacitySpineResult(int PackBytes, int InterestCount);

public sealed record RuntimeSpriteAssetResult(int FrameCount, string FirstFramePath, string SecondFramePath);
