using System;
using Genesis.Runtime;
using Genesis.Runtime.Core;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Project
{
    /// <summary>Closes the player after a fixed play duration and triggers a screenshot.</summary>
    public sealed class AutoshotSubsystem : ISceneSubsystem
    {
        private readonly float _durationSeconds;
        private readonly Action _captureAndExit;
        private readonly Func<bool> _countElapsed;
        private float _elapsed;
        private bool _done;

        public AutoshotSubsystem(float durationSeconds, Action captureAndExit, Func<bool> countElapsed = null)
        {
            _durationSeconds = Math.Max(0.1f, durationSeconds);
            _captureAndExit = captureAndExit;
            _countElapsed = countElapsed;
        }

        public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

        public void Update(RuntimeScene scene, GameTime time)
        {
            if (_done) return;
            if (_countElapsed is not null && !_countElapsed()) return;
            _elapsed += time.Delta;
            if (_elapsed < _durationSeconds) return;
            _done = true;
            Console.WriteLine($"[AutoshotSubsystem] Firing capture after {_elapsed:F2}s");
            _captureAndExit?.Invoke();
        }

        public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer) { }

        public void Dispose() { }
    }
}
