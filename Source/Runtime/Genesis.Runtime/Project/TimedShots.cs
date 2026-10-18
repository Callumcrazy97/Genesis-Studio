using System;
using System.Globalization;
using System.Linq;
using Genesis.Runtime.Core;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Project
{
    /// <summary>
    /// Screenshots at set seconds of play, the game left running: <c>GENESIS_SHOTS=2,5.5,9</c>. For
    /// an automation run (Studio's <c>game.run</c>), which plays scripted input and then reads the
    /// pictures; unlike GENESIS_AUTOSHOT it does not close the game. Play time is counted across
    /// room changes, from when the boot splash has gone, in the game's own time step, so a replayed
    /// run takes its pictures on the same frames every time. Each picture is logged as
    /// "screenshot saved: path". Off unless the variable is set.
    /// </summary>
    public sealed class TimedShots
    {
        public const string EnvironmentVariable = "GENESIS_SHOTS";
        private const int MaxAttempts = 3;

        private readonly float[] _seconds;
        private readonly string _projectPath;
        private readonly ProjectLogger _logger;
        private float _elapsed;
        private int _next;
        private bool _pending;
        private int _attempts;

        public TimedShots(float[] seconds, string projectPath, ProjectLogger logger)
        {
            _seconds = seconds;
            _projectPath = projectPath;
            _logger = logger;
        }

        /// <summary>The seconds in the variable, in order; empty when it is unset or holds none.</summary>
        public static float[] Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return [];
            return [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds) ? seconds : -1f)
                .Where(seconds => seconds >= 0f)
                .OrderBy(seconds => seconds)];
        }

        /// <summary>Counts play time in one room's scene; a new one is added for each room, sharing the count.</summary>
        public ISceneSubsystem Clock(Func<bool> countElapsed) => new ClockSubsystem(this, countElapsed);

        /// <summary>Hooked to the host's EndFrame: takes the picture that is due from the frame just drawn.</summary>
        public void OnEndFrame(IRenderController renderer)
        {
            if (!_pending) return;
            string label = $"shot-{_next + 1:00}-{_seconds[_next].ToString("0.##", CultureInfo.InvariantCulture)}s";
            string path = null;
            try { path = ProjectScreenshot.Capture(renderer, _projectPath, label, frameAlreadySubmitted: true); }
            catch (Exception error) { _logger?.Line("timed shot error: " + error.Message); }
            if (path == null && ++_attempts < MaxAttempts) return;
            _logger?.Line(path != null ? $"screenshot saved: {path}" : $"timed shot {label} could not be read from the renderer");
            _pending = false;
            _attempts = 0;
            _next++;
        }

        private void Advance(float delta)
        {
            if (_pending || _next >= _seconds.Length) return;
            _elapsed += delta;
            if (_elapsed >= _seconds[_next]) _pending = true;
        }

        private sealed class ClockSubsystem(TimedShots shots, Func<bool> countElapsed) : ISceneSubsystem
        {
            public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

            public void Update(RuntimeScene scene, GameTime time)
            {
                if (countElapsed is not null && !countElapsed()) return;
                shots.Advance(time.Delta);
            }

            public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer) { }

            public void Dispose() { }
        }
    }
}
