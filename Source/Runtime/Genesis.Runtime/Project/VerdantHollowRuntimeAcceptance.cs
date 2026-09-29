#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Genesis.Rendering.Core;
using Genesis.Runtime.Core;
using Genesis.Runtime.Input;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Project;

/// <summary>Opt-in exported template check. Supplies keyboard/look input to production gameplay;
/// reads the real physics body, authored PGSL and submitted frames without teleporting actors.</summary>
internal sealed class VerdantHollowRuntimeAcceptance : ISceneSubsystem
{
    private readonly string _output;
    private readonly ProjectGameContext _game;
    private readonly ScriptHostSystem _scripts;
    private readonly Func<bool> _ready;
    private readonly Action<int> _finish;
    private readonly List<string> _diagnostics = [];
    private readonly List<CaptureEvidence> _captures = [];
    private readonly DateTime _started = DateTime.UtcNow;
    private RuntimeScene? _scene;
    private Entity _hero;
    private int _stage;
    private int _frames;
    private float _stageSeconds;
    private bool _pulseSent;
    private bool _done;
    private string? _pendingCapture;
    private string _backend = string.Empty;
    private Vector3 _stageStart;
    private float _walkSpeed;
    private float _sprintSpeed;
    private float _jumpRise;
    private bool _guideVerified;
    private bool _lookVerified;
    private bool _landVerified;
    private bool _audioVerified;
    private bool _terrainSurfaceVerified;
    private bool _grassGroundVerified;

    public VerdantHollowRuntimeAcceptance(string output, ProjectGameContext game,
        ScriptHostSystem scripts, Func<bool> ready, Action<int> finish)
    {
        _output = output; _game = game; _scripts = scripts; _ready = ready; _finish = finish;
        Directory.CreateDirectory(output);
        scripts.DiagnosticReported += OnDiagnostic;
    }

    private void OnDiagnostic(ScriptDiagnostic diagnostic) => _diagnostics.Add(diagnostic.Message);
    private Vector3 Position => _scene!.World.GetRef<Transform3DComponent>(_hero).Position;
    private CharacterMotorComponent Motor => _scene!.World.GetRef<CharacterMotorComponent>(_hero);
    private double Value(string name) => Convert.ToDouble(((PgslBehavior)_scripts.FindBehaviorForEntity(_hero))
        .GetVariablesSnapshot()[name], System.Globalization.CultureInfo.InvariantCulture);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private void Hold(Key key, bool down) { if (down) _scene!.Input.OnKeyDown(key); else _scene!.Input.OnKeyUp(key); }

    public void FixedUpdate(RuntimeScene scene, float fixedDelta)
    {
        if (_done || !_ready()) return;
        _scene = scene;
        _stageSeconds += fixedDelta;
        Hold(Key.Up, _stage == 2);
        Hold(Key.W, _stage == 3);
        Hold(Key.Shift, _stage == 3);
        bool pulse = !_pulseSent && _stage is 1 or 4 or 6;
        Hold(Key.H, pulse && _stage is 1 or 6);
        Hold(Key.Space, pulse && _stage == 4);
        if (pulse) _pulseSent = true;
    }

    public void Update(RuntimeScene scene, GameTime time)
    {
        if (_done || !_ready()) return;
        _scene = scene;
        try
        {
            _frames++;
            _hero = _scripts.Instances.OfType<PgslBehavior>().Single(behavior => ObjectDrawAssetRegistry.TryGet(behavior.Entity, out var asset)
                && Path.GetFileNameWithoutExtension(asset.Prefab).StartsWith("NatureExplorer", StringComparison.OrdinalIgnoreCase)).Entity;
            Require(_diagnostics.Count == 0, "Script faults: " + string.Join("; ", _diagnostics));
            Require(DateTime.UtcNow - _started < TimeSpan.FromMinutes(3), $"Timed out in stage {_stage}, position {Position}, motor {Motor.State}.");
            Require(float.IsFinite(Position.Y) && Position.Y > -60, "Character fell through the authored terrain.");
            switch (_stage)
            {
                case 0:
                    if (_stageSeconds > .8f && Motor.State == CharacterMotorState.Grounded)
                    {
                        RoomTerrainSubsystem terrain = scene.Subsystems.OfType<RoomTerrainSubsystem>().Single();
                        if (terrain.AuthoredMaterialGroundCount != 1) break;
                        Require(terrain.VisiblePathMeshCount == 0, "Painted trails gained duplicate ribbon geometry.");
                        _terrainSurfaceVerified = true;
                        int grassCount = 0;
                        foreach (var behavior in _scripts.Instances.OfType<PgslBehavior>())
                        {
                            if (!ObjectDrawAssetRegistry.TryGet(behavior.Entity, out var grassAsset)
                                || !grassAsset.InstanceName.StartsWith("Interactive Trail Grass", StringComparison.Ordinal)) continue;
                            Transform3DComponent grass = scene.World.GetRef<Transform3DComponent>(behavior.Entity);
                            Require(Math.Abs(grass.Position.Y - _game.GetTerrainHeight(grass.Position.X, grass.Position.Z)) < .02f,
                                "Scripted grass did not sample the authored ground during Create: " + grassAsset.InstanceName);
                            grassCount++;
                        }
                        Require(grassCount >= 10, "The saved scripted trail grass did not load.");
                        _grassGroundVerified = true;
                        Require(scene.Physics?.ExternalStaticCount > 0 && scene.WaterVolumes.Count == 4,
                            "Saved terrain collision or four authored water volumes did not load.");
                        Require(Value("showHelp") == 1, "New players are not shown the controls guide.");
                        ObjectCompositionSubsystem composition = scene.Subsystems.OfType<ObjectCompositionSubsystem>().Single();
                        _audioVerified = _game.Audio is Genesis.Audio.XAudioSystem && composition.ActiveAudioCount >= 2;
                        Require(_audioVerified, "Exported ambience/campfire did not start through native audio.");
                        _pendingCapture = "spawn-guide";
                    }
                    break;
                case 1:
                    // The same InputState path consumed by the saved Step event's mouse look.
                    if (_stageSeconds < .25f) scene.Input.LookDelta = new Vector2(2, 0);
                    if (_stageSeconds > .4f)
                    {
                        Require(Value("showHelp") == 0, "H did not hide the guide.");
                        _lookVerified = Value("lookYaw") > .1 && scene.Camera3D.Forward.X > .001f;
                        Require(_lookVerified, "Saved PGSL mouse look did not change the rendered camera.");
                        _pendingCapture = "look";
                    }
                    break;
                case 2:
                case 3:
                    var body = scene.World.GetRef<RigidBodyComponent>(_hero);
                    Vector3 velocity = scene.Physics!.GetLinearVelocity(body.RegistrationId);
                    float speed = new Vector2(velocity.X, velocity.Z).Length();
                    if (_stage == 2) _walkSpeed = Math.Max(_walkSpeed, speed);
                    else _sprintSpeed = Math.Max(_sprintSpeed, speed);
                    if (_stageSeconds > 1.1f)
                    {
                        Require(Vector2.Distance(new(Position.X, Position.Z), new(_stageStart.X, _stageStart.Z)) > 3,
                            "Keyboard input did not move the real character body.");
                        Require(Motor.State == CharacterMotorState.Grounded,
                            $"Trail traversal lost its walkable terrain contact: position={Position}, ground={_game.GetTerrainHeight(Position.X, Position.Z)}, velocity={velocity}.");
                        if (_stage == 3) Require(_sprintSpeed > _walkSpeed * 1.5f, "Sprint did not use the saved character settings.");
                        _pendingCapture = _stage == 2 ? "walking" : "sprinting";
                    }
                    break;
                case 4:
                    _jumpRise = Math.Max(_jumpRise, Position.Y - _stageStart.Y);
                    if (_jumpRise > .8f && Motor.State == CharacterMotorState.Falling) _pendingCapture = "jumping";
                    Require(_stageSeconds < 2, "Space did not produce a physics jump.");
                    break;
                case 5:
                    if (_stageSeconds > .4f && Motor.State == CharacterMotorState.Grounded)
                    {
                        _landVerified = true;
                        _pendingCapture = "landed";
                    }
                    Require(_stageSeconds < 4, "Jump did not land on the native terrain collider.");
                    break;
                case 6:
                    if (_stageSeconds > .2f)
                    {
                        _guideVerified = Value("showHelp") == 1;
                        Require(_guideVerified, "H did not reopen the controls guide.");
                        _pendingCapture = "guide-reopened";
                    }
                    break;
            }
        }
        catch (Exception exception) { Complete(false, exception.ToString()); }
    }

    public void CaptureFrame(IRenderController renderer)
    {
        if (_done || _pendingCapture == null || _scene == null) return;
        try
        {
            _backend = renderer.BackendName;
            var backend = RenderBackendCatalog.Describe(RenderBackendCatalog.ParseExplicitValue(
                Environment.GetEnvironmentVariable(RenderBackendSelection.EnvironmentVariable) ?? ""));
            Require(_backend == backend.DisplayName || _backend == backend.ShortName, "Actual backend differs from the requested backend: " + _backend);
            Require(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] pixels)
                && pixels.Length == width * height * 4, "No submitted game frame was available.");
            for (int index = 3; index < pixels.Length; index += 4) Require(pixels[index] == 255, "Submitted game frame contains transparent pixels.");
            string name = _pendingCapture;
            string path = Path.Combine(_output, name + ".png");
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                for (int row = 0; row < height; row++) Marshal.Copy(pixels, row * width * 4, data.Scan0 + row * data.Stride, width * 4);
                bitmap.UnlockBits(data); bitmap.Save(path, ImageFormat.Png);
            }
            PgslRecordingDrawSurface hud = new();
            _scripts.DispatchPgslGuiDraw(renderer, null, hud);
            string[] texts = hud.Texts.Select(text => text.Text).ToArray();
            Require(texts.Contains("VERDANT HOLLOW"), "The saved DrawGui event did not render its scenic card.");
            if (name is "spawn-guide" or "guide-reopened") Require(texts.Any(text => text.Contains("jump / swim", StringComparison.Ordinal)), "Authored controls guide was absent.");
            _captures.Add(new(name, path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                width, height, Position.X, Position.Y, Position.Z, Motor.State.ToString(), texts));
            _pendingCapture = null;
            if (_stage == 6) { Complete(true, ""); return; }
            _stage++;
            _stageSeconds = 0;
            _pulseSent = false;
            _stageStart = Position;
        }
        catch (Exception exception) { Complete(false, exception.ToString()); }
    }

    private void Complete(bool success, string error)
    {
        if (_done) return;
        _done = true;
        _scene?.Input?.ClearHeld();
        bool bound = _scene != null && !_hero.IsNull && _scene.World.Has<Transform3DComponent>(_hero)
            && _scene.World.Has<CharacterMotorComponent>(_hero);
        Vector3 current = bound ? Position : Vector3.Zero;
        File.WriteAllText(Path.Combine(_output, "acceptance.json"), JsonSerializer.Serialize(new
        {
            Success = success, Error = error, Backend = _backend, InputOnly = true, Frames = _frames,
            GuideVerified = _guideVerified, CameraLookVerified = _lookVerified, LandingVerified = _landVerified,
            AudioPlaybackVerified = _audioVerified, WalkSpeed = _walkSpeed, SprintSpeed = _sprintSpeed, JumpRise = _jumpRise,
            AuthoredTerrainSurfaceVerified = _terrainSurfaceVerified,
            ScriptedGrassGroundVerified = _grassGroundVerified,
            CurrentStage = _stage, CurrentPosition = new[] { current.X, current.Y, current.Z }, CurrentMotor = bound ? Motor.State.ToString() : "Unbound",
            StartedUtc = _started, CompletedUtc = DateTime.UtcNow, Diagnostics = _diagnostics, Captures = _captures,
        }, new JsonSerializerOptions { WriteIndented = true }));
        _finish(success ? 0 : 5);
    }

    public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer) { }
    public void Dispose() => _scripts.DiagnosticReported -= OnDiagnostic;
    private sealed record CaptureEvidence(string Name, string File, string Sha256, int Width, int Height,
        float X, float Y, float Z, string Motor, string[] HudText);
}
