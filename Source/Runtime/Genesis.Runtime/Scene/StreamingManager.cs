using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Rendering.D3dMath;
using Genesis.Runtime.Core;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;
using Genesis.Streaming;

namespace Genesis.Runtime.Scene
{
    /// <summary>
    /// Central streaming coordinator. Providers register here; the scene ticks this before other subsystems.
    /// Load/unload and frustum tests use <see cref="Camera3D.FarPlane"/> (render distance in blocks).
    /// </summary>
    public sealed class StreamingManager : ISceneSubsystem, IDisposable
    {
        private readonly List<IStreamingProvider> _providers = new List<IStreamingProvider>();
        private readonly StreamingSettings _settings = new StreamingSettings();
        private readonly StreamingJobQueue _jobs = new StreamingJobQueue(4);
        private Vector3? _focusOverride;

        public StreamingSettings Settings => _settings;

        /// <summary>Optional world position for distance-based streaming priority. Defaults to the active camera.</summary>
        public Vector3? FocusOverride
        {
            get => _focusOverride;
            set => _focusOverride = value;
        }

        public IReadOnlyList<IStreamingProvider> Providers => _providers;

        public void Register(IStreamingProvider provider)
        {
            if (!_providers.Contains(provider))
                _providers.Add(provider);
        }

        public void Unregister(IStreamingProvider provider) => _providers.Remove(provider);

        /// <summary>Forces all providers to rebuild their load queues on the next tick.</summary>
        public void RequestRefresh()
        {
            for (int i = 0; i < _providers.Count; i++)
                _providers[i].RequestStreamingRefresh();
        }

        public void Update(RuntimeScene scene, GameTime time)
        {
            UpdateProviders(scene, time);
        }

        public void ProcessMainThreadCallbacks()
        {
            _jobs.ProcessMainThread(_settings.MaxMainThreadCallbacksPerFrame);
        }

        public void UpdateProviders(RuntimeScene scene, GameTime time)
        {
            _jobs.SetMaxConcurrent(_settings.MaxBackgroundJobs);

            Camera3D camera = scene.Camera3D;
            var context = new StreamingContext
            {
                FocusPosition = ResolveFocusPosition(scene),
                ViewProjection = camera.ViewProjection,
                StreamingViewProjection = BuildStreamingViewProjection(camera, _settings),
                CameraFarPlane = camera.FarPlane,
                CameraYaw = camera.Yaw,
                CameraPitch = camera.Pitch,
                Settings = _settings,
                Jobs = _jobs,
                DeltaTime = time.Delta,
            };

            for (int i = 0; i < _providers.Count; i++)
            {
                _providers[i].Stats.ResetFrameCounters();
                _providers[i].Tick(context);
            }
        }

        public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

        public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer) { }

        public void Dispose()
        {
            _jobs.Dispose();
            _providers.Clear();
        }

        private Vector3 ResolveFocusPosition(RuntimeScene scene) =>
            _focusOverride ?? scene.Camera3D.Position;

        private static Matrix4x4 BuildStreamingViewProjection(Camera3D camera, StreamingSettings settings)
        {
            float scale = settings.StreamingFrustumFovScale;
            if (MathF.Abs(scale - 1f) < 0.001f)
                return camera.ViewProjection;

            float fov = Math.Clamp(camera.FieldOfView * scale, 0.05f, MathF.PI - 0.05f);
            Matrix4x4 projection = MathUtil.PerspectiveFovLH(fov, camera.AspectRatio, camera.NearPlane, camera.FarPlane);
            return camera.ViewMatrix * projection;
        }
    }
}
