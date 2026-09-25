using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Commands;

namespace Genesis.Runtime.Rendering;

public sealed partial class RoomRenderSubsystem
{
    private readonly Dictionary<int, (RenderTargetHandle Target, int Width, int Height)> _viewportTargets = new();
    public bool HasThreeDViewports => !IsTwoD && _room.UsesViewports;

    /// <summary>Matches the authored camera marker and inset, using live follow positions without editing the asset.</summary>
    public void ConfigureViewportCamera(RuntimeScene scene, int index)
    {
        RoomViewport viewport = _room.Viewports[index];
        Camera3D camera = scene.Camera3D;
        camera.Position = GetViewportPosition(index);
        camera.Yaw = 0; camera.Pitch = 0;
        camera.FieldOfView = Math.Clamp(viewport.FieldOfView, 1f, 175f) * MathF.PI / 180f;
        camera.NearPlane = Math.Max(.001f, viewport.FrustumNear);
        camera.FarPlane = Math.Max(camera.NearPlane + .01f, viewport.FrustumFar);
        camera.AspectRatio = viewport.PortWidth / (float)Math.Max(1, viewport.PortHeight);
        if (Engine.TryGetCamera3DPose(index, out Vector3 position, out float yaw, out float pitch,
                out float fov, out float aspect, out float near, out float far))
        {
            camera.Position = position; camera.Yaw = yaw * MathF.PI / 180f; camera.Pitch = pitch * MathF.PI / 180f;
            if (fov > 0) camera.FieldOfView = Math.Clamp(fov, 1f, 175f) * MathF.PI / 180f;
            if (aspect > .01f) camera.AspectRatio = aspect;
            if (near > 0) camera.NearPlane = near;
            if (far > camera.NearPlane) camera.FarPlane = far;
        }
        else if (ObjectDrawPass.TryFindInstancePosition(scene.World, viewport.FollowTarget, out float x, out float y, out float z))
        {
            Vector3 direction = new Vector3(x, y, z) - camera.Position;
            if (direction.LengthSquared() > 1e-8f)
            {
                direction = Vector3.Normalize(direction);
                camera.Yaw = MathF.Atan2(direction.X, -direction.Z);
                camera.Pitch = MathF.Asin(Math.Clamp(direction.Y, -1, 1));
            }
        }
        Vector2 shake = GetViewportShakeOffset(index);
        camera.Position += camera.Right * shake.X + Vector3.UnitY * shake.Y;
    }

    /// <summary>Each port receives a complete forward/post pass, then the finished views are composited once.</summary>
    public bool RenderViewports3D(RuntimeScene scene, IRenderController renderer, Action renderCamera)
    {
        if (!HasThreeDViewports) return false;
        _renderer = renderer;
        Camera3D camera = scene.Camera3D;
        (Vector3 Position, float Yaw, float Pitch, float Fov, float Near, float Far, float Aspect) previous =
            (camera.Position, camera.Yaw, camera.Pitch, camera.FieldOfView, camera.NearPlane, camera.FarPlane, camera.AspectRatio);
        var rendered = new List<(TextureHandle Texture, RoomViewport Port)>();
        try
        {
            for (int index = 0; index < _room.Viewports.Count; index++)
            {
                RoomViewport port = _room.Viewports[index];
                if (!port.Enabled || port.PortWidth <= 0 || port.PortHeight <= 0
                    || (long)port.PortX + port.PortWidth <= 0 || (long)port.PortY + port.PortHeight <= 0
                    || port.PortX >= renderer.PixelWidth || port.PortY >= renderer.PixelHeight)
                {
                    if (_viewportTargets.Remove(index, out var unused)) renderer.ReleaseRenderTarget(unused.Target);
                    continue;
                }
                // Never allocate an authored 100,000-pixel port at that size. Composition clips to the actual window.
                int width = Math.Clamp(port.PortWidth, 1, Math.Max(1, renderer.PixelWidth));
                int height = Math.Clamp(port.PortHeight, 1, Math.Max(1, renderer.PixelHeight));
                if (!_viewportTargets.TryGetValue(index, out var target) || target.Width != width || target.Height != height)
                {
                    if (target.Target.IsValid) renderer.ReleaseRenderTarget(target.Target);
                    target = (renderer.CreateRenderTarget(width, height), width, height);
                    _viewportTargets[index] = target;
                }
                if (!target.Target.IsValid) throw new InvalidOperationException("Could not allocate Room camera output.");
                ConfigureViewportCamera(scene, index);
                renderer.BeginCameraPass(postProcessing: true);
                renderer.SetRenderTarget(target.Target);
                Vector4 background = scene.Environment.BackgroundColor;
                renderer.Clear(background.X, background.Y, background.Z, background.W);
                renderer.SetViewport(0, 0, width, height);
                renderCamera();
                renderer.EndFrame();
                rendered.Add((renderer.GetRenderTargetTexture(target.Target), port));
            }
            renderer.SetRenderTargetDefault();
            renderer.BeginCameraPass(postProcessing: false);
            renderer.Set3DFrameActive(false);
            renderer.SetViewport(0, 0, renderer.PixelWidth, renderer.PixelHeight);
            renderer.SetCamera2D(renderer.PixelWidth * .5f, renderer.PixelHeight * .5f, 1f, 0f);
            foreach ((TextureHandle texture, RoomViewport port) in rendered)
                renderer.DrawSprite(new SpriteDrawCall
                {
                    Texture = texture, X = port.PortX, Y = port.PortY, Width = port.PortWidth, Height = port.PortHeight,
                    ScaleX = 1, ScaleY = 1, Alpha = 1, Tint = new RenderColor(1, 1, 1, 1),
                });
            return true;
        }
        finally
        {
            renderer.SetRenderTargetDefault();
            camera.Position = previous.Position; camera.Yaw = previous.Yaw; camera.Pitch = previous.Pitch;
            camera.FieldOfView = previous.Fov; camera.NearPlane = previous.Near; camera.FarPlane = previous.Far;
            camera.AspectRatio = previous.Aspect;
        }
    }

    private void ReleaseViewportTargets()
    {
        if (_renderer != null)
            foreach (var target in _viewportTargets.Values) _renderer.ReleaseRenderTarget(target.Target);
        _viewportTargets.Clear();
    }
}
