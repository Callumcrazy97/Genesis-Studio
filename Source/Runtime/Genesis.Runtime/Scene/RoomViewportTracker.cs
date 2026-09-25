using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.Runtime.Scene
{
    /// <summary>
    /// Live position of each room viewport, advanced once per frame so a viewport following a
    /// target scrolls at its configured speed rather than teleporting.
    /// </summary>
    /// <remarks>
    /// <para>Separate from <see cref="RoomViewport"/> because that is the authored document and this
    /// is per-run state. Keeping them apart is what stops a play session writing scroll positions
    /// back into the designer's room file.</para>
    ///
    /// <para>The follow rule is the standard margin one: the viewport does not move while the target
    /// is inside its margin box, and once the target crosses that edge the viewport moves only far
    /// enough to put it back on the edge — capped by the follow speed. That is what makes the
    /// margin a dead zone rather than an offset.</para>
    /// </remarks>
    public sealed class RoomViewportTracker
    {
        private readonly List<Vector3d> _positions = new();
        private readonly List<ShakeState> _shakes = new();

        private struct Vector3d
        {
            public float X, Y, Z;
        }

        private struct ShakeState
        {
            public float StartedAt, Duration, Magnitude;
        }

        /// <summary>Resets every viewport to its authored source position.</summary>
        public void Reset(RoomAsset room)
        {
            _positions.Clear();
            _shakes.Clear();
            if (room?.Viewports == null) return;
            foreach (RoomViewport viewport in room.Viewports)
            {
                _positions.Add(new Vector3d { X = viewport.SourceX, Y = viewport.SourceY, Z = viewport.SourceZ });
                _shakes.Add(default);
            }
        }

        /// <summary>Top-left of the viewport's source region right now, in room coordinates.</summary>
        public (float X, float Y, float Z) PositionOf(RoomAsset room, int index)
        {
            EnsureCapacity(room);
            if (index < 0 || index >= _positions.Count)
            {
                RoomViewport authored = Authored(room, index);
                return authored is null ? (0f, 0f, 0f) : (authored.SourceX, authored.SourceY, authored.SourceZ);
            }

            Vector3d position = _positions[index];
            return (position.X, position.Y, position.Z);
        }

        /// <summary>Immediately moves a live viewport without changing the room document.</summary>
        public void SetPosition(RoomAsset room, int index, float x, float y, float z)
        {
            EnsureCapacity(room);
            if (index < 0 || index >= _positions.Count) return;
            RoomViewport viewport = Authored(room, index);
            if (viewport == null) return;
            _positions[index] = new Vector3d
            {
                X = room.Dimension == RoomDimension.ThreeD ? x : ClampToRoom(x, viewport.SourceWidth, room.Settings.Width),
                Y = room.Dimension == RoomDimension.ThreeD ? y : ClampToRoom(y, viewport.SourceHeight, room.Settings.Height),
                Z = room.Dimension == RoomDimension.ThreeD ? z : ClampToRoom(z, viewport.SourceDepth, room.Settings.Depth),
            };
        }

        /// <summary>Starts a deterministic, decaying camera shake for one viewport.</summary>
        public void StartShake(RoomAsset room, int index, float magnitude, float duration, float totalTime)
        {
            EnsureCapacity(room);
            if (index < 0 || index >= _shakes.Count) return;
            _shakes[index] = new ShakeState
            {
                StartedAt = totalTime,
                Duration = Math.Max(0f, duration),
                Magnitude = Math.Max(0f, magnitude),
            };
        }

        /// <summary>Current screen-space shake offset; zero once the requested duration expires.</summary>
        public Vector2 ShakeOffset(RoomAsset room, int index, float totalTime)
        {
            EnsureCapacity(room);
            if (index < 0 || index >= _shakes.Count) return Vector2.Zero;
            ShakeState shake = _shakes[index];
            if (shake.Duration <= 0f || shake.Magnitude <= 0f) return Vector2.Zero;
            float elapsed = Math.Max(0f, totalTime - shake.StartedAt);
            if (elapsed >= shake.Duration)
            {
                _shakes[index] = default;
                return Vector2.Zero;
            }

            float envelope = 1f - (elapsed / shake.Duration);
            float phase = (elapsed * 47.3f) + (index * 3.17f) + 0.37f;
            return new Vector2(
                MathF.Sin(phase * 1.73f),
                MathF.Cos(phase * 2.11f)) * (shake.Magnitude * envelope);
        }

        /// <summary>
        /// Advances one viewport toward its target. <paramref name="hasTarget"/> false leaves the
        /// viewport where it is, which is what an unset or destroyed follow target should do.
        /// </summary>
        public void Follow(
            RoomAsset room,
            int index,
            bool hasTarget,
            float targetX,
            float targetY,
            float targetZ)
        {
            EnsureCapacity(room);
            if (!hasTarget || index < 0 || index >= _positions.Count) return;

            RoomViewport viewport = Authored(room, index);
            if (viewport is null) return;

            Vector3d position = _positions[index];
            position.X = Advance(position.X, targetX, viewport.SourceWidth, viewport.FollowMarginX, viewport.FollowSpeedX);
            position.Y = Advance(position.Y, targetY, viewport.SourceHeight, viewport.FollowMarginY, viewport.FollowSpeedY);
            position.Z = Advance(position.Z, targetZ, viewport.SourceDepth, viewport.FollowMarginZ, viewport.FollowSpeedZ);

            // Keep the viewport inside the room, but never so hard that a source region larger than
            // the room snaps to a corner — in that case the room is smaller than the view and the
            // authored position is the only sensible answer.
            if (room.Dimension == RoomDimension.TwoD)
            {
                position.X = ClampToRoom(position.X, viewport.SourceWidth, room.Settings.Width);
                position.Y = ClampToRoom(position.Y, viewport.SourceHeight, room.Settings.Height);
                position.Z = ClampToRoom(position.Z, viewport.SourceDepth, room.Settings.Depth);
            }

            _positions[index] = position;
        }

        /// <summary>
        /// One axis of the margin rule. Returns the viewport's new edge position. Public because it
        /// is the whole behaviour a designer tunes, and a rule that cannot be asserted directly
        /// gets asserted through a screenshot instead.
        /// </summary>
        public static float Advance(float position, float target, float size, float margin, float speed)
        {
            // A margin at or beyond half the view size means there is no dead zone left: the target
            // is pinned to the centre. Clamping here keeps the two edges from crossing over, which
            // would otherwise oscillate the viewport between them every frame.
            float limit = size * 0.5f;
            float clampedMargin = Math.Clamp(margin, 0f, limit);

            float minEdge = position + clampedMargin;
            float maxEdge = position + size - clampedMargin;

            float desired = position;
            if (target < minEdge) desired = target - clampedMargin;
            else if (target > maxEdge) desired = target - size + clampedMargin;
            else return position;   // inside the dead zone: do not move at all

            if (speed < 0f) return desired;         // instant

            float delta = desired - position;
            float step = Math.Min(Math.Abs(delta), speed);
            return position + (Math.Sign(delta) * step);
        }

        private static float ClampToRoom(float position, float size, float roomSize)
        {
            if (size >= roomSize) return position;
            return Math.Clamp(position, 0f, roomSize - size);
        }

        private static RoomViewport Authored(RoomAsset room, int index) =>
            room?.Viewports != null && index >= 0 && index < room.Viewports.Count
                ? room.Viewports[index]
                : null;

        private void EnsureCapacity(RoomAsset room)
        {
            if (room?.Viewports == null) return;
            while (_positions.Count < room.Viewports.Count)
            {
                RoomViewport viewport = room.Viewports[_positions.Count];
                _positions.Add(new Vector3d { X = viewport.SourceX, Y = viewport.SourceY, Z = viewport.SourceZ });
                _shakes.Add(default);
            }
        }
    }
}
