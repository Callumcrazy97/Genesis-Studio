using System;
using System.Diagnostics;

namespace Genesis.Runtime.Scene
{
    /// <summary>
    /// A room change that is being spread over several frames, as the scene and its host see it.
    /// While a scene holds one, nothing in the scene is updated: the change is advanced instead,
    /// and the host draws a cover and a loading screen in place of the half-made room.
    /// </summary>
    public sealed class RoomChangeProgress
    {
        private readonly long _started = Stopwatch.GetTimestamp();

        public RoomChangeProgress()
        {
        }

        /// <summary>A change at a given point, for drawing its loading screen outside a running game.</summary>
        public RoomChangeProgress(string roomName, float progress)
        {
            RoomName = roomName;
            Progress = Math.Clamp(progress, 0f, 1f);
        }

        /// <summary>The room being changed to.</summary>
        public string RoomName { get; internal set; }

        /// <summary>How far along the change is, from 0 to 1. It never goes backwards.</summary>
        public float Progress { get; internal set; }

        /// <summary>
        /// False while the room is being put together, when none of it may be drawn. True once it
        /// is whole and is being drawn behind the cover, so that what its first view needs is
        /// loaded before anyone sees it.
        /// </summary>
        public bool RoomBuilt { get; internal set; }

        /// <summary>Seconds since the change began.</summary>
        public double Seconds => Stopwatch.GetElapsedTime(_started).TotalSeconds;

        /// <summary>How long the cover takes to fade once the room is ready, in seconds.</summary>
        public float RevealSeconds { get; internal set; }

        /// <summary>What is left of <see cref="RevealSeconds"/>; the host counts it down in real time.</summary>
        public float RevealRemaining { get; set; }

        /// <summary>Does one frame's worth of the change. Set by whoever is changing the room.</summary>
        internal Action<RuntimeScene> Advance { get; set; }
    }
}
