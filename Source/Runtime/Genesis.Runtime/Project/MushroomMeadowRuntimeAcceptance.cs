#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Genesis.Rendering.Core;
using Genesis.Runtime.Core;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Input;
using Genesis.Runtime.Imaging;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.Audio;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Project;

/// <summary>Opt-in exported-game acceptance (--acceptance-meadow). Feeds the window's InputState
/// and observes production scripts/rendering; never changes positions or game variables.</summary>
internal sealed class MushroomMeadowRuntimeAcceptance : ISceneSubsystem
{
    private readonly string _output;
    private readonly string _project;
    private readonly ProjectGameContext _game;
    private readonly ScriptHostSystem _scripts;
    private readonly Func<bool> _ready;
    private readonly Action<int> _finish;
    private readonly List<string> _diagnostics = [];
    private readonly List<CaptureEvidence> _captures = [];
    private readonly List<string> _lifeLosses = [];
    private readonly List<int[]> _labelPixels = [];
    private readonly DateTime _started = DateTime.UtcNow;
    private RuntimeScene? _scene;
    private RoomTileCollisionMap? _tiles;
    private int _stage;
    private int _stageFrames;
    private int _frames;
    private bool _done;
    private bool _crossingPit;
    private bool _pauseVerified;
    private bool _musicVerified;
    private bool _rigVerified;
    private int _rigCaptureFrames;
    private string _pausedActors = string.Empty;
    private double _pausedClock;
    private string _backend = string.Empty;
    private string? _pendingCapture;
    private PointF _previousPosition;
    private double _previousLives = 3;
    private float _maxDelta;

    public MushroomMeadowRuntimeAcceptance(string output, string project, ProjectGameContext game,
        ScriptHostSystem scripts, Func<bool> ready, Action<int> finish)
    {
        _output = output; _project = project; _game = game; _scripts = scripts; _ready = ready; _finish = finish;
        Directory.CreateDirectory(output);
        scripts.DiagnosticReported += OnDiagnostic;
    }

    private void OnDiagnostic(ScriptDiagnostic diagnostic) => _diagnostics.Add(diagnostic.Message);
    private Entity Hero => _scripts.Instances.OfType<PgslBehavior>().Single(behavior => ObjectDrawAssetRegistry.TryGet(behavior.Entity, out var asset)
        && asset.Prefab.Contains("Explorer", StringComparison.OrdinalIgnoreCase)).Entity;
    private PgslBehavior Player => (PgslBehavior)_scripts.FindBehaviorForEntity(Hero);
    private double Value(string name) => Convert.ToDouble(Player.GetVariablesSnapshot()[name], System.Globalization.CultureInfo.InvariantCulture);
    private PointF Position { get { var position = _scene!.World.GetRef<TransformComponent>(Hero); return new(position.X, position.Y); } }
    private void Hold(Key key, bool down) { if (down) _scene!.Input.OnKeyDown(key); else _scene!.Input.OnKeyUp(key); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private void Stage(int stage) { _stage = stage; _stageFrames = 0; }

    public void Update(RuntimeScene scene, GameTime time)
    {
        if (_done || !_ready()) return;
        _scene = scene;
        try
        {
            _frames++; _stageFrames++;
            _maxDelta = Math.Max(_maxDelta, time.Delta);
            if (Value("lives") < _previousLives)
                _lifeLosses.Add($"Frame {_frames}: {_previousPosition} -> {Position}; lives={Value("lives")}; delta={time.Delta}");
            _previousPosition = Position; _previousLives = Value("lives");
            Require(_diagnostics.Count == 0, "Game script faults: " + string.Join("; ", _diagnostics));
            Require(DateTime.UtcNow - _started < TimeSpan.FromMinutes(3), $"Gameplay timed out at {Position}, state={Value("state")}.");
            Hold(Key.Enter, _stage == 1 && _stageFrames == 1);
            Hold(Key.Escape, _stage == 2 && _stageFrames == 1);
            Hold(Key.P, _stage == 4 && _stageFrames == 1);
            Hold(Key.R, _stage == 6 && _stageFrames == 1);
            Hold(Key.D, _stage is 1 or 5);
            Hold(Key.Shift, _stage is 1 or 5);
            if (_stage != 5) Hold(Key.Space, false);
            switch (_stage)
            {
                case 0:
                    Require(Value("state") == 0 && Position.X == 112, "Title did not hold the authored spawn.");
                    if (_stageFrames >= 3) _pendingCapture = "title";
                    break;
                case 1:
                    Require(_stageFrames < 150, "Run animation did not produce two rendered frames.");
                    if (_stageFrames >= 8 && Value("motion") == 1)
                    {
                        int frame = scene.World.GetRef<SpriteComponent>(Hero).ImageIndex;
                        CaptureEvidence? first = _captures.FirstOrDefault(capture => capture.Name == "running-a");
                        if (first == null) _pendingCapture = "running-a";
                        else if (frame != first.SpriteFrame) _pendingCapture = "running-b";
                    }
                    break;
                case 2:
                    if (_stageFrames >= 3)
                    {
                        Require(Value("state") == 2, "Pause input did not enter the game's pause state.");
                        _pausedClock = Value("timeLeft"); _pausedActors = Actors();
                        _pendingCapture = "paused";
                    }
                    break;
                case 3:
                    Require(Value("state") == 2 && Value("timeLeft") == _pausedClock && Actors() == _pausedActors,
                        "Paused game advanced its timer or actors.");
                    if (_stageFrames >= 30) { _pauseVerified = true; Stage(4); }
                    break;
                case 4:
                    if (_stageFrames >= 2) { Require(Value("state") == 1, "Pause did not resume."); Stage(5); }
                    break;
                case 5:
                    if (Value("checkpoint") == 1 && !_rigVerified)
                    {
                        Hold(Key.D, false); Hold(Key.Space, false);
                        _rigCaptureFrames++;
                        Require(_rigCaptureFrames < 180, "The saved rig animation never produced a second visible pose.");
                        if (_rigCaptureFrames >= 8 && !_captures.Any(capture => capture.Name == "rig-a")) _pendingCapture = "rig-a";
                        else if (_rigCaptureFrames >= 16) _pendingCapture = "rig-b";
                        break;
                    }
                    if (Value("state") == 3)
                    {
                        Require(Value("coins") > 0 && Value("checkpoint") == 1 && Value("score") > 1000,
                            "Input-only traversal omitted collectibles, checkpoint or the time bonus.");
                        _pendingCapture = "win";
                    }
                    else
                    {
                        if (Value("state") != 1) _pendingCapture = "game-over";
                        else DriveTraversal();
                    }
                    break;
                case 6:
                    if (_stageFrames >= 3)
                    {
                        Require(Value("state") == 0 && Value("lives") == 3 && Value("coins") == 0
                            && scene.World.LivingEntityCount == 47 && _scripts.Instances.Count == 47,
                            "Production restart did not restore the title, counters and all 47 actors.");
                        _pendingCapture = "restarted";
                    }
                    break;
            }
        }
        catch (Exception exception) { Complete(false, exception.ToString()); }
    }

    private string Actors() => JsonSerializer.Serialize(_scripts.Instances.Select(behavior =>
        { var transform = _scene!.World.GetRef<TransformComponent>(behavior.Entity); return new { behavior.Entity.Id, transform.X, transform.Y,
            RigFrame = SpriteRigRuntime.Get(_scene.World, behavior.Entity)?.Frame ?? 0 }; }));

    private void DriveTraversal()
    {
        _tiles ??= new RoomTileCollisionMap(_game.Room, _project);
        PointF player = Position;
        bool ground = _tiles.Intersects(new RectangleF(player.X - 7, player.Y - 33, 14, 34));
        bool obstacle = _tiles.Intersects(new RectangleF(player.X + 34, player.Y - 33, 14, 32));
        bool gap = !_tiles.Intersects(new RectangleF(player.X + 44, player.Y + 1, 14, 3));
        var enemies = _scripts.Instances.Where(behavior => behavior.Entity != Hero
            && ObjectDrawAssetRegistry.TryGet(behavior.Entity, out var asset)
            && asset.Prefab.Contains("Acorn Walker", StringComparison.OrdinalIgnoreCase))
            .Select(behavior => _scene!.World.GetRef<TransformComponent>(behavior.Entity))
            .Where(enemy => enemy.X > player.X && enemy.X - player.X < 110).OrderBy(enemy => enemy.X).ToArray();
        bool waitForEnemy = enemies.Length > 0 && player.Y > enemies[0].Y - 24;
        bool jumpAtEnemy = enemies.Length > 0 && enemies[0].X - player.X < 35;
        Entity checkpoint = _scripts.Instances.First(behavior => ObjectDrawAssetRegistry.TryGet(behavior.Entity, out var asset)
            && asset.Prefab.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase)).Entity;
        bool checkpointStop = Value("checkpoint") == 0 && Math.Abs(player.X - _scene!.World.GetRef<TransformComponent>(checkpoint).X) < 16;
        bool pitAhead = !_tiles.Intersects(new RectangleF(player.X + 52, 289, 14, 2));
        if (ground)
        {
            bool jump = !checkpointStop && !_scene!.Input.IsDown(Key.Space) && (obstacle || gap || jumpAtEnemy);
            _crossingPit = jump && pitAhead;
            Hold(Key.Space, jump);
        }
        Hold(Key.D, !checkpointStop && !waitForEnemy && (!pitAhead || ground || _crossingPit));
    }

    public void CaptureFrame(IRenderController renderer)
    {
        if (_done || _pendingCapture == null || _scene == null) return;
        try
        {
            _backend = renderer.BackendName;
            var requested = RenderBackendCatalog.ParseExplicitValue(Environment.GetEnvironmentVariable(RenderBackendSelection.EnvironmentVariable) ?? "");
            var descriptor = RenderBackendCatalog.Describe(requested);
            Require(_backend.Equals(descriptor.DisplayName, StringComparison.OrdinalIgnoreCase)
                || _backend.Equals(descriptor.ShortName, StringComparison.OrdinalIgnoreCase), $"Requested {requested}, actually running {_backend}.");
            Require(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] pixels)
                && pixels.Length == width * height * 4, "Renderer could not read its submitted frame.");
            string name = _pendingCapture;
            string path = Path.Combine(_output, name + ".png");
            ObjectDrawAssetRegistry.TryGet(Hero, out var sprite);
            PgslRecordingDrawSurface authoredHud = new();
            _scripts.DispatchPgslGuiDraw(renderer, null, authoredHud);
            double[] labelCoverage = CheckHudLabels(pixels, width, height, name == "title");
            var roomRenderer = _scene.Subsystems.OfType<RoomRenderSubsystem>().Single();
            var origin = roomRenderer.GetViewportPosition(0);
            RoomViewport viewport = _game.Room.Viewports[0];
            Rectangle port = RoomDisplayLayout.Port(_game.Room, viewport, width, height);
            float scale = port.Width / viewport.SourceWidth;
            PointF position = Position;
            int cropX = (int)Math.Round(port.X + (position.X - origin.X - 16) * scale);
            int cropY = (int)Math.Round(port.Y + (position.Y - origin.Y - 38) * scale);
            int cropWidth = Math.Max(1, (int)Math.Round(32 * scale));
            int cropHeight = Math.Max(1, (int)Math.Round(40 * scale));
            Require(cropX >= 0 && cropY >= 0 && cropX + cropWidth <= width && cropY + cropHeight <= height,
                "Player capture was outside the rendered viewport.");
            byte[] crop = new byte[cropWidth * cropHeight * 4];
            for (int row = 0; row < cropHeight; row++)
                Buffer.BlockCopy(pixels, ((cropY + row) * width + cropX) * 4, crop, row * cropWidth * 4, cropWidth * 4);
            int rigFrame = 0, rigVisiblePixels = 0;
            string rigPixels = string.Empty, rigScreenPixels = string.Empty;
            if (name is "rig-a" or "rig-b")
            {
                Entity checkpoint = _scripts.Instances.First(behavior => ObjectDrawAssetRegistry.TryGet(behavior.Entity, out var asset)
                    && asset.Prefab.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase)).Entity;
                PixelRigSprite binding = _scene.World.GetRef<PixelRigSpriteComponent>(checkpoint).Binding
                    ?? throw new InvalidOperationException("The checkpoint's saved Image rig is not bound.");
                Require(binding.Player.Playing && binding.Player.AnimationName == "Flutter", "Object code did not play the saved Image rig animation.");
                var flag = _scene.World.GetRef<TransformComponent>(checkpoint);
                int left = (int)Math.Round(port.X + (flag.X - origin.X - binding.OriginX) * scale);
                int top = (int)Math.Round(port.Y + (flag.Y - origin.Y - binding.OriginY) * scale);
                int rigWidth = (int)Math.Round(binding.Player.Width * scale), rigHeight = (int)Math.Round(binding.Player.Height * scale);
                Require(left >= 0 && top >= 0 && left + rigWidth <= width && top + rigHeight <= height,
                    "Rig evidence is outside the submitted game frame.");
                byte[] rigCrop = new byte[rigWidth * rigHeight * 4];
                for (int row = 0; row < rigHeight; row++)
                    Buffer.BlockCopy(pixels, ((top + row) * width + left) * 4, rigCrop, row * rigWidth * 4, rigWidth * 4);
                for (int index = 0; index < rigCrop.Length; index += 4)
                    if (rigCrop[index + 2] > 200 && rigCrop[index + 1] > 170 && rigCrop[index] < 150) rigVisiblePixels++;
                Require(rigVisiblePixels > 30, "The animated gold checkpoint pennant is absent from renderer readback.");
                rigFrame = binding.Player.Frame;
                rigPixels = Convert.ToHexString(SHA256.HashData(binding.Player.GetPixels().Span));
                rigScreenPixels = Convert.ToHexString(SHA256.HashData(rigCrop));
                if (name == "rig-b")
                {
                    CaptureEvidence firstRig = _captures.Single(capture => capture.Name == "rig-a");
                    // A looping animation can visit the same pose twice. Capture two genuinely
                    // different sampled poses, then separately require changed submitted pixels.
                    if (firstRig.RigFrame == rigFrame || firstRig.RigPixelsSha256 == rigPixels)
                    { _pendingCapture = null; return; }
                }
            }
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                for (int row = 0; row < height; row++) Marshal.Copy(pixels, row * width * 4, data.Scan0 + row * data.Stride, width * 4);
                bitmap.UnlockBits(data); bitmap.Save(path, ImageFormat.Png);
            }
            var evidence = new CaptureEvidence(name, path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                width, height, Value("state"), Value("score"), Value("coins"), Value("checkpoint"), Value("lives"),
                position.X, position.Y, sprite?.Image ?? "", _scene.World.GetRef<SpriteComponent>(Hero).ImageIndex,
                Convert.ToHexString(SHA256.HashData(crop)), authoredHud.Texts.Select(text => text.Text).ToArray(), labelCoverage,
                rigFrame, rigPixels, rigScreenPixels, rigVisiblePixels);
            _captures.Add(evidence); _pendingCapture = null;
            switch (name)
            {
                case "title":
                    Require(_game.Audio.IsPlaying(new AudioChannel((int)Value("music"))), "Authored title music did not start.");
                    _musicVerified = true; Stage(1); break;
                case "running-b":
                    var first = _captures.Single(capture => capture.Name == "running-a");
                    Require(first.Sprite == evidence.Sprite && first.SpriteFrame != evidence.SpriteFrame
                        && first.PlayerPixelsSha256 != evidence.PlayerPixelsSha256, "Authored animation did not change rendered player pixels.");
                    Stage(2); break;
                case "paused": Stage(3); break;
                case "rig-b":
                    var rigFirst = _captures.Single(capture => capture.Name == "rig-a");
                    Require(rigFirst.RigFrame != evidence.RigFrame && rigFirst.RigPixelsSha256 != evidence.RigPixelsSha256
                        && rigFirst.RigScreenPixelsSha256 != evidence.RigScreenPixelsSha256,
                        "Image rig playback did not change both deformed artwork and submitted game pixels.");
                    _rigVerified = true; break;
                case "win":
                    Require(!_game.Audio.IsPlaying(new AudioChannel((int)Value("music"))), "Finish did not stop the looping meadow music.");
                    Stage(6); break;
                case "game-over": Complete(false, $"Input-only run ended at {Position}; state={Value("state")}, lives={Value("lives")}."); break;
                case "restarted": Require(_pauseVerified, "Pause verification was skipped."); Complete(true, ""); break;
            }
        }
        catch (Exception exception) { Complete(false, exception.ToString()); }
    }

    private void Complete(bool success, string error)
    {
        if (_done) return;
        _done = true;
        File.WriteAllText(Path.Combine(_output, "acceptance.json"), JsonSerializer.Serialize(new
        {
            Success = success, Error = error, Backend = _backend, RequestedBackend = Environment.GetEnvironmentVariable(RenderBackendSelection.EnvironmentVariable),
            Frames = _frames, InputOnly = true, PauseVerified = _pauseVerified, AudioAvailable = _game.Audio is Genesis.Audio.XAudioSystem,
            AudioPlaybackVerified = _musicVerified,
            RigPlaybackVerified = _rigVerified,
            MaxDeltaSeconds = _maxDelta, LifeLosses = _lifeLosses,
            StartedUtc = _started, CompletedUtc = DateTime.UtcNow, Diagnostics = _diagnostics, Captures = _captures,
        }, new JsonSerializerOptions { WriteIndented = true }));
        _finish(success ? 0 : 5);
    }

    public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }
    public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer) { }
    public void Dispose() => _scripts.DiagnosticReported -= OnDiagnostic;
    private sealed record CaptureEvidence(string Name, string File, string Sha256, int Width, int Height,
        double State, double Score, double Coins, double Checkpoint, double Lives, float X, float Y,
        string Sprite, int SpriteFrame, string PlayerPixelsSha256, string[] HudText, double[] HudLabelCoverage,
        int RigFrame, string RigPixelsSha256, string RigScreenPixelsSha256, int RigVisiblePixels);

    private double[] CheckHudLabels(byte[] pixels, int width, int height, bool baseline)
    {
        Require(width == 1280 && height == 720, "Meadow acceptance expects the authored 1280x720 game window.");
        Rectangle[] regions = [new(330, 57, 55, 27), new(600, 57, 49, 27), new(860, 57, 45, 27)];
        string[] names = ["COINS", "LIVES", "TIME"];
        if (baseline)
        {
            foreach (Rectangle region in regions)
            {
                List<int> bright = [];
                for (int y = region.Top; y < region.Bottom; y++)
                    for (int x = region.Left; x < region.Right; x++)
                    {
                        int index = (y * width + x) * 4;
                        if (pixels[index] > 215 && pixels[index + 1] > 225 && pixels[index + 2] > 210) bright.Add(index);
                    }
                Require(bright.Count >= 30, "Title HUD contains no readable label at " + region);
                _labelPixels.Add(bright.ToArray());
            }
        }
        double[] coverage = _labelPixels.Select(indices => indices.Count(index => pixels[index] > 200
            && pixels[index + 1] > 210 && pixels[index + 2] > 200) / (double)indices.Length).ToArray();
        for (int index = 0; index < coverage.Length; index++)
            Require(coverage[index] >= .95, $"Rendered {names[index]} label lost pixels: {coverage[index]:P1} of the title baseline remains.");
        return coverage;
    }

}
