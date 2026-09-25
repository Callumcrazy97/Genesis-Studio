using System;
using System.IO;
using System.Numerics;
using Genesis.Rendering.Diagnostics;
using Genesis.Runtime.Input;
using Genesis.Runtime.Platform;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Audio;
using Genesis.Shared.Interfaces;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Project
{
    /// <summary>
    /// Engine services exposed to project <see cref="EntityBehavior"/> scripts during play.
    /// </summary>
    public sealed class ProjectGameContext : IGameContext
    {
        private readonly string _projectPath;
        private readonly RuntimeScene _scene;
        private readonly IRenderController _renderer;
        private readonly SilkGameWindow _window;
        private readonly ProjectLogger _logger;
        private IAudioSystem _audio;   // settable: host wires it after construction
        private RoomAsset _room;
        private string _pendingRoom;

        public ProjectGameContext(
            string projectPath,
            RuntimeScene scene,
            IRenderController renderer,
            SilkGameWindow window,
            RoomAsset room,
            ProjectLogger logger,
            IAudioSystem audio = null)
        {
            _projectPath = projectPath;
            _scene = scene;
            _renderer = renderer;
            _window = window;
            _room = room;
            _logger = logger;
            _audio = audio ?? NullAudioSystem.Instance;
        }

        public RuntimeScene Scene => _scene;
        public ScriptHostSystem ScriptHost { get; internal set; }
        public EcsWorld World => _scene?.World;
        public Camera3D Camera => _scene?.Camera3D;
        public InputState Input => _scene?.Input;
        public RoomAsset Room => _room;

        public void SetRoom(RoomAsset room) => _room = room;

        /// <summary>Install or replace the audio service (host wires this after construction).</summary>
        public void SetAudio(IAudioSystem audio) => _audio = audio ?? NullAudioSystem.Instance;

        public void Log(string message)
        {
            _logger?.Line(message);
            RenderLog.Line(message);
        }

        public float DeltaTime => _scene?.GameTime.Delta ?? 0f;
        public float TotalTime => _scene?.GameTime.Total ?? 0f;
        public long FrameCount => _scene?.GameTime.FrameCount ?? 0;

        public int ScreenWidth => ClientWidth;
        public int ScreenHeight => ClientHeight;
        public int ClientWidth => _window?.Width ?? 0;
        public int ClientHeight => _window?.Height ?? 0;
        public int RenderWidth => _renderer?.PixelWidth ?? 0;
        public int RenderHeight => _renderer?.PixelHeight ?? 0;
        public float OverlayScaleX => ClientWidth > 0 ? (float)RenderWidth / ClientWidth : 1f;
        public float OverlayScaleY => ClientHeight > 0 ? (float)RenderHeight / ClientHeight : 1f;

        public Vector2 MapMouseToOverlay(Vector2 clientPosition) =>
            new Vector2(clientPosition.X * OverlayScaleX, clientPosition.Y * OverlayScaleY);

        public bool MouseCaptured => _window?.MouseCaptured ?? false;

        public void SetMouseCaptured(bool captured)
        {
            try { _window?.SetMouseCaptured(captured); } catch { }
        }

        public void SetCursorMode(CursorMode mode)
        {
            try { _window?.SetCursorMode(mode); } catch { }
        }

        public void ChangeRoom(string roomName) => _pendingRoom = roomName;

        /// <summary>Returns and clears a pending room change requested by gameplay scripts.</summary>
        public bool TryConsumePendingRoom(out string roomName)
        {
            roomName = _pendingRoom;
            if (string.IsNullOrEmpty(roomName))
                return false;
            _pendingRoom = null;
            return true;
        }

        public string PendingRoom => _pendingRoom;

        public bool VSyncEnabled => _window?.VSync ?? true;
        public int TargetFps => _window?.TargetFps ?? 0;
        public WindowMode WindowMode => _window?.Mode ?? WindowMode.Windowed;

        public void SetVSync(bool enabled)
        {
            if (_window != null) _window.VSync = enabled;
            _renderer?.SetVSync(enabled);
        }

        public void SetTargetFps(int fps)
        {
            if (_window != null) _window.TargetFps = fps;
        }

        public void SetResolution(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            try { _window?.SetSize(width, height); } catch { }
            try { _renderer?.TryResize(width, height); } catch { }
        }

        public void SetWindowMode(WindowMode mode)
        {
            if (_window == null) return;
            _window.Mode = mode;
            // ApplyMode runs on the next render tick and fires Resize with the final client size.
        }

        /// <summary>
        /// Applies window mode, resolution, vsync, and fps cap together (mirrors source GameApp.ApplyDisplaySettings).
        /// </summary>
        public void ApplyDisplaySettings(WindowMode mode, int width, int height, bool vsync, int targetFps)
        {
            SetTargetFps(targetFps);
            SetVSync(vsync);
            SetWindowMode(mode);
            SetResolution(width, height);
        }

        public MeshHandle RegisterMesh(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<ushort> indices)
            => _renderer?.RegisterMesh(vertices, indices) ?? MeshHandle.Invalid;

        public void ReleaseMesh(MeshHandle handle)
        {
            if (handle.IsValid) _renderer?.ReleaseMesh(handle);
        }

        public MeshHandle GetBuiltinMesh(BuiltinMeshKind kind)
            => _renderer?.GetBuiltinMesh(kind) ?? MeshHandle.Invalid;

        public string ResolveAssetPath(string projectRelativePath)
        {
            if (string.IsNullOrWhiteSpace(projectRelativePath) || projectRelativePath == ".") return _projectPath;
            return ResourceNames.ResolveFile(_projectPath, projectRelativePath);
        }

        public TextureHandle LoadTexture(string projectRelativePath)
        {
            string path = Genesis.Runtime.Assets.SpriteAssetLoader.ResolveFrameTexturePath(_projectPath, projectRelativePath, 0);
            return File.Exists(path) ? _renderer.LoadTexture(path) : TextureHandle.Invalid;
        }

        public TextureHandle CreateTexture(int width, int height, ReadOnlySpan<byte> rgba)
            => _renderer?.CreateTexture(width, height, rgba) ?? TextureHandle.Invalid;

        public void UpdateTexture(TextureHandle handle, int width, int height, ReadOnlySpan<byte> rgba)
            => _renderer?.UpdateTexture(handle, width, height, rgba);

        public void ReleaseTexture(TextureHandle handle)
        {
            if (handle.IsValid) _renderer?.ReleaseTexture(handle);
        }

        public void AddPointLight(Vector3 position, Vector3 color, float radius, float intensity = 1f, float falloff = 2f)
            => _renderer?.AddPointLight(position, color, radius, intensity, falloff);

        public void SetChunkBounds(int chunkId, Vector3 min, Vector3 max)
            => _renderer?.SetChunkBounds(chunkId, min, max);

        public IAudioSystem Audio => _audio;

        public float GetTerrainHeight(float worldX, float worldZ)
        {
            if (_scene != null)
            {
                foreach (var sub in _scene.Subsystems)
                {
                    if (sub is RoomTerrainSubsystem terrainSub)
                    {
                        return terrainSub.SampleHeight(worldX, worldZ);
                    }
                }
            }
            return 0f;
        }
    }
}
