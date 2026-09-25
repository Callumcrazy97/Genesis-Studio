using System;
using System.Numerics;

namespace Genesis.Shared.Rendering
{
    /// <summary>
    /// Backend-neutral 3D camera using Genesis's canonical Shared rendering conventions.
    /// </summary>
    public class Camera
    {
        public const float MinPitch = -89f * (MathF.PI / 180f);
        public const float MaxPitch = 89f * (MathF.PI / 180f);

        private Vector3 _position = new(0f, 12f, 28f);
        private float _yaw;
        private float _pitch = -0.26f;
        private float _fieldOfView = MathF.PI / 3f;
        private float _aspectRatio = 16f / 9f;
        private float _nearPlane = 0.1f;
        private float _farPlane = 2000f;
        private bool _viewDirty = true;
        private bool _projectionDirty = true;
        private Matrix4x4 _view;
        private Matrix4x4 _projection;

        public Vector3 Position
        {
            get => _position;
            set { _position = value; _viewDirty = true; }
        }

        public float Yaw
        {
            get => _yaw;
            set { _yaw = value; _viewDirty = true; }
        }

        public float Pitch
        {
            get => _pitch;
            set { _pitch = Math.Clamp(value, MinPitch, MaxPitch); _viewDirty = true; }
        }

        public float FieldOfView
        {
            get => _fieldOfView;
            set { _fieldOfView = value; _projectionDirty = true; }
        }

        public float AspectRatio
        {
            get => _aspectRatio;
            set { _aspectRatio = value; _projectionDirty = true; }
        }

        public float NearPlane
        {
            get => _nearPlane;
            set { _nearPlane = value; _projectionDirty = true; }
        }

        public float FarPlane
        {
            get => _farPlane;
            set { _farPlane = value; _projectionDirty = true; }
        }

        public Matrix4x4 ViewMatrix
        {
            get
            {
                if (_viewDirty)
                {
                    _view = Conventions.CreateLookAt(_position, _position + Forward, Conventions.Up);
                    _viewDirty = false;
                }

                return _view;
            }
        }

        public Matrix4x4 ProjectionMatrix
        {
            get
            {
                if (_projectionDirty)
                {
                    _projection = Conventions.CreatePerspective(
                        _fieldOfView,
                        _aspectRatio,
                        _nearPlane,
                        _farPlane);
                    _projectionDirty = false;
                }

                return _projection;
            }
        }

        public Matrix4x4 ViewProjection => ViewMatrix * ProjectionMatrix;

        public Vector3 Forward => Conventions.DirectionFromYawPitch(_yaw, _pitch);

        /// <summary>
        /// Current Genesis camera-right basis. Kept identical to the existing runtime camera so
        /// Phase 1 does not change navigation behaviour while consolidating ownership.
        /// </summary>
        public Vector3 Right => Vector3.Normalize(Vector3.Cross(Conventions.Up, Forward));

        public void ApplyLook(Vector2 lookDelta, float sensitivity = 0.005f)
        {
            if (lookDelta == Vector2.Zero) return;
            Yaw -= lookDelta.X * sensitivity;
            Pitch -= lookDelta.Y * sensitivity;
        }

        public Vector4 WorldToClip(Vector3 world) =>
            Vector4.Transform(new Vector4(world, 1f), ViewProjection);

        public Vector3 WorldToPixel(Vector3 world, float width, float height) =>
            Conventions.ClipToPixel(WorldToClip(world), width, height);

        public Vector3 PixelToWorldRay(float pixelX, float pixelY, float width, float height)
        {
            RenderViewport viewport = Conventions.CreateViewport(width, height);
            float ndcX = (pixelX - viewport.X) * 2f / viewport.Width - 1f;
            float ndcY = 1f - (pixelY - viewport.Y) * 2f / viewport.Height;

            float yScale = 1f / MathF.Tan(_fieldOfView * 0.5f);
            float xScale = yScale / _aspectRatio;
            Vector3 up = Vector3.Cross(Forward, Right);

            return Vector3.Normalize(
                (ndcX / xScale) * Right +
                (ndcY / yScale) * up +
                Forward);
        }
    }
}
