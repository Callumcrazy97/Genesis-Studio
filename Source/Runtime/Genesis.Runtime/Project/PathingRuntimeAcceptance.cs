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
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Navigation;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Project;

/// <summary>Observes a saved Model patrol in the standalone Player without replacing its scripts or movement.</summary>
internal sealed class PathingRuntimeAcceptance : ISceneSubsystem
{
    private readonly string _output;
    private readonly ProjectGameContext _game;
    private readonly ScriptHostSystem _scripts;
    private readonly Func<bool> _ready;
    private readonly Action<int> _finish;
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly List<string> _diagnostics = [];
    private readonly List<Capture> _captures = [];
    private float _elapsed;
    private float _maxX = float.MinValue;
    private bool _animationVerified;
    private bool _routineVerified;
    private bool _done;
    private Entity _actor;
    private Vector3 _position;
    private string _backend = "";
    private GModelAsset? _model;
    private float _poseMinimum = float.MaxValue;
    private float _poseMaximum = float.MinValue;
    private string? _pending;
    private float _currentPose;
    private float _startPose;
    private Vector3 _startPosition;
    private bool[]? _startMask;
    private int _animationPixelChanges;

    public PathingRuntimeAcceptance(string output, ProjectGameContext game, ScriptHostSystem scripts,
        Func<bool> ready, Action<int> finish)
    {
        _output = output; _game = game; _scripts = scripts; _ready = ready; _finish = finish;
        Directory.CreateDirectory(output); scripts.DiagnosticReported += Diagnostic;
    }
    private void Diagnostic(ScriptDiagnostic value) => _diagnostics.Add(value.Message);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }
    public void Update(RuntimeScene scene, GameTime time)
    {
        if (_done || !_ready()) return;
        try
        {
            _elapsed += time.Delta;
            Require(DateTime.UtcNow - _started < TimeSpan.FromSeconds(60), "Saved patrol timed out.");
            Require(_diagnostics.Count == 0, "Saved PGSL failed: " + string.Join(';', _diagnostics));
            _actor = _scripts.Instances.OfType<PgslBehavior>().Single(behavior =>
                ObjectDrawAssetRegistry.TryGet(behavior.Entity, out var asset) && asset.InstanceName == "Patrol Unit").Entity;
            _position = scene.World.GetRef<Transform3DComponent>(_actor).Position;
            _maxX = MathF.Max(_maxX, _position.X);
            var agent = scene.World.GetRef<NavMeshAgentComponent>(_actor).Agent;
            var animation = scene.World.GetRef<ModelAnimatorComponent>(_actor);
            Require(agent != null && !agent.PlanarXY && Math.Abs(agent.Speed - 4) < .001f,
                "The saved 3D route did not start with its authored speed and dimension.");
            Require(animation.ClipName == "Patrol" && animation.Playing, "PathFollow did not start the saved Model animation.");
            if (_model == null)
            {
                string model = scene.World.GetRef<ModelRendererComponent>(_actor).ModelAsset;
                _model = StudioModelResourceLoader.LoadReadOnly(StudioModelResourceLoader.Resolve(_game.ResolveAssetPath("."), model));
                Require(_model.Rig.IsValid && _model.Meshes.Any(mesh => mesh.IsSkinned), "The authored Model rig was lost.");
            }
            Matrix4x4[] locals = GModelPrimitiveFactory.EvaluateAnimatedLocals(_model,
                new RuntimeModelAnimationState(animation.ClipName, animation.TimeSeconds, animation.ClipFps, animation.Loop));
            _currentPose = locals[0].M13;
            _poseMinimum = MathF.Min(_poseMinimum, locals[0].M13); _poseMaximum = MathF.Max(_poseMaximum, locals[0].M13);
            _animationVerified = animation.TimeSeconds > .5f && _poseMaximum - _poseMinimum > .4f && _animationPixelChanges > 30;
            switch (_captures.Count)
            {
                case 0 when _elapsed > .12f: _pending = "start"; break;
                case 1 when Math.Abs(Math.Abs(_currentPose) - Math.Abs(_startPose)) > .2f:
                    Require(Vector3.Distance(_position, _startPosition) < .001f,
                        "The stationary animation check ran after the saved waypoint wait ended.");
                    _pending = "animated"; break;
                case 2 when _position.X > -.5f:
                    Require(_position.Y > .8f && _position.Z > -.2f, "Patrol flattened height or Z to the 2D plane.");
                    _pending = "moving"; break;
                case 3 when _maxX > 3.8f && _position.X < 3.3f: _pending = "returning"; break;
                case 4 when _position.X < -.5f:
                    Require(_animationVerified, "The real Model animation pose did not advance.");
                    _routineVerified = true; _pending = "returned"; break;
            }
        }
        catch (Exception error) { Complete(false, error.ToString()); }
    }
    public void CaptureFrame(IRenderController renderer)
    {
        if (_done || _pending == null) return;
        try
        {
            _backend = renderer.BackendName;
            var requested = RenderBackendCatalog.Describe(RenderBackendCatalog.ParseExplicitValue(
                Environment.GetEnvironmentVariable(RenderBackendSelection.EnvironmentVariable) ?? ""));
            Require(_backend == requested.DisplayName || _backend == requested.ShortName, "The Player substituted its renderer.");
            Require(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] pixels)
                && pixels.Length == width * height * 4, "The submitted game frame was unavailable.");
            int gold = 0;
            bool[] mask = new bool[width * height];
            for (int index = 0; index < pixels.Length; index += 4)
            {
                Require(pixels[index + 3] == 255, "The presented scene has transparent pixels.");
                if (pixels[index + 2] > 110 && pixels[index + 1] > 65 && pixels[index] < 100)
                { gold++; mask[index / 4] = true; }
            }
            string file = Path.Combine(_output, _pending + ".png");
            using Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < height; y++) Marshal.Copy(pixels, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
            bitmap.UnlockBits(data); bitmap.Save(file);
            Require(gold > 300, "The saved Model/material is absent from the physical game frame: " + file);
            if (_pending == "start") { _startMask = mask; _startPose = _currentPose; _startPosition = _position; }
            if (_pending == "animated")
            {
                Require(_startMask != null && _startMask.Length == mask.Length, "No matching stationary animation frame.");
                for (int pixel = 0; pixel < mask.Length; pixel++) if (mask[pixel] != _startMask![pixel]) _animationPixelChanges++;
                Require(_animationPixelChanges > 30, "The saved bone pose changed but the rendered Model stayed static.");
            }
            _captures.Add(new(_pending, file, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
                _position.X, _position.Y, _position.Z, width, height));
            _pending = null;
            if (_captures.Count == 5) Complete(true, "");
        }
        catch (Exception error) { Complete(false, error.ToString()); }
    }
    private void Complete(bool success, string error)
    {
        if (_done) return; _done = true;
        File.WriteAllText(Path.Combine(_output, "acceptance.json"), JsonSerializer.Serialize(new
        { Success = success, Error = error, Backend = _backend, SavedRoutineVerified = _routineVerified,
            ModelAnimationVerified = _animationVerified, RenderedAnimationPixelChanges = _animationPixelChanges,
            Diagnostics = _diagnostics, Captures = _captures },
            new JsonSerializerOptions { WriteIndented = true }));
        _finish(success ? 0 : 5);
    }
    public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer) { }
    public void Dispose() => _scripts.DiagnosticReported -= Diagnostic;
    private sealed record Capture(string Name, string File, string Sha256, float X, float Y, float Z, int Width, int Height);
}
