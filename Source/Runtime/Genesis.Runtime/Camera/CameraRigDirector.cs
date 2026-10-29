#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Genesis.Shared.Rendering;

namespace Genesis.Runtime.Cameras;

/// <summary>
/// A room's camera rigs (Engine.Camera.Rigs): the rigs a game has made, which one is active, and
/// the blend under way between two. Each frame, after the scripts and the orbit camera, the active
/// rig's pose (or the blend's) is put on the 3D camera. A room with no active rig is left exactly
/// as it was: nothing here touches the camera. The rigs belong to the room and go with it.
/// </summary>
public sealed class CameraRigDirector
{
    private static readonly ConditionalWeakTable<RuntimeScene, CameraRigDirector> Directors = new();
    private static int _made;

    private readonly Dictionary<string, CameraRig> _rigs = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;
    private string _active = "";
    private bool _blending, _driving, _fovDriven;
    private CameraRig? _fromRig;
    private CameraPose _fromPose, _last;
    private float _blendSeconds, _blendElapsed, _baseFov;
    private CameraBlendCurve _blendCurve;

    private CameraRigDirector(int generation) => _generation = generation;

    /// <summary>The scene's rigs when it has any made in this room; reading never makes them.</summary>
    public static CameraRigDirector? For(RuntimeScene? scene) =>
        scene != null && Directors.TryGetValue(scene, out CameraRigDirector? director) ? director.Current(scene) : null;

    /// <summary>The scene's rigs, made the first time a script sets one up.</summary>
    public static CameraRigDirector? Make(RuntimeScene? scene)
    {
        if (scene == null) return null;
        return Directors.GetValue(scene, made =>
        {
            Interlocked.Increment(ref _made);
            return new CameraRigDirector(made.RoomGeneration);
        }).Current(scene);
    }

    /// <summary>Puts the active rig's pose on the scene's camera. RuntimeScene calls this once a frame, last.</summary>
    public static void Update(RuntimeScene scene, float dt)
    {
        // No game in this process has made a rig: one read and done.
        if (Volatile.Read(ref _made) == 0 || scene == null || !Directors.TryGetValue(scene, out CameraRigDirector? director)) return;
        director.Current(scene).Step(scene, dt);
    }

    /// <summary>False hands the camera back as it is without a rig, and keeps the rigs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds a change of <see cref="Active"/> made without a time of its own blends over.</summary>
    public float BlendTime { get; set; } = 1f;

    public CameraBlendCurve BlendCurve { get; set; } = CameraBlendCurve.EaseInOut;

    /// <summary>The rig Engine.Camera.Rig's values belong to: the last made, or one named.</summary>
    public string Selected { get; set; } = "";

    /// <summary>The name of the rig driving the camera; "" for none.</summary>
    public string Active => _active;

    public bool Blending => _blending;

    /// <summary>How far the blend into the active rig has gone, 0 to 1; 1 when none is under way.</summary>
    public float Progress => _blending && _blendSeconds > 0f ? Math.Clamp(_blendElapsed / _blendSeconds, 0f, 1f) : 1f;

    public int Count => _rigs.Count;

    /// <summary>Whether a rig is putting its pose on the camera (it did in the last frame).</summary>
    public bool Driving => _driving;

    public IEnumerable<CameraRig> Rigs => _rigs.Values;

    public CameraRig? Find(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _rigs.TryGetValue(name.Trim(), out CameraRig? rig) ? rig : null;

    /// <summary>
    /// Makes a rig, in place of any of the same name (the active one stays active as the new rig).
    /// A fixed or look-at rig starts where <paramref name="camera"/> is, looking where it looks.
    /// </summary>
    public CameraRig? Create(string? name, CameraRigKind kind, Camera camera)
    {
        string key = name?.Trim() ?? "";
        if (key.Length == 0 || key.Length > 128) return null;
        var rig = new CameraRig(key, kind);
        if (kind is CameraRigKind.Fixed or CameraRigKind.LookAt)
        {
            rig.Offset = camera.Position;
            rig.Yaw = camera.Yaw * 180f / MathF.PI;
            rig.Pitch = -camera.Pitch * 180f / MathF.PI;
        }
        if (_rigs.TryGetValue(key, out CameraRig? old) && ReferenceEquals(old, _fromRig)) ForgetFrom();
        _rigs[key] = rig;
        Selected = key;
        return rig;
    }

    public bool Remove(string? name)
    {
        if (Find(name) is not { } rig) return false;
        if (ReferenceEquals(rig, _fromRig)) ForgetFrom();
        _rigs.Remove(rig.Name);
        if (string.Equals(_active, rig.Name, StringComparison.OrdinalIgnoreCase)) _active = "";
        return true;
    }

    public void Clear()
    {
        _rigs.Clear();
        _active = "";
        _blending = false;
        _fromRig = null;
        Selected = "";
    }

    /// <summary>
    /// Makes <paramref name="name"/> the active rig, blending to it from where the camera is over
    /// <paramref name="seconds"/> along <paramref name="curve"/> (0 seconds or Cut: at once). "" hands
    /// the camera back at once. A name no rig has yet is kept, and a rig made with it takes over
    /// then. Returns whether the name is "" or a rig's. Asking for the active rig again changes nothing.
    /// </summary>
    public bool BlendTo(string? name, float seconds, CameraBlendCurve curve, Camera camera)
    {
        string key = name?.Trim() ?? "";
        bool known = key.Length == 0 || _rigs.ContainsKey(key);
        if (string.Equals(key, _active, StringComparison.OrdinalIgnoreCase)) return known;
        bool partWay = _blending;
        _fromRig = null;
        _blending = false;
        if (key.Length > 0 && seconds > 0f && float.IsFinite(seconds) && curve != CameraBlendCurve.Cut)
        {
            // From the rig in use, live; from a blend part way, or a camera no rig had, where it is now.
            if (_driving && !partWay && Find(_active) is { } current) _fromRig = current;
            _fromPose = _driving ? _last : CameraPose.Of(camera);
            _blending = true;
            _blendSeconds = Math.Min(seconds, 600f);
            _blendElapsed = 0f;
            _blendCurve = curve;
        }
        _active = key;
        Find(key)?.Settle();
        return known;
    }

    // A rig made afresh in place of one being blended from: the blend carries on from its last pose.
    private void ForgetFrom()
    {
        _fromRig = null;
        _fromPose = _last;
    }

    private CameraRigDirector Current(RuntimeScene scene)
    {
        if (_generation == scene.RoomGeneration) return this;
        // A new room: its camera starts as the room sets it, with no rig of the last room's.
        _generation = scene.RoomGeneration;
        Clear();
        _driving = _fovDriven = false;
        Enabled = true;
        BlendTime = 1f;
        BlendCurve = CameraBlendCurve.EaseInOut;
        return this;
    }

    private void Step(RuntimeScene scene, float dt)
    {
        Camera camera = scene.Camera3D;
        dt = float.IsFinite(dt) ? Math.Clamp(dt, 0f, 0.25f) : 0f;
        CameraRig? rig = Enabled && _active.Length > 0 ? Find(_active) : null;
        if (rig == null)
        {
            Release(camera);
            return;
        }
        if (!_fovDriven) _baseFov = camera.FieldOfView;
        if (!_driving && !_blending) rig.Settle();

        CameraPose pose = rig.Evaluate(scene, dt, takesInput: true);
        if (_blending)
        {
            CameraPose from = _fromRig != null ? _fromRig.Evaluate(scene, dt, takesInput: false) : _fromPose;
            _blendElapsed += dt;
            float t = _blendSeconds > 0f ? _blendElapsed / _blendSeconds : 1f;
            if (t >= 1f)
            {
                _blending = false;
                _fromRig = null;
            }
            else pose = CameraPose.Blend(from, pose, CameraRigNames.Ease(_blendCurve, t), _baseFov);
        }

        camera.Position = pose.Position;
        camera.Yaw = pose.Yaw;
        camera.Pitch = pose.Pitch;
        if (!float.IsNaN(pose.Fov))
        {
            camera.FieldOfView = pose.Fov;
            _fovDriven = true;
        }
        else if (_fovDriven)
        {
            camera.FieldOfView = _baseFov;
            _fovDriven = false;
        }
        _last = pose;
        _driving = true;
    }

    // No rig drives the camera: it keeps where it is, and gets its own field of view back.
    private void Release(Camera camera)
    {
        if (_fovDriven) camera.FieldOfView = _baseFov;
        _fovDriven = false;
        _driving = false;
        _blending = false;
        _fromRig = null;
    }
}
