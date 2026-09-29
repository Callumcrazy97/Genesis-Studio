using System.Numerics;
using System.Reflection;
using Genesis.Shared.Commands;

namespace Genesis.Application.Headless.Suites;

/// <summary>Isolates process-wide Engine camera state, including restoration after a failed case.</summary>
internal sealed class EngineCameraRegistryScope : IDisposable
{
    private readonly Array _twoD = (Array)Field("_cameras2D").GetValue(null)!;
    private readonly Array _threeD = (Array)Field("_cameras3D").GetValue(null)!;
    private readonly Array _savedTwoD;
    private readonly Array _savedThreeD;
    private readonly int _activeTwoD = (int)Field("_activeCamera2DId").GetValue(null)!;
    private readonly int _activeThreeD = (int)Field("_activeCamera3DId").GetValue(null)!;
    private readonly Vector3 _position = (Vector3)Field("_cameraPosition").GetValue(null)!;
    private readonly Vector3 _target = (Vector3)Field("_cameraTarget").GetValue(null)!;
    private readonly float _fov = (float)Field("_cameraFov").GetValue(null)!;
    private readonly float _near = (float)Field("_cameraNearPlane").GetValue(null)!;
    private readonly float _far = (float)Field("_cameraFarPlane").GetValue(null)!;

    public EngineCameraRegistryScope()
    {
        _savedTwoD = (Array)_twoD.Clone();
        _savedThreeD = (Array)_threeD.Clone();
        Array.Clear(_twoD);
        Array.Clear(_threeD);
    }

    public void Dispose()
    {
        Array.Copy(_savedTwoD, _twoD, _twoD.Length);
        Array.Copy(_savedThreeD, _threeD, _threeD.Length);
        Field("_activeCamera2DId").SetValue(null, _activeTwoD);
        Field("_activeCamera3DId").SetValue(null, _activeThreeD);
        Engine.SetCameraPosition(_position.X, _position.Y, _position.Z);
        Engine.SetCameraTarget(_target.X, _target.Y, _target.Z);
        Engine.SetCameraFov(_fov);
        Engine.SetCameraNearPlane(_near);
        Engine.SetCameraFarPlane(_far);
    }

    private static FieldInfo Field(string name) => typeof(Engine).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Engine camera fixture field is missing: " + name);
}
