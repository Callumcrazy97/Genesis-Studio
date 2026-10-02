using System;
using System.Diagnostics;
using Genesis.Shared.Assets;

namespace Genesis.Runtime.Project
{
    /// <summary>
    /// Writes a line to the game's log for each frame that takes far longer than it should, saying
    /// what the frame was spent on: the update, gathering and drawing, presenting, what was loaded
    /// and how long the garbage collector held it. A hitch can then be attributed from the log of
    /// an ordinary run, on the machine it happened on.
    /// </summary>
    public sealed class SlowFrameLog
    {
        /// <summary>Overrides how long a frame must take, in milliseconds, to be written down.</summary>
        public const string ThresholdEnvironmentVariable = "GENESIS_SLOW_FRAME_MS";

        /// <summary>At most this many lines for each room, so a game that is slow throughout does not fill the log.</summary>
        public const int MaximumPerRoom = 24;

        private readonly Action<string> _write;
        private readonly double _thresholdMilliseconds;
        private long _lastFrameEnded;
        private long _roomStarted;
        private LoadClockSnapshot _loads;
        private TimeSpan _collectorPaused;
        private string _room;
        private int _frameInRoom;
        private int _written;

        public SlowFrameLog(Action<string> write, double thresholdMilliseconds = 100)
        {
            _write = write ?? throw new ArgumentNullException(nameof(write));
            _thresholdMilliseconds = thresholdMilliseconds;
            if (double.TryParse(Environment.GetEnvironmentVariable(ThresholdEnvironmentVariable),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double asked)
                && double.IsFinite(asked) && asked > 0)
                _thresholdMilliseconds = asked;
        }

        /// <summary>How many lines have been written for the current room.</summary>
        public int Written => _written;

        /// <summary>
        /// Called when a frame has been presented. <paramref name="counted"/> is false for frames
        /// that are expected to be long or are not the game's (the loading screen at start-up).
        /// </summary>
        /// <param name="longestParts">The parts of the scene's frame that took longest, in words, or empty.</param>
        /// <param name="overlayMilliseconds">Time spent drawing the HUD and in whatever is hooked to the end of a frame.</param>
        public void FrameEnded(string room, bool counted, double updateMilliseconds, double collectMilliseconds,
            double drawMilliseconds, double presentMilliseconds, string longestParts = "", double overlayMilliseconds = 0)
        {
            long now = Stopwatch.GetTimestamp();
            LoadClockSnapshot loads = LoadClock.Capture();
            TimeSpan paused = GC.GetTotalPauseDuration();
            if (!string.Equals(room, _room, StringComparison.Ordinal))
            {
                _room = room;
                _roomStarted = _lastFrameEnded != 0 ? _lastFrameEnded : now;
                _frameInRoom = 0;
                _written = 0;
            }

            _frameInRoom++;
            if (counted && _lastFrameEnded != 0 && _written < MaximumPerRoom)
            {
                double frame = Stopwatch.GetElapsedTime(_lastFrameEnded, now).TotalMilliseconds;
                if (frame >= _thresholdMilliseconds)
                {
                    _written++;
                    string loaded = loads.Describe(_loads);
                    // What the frame's own work does not account for: the window's messages, a wait
                    // for the frame rate, or another program holding the processor or graphics card.
                    double outside = frame - (updateMilliseconds + collectMilliseconds + drawMilliseconds
                        + overlayMilliseconds + presentMilliseconds);
                    _write($"Slow frame: {frame:F0} ms in {room} (frame {_frameInRoom}, "
                        + $"{Stopwatch.GetElapsedTime(_roomStarted, now).TotalSeconds:F1} s after the room began): "
                        + $"update {updateMilliseconds:F0} ms, gathering what to draw {collectMilliseconds:F0} ms, "
                        + $"drawing {drawMilliseconds:F0} ms, HUD and hooks {overlayMilliseconds:F0} ms, presenting {presentMilliseconds:F0} ms; "
                        + (outside >= Math.Max(20.0, frame * 0.15) ? $"{outside:F0} ms was outside the frame's own work; " : string.Empty)
                        + (string.IsNullOrEmpty(longestParts) ? string.Empty : $"longest parts: {longestParts}; ")
                        + $"loading in that frame: {(loaded.Length == 0 ? "none" : loaded)}; "
                        + $"garbage collector paused {(paused - _collectorPaused).TotalMilliseconds:F0} ms"
                        + (_written == MaximumPerRoom ? "; no more are written for this room" : string.Empty));
                }
            }

            _lastFrameEnded = now;
            _loads = loads;
            _collectorPaused = paused;
        }
    }
}
