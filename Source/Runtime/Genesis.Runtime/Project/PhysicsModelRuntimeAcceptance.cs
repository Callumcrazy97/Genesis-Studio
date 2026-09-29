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
using Genesis.Physics;
using Genesis.Rendering.Core;
using Genesis.Runtime.Core;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using PhysicsMotionType = Genesis.Shared.ECS.Components.PhysicsMotionType;

namespace Genesis.Runtime.Project;

/// <summary>Observes saved Physics/Model Objects and their authored PGSL in the standalone Player.
/// It never injects movement, replaces gameplay code or changes the saved resources.</summary>
internal sealed class PhysicsModelRuntimeAcceptance : ISceneSubsystem
{
    private readonly string _output;
    private readonly ScriptHostSystem _scripts;
    private readonly Func<bool> _ready;
    private readonly Action<int> _finish;
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly List<string> _diagnostics = [];
    private readonly List<Capture> _captures = [];
    private string? _pending;
    private string _backend = "";
    private Vector3 _position;
    private bool[]? _lastMask;
    private int _pixelChanges;
    private bool _landed;
    private bool _jumped;
    private bool _done;
    private float _elapsed;

    public PhysicsModelRuntimeAcceptance(string output, ScriptHostSystem scripts, Func<bool> ready, Action<int> finish)
    {
        _output = output; _scripts = scripts; _ready = ready; _finish = finish;
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
            Require(DateTime.UtcNow - _started < TimeSpan.FromSeconds(45), "Saved Physics workflow timed out.");
            Require(_diagnostics.Count == 0, "Saved PGSL failed: " + string.Join(';', _diagnostics));
            Entity Find(string name) => _scripts.Instances.Single(behavior => ObjectDrawAssetRegistry.TryGet(behavior.Entity, out var asset)
                && asset.InstanceName == name).Entity;
            Entity actor = Find("Physics actor"), floor = Find("Physics floor");
            ref RigidBodyComponent body = ref scene.World.GetRef<RigidBodyComponent>(actor);
            ref RigidBodyComponent ground = ref scene.World.GetRef<RigidBodyComponent>(floor);
            Require(body.Motion == PhysicsMotionType.Dynamic && body.Size == new Vector3(.5f) && body.LockRotation
                && Math.Abs(body.Mass - 2) < .001 && Math.Abs(body.Friction - .35f) < .001
                && ground.Motion == PhysicsMotionType.Static && ground.Size == new Vector3(4, .125f, 4),
                "Exported gameplay did not use saved Physics material or placed Model bounds.");
            _position = scene.World.GetRef<Transform3DComponent>(actor).Position;
            bool touching = scene.Physics.ContactChanges.Any(contact => contact.Phase != PhysicsContactPhase.Exit
                && (contact.A == actor && contact.B == floor || contact.A == floor && contact.B == actor));
            switch (_captures.Count)
            {
                case 0 when _elapsed > .12f:
                    Require(_position.Y > 3.8f, "The initial saved body was not at its authored pivot."); _pending = "start"; break;
                case 1 when _position.Y < 2.7f && _position.Y > .8f: _pending = "falling"; break;
                case 2 when touching && Math.Abs(_position.Y) < .08f:
                    _landed = true; _pending = "landed"; break;
                case 3 when _position.Y > .8f && scene.Physics.GetLinearVelocity(body.RegistrationId).Y > 1:
                    _jumped = true; _pending = "script-jump"; break;
                case 4 when touching && Math.Abs(_position.Y) < .08f:
                    Require(_landed && _jumped, "The saved body did not land, jump from PGSL and return."); _pending = "returned"; break;
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
            Require(_backend == requested.DisplayName || _backend == requested.ShortName, "Player substituted its renderer.");
            Require(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] pixels)
                && pixels.Length == width * height * 4, "No submitted game frame.");
            bool[] mask = new bool[width * height]; int gold = 0;
            for (int index = 0; index < pixels.Length; index += 4)
            {
                Require(pixels[index + 3] == 255, "The presented game has transparent capture pixels.");
                if (pixels[index + 2] > 90 && pixels[index + 1] > 65 && pixels[index] < 100) { gold++; mask[index / 4] = true; }
            }
            if (_lastMask != null && _pending is "falling" or "script-jump")
            {
                int changes = mask.Where((value, pixel) => value != _lastMask[pixel]).Count();
                Require(changes > 50, "Physics moved but the rendered Model stayed behind."); _pixelChanges += changes;
            }
            _lastMask = mask;
            string file = Path.Combine(_output, _pending + ".png");
            using Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < height; y++) Marshal.Copy(pixels, y * width * 4, data.Scan0 + y * data.Stride, width * 4); }
            finally { bitmap.UnlockBits(data); }
            bitmap.Save(file);
            Require(gold > 150, "The saved Model/material is absent from the physical game frame (gold pixels=" + gold + "): " + file);
            _captures.Add(new(_pending, file, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), _position.Y, width, height));
            _pending = null;
            if (_captures.Count == 5) Complete(true, "");
        }
        catch (Exception error) { Complete(false, error.ToString()); }
    }
    private void Complete(bool success, string error)
    {
        if (_done) return; _done = true;
        File.WriteAllText(Path.Combine(_output, "acceptance.json"), JsonSerializer.Serialize(new
        { Success = success, Error = error, Backend = _backend, SavedPhysicsVerified = _landed && _jumped,
            RenderedPhysicsPixelChanges = _pixelChanges, Diagnostics = _diagnostics, Captures = _captures }, new JsonSerializerOptions { WriteIndented = true }));
        _finish(success ? 0 : 5);
    }
    public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer) { }
    public void Dispose() => _scripts.DiagnosticReported -= Diagnostic;
    private sealed record Capture(string Name, string File, string Sha256, float Y, int Width, int Height);
}
