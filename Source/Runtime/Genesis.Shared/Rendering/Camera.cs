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

        /// <summary>
        /// The near plane the projection uses. A view of four kilometres or more cannot keep a
        /// near plane of a few centimetres: the depth buffer runs out of precision and distant
        /// ground, water and shore flicker through each other. Such a view moves the near plane out
        /// to a thirty-thousandth of the far plane; shorter views use the authored value unchanged.
        /// </summary>
        public float EffectiveNearPlane => _farPlane >= 4000f
            ? MathF.Max(_nearPlane, _farPlane / 30000f)
            : _nearPlane;

        public Matrix4x4 ViewMatrix
        {
            get
            {
                if (_viewDirty)
                {
                    Vector3 eye = _position + _viewOffset;
                    _view = Conventions.CreateLookAt(eye, eye + Forward, Conventions.Up);
                    if (_viewRoll != 0f) _view *= Matrix4x4.CreateRotationZ(_viewRoll);
                    _viewDirty = false;
                }

                return _view;
            }
        }

        private Vector3 _viewOffset;
        private float _viewRoll;
        private float _shakeAmplitude, _shakeRoll, _shakeSeconds, _shakeRemaining, _shakeClock;

        /// <summary>
        /// Moves what the camera shows without moving the camera: <see cref="Position"/> stays
        /// where the game put it, so a script that follows the player is not fighting the shake.
        /// </summary>
        public Vector3 ViewOffset
        {
            get => _viewOffset;
            set { if (value != _viewOffset) { _viewOffset = value; _viewDirty = true; } }
        }

        /// <summary>Tilt of the picture about the direction of view, in radians.</summary>
        public float ViewRoll
        {
            get => _viewRoll;
            set { if (value != _viewRoll) { _viewRoll = value; _viewDirty = true; } }
        }

        /// <summary>True while a shake started with <see cref="Shake"/> is still dying away.</summary>
        public bool IsShaking => _shakeRemaining > 0f;

        /// <summary>
        /// Shakes the view: up to <paramref name="amplitude"/> metres sideways and up and down, and
        /// up to <paramref name="rollDegrees"/> of tilt, dying away over <paramref name="seconds"/>.
        /// A shake asked for during a stronger one does not weaken it.
        /// </summary>
        public void Shake(float amplitude, float seconds, float rollDegrees = 0f)
        {
            if (!(seconds > 0f) || !float.IsFinite(amplitude) || !float.IsFinite(rollDegrees)) return;
            float left = _shakeSeconds > 0f ? Math.Clamp(_shakeRemaining / _shakeSeconds, 0f, 1f) : 0f;
            float strength = left * left;
            _shakeAmplitude = MathF.Max(MathF.Abs(amplitude), _shakeAmplitude * strength);
            _shakeRoll = MathF.Max(MathF.Abs(rollDegrees) * (MathF.PI / 180f), _shakeRoll * strength);
            _shakeSeconds = MathF.Min(seconds, 30f);
            _shakeRemaining = _shakeSeconds;
        }

        /// <summary>Advances a shake by real elapsed time. The host calls this once a frame.</summary>
        public void AdvanceShake(float deltaSeconds)
        {
            if (_shakeRemaining <= 0f) return;
            _shakeRemaining -= MathF.Max(0f, deltaSeconds);
            if (_shakeRemaining <= 0f)
            {
                _shakeRemaining = 0f;
                ViewOffset = Vector3.Zero;
                ViewRoll = 0f;
                return;
            }

            _shakeClock += MathF.Max(0f, deltaSeconds);
            float left = _shakeRemaining / _shakeSeconds, strength = left * left;
            // Two sines at unrelated rates on each axis: irregular enough to read as a jolt.
            float t = _shakeClock;
            float sideways = (MathF.Sin(t * 61.3f) + MathF.Sin(t * 23.7f + 1.3f)) * 0.5f;
            float upward = (MathF.Sin(t * 53.9f + 2.1f) + MathF.Sin(t * 29.1f + 4.2f)) * 0.5f;
            float tilt = (MathF.Sin(t * 47.3f + 0.7f) + MathF.Sin(t * 19.9f + 3.3f)) * 0.5f;
            Vector3 right = Right;
            // Each axis reaches 1, so together they reach the square root of 2: scale to keep the
            // displacement within the amplitude asked for.
            ViewOffset = (right * sideways + Vector3.Cross(Forward, right) * upward) * (_shakeAmplitude * strength * 0.70710678f);
            ViewRoll = tilt * _shakeRoll * strength;
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
                        EffectiveNearPlane,
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
